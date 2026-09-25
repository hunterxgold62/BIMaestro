import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { gunzip } from 'fflate';
import type { ModelTile } from './mep-contract';
import { resolveTiles } from './mep-api';
import { digest } from './mep-package';

export function tileBounds(tile: ModelTile) {
  return new THREE.Box3(new THREE.Vector3(...tile.bounds.slice(0, 3)), new THREE.Vector3(...tile.bounds.slice(3, 6)));
}

export function disposeModel(model: THREE.Object3D) {
  const materials = new Set<THREE.Material>();
  model.traverse(object => {
    const mesh = object as THREE.Mesh;
    if (mesh.geometry) { mesh.geometry.disposeBoundsTree?.(); mesh.geometry.dispose(); }
    if (mesh.material) (Array.isArray(mesh.material) ? mesh.material : [mesh.material]).forEach(material => materials.add(material));
  });
  materials.forEach(material => material.dispose());
}

export class ModelStream {
  private loaded = new Map<string, THREE.Group>();
  private pending = new Map<string, AbortController>();
  private failures = new Map<string, number>();
  private wanted = new Set<string>();
  private stopped = false;
  private lastUpdate = -Infinity;
  private loader = new GLTFLoader();
  private signed = new Map<string, { url: string; bytes: number; sha256: string; expires: number }>();
  private signing = false;
  private cache = new Map<string, Uint8Array>();
  private cacheBytes = 0;
  private decodeTail: Promise<void> = Promise.resolve();
  private boxes = new Map<string, THREE.Box3>();
  private elementTiles = new Map<number, string[]>();
  constructor(readonly tiles: ModelTile[], private token: string, private revision: number,
    private add: (model: THREE.Group) => void | Promise<void>, private remove: (model: THREE.Group) => void,
    private changed: () => void, private error: (message: string) => void) {
    // Every detailed tile belongs to this session, independently of the camera.
    this.wanted = new Set(tiles.map(tile => tile.name));
    for (const tile of tiles) {
      this.boxes.set(tile.name, tileBounds(tile));
      for (const id of tile.elements) {
        const members = this.elementTiles.get(id) || []; members.push(tile.name); this.elementTiles.set(id, members);
      }
    }
  }
  complete(element: number) {
    const members = this.elementTiles.get(element);
    return !!members?.length && members.every(name => this.loaded.has(name));
  }
  readyAt(point: THREE.Vector3) {
    return this.tiles.every(tile => this.boxes.get(tile.name)!.distanceToPoint(point) > 8 || this.loaded.has(tile.name));
  }
  get loading() { return this.loaded.size !== this.tiles.length || this.pending.size > 0 || this.signing; }
  get count() { return this.loaded.size; }
  update(now: number) {
    if (this.stopped || now - this.lastUpdate < 500) return;
    this.lastUpdate = now;
    this.pump(now);
  }
  private pump(now: number) {
    if (this.stopped) return;
    if (this.loaded.size === this.tiles.length) { this.cache.clear(); this.cacheBytes = 0; this.signed.clear(); return; }
    const candidates = this.tiles.filter(tile => this.wanted.has(tile.name) && !this.loaded.has(tile.name) && !this.pending.has(tile.name) && now >= (this.failures.get(tile.name) || 0));
    const order = new Map([...this.wanted].map((name, index) => [name, index]));
    candidates.sort((a, b) => order.get(a.name)! - order.get(b.name)!);
    const unsigned = candidates.filter(tile => !this.cache.has(tile.sha256) && (this.signed.get(tile.name)?.expires || 0) < now).slice(0, 24);
    if (unsigned.length && !this.signing) {
      this.signing = true;
      void resolveTiles(this.token, this.revision, unsigned.map(tile => tile.name)).then(result => {
        if (!this.stopped) for (const tile of result.tiles) this.signed.set(tile.name, { ...tile, expires: performance.now() + 240000 });
      }).catch(caught => {
        for (const tile of unsigned) this.failures.set(tile.name, performance.now() + 10000);
        if (!this.stopped) this.error(caught instanceof Error ? caught.message : 'Chargement indisponible');
      }).finally(() => { this.signing = false; this.pump(performance.now()); });
    }
    for (const tile of candidates) {
      if (this.pending.size >= 6) break;
      if (!this.cache.has(tile.sha256) && (this.signed.get(tile.name)?.expires || 0) < now) continue;
      const controller = new AbortController(); this.pending.set(tile.name, controller);
      void this.load(tile, controller);
    }
  }
  private remember(key: string, data: Uint8Array) {
    if (data.byteLength > 48 * 1024 * 1024) return;
    const existing = this.cache.get(key); if (existing) this.cacheBytes -= existing.byteLength;
    this.cache.delete(key); this.cache.set(key, data); this.cacheBytes += data.byteLength;
    while (this.cacheBytes > 48 * 1024 * 1024) {
      const first = this.cache.keys().next().value!;
      this.cacheBytes -= this.cache.get(first)!.byteLength; this.cache.delete(first);
    }
  }
  private async load(tile: ModelTile, controller: AbortController) {
    let model: THREE.Group | undefined;
    try {
      let data = this.cache.get(tile.sha256);
      if (!data) {
        const signed = this.signed.get(tile.name)!;
        if (signed.bytes !== tile.size || signed.sha256 !== tile.sha256) throw new Error('La zone ne correspond pas à cette version de la maquette.');
        const response = await fetch(signed.url, { signal: controller.signal, referrerPolicy: 'no-referrer' });
        if (!response.ok) { this.signed.delete(tile.name); throw new Error('Une zone de la maquette est indisponible.'); }
        data = new Uint8Array(await response.arrayBuffer());
        if (data.length !== tile.size || await digest(data) !== tile.sha256) throw new Error('Le contrôle d’intégrité de la zone a échoué.');
      }
      this.remember(tile.sha256, data);
      // Downloads overlap, but decode/geometry preparation is bounded to one
      // tile at a time so six large GLBs cannot exhaust a small PC's memory.
      const previous = this.decodeTail;
      let release!: () => void;
      this.decodeTail = new Promise<void>(resolve => { release = resolve; });
      await previous;
      try {
      if (this.stopped || controller.signal.aborted) return;
      const decoded = await new Promise<Uint8Array>((resolve, reject) => gunzip(data!, (error, result) => error ? reject(error) : resolve(result)));
      if (decoded.length !== tile.decodedBytes) throw new Error('La taille de la zone est incorrecte.');
      if (this.stopped || controller.signal.aborted) return;
      const gltf = await this.loader.parseAsync(Uint8Array.from(decoded).buffer, ''); model = gltf.scene;
      if (this.stopped || controller.signal.aborted || !this.wanted.has(tile.name)) { disposeModel(model); return; }
      model.userData.selectionElements = new Set(tile.elements);
      await this.add(model);
      if (this.stopped || controller.signal.aborted || !this.wanted.has(tile.name)) { this.remove(model); disposeModel(model); return; }
      this.loaded.set(tile.name, model); this.failures.delete(tile.name);
      } finally { release(); }
    } catch (caught) {
      if (model && !this.loaded.has(tile.name)) { this.remove(model); disposeModel(model); }
      if (!this.stopped && !controller.signal.aborted) {
        this.failures.set(tile.name, performance.now() + 30000);
        this.error(caught instanceof Error ? caught.message : 'Chargement de la zone impossible.');
      }
    } finally { this.pending.delete(tile.name); this.pump(performance.now()); if (!this.stopped) this.changed(); }
  }
  dispose() {
    this.stopped = true; this.pending.forEach(controller => controller.abort()); this.pending.clear();
    // Scene teardown owns disposal of the remaining models.
    this.loaded.clear(); this.cache.clear(); this.cacheBytes = 0; this.signed.clear();
  }
}

import * as THREE from 'three';
import type { WebProperty } from './mep-contract';
import type { Markup } from './mep-markup';
import { packGeometry, unpackGeometry, geometryTransfers, type GeometryWire } from './mep-geometry-wire';

type Result = { index: number; geometry: GeometryWire | null; edges: GeometryWire; proxy: GeometryWire };
export class AsyncWallCutouts {
  private worker: Worker;
  private originals: THREE.BufferGeometry[];
  private stopped = false;
  private version = 0;
  private signature = '';
  private initialized: Promise<void> | null = null;
  private originalEdges = new Map<THREE.LineSegments, THREE.BufferGeometry>();
  constructor(private meshes: THREE.Mesh[], private properties: Map<number, WebProperty>,
    private busy: (value: boolean) => void, private error: (message: string) => void,
    private replaceProxy: (mesh: THREE.Mesh, geometry: THREE.BufferGeometry) => void, private completeIds?: number[]) {
    this.originals = meshes.map(mesh => mesh.geometry);
    for (const mesh of meshes) for (const child of mesh.children) if (child instanceof THREE.LineSegments) this.originalEdges.set(child, child.geometry);
    this.worker = new Worker(new URL('./mep-cutouts.worker.ts', import.meta.url), { type: 'module' });
    this.worker.onerror = event => {
      event.preventDefault();
      console.error('Worker de découpe :', event.message);
      this.fail('Le calcul de découpe a été interrompu. La réservation est conservée.');
    };
    this.worker.onmessageerror = () => this.fail('Le résultat de la découpe n’a pas pu être reçu. La réservation est conservée.');
    this.worker.onmessage = ({ data }: MessageEvent<{ version: number; results: Result[]; error?: string }>) => {
      if (this.stopped) return;
      // Apply every ordered worker result: later results are deltas from this state.
      // All meshes and their collision/occlusion geometry change in the same frame.
      for (const item of data.results) {
        const mesh = this.meshes[item.index], original = this.originals[item.index];
        if (mesh.geometry !== original) { mesh.geometry.disposeBoundsTree(); mesh.geometry.dispose(); }
        mesh.geometry = item.geometry ? unpackGeometry(item.geometry) : original;
        for (const child of mesh.children) if (child instanceof THREE.LineSegments) {
          if (child.geometry !== this.originalEdges.get(child)) child.geometry.dispose();
          child.geometry = unpackGeometry(item.edges);
        }
        this.replaceProxy(mesh, unpackGeometry(item.proxy));
      }
      if (data.version === this.version) { this.busy(false); if (data.error) this.error(data.error); }
    };
  }
  private fail(message: string) {
    if (this.stopped) return;
    // Stop queued mesh messages too: one startup error must not repeat once
    // for every mesh or leave the progress indicator running indefinitely.
    this.stopped = true; this.worker.terminate(); this.busy(false); this.error(message);
  }
  originalGeometry(mesh: THREE.Mesh) {
    const index = this.meshes.indexOf(mesh);
    return index < 0 ? mesh.geometry : this.originals[index];
  }
  private async initialize() {
    for (let index = 0; index < this.meshes.length; index++) {
      if (this.stopped) return;
      const mesh = this.meshes[index]; mesh.updateWorldMatrix(true, false);
      // Transfer one mesh at a time, giving navigation a frame between copies.
      const geometry = packGeometry(this.originals[index], false);
      this.worker.postMessage({ type: 'mesh', geometry, matrix: mesh.matrixWorld.toArray() }, geometryTransfers(geometry));
      await new Promise<void>(resolve => setTimeout(resolve, 0));
    }
  }
  update(marks: Markup[]) {
    if (this.stopped) return;
    const reservations = marks.filter(mark => mark.kind === 'reservation');
    const signature = JSON.stringify(reservations.map(({ id, position, normal, widthCm, heightCm, depthCm, shape, diameterCm }) => ({ id, position, normal, widthCm, heightCm, depthCm, shape, diameterCm })));
    if (signature === this.signature) return;
    this.signature = signature; const version = ++this.version; this.busy(true);
    this.initialized ||= this.initialize();
    void this.initialized.then(() => {
      if (!this.stopped && version === this.version) this.worker.postMessage({ type: 'update', version, marks: reservations, properties: [...this.properties], completeIds: this.completeIds });
    }).catch(caught => this.fail(String(caught)));
  }
  dispose() {
    this.stopped = true; this.worker.terminate(); this.busy(false);
    this.meshes.forEach((mesh, index) => {
      if (mesh.geometry !== this.originals[index]) { mesh.geometry.disposeBoundsTree(); mesh.geometry.dispose(); mesh.geometry = this.originals[index]; }
    });
    for (const [child, original] of this.originalEdges) if (child.geometry !== original) { child.geometry.dispose(); child.geometry = original; }
    this.originalEdges.clear();
  }
}

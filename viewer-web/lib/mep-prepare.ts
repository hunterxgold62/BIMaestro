import * as THREE from 'three';
import { packGeometry, unpackGeometry, geometryTransfers, type GeometryWire } from './mep-geometry-wire';
type Prepared = { geometry: THREE.BufferGeometry; proxy: THREE.BufferGeometry | null; edges: THREE.BufferGeometry | null };
export class ModelPreparation {
  private worker: Worker;
  private next = 0;
  private stopped = false;
  private pending = new Map<number, { resolve: (result: Prepared) => void; reject: (error: Error) => void }>();
  constructor(blockers: number[]) {
    this.worker = new Worker(new URL('./mep-prepare.worker.ts', import.meta.url), { type: 'module' });
    this.worker.postMessage({ blockers });
    this.worker.onmessage = ({ data }: MessageEvent<{ id: number; error?: string; geometry: GeometryWire; proxy: GeometryWire | null; edges: GeometryWire | null }>) => {
      const task = this.pending.get(data.id); if (!task) return;
      this.pending.delete(data.id);
      if (data.error) task.reject(new Error(data.error));
      else task.resolve({ geometry: unpackGeometry(data.geometry), proxy: data.proxy ? unpackGeometry(data.proxy) : null, edges: data.edges ? unpackGeometry(data.edges) : null });
    };
    this.worker.onerror = event => { event.preventDefault(); this.dispose(); };
    this.worker.onmessageerror = () => this.dispose();
  }
  prepare(source: THREE.BufferGeometry, angle: number | null): Promise<Prepared> {
    if (this.stopped) return Promise.reject(new Error('Préparation de la maquette interrompue. Rechargez la page.'));
    const id = ++this.next, geometry = packGeometry(source, false);
    return new Promise((resolve, reject) => { this.pending.set(id, { resolve, reject }); this.worker.postMessage({ id, geometry, angle }, geometryTransfers(geometry)); });
  }
  dispose() {
    this.stopped = true; this.worker.terminate();
    for (const task of this.pending.values()) task.reject(new Error('Préparation interrompue'));
    this.pending.clear();
  }
}

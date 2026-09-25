import * as THREE from 'three';
import { computeBoundsTree, disposeBoundsTree } from 'three-mesh-bvh';
import { triangleSubset } from './mep-cutouts';
import { packGeometry, unpackGeometry, geometryTransfers, type GeometryWire } from './mep-geometry-wire';
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;
let blockers = new Set<number>();
self.onmessage = ({ data }: MessageEvent<{ blockers?: number[]; id: number; geometry: GeometryWire; angle: number | null }>) => {
  if (data.blockers) { blockers = new Set(data.blockers); return; }
  const source = unpackGeometry(data.geometry);
  let proxy: THREE.BufferGeometry | undefined, edges: THREE.BufferGeometry | undefined;
  try {
    if (source.getAttribute('position').count) source.computeBoundsTree({ targetLeafSize: 24, indirect: true });
    const hasIds = source.hasAttribute('_element') || source.hasAttribute('_ELEMENT');
    if (hasIds) {
      proxy = triangleSubset(source, id => blockers.has(id));
      if (proxy.getAttribute('position').count) proxy.computeBoundsTree({ targetLeafSize: 24, indirect: true });
    }
    if (data.angle !== null) edges = new THREE.EdgesGeometry(source, data.angle);
    const result = { id: data.id, geometry: packGeometry(source), proxy: proxy ? packGeometry(proxy) : null, edges: edges ? packGeometry(edges) : null };
    self.postMessage(result, { transfer: [result.geometry, result.proxy, result.edges].flatMap(item => item ? geometryTransfers(item) : []) });
  } catch (error) { self.postMessage({ id: data.id, error: String(error) }); }
  finally { for (const geometry of [source, proxy, edges]) { geometry?.disposeBoundsTree(); geometry?.dispose(); } }
};

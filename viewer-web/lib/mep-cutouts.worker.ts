import * as THREE from 'three';
import { computeBoundsTree, disposeBoundsTree } from 'three-mesh-bvh';
import { WallCutouts, triangleSubset } from './mep-cutouts';
import { blocksView } from './mep-visibility';
import { packGeometry, unpackGeometry, geometryTransfers, type GeometryWire } from './mep-geometry-wire';
import type { Markup } from './mep-markup';
import type { WebProperty } from './mep-contract';

THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;
const meshes: THREE.Mesh[] = [];
const originals: THREE.BufferGeometry[] = [];
let controller: WallCutouts;
let blockers = new Set<number>();
type Request = { type: 'mesh'; geometry: GeometryWire; matrix: number[] } |
  { type: 'update'; version: number; marks: Markup[]; properties: [number, WebProperty][]; completeIds?: number[] };
self.onmessage = ({ data }: MessageEvent<Request>) => {
  if (data.type === 'mesh') {
    const mesh = new THREE.Mesh(unpackGeometry(data.geometry));
    mesh.matrixAutoUpdate = false; mesh.matrix.fromArray(data.matrix); mesh.updateMatrixWorld(true);
    meshes.push(mesh); originals.push(mesh.geometry); return;
  }
  let error: string | undefined;
  const before = meshes.map(mesh => mesh.geometry);
  try {
    if (!controller) {
      const complete = data.completeIds ? new Set(data.completeIds) : null;
      const properties = new Map(data.properties.filter(([id]) => !complete || complete.has(id)));
      blockers = new Set(data.properties.filter(([, prop]) => blocksView(prop)).map(([id]) => id));
      controller = new WallCutouts(meshes, properties);
    }
    controller.update(data.marks);
  } catch (caught) { error = caught instanceof Error ? caught.message : 'Découpe impossible.'; }
  try {
    const results = meshes.flatMap((mesh, index) => {
      if (mesh.geometry === before[index]) return [];
      const edges = new THREE.EdgesGeometry(mesh.geometry, 22);
      const proxy = triangleSubset(mesh.geometry, id => blockers.has(id));
      if (proxy.getAttribute('position').count) proxy.computeBoundsTree({ targetLeafSize: 24, indirect: true });
      const result = { index, geometry: mesh.geometry === originals[index] ? null : packGeometry(mesh.geometry), edges: packGeometry(edges), proxy: packGeometry(proxy) };
      edges.dispose(); proxy.disposeBoundsTree(); proxy.dispose(); return [result];
    });
    const transfers = results.flatMap(item => [...(item.geometry ? geometryTransfers(item.geometry) : []), ...geometryTransfers(item.edges), ...geometryTransfers(item.proxy)]);
    self.postMessage({ version: data.version, results, error }, { transfer: transfers });
  } catch (caught) { self.postMessage({ version: data.version, results: [], error: String(caught) }); }
};

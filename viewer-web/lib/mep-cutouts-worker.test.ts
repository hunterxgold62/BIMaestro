import { it, expect, vi } from 'vitest';
import * as THREE from 'three';
import { packGeometry, unpackGeometry, type GeometryWire } from './mep-geometry-wire';
import type { Markup } from './mep-markup';

it('returns transferable geometry, collision BVH and occlusion before restoring a deleted cut', async () => {
  type Reply = { error?: string; results: { geometry: GeometryWire; proxy: GeometryWire }[] };
  const messages: Reply[] = [];
  const scope = { onmessage: (_event: { data: unknown }) => {}, postMessage: (data: Reply, options?: StructuredSerializeOptions) => messages.push(structuredClone(data, options)) };
  vi.stubGlobal('self', scope);
  try {
    await import('./mep-cutouts.worker');
    const geometry = new THREE.BoxGeometry(10, 10, 1).toNonIndexed();
    geometry.setAttribute('_element', new THREE.Float32BufferAttribute(new Float32Array(geometry.getAttribute('position').count).fill(1), 1));
    scope.onmessage({ data: { type: 'mesh', geometry: packGeometry(geometry), matrix: new THREE.Matrix4().toArray() } });
    const mark: Markup = { id: 'cut', kind: 'reservation', elementKey: 'wall', elementName: '', position: [0, 0, .5], normal: [0, 0, 1], widthCm: 60.96, heightCm: 60.96, depthCm: 30.48, text: '', modelRevision: 1 };
    const properties = [[1, { key: 'wall', category: 'Murs' }]];
    scope.onmessage({ data: { type: 'update', version: 1, marks: [mark], properties } });
    expect(messages[0].error).toBeUndefined();
    const result = messages[0].results[0];
    const cut = unpackGeometry(result.geometry), proxy = unpackGeometry(result.proxy);
    expect(cut.boundsTree).toBeDefined(); expect(proxy.boundsTree).toBeDefined();
    const mesh = new THREE.Mesh(cut, new THREE.MeshBasicMaterial({ side: THREE.DoubleSide })); mesh.updateMatrixWorld();
    expect(new THREE.Raycaster(new THREE.Vector3(0, 0, 2), new THREE.Vector3(0, 0, -1)).intersectObject(mesh)).toHaveLength(0);
    scope.onmessage({ data: { type: 'update', version: 2, marks: [], properties } });
    expect(messages[1].results[0].geometry).toBeNull();
    scope.onmessage({ data: { type: 'update', version: 3, marks: [], properties } });
    expect(messages[2].results).toHaveLength(0);
  } finally { vi.unstubAllGlobals(); }
});

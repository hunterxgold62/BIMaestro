import * as THREE from 'three';
import { MeshBVH, type SerializedBVH } from 'three-mesh-bvh';

export type GeometryWire = {
  attributes: Record<string, { array: THREE.TypedArray; itemSize: number; normalized: boolean }>;
  index: Uint32Array | Uint16Array | null;
  bvh?: SerializedBVH;
};
export function packGeometry(geometry: THREE.BufferGeometry, includeBvh = true): GeometryWire {
  const attributes: GeometryWire['attributes'] = {};
  for (const [name, attribute] of Object.entries(geometry.attributes)) {
    const attr = attribute as THREE.BufferAttribute;
    attributes[name] = { array: attr.array.slice(), itemSize: attr.itemSize, normalized: attr.normalized };
  }
  return { attributes, index: geometry.index ? new Uint32Array(geometry.index.array) : null,
    bvh: includeBvh && geometry.boundsTree ? MeshBVH.serialize(geometry.boundsTree as MeshBVH) : undefined };
}
export function unpackGeometry(data: GeometryWire) {
  const geometry = new THREE.BufferGeometry();
  for (const [name, attr] of Object.entries(data.attributes)) geometry.setAttribute(name, new THREE.BufferAttribute(attr.array, attr.itemSize, attr.normalized));
  if (data.index) geometry.setIndex(new THREE.BufferAttribute(data.index, 1));
  if (data.bvh) geometry.boundsTree = MeshBVH.deserialize(data.bvh, geometry, { setIndex: false });
  return geometry;
}
export function geometryTransfers(data: GeometryWire): ArrayBuffer[] {
  return [...new Set([
    ...Object.values(data.attributes).map(attr => attr.array.buffer as ArrayBuffer),
    ...(data.index ? [data.index.buffer as ArrayBuffer] : []),
    ...(data.bvh?.roots || []),
    ...(data.bvh?.index ? [data.bvh.index.buffer as ArrayBuffer] : []),
    ...(data.bvh?.indirectBuffer ? [data.bvh.indirectBuffer.buffer as ArrayBuffer] : []),
  ])];
}

import * as THREE from 'three';
import type { WebProperty } from './mep-contract';

export const isReservationHost = (property?: WebProperty) => !!property && /^(murs?|walls?|ost_walls|sols?|floors?|ost_floors|dalles?|slabs?)$/i.test(property.category.trim());

// Measure the actual exit face of this element, including rotated and split meshes.
// Do not use its axis-aligned bounding box: that overestimates oblique walls.
export function hostDepthCm(meshes: THREE.Mesh[], id: number, position: THREE.Vector3, normal: THREE.Vector3,
  original: (mesh: THREE.Mesh) => THREE.BufferGeometry = mesh => mesh.geometry): number | undefined {
  const outward = normal.clone().normalize();
  const ray = new THREE.Raycaster(position.clone().addScaledVector(outward, .001), outward.clone().negate());
  ray.firstHitOnly = false;
  const material = new THREE.MeshBasicMaterial({ side: THREE.DoubleSide });
  let depth = Infinity;
  try {
    for (const mesh of meshes) {
      const geometry = original(mesh), ids = geometry.getAttribute('_element') || geometry.getAttribute('_ELEMENT');
      if (!ids) continue;
      mesh.updateWorldMatrix(true, false);
      const proxy = new THREE.Mesh(geometry, material);
      proxy.matrixAutoUpdate = false; proxy.matrixWorld.copy(mesh.matrixWorld);
      for (const hit of ray.intersectObject(proxy, false)) {
        if (hit.faceIndex == null || !hit.face) continue;
        const vertex = geometry.index ? geometry.index.getX(hit.faceIndex * 3) : hit.faceIndex * 3;
        if (Math.round(ids.getX(vertex)) !== id) continue;
        const distance = hit.distance - .001;
        const faceNormal = hit.face.normal.clone().applyNormalMatrix(new THREE.Matrix3().getNormalMatrix(mesh.matrixWorld));
        if (distance > .0001 && faceNormal.dot(outward) < -.01) depth = Math.min(depth, distance);
      }
    }
  } finally { material.dispose(); }
  // Round up to the editable millimetre, so the opening reaches the opposite face.
  return Number.isFinite(depth) && depth * 30.48 <= 1000 ? Math.max(1, Math.ceil(depth * 304.8 - 1e-4) / 10) : undefined;
}

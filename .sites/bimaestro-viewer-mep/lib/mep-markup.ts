import * as THREE from 'three';

export type Markup = {
  id: string; kind: 'note' | 'reservation'; elementKey: string; elementName: string;
  position: [number, number, number]; normal: [number, number, number];
  text: string; widthCm: number; heightCm: number; depthCm: number;
  modelRevision: number;
  stableKey?: string;
  anchorCenter?: [number, number, number];
  anchorSize?: [number, number, number];
  needsReview?: boolean;
};

// Model coordinates are feet, Y-up. Keep vertical edges vertical on wall faces.
export function markupQuaternion(normal: [number, number, number]) {
  const z = new THREE.Vector3(...normal).normalize();
  const reference = Math.abs(z.y) > .99 ? new THREE.Vector3(0, 0, -1) : new THREE.Vector3(0, 1, 0);
  const x = new THREE.Vector3().crossVectors(reference, z).normalize();
  const y = new THREE.Vector3().crossVectors(z, x).normalize();
  return new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, y, z));
}

export function markupObject(mark: Markup) {
  const group = new THREE.Group();
  group.position.set(...mark.position);
  group.quaternion.copy(markupQuaternion(mark.normal));
  const color = mark.kind === 'note' ? '#fbbf24' : '#38bdf8';
  const geometry = mark.kind === 'note' ? new THREE.SphereGeometry(.09, 16, 12)
    : new THREE.BoxGeometry(mark.widthCm / 30.48, mark.heightCm / 30.48, mark.depthCm / 30.48);
  const mesh = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ color, transparent: true, opacity: mark.kind === 'note' ? 1 : .28, depthWrite: false }));
  mesh.position.z = mark.kind === 'note' ? .07 : -mark.depthCm / 30.48 / 2 + .015;
  group.add(mesh);
  if (mark.kind === 'reservation') mesh.visible = false;
  if (mark.kind === 'reservation') {
    const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), new THREE.LineBasicMaterial({ color }));
    edges.position.copy(mesh.position); group.add(edges);
  }
  return group;
}

export function disposeMarkups(group: THREE.Group) {
  group.traverse((object) => {
    if (object instanceof THREE.Mesh || object instanceof THREE.LineSegments) {
      object.geometry.dispose();
      (Array.isArray(object.material) ? object.material : [object.material]).forEach((material: THREE.Material) => material.dispose());
    }
  });
  group.clear();
}

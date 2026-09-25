import * as THREE from 'three';

export type Markup = {
  id: string; kind: 'note' | 'reservation'; elementKey: string; elementName: string;
  position: [number, number, number]; normal: [number, number, number];
  text: string; widthCm: number; heightCm: number; depthCm: number;
  shape?: 'rectangle' | 'round'; diameterCm?: number; lot?: string;
  dimensions?: ReservationDimension[];
  modelRevision: number;
  stableKey?: string;
  anchorCenter?: [number, number, number];
  anchorSize?: [number, number, number];
  needsReview?: boolean;
  reviewCandidate?: boolean;
};

export type ReservationDimension = {
  id: string; elementKey: string; elementName: string;
  point: [number, number, number]; normal: [number, number, number];
};
export const reservationLot = (mark: Markup) => mark.lot?.trim() || 'MEP';
export function lotColor(lot: string) {
  const colors: Record<string, string> = { MEP: '#0284c7', GC: '#c2410c', ELEC: '#7c3aed' };
  if (colors[lot]) return colors[lot];
  return `hsl(${Array.from(lot).reduce((n, c) => (n * 31 + c.charCodeAt(0)) % 360, 0)}, 65%, 40%)`;
}
export const reservationSize = (mark: Markup) => mark.shape === 'round' ? [mark.diameterCm || mark.widthCm, mark.diameterCm || mark.widthCm] : [mark.widthCm, mark.heightCm];
export const reservationLabel = (mark: Markup) => mark.shape === 'round' ? `Ø ${reservationSize(mark)[0]} cm` : `${mark.widthCm} × ${mark.heightCm} cm`;
export function reservationGeometry(mark: Markup, extraDepth = 0) {
  const [width, height] = reservationSize(mark), depth = mark.depthCm / 30.48 + extraDepth;
  return mark.shape === 'round'
    ? new THREE.CylinderGeometry(width / 60.96, width / 60.96, depth, 64).rotateX(Math.PI / 2)
    : new THREE.BoxGeometry(width / 30.48, height / 30.48, depth);
}
// Signed perpendicular distance from the centre on the host face to the reference plane.
export function dimensionDistance(mark: Markup, dimension: ReservationDimension) {
  return new THREE.Vector3(...mark.position).sub(new THREE.Vector3(...dimension.point)).dot(new THREE.Vector3(...dimension.normal)) * 30.48;
}
export function dimensionFoot(mark: Markup, dimension: ReservationDimension) {
  return new THREE.Vector3(...mark.position).addScaledVector(new THREE.Vector3(...dimension.normal), -dimensionDistance(mark, dimension) / 30.48);
}
export function moveToDimension(mark: Markup, dimension: ReservationDimension, cm: number): Markup {
  if (!Number.isFinite(cm) || Math.abs(cm) > 100000) throw new Error('Distance invalide.');
  const n = new THREE.Vector3(...dimension.normal), host = new THREE.Vector3(...mark.normal).normalize();
  const direction = n.clone().addScaledVector(host, -n.dot(host));
  if (direction.lengthSq() < .0001) throw new Error('Choisissez un sol ou un mur transversal à la face de la réservation.');
  const position = new THREE.Vector3(...mark.position).addScaledVector(direction, (cm - dimensionDistance(mark, dimension)) / 30.48 / direction.lengthSq());
  return { ...mark, position: position.toArray() };
}

// Model coordinates are feet, Y-up. Keep vertical edges vertical on wall faces.
export function markupQuaternion(normal: [number, number, number]) {
  const z = new THREE.Vector3(...normal).normalize();
  const reference = Math.abs(z.y) > .99 ? new THREE.Vector3(0, 0, -1) : new THREE.Vector3(0, 1, 0);
  const x = new THREE.Vector3().crossVectors(reference, z).normalize();
  const y = new THREE.Vector3().crossVectors(z, x).normalize();
  return new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, y, z));
}

export function markupObject(mark: Markup, options: { color?: string; showDimensions?: boolean } = {}) {
  const group = new THREE.Group();
  group.position.set(...mark.position);
  group.quaternion.copy(markupQuaternion(mark.normal));
  const color = options.color || lotColor(reservationLot(mark));
  const geometry = mark.kind === 'note' ? new THREE.SphereGeometry(.09, 16, 12)
    : reservationGeometry(mark);
  const mesh = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ color, transparent: true, opacity: mark.kind === 'note' ? 1 : .28, depthWrite: false }));
  mesh.position.z = mark.kind === 'note' ? .07 : -mark.depthCm / 30.48 / 2 + .015;
  group.add(mesh);
  if (mark.kind === 'reservation') mesh.visible = false;
  if (mark.kind === 'reservation') {
    const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), new THREE.LineBasicMaterial({ color, depthTest: !mark.needsReview }));
    edges.position.copy(mesh.position); group.add(edges);
    const inverse = group.quaternion.clone().invert();
    for (const dimension of options.showDimensions ? mark.dimensions || [] : []) {
      const end = dimensionFoot(mark, dimension).sub(group.position).applyQuaternion(inverse);
      const tick = new THREE.Vector3(.08, .08, 0);
      const lines = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), end, tick.clone().negate(), tick, end.clone().sub(tick), end.clone().add(tick)]);
      group.add(new THREE.LineSegments(lines, new THREE.LineBasicMaterial({ color, depthTest: false })));
    }
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

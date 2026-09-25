import * as THREE from 'three';

export type MeasurePoint = [number, number, number];
export type MeasureMode = 'free' | 'horizontal';
export function measureEnd(start: MeasurePoint, end: MeasurePoint, mode: MeasureMode, viewRight?: THREE.Vector3): MeasurePoint {
  if (mode === 'horizontal' && viewRight) {
    const axis = viewRight.clone().setY(0).normalize();
    if (axis.lengthSq() > .5) {
      const a = new THREE.Vector3(...start);
      return a.clone().addScaledVector(axis, new THREE.Vector3(...end).sub(a).dot(axis)).toArray();
    }
  }
  return mode === 'horizontal' ? [end[0], start[1], end[2]] : end;
}
export type TemporaryMeasure = { id: string; start: MeasurePoint; end: MeasurePoint; mode?: MeasureMode };
export function measureDistances(start: MeasurePoint, end: MeasurePoint) {
  const dx = (end[0] - start[0]) * .3048, dy = (end[1] - start[1]) * .3048, dz = (end[2] - start[2]) * .3048;
  return { direct: Math.hypot(dx, dy, dz), horizontal: Math.hypot(dx, dz), vertical: Math.abs(dy) };
}
export function measureObject(measure: TemporaryMeasure) {
  const group = new THREE.Group();
  const a = new THREE.Vector3(...measure.start), b = new THREE.Vector3(...measure.end);
  const addLine = (points: THREE.Vector3[], color: string) => {
    const line = new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(points), new THREE.LineBasicMaterial({ color, depthTest: false, depthWrite: false }));
    line.renderOrder = 100; group.add(line);
  };
  if (measure.mode === 'horizontal') {
    const { from, to } = horizontalDimension(measure);
    const axis = to.clone().sub(from);
    const tick = new THREE.Vector3(-axis.z, 0, axis.x).normalize().multiplyScalar(.09);
    // One straight dimension and two short end marks. No diagonal return to
    // the raw second pick: that extension was producing the unwanted V.
    addLine([from, to, from.clone().sub(tick), from.clone().add(tick), to.clone().sub(tick), to.clone().add(tick)], '#15803d');
  } else {
    // Original free measurement: direct segment between the actual 3D picks,
    // with its horizontal and vertical components, without an offset dimension.
    const corner = new THREE.Vector3(b.x, a.y, b.z);
    const sameLevel = Math.abs(a.y - b.y) < 1e-6, sameVertical = a.distanceTo(corner) < 1e-6;
    const segments = sameLevel ? [[a, b, '#15803d']] as const : sameVertical ? [[a, b, '#b45309']] as const
      : [[a, b, '#0369a1'], [a, corner, '#15803d'], [corner, b, '#b45309']] as const;
    for (const [from, to, color] of segments) {
      if (from.distanceToSquared(to) > 1e-12) addLine([from, to], color);
    }
  }
  return group;
}

export function horizontalDimension(measure: TemporaryMeasure) {
  return { from: new THREE.Vector3(...measure.start), to: new THREE.Vector3(measure.end[0], measure.start[1], measure.end[2]) };
}
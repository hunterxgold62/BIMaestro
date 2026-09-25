import * as THREE from 'three';
import { markupQuaternion, type Markup } from './mep-markup';

export function reservationBounds(mark: Markup) {
  const depth = mark.depthCm / 30.48;
  const bounds = new THREE.Box3(
    new THREE.Vector3(-mark.widthCm / 60.96, -mark.heightCm / 60.96, -depth - .002),
    new THREE.Vector3(mark.widthCm / 60.96, mark.heightCm / 60.96, .002),
  );
  return bounds.applyMatrix4(new THREE.Matrix4().compose(new THREE.Vector3(...mark.position), markupQuaternion(mark.normal), new THREE.Vector3(1, 1, 1)));
}

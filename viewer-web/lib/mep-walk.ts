import * as THREE from 'three';

const groundNormal = new THREE.Vector3();
const down = new THREE.Vector3(0, -1, 0);

export function isWalkableHit(hit: THREE.Intersection) {
  return !!hit.face && groundNormal.copy(hit.face.normal).transformDirection(hit.object.matrixWorld).y > .45;
}

export function findLandingFootY(
  position: THREE.Vector3,
  objects: THREE.Object3D[],
  isHitVisible: (hit: THREE.Intersection) => boolean,
) {
  // Tiles can contain both a steep face near the camera and a floor below it.
  const ray = new THREE.Raycaster(position, down);
  const landing = ray.intersectObjects(objects, false).find(hit => isWalkableHit(hit) && isHitVisible(hit));
  return landing ? landing.point.y + .04 : null;
}

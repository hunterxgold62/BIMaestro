import { expect, test } from 'vitest';
import * as THREE from 'three';
import { computeBoundsTree, disposeBoundsTree } from 'three-mesh-bvh';
import { hostDepthCm, isReservationHost } from './mep-host';
import { WallCutouts } from './mep-cutouts';
import { measureEnd, measureObject } from './mep-measure';
import { orbitSelection } from './mep-selection';
import type { WebProperty } from './mep-contract';
import type { Markup } from './mep-markup';
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;

function host(width: number, height: number, depth: number, id = 1) {
  const geometry = new THREE.BoxGeometry(width, height, depth).toNonIndexed();
  geometry.setAttribute('_element', new THREE.Float32BufferAttribute(new Float32Array(geometry.getAttribute('position').count).fill(id), 1));
  return new THREE.Mesh(geometry);
}
test('detects real thickness along rotated wall normals and ignores other elements', () => {
  const wall = host(10, 8, .75), other = host(10, 8, .2, 2);
  wall.rotation.y = .65; wall.position.set(15, 20, 30); wall.updateMatrixWorld();
  const point = new THREE.Vector3(0, 0, .375).applyMatrix4(wall.matrixWorld);
  const normal = new THREE.Vector3(0, 0, 1).transformDirection(wall.matrixWorld);
  expect(hostDepthCm([other, wall], 1, point, normal)).toBe(22.9);
  expect(hostDepthCm([other], 1, point, normal)).toBeUndefined();
});
test.each([1, -1])('detects slab thickness and cuts through from face %s', sign => {
  const floor = host(10, .8, 10), original = floor.geometry;
  const position = new THREE.Vector3(0, sign * .4, 0), normal = new THREE.Vector3(0, sign, 0);
  const depth = hostDepthCm([floor], 1, position, normal)!;
  expect(depth).toBe(24.4);
  const property = { index: 1, category: 'Sols', key: 'floor' } as WebProperty;
  expect(isReservationHost(property)).toBe(true);
  const mark: Markup = { id: 'floor-cut', kind: 'reservation', elementKey: 'floor', elementName: 'Sol', position: position.toArray(), normal: normal.toArray(), widthCm: 60, heightCm: 40, depthCm: depth, text: '', modelRevision: 1 };
  const cuts = new WallCutouts([floor], new Map([[1, property]]));
  cuts.update([mark]);
  const probe = new THREE.Mesh(floor.geometry, new THREE.MeshBasicMaterial({ side: THREE.DoubleSide }));
  expect(new THREE.Raycaster(new THREE.Vector3(0, 2, 0), new THREE.Vector3(0, -1, 0)).intersectObject(probe)).toHaveLength(0);
  expect(new THREE.Raycaster(new THREE.Vector3(3, 2, 0), new THREE.Vector3(0, -1, 0)).intersectObject(probe).length).toBeGreaterThan(0);
  cuts.update([]); expect(floor.geometry).toBe(original); cuts.dispose();
});
test('horizontal measures remain at the first level and render one straight dimension with short end marks', () => {
  const start: [number, number, number] = [1, 4, 2];
  const end = measureEnd(start, [5, 4.1, 8], 'horizontal');
  expect(end).toEqual([5, 4, 8]);
  expect(measureEnd(start, [5, 4.1, 8], 'free')).toEqual([5, 4.1, 8]);
  const object = measureObject({ id: 'm', start, end, mode: 'horizontal' });
  expect(object.children).toHaveLength(1);
  const line = object.children[0] as THREE.LineSegments;
  expect(line.geometry.getAttribute('position').count).toBe(6);
});

test('selected orbit settles near the top without flipping at the vertical limit', () => {
  const camera = new THREE.PerspectiveCamera(), pivot = new THREE.Vector3(), target = pivot.clone();
  camera.position.set(0, 10, .001); camera.lookAt(target);
  orbitSelection(camera, target, pivot, 0, 0);
  const settled = camera.quaternion.clone();
  for (let i = 0; i < 100; i++) orbitSelection(camera, target, pivot, 0, 0);
  expect(camera.quaternion.angleTo(settled)).toBeLessThan(1e-6);
  expect(Math.abs(Math.asin(camera.getWorldDirection(new THREE.Vector3()).y))).toBeLessThanOrEqual(1.480001);
});
test('orbit retains the selected centre after zoom and pan without re-centring the view', () => {
  const camera = new THREE.PerspectiveCamera(), pivot = new THREE.Vector3(3, 2, 1), target = pivot.clone();
  camera.position.set(12, 7, 15); camera.lookAt(target);
  for (const translation of [new THREE.Vector3(-2, -1, -3), new THREE.Vector3(4, 2, 0)]) { camera.position.add(translation); target.add(translation); }
  const radius = camera.position.distanceTo(pivot), offset = target.distanceTo(pivot), viewDistance = camera.position.distanceTo(target);
  orbitSelection(camera, target, pivot, .4, .1);
  expect(camera.position.distanceTo(pivot)).toBeCloseTo(radius, 8);
  expect(target.distanceTo(pivot)).toBeCloseTo(offset, 8);
  expect(camera.position.distanceTo(target)).toBeCloseTo(viewDistance, 8);
  expect(pivot.toArray()).toEqual([3, 2, 1]);
});

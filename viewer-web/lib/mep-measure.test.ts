import { expect, test } from 'vitest';
import { measureDistances, horizontalDimension, measureEnd, measureObject } from './mep-measure';
import * as THREE from 'three';

test('measures direct, horizontal and vertical distances in metres in a Y-up scene', () => {
  const values = measureDistances([10, 20, 30], [13, 32, 34]);
  expect(values.direct).toBeCloseTo(13 * .3048);
  expect(values.horizontal).toBeCloseTo(5 * .3048);
  expect(values.vertical).toBeCloseTo(12 * .3048);
  expect(measureDistances([13, 32, 34], [10, 20, 30])).toEqual(values);
  expect(measureDistances([0, 0, 0], [0, 0, 0])).toEqual({ direct: 0, horizontal: 0, vertical: 0 });
});

test('view horizontal projects the second point instead of measuring the plan diagonal', () => {
  const camera = new THREE.PerspectiveCamera(48, 1, .1, 1000);
  camera.position.set(0, 10, 10); camera.lookAt(0, 0, 0); camera.updateMatrixWorld();
  const start: [number, number, number] = [0, 0, 0], rawEnd: [number, number, number] = [10, 2, 6];
  const end = measureEnd(start, rawEnd, 'horizontal', new THREE.Vector3(1,0,0).applyQuaternion(camera.quaternion));
  expect(end).toEqual([10,0,0]);
  expect(measureDistances(start, end).horizontal).toBeCloseTo(3.048);
  const measure = { id: 'aligned', start, end, mode: 'horizontal' as const };
  const { from, to } = horizontalDimension(measure);
  expect(from.clone().project(camera).y).toBeCloseTo(to.clone().project(camera).y, 8);
  const line = measureObject(measure).children[0] as THREE.LineSegments;
  const points = line.geometry.getAttribute('position');
  expect(points.count).toBe(6);
  expect(new THREE.Vector3().fromBufferAttribute(points, 0).toArray()).toEqual(start);
  expect(new THREE.Vector3().fromBufferAttribute(points, 1).toArray()).toEqual(end);
  for (const index of [2, 4]) expect(new THREE.Vector3().fromBufferAttribute(points,index).distanceTo(new THREE.Vector3().fromBufferAttribute(points,index+1))).toBeCloseTo(.18);
});

test('free mode keeps the original direct segment between the actual picks at different elevations', () => {
  const start: [number,number,number] = [1,2,3], end: [number,number,number] = [8,14,20];
  expect(measureEnd(start,end,'free',new THREE.Vector3(1,0,0))).toEqual(end);
  const group = measureObject({ id: 'free', start, end, mode: 'free' });
  const points = (group.children[0] as THREE.LineSegments).geometry.getAttribute('position');
  expect(new THREE.Vector3().fromBufferAttribute(points,0).toArray()).toEqual(start);
  expect(new THREE.Vector3().fromBufferAttribute(points,1).toArray()).toEqual(end);
});

test('horizontal dimension stays level and has the projected length even for sloping measurements', () => {
  const measure = { id: 'slope', start: [10, 20, 30] as [number, number, number], end: [13, 32, 34] as [number, number, number] };
  const { from, to } = horizontalDimension(measure);
  expect(from.y).toBe(to.y);
  expect(from.distanceTo(to) * .3048).toBeCloseTo(measureDistances(measure.start, measure.end).horizontal);
  expect(from.distanceTo(to)).toBeCloseTo(5);
});

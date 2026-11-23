import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import { findLandingFootY } from './mep-walk';

function floor(x: number, y: number, z: number) {
  const mesh = new THREE.Mesh(new THREE.PlaneGeometry(8, 8));
  mesh.rotateX(-Math.PI / 2);
  mesh.position.set(x, y, z);
  mesh.updateMatrixWorld();
  return mesh;
}

describe('leaving flight mode', () => {
  it('lands at the current horizontal position on the closest level below', () => {
    const origin = new THREE.Vector3(30, 12, -20);
    const floors = [floor(0, 9, 0), floor(30, 14, -20), floor(30, 1, -20), floor(30, 6, -20)];
    expect(findLandingFootY(origin, floors, () => true)).toBeCloseTo(6.04);
    expect(origin.toArray()).toEqual([30, 12, -20]);
  });

  it('skips a level excluded by the visible section', () => {
    const floors = [floor(0, 8, 0), floor(0, 2, 0)];
    expect(findLandingFootY(new THREE.Vector3(0, 12, 0), floors, hit => hit.point.y < 5)).toBeCloseTo(2.04);
  });

  it('returns no landing outside the model or above a steep face alone', () => {
    const slope = floor(0, 1, 0);
    slope.rotateX(Math.PI * .42);
    slope.updateMatrixWorld();
    expect(findLandingFootY(new THREE.Vector3(0, 12, 0), [slope], () => true)).toBeNull();
    expect(findLandingFootY(new THREE.Vector3(30, 12, 0), [floor(0, 0, 0)], () => true)).toBeNull();
  });
});

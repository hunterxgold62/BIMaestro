import { describe, expect, it } from 'vitest';
import { Box3, BoxGeometry, Mesh, Plane, Vector3 } from 'three';
import { defaultSection, sectionContains, sectionPlane, sectionFromFace, sectionPlanes, faceSectionPlane } from './mep-section';

import { MeshBVH } from 'three-mesh-bvh';
import { appendSectionOutline } from './mep-section-outline';

const bounds = new Box3(new Vector3(-20, 10, 5), new Vector3(40, 30, 15));
describe('section plane', () => {
  it('creates a cut through the clicked face, retaining the side behind that face', () => {
    const cut = sectionFromFace('one', 'Coupe 1', new Vector3(10, 2, 3), new Vector3(1, 1, 0));
    const plane = faceSectionPlane(cut);
    expect(plane.distanceToPoint(new Vector3(10, 2, 3))).toBeCloseTo(0);
    expect(sectionContains(plane, new Vector3(9, 1, 3))).toBe(true);
    expect(sectionContains(plane, new Vector3(11, 3, 3))).toBe(false);
    cut.offset = 2;
    expect(plane.distanceToPoint(new Vector3(10, 2, 3))).toBeCloseTo(0);
    expect(faceSectionPlane(cut).distanceToPoint(new Vector3(10, 2, 3))).toBeCloseTo(-2);
  });
  it('intersects multiple cuts and restores the missing region when one is removed', () => {
    const left = sectionFromFace('left', 'Left', new Vector3(0, 0, 0), new Vector3(-1, 0, 0));
    const right = sectionFromFace('right', 'Right', new Vector3(10, 0, 0), new Vector3(1, 0, 0));
    expect(sectionContains(sectionPlanes([left, right]), new Vector3(5, 0, 0))).toBe(true);
    expect(sectionContains(sectionPlanes([left, right]), new Vector3(15, 0, 0))).toBe(false);
    expect(sectionContains(sectionPlanes([left]), new Vector3(15, 0, 0))).toBe(true);
    expect(sectionContains(sectionPlanes([]), new Vector3(-5, 0, 0))).toBe(true);
    right.enabled = false;
    expect(sectionContains(sectionPlanes([left, right]), new Vector3(15, 0, 0))).toBe(true);
  });
  it('keeps the full model when disabled or bounds are unavailable', () => {
    expect(sectionPlane(defaultSection, bounds)).toBeNull();
    expect(sectionPlane({ ...defaultSection, enabled: true }, new Box3())).toBeNull();
    expect(sectionContains(null, new Vector3(0, 100, 0))).toBe(true);
  });
  it('cuts horizontally at the midpoint and reverses around the same plane', () => {
    const settings = { ...defaultSection, enabled: true };
    const plane = sectionPlane(settings, bounds)!;
    expect(sectionContains(plane, new Vector3(0, 19, 0))).toBe(true);
    expect(sectionContains(plane, new Vector3(0, 21, 0))).toBe(false);
    expect(sectionContains(plane, new Vector3(0, 20, 0))).toBe(true);
    const inverse = sectionPlane({ ...settings, inverted: true }, bounds)!;
    expect(sectionContains(inverse, new Vector3(0, 19, 0))).toBe(false);
    expect(sectionContains(inverse, new Vector3(0, 21, 0))).toBe(true);
  });
  it('uses actual bounds for both vertical axes and slider endpoints', () => {
    for (const axis of ['x', 'z'] as const) {
      for (const position of [0, 25, 100]) {
        const plane = sectionPlane({ enabled: true, axis, position, inverted: false }, bounds)!;
        const point = bounds.min.clone().lerp(bounds.max, position / 100);
        expect(plane.distanceToPoint(point)).toBeCloseTo(0);
        point[axis] += 1;
        expect(sectionContains(plane, point)).toBe(false);
      }
    }
  });
});

describe('section outline', () => {
  it.each([false, true])('trims the intersections of transformed geometry against the other planes (BVH: %s)', useBvh => {
    const mesh = new Mesh(new BoxGeometry(4, 2, 4));
    mesh.position.set(6, 3, -4);
    mesh.scale.set(2, 1, 1);
    mesh.rotation.y = Math.PI / 2;
    mesh.updateMatrixWorld();
    if (useBvh) mesh.geometry.boundsTree = new MeshBVH(mesh.geometry);

    const horizontal = new Plane(new Vector3(0, -1, 0), 3);
    const vertical = new Plane(new Vector3(1, 0, 0), -6);
    const positions: number[] = [];
    appendSectionOutline(mesh, [horizontal, vertical], positions);

    let horizontalLength = 0, verticalLength = 0;
    for (let i = 0; i < positions.length; i += 6) {
      const start = new Vector3().fromArray(positions, i);
      const end = new Vector3().fromArray(positions, i + 3);
      expect(sectionContains([horizontal, vertical], start)).toBe(true);
      expect(sectionContains([horizontal, vertical], end)).toBe(true);
      if (Math.abs(start.y - 3) < 1e-6 && Math.abs(end.y - 3) < 1e-6) horizontalLength += start.distanceTo(end);
      else {
        expect(start.x).toBeCloseTo(6);
        expect(end.x).toBeCloseTo(6);
        verticalLength += start.distanceTo(end);
      }
    }
    expect(horizontalLength).toBeCloseTo(12);
    expect(verticalLength).toBeCloseTo(10);
  });

  it('updates when the cut moves and omits triangles lying on the plane', () => {
    const mesh = new Mesh(new BoxGeometry(2, 2, 2));
    const top: number[] = [], middle: number[] = [], outside: number[] = [];
    appendSectionOutline(mesh, [new Plane(new Vector3(0, -1, 0), 1)], top);
    appendSectionOutline(mesh, [new Plane(new Vector3(0, -1, 0), 0)], middle);
    appendSectionOutline(mesh, [new Plane(new Vector3(0, -1, 0), 2)], outside);
    expect(top).toHaveLength(4 * 6);
    expect(middle).toHaveLength(8 * 6);
    expect(middle.filter((_, i) => i % 3 === 1).every(y => y === 0)).toBe(true);
    expect(outside).toHaveLength(0);
  });
});

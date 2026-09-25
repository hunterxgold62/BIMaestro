import { describe, it, expect } from 'vitest';
import * as THREE from 'three';
import { computeBoundsTree, disposeBoundsTree } from 'three-mesh-bvh';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { cutWall, triangleSubset, WallCutouts } from './mep-cutouts';
import type { Markup } from './mep-markup';
import type { WebProperty } from './mep-contract';
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;
const mark: Markup = { id: 'test', kind: 'reservation', elementKey: 'wall', elementName: 'Mur', position: [0, 0, .5], normal: [0, 0, 1], widthCm: 60.96, heightCm: 60.96, depthCm: 40, text: '', modelRevision: 1 };
function wall(id = 1, z = 0) {
  const g = new THREE.BoxGeometry(10, 10, 1).toNonIndexed(); g.translate(0, 0, z);
  g.setAttribute('_element', new THREE.Float32BufferAttribute(Array.from({ length: g.getAttribute('position').count }, () => id), 1));
  return g;
}
function volume(g: THREE.BufferGeometry) {
  const p = g.getAttribute('position'); let result = 0;
  for (let i = 0; i < p.count; i += 3) {
    const a = new THREE.Vector3().fromBufferAttribute(p, i), b = new THREE.Vector3().fromBufferAttribute(p, i + 1), c = new THREE.Vector3().fromBufferAttribute(p, i + 2);
    result += a.dot(b.cross(c)) / 6;
  }
  return Math.abs(result);
}
describe('wall and floor cutouts', () => {
  it('cuts a cylindrical bore, leaving material at corners of its bounding square', () => {
    const g = cutWall(wall(), new THREE.Matrix4(), [{ ...mark, shape: 'round', diameterCm: 60.96 }], 1);
    expect(volume(g)).toBeCloseTo(100 - Math.PI, 1);
    const mesh = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ side: THREE.DoubleSide })); mesh.updateMatrixWorld();
    expect(new THREE.Raycaster(new THREE.Vector3(0, 0, 2), new THREE.Vector3(0, 0, -1)).intersectObject(mesh)).toHaveLength(0);
    expect(new THREE.Raycaster(new THREE.Vector3(.9, .9, 2), new THREE.Vector3(0, 0, -1)).intersectObject(mesh).length).toBeGreaterThan(0);
    g.dispose();
  });
  it('cuts walls and floors within depth, preserving elements beyond the volume', () => {
    const original = mergeGeometries([wall(1, 0), wall(2, -1), wall(3, -4), wall(4, -1)], false)!;
    const mesh = new THREE.Mesh(original); mesh.updateMatrixWorld();
    const properties = new Map([1, 2, 3, 4].map(id => [id, { key: id === 1 ? 'wall' : String(id), category: id === 4 ? 'Sols' : 'Murs' } as WebProperty]));
    const controller = new WallCutouts([mesh], properties);
    controller.update([{ ...mark, depthCm: 60.96 }]);
    expect(volume(triangleSubset(mesh.geometry, id => id === 1))).toBeCloseTo(96, 3);
    expect(volume(triangleSubset(mesh.geometry, id => id === 2))).toBeCloseTo(96, 3);
    expect(volume(triangleSubset(mesh.geometry, id => id === 3))).toBeCloseTo(100, 3);
    expect(volume(triangleSubset(mesh.geometry, id => id === 4))).toBeCloseTo(96, 3);
    controller.update([]); expect(mesh.geometry).toBe(original);
  });
  it('opens the full thickness with interior faces and keeps host IDs', () => {
    const g = cutWall(wall(), new THREE.Matrix4(), [mark], 1);
    expect(volume(g)).toBeCloseTo(96, 4);
    expect(new Set(g.getAttribute('_element').array)).toEqual(new Set([1]));
    const mesh = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ side: THREE.DoubleSide })); mesh.updateMatrixWorld();
    expect(new THREE.Raycaster(new THREE.Vector3(0, 0, 2), new THREE.Vector3(0, 0, -1)).intersectObject(mesh)).toHaveLength(0);
    expect(new THREE.Raycaster(new THREE.Vector3(0, 0, 0), new THREE.Vector3(1, 0, 0)).intersectObject(mesh).length).toBeGreaterThan(0);
  });
  it('supports overlapping reservations without double subtraction', () => {
    const g = cutWall(wall(), new THREE.Matrix4(), [mark, { ...mark, id: 'second', position: [1, 0, .5] }], 1);
    expect(volume(g)).toBeCloseTo(94, 4);
  });
  it('handles rotated and translated model coordinates', () => {
    const world = new THREE.Matrix4().makeRotationY(Math.PI / 2); world.setPosition(100, 0, 20);
    const position = new THREE.Vector3(...mark.position).applyMatrix4(world).toArray();
    expect(volume(cutWall(wall(), world, [{ ...mark, position, normal: [1, 0, 0] }], 1))).toBeCloseTo(96, 3);
  });
  it('preserves the adjacent wall and restores the original on deletion', () => {
    const original = mergeGeometries([wall(), wall(2, -3)], false)!;
    const mesh = new THREE.Mesh(original); mesh.updateMatrixWorld();
    const props = new Map([[1, { key: 'wall', category: 'Murs' } as WebProperty], [2, { key: 'other', category: 'Murs' } as WebProperty]]);
    const controller = new WallCutouts([mesh], props); controller.update([mark]);
    expect(volume(triangleSubset(mesh.geometry, id => id === 1))).toBeCloseTo(96, 4);
    expect(Array.from(triangleSubset(mesh.geometry, id => id === 2).getAttribute('position').array)).toEqual(Array.from(wall(2, -3).getAttribute('position').array));
    controller.update([]); expect(mesh.geometry).toBe(original); controller.dispose();
  });
  it('does not cut non-wall elements and rejects open wall geometry', () => {
    const mesh = new THREE.Mesh(wall());
    new WallCutouts([mesh], new Map([[1, { key: 'wall', category: 'Canalisations' } as WebProperty]])).update([mark]);
    expect(volume(mesh.geometry)).toBeCloseTo(100);
    const plane = new THREE.PlaneGeometry(10, 10).toNonIndexed(); plane.setAttribute('_element', new THREE.Float32BufferAttribute([1, 1, 1, 1, 1, 1], 1));
    expect(() => cutWall(plane, new THREE.Matrix4(), [mark], 1)).toThrow('faces manquantes');
  });
  it('uses the selected depth for a recess instead of drilling the full wall', () => {
    expect(volume(cutWall(wall(), new THREE.Matrix4(), [{ ...mark, depthCm: 15.24 }], 1))).toBeCloseTo(98, 3);
  });
  it('assembles faces of a wall split across several exported chunks', () => {
    const source = wall(); const left = new THREE.BufferGeometry(), right = new THREE.BufferGeometry();
    for (const name of Object.keys(source.attributes)) {
      const attr = source.getAttribute(name); const middle = 18 * attr.itemSize;
      left.setAttribute(name, new THREE.Float32BufferAttribute(Array.from(attr.array).slice(0, middle), attr.itemSize));
      right.setAttribute(name, new THREE.Float32BufferAttribute(Array.from(attr.array).slice(middle), attr.itemSize));
    }
    const a = new THREE.Mesh(left), b = new THREE.Mesh(right);
    const controller = new WallCutouts([a, b], new Map([[1, { key: 'wall', category: 'Murs' } as WebProperty]]));
    controller.update([mark]); expect(volume(a.geometry) + volume(b.geometry)).toBeCloseTo(96, 4);
    controller.update([]); expect(a.geometry).toBe(left); expect(b.geometry).toBe(right);
  });
});

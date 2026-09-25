import { describe, it, expect } from 'vitest';
import * as THREE from 'three';
import { acceleratedRaycast, computeBoundsTree, disposeBoundsTree } from 'three-mesh-bvh';
import { BuildingVisibility, elementCenter, blocksView } from './mep-visibility';
import { cutWall } from './mep-cutouts';
import type { WebProperty } from './mep-contract';
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;
THREE.Mesh.prototype.raycast = acceleratedRaycast;
function mesh(id: number, z: number) {
 const geometry = new THREE.BoxGeometry(4, 4, .5).toNonIndexed();
 geometry.setAttribute('_element', new THREE.Float32BufferAttribute(Array.from({ length: geometry.getAttribute('position').count }, () => id), 1));
 const result = new THREE.Mesh(geometry); result.position.z = z; result.updateMatrixWorld(); return result;
}
const property = (category: string) => ({ category } as WebProperty);
describe('architecture visibility and orbit pivot', () => {
 it('ignores clipped blockers but still checks retained faces and hidden parents', () => {
  const wall = mesh(1, 0), parent = new THREE.Group(); parent.add(wall);
  const visibility = new BuildingVisibility([wall], new Map([[1, property('Murs')]])); visibility.sync();
  const camera = new THREE.Vector3(0, 0, 5), anchor = new THREE.Vector3(0, 0, -2);
  expect(visibility.obscured(camera, anchor, [new THREE.Plane(new THREE.Vector3(0,0,-1), -1)])).toBe(false);
  expect(visibility.obscured(camera, anchor, [new THREE.Plane(new THREE.Vector3(0,0,-1), 0)])).toBe(true);
  parent.visible = false; visibility.sync();
  expect(visibility.obscured(camera, anchor)).toBe(false); visibility.dispose();
 });
 it('ignores pipes but masks labels behind walls and respects the anchor surface', () => {
  const pipe = mesh(1, 2), wall = mesh(2, 0);
  const visibility = new BuildingVisibility([pipe, wall], new Map([[1, property('Canalisations')], [2, property('Murs')]])); visibility.sync();
  const camera = new THREE.Vector3(0, 0, 5);
  expect(visibility.obscured(camera, new THREE.Vector3(0, 0, 1))).toBe(false);
  expect(visibility.obscured(camera, new THREE.Vector3(0, 0, .25))).toBe(false);
  expect(visibility.obscured(camera, new THREE.Vector3(0, 0, -2))).toBe(true); visibility.dispose();
 });
 it('tracks door transforms and wall cutout replacements', () => {
  const door = mesh(1, 0); const visibility = new BuildingVisibility([door], new Map([[1, property('Portes')]]));
  const camera = new THREE.Vector3(0, 0, 5), anchor = new THREE.Vector3(0, 0, -2);
  visibility.sync(); expect(visibility.obscured(camera, anchor)).toBe(true);
  door.position.x = 5; visibility.sync(); expect(visibility.obscured(camera, anchor)).toBe(false);
  door.position.x = 0; door.updateMatrixWorld();
  door.geometry = cutWall(door.geometry, door.matrixWorld, [{ id:'hole', kind:'reservation', elementKey:'door',elementName:'',position:[0,0,.25],normal:[0,0,1],widthCm:60,heightCm:60,depthCm:50,text:'',modelRevision:1 }], 1);
  visibility.sync(); expect(visibility.obscured(camera, anchor)).toBe(false); visibility.dispose();
 });
 it('finds the center across fragmented and transformed geometry', () => {
  const a = mesh(1, 0), b = mesh(1, 10), other = mesh(2, 100);
  expect(elementCenter([a,b,other],1)?.z).toBeCloseTo(5);
  expect(elementCenter([a,b],99)).toBeNull();
  expect(blocksView(property('Équipements mécaniques'))).toBe(false);
  expect(blocksView(property('Plafonds'))).toBe(true);
 });
});

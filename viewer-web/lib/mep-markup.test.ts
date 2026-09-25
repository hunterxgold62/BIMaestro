import { describe, it, expect } from 'vitest';
import * as THREE from 'three';
import { markupObject, markupQuaternion, disposeMarkups, dimensionDistance, moveToDimension, reservationGeometry, type Markup } from './mep-markup';

const mark: Markup = { id: '12345678-1234-1234-1234-123456789012', kind: 'reservation', elementKey: 'wall-1', elementName: 'Mur', position: [10, 20, 30], normal: [0, 0, 1], text: '', widthCm: 60, heightCm: 40, depthCm: 30, modelRevision: 1 };
describe('3D markups', () => {
  it('hides reservation dimension lines by default and shows them only when requested', () => {
    const dimension = { id: 'floor', elementKey: 'floor', elementName: 'Sol', point: [0, 0, 0] as [number, number, number], normal: [0, 1, 0] as [number, number, number] };
    const marked = { ...mark, dimensions: [dimension] };
    const general = markupObject(marked), selected = markupObject(marked, { showDimensions: true, color: '#ff0000' });
    expect(general.children).toHaveLength(2); expect(selected.children).toHaveLength(3);
    expect(((selected.children[1] as THREE.LineSegments).material as THREE.LineBasicMaterial).color.getHexString()).toBe('ff0000');
    disposeMarkups(general); disposeMarkups(selected);
  });
  it('keeps a round bore circular and aligns its depth with the face normal', () => {
    const geometry = reservationGeometry({ ...mark, shape: 'round', diameterCm: 20 });
    geometry.computeBoundingBox();
    const size = geometry.boundingBox!.getSize(new THREE.Vector3()).multiplyScalar(30.48);
    expect(size.x).toBeCloseTo(20); expect(size.y).toBeCloseTo(20); expect(size.z).toBeCloseTo(30);
    geometry.dispose();
  });
  it('moves to a floor or angled reference while staying on the host face', () => {
    const floor = { id: 'floor', elementKey: 'floor', elementName: 'Sol', point: [0, 0, 0] as [number, number, number], normal: [0, 1, 0] as [number, number, number] };
    const moved = moveToDimension(mark, floor, 125);
    expect(dimensionDistance(moved, floor)).toBeCloseTo(125);
    expect(moved.position[2]).toBe(mark.position[2]);
    const angled = { ...floor, normal: [Math.SQRT1_2, 0, Math.SQRT1_2] as [number, number, number] };
    expect(dimensionDistance(moveToDimension(mark, angled, 50), angled)).toBeCloseTo(50);
    expect(() => moveToDimension(mark, { ...floor, normal: [0, 0, 1] }, 30)).toThrow(/transversal/);
    expect(() => moveToDimension(mark, floor, NaN)).toThrow();
  });
  it('converts centimeters to feet and places depth inside the picked face', () => {
    const group = markupObject(mark); const mesh = group.children[0] as THREE.Mesh<THREE.BoxGeometry>;
    expect(mesh.geometry.parameters.width * 30.48).toBeCloseTo(60);
    expect(mesh.geometry.parameters.height * 30.48).toBeCloseTo(40);
    expect(mesh.position.z + mesh.geometry.parameters.depth / 2).toBeCloseTo(.015);
    expect(group.position.toArray()).toEqual(mark.position); disposeMarkups(group);
    expect(group.children).toHaveLength(0);
  });
  it('keeps the height vertical on an angled wall and handles floors', () => {
    const q = markupQuaternion([Math.SQRT1_2, 0, Math.SQRT1_2]);
    expect(new THREE.Vector3(0, 1, 0).applyQuaternion(q).y).toBeCloseTo(1);
    expect(new THREE.Vector3(0, 0, 1).applyQuaternion(q).x).toBeCloseTo(Math.SQRT1_2);
    expect(new THREE.Vector3(0, 0, 1).applyQuaternion(markupQuaternion([0, 1, 0])).y).toBeCloseTo(1);
  });
});

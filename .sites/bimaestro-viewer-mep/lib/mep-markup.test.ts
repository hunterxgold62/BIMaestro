import { describe, it, expect } from 'vitest';
import * as THREE from 'three';
import { markupObject, markupQuaternion, disposeMarkups, type Markup } from './mep-markup';

const mark: Markup = { id: '12345678-1234-1234-1234-123456789012', kind: 'reservation', elementKey: 'wall-1', elementName: 'Mur', position: [10, 20, 30], normal: [0, 0, 1], text: '', widthCm: 60, heightCm: 40, depthCm: 30, modelRevision: 1 };
describe('3D markups', () => {
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

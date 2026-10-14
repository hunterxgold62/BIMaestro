import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import { waterPathRadii } from './mep-water-size';
import { createWaterVolume } from './mep-water-volume';
import type { MepConnector, WebProperty } from './mep-contract';

const radius = (dn: number) => dn / 304.8 / 2;
const connector = (index: number, dn: number): MepConnector => ({ index, elementKey: 'pipe', systemKey: 'water', position: [index, 0, 0], crossSectionArea: Math.PI * radius(dn) ** 2 });
const path = { startConnector: 0, endConnector: 1 };
describe('Water uses exported pipe sections', () => {
  it('preserves DN500 / DN80 geometry ratio, without screen-size scaling', () => {
    const volumes = [80, 500].map(dn => {
      const radii = waterPathRadii(path, new Map([0, 1].map(i => [i, connector(i, dn)])))!;
      const water = createWaterVolume([new THREE.Vector3(), new THREE.Vector3(10, 0, 0)], '#29b9cf', radii.start, radii.end);
      water.mesh.geometry.computeBoundingBox();
      expect(water.uniforms).not.toHaveProperty('radiusScale');
      const width = water.mesh.geometry.boundingBox!.getSize(new THREE.Vector3()).y;
      water.mesh.geometry.dispose(); water.mesh.material.dispose();
      return width;
    });
    expect(volumes[1] / volumes[0]).toBeCloseTo(6.25, 5);
  });
  it('retains both end sections through a reducer', () => {
    const radii = waterPathRadii(path, new Map([[0, connector(0, 500)], [1, connector(1, 80)]]))!;
    expect(radii.start).toBeCloseTo(radius(500));
    expect(radii.end).toBeCloseTo(radius(80));
    const water = createWaterVolume([new THREE.Vector3(), new THREE.Vector3(10, 0, 0)], '#29b9cf', radii.start, radii.end);
    const positions = water.mesh.geometry.getAttribute('position'), uv = water.mesh.geometry.getAttribute('uv');
    for (let i = 0; i < positions.count; i++) {
      expect(Math.hypot(positions.getY(i), positions.getZ(i))).toBeCloseTo(THREE.MathUtils.lerp(radius(500), radius(80), uv.getX(i)), 5);
    }
    water.mesh.geometry.dispose(); water.mesh.material.dispose();
  });
  it('uses an explicitly unit-labelled diameter, never a bounding box', () => {
    const property: WebProperty = { index: 1, key: 'pipe', elementId: 1, name: 'Pipe', category: 'Tuyaux', typeName: '', levelName: '', documentTitle: '', size: [500, 500, 500], properties: { 'Diamètre': '80 mm' } };
    expect(waterPathRadii(path, new Map(), property)?.start).toBeCloseTo(radius(80));
    expect(waterPathRadii(path, new Map(), { ...property, properties: {} })).toBeNull();
    expect(waterPathRadii(path, new Map(), { ...property, properties: { 'Diamètre': '80' } })).toBeNull();
  });
});

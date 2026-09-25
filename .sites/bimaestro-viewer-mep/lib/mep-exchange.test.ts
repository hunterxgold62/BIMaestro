import { expect, test } from 'vitest';
import * as THREE from 'three';
import { writeFileSync } from 'node:fs';
import { anchorMarkup, reconcileMarkups } from './mep-revisions';
import { reservationExchange, reservationsIfc, ifcGuid } from './mep-exchange';
import { ElementSelection, zoomStep } from './mep-selection';
import type { Markup } from './mep-markup';
import type { WebProperty, ViewerPackage } from './mep-contract';

const property: WebProperty = { index: 1, key: 'oldpath|3', stableKey: 'doc|uid', center: [0, 0, 0], size: [10, 8, 1], name: 'Mur été', category: 'Murs', elementId: 3, typeName: '', levelName: '', documentTitle: '', properties: {} };
const mark: Markup = { id: '12345678-1234-1234-1234-123456789012', kind: 'reservation', elementKey: property.key, elementName: property.name, modelRevision: 1, position: [4, 3, 2], normal: [0, 0, 1], widthCm: 60, heightCm: 40, depthCm: 30, text: 'L’ouverture' };
const manifest = { schemaVersion: 2, sourceOrigin: [1000, 2000, 50], sourceDocumentId: 'doc', name: 'test', documentTitle: '', viewName: '', units: 'revit-internal-feet', coordinateSystem: 'right-handed-z-up', files: {} } as ViewerPackage['manifest'];

test('republication follows a stable host and translates the anchor, retaining the original', () => {
  const old = anchorMarkup(mark, property, 1);
  const next = reconcileMarkups([old], [{ ...property, key: 'renamed|3', center: [100, 2, -20] }], 2)[0];
  expect(next.position).toEqual([104, 5, -18]); expect(next.modelRevision).toBe(2); expect(next.needsReview).toBe(false);
  expect(old.position).toEqual([4, 3, 2]); expect(next.id).toBe(mark.id);
});
test('missing, resized, ambiguous and legacy hosts keep annotations for manual review', () => {
  const anchored = anchorMarkup(mark, property, 1);
  for (const props of [[], [property, property], [{ ...property, size: [12, 8, 1] as [number, number, number] }]])
    expect(reconcileMarkups([anchored], props, 2)[0].needsReview).toBe(true);
  expect(reconcileMarkups([mark], [property], 2)[0].needsReview).toBe(true);
});
test('Revit exchange restores internal coordinates, axes, units and skips unresolved reservations', () => {
  const exchange = reservationExchange([mark, { ...mark, needsReview: true }], manifest, '12345678-1234-1234-1234-123456789012');
  expect(exchange.reservations).toHaveLength(1);
  expect(exchange.reservations[0].position).toEqual([1004, 1998, 53]);
  expect(exchange.reservations[0].normal.map(n => n || 0)).toEqual([0, -1, 0]);
  expect(exchange.reservations[0].widthCm).toBe(60);
  expect(() => reservationExchange([mark], { ...manifest, sourceOrigin: undefined }, 'id')).toThrow(/Republiez/);
});
test('IFC has stable identifiers, metre dimensions and provision solids', () => {
  const exchange = reservationExchange([mark], manifest, '12345678-1234-1234-1234-123456789012');
  const ifc = reservationsIfc(exchange);
  expect(ifcGuid('00000000-0000-0000-0000-000000000000')).toBe('0000000000000000000000');
  expect(ifcGuid('ffffffff-ffff-ffff-ffff-ffffffffffff')).toBe('3$$$$$$$$$$$$$$$$$$$$$');
  expect(ifc).toContain('.PROVISIONFORVOID.'); expect(ifc).toContain('0.600000000,0.400000000');
  expect(ifc).toContain('306.019200000,608.990400000,16.154400000');
  if (process.env.BIMAESTRO_IFC_FIXTURE) {
    writeFileSync(process.env.BIMAESTRO_IFC_FIXTURE, ifc);
    writeFileSync(process.env.BIMAESTRO_IFC_FIXTURE + '.json', JSON.stringify(exchange));
  }
});
test('selection only includes the chosen element in a merged mesh and follows replacement geometry', () => {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([0,0,0, 1,0,0, 0,1,0, 100,0,0, 101,0,0, 100,1,0], 3));
  geometry.setAttribute('_element', new THREE.Float32BufferAttribute([1,1,1,2,2,2], 1));
  const mesh = new THREE.Mesh(geometry); const selection = new ElementSelection();
  selection.set([mesh], new Set([1])); expect(selection.bounds.max.x).toBe(1);
  expect((selection.group.children[0] as THREE.Mesh).geometry.index?.count).toBe(3);
  mesh.geometry = geometry.clone().translate(10, 0, 0); selection.sync(); expect(selection.bounds.max.x).toBe(11);
  selection.dispose(); geometry.dispose(); mesh.geometry.dispose();
});
test('zoom scales with distance and remains bounded near a wall', () => {
  expect(zoomStep(1000, -100)).toBeGreaterThan(200);
  expect(zoomStep(1, -100)).toBeLessThan(1);
  expect(zoomStep(100, 100)).toBeLessThan(0);
});

import { expect, test, vi } from 'vitest';
import * as THREE from 'three';
import { writeFileSync } from 'node:fs';
import { anchorMarkup, reconcileMarkups } from './mep-revisions';
import { annotationExchange, annotationsIfc, reservationExchange, reservationsIfc, ifcGuid } from './mep-exchange';
import { ElementSelection, zoomStep } from './mep-selection';
import type { Markup } from './mep-markup';
import { reservationLots } from './mep-lots';
import type { WebProperty, ViewerPackage } from './mep-contract';

const property: WebProperty = { index: 1, key: 'oldpath|3', stableKey: 'doc|uid', center: [0, 0, 0], size: [10, 8, 1], name: 'Mur été', category: 'Murs', elementId: 3, typeName: '', levelName: '', documentTitle: '', properties: {} };
const mark: Markup = { id: '12345678-1234-1234-1234-123456789012', kind: 'reservation', elementKey: property.key, elementName: property.name, modelRevision: 1, position: [4, 3, 2], normal: [0, 0, 1], widthCm: 60, heightCm: 40, depthCm: 30, text: 'L’ouverture' };
const manifest = { schemaVersion: 2, sourceOrigin: [1000, 2000, 50], sharedCoordinates: { origin: [0,0,0], xAxis: [1,0,0], siteName: 'Site actif' }, sourceDocumentId: 'doc', name: 'test', documentTitle: '', viewName: '', units: 'revit-internal-feet', coordinateSystem: 'right-handed-z-up', files: {} } as ViewerPackage['manifest'];

test('shared IFC applies site translation, rotation and elevation exactly once, keeping Revit exchange internal', () => {
  const shared = { origin: [2000000, 6000000, 100] as [number, number, number], xAxis: [0,1,0] as [number, number, number], siteName: 'Site géoréférencé' };
  const exchange = reservationExchange([mark], { ...manifest, sharedCoordinates: shared }, 'id');
  expect(exchange.reservations[0].position).toEqual([1004,1998,53]);
  const ifc = reservationsIfc(exchange);
  expect(ifc).toContain('IFCSITE(');
  expect(ifc).toContain('609600.000000000,1828800.000000000,30.480000000');
  expect(ifc).toContain('IFCDIRECTION((0.000000000,1.000000000,0.000000000))');
  // Read the site placement from the actual IFC and independently transform the product origin.
  const entities = new Map([...ifc.matchAll(/(#\d+)=(.*);/g)].map(m => [m[1], m[2]]));
  const site = [...entities.values()].find(value => value.startsWith('IFCSITE('))!;
  const local = entities.get(site.split(',')[5])!;
  const frame = entities.get(local.match(/IFCLOCALPLACEMENT\(\$,([#\d]+)\)/)![1])!;
  const refs = frame.match(/#\d+/g)!;
  const numbers = (id: string) => entities.get(id)!.match(/-?\d+\.\d+/g)!.map(Number);
  const origin = new THREE.Vector3(...numbers(refs[0]));
  const x = new THREE.Vector3(...numbers(refs[2])), z = new THREE.Vector3(...numbers(refs[1]));
  const y = new THREE.Vector3().crossVectors(z,x);
  const p = exchange.reservations[0].position.map(n => n * .3048);
  const mapped = origin.addScaledVector(x,p[0]).addScaledVector(y,p[1]).addScaledVector(z,p[2]);
  expect(mapped.x).toBeCloseTo((2000000 - 1998) * .3048, 6);
  expect(mapped.y).toBeCloseTo((6000000 + 1004) * .3048, 6);
  expect(mapped.z).toBeCloseTo(153 * .3048, 6);
});

test('legacy publications cannot silently export internal coordinates as shared IFC', () => {
  const exchange = reservationExchange([mark], { ...manifest, sharedCoordinates: undefined }, 'id');
  expect(() => reservationsIfc(exchange)).toThrow(/repère partagé manque/);
  expect(reservationsIfc(exchange, 'internal')).toContain('END-ISO-10303-21;');
});

test('annotation IFC exports confirmed notes with text, host identity and shared coordinates independently of reservations', () => {
  const note: Markup = { ...mark, kind: 'note', text: "Vérifier l'été", stableKey: 'doc|uid' };
  const exchange = annotationExchange([mark, note, { ...note, needsReview: true }], manifest, 'publication');
  expect(exchange.annotations).toHaveLength(1);
  expect(exchange.annotations[0].position).toEqual([1004, 1998, 53]);
  const ifc = annotationsIfc(exchange);
  expect(ifc).toContain('IFCBUILDINGELEMENTPROXY(');
  expect(ifc).toContain("'Body','SweptSolid'");
  expect(ifc.match(/IFCEXTRUDEDAREASOLID\(/g)).toHaveLength(40);
  expect(ifc).toContain('0.000000000,-1.000000000,0.000000000');
  expect(ifc).toContain("'BIMaestro Annotation'");
  expect(ifc).toContain("IFCPROPERTYSET(");
  expect(ifc).toContain("'BIMaestro_Annotation'");
  expect(ifc).toContain("'StableKey',$,IFCIDENTIFIER('doc|uid')");
  expect(ifc).toContain('306.019200000,608.990400000,16.154400000');
  expect(ifc).not.toContain('IFCTEXTLITERAL(');
  if (process.env.BIMAESTRO_ANNOTATION_IFC_FIXTURE) writeFileSync(process.env.BIMAESTRO_ANNOTATION_IFC_FIXTURE, ifc);
  expect(() => annotationsIfc(annotationExchange([note], { ...manifest, sharedCoordinates: undefined }, 'publication'))).toThrow(/repère partagé manque/);
});

test('export worker returns an IFC and reports invalid exports without touching the viewer', async () => {
  const postMessage = vi.fn();
  const scope = { postMessage, onmessage: null as null | ((event: unknown) => void) };
  vi.stubGlobal('self', scope);
  try {
    await import('./mep-exchange.worker');
    scope.onmessage!({ data: { args: [[mark], manifest, 'publication'], ifc: true } });
    expect(postMessage.mock.calls[0][0].content).toContain('END-ISO-10303-21;');
    scope.onmessage!({ data: { args: [[], manifest, 'publication'], ifc: true } });
    expect(postMessage.mock.calls[1][0].error).toContain('Aucune réservation');
    scope.onmessage!({ data: { args: [[{ ...mark, kind: 'note', text: 'Contrôle' }], manifest, 'publication'], ifc: true, annotations: true } });
    expect(postMessage.mock.calls[2][0].content).toContain('IFCBUILDINGELEMENTPROXY(');
  } finally { vi.unstubAllGlobals(); }
});

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
test('exports all lots or exactly one lot, retaining circular profiles and lot properties', () => {
  const round: Markup = { ...mark, shape: 'round', diameterCm: 20, lot: 'ELEC' };
  const marks = [mark, round, { ...round, needsReview: true }];
  expect(reservationExchange(marks, manifest, 'id').reservations).toHaveLength(2);
  const exchange = reservationExchange(marks, manifest, 'id', 'ELEC');
  expect(exchange.reservations).toHaveLength(1);
  expect(exchange.reservations[0]).toMatchObject({ shape: 'round', diameterCm: 20, widthCm: 20, heightCm: 20, lot: 'ELEC' });
  expect(reservationsIfc(exchange)).toContain('IFCCIRCLEPROFILEDEF(.AREA.,$,$,0.100000000)');
  expect(reservationsIfc(exchange)).toContain("IFCLABEL('ELEC')");
  expect(reservationExchange(marks, manifest, 'id', 'GC').reservations).toHaveLength(0);
});
test('renaming a lot preserves its ID, reservations, colour and export filter', () => {
  const settings = { ELEC: { name: 'Électricité', color: '#f97316' } };
  const marks = [{ ...mark, lot: 'ELEC' }, { ...mark, lot: 'TEST' }];
  const lots = reservationLots(marks, settings);
  expect(lots.map(lot => lot.id)).toEqual(expect.arrayContaining(['MEP', 'GC', 'ELEC', 'TEST']));
  expect(lots.find(lot => lot.id === 'ELEC')).toEqual({ id: 'ELEC', name: 'Électricité', color: '#f97316' });
  const exchange = reservationExchange(marks, manifest, 'id', 'ELEC', settings);
  expect(exchange.reservations).toHaveLength(1);
  expect(exchange.reservations[0].lot).toBe('Électricité');
  expect(exchange.reservations[0].color).toBe('#f97316');
  const ifc = reservationsIfc(exchange);
  expect(ifc).toContain('0.976470588,0.450980392,0.086274510');
  expect(ifc).toMatch(/IFCSURFACESTYLESHADING\(#\d+,0\.\)/);
  expect(ifc).toMatch(/IFCSTYLEDITEM\(#\d+,\(#\d+\),\$\)/);
  expect(marks[0].lot).toBe('ELEC');
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


test('review previews follow the recovered host without losing reservation identity or dimensions', () => {
  const old = anchorMarkup({ ...mark, dimensions: [{ id: 'ref', elementKey: 'wall', elementName: 'Wall', point: [5, 6, 7], normal: [1, 0, 0] }] }, property, 1);
  const moved = { ...property, center: [100, 2, -20] as [number, number, number] };
  const next = reconcileMarkups([old], [moved], 2)[0];
  expect(next.needsReview).toBe(true);
  expect(next.reviewCandidate).toBe(true);
  expect(next.position).toEqual([104, 5, -18]);
  expect(next.dimensions![0].point).toEqual([105, 8, -13]);
  expect(next.id).toBe(old.id);
  expect(next.modelRevision).toBe(1);
  expect(old.dimensions![0].point).toEqual([5, 6, 7]);
  expect(anchorMarkup(next, moved, 2).needsReview).toBe(false);
});


test('a uniquely replaced support is proposed only when the model offset is corroborated', () => {
  const witnesses = [1, 2, 3].map(i => ({ ...property, key: `w${i}`, stableKey: `w${i}`, center: [i * 20, 0, 0] as [number, number, number] }));
  const old = anchorMarkup(mark, property, 1);
  const anchors = witnesses.map((p, i) => anchorMarkup({ ...mark, id: `a${i}` }, p, 1));
  const current = witnesses.map(p => ({ ...p, center: [p.center[0] + 100, 0, 0] as [number, number, number] }));
  const replacement = { ...property, key: 'replacement', stableKey: 'replacement', center: [100, 0, 0] as [number, number, number] };
  const next = reconcileMarkups([old, ...anchors], [replacement, ...current], 2)[0];
  expect(next.reviewCandidate).toBe(true);
  expect(next.needsReview).toBe(true);
  expect(next.elementKey).toBe('replacement');
  expect(reconcileMarkups([old, ...anchors], [replacement, { ...replacement, key: 'duplicate' }, ...current], 2)[0].reviewCandidate).not.toBe(true);
});

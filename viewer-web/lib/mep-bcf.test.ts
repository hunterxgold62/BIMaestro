import { expect, test } from 'vitest';
import { strFromU8, unzipSync } from 'fflate';
import { writeFileSync } from 'node:fs';
import { annotationsBcf } from './mep-bcf';
import type { Markup } from './mep-markup';
import type { ViewerPackage } from './mep-contract';

const note: Markup = {
  id: '12345678-1234-1234-1234-123456789012', kind: 'note', elementKey: 'old|wall', stableKey: 'doc|revit-uid',
  elementName: 'Mur & dalle', position: [4, 3, 2], normal: [0, 0, 1], text: 'Vérifier <la réservation>',
  widthCm: 30, heightCm: 30, depthCm: 0, modelRevision: 1,
};
const manifest = {
  name: 'Maquette MEP', sourceDocumentId: 'doc', sourceOrigin: [1000, 2000, 50],
  sharedCoordinates: { origin: [0, 0, 0], xAxis: [1, 0, 0], siteName: 'Site' },
} as ViewerPackage['manifest'];

test('BCF 3.0 contains one topic per confirmed note with snapshot and georeferenced viewpoint', async () => {
  const png = Uint8Array.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const archive = await annotationsBcf(
    [note, { ...note, id: '22345678-1234-1234-1234-123456789012', needsReview: true }, { ...note, kind: 'reservation' }],
    manifest, 'publication', async () => ({ image: png, position: note.position, direction: [0, 0, -1], up: [0, 1, 0], fieldOfView: 48, aspectRatio: 4 / 3 }),
    [{ index: 1, key: note.elementKey, elementId: 3, name: note.elementName, category: 'Murs', typeName: '', levelName: '', documentTitle: '', properties: { 'IFC GUID': '1234567890123456789012' } }],
  );
  const files = unzipSync(archive), folder = `${note.id}/`;
  if (process.env.BIMAESTRO_BCF_FIXTURE) writeFileSync(process.env.BIMAESTRO_BCF_FIXTURE, archive);
  expect(Object.keys(files).sort()).toEqual(['bcf.version', 'project.bcfp', folder + 'markup.bcf', folder + 'snapshot.png', folder + 'viewpoint.bcfv'].sort());
  expect(strFromU8(files['bcf.version'])).toContain('VersionId="3.0"');
  expect(strFromU8(files[folder + 'markup.bcf'])).toContain('Vérifier &lt;la réservation&gt;');
  expect(strFromU8(files[folder + 'markup.bcf'])).toContain('<Snapshot>snapshot.png</Snapshot>');
  expect(strFromU8(files[folder + 'viewpoint.bcfv'])).toContain('<AuthoringToolId>revit-uid</AuthoringToolId>');
  expect(strFromU8(files[folder + 'viewpoint.bcfv'])).toContain('IfcGuid="1234567890123456789012"');
  expect(strFromU8(files[folder + 'viewpoint.bcfv'])).toContain('<Lines>');
  expect(strFromU8(files[folder + 'viewpoint.bcfv'])).toContain('<X>306.0192</X><Y>608.9904</Y><Z>16.1544</Z>');
  expect(files[folder + 'snapshot.png']).toEqual(png);
});

test('BCF export requires confirmed notes and shared coordinates', async () => {
  const capture = async () => ({ image: Uint8Array.from([1]), position: note.position, direction: [0, 0, -1] as [number, number, number], up: [0, 1, 0] as [number, number, number], fieldOfView: 48, aspectRatio: 1 });
  await expect(annotationsBcf([note], { ...manifest, sharedCoordinates: undefined }, 'publication', capture)).rejects.toThrow(/coordonnées partagées/);
  await expect(annotationsBcf([{ ...note, needsReview: true }], manifest, 'publication', capture)).rejects.toThrow(/Aucune annotation/);
});

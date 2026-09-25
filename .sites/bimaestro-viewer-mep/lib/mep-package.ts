import { unzip, strFromU8 } from 'fflate';
import { manifestSchema, type MepReplay, type ViewerConfig, type ViewerPackage, type WebProperty } from './mep-contract';

export async function digest(data: Uint8Array): Promise<string> {
  const copy = Uint8Array.from(data);
  const hash = await crypto.subtle.digest('SHA-256', copy.buffer);
  return [...new Uint8Array(hash)].map((value) => value.toString(16).padStart(2, '0')).join('');
}

export async function openViewerPackage(url: string): Promise<ViewerPackage> {
  const response = await fetch(url, { cache: 'no-store', referrerPolicy: 'no-referrer' });
  if (!response.ok) throw new Error(`Paquet indisponible (${response.status})`);
  const data = new Uint8Array(await response.arrayBuffer());
  const archive = await new Promise<Record<string, Uint8Array>>((resolve, reject) => unzip(data, (error, files) => error ? reject(error) : resolve(files)));
  const required = ['manifest.json', 'mep.json', 'properties.json'];
  for (const name of required) if (!archive[name]) throw new Error(`Fichier ${name} manquant`);
  const manifest = manifestSchema.parse(JSON.parse(strFromU8(archive['manifest.json'])));
  if (manifest.files['overview.glb']) { required.push('overview.glb'); if (!archive['overview.glb']) throw new Error('Vue globale manquante'); }
  if (manifest.schemaVersion === 1) { required.push('model.glb'); if (!archive['model.glb']) throw new Error('Fichier model.glb manquant'); }
  for (const name of required.filter((item) => item !== 'manifest.json')) {
    const expected = manifest.files[name];
    if (!expected || expected.bytes !== archive[name].byteLength || await digest(archive[name]) !== expected.sha256) {
      throw new Error(`Contrôle d’intégrité échoué pour ${name}`);
    }
  }
  const modelUrl = manifest.schemaVersion === 1 ? URL.createObjectURL(new Blob([Uint8Array.from(archive['model.glb'])], { type: 'model/gltf-binary' })) : '';
  const overviewUrl = archive['overview.glb'] && manifest.files['overview.glb'] ? URL.createObjectURL(new Blob([Uint8Array.from(archive['overview.glb'])], { type: 'model/gltf-binary' })) : undefined;
  const viewerFile = archive['viewer.json'];
  if (viewerFile) {
    const expected = manifest.files['viewer.json'];
    if (!expected || expected.bytes !== viewerFile.byteLength || await digest(viewerFile) !== expected.sha256) throw new Error('Contrôle d’intégrité échoué pour viewer.json');
  }
  return {
    manifest,
    modelUrl, overviewUrl,
    replay: JSON.parse(strFromU8(archive['mep.json'])) as MepReplay,
    properties: JSON.parse(strFromU8(archive['properties.json'])) as WebProperty[],
    viewer: viewerFile ? JSON.parse(strFromU8(viewerFile)) as ViewerConfig : null,
    dispose: () => { URL.revokeObjectURL(modelUrl); if (overviewUrl) URL.revokeObjectURL(overviewUrl); },
  };
}

import { zipSync, strToU8 } from 'fflate';
import type { ViewerPackage, WebProperty } from './mep-contract';
import type { Markup } from './mep-markup';

export type BcfCapture = {
  image: Uint8Array;
  position: [number, number, number];
  direction: [number, number, number];
  up: [number, number, number];
  fieldOfView: number;
  aspectRatio: number;
};

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const xml = (value: string) => value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');
const xmlDocument = (body: string) => `<?xml version="1.0" encoding="UTF-8"?>\n${body}`;
const coordinate = (value: number) => { if (!Number.isFinite(value)) throw new Error('Coordonnée BCF invalide.'); return Number(value.toFixed(6)); };
const vector = (tag: string, v: [number, number, number]) => `<${tag}><X>${coordinate(v[0])}</X><Y>${coordinate(v[1])}</Y><Z>${coordinate(v[2])}</Z></${tag}>`;

export async function annotationsBcf(
  marks: Markup[], manifest: ViewerPackage['manifest'], publicationId: string,
  capture: (mark: Markup) => Promise<BcfCapture>,
  properties: WebProperty[] = [],
) {
  if (!manifest.sourceOrigin || !manifest.sourceDocumentId || !manifest.sharedCoordinates)
    throw new Error('Republiez la maquette depuis Revit avec les coordonnées partagées pour exporter le BCF.');
  const notes = marks.filter(mark => mark.kind === 'note' && !mark.needsReview);
  if (!notes.length) throw new Error('Aucune annotation confirmée à exporter en BCF.');
  const { sourceOrigin, sharedCoordinates } = manifest;
  const xAxis = sharedCoordinates.xAxis;
  const rotate = (v: [number, number, number]): [number, number, number] => {
    const internal: [number, number, number] = [v[0], -v[2], v[1]];
    return [xAxis[0] * internal[0] - xAxis[1] * internal[1], xAxis[1] * internal[0] + xAxis[0] * internal[1], internal[2]];
  };
  const sharedPoint = (v: [number, number, number]): [number, number, number] => {
    const p = rotate(v);
    const internalOrigin: [number, number, number] = [
      xAxis[0] * sourceOrigin[0] - xAxis[1] * sourceOrigin[1],
      xAxis[1] * sourceOrigin[0] + xAxis[0] * sourceOrigin[1],
      sourceOrigin[2],
    ];
    return p.map((n, i) => (n + internalOrigin[i] + sharedCoordinates.origin[i]) * .3048) as [number, number, number];
  };
  const files: Record<string, Uint8Array> = {
    'bcf.version': strToU8(xmlDocument('<Version VersionId="3.0"/>')),
    'project.bcfp': strToU8(xmlDocument(`<ProjectInfo><Project ProjectId="${xml(publicationId)}"><Name>${xml(manifest.name || 'Maquette MEP')}</Name></Project></ProjectInfo>`)),
  };
  const exportedAt = new Date().toISOString();
  for (const mark of notes) {
    const shot = await capture(mark);
    if (!shot.image.length || !Number.isFinite(shot.fieldOfView) || shot.fieldOfView <= 0 || shot.fieldOfView >= 180 || !Number.isFinite(shot.aspectRatio) || shot.aspectRatio <= 0)
      throw new Error(`Capture BCF invalide pour l’annotation « ${mark.elementName} ».`);
    const topicId = uuid.test(mark.id) ? mark.id : crypto.randomUUID();
    const viewpointId = crypto.randomUUID();
    const base = `${topicId}/`;
    const title = (`Annotation — ${mark.elementName || 'Maquette MEP'}`).slice(0, 160);
    const authoringId = mark.stableKey?.split('|').at(-1) || mark.elementKey;
    const host = properties.find(property => property.key === mark.elementKey || (!!mark.stableKey && property.stableKey === mark.stableKey));
    const ifcGuid = Object.entries(host?.properties || {}).find(([key, value]) => /^(ifc\s*guid|global\s*id)$/i.test(key.trim()) && /^[0-9A-Za-z_$]{22}$/.test(value.trim()))?.[1].trim();
    const camera = `<PerspectiveCamera>${vector('CameraViewPoint', sharedPoint(shot.position))}${vector('CameraDirection', rotate(shot.direction))}${vector('CameraUpVector', rotate(shot.up))}<FieldOfView>${shot.fieldOfView}</FieldOfView><AspectRatio>${shot.aspectRatio}</AspectRatio></PerspectiveCamera>`;
    const component = authoringId ? `<Components><Selection><Component${ifcGuid ? ` IfcGuid="${ifcGuid}"` : ''}><OriginatingSystem>Revit</OriginatingSystem><AuthoringToolId>${xml(authoringId)}</AuthoringToolId></Component></Selection></Components>` : '';
    const point = sharedPoint(mark.position);
    const lines = `<Lines>${([0, 1, 2] as const).map(axis => {
      const start = [...point] as [number, number, number], end = [...point] as [number, number, number];
      start[axis] -= .15; end[axis] += .15;
      return `<Line>${vector('StartPoint', start)}${vector('EndPoint', end)}</Line>`;
    }).join('')}</Lines>`;
    const viewpoint = xmlDocument(`<VisualizationInfo Guid="${viewpointId}">${component}${camera}${lines}</VisualizationInfo>`);
    const markup = xmlDocument(`<Markup><Topic Guid="${topicId}" TopicType="Comment" TopicStatus="Open"><Title>${xml(title)}</Title><CreationDate>${exportedAt}</CreationDate><CreationAuthor>BIMaestro</CreationAuthor><Description>${xml(mark.text || title)}</Description><Viewpoints><ViewPoint Guid="${viewpointId}"><Viewpoint>viewpoint.bcfv</Viewpoint><Snapshot>snapshot.png</Snapshot><Index>0</Index></ViewPoint></Viewpoints></Topic></Markup>`);
    files[base + 'markup.bcf'] = strToU8(markup);
    files[base + 'viewpoint.bcfv'] = strToU8(viewpoint);
    files[base + 'snapshot.png'] = shot.image;
  }
  return zipSync(files, { level: 2 });
}

export function downloadBcf(content: Uint8Array, name: string) {
  const url = URL.createObjectURL(new Blob([new Uint8Array(content)], { type: 'application/zip' }));
  const link = document.createElement('a'); link.href = url; link.download = name;
  link.target = '_blank'; link.rel = 'noopener'; link.hidden = true;
  document.body.appendChild(link);
  try { link.click(); } finally { link.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 60000); }
}

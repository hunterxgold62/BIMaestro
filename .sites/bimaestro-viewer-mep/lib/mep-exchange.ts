import * as THREE from 'three';
import type { ViewerPackage } from './mep-contract';
import { markupQuaternion, type Markup } from './mep-markup';

export function reservationExchange(marks: Markup[], manifest: ViewerPackage['manifest'], publicationId: string) {
  if (!manifest.sourceOrigin || !manifest.sourceDocumentId) throw new Error('Republiez la maquette avec le nouveau plugin pour exporter dans le repère Revit.');
  const origin = manifest.sourceOrigin;
  const rootVector = (v: THREE.Vector3) => [v.x, -v.z, v.y] as [number, number, number];
  return { schemaVersion: 1, kind: 'bimaestro-reservations', units: 'revit-internal-feet', sourceDocumentId: manifest.sourceDocumentId,
    publicationId, exportedAt: new Date().toISOString(), reservations: marks.filter(m => m.kind === 'reservation' && !m.needsReview).map(mark => {
      const point = rootVector(new THREE.Vector3(...mark.position)).map((n, i) => n + origin[i]);
      const xAxis = rootVector(new THREE.Vector3(1, 0, 0).applyQuaternion(markupQuaternion(mark.normal)));
      return { id: mark.id, elementKey: mark.elementKey, stableKey: mark.stableKey, elementName: mark.elementName, text: mark.text,
        position: point, normal: rootVector(new THREE.Vector3(...mark.normal)), xAxis,
        widthCm: mark.widthCm, heightCm: mark.heightCm, depthCm: mark.depthCm };
    }) };
}

const alphabet = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$';
export function ifcGuid(uuid: string) {
  let n = BigInt('0x' + uuid.replace(/-/g, '')), result = '';
  for (let i = 0; i < 22; i++) { result = alphabet[Number(n & BigInt(63))] + result; n >>= BigInt(6); }
  return result;
}
function text(value: string) {
  return "'" + value.replace(/'/g, "''").replace(/[^\x20-\x7e]|\\/g, char => '\\X2\\' + char.charCodeAt(0).toString(16).padStart(4, '0').toUpperCase() + '\\X0\\') + "'";
}
const number = (n: number) => { if (!Number.isFinite(n)) throw new Error('Coordonnée invalide'); return n.toFixed(9); };
export function reservationsIfc(exchange: ReturnType<typeof reservationExchange>) {
  if (!exchange.reservations.length) throw new Error('Aucune réservation dont l’emplacement est confirmé à exporter.');
  const lines: string[] = [];
  const entity = (value: string) => { lines.push('#' + (lines.length + 1) + '=' + value + ';'); return '#' + lines.length; };
  const point = (v: number[]) => entity('IFCCARTESIANPOINT((' + v.map(number).join(',') + '))');
  const direction = (v: number[]) => entity('IFCDIRECTION((' + v.map(number).join(',') + '))');
  const placement = (p: number[], z = [0, 0, 1], x = [1, 0, 0]) => entity(`IFCAXIS2PLACEMENT3D(${point(p)},${direction(z)},${direction(x)})`);
  const world = placement([0, 0, 0]);
  const context = entity(`IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,0.000001,${world},$)`);
  const unit = entity('IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)');
  const units = entity(`IFCUNITASSIGNMENT((${unit}))`);
  const guid = () => text(ifcGuid(crypto.randomUUID()));
  const project = entity(`IFCPROJECT(${guid()},$,'BIMaestro - Reservations',$,$,$,$,(${context}),${units})`);
  const buildingPlacement = entity(`IFCLOCALPLACEMENT($,${world})`);
  const building = entity(`IFCBUILDING(${guid()},$,'Reservations MEP',$,$,${buildingPlacement},$,$,.ELEMENT.,$,$,$)`);
  entity(`IFCRELAGGREGATES(${guid()},$,$,$,${project},(${building}))`);
  const products: string[] = [];
  for (const mark of exchange.reservations) {
    const frame = placement(mark.position.map(n => n * .3048), mark.normal.map(n => -n), mark.xAxis);
    const local = entity(`IFCLOCALPLACEMENT(${buildingPlacement},${frame})`);
    const profile = entity(`IFCRECTANGLEPROFILEDEF(.AREA.,$,$,${number(mark.widthCm / 100)},${number(mark.heightCm / 100)})`);
    const solid = entity(`IFCEXTRUDEDAREASOLID(${profile},${world},${direction([0, 0, 1])},${number(mark.depthCm / 100)})`);
    const shape = entity(`IFCSHAPEREPRESENTATION(${context},'Body','SweptSolid',(${solid}))`);
    const representation = entity(`IFCPRODUCTDEFINITIONSHAPE($,$,(${shape}))`);
    products.push(entity(`IFCBUILDINGELEMENTPROXY(${text(ifcGuid(mark.id))},$,${text('Reservation ' + mark.elementName)},${text(mark.text)},'Reservation MEP',${local},${representation},${text(mark.id)},.PROVISIONFORVOID.)`));
  }
  entity(`IFCRELCONTAINEDINSPATIALSTRUCTURE(${guid()},$,$,$,(${products.join(',')}),${building})`);
  return `ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\nFILE_NAME('reservations.ifc',${text(exchange.exportedAt)},('BIMaestro'),('BIMaestro'),'BIMaestro','BIMaestro','');\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n${lines.join('\n')}\nENDSEC;\nEND-ISO-10303-21;\n`;
}

export function downloadExchange(content: string, name: string) {
  const url = URL.createObjectURL(new Blob([content], { type: 'application/octet-stream' }));
  const link = document.createElement('a'); link.href = url; link.download = name; link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

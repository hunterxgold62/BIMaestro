import * as THREE from 'three';
import type { ViewerPackage } from './mep-contract';
import { markupQuaternion, reservationSize, reservationLot, type Markup } from './mep-markup';
import { lotDetails, type ReservationLots } from './mep-lots';

export function reservationExchange(marks: Markup[], manifest: ViewerPackage['manifest'], publicationId: string, lot?: string, lots: ReservationLots = {}) {
  if (!manifest.sourceOrigin || !manifest.sourceDocumentId) throw new Error('Republiez la maquette avec le nouveau plugin pour exporter dans le repère Revit.');
  const origin = manifest.sourceOrigin;
  const rootVector = (v: THREE.Vector3) => [v.x, -v.z, v.y] as [number, number, number];
  return { schemaVersion: 1, kind: 'bimaestro-reservations', units: 'revit-internal-feet', sourceDocumentId: manifest.sourceDocumentId,
    sharedCoordinates: manifest.sharedCoordinates,
    publicationId, exportedAt: new Date().toISOString(), reservations: marks.filter(m => m.kind === 'reservation' && !m.needsReview && (!lot || reservationLot(m) === lot)).map(mark => {
      const point = rootVector(new THREE.Vector3(...mark.position)).map((n, i) => n + origin[i]);
      const xAxis = rootVector(new THREE.Vector3(1, 0, 0).applyQuaternion(markupQuaternion(mark.normal)));
      return { id: mark.id, elementKey: mark.elementKey, stableKey: mark.stableKey, elementName: mark.elementName, text: mark.text,
        position: point, normal: rootVector(new THREE.Vector3(...mark.normal)), xAxis,
        shape: mark.shape || 'rectangle', diameterCm: mark.shape === 'round' ? reservationSize(mark)[0] : undefined, lot: lotDetails(reservationLot(mark), lots).name,
        color: lotDetails(reservationLot(mark), lots).color,
        widthCm: reservationSize(mark)[0], heightCm: reservationSize(mark)[1], depthCm: mark.depthCm };
    }) };
}

export function annotationExchange(marks: Markup[], manifest: ViewerPackage['manifest'], publicationId: string) {
  if (!manifest.sourceOrigin || !manifest.sourceDocumentId) throw new Error('Republiez la maquette avec le nouveau plugin pour exporter dans le repère Revit.');
  const origin = manifest.sourceOrigin;
  return {
    exportedAt: new Date().toISOString(), publicationId, sharedCoordinates: manifest.sharedCoordinates,
    annotations: marks.filter(mark => mark.kind === 'note' && !mark.needsReview).map(mark => ({
      id: mark.id, elementKey: mark.elementKey, stableKey: mark.stableKey, elementName: mark.elementName, text: mark.text,
      position: [mark.position[0] + origin[0], -mark.position[2] + origin[1], mark.position[1] + origin[2]] as [number, number, number],
    })),
  };
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
export function reservationsIfc(exchange: ReturnType<typeof reservationExchange>, coordinateBase: 'shared' | 'internal' = 'shared') {
  if (!exchange.reservations.length) throw new Error('Aucune réservation dont l’emplacement est confirmé à exporter.');
  const shared = coordinateBase === 'shared' ? exchange.sharedCoordinates : null;
  if (coordinateBase === 'shared' && !shared) throw new Error('Le repère partagé manque dans cette publication. Republiez la maquette depuis Revit avec le plugin corrigé pour exporter un IFC en coordonnées partagées.');
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
  // Apply the internal -> shared rigid transform once, at the site placement.
  // Product coordinates remain internal and in metres; the Revit JSON stays in feet.
  const siteFrame = shared ? placement(shared.origin.map(n => n * .3048), [0, 0, 1], shared.xAxis) : world;
  const sitePlacement = entity(`IFCLOCALPLACEMENT($,${siteFrame})`);
  const site = entity(`IFCSITE(${guid()},$,${text(shared?.siteName || 'Site Revit')},$,$,${sitePlacement},$,$,.ELEMENT.,$,$,$,$,$)`);
  const buildingPlacement = entity(`IFCLOCALPLACEMENT(${sitePlacement},${world})`);
  const building = entity(`IFCBUILDING(${guid()},$,'Reservations MEP',$,$,${buildingPlacement},$,$,.ELEMENT.,$,$,$)`);
  entity(`IFCRELAGGREGATES(${guid()},$,$,$,${project},(${site}))`);
  entity(`IFCRELAGGREGATES(${guid()},$,$,$,${site},(${building}))`);
  const products: string[] = [];
  for (const mark of exchange.reservations) {
    const frame = placement(mark.position.map(n => n * .3048), mark.normal.map(n => -n), mark.xAxis);
    const local = entity(`IFCLOCALPLACEMENT(${buildingPlacement},${frame})`);
    const profile = entity(mark.shape === 'round' ? `IFCCIRCLEPROFILEDEF(.AREA.,$,$,${number(mark.diameterCm! / 200)})` : `IFCRECTANGLEPROFILEDEF(.AREA.,$,$,${number(mark.widthCm / 100)},${number(mark.heightCm / 100)})`);
    const solid = entity(`IFCEXTRUDEDAREASOLID(${profile},${world},${direction([0, 0, 1])},${number(mark.depthCm / 100)})`);
    const hex = new THREE.Color(mark.color).getHex(THREE.SRGBColorSpace);
    const rgb = entity(`IFCCOLOURRGB(${text(mark.lot)},${number(((hex >> 16) & 255) / 255)},${number(((hex >> 8) & 255) / 255)},${number((hex & 255) / 255)})`);
    const shading = entity(`IFCSURFACESTYLESHADING(${rgb},0.)`);
    const style = entity(`IFCSURFACESTYLE(${text(mark.lot)},.BOTH.,(${shading}))`);
    entity(`IFCSTYLEDITEM(${solid},(${style}),$)`);
    const shape = entity(`IFCSHAPEREPRESENTATION(${context},'Body','SweptSolid',(${solid}))`);
    const representation = entity(`IFCPRODUCTDEFINITIONSHAPE($,$,(${shape}))`);
    const product = entity(`IFCBUILDINGELEMENTPROXY(${text(ifcGuid(mark.id))},$,${text('Reservation ' + mark.lot + ' - ' + mark.elementName)},${text(mark.text)},${text('Reservation ' + mark.lot)},${local},${representation},${text(mark.id)},.PROVISIONFORVOID.)`);
    products.push(product);
    const lot = entity(`IFCPROPERTYSINGLEVALUE('Lot',$,IFCLABEL(${text(mark.lot)}),$)`);
    const properties = entity(`IFCPROPERTYSET(${guid()},$,'BIMaestro_Reservation',$,(${lot}))`);
    entity(`IFCRELDEFINESBYPROPERTIES(${guid()},$,$,$,(${product}),${properties})`);
  }
  entity(`IFCRELCONTAINEDINSPATIALSTRUCTURE(${guid()},$,$,$,(${products.join(',')}),${building})`);
  return `ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\nFILE_NAME('reservations.ifc',${text(exchange.exportedAt)},('BIMaestro'),('BIMaestro'),'BIMaestro','BIMaestro','');\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n${lines.join('\n')}\nENDSEC;\nEND-ISO-10303-21;\n`;
}

export function annotationsIfc(exchange: ReturnType<typeof annotationExchange>) {
  if (!exchange.annotations.length) throw new Error('Aucune annotation confirmée à exporter.');
  const shared = exchange.sharedCoordinates;
  if (!shared) throw new Error('Le repère partagé manque dans cette publication. Republiez la maquette depuis Revit avec le plugin corrigé pour exporter un IFC en coordonnées partagées.');
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
  const project = entity(`IFCPROJECT(${guid()},$,'BIMaestro - Annotations',$,$,$,$,(${context}),${units})`);
  const siteFrame = placement(shared.origin.map(n => n * .3048), [0, 0, 1], shared.xAxis);
  const sitePlacement = entity(`IFCLOCALPLACEMENT($,${siteFrame})`);
  const site = entity(`IFCSITE(${guid()},$,${text(shared.siteName || 'Site Revit')},$,$,${sitePlacement},$,$,.ELEMENT.,$,$,$,$,$)`);
  const buildingPlacement = entity(`IFCLOCALPLACEMENT(${sitePlacement},${world})`);
  const building = entity(`IFCBUILDING(${guid()},$,'Annotations MEP',$,$,${buildingPlacement},$,$,.ELEMENT.,$,$,$)`);
  entity(`IFCRELAGGREGATES(${guid()},$,$,$,${project},(${site}))`);
  entity(`IFCRELAGGREGATES(${guid()},$,$,$,${site},(${building}))`);
  const products: string[] = [];
  // A solid marker is deliberately used here: most coordination viewers omit IFC text geometry.
  // Two perpendicular rings make the marker visible from any viewing angle.
  // Each ring has 16 short bars and eight triangular fins pointing to its centre.
  const extrusionDirection = direction([0, 0, 1]);
  const barProfile = entity('IFCRECTANGLEPROFILEDEF(.AREA.,$,$,0.091000000,0.045000000)');
  const colour = entity("IFCCOLOURRGB('Annotation',0.700000000,0.700000000,0.700000000)");
  const shading = entity(`IFCSURFACESTYLESHADING(${colour},0.)`);
  const style = entity(`IFCSURFACESTYLE('Annotation',.BOTH.,(${shading}))`);
  for (const mark of exchange.annotations) {
    const markerSolids: string[] = [];
    for (const plane of ['horizontal', 'vertical'] as const) {
      for (let i = 0; i < 16; i++) {
        const angle = (i + .5) * Math.PI / 8;
        const x = .225 * Math.cos(angle), y = .225 * Math.sin(angle);
        const tangent: [number, number, number] = plane === 'horizontal' ? [-Math.sin(angle), Math.cos(angle), 0] : [-Math.sin(angle), 0, Math.cos(angle)];
        const barFrame = plane === 'horizontal'
          ? placement([x, y, -.02], [0, 0, 1], tangent)
          : placement([x, .02, y], [0, -1, 0], tangent);
        markerSolids.push(entity(`IFCEXTRUDEDAREASOLID(${barProfile},${barFrame},${extrusionDirection},0.040000000)`));
      }
      for (let i = 0; i < 8; i++) {
        const angle = i * Math.PI / 4;
        const vertices = [
          [.045 * Math.cos(angle), .045 * Math.sin(angle)],
          [.19 * Math.cos(angle - .24), .19 * Math.sin(angle - .24)],
          [.19 * Math.cos(angle + .24), .19 * Math.sin(angle + .24)],
        ];
        const outline = entity(`IFCPOLYLINE((${[...vertices, vertices[0]].map(v => point(v)).join(',')}))`);
        const profile = entity(`IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,${outline})`);
        const finFrame = plane === 'horizontal' ? placement([0, 0, -.025]) : placement([0, .025, 0], [0, -1, 0]);
        markerSolids.push(entity(`IFCEXTRUDEDAREASOLID(${profile},${finFrame},${extrusionDirection},0.050000000)`));
      }
    }
    for (const solid of markerSolids) entity(`IFCSTYLEDITEM(${solid},(${style}),$)`);
    const frame = placement(mark.position.map(n => n * .3048));
    const local = entity(`IFCLOCALPLACEMENT(${buildingPlacement},${frame})`);
    const shape = entity(`IFCSHAPEREPRESENTATION(${context},'Body','SweptSolid',(${markerSolids.join(',')}))`);
    const representation = entity(`IFCPRODUCTDEFINITIONSHAPE($,$,(${shape}))`);
    const product = entity(`IFCBUILDINGELEMENTPROXY(${text(ifcGuid(mark.id))},$,${text('Annotation - ' + mark.elementName)},${text(mark.text)},'BIMaestro Annotation',${local},${representation},${text(mark.id)},.USERDEFINED.)`);
    products.push(product);
    const properties = [
      entity(`IFCPROPERTYSINGLEVALUE('Texte',$,IFCTEXT(${text(mark.text)}),$)`),
      entity(`IFCPROPERTYSINGLEVALUE('Element',$,IFCLABEL(${text(mark.elementName)}),$)`),
      entity(`IFCPROPERTYSINGLEVALUE('ElementKey',$,IFCIDENTIFIER(${text(mark.elementKey)}),$)`),
      ...(mark.stableKey ? [entity(`IFCPROPERTYSINGLEVALUE('StableKey',$,IFCIDENTIFIER(${text(mark.stableKey)}),$)`)] : []),
      entity(`IFCPROPERTYSINGLEVALUE('PublicationId',$,IFCIDENTIFIER(${text(exchange.publicationId)}),$)`),
    ];
    const propertySet = entity(`IFCPROPERTYSET(${guid()},$,'BIMaestro_Annotation',$,(${properties.join(',')}))`);
    entity(`IFCRELDEFINESBYPROPERTIES(${guid()},$,$,$,(${product}),${propertySet})`);
  }
  entity(`IFCRELCONTAINEDINSPATIALSTRUCTURE(${guid()},$,$,$,(${products.join(',')}),${building})`);
  return `ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\nFILE_NAME('annotations.ifc',${text(exchange.exportedAt)},('BIMaestro'),('BIMaestro'),'BIMaestro','BIMaestro','');\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n${lines.join('\n')}\nENDSEC;\nEND-ISO-10303-21;\n`;
}

export function downloadExchange(content: string, name: string) {
  const url = URL.createObjectURL(new Blob([content], { type: 'application/octet-stream' }));
  const link = document.createElement('a'); link.href = url; link.download = name;
  link.target = '_blank'; link.rel = 'noopener'; link.hidden = true;
  document.body.appendChild(link);
  try { link.click(); } finally { link.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 60000); }
}

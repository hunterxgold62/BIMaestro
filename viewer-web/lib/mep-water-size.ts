import type { MepConnector, MepPath, WebProperty } from './mep-contract';

function connectorRadius(connector: MepConnector | undefined) {
  const area = connector?.crossSectionArea;
  return typeof area === 'number' && Number.isFinite(area) && area > 0 ? Math.sqrt(area / Math.PI) : null;
}

function propertyRadius(properties: WebProperty['properties'] | undefined) {
  for (const [key, value] of Object.entries(properties || {})) {
    const label = key.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().trim();
    if (!/^(dn|diametre(?: nominal| interieur)?|(?:nominal |inside )?diameter)$/.test(label)) continue;
    const match = value.replace(/[\s\u00a0\u202f]+/g, ' ').trim().match(/^(?:dn\s*|[øØ]\s*)?(\d+(?:[.,]\d+)?)\s*(mm|cm|m|ft|in|"|')?$/i);
    if (!match) continue;
    const number = Number(match[1].replace(',', '.'));
    const unit = match[2]?.toLowerCase() || (label === 'dn' ? 'mm' : '');
    const feet = unit === 'mm' ? number / 304.8 : unit === 'cm' ? number / 30.48 : unit === 'm' ? number / .3048 : unit === 'in' || unit === '"' ? number / 12 : unit === 'ft' || unit === "'" ? number : 0;
    if (feet > 0 && Number.isFinite(feet)) return feet / 2;
  }
  return null;
}

/** Connector sections are exported in Revit internal square feet. No bbox or zoom inference. */
export function waterPathRadii(path: Pick<MepPath, 'startConnector' | 'endConnector'>, connectors: Map<number, MepConnector>, property?: WebProperty) {
  const start = connectorRadius(connectors.get(path.startConnector));
  const end = connectorRadius(connectors.get(path.endConnector));
  const fallback = start ?? end ?? propertyRadius(property?.properties);
  return fallback === null ? null : { start: start ?? fallback, end: end ?? fallback };
}

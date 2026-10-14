import type { MepElement } from './mep-contract';

/** Only named equipment, never pipe curves or ordinary junction fittings. */
export function equipmentStopKind(element: MepElement | undefined): string | null {
  if (!element || element.isPipeCurve || element.isPipeFitting) return null;
  const text = `${element.name} ${element.typeName}`.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, ' ');
  if (/\b(pompe|pump|circulateur)\b/.test(text)) return 'Pompe';
  if (/\b(filtre|filter)\b/.test(text)) return 'Filtre';
  if (/\b(echangeur|exchanger)\b/.test(text)) return 'Échangeur';
  return null;
}

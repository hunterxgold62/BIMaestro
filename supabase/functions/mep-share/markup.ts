export function validateMarkup(value: unknown) {
  const mark = value as Record<string, any> | null;
  const vector = (v: unknown) => Array.isArray(v) && v.length === 3 && v.every(n => typeof n === 'number' && Number.isFinite(n) && Math.abs(n) <= 1e8);
  if (!mark || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(mark.id) ||
    !['note', 'reservation'].includes(mark.kind) || typeof mark.elementKey !== 'string' || !mark.elementKey || mark.elementKey.length > 1000 ||
    typeof mark.elementName !== 'string' || mark.elementName.length > 500 || typeof mark.text !== 'string' || mark.text.length > 2000 ||
    (mark.kind === 'note' && !mark.text.trim()) || !vector(mark.position) || !vector(mark.normal) ||
    Math.abs(Math.hypot(...mark.normal) - 1) > .01 || !Number.isSafeInteger(mark.modelRevision) || mark.modelRevision < 1 ||
    !['widthCm', 'heightCm', 'depthCm'].every(key => typeof mark[key] === 'number' && Number.isFinite(mark[key]) && mark[key] >= 1 && mark[key] <= 1000)) {
    throw new Error('Annotation ou dimensions invalides');
  }
  if ((mark.stableKey !== undefined && (typeof mark.stableKey !== 'string' || mark.stableKey.length > 1000)) ||
    (mark.anchorCenter !== undefined && !vector(mark.anchorCenter)) ||
    (mark.anchorSize !== undefined && (!vector(mark.anchorSize) || mark.anchorSize.some((n: number) => n < 0))))
    throw new Error('Rattachement invalide');
  if (mark.shape !== undefined && !['rectangle', 'round'].includes(mark.shape)) throw new Error('Forme invalide');
  if (mark.shape === 'round' && (typeof mark.diameterCm !== 'number' || !Number.isFinite(mark.diameterCm) || mark.diameterCm < 1 || mark.diameterCm > 1000)) throw new Error('Diamètre invalide');
  if (mark.lot !== undefined && (typeof mark.lot !== 'string' || !mark.lot.trim() || mark.lot.length > 40 || /[\x00-\x1f]/.test(mark.lot))) throw new Error('Lot invalide');
  if (mark.dimensions !== undefined && (!Array.isArray(mark.dimensions) || mark.dimensions.length > 3 || mark.dimensions.some((d: any) => !d || typeof d.id !== 'string' || d.id.length > 100 || !d.id || typeof d.elementKey !== 'string' || d.elementKey.length > 1000 || !d.elementKey || typeof d.elementName !== 'string' || d.elementName.length > 500 || !vector(d.point) || !vector(d.normal) || Math.abs(Math.hypot(...d.normal) - 1) > .001) || new Set(mark.dimensions.map((d: any) => d.id)).size !== mark.dimensions.length)) throw new Error('Cotation invalide');
  return { id: mark.id, kind: mark.kind, elementKey: mark.elementKey, elementName: mark.elementName,
    shape: mark.shape || 'rectangle', diameterCm: mark.shape === 'round' ? mark.diameterCm : undefined, lot: mark.lot?.trim() || 'MEP',
    dimensions: (mark.dimensions || []).map((d: any) => ({ id: d.id, elementKey: d.elementKey, elementName: d.elementName, point: d.point, normal: d.normal })),
    stableKey: mark.stableKey, anchorCenter: mark.anchorCenter, anchorSize: mark.anchorSize,
    position: mark.position, normal: mark.normal, text: mark.text.trim(), widthCm: mark.shape === 'round' ? mark.diameterCm : mark.widthCm,
    heightCm: mark.shape === 'round' ? mark.diameterCm : mark.heightCm, depthCm: mark.depthCm, modelRevision: mark.modelRevision };
}

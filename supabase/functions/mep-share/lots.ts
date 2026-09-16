export function updateReservationLot(state: Record<string, any>, id: unknown, value: unknown) {
  const lot = value as Record<string, unknown> | null;
  if (typeof id !== 'string' || !id || id.length > 40 || ['__proto__', 'prototype', 'constructor'].includes(id) || /[\x00-\x1f]/.test(id) ||
    !lot || typeof lot.name !== 'string' || !lot.name.trim() || lot.name.length > 40 || /[\x00-\x1f]/.test(lot.name) || typeof lot.color !== 'string' || !/^#[0-9a-f]{6}$/i.test(lot.color)) throw new Error('Nom ou couleur du lot invalide.');
  const settings = state.reservationLots || {};
  const ids = new Set<string>(['MEP', 'GC', 'ELEC', ...Object.keys(settings), ...Object.values(state.markups || {}).filter((m: any) => m.kind === 'reservation').map((m: any) => m.lot?.trim() || 'MEP')]);
  if (!ids.has(id) && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)) throw new Error('Identifiant de lot invalide.');
  const name = lot.name.trim();
  for (const existing of ids) {
    const other = Object.hasOwn(settings, existing) ? settings[existing].name : existing;
    if (existing !== id && other.toLocaleLowerCase('fr') === name.toLocaleLowerCase('fr')) throw new Error('Un lot porte déjà ce nom.');
  }
  if (!Object.hasOwn(settings, id) && Object.keys(settings).length >= 100) throw new Error('Limite de 100 lots personnalisés atteinte.');
  // IDs remain stable: a rename cannot orphan an existing reservation or an open draft.
  return { ...settings, [id]: { name, color: lot.color.toLowerCase() } };
}

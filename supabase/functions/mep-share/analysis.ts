export function validateAnalysis(value: unknown): { allowImplicitTerminals?: boolean; endpoints?: Record<string, number> } {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Hypothèses invalides');
  const input = value as Record<string, unknown>;
  if (Object.keys(input).some(key => key !== 'allowImplicitTerminals' && key !== 'endpoints')) throw new Error('Hypothèse inconnue');
  const result: { allowImplicitTerminals?: boolean; endpoints?: Record<string, number> } = {};
  if ('allowImplicitTerminals' in input) {
    if (typeof input.allowImplicitTerminals !== 'boolean') throw new Error('Mode des extrémités invalide');
    result.allowImplicitTerminals = input.allowImplicitTerminals;
  }
  if ('endpoints' in input) {
    if (!input.endpoints || typeof input.endpoints !== 'object' || Array.isArray(input.endpoints)) throw new Error('Extrémités invalides');
    const pairs = Object.entries(input.endpoints);
    if (pairs.length > 1000) throw new Error('Trop d’extrémités');
    if (pairs.some(([key, role]) => !key || key.length > 500 || /[\u0000-\u001f]/.test(key) || ['__proto__','prototype','constructor'].includes(key) || typeof role !== 'number' || !Number.isInteger(role) || role < 0 || role > 4)) throw new Error('Rôle ou identifiant d’extrémité invalide');
    result.endpoints = Object.fromEntries(pairs) as Record<string, number>;
  }
  if (!Object.keys(result).length) throw new Error('Hypothèses vides');
  return result;
}

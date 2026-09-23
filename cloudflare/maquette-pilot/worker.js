const shareApi = 'https://xqovxfgghbqxwsadzhzl.functions.supabase.co/mep-share';
const supabaseKey = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30.ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo';
const pilotPublication = '5b1d1220-2b0c-4194-8b4e-ea18db63f73d';
const allowedOrigins = new Set(['https://viewer.bimaestro.fr', 'http://localhost:3000', 'http://localhost:3001']);

function headers(origin) {
  return {
    'Access-Control-Allow-Origin': allowedOrigins.has(origin) ? origin : 'https://viewer.bimaestro.fr',
    'Access-Control-Allow-Headers': 'content-type',
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Access-Control-Max-Age': '600',
    'Cache-Control': 'no-store',
    'Vary': 'Origin',
    'X-Content-Type-Options': 'nosniff',
  };
}

function failure(origin, status, message) {
  return new Response(message, { status, headers: headers(origin) });
}

export default {
  async fetch(request, env) {
    const origin = request.headers.get('Origin') || '';
    const path = new URL(request.url).pathname;
    if (request.method === 'OPTIONS' && path === '/asset') return new Response(null, { status: 204, headers: headers(origin) });
    if (request.method !== 'POST' || path !== '/asset') return failure(origin, 404, 'Introuvable');
    if (origin && !allowedOrigins.has(origin)) return failure(origin, 403, 'Origine refusée');
    let input;
    try { input = await request.json(); } catch { return failure(origin, 400, 'Requête invalide'); }
    const { token, revision, name } = input || {};
    if (typeof token !== 'string' || !/^[A-Za-z0-9_-]{32,160}$/.test(token) ||
        !Number.isSafeInteger(revision) || revision < 1 ||
        typeof name !== 'string' || !/^[A-Za-z0-9._-]{1,120}$/.test(name)) {
      return failure(origin, 400, 'Fichier invalide');
    }
    let access;
    try {
      const response = await fetch(shareApi, {
        method: 'POST',
        headers: { 'content-type': 'application/json', apikey: supabaseKey },
        body: JSON.stringify({ action: 'resolve', token }),
      });
      if (!response.ok) return failure(origin, response.status === 429 ? 429 : 403, 'Lien indisponible');
      access = await response.json();
    } catch { return failure(origin, 503, 'Vérification indisponible'); }
    if (access.publication?.id !== pilotPublication || access.publication.revision !== revision) {
      return failure(origin, 403, 'Publication non autorisée');
    }
    const asset = access.manifest?.assets?.find(item => item.name === name);
    if (!asset || !Number.isSafeInteger(asset.bytes) || asset.bytes <= 0) return failure(origin, 404, 'Fichier inconnu');
    const object = await env.MODELS.get(`${pilotPublication}/${revision}/${name}`);
    if (!object || object.size !== asset.bytes) return failure(origin, 503, 'Copie R2 absente ou incomplète');
    return new Response(object.body, {
      headers: { ...headers(origin), 'Content-Type': 'application/octet-stream', 'Content-Length': String(object.size) },
    });
  },
};

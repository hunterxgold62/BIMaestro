const shareApi = 'https://xqovxfgghbqxwsadzhzl.functions.supabase.co/mep-share';
const supabaseKey = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inhxb3Z4ZmdnaGJxeHdzYWR6aHpsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTI0MDY5MzMsImV4cCI6MjA2Nzk4MjkzM30.ocKoeuUTLQ_oOr83TtpaJD3RUDOBbwLQ5nJNvOinYlo';
const allowedOrigins = new Set(['https://viewer.bimaestro.fr', 'http://localhost:3000', 'http://localhost:3001']);

function headers(origin) {
  return {
    'Access-Control-Allow-Origin': allowedOrigins.has(origin) ? origin : 'https://viewer.bimaestro.fr',
    'Access-Control-Allow-Headers': 'content-type, authorization',
    'Access-Control-Allow-Methods': 'POST, PUT, OPTIONS',
    'Access-Control-Max-Age': '600',
    'Cache-Control': 'no-store',
    'Vary': 'Origin',
    'X-Content-Type-Options': 'nosniff',
  };
}

function failure(origin, status, message) {
  return new Response(message, { status, headers: headers(origin) });
}

async function authorize(body, bearer) {
  const response = await fetch(shareApi, {
    method: 'POST',
    headers: { 'content-type': 'application/json', apikey: supabaseKey, ...(bearer ? { authorization: bearer } : {}) },
    body: JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`Supabase ${response.status}`);
  return response.json();
}

async function sha256(bytes) {
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('');
}

function validTarget(id, revision, name) {
  return /^[0-9a-f-]{36}$/.test(id || '') && Number.isSafeInteger(revision) && revision > 0 &&
    /^(index\.zip|tile-\d{5}\.glb\.gz)$/.test(name || '');
}

export default {
  async fetch(request, env) {
    const origin = request.headers.get('Origin') || '';
    const path = new URL(request.url).pathname;
    if (request.method === 'OPTIONS' && ['/asset', '/upload', '/verify', '/delete'].includes(path)) return new Response(null, { status: 204, headers: headers(origin) });
    if (origin && !allowedOrigins.has(origin)) return failure(origin, 403, 'Origine refusée');
    if (request.method === 'PUT' && path === '/upload') {
      const url = new URL(request.url);
      const publicationId = url.searchParams.get('publicationId');
      const revision = Number(url.searchParams.get('revision'));
      const name = url.searchParams.get('name');
      if (!validTarget(publicationId, revision, name)) return failure(origin, 400, 'Fichier invalide');
      const bearer = request.headers.get('authorization');
      if (!bearer?.startsWith('Bearer ')) return failure(origin, 401, 'Licence requise');
      let access;
      try { access = await authorize({ action: 'r2-upload-authorize', publicationId, revision, name }, bearer); }
      catch { return failure(origin, 403, 'Publication non autorisée'); }
      const asset = access.asset;
      if (access.publicationId !== publicationId || access.revision !== revision || asset?.name !== name ||
          !Number.isSafeInteger(asset.bytes) || asset.bytes < 1 || asset.bytes > 48 * 1024 * 1024 ||
          !/^[0-9a-f]{64}$/.test(asset.sha256 || '')) return failure(origin, 403, 'Manifeste invalide');
      if (Number(request.headers.get('content-length')) > asset.bytes) return failure(origin, 413, 'Fichier trop volumineux');
      const bytes = await request.arrayBuffer();
      if (bytes.byteLength !== asset.bytes || await sha256(bytes) !== asset.sha256) return failure(origin, 409, 'Intégrité incorrecte');
      const key = `${publicationId}/${revision}/${name}`;
      const stored = await env.MODELS.put(key, bytes, { customMetadata: { sha256: asset.sha256 } });
      if (!stored || stored.size !== asset.bytes) return failure(origin, 503, 'Envoi R2 incomplet');
      return new Response(null, { status: 201, headers: headers(origin) });
    }
    if (request.method === 'POST' && path === '/verify') {
      let input;
      try { input = await request.json(); } catch { return failure(origin, 400, 'Requête invalide'); }
      const { publicationId, revision, names } = input || {};
      if (!/^[0-9a-f-]{36}$/.test(publicationId || '') || !Number.isSafeInteger(revision) || revision < 1 ||
          !Array.isArray(names) || names.length < 1 || names.length > 32 || names.some(name => !/^(index\.zip|tile-\d{5}\.glb\.gz)$/.test(name))) return failure(origin, 400, 'Lot invalide');
      const bearer = request.headers.get('authorization');
      if (!bearer?.startsWith('Bearer ')) return failure(origin, 401, 'Licence requise');
      let access;
      try { access = await authorize({ action: 'r2-verify-authorize', publicationId, revision, names }, bearer); }
      catch { return failure(origin, 403, 'Publication non autorisée'); }
      if (access.publicationId !== publicationId || access.revision !== revision || access.assets?.length !== names.length) return failure(origin, 403, 'Manifeste invalide');
      const objects = await Promise.all(access.assets.map(asset => env.MODELS.head(`${publicationId}/${revision}/${asset.name}`)));
      const valid = objects.filter((object, index) => object?.size === access.assets[index].bytes && object.customMetadata?.sha256 === access.assets[index].sha256);
      return new Response(JSON.stringify({ expected: names.length, valid: valid.length, bytes: valid.reduce((sum, item) => sum + item.size, 0) }),
        { headers: { ...headers(origin), 'content-type': 'application/json' } });
    }
    if (request.method === 'POST' && path === '/delete') {
      let input;
      try { input = await request.json(); } catch { return failure(origin, 400, 'Requête invalide'); }
      const { publicationId, revision, names } = input || {};
      if (!/^[0-9a-f-]{36}$/.test(publicationId || '') || !Number.isSafeInteger(revision) || revision < 1 ||
          !Array.isArray(names) || names.length < 1 || names.length > 100 || names.some(name => !/^(index\.zip|tile-\d{5}\.glb\.gz)$/.test(name))) return failure(origin, 400, 'Lot invalide');
      const bearer = request.headers.get('authorization');
      if (!bearer?.startsWith('Bearer ')) return failure(origin, 401, 'Accès refusé');
      let access;
      try { access = await authorize({ action: 'r2-delete-authorize', publicationId, revision, names }, bearer); }
      catch { return failure(origin, 403, 'Suppression non autorisée'); }
      if (access.publicationId !== publicationId || access.revision !== revision || access.names?.length !== names.length ||
          names.some(name => !access.names.includes(name))) return failure(origin, 403, 'Manifeste invalide');
      await env.MODELS.delete(names.map(name => `${publicationId}/${revision}/${name}`));
      return new Response(null, { status: 204, headers: headers(origin) });
    }
    if (request.method !== 'POST' || path !== '/asset') return failure(origin, 404, 'Introuvable');
    let input;
    try { input = await request.json(); } catch { return failure(origin, 400, 'Requête invalide'); }
    const { token, revision, name } = input || {};
    if (typeof token !== 'string' || !/^[A-Za-z0-9_-]{32,160}$/.test(token) ||
        !Number.isSafeInteger(revision) || revision < 1 ||
        typeof name !== 'string' || !/^[A-Za-z0-9._-]{1,120}$/.test(name)) {
      return failure(origin, 400, 'Fichier invalide');
    }
    let access;
    try { access = await authorize({ action: 'r2-asset', token, revision, name }); }
    catch { return failure(origin, 403, 'Lien indisponible'); }
    if (!/^[0-9a-f-]{36}$/.test(access.publicationId || '') || access.revision !== revision) {
      return failure(origin, 403, 'Publication non autorisée');
    }
    const asset = access.asset;
    if (asset?.name !== name || !Number.isSafeInteger(asset.bytes) || asset.bytes <= 0 || !/^[0-9a-f]{64}$/.test(asset.sha256 || '')) return failure(origin, 404, 'Fichier inconnu');
    const object = await env.MODELS.get(`${access.publicationId}/${revision}/${name}`);
    if (!object || object.size !== asset.bytes) return failure(origin, 503, 'Copie R2 absente ou incomplète');
    return new Response(object.body, {
      headers: { ...headers(origin), 'Content-Type': 'application/octet-stream', 'Content-Length': String(object.size) },
    });
  },
};

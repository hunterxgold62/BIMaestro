import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import worker from './worker.js';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import library from '../family-library/worker.js';
import { requestCost, modelRequestCost, R2_OBJECT_OVERHEAD } from '../family-library/quota.js';

function quotaEnv(models) {
  const sql = new DatabaseSync(':memory:');
  sql.exec(readFileSync(new URL('../family-library/schema.sql', import.meta.url), 'utf8'));
  sql.exec("UPDATE quota_policy SET enabled=1,scope='account',verified_until='2099-01-01T00:00:00.000Z'");
  return { QUOTA_ENABLED: 'true', sql, MODELS: models, CATALOG: { prepare(text) { return { bind(...args) { return {
    async first() { return sql.prepare(text).get(...args) || null; },
    async all() { return { results: sql.prepare(text).all(...args) }; },
    async run() { return sql.prepare(text).run(...args); },
  }; } }; } } };
}

const token = 'a'.repeat(43);
const id = '5b1d1220-2b0c-4194-8b4e-ea18db63f73d';
const asset = { name: 'index.zip', bytes: 4, sha256: 'a'.repeat(64) };
const request = (body, origin = 'https://viewer.bimaestro.fr') => new Request('https://pilot.example/asset', {
  method: 'POST', headers: { origin, 'content-type': 'application/json' }, body: JSON.stringify(body),
});

test('valid pilot link reads only the declared R2 asset', async () => {
  const original = globalThis.fetch;
  const keys = [];
  globalThis.fetch = async (_url, options) => {
    assert.deepEqual(JSON.parse(options.body), { action: 'r2-asset', token, revision: 2, name: 'index.zip' });
    return Response.json({ publicationId: id, revision: 2, asset });
  };
  try {
    const response = await worker.fetch(request({ token, revision: 2, name: 'index.zip' }), quotaEnv({
      get: async key => { keys.push(key); return { size: 4, body: new Uint8Array([1, 2, 3, 4]) }; },
    }));
    assert.equal(response.status, 200);
    assert.deepEqual(keys, [`${id}/2/index.zip`]);
    assert.deepEqual([...new Uint8Array(await response.arrayBuffer())], [1, 2, 3, 4]);
  } finally { globalThis.fetch = original; }
});

test('invalid, undeclared and incomplete files cannot be served', async () => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => Response.json({ publicationId: id, revision: 2, asset });
  const env = quotaEnv({ get: async () => ({ size: 3, body: new Uint8Array([1, 2, 3]) }) });
  try {
    assert.equal((await worker.fetch(request({ token: 'bad', revision: 2, name: 'index.zip' }), env)).status, 400);
    assert.equal((await worker.fetch(request({ token, revision: 2, name: '../secret' }), env)).status, 400);
    assert.equal((await worker.fetch(request({ token, revision: 2, name: 'other.zip' }), env)).status, 404);
    assert.equal((await worker.fetch(request({ token, revision: 2, name: 'index.zip' }), env)).status, 503);
    assert.equal((await worker.fetch(request({ token, revision: 2, name: 'index.zip' }, 'https://other.example'), env)).status, 403);
  } finally { globalThis.fetch = original; }
});

test('licensed upload verifies SHA-256 before storing a revision', async () => {
  const original = globalThis.fetch;
  const bytes = new Uint8Array([1, 2, 3, 4]);
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  const writes = [];
  globalThis.fetch = async (_url, options) => {
    assert.equal(options.headers.authorization, 'Bearer license');
    assert.equal(JSON.parse(options.body).action, 'r2-upload-authorize');
    return Response.json({ publicationId: id, publicationName: 'École Antibes', revision: 3, asset: { name: 'index.zip', bytes: 4, sha256 } });
  };
  try {
    const url = `https://pilot.example/upload?publicationId=${id}&revision=3&name=index.zip`;
    const env = quotaEnv({ put: async (key, body, options) => {
      writes.push({ key, body, sha256: options?.customMetadata?.sha256 });
      return { size: body.byteLength };
    } });
    const headers = { authorization: 'Bearer license' };
    assert.equal((await worker.fetch(new Request(url, { method: 'PUT', headers, body: bytes }), env)).status, 201);
    assert.equal(writes[0].key, `${id}/3/index.zip`);
    assert.deepEqual([...new Uint8Array(writes[0].body)], [1, 2, 3, 4]);
    assert.equal(writes[0].sha256, sha256);
    assert.equal(writes[1].key, `${id}/3/00 - Ecole Antibes.txt`);
    assert.match(writes[1].body, /Maquette : École Antibes/);
    const counters = env.sql.prepare('SELECT * FROM quota_state').get();
    assert.equal(counters.r2_a, 2);
    assert.equal(counters.r2_bytes, 4 + new TextEncoder().encode(writes[1].body).byteLength + 2*R2_OBJECT_OVERHEAD);
    assert.equal((await worker.fetch(new Request(url, { method: 'PUT', headers, body: new Uint8Array([9, 2, 3, 4]) }), env)).status, 409);
    assert.equal(writes.length, 2);
    assert.equal(env.sql.prepare('SELECT r2_a FROM quota_state').get().r2_a, 4, 'failed upload is still reserved');
  } finally { globalThis.fetch = original; }
});

test('library and models consume the same last R2 read, atomically', async () => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => Response.json({ publicationId: id, revision: 2, asset });
  let gets = 0;
  const env = quotaEnv({ get: async () => { gets++; return { size: 4, body: new Uint8Array(4) }; } });
  env.LIBRARY_ENABLED = 'true';
  env.FAMILIES = { get: async () => { gets++; return { size: 4, body: new Uint8Array(4) }; } };
  const familyId = 'c'.repeat(64);
  env.sql.prepare("INSERT INTO shared_families(id,name,category,revit_version,description,size_bytes,created_at,status) VALUES(?,'Test','generic',2025,'',4,'2026-01-01','ready')").run(familyId);
  env.sql.exec('UPDATE quota_state SET r2_b=7999999');
  try {
    const results = await Promise.all([
      library.fetch(new Request(`https://library/v1/families/${familyId}/file`), env),
      worker.fetch(request({ token, revision: 2, name: 'index.zip' }), env),
    ]);
    assert.deepEqual(results.map(r => r.status).sort(), [200, 429]);
    assert.equal(gets, 1);
    assert.equal(env.sql.prepare('SELECT r2_b FROM quota_state').get().r2_b, 8000000);
  } finally { globalThis.fetch = original; }
});

test('every model R2 route fails closed at quota, without touching storage', async () => {
  const original = globalThis.fetch;
  const bytes = new Uint8Array([1,2,3,4]);
  const declared = { ...asset, sha256: createHash('sha256').update(bytes).digest('hex') };
  globalThis.fetch = async () => Response.json({ publicationId: id, revision: 2, asset: declared, assets: [declared], names: ['index.zip'] });
  const cases = [
    () => request({ token, revision: 2, name: 'index.zip' }),
    () => new Request(`https://pilot/upload?publicationId=${id}&revision=2&name=index.zip`, { method:'PUT', headers:{authorization:'Bearer license'}, body:bytes }),
    ...['verify','delete'].map(route => () => new Request(`https://pilot/${route}`, { method:'POST', headers:{authorization:'Bearer license','content-type':'application/json'}, body:JSON.stringify({ publicationId:id,revision:2,names:['index.zip'] }) })),
  ];
  try {
    for (const makeRequest of cases) {
      const env = quotaEnv(new Proxy({}, { get() { return () => assert.fail('R2 must not be accessed'); } }));
      env.sql.exec('UPDATE quota_state SET r2_a=800000,r2_b=8000000,r2_bytes=8000000000');
      assert.equal((await worker.fetch(makeRequest(), env)).status, 429);
    }
  } finally { globalThis.fetch = original; }
});

test('models refuse unverified baseline, expiry, administrative pause and unavailable D1', async () => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => Response.json({ publicationId: id, revision: 2, asset });
  try {
    for (const setup of [
      env => env.sql.exec("UPDATE quota_policy SET scope='unverified'"),
      env => env.sql.exec("UPDATE quota_policy SET verified_until='2000-01-01'"),
      env => env.sql.exec('UPDATE quota_policy SET enabled=0'),
      env => { delete env.CATALOG; },
      env => { env.QUOTA_ENABLED='false'; },
    ]) {
      const env = quotaEnv({ get: async () => assert.fail('R2 must not be accessed') });
      setup(env);
      assert.ok([429,503].includes((await worker.fetch(request({token,revision:2,name:'index.zip'}),env)).status));
    }
  } finally { globalThis.fetch = original; }
});

test('model verification reserves every HEAD and deletion reserves its LIST', async () => {
  const original=globalThis.fetch;
  const assets = [{...asset}, {...asset,name:'tile-00001.glb.gz'}];
  globalThis.fetch = async () => Response.json({ publicationId:id,revision:2,assets,names:assets.map(a=>a.name) });
  let heads=0;
  const env=quotaEnv({head:async()=>{heads++;return {size:4,customMetadata:{sha256:asset.sha256}};},delete:async()=>{},list:async()=>({objects:[]})});
  const make=route=>new Request(`https://pilot/${route}`,{method:'POST',headers:{authorization:'Bearer license','content-type':'application/json'},body:JSON.stringify({publicationId:id,revision:2,names:assets.map(a=>a.name)})});
  try {
    assert.equal((await worker.fetch(make('verify'),env)).status,200);
    assert.equal(heads,2);
    assert.equal(env.sql.prepare('SELECT r2_b FROM quota_state').get().r2_b,2);
    assert.equal((await worker.fetch(make('delete'),env)).status,204);
    assert.equal(env.sql.prepare('SELECT r2_a FROM quota_state').get().r2_a,1);
  } finally {globalThis.fetch=original;}
});

test('lost R2 response keeps its reservation and malformed uploads cannot write R2', async () => {
  const original=globalThis.fetch;
  const bytes=new Uint8Array([1,2,3,4]);
  globalThis.fetch=async()=>Response.json({publicationId:id,revision:2,asset:{...asset,sha256:createHash('sha256').update(bytes).digest('hex')}});
  const env=quotaEnv({put:async()=>{throw new Error('Lost response');}});
  const make=body=>new Request(`https://pilot/upload?publicationId=${id}&revision=2&name=index.zip`,{method:'PUT',headers:{authorization:'Bearer license'},body});
  try {
    assert.equal((await worker.fetch(make(bytes),env)).status,503);
    assert.equal(env.sql.prepare('SELECT r2_bytes FROM quota_state').get().r2_bytes,4+R2_OBJECT_OVERHEAD);
    env.MODELS.put=async()=>assert.fail('Malformed body must not be written');
    assert.equal((await worker.fetch(make(new Uint8Array(5)),env)).status,413);
    assert.equal(env.sql.prepare('SELECT r2_bytes FROM quota_state').get().r2_bytes,2*(4+R2_OBJECT_OVERHEAD));
  } finally {globalThis.fetch=original;}
});

test('library costs distinguish read-only requests without weakening upload reservations', () => {
  assert.equal(requestCost('list').d1_writes,1);
  assert.equal(requestCost('previewDownload').d1_writes,1);
  assert.equal(requestCost('download').d1_writes,2);
  assert.equal(requestCost('upload').d1_writes,16);
  assert.throws(()=>requestCost('unknown'));
  assert.throws(()=>modelRequestCost('verify',{count:1.5}));
  assert.throws(()=>modelRequestCost('upload',{bytes:-1}));
  assert.throws(()=>modelRequestCost('unknown'));
});

test('completion only accepts matching R2 metadata', async () => {
  const original = globalThis.fetch;
  const sha256 = 'b'.repeat(64);
  globalThis.fetch = async () => Response.json({ publicationId: id, revision: 3, assets: [{ name: 'index.zip', bytes: 4, sha256 }] });
  try {
    const request = new Request('https://pilot.example/verify', { method: 'POST',
      headers: { authorization: 'Bearer license', 'content-type': 'application/json' },
      body: JSON.stringify({ publicationId: id, revision: 3, names: ['index.zip'] }) });
    const env = quotaEnv({ head: async () => ({ size: 4, customMetadata: { sha256 } }) });
    const result = await worker.fetch(request, env);
    assert.deepEqual(await result.json(), { expected: 1, valid: 1, bytes: 4 });
  } finally { globalThis.fetch = original; }
});

test('deletion requires server authorization for an inactive revision', async () => {
  const original = globalThis.fetch;
  let deleted = 0;
  const body = JSON.stringify({ publicationId: id, revision: 1, names: ['index.zip'] });
  const env = quotaEnv({
    delete: async keys => { if (deleted === 0) assert.deepEqual(keys, [`${id}/1/index.zip`]); deleted++; },
    list: async () => ({ objects: [{ key: `${id}/1/00 - Ecole Antibes.txt` }] }),
  });
  try {
    globalThis.fetch = async () => new Response('Denied', { status: 403 });
    assert.equal((await worker.fetch(new Request('https://pilot.example/delete', { method: 'POST',
      headers: { authorization: 'Bearer server', 'content-type': 'application/json' }, body }), env)).status, 403);
    assert.equal(deleted, 0);
    globalThis.fetch = async () => Response.json({ publicationId: id, revision: 1, names: ['index.zip'] });
    assert.equal((await worker.fetch(new Request('https://pilot.example/delete', { method: 'POST',
      headers: { authorization: 'Bearer server', 'content-type': 'application/json' }, body }), env)).status, 204);
    assert.equal(deleted, 2);
  } finally { globalThis.fetch = original; }
});

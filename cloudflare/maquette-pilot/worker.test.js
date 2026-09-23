import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import worker from './worker.js';

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
    const response = await worker.fetch(request({ token, revision: 2, name: 'index.zip' }), {
      MODELS: { get: async key => { keys.push(key); return { size: 4, body: new Uint8Array([1, 2, 3, 4]) }; } },
    });
    assert.equal(response.status, 200);
    assert.deepEqual(keys, [`${id}/2/index.zip`]);
    assert.deepEqual([...new Uint8Array(await response.arrayBuffer())], [1, 2, 3, 4]);
  } finally { globalThis.fetch = original; }
});

test('invalid, undeclared and incomplete files cannot be served', async () => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => Response.json({ publicationId: id, revision: 2, asset });
  const env = { MODELS: { get: async () => ({ size: 3, body: new Uint8Array([1, 2, 3]) }) } };
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
    return Response.json({ publicationId: id, revision: 3, asset: { name: 'index.zip', bytes: 4, sha256 } });
  };
  try {
    const url = `https://pilot.example/upload?publicationId=${id}&revision=3&name=index.zip`;
    const env = { MODELS: { put: async (key, body, options) => {
      writes.push({ key, bytes: [...new Uint8Array(body)], sha256: options.customMetadata.sha256 });
      return { size: body.byteLength };
    } } };
    const headers = { authorization: 'Bearer license' };
    assert.equal((await worker.fetch(new Request(url, { method: 'PUT', headers, body: bytes }), env)).status, 201);
    assert.deepEqual(writes, [{ key: `${id}/3/index.zip`, bytes: [1, 2, 3, 4], sha256 }]);
    assert.equal((await worker.fetch(new Request(url, { method: 'PUT', headers, body: new Uint8Array([9, 2, 3, 4]) }), env)).status, 409);
    assert.equal(writes.length, 1);
  } finally { globalThis.fetch = original; }
});

test('completion only accepts matching R2 metadata', async () => {
  const original = globalThis.fetch;
  const sha256 = 'b'.repeat(64);
  globalThis.fetch = async () => Response.json({ publicationId: id, revision: 3, assets: [{ name: 'index.zip', bytes: 4, sha256 }] });
  try {
    const request = new Request('https://pilot.example/verify', { method: 'POST',
      headers: { authorization: 'Bearer license', 'content-type': 'application/json' },
      body: JSON.stringify({ publicationId: id, revision: 3, names: ['index.zip'] }) });
    const env = { MODELS: { head: async () => ({ size: 4, customMetadata: { sha256 } }) } };
    const result = await worker.fetch(request, env);
    assert.deepEqual(await result.json(), { expected: 1, valid: 1, bytes: 4 });
  } finally { globalThis.fetch = original; }
});

test('deletion requires server authorization for an inactive revision', async () => {
  const original = globalThis.fetch;
  let deleted = 0;
  const body = JSON.stringify({ publicationId: id, revision: 1, names: ['index.zip'] });
  const env = { MODELS: { delete: async keys => { assert.deepEqual(keys, [`${id}/1/index.zip`]); deleted++; } } };
  try {
    globalThis.fetch = async () => new Response('Denied', { status: 403 });
    assert.equal((await worker.fetch(new Request('https://pilot.example/delete', { method: 'POST',
      headers: { authorization: 'Bearer server', 'content-type': 'application/json' }, body }), env)).status, 403);
    assert.equal(deleted, 0);
    globalThis.fetch = async () => Response.json({ publicationId: id, revision: 1, names: ['index.zip'] });
    assert.equal((await worker.fetch(new Request('https://pilot.example/delete', { method: 'POST',
      headers: { authorization: 'Bearer server', 'content-type': 'application/json' }, body }), env)).status, 204);
    assert.equal(deleted, 1);
  } finally { globalThis.fetch = original; }
});

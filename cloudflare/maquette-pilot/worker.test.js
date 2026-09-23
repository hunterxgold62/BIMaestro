import test from 'node:test';
import assert from 'node:assert/strict';
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
    assert.equal(JSON.parse(options.body).token, token);
    return Response.json({ publication: { id, revision: 2 }, manifest: { assets: [asset] } });
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
  globalThis.fetch = async () => Response.json({ publication: { id, revision: 2 }, manifest: { assets: [asset] } });
  const env = { MODELS: { get: async () => ({ size: 3, body: new Uint8Array([1, 2, 3]) }) } };
  try {
    assert.equal((await worker.fetch(request({ token: 'bad', revision: 2, name: 'index.zip' }), env)).status, 400);
    assert.equal((await worker.fetch(request({ token, revision: 2, name: '../secret' }), env)).status, 400);
    assert.equal((await worker.fetch(request({ token, revision: 2, name: 'other.zip' }), env)).status, 404);
    assert.equal((await worker.fetch(request({ token, revision: 2, name: 'index.zip' }), env)).status, 503);
    assert.equal((await worker.fetch(request({ token, revision: 2, name: 'index.zip' }, 'https://other.example'), env)).status, 403);
  } finally { globalThis.fetch = original; }
});

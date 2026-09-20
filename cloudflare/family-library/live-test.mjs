// Controlled smoke test: only this service and an existing native test fixture.
// No production files, account credentials, or automatic retries.
import assert from 'node:assert/strict';
import { readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { createHash, randomBytes } from 'node:crypto';
const base = 'https://bimaestro-family-library.bimaestro-community.workers.dev';
const mode = process.argv[2];
assert.ok(['blocked', 'roundtrip', 'ownership'].includes(mode), 'mode: blocked, roundtrip or ownership');
const fixture = new URL(mode === 'ownership' ? '../../tmp/codex-native-validation/2024-history-network-v7/BIMaestro-test-hosted.rfa' : '../../tmp/codex-native-validation/2024-history-v8/BIMaestro-test-free.rfa', import.meta.url);
const owner = randomBytes(32).toString('hex');
const bytes = readFileSync(fixture);
const hash = createHash('sha256').update(bytes).digest('hex');
const headers = {
  'X-Owner-Token': owner, 'X-Family-Origin': 'personal',
  'Content-Type': 'application/octet-stream', 'Content-Length': String(bytes.length),
  'X-Family-Name': encodeURIComponent('BIMaestro - Famille de test'),
  'X-Family-Category': 'generic', 'X-Revit-Version': '2024',
  'X-Family-Description': encodeURIComponent('Famille de démonstration générée pour les tests natifs BIMaestro. Essai de partage et de téléchargement.')
};
const request = (path, init = {}) => fetch(base + path, { ...init, redirect: 'error', signal: AbortSignal.timeout(30000) });
const response = await request('/v1/families', { method: 'POST', headers, body: bytes });
const data = await response.json();
const result = { at: new Date().toISOString(), mode, status: response.status, bytes: bytes.length, sha256: hash, response: data };
if (mode === 'blocked') {
  assert.equal(response.status, 429);
  assert.equal(data.error, 'quota_exceeded');
} else if (mode === 'ownership') {
  assert.equal(response.status, 201);
  assert.equal(data.item.isOwner, true);
  const own = await request('/v1/families?q=&category=&origin=&mine=1&revitVersion=2025', { headers: { 'X-Owner-Token': owner } });
  assert.equal(own.status, 200);
  assert.ok((await own.json()).items.some(item => item.id === hash && item.isOwner));
  const other = randomBytes(32).toString('hex');
  const denied = await request(`/v1/families/${hash}`, { method: 'DELETE', headers: { 'X-Owner-Token': other } });
  assert.equal(denied.status, 403);
  const removed = await request(`/v1/families/${hash}`, { method: 'DELETE', headers: { 'X-Owner-Token': owner } });
  assert.equal(removed.status, 204);
  const hidden = await request(`/v1/families/${hash}/file`);
  assert.equal(hidden.status, 404);
  result.ownershipVerified = true;
  result.withdrawalVerified = true;
} else {
  assert.ok(response.status === 201 || response.status === 200);
  assert.equal(data.item.id, hash);
  const listing = await request('/v1/families?q=BIMaestro&revitVersion=2025');
  assert.equal(listing.status, 200);
  const listed = (await listing.json()).items.find(item => item.id === hash);
  assert.ok(listed, 'Revit 2025 must include the 2024 fixture');
  const download = await request(`/v1/families/${hash}/file`);
  assert.equal(download.status, 200);
  assert.equal(Number(download.headers.get('content-length')), bytes.length);
  const downloaded = Buffer.from(await download.arrayBuffer());
  assert.deepEqual(downloaded, bytes);
  const after = await request('/v1/families?q=BIMaestro&revitVersion=2025');
  assert.equal(after.status, 200);
  const updated = (await after.json()).items.find(item => item.id === hash);
  assert.equal(updated.downloadCount, listed.downloadCount + 1);
  result.downloadCount = updated.downloadCount;
  result.downloadVerified = true;
}
mkdirSync(new URL('../../tmp/family-library/', import.meta.url), { recursive: true });
writeFileSync(new URL(`../../tmp/family-library/${mode}.json`, import.meta.url), JSON.stringify(result, null, 2));
console.log(JSON.stringify(result));

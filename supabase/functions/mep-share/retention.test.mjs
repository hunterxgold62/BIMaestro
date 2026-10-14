import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';
import ts from '../../../viewer-web/node_modules/typescript/lib/typescript.js';

const source = readFileSync(new URL('./index.ts', import.meta.url), 'utf8');
const cleanup = source.slice(source.indexOf('async function cleanupExpiredPublications()'), source.indexOf('async function cleanupSupersededExports('));
const { outputText } = ts.transpileModule(cleanup + '\nexport { cleanupExpiredPublications };', { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } });
async function run(results, failStorage = false) {
  const calls = [], removed = [];
  const admin = { rpc: async () => ({ data: 99 }), from(table) {
    const call = { table, filters: [] }; calls.push(call);
    const query = new Proxy({}, { get(_, key) {
      if (key === 'then') return (resolve) => resolve(results.shift());
      return (...args) => { call.filters.push([key, ...args]); return query; };
    } });
    return query;
  } };
  globalThis.retentionTest = { admin, publicationOwned: async () => ({ expires_at: '2026-01-01' }), releaseUpload: async () => {}, removeExportAssets: async item => { removed.push(item); return { error: failStorage ? new Error('R2 unavailable') : null }; }, HttpError: Error };
  const { cleanupExpiredPublications } = await import('data:text/javascript;base64,' + Buffer.from('const { admin, publicationOwned, releaseUpload, removeExportAssets, HttpError } = globalThis.retentionTest;\n' + outputText).toString('base64') + '#' + Math.random());
  await cleanupExpiredPublications();
  assert.equal(results.length, 0);
  return { calls, removed };
}
const geometry = { publication_id: 'retained', revision: 4 };
let result = await run([{ data: [{ id: 'retained' }] }, { data: [geometry] }, {}, { data: [] }]);
assert.deepEqual(result.removed, [geometry]);
assert.equal(result.calls.some(c => c.table === 'mep_publications' && c.filters.some(f => f[0] === 'delete')), false, '30-day cleanup must keep reservations');
result = await run([{ data: [] }, { data: [{ id: 'old', mep_exports: [] }, { id: 'pending-files', mep_exports: [{ id: 1 }] }] }, {}]);
const purge = result.calls.at(-1);
assert.deepEqual(purge.filters.find(f => f[0] === 'in'), ['in', 'id', ['old']]);
assert(purge.filters.some(f => f[0] === 'lt' && f[1] === 'data_expires_at'));
assert(purge.filters.some(f => f[0] === 'lt' && f[1] === 'expires_at'));
result = await run([{ data: [{ id: 'retained' }] }, { data: [geometry] }], true);
assert.equal(result.calls.length, 2, 'storage failure must preserve database state');
console.log('PASS: geometry expiry keeps state, five-month purge protects active shares and pending files, R2 failure preserves data');

// Exercise the actual publication entrypoint with a retained share and no
// local publication ID, as when Revit is reopened on another workstation.
const startSource = source.slice(source.indexOf('async function startPublication('), source.indexOf('async function completePublication('));
const startJs = ts.transpileModule(startSource + '\nexport { startPublication };', { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const state = { markups: { r: { kind: 'reservation' } }, reservationLots: { MEP: { name: 'Plomberie' } }, valves: { v: false } };
const retained = { id: 'retained', active_revision: 4, expires_at: '2026-01-01T00:00:00Z', scenario_state: state };
const answers = [{ data: retained }, {}, {}];
const calls = [];
const admin = { from(table) {
  const call = { table, filters: [] }; calls.push(call);
  const query = new Proxy({}, { get(_, key) {
    if (key === 'then') return resolve => resolve(answers.shift());
    return (...args) => { call.filters.push([key, ...args]); return query; };
  } }); return query;
} };
globalThis.retentionStartTest = { admin, reserveUpload: async () => 5, releaseUpload: async () => {}, licenseIdentity: async () => ({ licenseHash: 'owner', licenseKey: 'license', userName: 'Test' }), validateAssets: () => [{ name: 'index.zip', bytes: 1, sha256: 'a'.repeat(64) }], cleanupExpiredPublications: async () => {}, sha256: async () => 'hash', randomToken: () => 'token', cleanName: x => x || 'MEP', r2WorkerUrl: 'https://example.test', maxPackageBytes: 100, maxViewerStorageBytes: 1000, HttpError: Error };
const startModule = await import('data:text/javascript;base64,' + Buffer.from('const { admin, reserveUpload, releaseUpload, licenseIdentity, validateAssets, cleanupExpiredPublications, sha256, randomToken, cleanName, r2WorkerUrl, maxPackageBytes, maxViewerStorageBytes, HttpError } = globalThis.retentionStartTest;\n' + startJs).toString('base64'));
const started = await startModule.startPublication({}, { packageBytes: 1, packageSha256: 'a'.repeat(64), storageBackend: 'r2', modelKey: 'same-model' });
assert.equal(started.publicationId, 'retained'); assert.equal(started.revision, 5);
assert(new Date(started.expiresAt).getTime() > Date.now());
assert(calls[0].filters.some(f => f[0] === 'eq' && f[1] === 'owner_license_hash' && f[2] === 'owner'));
assert(calls[0].filters.some(f => f[0] === 'eq' && f[1] === 'model_key_hash' && f[2] === 'hash'));
const changed = calls[1].filters.find(f => f[0] === 'update')[1];
assert.equal('scenario_state' in changed, false, 'restoring access must not overwrite reservations');
assert.deepEqual(retained.scenario_state, state); assert.equal(answers.length, 0);
console.log('PASS: reopening the same model restores retained publication and advances revision without overwriting collaborative state');

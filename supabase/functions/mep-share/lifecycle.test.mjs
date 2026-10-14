import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';
import ts from '../../../viewer-web/node_modules/typescript/lib/typescript.js';
const source = readFileSync(new URL('./index.ts', import.meta.url), 'utf8');
const manage = source.slice(source.indexOf('async function managePublication('), source.indexOf('serve(async'));
const compiled = ts.transpileModule(manage + '\nexport { managePublication };', { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const state = { markups: { r: { kind: 'reservation' }, n: { kind: 'note' } }, reservationLots: { MEP: {} }, valves: { v: false }, analysis: { allowImplicitTerminals: false } };
class HttpError extends Error { constructor(status, message) { super(message); this.status = status; } }
async function run(body, results, busy = false) {
  const calls = [], released = [], removed = [], broadcasts = [];
  const admin = { from(table) {
    const call = { table, filters: [] }; calls.push(call);
    const query = new Proxy({}, { get(_, key) {
      if (key === 'then') return resolve => resolve(results.shift());
      return (...args) => { call.filters.push([key, ...args]); return query; };
    } }); return query;
  } };
  globalThis.lifecycleTest = { admin, licenseIdentity: async () => ({ licenseHash: 'owner', userName: 'Test' }), publicationOwned: async () => ({ id: 'p', active_revision: 4 }), reserveUpload: async () => { if (busy) throw new Error('busy'); return 7; }, releaseUpload: async (...args) => released.push(args), currentScenario: async () => ({ revision: 9, state }), broadcastScenario: async (...args) => broadcasts.push(args), sha256: async x => x, randomToken: () => 'new-token', removeExportAssets: async x => { removed.push(x); return {}; }, HttpError };
  const mod = await import('data:text/javascript;base64,' + Buffer.from('const { admin, licenseIdentity, publicationOwned, reserveUpload, releaseUpload, currentScenario, broadcastScenario, sha256, randomToken, removeExportAssets, HttpError } = globalThis.lifecycleTest;\n' + compiled).toString('base64') + '#' + Math.random());
  let error;
  try { await mod.managePublication({}, body); } catch (e) { error = e; }
  assert.equal(results.length, 0);
  return { calls, released, removed, broadcasts, error };
}
let result = await run({ command: 'delete', scope: 'data' }, [{ data: { id: 'p' } }]);
assert.equal(result.error, undefined);
const data = result.calls[0].filters.find(f => f[0] === 'update')[1];
assert.deepEqual(data.scenario_state.markups, {}); assert.deepEqual(data.scenario_state.reservationLots, {});
assert.deepEqual(data.scenario_state.valves, state.valves); assert.deepEqual(data.scenario_state.analysis, state.analysis);
assert.equal(result.removed.length, 0); assert.equal(result.broadcasts.length, 1); assert.equal(result.released.length, 1);
for (const scope of ['model', 'both']) {
  result = await run({ command: 'delete', scope }, [{}, { data: [{ revision: 4 }] }, {}]);
  assert.equal(result.error, undefined); assert.equal(result.removed.length, 1);
  const deletes = result.calls.filter(c => c.filters.some(f => f[0] === 'delete'));
  assert.deepEqual(deletes.map(c => c.table), [scope === 'model' ? 'mep_exports' : 'mep_publications']);
  const expired = result.calls[0].filters.find(f => f[0] === 'update')[1];
  assert(new Date(expired.expires_at).getTime() <= Date.now());
  assert('viewer_token_hash' in expired && 'editor_token_hash' in expired);
  assert.equal('scenario_state' in expired, false);
}
result = await run({ command: 'extend' }, [{ data: null }]);
assert(result.error.message.includes('nettoyée')); assert.equal(result.calls.length, 1); assert.equal(result.released.length, 1);
result = await run({ command: 'extend' }, [{ data: { id: 1 } }, {}]); assert.equal(result.error, undefined);
result = await run({ command: 'delete', scope: 'both' }, [], true); assert(result.error); assert.equal(result.calls.length, 0);
result = await run({ command: 'revoke' }, []); assert(result.error.message.includes('Choisissez'));
const upload = source.slice(source.indexOf('function requireUpload('), source.indexOf('async function reserveUpload('));
const uploadJs = ts.transpileModule(upload + '\nexport { requireUpload };', { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const { requireUpload } = await import('data:text/javascript;base64,' + Buffer.from('const HttpError = Error;\n' + uploadJs).toString('base64'));
const publication = { active_revision: 4, revision_counter: 7, upload_until: new Date(Date.now() + 60000).toISOString() };
assert.doesNotThrow(() => requireUpload(publication, 7));
assert.throws(() => requireUpload(publication, 5)); assert.throws(() => requireUpload({ ...publication, upload_until: '2020-01-01' }, 7));
console.log('PASS: three deletion scopes, preservation of simulation, link invalidation, empty-model extension blocked, busy deletion refused, obsolete/expired uploads refused');

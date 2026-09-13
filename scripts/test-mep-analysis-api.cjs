const fs = require('node:fs'), assert = require('node:assert/strict');
const credentials = JSON.parse(fs.readFileSync('tmp/mep-api-test.json', 'utf8'));
const url = 'https://xqovxfgghbqxwsadzhzl.functions.supabase.co/mep-share';
async function call(token, body) { const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token, ...body }) }); return { status: response.status, data: await response.json() }; }
(async () => {
 const change = { action: 'analysis', expectedRevision: 0, modelRevision: 1, settings: { allowImplicitTerminals: false } };
 assert.equal((await call(credentials.viewer, change)).status, 403);
 assert.equal((await call(credentials.editor, { ...change, modelRevision: 2 })).status, 409);
 assert.equal((await call(credentials.editor, { ...change, settings: { endpoints: { x: 9 } } })).status, 400);
 const saved = await call(credentials.editor, change); assert.equal(saved.status, 200); assert.equal(saved.data.scenario.revision, 1);
 assert.equal(saved.data.scenario.state.valves['test-valve'], false); assert.equal(saved.data.scenario.state.sources['test-source'], 'inlet');
 assert.equal((await call(credentials.editor, change)).status, 409);
 const concurrent = await Promise.all([1,2].map(role => call(credentials.editor, { ...change, expectedRevision: 1, settings: { endpoints: { x: role } } })));
 assert.deepEqual(concurrent.map(r => r.status).sort(), [200,409]);
 const read = await call(credentials.viewer, { action: 'state' }); assert.equal(read.status, 200); assert.equal(read.data.scenario.revision, 2); assert.equal(read.data.scenario.state.analysis.allowImplicitTerminals, false);
 console.log('Live API: editor/viewer rights, validation, stale model/scenario, concurrent updates and preserved state passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });

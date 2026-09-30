const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../BIMaestro/les couleurs/Changement auto des couleurs/ColoringStateManager.cs'), 'utf8');
const start = source.indexOf('    const findVisibleActiveParent=()=>{');
const end = source.indexOf('    const renderActiveParent=()=>{', start);
const context = vm.createContext({
  activeViewPath: ['Livrable', 'GCO'], activeViewElementId: 'view',
  activeParentNodeId: 'wrong', normalizeLabel: name => name.trim().toLowerCase(),
  getVisibleViewRows: () => context.rows
});
vm.runInContext(source.slice(start, end) + '\nthis.findParent=findVisibleActiveParent;', context);
const row = (name, id, ancestors = []) => ({ name, id, ancestors, branch: 'views' });
const root = row('vues (toutes)', 'root');
const work = row('travail', 'work', [root]);
const delivery = row('livrable', 'delivery', [root]);
const wrong = row('gco', 'wrong', [root, work]);
const correct = row('gco', 'correct', [root, delivery]);
context.rows = [root, work, wrong, delivery, correct];
assert.equal(context.findParent(), correct, 'Same label in a different branch must not match, even when cached');
context.rows = [wrong, correct];
assert.equal(context.findParent(), correct, 'Remembered complete lineage works after scrolling');
context.rows = [wrong];
assert.equal(context.findParent(), null, 'Do not mark the other branch when the correct branch is offscreen');
context.rows = [wrong, delivery];
assert.equal(context.findParent(), delivery, 'Use the closest visible verified parent');
const partial = row('gco', 'partial', [root]);
context.rows = [partial];
assert.equal(context.findParent(), null, 'Incomplete lineage is not proof of identity');
context.activeViewPath = ['Livrable', '???'];
context.rows = [row('???', 'wrong', [root, work]), row('???', 'correct', [root, delivery])];
assert.equal(context.findParent().id, 'correct', 'Placeholder labels also require the full path');
context.activeViewPath = ['Livrable', 'GCO'];
context.rows = [correct, row('gco', 'duplicate', [root, delivery])];
assert.equal(context.findParent(), null, 'Ambiguous identities must not be guessed');
console.log('Active parent navigation: 7 regression checks passed.');

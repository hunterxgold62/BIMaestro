const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../BIMaestro/les couleurs/Changement auto des couleurs/ColoringStateManager.cs'), 'utf8');
const start = source.indexOf('    const getCategoryRules=row=>{');
const end = source.indexOf('    const applyCategoryRules=', start);
const rule = {path:"Plans d'étage", color:'#00ff00', scope:'Branche', effect:'Texte'};
const context = vm.createContext({
  theme:{categoryEnabled:true, categoryRules:[rule]},
  normalizeLabel:value=>String(value).trim().toLowerCase(),
  viewPathsById:new Map([['1',''],['2','Ancien dossier'],['3',"Plans d'étage"]]),
  viewPathsByName:new Map()
});
vm.runInContext(source.slice(start,end)+'\nthis.rulesFor=getRowCategoryRules;',context);
const root={name:'vues (tout)',branch:'views'};
const folder={name:"plans d'étage",branch:'views',ancestors:[root]};
for(const [id,name] of [['1','niveau 1'],['2','niveau 2'],['3','site']]) {
  const row={id,name,branch:'views',ancestors:[root,folder]};
  assert.equal(context.rulesFor(row).includes(rule),true,`${name} inherits its visible folder despite a missing or stale mapped path`);
}
assert.equal(context.rulesFor({id:'3',name:'site',branch:'views',ancestors:[root,folder]}).length,1,'Rule is applied once');
rule.scope='Dossier';
assert.equal(context.rulesFor(folder).length,1,'Folder-only scope colors the folder');
assert.equal(context.rulesFor({id:'3',name:'site',branch:'views',ancestors:[root,folder]}).length,0,'Folder-only scope does not color views');
rule.scope='Enfants';
assert.equal(context.rulesFor(folder).length,0,'Contents-only scope leaves the folder unchanged');
assert.equal(context.rulesFor({id:'1',name:'niveau 1',branch:'views',ancestors:[root,folder]}).length,1,'Contents-only scope colors descendants');
console.log('PASS: 8 folder color regression checks.');

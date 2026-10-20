// Offline syntax and scope checks; native screenshots remain the visual authority.
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=fs.readFileSync(require('node:path').join(__dirname,'home-concepts-legacy.js'),'utf8');
new vm.Script(source);
const foreign={window:{},location:{href:'https://example.com/home'}};
vm.runInNewContext(source,foreign);assert.equal(foreign.window.BIMaestroHomeConceptLegacy,undefined);
const context={window:{innerWidth:1500},location:{href:'file:///C:/Program%20Files/Autodesk/Revit%202023/Addins/ModelBrowser/resource/index.html#/'},document:{documentElement:{scrollWidth:1500},getElementById:()=>null,querySelector:()=>null,querySelectorAll:()=>[]}};
vm.runInNewContext(source,context);
const api=context.window.BIMaestroHomeConceptLegacy;
assert(api);assert.equal(api.apply('2023').applied,false);assert.equal(api.apply('2024').applied,false);assert.equal(api.apply('2026').applied,false);assert.equal(api.inspect().cards,0);
console.log('PASS: syntax, exact legacy Home URL scope, absent native DOM rejection, wrong concept guard. Native visual/integration checks still required.');

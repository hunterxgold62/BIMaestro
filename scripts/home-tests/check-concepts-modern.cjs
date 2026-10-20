// Syntax and scope guard checks. Visual/native integration is tested in Revit separately.
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'home-concepts-modern.js'),'utf8');
new vm.Script(source);
const foreign={window:{},location:{href:'https://example.com/udabrowser.html'}};vm.runInNewContext(source,foreign);assert.equal(foreign.window.BIMaestroHomeConceptModern,undefined);
const attrs=new Map(),context={window:{},location:{href:'file:///C:/Autodesk/Revit%202026/Addins/ModelBrowser/resource/udabrowser.html'},document:{documentElement:{getAttribute:k=>attrs.get(k)||null},getElementById:()=>null,querySelector:()=>null,querySelectorAll:()=>[]}};
vm.runInNewContext(source,context);const api=context.window.BIMaestroHomeConceptModern;assert(api);assert.equal(api.apply('2026').applied,false);assert.equal(api.inspect().cards,0);assert.throws(()=>api.apply('2024'));assert.equal(attrs.size,0);
console.log('PASS: syntax, exact Home URL guard, absent DOM guard, invalid version rejection, no mutation on rejected application. Full design integration requires native Revit.');


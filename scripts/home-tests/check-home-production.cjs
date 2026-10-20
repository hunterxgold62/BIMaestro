// Production resources: foreign-page and absent-native-DOM guards must never mutate.
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
const dir=path.resolve(__dirname,'../../BIMaestro/les couleurs/Home/Resources');
for(const [file,page,apiName] of [['home-legacy.js','index','BIMaestroHomeConceptLegacy'],['home-modern.js','udabrowser','BIMaestroHomeConceptModern']]){
 const source=fs.readFileSync(path.join(dir,file),'utf8');new vm.Script(source);
 for(const href of ['https://example.org/'+page+'.html','file:///C:/unrelated/'+page+'.html']){const ctx={window:{},location:{href}};vm.runInNewContext(source,ctx);assert.equal(ctx.window[apiName],undefined);}
 for(const dark of [false,true]){
  let writes=0;const ctx={window:{__bmHomeDark:dark,innerWidth:1500},location:{href:'file:///C:/Revit/AddIns/ModelBrowser/resource/'+page+'.html'},document:{documentElement:{scrollWidth:1500,clientWidth:1500,getAttribute:()=>null,setAttribute:()=>writes++},getElementById:()=>null,querySelector:()=>null,querySelectorAll:()=>[]}};
  vm.runInNewContext(source,ctx);const api=ctx.window[apiName];assert(api);
  for(const style of ['2023','2024','2025','2026'])assert.equal(api.apply(style).applied,false);
  assert.equal(writes,0);
 }
 assert(source.includes('--bm-home-'));assert(source.includes('window.__bmHomeDark===true'));
 console.log('PASS '+file+': exact Home scope, all four selections reject absent native DOM without mutation, light/dark guards.');
}

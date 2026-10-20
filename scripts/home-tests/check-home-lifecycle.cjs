// Minimal native-Home-shaped DOM verifies application/reapplication lifecycle without Revit.
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
const dir=path.resolve(__dirname,'../../BIMaestro/les couleurs/Home/Resources');
for(const modern of [false,true]){
 const nodes=[];class Element{constructor(){this.attributes={};this.children=[];this.parentNode=null;this.style={};nodes.push(this);}appendChild(n){this.children.push(n);n.parentNode=this;return n;}insertBefore(n){return this.appendChild(n);}removeChild(n){this.children=this.children.filter(x=>x!==n);n.parentNode=null;}remove(){if(this.parentNode)this.parentNode.removeChild(this);}setAttribute(k,v){this.attributes[k]=v;}getAttribute(k){return this.attributes[k]??null;}removeAttribute(k){delete this.attributes[k];}querySelector(){return null;}addEventListener(){}insertAdjacentElement(_,n){this.parentNode.appendChild(n);}}
 const root=new Element(),head=new Element(),body=new Element(),recent=new Element(),side=new Element(),open=new Element();root.scrollWidth=root.clientWidth=1500;root.contains=()=>true;body.appendChild(recent);body.appendChild(side);
 const doc={documentElement:root,head,body,createElement:()=>new Element(),querySelector:s=>s==='.RecentFilesView'||s==='[data-testid="recent-container"]'?recent:s==='.MbSidePanel'?side:s==='[data-testid="open-revit-file-button"]'?open:null,querySelectorAll:()=>[],getElementById:id=>nodes.find(n=>n.id===id&&n.parentNode)||null};
 const ctx={window:{innerWidth:1500,BIMaestroHomeProbe:{apply(){},restore(){}}},location:{href:'file:///C:/Revit/AddIns/ModelBrowser/resource/'+(modern?'udabrowser':'index')+'.html'},document:doc,MutationObserver:class{observe(){}disconnect(){}},setTimeout:()=>1,clearTimeout(){}};
 vm.runInNewContext(fs.readFileSync(path.join(dir,modern?'home-modern.js':'home-legacy.js'),'utf8'),ctx);const api=ctx.window[modern?'BIMaestroHomeConceptModern':'BIMaestroHomeConceptLegacy'];
 for(const dark of [false,true])for(const style of ['2023','2024','2025','2026']){ctx.window.__bmHomeDark=dark;assert.equal(api.apply(style).applied,true);const styleNode=doc.getElementById(modern?'bm-modern-concept-style':'bm-legacy-style');assert(styleNode);assert(styleNode.textContent.includes('--bm-home-bg:'));if(dark)assert(styleNode.textContent.includes('--bm-home-bg:#18211e'));api.restore();assert.equal(doc.getElementById(styleNode.id),null);}
 console.log('PASS '+(modern?'modern':'legacy')+': four styles x two themes apply and restore owned stylesheet.');
}


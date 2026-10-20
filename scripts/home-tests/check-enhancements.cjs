const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'home-enhancements.js'),'utf8');
function node(id,name,initial={}){
  const attrs=new Map(Object.entries(initial));
  const n={id,textContent:name,getAttribute:k=>attrs.has(k)?attrs.get(k):null,setAttribute:(k,v)=>attrs.set(k,v),removeAttribute:k=>attrs.delete(k)};
  n.style={setProperty(k,v){this[k]=v;attrs.set('style',(attrs.get('style')||'')+k+':'+v+'!important;');}};
  return n;
}
function run(url,signatures=true){
 const a=node('C:\\A.rvt','Zulu',{title:'old title',style:'opacity:.8;'}),b=node('C:\\B.rfa','Alpha');
 const badgeA=node('',''),badgeB=node('','',{title:'old badge'});
 [a,b].forEach((c,i)=>c.querySelector=s=>s.includes('file-type-wrapper')?[badgeA,badgeB][i]:s.includes('tile-title')?{textContent:c.textContent}:null);
 const cards=[a,b];const context={window:{},location:{href:url},document:{querySelector:()=>signatures?{}:null,querySelectorAll:()=>cards}};
 vm.createContext(context);vm.runInContext(source,context);return {...context,cards,badges:[badgeA,badgeB]};
}
const url='file:///C:/Autodesk/Revit%202025/Addins/ModelBrowser/resource/udabrowser.html';
assert.equal(run('https://example.com').window.__bimaestroHomeEnhance,undefined);
const absent=run(url,false);assert.equal(absent.window.__bimaestroHomeEnhance.apply().applied,false);assert.equal(absent.cards[0].getAttribute('title'),'old title');
const r=run(url),api=r.window.__bimaestroHomeEnhance;
assert.equal(api.apply({group:true,alphabetical:true}).applied,true);
assert.equal(r.cards[0].getAttribute('title'),'C:\\A.rvt');assert.equal(r.cards[1].getAttribute('title'),'C:\\B.rfa');
assert.equal(r.cards[1].style.order,'0');assert.equal(r.cards[0].style.order,'1');
assert.equal(r.badges[0].getAttribute('title'),'RVT');assert.equal(r.badges[1].getAttribute('title'),'RFA');
api.apply({alphabetical:true});assert.equal(api.inspect().changedNodes,4);
api.restore();
assert.equal(r.cards[0].getAttribute('title'),'old title');assert.equal(r.cards[0].getAttribute('style'),'opacity:.8;');
assert.equal(r.cards[1].getAttribute('title'),null);assert.equal(r.cards[1].getAttribute('style'),null);
assert.equal(r.badges[1].getAttribute('title'),'old badge');assert.equal(r.badges[0].getAttribute('style'),null);
console.log('PASS: guarded Home enhancements, paths/types, visual grouping/order, native badge recoloring, repeat apply and exact attribute restoration. Offline DOM mock only.');

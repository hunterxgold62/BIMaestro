import test from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {readFileSync,mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {Worker} from 'node:worker_threads';
import worker,{MAX_UPLOAD_BYTES,readBounded} from '../worker.js';
import {reserve,requestCost} from '../quota.js';
function harness(){
 const sql=new DatabaseSync(':memory:');sql.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
 sql.exec("UPDATE quota_policy SET enabled=1,threshold_percent=1,verified_until='2099-01-01T00:00:00.000Z'");
 const db={prepare(text){return {bind(...args){const stmt=sql.prepare(text);return {
  async first(){return stmt.get(...args)||null;},async all(){return {results:stmt.all(...args)};},async run(){return stmt.run(...args);}
 };}};}};
 const objects=new Map();const calls={put:0,get:0};
 const env={LIBRARY_ENABLED:'true',CATALOG:db,FAMILIES:{async put(key,value){calls.put++;objects.set(key,value);},async get(key){calls.get++;const value=objects.get(key);return value?{body:value,size:value.length}:null;}}};
 return {sql,db,env,calls};
}
function upload(size=512,body){const bytes=body||new Uint8Array(size);if(!body)bytes.set([208,207,17,224,161,177,26,225]);return new Request('https://library/v1/families',{method:'POST',headers:{'Content-Length':String(size),'X-Family-Name':encodeURIComponent('Étagère'),'X-Family-Category':'Mobilier','X-Revit-Version':'2024','X-Owner-Token':'a'.repeat(64),'X-Family-Origin':'ai','X-Creator-Name':encodeURIComponent('Marie Martin')},body:bytes});}

test('stalled upload hits wall-clock deadline and cancels the input stream',async()=>{
 let cancelled=false;
 const body=new ReadableStream({cancel(){cancelled=true;}});
 await assert.rejects(readBounded({body},512,10),/upload_timeout/);
 assert.equal(cancelled,true);
});
test('1% storage boundary is atomic under simultaneous reservations',async()=>{
 const h=harness();h.sql.exec('UPDATE quota_state SET r2_bytes=99998976');
 const accepted=await Promise.all(Array.from({length:12},()=>reserve(h.db,requestCost('upload',512))));
 assert.equal(accepted.filter(Boolean).length,2);
 assert.equal(h.sql.prepare('SELECT r2_bytes FROM quota_state').get().r2_bytes,100000000);
});
test('independent SQLite connections race at 1% without over-reserving',async()=>{
 const folder=mkdtempSync(join(tmpdir(),'bimaestro-quota-'));const path=join(folder,'quota.sqlite');
 const db=new DatabaseSync(path);db.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
 db.exec("UPDATE quota_policy SET enabled=1,threshold_percent=1,verified_until='2099-01-01T00:00:00.000Z'; UPDATE quota_state SET r2_bytes=99998976");
 const gate=new SharedArrayBuffer(4);let ready=0;const workers=[];
 try{
  const results=await Promise.all(Array.from({length:8},()=>new Promise((resolve,reject)=>{
   const child=new Worker(new URL('./reserve-worker.js',import.meta.url),{workerData:{path,gate}});workers.push(child);
   child.on('error',reject);child.on('message',message=>{if(message==='ready'){if(++ready===8){Atomics.store(new Int32Array(gate),0,1);Atomics.notify(new Int32Array(gate),0);}}else resolve(message);});
  })));
  assert.equal(results.filter(Boolean).length,2);
  assert.equal(db.prepare('SELECT r2_bytes FROM quota_state').get().r2_bytes,100000000);
 }finally{await Promise.all(workers.map(w=>w.terminate()));db.close();rmSync(folder,{recursive:true,force:true});}
});
test('every metric blocks before crossing 1% independently',async()=>{
 for(const metric of ['r2_bytes','r2_a','r2_b','worker_requests','d1_reads','d1_writes','d1_bytes']){
  const h=harness();h.sql.exec(`UPDATE quota_state SET ${metric}=(SELECT ${metric}_limit/100 FROM quota_policy)`);
  assert.equal(await reserve(h.db,{[metric]:1}),false,metric);
 }
});
test('80% permits exact boundary and rejects next unit for every metric',async()=>{
 for(const metric of ['r2_bytes','r2_a','r2_b','worker_requests','d1_reads','d1_writes','d1_bytes']){
  const h=harness();h.sql.exec('UPDATE quota_policy SET threshold_percent=80');
  h.sql.exec(`UPDATE quota_state SET ${metric}=(SELECT ${metric}_limit*80/100-1 FROM quota_policy)`);
  assert.equal(await reserve(h.db,{[metric]:1}),true,metric);
  assert.equal(await reserve(h.db,{[metric]:1}),false,metric);
 }
});
test('tampered oversized, fractional or nonnumeric policy/state fails closed',async()=>{
 for(const statement of [
  'UPDATE quota_policy SET r2_bytes_limit=100000000000',
  'UPDATE quota_policy SET r2_bytes_limit=-1',
  "UPDATE quota_policy SET r2_bytes_limit='oops'",
  'UPDATE quota_state SET r2_bytes=1.5',
  'UPDATE quota_state SET r2_bytes=9223372036854775807',
  "UPDATE quota_state SET r2_bytes='oops'"
 ]){const h=harness();h.sql.exec(statement);assert.equal(await reserve(h.db,requestCost('upload',512)),false);}
});
test('Worker denied upload at 1% never calls R2 or inserts family',async()=>{
 const h=harness();h.sql.exec('UPDATE quota_state SET r2_bytes=100000000');
 const response=await worker.fetch(upload(),h.env);assert.equal(response.status,429);
 assert.equal((await response.json()).error,'quota_exceeded');assert.equal(h.calls.put,0);
 assert.equal(h.sql.prepare('SELECT count(*) n FROM shared_families').get().n,0);
});
test('upload, deduplication, catalogue filters and binary download',async()=>{
 const h=harness();let response=await worker.fetch(upload(),h.env);assert.equal(response.status,201);
 const {item}=await response.json();assert.equal(item.name,'Étagère');assert.equal(item.creatorName,'Marie Martin');assert.equal(h.calls.put,1);
 response=await worker.fetch(upload(),h.env);assert.equal((await response.json()).duplicate,true);assert.equal(h.calls.put,1);
 response=await worker.fetch(new Request('https://library/v1/families?q='+encodeURIComponent('étag')),h.env);
 assert.equal((await response.json()).items.length,1);
 response=await worker.fetch(new Request(`https://library/v1/families/${item.id}/file`),h.env);
 assert.equal(response.status,200);assert.equal((await response.arrayBuffer()).byteLength,512);
});
test('owner can add a PNG preview that is advertised and publicly cached',async()=>{
 const h=harness();const {item}=await (await worker.fetch(upload(),h.env)).json();
 const png=new Uint8Array([137,80,78,71,13,10,26,10,0,0,0,0]);
 const put=new Request(`https://library/v1/families/${item.id}/preview`,{method:'PUT',headers:{'Content-Length':String(png.length),'Content-Type':'image/png','X-Owner-Token':'a'.repeat(64)},body:png});
 assert.equal((await worker.fetch(put,h.env)).status,204);
 const list=await (await worker.fetch(new Request('https://library/v1/families'),h.env)).json();
 assert.equal(list.items[0].hasPreview,true);
 const response=await worker.fetch(new Request(`https://library/v1/families/${item.id}/preview`),h.env);
 assert.equal(response.status,200);assert.equal(response.headers.get('Content-Type'),'image/png');assert.match(response.headers.get('Cache-Control'),/max-age=300/);
 assert.deepEqual(new Uint8Array(await response.arrayBuffer()),png);
});
test('preview upload rejects another owner and non-PNG content',async()=>{
 const h=harness();const {item}=await (await worker.fetch(upload(),h.env)).json();
 const request=(token,bytes)=>new Request(`https://library/v1/families/${item.id}/preview`,{method:'PUT',headers:{'Content-Length':String(bytes.length),'Content-Type':'image/png','X-Owner-Token':token},body:bytes});
 assert.equal((await worker.fetch(request('b'.repeat(64),new Uint8Array([137,80,78,71,13,10,26,10])),h.env)).status,403);
 assert.equal((await worker.fetch(request('a'.repeat(64),new Uint8Array(8)),h.env)).status,400);
 assert.equal(h.sql.prepare('SELECT has_preview FROM shared_families').get().has_preview,0);
});
test('length lies, invalid magic and oversize cannot write R2',async()=>{
 for(const request of [upload(512,new Uint8Array(513)),upload(512,new Uint8Array(511)),upload(512,new Uint8Array(512)),upload(MAX_UPLOAD_BYTES+1,new Uint8Array(8))]){
  const h=harness();const response=await worker.fetch(request,h.env);assert.ok([400,413].includes(response.status));assert.equal(h.calls.put,0);
 }
});

test('Revit 2025 lists 2023 through 2025 and excludes 2026',async()=>{
 const h=harness();
 for(const year of [2022,2023,2024,2025,2026])h.sql.prepare("INSERT INTO shared_families(id,name,category,revit_version,description,size_bytes,created_at,status) VALUES(?,?,?,?,?,512,?,'ready')")
  .run(String(year).padStart(64,'0'),'Famille '+year,'generic',year,'','2026-09-20');
 for(const version of [2023,2024,2025,2026]){
  const response=await worker.fetch(new Request(`https://library/v1/families?revitVersion=${version}`),h.env);
  assert.equal(response.status,200);
  assert.deepEqual((await response.json()).items.map(x=>x.revitVersion),[2023,2024,2025,2026].filter(y=>y<=version));
 }
 assert.equal((await worker.fetch(new Request('https://library/v1/families?revitVersion=oops'),h.env)).status,400);
});

test('download deliveries increment atomically; listing and failed downloads do not',async()=>{
 const h=harness();
 const {item}=await (await worker.fetch(upload(),h.env)).json();
 const request=()=>new Request(`https://library/v1/families/${item.id}/file`);
 assert.equal(item.downloadCount,0);
 const responses=await Promise.all([worker.fetch(request(),h.env),worker.fetch(request(),h.env)]);
 assert.ok(responses.every(r=>r.status===200));
 const list=await worker.fetch(new Request('https://library/v1/families?revitVersion=2025'),h.env);
 assert.equal((await list.json()).items[0].downloadCount,2);
 h.env.FAMILIES.get=async()=>null;
 assert.equal((await worker.fetch(request(),h.env)).status,503);
 h.sql.exec('UPDATE quota_state SET r2_b=100000');
 assert.equal((await worker.fetch(request(),h.env)).status,429);
 assert.equal(h.sql.prepare('SELECT download_count FROM shared_families').get().download_count,2);
});
test('failed R2 write retains reservation and pending claim blocks blind retry',async()=>{
 const h=harness();h.env.FAMILIES.put=async()=>{h.calls.put++;throw new Error('ambiguous network error');};
 assert.equal((await worker.fetch(upload(),h.env)).status,503);
 assert.equal(h.sql.prepare('SELECT r2_bytes FROM quota_state').get().r2_bytes,512);
 assert.equal((await worker.fetch(upload(),h.env)).status,409);assert.equal(h.calls.put,1);
});
test('expired, disabled, missing state and failed D1 all fail closed',async()=>{
 for(const setup of [h=>h.sql.exec("UPDATE quota_policy SET verified_until='2000-01-01'"),h=>h.sql.exec('UPDATE quota_policy SET enabled=0'),h=>h.sql.exec('DELETE FROM quota_state'),h=>h.sql.exec('DELETE FROM quota_policy'),h=>{h.env.CATALOG.prepare=()=>{throw new Error('offline');};}]){
  const h=harness();setup(h);assert.ok([429,503].includes((await worker.fetch(upload(),h.env)).status));assert.equal(h.calls.put,0);
 }
 const h=harness();h.env.LIBRARY_ENABLED='false';h.env.CATALOG.prepare=()=>assert.fail('must not access DB');
 assert.equal((await worker.fetch(upload(),h.env)).status,503);
});
test('month/day changes never reset reserved counters',async()=>{
 const h=harness();h.sql.exec('UPDATE quota_state SET r2_a=10000');
 assert.equal(await reserve(h.db,{r2_a:1},'2026-10-01T00:00:00.000Z'),false);
});
test('ownership proof is required and never leaked in public items',async()=>{
 const h=harness();const missing=upload();missing.headers.delete('X-Owner-Token');
 assert.equal((await worker.fetch(missing,h.env)).status,400);
 const originMissing=upload();originMissing.headers.delete('X-Family-Origin');assert.equal((await worker.fetch(originMissing,h.env)).status,400);
 const {item}=await (await worker.fetch(upload(),h.env)).json();assert.equal(item.isOwner,true);assert.equal(item.origin,'ai');
 const listing=await (await worker.fetch(new Request('https://library/v1/families'),h.env)).json();
 assert.equal(listing.items[0].isOwner,false);assert.equal('owner_hash' in listing.items[0],false);
 assert.equal(JSON.stringify(listing).includes('a'.repeat(64)),false);
});
test('different owner cannot claim duplicate; author can withdraw and restore',async()=>{
 const h=harness();const {item}=await (await worker.fetch(upload(),h.env)).json();
 const duplicate=upload();duplicate.headers.set('X-Owner-Token','b'.repeat(64));duplicate.headers.set('X-Family-Origin','personal');
 const dupe=await (await worker.fetch(duplicate,h.env)).json();assert.equal(dupe.duplicate,true);assert.equal(dupe.item.isOwner,false);assert.equal(dupe.item.origin,'ai');
 const remove=token=>new Request(`https://library/v1/families/${item.id}`,{method:'DELETE',headers:{'X-Owner-Token':token}});
 assert.equal((await worker.fetch(remove('b'.repeat(64)),h.env)).status,403);
 const before=h.sql.prepare('SELECT r2_bytes,r2_a,r2_b FROM quota_state').get();
 assert.equal((await worker.fetch(remove('a'.repeat(64)),h.env)).status,204);
 assert.equal((await worker.fetch(remove('a'.repeat(64)),h.env)).status,204);
 assert.deepEqual(h.sql.prepare('SELECT r2_bytes,r2_a,r2_b FROM quota_state').get(),before);
 assert.equal((await (await worker.fetch(new Request('https://library/v1/families'),h.env)).json()).items.length,0);
 assert.equal((await worker.fetch(new Request(`https://library/v1/families/${item.id}/file`),h.env)).status,404);
 const restoredResponse=await worker.fetch(upload(),h.env);assert.equal(restoredResponse.status,200);
 const restored=await restoredResponse.json();assert.equal(restored.restored,true);assert.equal(restored.item.isOwner,true);
 assert.equal((await (await worker.fetch(new Request('https://library/v1/families'),h.env)).json()).items.length,1);
 assert.equal(h.calls.put,1);assert.equal(h.calls.get,0);
});
test('withdrawn family cannot be restored by a different owner',async()=>{
 const h=harness();const {item}=await (await worker.fetch(upload(),h.env)).json();
 await worker.fetch(new Request(`https://library/v1/families/${item.id}`,{method:'DELETE',headers:{'X-Owner-Token':'a'.repeat(64)}}),h.env);
 const other=upload();other.headers.set('X-Owner-Token','b'.repeat(64));
 assert.equal((await worker.fetch(other,h.env)).status,409);
 assert.equal(h.sql.prepare('SELECT withdrawn FROM shared_families').get().withdrawn,1);
});
test('mine and origin filters respect token and legacy ownerless rows',async()=>{
 const h=harness();await worker.fetch(upload(),h.env);
 const mine=async(token,origin)=>await (await worker.fetch(new Request(`https://library/v1/families?mine=1&origin=${origin}`,{headers:{'X-Owner-Token':token}}),h.env)).json();
 assert.equal((await mine('a'.repeat(64),'ai')).items.length,1);
 assert.equal((await mine('a'.repeat(64),'personal')).items.length,0);
 assert.equal((await mine('b'.repeat(64),'ai')).items.length,0);
 assert.equal((await worker.fetch(new Request('https://library/v1/families?mine=1'),h.env)).status,400);
 h.sql.exec("UPDATE shared_families SET owner_hash='',origin='unknown'");
 const dupe=await (await worker.fetch(upload(),h.env)).json();assert.equal(dupe.item.isOwner,false);
 assert.equal((await worker.fetch(new Request(`https://library/v1/families/${dupe.item.id}`,{method:'DELETE',headers:{'X-Owner-Token':'a'.repeat(64)}}),h.env)).status,403);
});
test('desktop default empty filters list all visible families',async()=>{
 const h=harness();await worker.fetch(upload(),h.env);
 const response=await worker.fetch(new Request('https://library/v1/families?q=&category=&revitVersion=2025&cursor=&origin=&mine=0',
  {headers:{'X-Owner-Token':'a'.repeat(64)}}),h.env);
 assert.equal(response.status,200);const result=await response.json();assert.equal(result.items.length,1);assert.equal(result.items[0].isOwner,true);
});
test('withdrawal during R2 lookup prevents subsequent delivery',async()=>{
 const h=harness();const {item}=await (await worker.fetch(upload(),h.env)).json();
 h.env.FAMILIES.get=async()=>{h.sql.exec('UPDATE shared_families SET withdrawn=1');return {body:new Uint8Array(512),size:512};};
 assert.equal((await worker.fetch(new Request(`https://library/v1/families/${item.id}/file`),h.env)).status,404);
 assert.equal(h.sql.prepare('SELECT download_count FROM shared_families').get().download_count,0);
});
test('owner edits metadata and publishes a downloadable version history',async()=>{
 const h=harness();const first=(await (await worker.fetch(upload(),h.env)).json()).item;
 const edit=new Request(`https://library/v1/families/${first.id}`,{method:'PATCH',headers:{'X-Owner-Token':'a'.repeat(64),'X-Family-Name':encodeURIComponent('Nom corrigé'),'X-Family-Category':'generic','X-Family-Description':'Description','X-Family-Origin':'personal'}});
 const edited=await worker.fetch(edit,h.env);assert.equal(edited.status,200);assert.equal((await edited.json()).item.name,'Nom corrigé');
 const forbidden=new Request(`https://library/v1/families/${first.id}`,{method:'PATCH',headers:{'X-Owner-Token':'b'.repeat(64),'X-Family-Name':'Vol','X-Family-Category':'generic','X-Family-Description':'','X-Family-Origin':'ai'}});assert.equal((await worker.fetch(forbidden,h.env)).status,403);
 const bytes=new Uint8Array(512);bytes.set([0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1]);bytes[20]=1;
 const next=upload(512,bytes);next.headers.set('X-Replaces-Family-ID',first.id);next.headers.set('X-Change-Note',encodeURIComponent('Paramètres corrigés'));
 const published=await worker.fetch(next,h.env);assert.equal(published.status,201);const second=(await published.json()).item;assert.equal(second.revisionNumber,2);
 const list=await (await worker.fetch(new Request('https://library/v1/families'),h.env)).json();assert.deepEqual(list.items.map(x=>x.id),[second.id]);
 const history=await (await worker.fetch(new Request(`https://library/v1/families/${second.id}/versions`,{headers:{'X-Owner-Token':'a'.repeat(64)}}),h.env)).json();assert.deepEqual(history.items.map(x=>x.revisionNumber),[2,1]);assert.equal(history.items[0].changeNote,'Paramètres corrigés');
 assert.equal((await worker.fetch(new Request(`https://library/v1/families/${first.id}/file`),h.env)).status,200);
});


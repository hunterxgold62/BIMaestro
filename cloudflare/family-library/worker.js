import {reserve,requestCost} from './quota.js';
export const MAX_UPLOAD_BYTES=20_000_000;
const MAGIC=[0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1];
function json(value,status=200){return Response.json(value,{status,headers:{'Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}});}
function fail(code,message,status){return json({error:code,message},status);}
function metadata(request,key,max,required=true){
 let value=decodeURIComponent(request.headers.get(key)||'').normalize('NFC').trim();
 if((required&&!value)||value.length>max||/[\u0000-\u001f\u007f]/u.test(value))throw new Error('metadata');
 return value;
}
export async function readBounded(request,size,timeoutMs=90_000){
 if(!request.body)throw new Error('body');
 const output=new Uint8Array(size);let offset=0;const reader=request.body.getReader();
 let timer;
 const deadline=new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error('upload_timeout')),timeoutMs);});
 try{while(true){const {done,value}=await Promise.race([reader.read(),deadline]);if(done)break;
 if(offset+value.length>size)throw new Error('size');output.set(value,offset);offset+=value.length;
 }if(offset!==size||MAGIC.some((v,i)=>output[i]!==v))throw new Error('format');return output;
 }catch(e){await reader.cancel().catch(()=>{});throw e;}finally{clearTimeout(timer);reader.releaseLock();}
}
async function sha256(bytes){return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),b=>b.toString(16).padStart(2,'0')).join('');}
function item(row,ownerHash=''){return {id:row.id,name:row.name,category:row.category,revitVersion:row.revit_version,
 description:row.description,sizeBytes:row.size_bytes,createdAt:row.created_at,downloadCount:row.download_count,
 origin:row.origin,isOwner:!!ownerHash&&!!row.owner_hash&&ownerHash===row.owner_hash};}
export default {async fetch(request,env){
 // Deployment switch blocks without even touching D1. No shared secret in desktop clients.
 if(env.LIBRARY_ENABLED!=='true')return fail('library_paused','Bibliothèque temporairement fermée.',503);
 const url=new URL(request.url);let kind,id,size=0,meta;
 if(url.pathname==='/v1/families'&&request.method==='GET')kind='list';
 else if(url.pathname==='/v1/families'&&request.method==='POST')kind='upload';
 else if(request.method==='GET'&&/^\/v1\/families\/[a-f0-9]{64}\/file$/.test(url.pathname)){kind='download';id=url.pathname.split('/')[3];}
 else if(request.method==='DELETE'&&/^\/v1\/families\/[a-f0-9]{64}$/.test(url.pathname)){kind='withdraw';id=url.pathname.split('/')[3];}
 else return fail('not_found','Adresse inconnue.',404);
 const ownerToken=request.headers.get('X-Owner-Token')||'';
 if((ownerToken&&!/^[a-f0-9]{64}$/.test(ownerToken))||(!ownerToken&&(kind==='upload'||kind==='withdraw'||url.searchParams.get('mine')==='1')))
  return fail('invalid_owner_token','La clé personnelle de publication est absente ou invalide. Mettez BIMaestro à jour.',400);
 if(kind==='upload'){
  const length=request.headers.get('Content-Length');size=Number(length);
  if(!length||!/^\d+$/.test(length)||!Number.isSafeInteger(size)||size<512||size>MAX_UPLOAD_BYTES)
   return fail('invalid_size','Fichier attendu entre 512 octets et 20 Mo.',413);
  try{meta={name:metadata(request,'X-Family-Name',120),category:metadata(request,'X-Family-Category',80),
   description:metadata(request,'X-Family-Description',1000,false),version:Number(request.headers.get('X-Revit-Version')),origin:request.headers.get('X-Family-Origin')};
   if(!['personal','ai'].includes(meta.origin))throw new Error('origin');
   if(!Number.isInteger(meta.version)||meta.version<2020||meta.version>2100||!/[\p{L}\p{N}]/u.test(meta.category))throw new Error('version');
  }catch{return fail('invalid_metadata','Informations de famille invalides.',400);}
 }
 try{
  const ownerHash=ownerToken?await sha256(new TextEncoder().encode(ownerToken)):'';
  if(!await reserve(env.CATALOG,requestCost(kind,size)))return fail('quota_exceeded','Bibliothèque fermée : seuil de sécurité atteint ou contrôle expiré.',429);
  if(kind==='withdraw'){
   // Soft withdrawal preserves the reserved storage budget and never deletes another owner's data.
   const removed=await env.CATALOG.prepare('UPDATE shared_families SET withdrawn=1 WHERE id=? AND owner_hash=? AND owner_hash<>\'\' RETURNING id').bind(id,ownerHash).first();
   return removed?new Response(null,{status:204,headers:{'Cache-Control':'no-store'}}):fail('not_owner','Seul l’auteur peut retirer cette famille.',403);
  }
  if(kind==='list'){
   const cursor=url.searchParams.get('cursor')||'';
   if(cursor&&!/^[a-f0-9]{64}$/.test(cursor))return fail('invalid_cursor','Curseur invalide.',400);
   const q=(url.searchParams.get('q')||'').toLocaleLowerCase().slice(0,120);
   const category=(url.searchParams.get('category')||'').slice(0,80);
   const version=url.searchParams.get('revitVersion');
   const origin=url.searchParams.get('origin')||null;const mine=url.searchParams.get('mine')==='1';
   if(origin!==null&&!['personal','ai'].includes(origin))return fail('invalid_origin','Origine invalide.',400);
   if(version!==null&&(!/^\d{4}$/.test(version)||Number(version)<2023||Number(version)>2100))
    return fail('invalid_version','Version Revit invalide.',400);
   // Bound scans even for search: filter an indexed page in memory, return its cursor.
   const page=await env.CATALOG.prepare('SELECT * FROM shared_families WHERE id>? ORDER BY id LIMIT 101').bind(cursor).all();
   const rows=page.results.slice(0,100);
   return json({items:rows.filter(r=>r.status==='ready'&&!r.withdrawn&&(!origin||r.origin===origin)&&(!mine||(ownerHash&&r.owner_hash===ownerHash))&&(!q||`${r.name} ${r.description}`.toLocaleLowerCase().includes(q))&&(!category||r.category===category)&&(!version||(r.revit_version>=2023&&r.revit_version<=Number(version)))).map(r=>item(r,ownerHash)),
    nextCursor:page.results.length>100?rows.at(-1).id:null});
  }
  if(kind==='download'){
   const row=await env.CATALOG.prepare("SELECT * FROM shared_families WHERE id=? AND status='ready' AND withdrawn=0").bind(id).first();
   if(!row)return fail('not_found','Famille introuvable.',404);
   const object=await env.FAMILIES.get(`families/${id}.rfa`);
   if(!object)return fail('unavailable','Fichier indisponible.',503);
   // Count server deliveries, not unique users or confirmed complete transfers.
   // Cached local reuse does not call this endpoint. Increment is atomic.
   const deliver=await env.CATALOG.prepare("UPDATE shared_families SET download_count=download_count+1 WHERE id=? AND status='ready' AND withdrawn=0 RETURNING id").bind(id).first();
   if(!deliver){await object.body?.cancel?.();return fail('not_found','Famille retirée.',404);}
   return new Response(object.body,{headers:{'Content-Type':'application/octet-stream','Content-Length':String(object.size),
    'Content-Disposition':`attachment; filename="${id}.rfa"`,'Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}});
  }
  let bytes;try{bytes=await readBounded(request,size);}catch{return fail('invalid_file','Fichier Revit invalide ou taille incorrecte.',400);}
  id=await sha256(bytes);
  const existing=await env.CATALOG.prepare('SELECT * FROM shared_families WHERE id=?').bind(id).first();
  if(existing){if(existing.withdrawn)return fail('family_withdrawn','Cette famille a été retirée de la bibliothèque.',409);
   return existing.status==='ready'?json({item:item(existing,ownerHash),duplicate:true}):fail('upload_pending','Ce fichier est déjà en cours de traitement.',409);}
  const createdAt=new Date().toISOString();
  const inserted=await env.CATALOG.prepare("INSERT OR IGNORE INTO shared_families(id,name,category,revit_version,description,size_bytes,created_at,status,origin,owner_hash) VALUES(?,?,?,?,?,?,?,'pending',?,?) RETURNING id")
   .bind(id,meta.name,meta.category,meta.version,meta.description,size,createdAt,meta.origin,ownerHash).first();
  if(!inserted)return fail('upload_pending','Ce fichier est déjà en cours de traitement.',409);
  // A crash leaves a pending row and its reservation; no blind retries or refunds.
  await env.FAMILIES.put(`families/${id}.rfa`,bytes,{httpMetadata:{contentType:'application/octet-stream'}});
  await env.CATALOG.prepare("UPDATE shared_families SET status='ready' WHERE id=?").bind(id).run();
  return json({item:{id,name:meta.name,category:meta.category,revitVersion:meta.version,description:meta.description,sizeBytes:size,createdAt,downloadCount:0,origin:meta.origin,isOwner:true},duplicate:false},201);
 }catch{return fail('service_unavailable','Service indisponible ; aucun nouvel accès autorisé.',503);}
}};


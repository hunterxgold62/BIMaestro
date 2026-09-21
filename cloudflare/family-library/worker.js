import {reserve,requestCost} from './quota.js';
export const MAX_UPLOAD_BYTES=20_000_000;
export const MAX_PREVIEW_BYTES=1_000_000;
const MAGIC=[0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1];
const PNG_MAGIC=[0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a];
function json(value,status=200){return Response.json(value,{status,headers:{'Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}});}
function fail(code,message,status){return json({error:code,message},status);}
function metadata(request,key,max,required=true){
 let value=decodeURIComponent(request.headers.get(key)||'').normalize('NFC').trim();
 if((required&&!value)||value.length>max||/[\u0000-\u001f\u007f]/u.test(value))throw new Error('metadata');
 return value;
}
export async function readBounded(request,size,timeoutMs=90_000,magic=MAGIC){
 if(!request.body)throw new Error('body');
 const output=new Uint8Array(size);let offset=0;const reader=request.body.getReader();
 let timer;
 const deadline=new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error('upload_timeout')),timeoutMs);});
 try{while(true){const {done,value}=await Promise.race([reader.read(),deadline]);if(done)break;
 if(offset+value.length>size)throw new Error('size');output.set(value,offset);offset+=value.length;
 }if(offset!==size||magic.some((v,i)=>output[i]!==v))throw new Error('format');return output;
 }catch(e){await reader.cancel().catch(()=>{});throw e;}finally{clearTimeout(timer);reader.releaseLock();}
}
async function sha256(bytes){return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),b=>b.toString(16).padStart(2,'0')).join('');}
function item(row,ownerHash=''){return {id:row.id,name:row.name,category:row.category,revitVersion:row.revit_version,
 description:row.description,sizeBytes:row.size_bytes,createdAt:row.created_at,downloadCount:row.download_count,
 origin:row.origin,creatorName:row.creator_name||null,revisionNumber:row.revision_number||1,changeNote:row.change_note||'',hasPreview:row.has_preview===1,isOwner:!!ownerHash&&!!row.owner_hash&&ownerHash===row.owner_hash};}
export default {async fetch(request,env){
 // Deployment switch blocks without even touching D1. No shared secret in desktop clients.
 if(env.LIBRARY_ENABLED!=='true')return fail('library_paused','Bibliothèque temporairement fermée.',503);
 const url=new URL(request.url);let kind,id,size=0,meta;
 if(url.pathname==='/v1/families'&&request.method==='GET')kind='list';
 else if(url.pathname==='/v1/families'&&request.method==='POST')kind='upload';
 else if(request.method==='GET'&&/^\/v1\/families\/[a-f0-9]{64}\/file$/.test(url.pathname)){kind='download';id=url.pathname.split('/')[3];}
 else if(request.method==='GET'&&/^\/v1\/families\/[a-f0-9]{64}\/preview$/.test(url.pathname)){kind='previewDownload';id=url.pathname.split('/')[3];}
 else if(request.method==='PUT'&&/^\/v1\/families\/[a-f0-9]{64}\/preview$/.test(url.pathname)){kind='previewUpload';id=url.pathname.split('/')[3];}
 else if(request.method==='GET'&&/^\/v1\/families\/[a-f0-9]{64}\/versions$/.test(url.pathname)){kind='versions';id=url.pathname.split('/')[3];}
 else if(request.method==='PATCH'&&/^\/v1\/families\/[a-f0-9]{64}$/.test(url.pathname)){kind='edit';id=url.pathname.split('/')[3];}
 else if(request.method==='DELETE'&&/^\/v1\/families\/[a-f0-9]{64}$/.test(url.pathname)){kind='withdraw';id=url.pathname.split('/')[3];}
 else return fail('not_found','Adresse inconnue.',404);
 const ownerToken=request.headers.get('X-Owner-Token')||'';
 if((ownerToken&&!/^[a-f0-9]{64}$/.test(ownerToken))||(!ownerToken&&(kind==='upload'||kind==='previewUpload'||kind==='withdraw'||kind==='edit'||url.searchParams.get('mine')==='1')))
  return fail('invalid_owner_token','La clé personnelle de publication est absente ou invalide. Mettez BIMaestro à jour.',400);
 if(kind==='upload'){
  const length=request.headers.get('Content-Length');size=Number(length);
  if(!length||!/^\d+$/.test(length)||!Number.isSafeInteger(size)||size<512||size>MAX_UPLOAD_BYTES)
   return fail('invalid_size','Fichier attendu entre 512 octets et 20 Mo.',413);
  try{meta={name:metadata(request,'X-Family-Name',120),category:metadata(request,'X-Family-Category',80),
   description:metadata(request,'X-Family-Description',1000,false),creatorName:metadata(request,'X-Creator-Name',120,false),changeNote:metadata(request,'X-Change-Note',500,false),replaces:request.headers.get('X-Replaces-Family-ID')||'',version:Number(request.headers.get('X-Revit-Version')),origin:request.headers.get('X-Family-Origin')};
   if(!['personal','ai'].includes(meta.origin))throw new Error('origin');
   if(meta.replaces&&!/^[a-f0-9]{64}$/.test(meta.replaces))throw new Error('replaces');
   if(!Number.isInteger(meta.version)||meta.version<2020||meta.version>2100||!/[\p{L}\p{N}]/u.test(meta.category))throw new Error('version');
  }catch{return fail('invalid_metadata','Informations de famille invalides.',400);}
 }
 if(kind==='edit')try{meta={name:metadata(request,'X-Family-Name',120),category:metadata(request,'X-Family-Category',80),description:metadata(request,'X-Family-Description',1000,false),origin:request.headers.get('X-Family-Origin')};if(!['personal','ai'].includes(meta.origin)||!/[\p{L}\p{N}]/u.test(meta.category))throw new Error('metadata');}catch{return fail('invalid_metadata','Informations de famille invalides.',400);}
 if(kind==='previewUpload'){
  const length=request.headers.get('Content-Length');size=Number(length);
  if(!length||!/^\d+$/.test(length)||!Number.isSafeInteger(size)||size<PNG_MAGIC.length||size>MAX_PREVIEW_BYTES||request.headers.get('Content-Type')!=='image/png')
   return fail('invalid_preview','Aperçu PNG attendu (1 Mo maximum).',413);
 }
 try{
  const ownerHash=ownerToken?await sha256(new TextEncoder().encode(ownerToken)):'';
  if(!await reserve(env.CATALOG,requestCost(kind,size)))return fail('quota_exceeded','Bibliothèque fermée : seuil de sécurité atteint ou contrôle expiré.',429);
  if(kind==='withdraw'){
   // Soft withdrawal preserves the reserved storage budget and never deletes another owner's data.
   const removed=await env.CATALOG.prepare('UPDATE shared_families SET withdrawn=1 WHERE id=? AND owner_hash=? AND owner_hash<>\'\' RETURNING id').bind(id,ownerHash).first();
   return removed?new Response(null,{status:204,headers:{'Cache-Control':'no-store'}}):fail('not_owner','Seul l’auteur peut retirer cette famille.',403);
  }
  if(kind==='edit'){
   const updated=await env.CATALOG.prepare("UPDATE shared_families SET name=?,category=?,description=?,origin=? WHERE id=? AND owner_hash=? AND status='ready' AND withdrawn=0 RETURNING *").bind(meta.name,meta.category,meta.description,meta.origin,id,ownerHash).first();
   return updated?json({item:item(updated,ownerHash)}):fail('not_owner','Seul l’auteur peut modifier cette famille.',403);
  }
  if(kind==='versions'){
   const current=await env.CATALOG.prepare("SELECT * FROM shared_families WHERE id=? AND status='ready' AND withdrawn=0").bind(id).first();
   if(!current)return fail('not_found','Famille introuvable.',404);
   const group=current.family_group_id||current.id;
   const versions=await env.CATALOG.prepare("SELECT * FROM shared_families WHERE (id=? OR family_group_id=?) AND status='ready' AND withdrawn=0 ORDER BY revision_number DESC LIMIT 50").bind(group,group).all();
   return json({items:versions.results.map(r=>item(r,ownerHash))});
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
   const page=await env.CATALOG.prepare('SELECT * FROM shared_families WHERE id>? AND superseded=0 ORDER BY id LIMIT 101').bind(cursor).all();
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
  if(kind==='previewDownload'){
   const row=await env.CATALOG.prepare("SELECT id FROM shared_families WHERE id=? AND status='ready' AND withdrawn=0 AND has_preview=1").bind(id).first();
   if(!row)return fail('not_found','Aperçu introuvable.',404);
   const object=await env.FAMILIES.get(`previews/${id}.png`);
   if(!object)return fail('unavailable','Aperçu indisponible.',503);
   return new Response(object.body,{headers:{'Content-Type':'image/png','Content-Length':String(object.size),'Cache-Control':'public, max-age=300','X-Content-Type-Options':'nosniff'}});
  }
  if(kind==='previewUpload'){
   const row=await env.CATALOG.prepare("SELECT id FROM shared_families WHERE id=? AND owner_hash=? AND status='ready' AND withdrawn=0").bind(id,ownerHash).first();
   if(!row)return fail('not_owner','Seul l’auteur peut ajouter cet aperçu.',403);
   let bytes;try{bytes=await readBounded(request,size,90_000,PNG_MAGIC);}catch{return fail('invalid_preview','Aperçu PNG invalide ou taille incorrecte.',400);}
   if(PNG_MAGIC.some((v,i)=>bytes[i]!==v))return fail('invalid_preview','Aperçu PNG invalide.',400);
   await env.FAMILIES.put(`previews/${id}.png`,bytes,{httpMetadata:{contentType:'image/png',cacheControl:'public, max-age=300'}});
   await env.CATALOG.prepare('UPDATE shared_families SET has_preview=1 WHERE id=?').bind(id).run();
   return new Response(null,{status:204,headers:{'Cache-Control':'no-store'}});
  }
  let bytes;try{bytes=await readBounded(request,size);}catch{return fail('invalid_file','Fichier Revit invalide ou taille incorrecte.',400);}
  id=await sha256(bytes);
  const existing=await env.CATALOG.prepare('SELECT * FROM shared_families WHERE id=?').bind(id).first();
  if(existing){
   if(existing.withdrawn){
    if(!ownerHash||!existing.owner_hash||existing.owner_hash!==ownerHash)return fail('family_withdrawn','Cette famille a été retirée par son auteur et ne peut être réactivée que par lui.',409);
    const restored=await env.CATALOG.prepare("UPDATE shared_families SET name=?,category=?,revit_version=?,description=?,origin=?,creator_name=?,withdrawn=0 WHERE id=? AND owner_hash=? AND status='ready' RETURNING *")
     .bind(meta.name,meta.category,meta.version,meta.description,meta.origin,meta.creatorName,id,ownerHash).first();
    return restored?json({item:item(restored,ownerHash),duplicate:true,restored:true}):fail('service_unavailable','Impossible de réactiver cette famille.',503);
   }
   if(existing.status==='ready'&&ownerHash&&existing.owner_hash===ownerHash&&meta.creatorName&&existing.creator_name!==meta.creatorName){
    const refreshed=await env.CATALOG.prepare("UPDATE shared_families SET creator_name=? WHERE id=? AND owner_hash=? RETURNING *").bind(meta.creatorName,id,ownerHash).first();
    return json({item:item(refreshed,ownerHash),duplicate:true,creatorUpdated:true});
   }
   return existing.status==='ready'?json({item:item(existing,ownerHash),duplicate:true}):fail('upload_pending','Ce fichier est déjà en cours de traitement.',409);}
  let replacesRow=null,groupId='',revision=1;
  if(meta.replaces){replacesRow=await env.CATALOG.prepare("SELECT * FROM shared_families WHERE id=? AND owner_hash=? AND status='ready' AND withdrawn=0 AND superseded=0").bind(meta.replaces,ownerHash).first();if(!replacesRow)return fail('not_owner','La publication remplacée est introuvable ou ne vous appartient pas.',403);groupId=replacesRow.family_group_id||replacesRow.id;const latest=await env.CATALOG.prepare('SELECT MAX(revision_number) AS revision FROM shared_families WHERE id=? OR family_group_id=?').bind(groupId,groupId).first();revision=(latest?.revision||1)+1;}
  const createdAt=new Date().toISOString();
  const inserted=await env.CATALOG.prepare("INSERT OR IGNORE INTO shared_families(id,name,category,revit_version,description,size_bytes,created_at,status,origin,owner_hash,creator_name,family_group_id,revision_number,change_note) VALUES(?,?,?,?,?,?,?,'pending',?,?,?,?,?,?) RETURNING id")
   .bind(id,meta.name,meta.category,meta.version,meta.description,size,createdAt,meta.origin,ownerHash,meta.creatorName,groupId,revision,meta.changeNote).first();
  if(!inserted)return fail('upload_pending','Ce fichier est déjà en cours de traitement.',409);
  // A crash leaves a pending row and its reservation; no blind retries or refunds.
  await env.FAMILIES.put(`families/${id}.rfa`,bytes,{httpMetadata:{contentType:'application/octet-stream'}});
  await env.CATALOG.prepare("UPDATE shared_families SET status='ready' WHERE id=?").bind(id).run();
  if(replacesRow)await env.CATALOG.prepare('UPDATE shared_families SET superseded=1 WHERE id=? AND owner_hash=?').bind(replacesRow.id,ownerHash).run();
  return json({item:{id,name:meta.name,category:meta.category,revitVersion:meta.version,description:meta.description,sizeBytes:size,createdAt,downloadCount:0,origin:meta.origin,creatorName:meta.creatorName,revisionNumber:revision,changeNote:meta.changeNote,hasPreview:false,isOwner:true},duplicate:false,newVersion:!!replacesRow},201);
 }catch{return fail('service_unavailable','Service indisponible ; aucun nouvel accès autorisé.',503);}
}};


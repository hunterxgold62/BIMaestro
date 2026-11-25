// Fixed, explicit maintenance operations for the two BIMaestro Workers only.
// Uses the approved existing Wrangler identity; never emits credentials.
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {homedir} from 'node:os';
import {join} from 'node:path';
import {createHash} from 'node:crypto';
import {METRICS,FREE_LIMITS} from './quota.js';
const account='fafe3694a73c1a868a3d02097f0d6448',database='1918916c-c5cb-4165-8db2-0532291d859c';
const names=['bimaestro-family-library','bimaestro-maquette-pilot'];
const root=new URL('../../',import.meta.url);
const output=new URL('output/quota-staging/',root);
await mkdir(output,{recursive:true});
const config=await readFile(join(process.env.APPDATA||join(homedir(),'AppData/Roaming'),'xdg.config/.wrangler/config/default.toml'),'utf8');
const token=config.match(/^oauth_token\s*=\s*"([^"]+)"/m)?.[1];
if(!token)throw new Error('Reconnect Wrangler');
async function raw(path,options={}) {
 const response=await fetch('https://api.cloudflare.com/client/v4/'+path,{
  ...options,headers:{Authorization:'Bearer '+token,'Content-Type':'application/json'},signal:AbortSignal.timeout(30000)});
 if(!response.ok){const failure=await response.json().catch(()=>({}));throw new Error(`Cloudflare ${response.status}: ${path}: ${JSON.stringify(failure.errors||[])}`);}
 return response;
}
async function api(path,options={}){const json=await(await raw(path,options)).json();if(json.success===false)throw new Error('Cloudflare refused operation');return json.result;}
async function sql(statement){return api(`accounts/${account}/d1/database/${database}/query`,{method:'POST',body:JSON.stringify({sql:statement})});}
const mode=process.argv[2];
if(mode==='backup'){
 const summary=[];
 for(const name of names){
  const settings=await api(`accounts/${account}/workers/scripts/${name}/settings`);
  const source=await raw(`accounts/${account}/workers/scripts/${name}`);
  const bytes=new Uint8Array(await source.arrayBuffer());
  await writeFile(new URL(name+'.source-backup',output),bytes);
  await writeFile(new URL(name+'.settings-backup.json',output),JSON.stringify({settings,contentType:source.headers.get('content-type')},null,2));
  summary.push({name,sourceBytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex'),bindings:settings.bindings.map(b=>({name:b.name,type:b.type}))});
 }
 console.log(JSON.stringify(summary,null,2));
}else if(mode==='pause'||mode==='resume'){
 for(const name of names)console.log(JSON.stringify({name,...await api(`accounts/${account}/workers/scripts/${name}/subdomain`,{method:'POST',body:JSON.stringify({enabled:mode==='resume',previews_enabled:false})})}));
}else if(mode==='migrate'){
 const columns=(await sql('PRAGMA table_info(quota_policy);'))[0].results;
 if(!columns.some(c=>c.name==='scope'))console.log(JSON.stringify(await sql((await readFile(new URL('./migrations/0007_account_quota_scope.sql',import.meta.url),'utf8')).replace(/^\s*--.*$/gm,'').trim())));
 else console.log('Scope column already present; no change');
}else if(mode==='seed'){
 const report=JSON.parse(await readFile(new URL('output/account-quota-audit-2026-11-23.json',root),'utf8'));
 if(Date.now()-Date.parse(report.checkedAt)>15*60000||report.account!==account)throw new Error('Fresh account audit required');
 if(report.scripts.map(x=>x.id).sort().join(',')!==[...names].sort().join(','))throw new Error('Unexpected Workers inventory');
 const buckets=[...new Set(report.analytics.r2StorageAdaptiveGroups.map(x=>x.dimensions.bucketName))].sort();
 if(buckets.join(',')!=='bimaestro-families,bimaestro-maquettes,bimaestro-maquettes-test')throw new Error('Unexpected R2 inventory');
 const results=await sql('SELECT * FROM quota_policy; SELECT * FROM quota_state;');
 const policy=results[0].results[0],state=results[1].results[0];
 if(policy.scope==='account')throw new Error('Already reconciled: seed is never replayed');
 if(policy.enabled!==1||policy.threshold_percent!==80||policy.verified_until<=new Date().toISOString())throw new Error('Policy not eligible');
 // Conservatively ADD the account-wide observations, even where already counted.
 // No credits/refunds for failures, overwrites, old reservations or removed objects.
 const added={...report.totals};
 // Extra conservative reserves for audit/deployment administrative accesses.
 added.r2_a+=100;added.r2_b+=100;added.worker_requests+=100;
 added.d1_reads+=4096;added.d1_writes+=64;added.d1_bytes+=65536;
 for(const metric of METRICS){
  if(!Number.isSafeInteger(added[metric])||added[metric]<0||!Number.isSafeInteger(state[metric])||state[metric]<0||
    !Number.isSafeInteger(policy[metric+'_limit'])||policy[metric+'_limit']>FREE_LIMITS[metric]||policy[metric+'_limit']<1||
    state[metric]+added[metric]>Math.floor(policy[metric+'_limit']*.8))throw new Error('Unsafe reconciliation: '+metric);
 }
 const conditions=METRICS.map(k=>`${k}=${state[k]}`).join(' AND ');
 const update=`UPDATE quota_state SET ${METRICS.map(k=>`${k}=${k}+${added[k]}`).join(',')} WHERE id=1 AND ${conditions} AND EXISTS(SELECT 1 FROM quota_policy WHERE id=1 AND enabled=1 AND threshold_percent=80 AND scope='unverified') RETURNING *;`;
 const seeded=(await sql(update))[0].results;
 if(seeded.length!==1)throw new Error('Concurrent change: reconciliation refused');
 const enabled=(await sql("UPDATE quota_policy SET scope='account' WHERE id=1 AND enabled=1 AND threshold_percent=80 AND scope='unverified' RETURNING *;"))[0].results;
 if(enabled.length!==1)throw new Error('Scope activation refused');
 const record={checkedAt:report.checkedAt,seededAt:new Date().toISOString(),added,before:state,after:seeded[0],policy:enabled[0]};
 await writeFile(new URL('baseline-record.json',output),JSON.stringify(record,null,2));
 console.log(JSON.stringify(record,null,2));
}else if(mode==='verify'){
 console.log(JSON.stringify(await sql('SELECT * FROM quota_policy; SELECT * FROM quota_state;'),null,2));
 for(const name of names){const settings=await api(`accounts/${account}/workers/scripts/${name}/settings`);console.log(JSON.stringify({name,bindings:settings.bindings}));}
}else throw new Error('Choose backup, pause, migrate, seed, resume or verify');

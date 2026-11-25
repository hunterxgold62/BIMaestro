// Read-only reconciliation for the shared quota. Never prints OAuth credentials.
// Run with the existing Wrangler session: node cloudflare/family-library/account-audit.mjs
import { readFile, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join } from 'node:path';
const account = 'fafe3694a73c1a868a3d02097f0d6448';
const database = '1918916c-c5cb-4165-8db2-0532291d859c';
const config = await readFile(join(process.env.APPDATA || join(homedir(),'AppData/Roaming'), 'xdg.config/.wrangler/config/default.toml'), 'utf8');
const token = config.match(/^oauth_token\s*=\s*"([^"]+)"/m)?.[1];
if (!token) throw new Error('Reconnect Wrangler before auditing');
async function api(route, options = {}) {
  const response = await fetch('https://api.cloudflare.com/client/v4/' + route, {
    ...options, headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
    signal: AbortSignal.timeout(30000),
  });
  const result = await response.json();
  if (!response.ok || result.success === false || result.errors?.length) throw new Error(`Cloudflare ${response.status}: ${route.split('?')[0]}`);
  return result;
}
const now = new Date();
// A rolling 31-day window also covers billing cycles that do not start on day 1.
const recent = new Date(now.getTime() - 48*3600000).toISOString();
const month = new Date(now.getTime() - 31*86400000 + 60000).toISOString();
const query = `{viewer{accounts(filter:{accountTag:"${account}"}){
 r2OperationsAdaptiveGroups(limit:1000,filter:{datetime_geq:"${month}",datetime_lt:"${now.toISOString()}"}){dimensions{actionType}sum{requests}}
 r2StorageAdaptiveGroups(limit:1000,filter:{datetime_geq:"${month}",datetime_lt:"${now.toISOString()}"}){dimensions{bucketName storageClass}max{payloadSize metadataSize objectCount}}
 workersInvocationsAdaptive(limit:1000,filter:{datetime_geq:"${recent}",datetime_lt:"${now.toISOString()}"}){sum{requests errors}}
 d1AnalyticsAdaptiveGroups(limit:1000,filter:{datetime_geq:"${recent}",datetime_lt:"${now.toISOString()}"}){sum{rowsRead rowsWritten}}
}}}`;
const analytics = (await api('graphql', { method:'POST', body:JSON.stringify({query}) })).data?.viewer?.accounts;
if (!Array.isArray(analytics) || analytics.length !== 1) throw new Error('Incomplete account analytics');
const usage = analytics[0];
for (const key of ['r2OperationsAdaptiveGroups','r2StorageAdaptiveGroups','workersInvocationsAdaptive','d1AnalyticsAdaptiveGroups'])
  if (!Array.isArray(usage[key]) || usage[key].length >= 1000) throw new Error('Incomplete analytics: '+key);
const classA = new Set(['ListBuckets','PutBucket','ListObjects','PutObject','CopyObject','CompleteMultipartUpload','CreateMultipartUpload','LifecycleStorageTierTransition','ListMultipartUploads','UploadPart','UploadPartCopy','ListParts','PutBucketEncryption','PutBucketCors','PutBucketLifecycleConfiguration']);
const classB = new Set(['HeadBucket','HeadObject','GetObject','UsageSummary','GetBucketEncryption','GetBucketLocation','GetBucketCors','GetBucketLifecycleConfiguration']);
const free = new Set(['DeleteObject','DeleteObjects','DeleteBucket','AbortMultipartUpload']);
function integer(value) { if(!Number.isSafeInteger(value)||value<0)throw new Error('Invalid metric'); return value; }
const totals = {r2_bytes:0,r2_a:0,r2_b:0,worker_requests:0,d1_reads:0,d1_writes:0,d1_bytes:0};
for (const row of usage.r2OperationsAdaptiveGroups) {
  const action=row.dimensions.actionType, count=integer(row.sum.requests);
  if(classA.has(action))totals.r2_a+=count;
  else if(classB.has(action))totals.r2_b+=count;
  else if(!free.has(action))throw new Error('Unclassified R2 operation: '+action);
}
for (const row of usage.r2StorageAdaptiveGroups) {
  const size=integer(row.max.payloadSize)+integer(row.max.metadataSize);
  if(row.dimensions.storageClass!=='Standard' && (size>0||integer(row.max.objectCount)>0))throw new Error('Non-free R2 storage class');
  totals.r2_bytes+=size;
}
for(const row of usage.workersInvocationsAdaptive)totals.worker_requests+=integer(row.sum.requests);
for(const row of usage.d1AnalyticsAdaptiveGroups){totals.d1_reads+=integer(row.sum.rowsRead);totals.d1_writes+=integer(row.sum.rowsWritten);}
const databases = await api(`accounts/${account}/d1/database?per_page=1000`);
if(databases.result_info?.total_count!==databases.result.length)throw new Error('Incomplete D1 inventory');
for(const db of databases.result)totals.d1_bytes+=integer(db.file_size);
const scripts = (await api(`accounts/${account}/workers/scripts`)).result.map(s=>({id:s.id,modified_on:s.modified_on}));
const state = (await api(`accounts/${account}/d1/database/${database}/query`, {
 method:'POST', body:JSON.stringify({sql:'SELECT * FROM quota_policy; SELECT * FROM quota_state;'}),
})).result;
let buckets, bucketInventoryError;
try { buckets=(await api(`accounts/${account}/r2/buckets`)).result.buckets; }
catch(error){bucketInventoryError=error.message;}
let subscriptions, subscriptionError;
try { subscriptions=(await api(`accounts/${account}/subscriptions`)).result.map(s=>({state:s.state,rate_plan:s.rate_plan?.id,price:s.price})); }
catch(error){subscriptionError=error.message;}
const report={checkedAt:now.toISOString(),account,database,windows:{operationsSince:month,dailyConservativeSince:recent},
 totals,analytics:usage,databases:databases.result.map(d=>({uuid:d.uuid,file_size:d.file_size})),scripts,buckets,bucketInventoryError,subscriptions,subscriptionError,
 policy:state[0]?.results?.[0],reserved:state[1]?.results?.[0]};
if(process.argv[2])await writeFile(process.argv[2],JSON.stringify(report,null,2));
console.log(JSON.stringify(report,null,2));

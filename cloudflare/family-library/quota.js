// No automatic resets: an operator must reconcile ALL account usage first.
// Costs are charged before work and never refunded, including failures/retries.
export const METRICS = ['r2_bytes','r2_a','r2_b','worker_requests','d1_reads','d1_writes','d1_bytes'];
export const FREE_LIMITS = {r2_bytes:10_000_000_000,r2_a:1_000_000,r2_b:10_000_000,worker_requests:100_000,d1_reads:5_000_000,d1_writes:100_000,d1_bytes:500_000_000};
export const R2_OBJECT_OVERHEAD=4096; // Conservative key/metadata allowance per PUT.
export const RESERVE_SQL = `UPDATE quota_state SET ${METRICS.map(k=>`${k}=${k}+?`).join(',')}
 WHERE id=1 AND EXISTS(SELECT 1 FROM quota_policy p WHERE p.id=1 AND p.enabled=1
 AND p.scope='account' AND p.threshold_percent BETWEEN 1 AND 80 AND p.verified_until>?
 AND ${METRICS.map(k=>`typeof(quota_state.${k})='integer' AND quota_state.${k} BETWEEN 0 AND ${FREE_LIMITS[k]} AND typeof(p.${k}_limit)='integer' AND p.${k}_limit BETWEEN 1 AND ${FREE_LIMITS[k]} AND quota_state.${k}+?<=CAST(p.${k}_limit*p.threshold_percent/100 AS INTEGER)`).join(' AND ')}) RETURNING id`;
export async function reserve(db, costs, now = new Date().toISOString()) {
 const values = METRICS.map(k=>costs[k] ?? 0);
 if(values.some(v=>!Number.isSafeInteger(v)||v<0)) throw new Error('Invalid quota reservation');
 return !!(await db.prepare(RESERVE_SQL).bind(...values,now,...values).first());
}
export function requestCost(kind, bytes=0) {
 // One row is updated for the quota itself. Business updates add another row.
 // Uploads retain the larger allowance for inserts, indexes and version changes.
 const writes={list:1,versions:1,previewDownload:1,download:2,previewUpload:2,withdraw:2,edit:2,upload:16};
 if(!Object.hasOwn(writes,kind))throw new Error('Unknown library operation');
 return {worker_requests:1,d1_reads:256,d1_writes:writes[kind],
  r2_a:(kind==='upload'||kind==='previewUpload')?1:0,r2_b:(kind==='download'||kind==='previewDownload')?1:0,
  r2_bytes:(kind==='upload'||kind==='previewUpload')?bytes+R2_OBJECT_OVERHEAD:0,d1_bytes:kind==='upload'?16384:0};
}

// Every model request reserves in the SAME CATALOG/quota_state as the library.
// Failed, duplicate and overwritten uploads are never refunded.
export function modelRequestCost(kind,{bytes=0,markerBytes=0,count=1}={}) {
 if(!['upload','download','verify','delete'].includes(kind)||
  ![bytes,markerBytes,count].every(Number.isSafeInteger)||bytes<0||markerBytes<0||count<1||count>1000)
  throw new Error('Invalid model reservation');
 return {worker_requests:1,d1_reads:16,d1_writes:1,
  r2_bytes:kind==='upload'?bytes+markerBytes+R2_OBJECT_OVERHEAD*(markerBytes>0?2:1):0,
  r2_a:kind==='upload'?(markerBytes>0?2:1):kind==='delete'?1:0,
  r2_b:kind==='download'?1:kind==='verify'?count:0};
}

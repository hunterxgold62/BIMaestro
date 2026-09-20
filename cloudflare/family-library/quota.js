// No automatic resets: an operator must reconcile ALL account usage first.
// Costs are charged before work and never refunded, including failures/retries.
export const METRICS = ['r2_bytes','r2_a','r2_b','worker_requests','d1_reads','d1_writes','d1_bytes'];
export const FREE_LIMITS = {r2_bytes:10_000_000_000,r2_a:1_000_000,r2_b:10_000_000,worker_requests:100_000,d1_reads:5_000_000,d1_writes:100_000,d1_bytes:500_000_000};
export const RESERVE_SQL = `UPDATE quota_state SET ${METRICS.map(k=>`${k}=${k}+?`).join(',')}
 WHERE id=1 AND EXISTS(SELECT 1 FROM quota_policy p WHERE p.id=1 AND p.enabled=1
 AND p.threshold_percent BETWEEN 1 AND 80 AND p.verified_until>?
 AND ${METRICS.map(k=>`typeof(quota_state.${k})='integer' AND quota_state.${k} BETWEEN 0 AND ${FREE_LIMITS[k]} AND typeof(p.${k}_limit)='integer' AND p.${k}_limit BETWEEN 1 AND ${FREE_LIMITS[k]} AND quota_state.${k}+?<=CAST(p.${k}_limit*p.threshold_percent/100 AS INTEGER)`).join(' AND ')}) RETURNING id`;
export async function reserve(db, costs, now = new Date().toISOString()) {
 const values = METRICS.map(k=>costs[k] ?? 0);
 if(values.some(v=>!Number.isSafeInteger(v)||v<0)) throw new Error('Invalid quota reservation');
 return !!(await db.prepare(RESERVE_SQL).bind(...values,now,...values).first());
}
export function requestCost(kind, bytes=0) {
 return {worker_requests:1,d1_reads:256,d1_writes:16,
  r2_a:kind==='upload'?1:0,r2_b:kind==='download'?1:0,
  r2_bytes:kind==='upload'?bytes:0,d1_bytes:kind==='upload'?16384:0};
}

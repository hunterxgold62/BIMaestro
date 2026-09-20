import {parentPort,workerData} from 'node:worker_threads';
import {DatabaseSync} from 'node:sqlite';
import {RESERVE_SQL,METRICS,requestCost} from '../quota.js';
const db=new DatabaseSync(workerData.path);db.exec('PRAGMA busy_timeout=10000');
const gate=new Int32Array(workerData.gate);parentPort.postMessage('ready');Atomics.wait(gate,0,0);
const costs=requestCost('upload',512),values=METRICS.map(k=>costs[k]??0);
try{const result=db.prepare(RESERVE_SQL).get(...values,new Date().toISOString(),...values);parentPort.postMessage(Boolean(result));}
finally{db.close();}

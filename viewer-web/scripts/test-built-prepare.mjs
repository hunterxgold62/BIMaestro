import { Worker } from 'node:worker_threads';
import { readdir } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
import * as THREE from 'three';
const directory = path.resolve('dist/client/_next/static/workers');
const filename = (await readdir(directory)).find(name => /^mep-prepare\.worker-.*\.js$/.test(name));
assert.ok(filename);
const worker = new Worker(`
  const { parentPort, workerData } = require('node:worker_threads');
  globalThis.self = globalThis;
  globalThis.postMessage = (data, options) => parentPort.postMessage(data, options?.transfer);
  parentPort.on('message', data => self.onmessage({ data }));
  import(workerData).then(() => parentPort.postMessage({ ready: true }));
`, { eval: true, workerData: pathToFileURL(path.join(directory, filename)).href });
const receive = () => new Promise((resolve, reject) => {
  const timer = setTimeout(() => done(new Error('Preparation worker timeout')), 20000);
  const message = data => done(null, data), error = caught => done(caught);
  function done(caught, data) { clearTimeout(timer); worker.off('message', message); worker.off('error', error); caught ? reject(caught) : resolve(data); }
  worker.once('message', message); worker.once('error', error);
});
try {
  assert.equal((await receive()).ready, true);
  const geometry = new THREE.BoxGeometry(10, 10, 1).toNonIndexed();
  geometry.setAttribute('_element', new THREE.Float32BufferAttribute(new Float32Array(geometry.getAttribute('position').count).fill(1), 1));
  const attributes = Object.fromEntries(Object.entries(geometry.attributes).map(([name, attr]) => [name, { array: attr.array, itemSize: attr.itemSize, normalized: attr.normalized }]));
  worker.postMessage({ blockers: [1] });
  const result = receive(); worker.postMessage({ id: 1, geometry: { attributes, index: null }, angle: 22 });
  const prepared = await result;
  assert.equal(prepared.error, undefined); assert.ok(prepared.geometry.bvh); assert.ok(prepared.proxy.bvh); assert.ok(prepared.edges.attributes.position.array.length > 0);
  assert.deepEqual(prepared.geometry.attributes.position.array, attributes.position.array);
  console.log('Production preparation worker: startup, geometry, collisions, edges and visibility passed.');
} finally { await worker.terminate(); }

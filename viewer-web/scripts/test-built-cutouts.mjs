import { Worker } from 'node:worker_threads';
import { readdir } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
import * as THREE from 'three';

// Exercise the exact production bundle in an isolated thread without window
// or document. Source-only tests cannot detect client-build constant folding.
const directory = path.resolve('dist/client/_next/static/workers');
const filename = (await readdir(directory)).find(name => /^mep-cutouts\.worker-.*\.js$/.test(name));
assert.ok(filename, 'Build the site before running this check.');
const worker = new Worker(`
  const { parentPort, workerData } = require('node:worker_threads');
  globalThis.self = globalThis;
  globalThis.postMessage = (data, options) => parentPort.postMessage(data, options?.transfer);
  parentPort.on('message', data => self.onmessage({ data }));
  import(workerData).then(() => parentPort.postMessage({ ready: true }));
`, { eval: true, workerData: pathToFileURL(path.join(directory, filename)).href });
function response() {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => done(new Error('Worker did not respond within 20 seconds.')), 20000);
    const receive = data => done(null, data);
    const fail = error => done(error);
    function done(error, data) {
      clearTimeout(timer); worker.off('message', receive); worker.off('error', fail);
      error ? reject(error) : resolve(data);
    }
    worker.once('message', receive); worker.once('error', fail);
  });
}
function geometry(data) {
  const result = new THREE.BufferGeometry();
  for (const [name, attr] of Object.entries(data.attributes)) result.setAttribute(name, new THREE.BufferAttribute(attr.array, attr.itemSize, attr.normalized));
  if (data.index) result.setIndex(new THREE.BufferAttribute(data.index, 1));
  return result;
}
try {
  assert.equal((await response()).ready, true);
  for (const id of [1, 2]) {
    const wall = new THREE.BoxGeometry(10, 10, 1).toNonIndexed(); wall.translate(0, 0, 1 - id);
    wall.setAttribute('_element', new THREE.Float32BufferAttribute(new Float32Array(wall.getAttribute('position').count).fill(id), 1));
    const attributes = Object.fromEntries(Object.entries(wall.attributes).map(([name, attr]) => [name, { array: attr.array, itemSize: attr.itemSize, normalized: attr.normalized }]));
    worker.postMessage({ type: 'mesh', geometry: { attributes, index: null }, matrix: new THREE.Matrix4().toArray() });
  }
  const marks = [{ id: 'cut', kind: 'reservation', elementKey: 'wall1', position: [0, 0, .5], normal: [0, 0, 1], widthCm: 60.96, heightCm: 60.96, depthCm: 60.96 }];
  const properties = [1, 2].map(id => [id, { key: 'wall' + id, category: 'Murs' }]);
  let pending = response();
  worker.postMessage({ type: 'update', version: 1, marks, properties });
  const result = await pending;
  assert.equal(result.error, undefined); assert.equal(result.results.length, 2);
  for (const item of result.results) {
    assert.ok(item.geometry.bvh); assert.ok(item.proxy.bvh);
    const wall = new THREE.Mesh(geometry(item.geometry), new THREE.MeshBasicMaterial({ side: THREE.DoubleSide })); wall.updateMatrixWorld();
    assert.equal(new THREE.Raycaster(new THREE.Vector3(0, 0, 2), new THREE.Vector3(0, 0, -1)).intersectObject(wall).length, 0);
    assert.ok(new THREE.Raycaster(new THREE.Vector3(3, 0, 2), new THREE.Vector3(0, 0, -1)).intersectObject(wall).length > 0);
  }
  pending = response(); worker.postMessage({ type: 'update', version: 2, marks: [], properties });
  const restored = await pending;
  assert.equal(restored.error, undefined);
  assert.equal(restored.results.length, 2);
  assert.ok(restored.results.every(item => item.geometry === null));
  console.log('Production worker: startup, two-wall cutting, geometry transfer and deletion passed.');
} finally { await worker.terminate(); }

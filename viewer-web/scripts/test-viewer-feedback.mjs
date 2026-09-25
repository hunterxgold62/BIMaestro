import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { createRequire } from 'node:module';
import { mkdir, readFile } from 'node:fs/promises';
import * as THREE from 'three';
import { zipSync, unzipSync, strFromU8 } from 'fflate';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/lemer/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const url = process.env.VIEWER_TEST_URL || 'http://localhost:3000';
const json = value => new TextEncoder().encode(JSON.stringify(value));
const hash = data => createHash('sha256').update(data).digest('hex');

// A sealed wall and a slab with independent IDs, packaged like a Revit publication.
const geometries = [new THREE.BoxGeometry(12, 8, 1).toNonIndexed().translate(0, 4, 0), new THREE.BoxGeometry(20, 1, 20).toNonIndexed().translate(0, -.5, 0)];
const views = [], accessors = [], chunks = [], primitives = [];
let byteLength = 0;
function attribute(values, type) {
  const array = Float32Array.from(values), data = new Uint8Array(array.buffer);
  views.push({ buffer: 0, byteOffset: byteLength, byteLength: data.length, target: 34962 }); chunks.push(data); byteLength += data.length;
  const size = type === 'VEC3' ? 3 : 1;
  const min = Array.from({ length: size }, (_, i) => Math.min(...array.filter((_, n) => n % size === i)));
  const max = Array.from({ length: size }, (_, i) => Math.max(...array.filter((_, n) => n % size === i)));
  accessors.push({ bufferView: views.length - 1, componentType: 5126, count: array.length / size, type, min, max });
  return accessors.length - 1;
}
geometries.forEach((g, id) => primitives.push({ attributes: { POSITION: attribute(g.attributes.position.array, 'VEC3'), NORMAL: attribute(g.attributes.normal.array, 'VEC3'), _ELEMENT: attribute(new Float32Array(g.attributes.position.count).fill(id + 1), 'SCALAR') }, material: 0 }));
let document = json({ asset: { version: '2.0' }, scene: 0, scenes: [{ nodes: [0] }], nodes: [{ mesh: 0 }], meshes: [{ primitives }], materials: [{ doubleSided: true, pbrMetallicRoughness: { baseColorFactor: [.7, .75, .8, 1] } }], buffers: [{ byteLength }], bufferViews: views, accessors });
const padded = new Uint8Array(Math.ceil(document.length / 4) * 4).fill(32); padded.set(document); document = padded;
const glb = Buffer.alloc(12 + 8 + document.length + 8 + byteLength);
glb.writeUInt32LE(0x46546c67, 0); glb.writeUInt32LE(2, 4); glb.writeUInt32LE(glb.length, 8);
glb.writeUInt32LE(document.length, 12); glb.writeUInt32LE(0x4e4f534a, 16); glb.set(document, 20);
glb.writeUInt32LE(byteLength, 20 + document.length); glb.writeUInt32LE(0x004e4942, 24 + document.length);
let offset = 28 + document.length; for (const chunk of chunks) { glb.set(chunk, offset); offset += chunk.length; }
const properties = [{ index: 1, key: 'wall', name: 'Mur test', category: 'Murs', center: [0, 4, 0], size: [12, 8, 1] }, { index: 2, key: 'floor', name: 'Sol test', category: 'Sols', center: [0, -.5, 0], size: [20, 1, 20] }].map(p => ({ ...p, elementId: p.index, stableKey: p.key, typeName: '', levelName: '', documentTitle: 'Test', properties: {} }));
const files = { 'model.glb': glb, 'properties.json': json(properties), 'mep.json': json({ schemaVersion: 1, graph: { elements: [], connectors: [], connections: [], valves: [], sources: [], systems: [] } }) };
files['manifest.json'] = json({ schemaVersion: 1, name: 'Maquette de validation', sourceOrigin: [0, 0, 0], sourceDocumentId: 'test', sharedCoordinates: { origin: [0, 0, 0], xAxis: [1, 0, 0], siteName: 'Site test' }, units: 'revit-internal-feet', coordinateSystem: 'right-handed-z-up', files: Object.fromEntries(Object.entries(files).map(([k, v]) => [k, { bytes: v.length, sha256: hash(v) }])) });
const archive = zipSync(files);
const mark = (kind, i) => ({ id: randomUUID(), kind, elementKey: 'wall', elementName: 'Mur test ' + i, position: [-4, 2, .5], normal: [0, 0, 1], widthCm: 60, heightCm: 40, depthCm: 30.5, lot: i % 2 ? 'ELEC' : 'GC', text: `Repère ${i}`, modelRevision: 1, dimensions: [] });
const entries = Array.from({ length: 45 }, (_, i) => mark(i === 0 ? 'reservation' : 'note', i));
entries[0].dimensions = [{ id: 'dim', elementKey: 'floor', elementName: 'Sol test', point: [0, 0, 0], normal: [0, 1, 0] }];
let scenario = { revision: 1, state: { valves: {}, sources: {}, markups: Object.fromEntries(entries.map(m => [m.id, m])), reservationLots: { ELEC: { name: 'Électricité', color: '#7c3aed' }, GC: { name: 'Gros œuvre', color: '#c2410c' } } } };
const browser = await chromium.launch({ channel: 'msedge', headless: true });
console.log('Browser launched.');
const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
const errors = []; page.on('pageerror', e => errors.push(e.message));
page.on('pageerror', e => console.error('PAGE', e.message));
page.setDefaultTimeout(20000);
// Test-only observation, injected into the dev module response. No debug API is shipped.
await page.route('**/components/mep-viewer.tsx*', async route => {
  const response = await route.fetch(); let body = await response.text();
  const marker = 'controlsRef.current = controls;';
  if (body.includes(marker)) body = body.replace(marker, marker + ` window.__viewerTest = { camera, controls, get pivot() { return selectedPivot; }, get selected() { return selectedIndexRef.current; }, screen(x,y,z) { const p = new THREE.Vector3(x,y,z).project(camera), r = renderer.domElement.getBoundingClientRect(); return { x: r.left + (p.x+1)*r.width/2, y: r.top + (1-p.y)*r.height/2 }; } };`);
  await route.fulfill({ response, body });
});
await page.route('**/mep-share', async route => {
  const body = route.request().postDataJSON(); let data = { scenario };
  if (body.action === 'resolve') data = { publication: { id: 'feedback-test', name: 'Maquette de validation', revision: 1 }, role: 'editor', packageUrl: url + '/feedback.zip', scenario, events: [], realtimeToken: '' };
  if (body.action === 'markup') { const markups = { ...scenario.state.markups }; if (body.remove) delete markups[body.markup.id]; else markups[body.markup.id] = body.markup; scenario = { ...scenario, revision: scenario.revision + 1, state: { ...scenario.state, markups } }; data = { scenario }; }
  await route.fulfill({ json: data });
});
await page.route('**/feedback.zip', route => route.fulfill({ body: Buffer.from(archive) }));
const waitFrames = () => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
const screen = xyz => page.evaluate(xyz => window.__viewerTest.screen(...xyz), xyz);
const state = () => page.evaluate(() => { const t = window.__viewerTest; return { selected: t.selected, pivot: t.pivot?.toArray(), camera: t.camera.position.toArray(), target: t.controls.target.toArray() }; });
try {
  console.log('Opening fixture.');
  await page.goto(url + '/#/share/' + 'a'.repeat(64));
  console.log('Fixture route loaded.');
  await page.locator('.model-opening').waitFor({ state: 'hidden', timeout: 90000 });
  await page.waitForFunction(() => !!window.__viewerTest);
  assert.match(await page.title(), /Maquette 3D/);
  assert.deepEqual((await page.locator('.view-toolbar button').allTextContents()).map(t => t.trim().replace(/\s*\(\d+\)/, '')), ['Isométrique', 'Coupe', 'Mesurer', 'Analyser les coupes', 'Annotation et Résa']);
  assert.equal(await page.locator('.view-toolbar button svg').count(), 5);
  await page.getByRole('button', { name: /^Annotation et Résa/ }).click();
  const list = page.locator('.markup-list');
  assert.match(await list.locator('.markup-list-item').first().innerText(), /^R\./);
  assert.match(await list.locator('.markup-list-item').nth(1).innerText(), /^A\./);
  assert.equal(await list.locator('.markup-list-item').nth(1).evaluate(e => getComputedStyle(e).color), 'rgb(124, 58, 237)');
  await list.hover(); await page.mouse.wheel(0, 2000);
  await page.waitForFunction(() => document.querySelector('.markup-list').scrollTop > 100);
  await list.locator('.markup-list-item').last().scrollIntoViewIfNeeded();
  assert.ok(await list.locator('.markup-list-item').last().isVisible());
  await list.locator('.markup-list-item').nth(1).click();
  const bcfDownload = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Exporter BCF', exact: true }).click();
  const bcf = await bcfDownload;
  assert.equal(bcf.suggestedFilename(), 'annotation.bcfzip');
  const bcfFiles = unzipSync(await readFile(await bcf.path()));
  const topicFolder = entries[1].id + '/';
  assert.ok(bcfFiles[topicFolder + 'snapshot.png']?.length > 1000);
  assert.equal(Buffer.from(bcfFiles[topicFolder + 'snapshot.png']).subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
  assert.match(strFromU8(bcfFiles[topicFolder + 'viewpoint.bcfv']), /<PerspectiveCamera>/);
  await page.locator('.markup-card').getByRole('button', { name: 'Fermer' }).click();
  await page.getByRole('button', { name: /^Annotation et Résa/ }).click();
  await list.locator('.markup-list-item').first().click();
  assert.equal(await page.getByRole('button', { name: 'Replacer sur une face' }).count(), 0);
  await page.getByRole('button', { name: 'Modifier / déplacer' }).click();
  const distance = page.getByLabel('Sol test · distance à l’axe (cm)');
  await distance.fill('120'); await distance.press('Tab'); await waitFrames();
  assert.equal(await page.getByRole('button', { name: 'Appliquer la cote' }).count(), 0);
  await page.getByRole('button', { name: 'Enregistrer', exact: true }).click();
  await page.locator('.markup-editor').waitFor({ state: 'hidden' });
  assert.ok(Math.abs(scenario.state.markups[entries[0].id].position[1] - 120 / 30.48) < 1e-8);
  // Right-click only establishes context; it must leave selection and camera alone.
  const wallPoint = await screen([3, 4, .5]);
  const before = await state(); await page.mouse.click(wallPoint.x, wallPoint.y, { button: 'right' }); await waitFrames();
  assert.equal((await state()).selected, before.selected); assert.deepEqual((await state()).camera, before.camera);
  await page.getByRole('button', { name: 'Créer une réservation ici' }).click();
  assert.equal(Number(await page.getByLabel('Profondeur (cm)', { exact: true }).inputValue()), 30.5);
  await page.getByRole('button', { name: 'Annuler', exact: true }).click();
  const floorPoint = await screen([4, 0, 5]);
  await page.mouse.click(floorPoint.x, floorPoint.y, { button: 'right' });
  await page.getByRole('button', { name: 'Créer une réservation ici' }).click();
  assert.equal(Number(await page.getByLabel('Profondeur (cm)', { exact: true }).inputValue()), 30.5);
  assert.match(await page.locator('.markup-editor').innerText(), /Sol test/);
  await page.getByRole('button', { name: 'Annuler', exact: true }).click();
  await page.mouse.click(wallPoint.x, wallPoint.y);
  await page.waitForFunction(() => window.__viewerTest.selected === 1 && window.__viewerTest.pivot);
  // Let the initial focus settle before exercising free travel.
  await page.waitForTimeout(800);
  const selected = await state(); const canvas = await page.locator('canvas').first().boundingBox();
  const x = canvas.x + canvas.width * .65, y = canvas.y + canvas.height * .65;
  await page.mouse.move(x, y); await page.mouse.wheel(0, -120); await waitFrames();
  assert.deepEqual((await state()).pivot, selected.pivot);
  await page.mouse.move(x, y); await page.mouse.down({ button: 'right' }); await page.mouse.move(x + 55, y + 25, { steps: 8 }); await page.mouse.up({ button: 'right' }); await page.waitForTimeout(600);
  assert.deepEqual((await state()).pivot, selected.pivot); assert.equal((await state()).selected, 1);
  const panned = await state(); const radius = new THREE.Vector3(...panned.camera).distanceTo(new THREE.Vector3(...panned.pivot));
  await page.mouse.move(x, y); await page.mouse.down(); await page.mouse.move(x + 100, y, { steps: 12 }); await page.mouse.up(); await waitFrames();
  const rotated = await state(); assert.deepEqual(rotated.pivot, selected.pivot);
  assert.ok(Math.abs(new THREE.Vector3(...rotated.camera).distanceTo(new THREE.Vector3(...rotated.pivot)) - radius) < .01);
  assert.notDeepEqual(rotated.camera, panned.camera);
  console.log('PASS: labels, list scrolling, automatic dimension, context clicks, wall/slab depth and persistent orbit pivot.');
  await page.keyboard.press('Escape'); await page.getByRole('button', { name: 'Isométrique', exact: true }).click();
  await page.getByRole('button', { name: 'Mesurer', exact: true }).click();
  await page.getByLabel('Direction', { exact: true }).selectOption('horizontal');
  for (const point of [[2, 3, .5], [4, 4, .5]]) { const p = await screen(point); await page.mouse.click(p.x, p.y); }
  await page.locator('.measure-result').waitFor();
  assert.match(await page.locator('.measure-result').innerText(), /Verticale\s*0\.000 m/);
  await page.locator('.measure-controls').getByRole('button', { name: 'Fermer', exact: true }).click();
  await page.getByRole('button', { name: /^Annotation et Résa/ }).click();
  for (const size of [{ width: 900, height: 640 }, { width: 600, height: 700 }, { width: 1100, height: 450 }]) {
    await page.setViewportSize(size); await list.locator('.markup-list-item').last().scrollIntoViewIfNeeded();
    const rect = await list.boundingBox(); assert.ok(rect.y >= 0 && rect.y + rect.height <= size.height, JSON.stringify({ size, rect }));
    await list.hover(); const old = await list.evaluate(e => e.scrollTop); await page.mouse.wheel(0, -150);
    await page.waitForFunction(old => document.querySelector('.markup-list').scrollTop < old, old);
  }
  await page.setViewportSize({ width: 1440, height: 960 }); await mkdir('work', { recursive: true }); await page.screenshot({ path: 'work/feedback-ui.png' });
  assert.deepEqual(errors, []);
  console.log('PASS: toolbar, lot colours, scrolling, automatic dimension, right-click, wall/slab depth, persistent orbit pivot and horizontal measurement.');
} catch (error) { console.error(error); console.error((await page.locator('body').innerText().catch(() => '')).slice(0, 3000)); throw error; }
finally { await browser.close(); }

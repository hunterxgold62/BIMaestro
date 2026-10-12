import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { createRequire } from 'node:module';
import { mkdir, readFile } from 'node:fs/promises';
import * as THREE from 'three';
import { zipSync, unzipSync, strFromU8 } from 'fflate';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
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
files['viewer.json'] = json({ eyeHeight: 5.28, spawn: [0, 0, 5], initialYaw: Math.PI / 2, doors: [] });
files['manifest.json'] = json({ schemaVersion: 1, name: 'Maquette de validation', sourceOrigin: [0, 0, 0], sourceDocumentId: 'test', sharedCoordinates: { origin: [0, 0, 0], xAxis: [1, 0, 0], siteName: 'Site test' }, units: 'revit-internal-feet', coordinateSystem: 'right-handed-z-up', files: Object.fromEntries(Object.entries(files).map(([k, v]) => [k, { bytes: v.length, sha256: hash(v) }])) });
const archive = zipSync(files);

// Real Chromium + a deterministic publication. Only controller hardware is mocked.
// Camera observations are injected into the dev response and never shipped.
await mkdir('test-results', { recursive: true });
const browser = await chromium.launch({ headless: true, ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}), args: ['--enable-webgl', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
const errors = []; page.on('pageerror', error => errors.push(error.message));
page.setDefaultTimeout(20000);
await page.addInitScript(() => {
  window.__pads = [];
  Object.defineProperty(navigator, 'getGamepads', { value: () => window.__pads, configurable: true });
});
await page.route('**/components/mep-viewer.tsx*', async route => {
  const response = await route.fetch(); let body = await response.text();
  const marker = 'controlsRef.current = controls;';
  assert.ok(body.includes(marker), 'The test must observe the real viewer module');
  body = body.replace(marker, marker + ` window.__viewerTest = { camera, controls, elapsed: 0, get selected() { return selectedIndexRef.current; }, get draft() { return draftRef.current; } };`);
  assert.ok(body.includes('previousFrameTime = now;'), 'The test must observe simulated frame time');
  body = body.replace('previousFrameTime = now;', 'previousFrameTime = now; window.__viewerTest.elapsed += delta;');
  await route.fulfill({ response, body });
});
const scenario = { revision: 1, state: { valves: {}, sources: {}, markups: {} } };
await page.routeWebSocket('**', socket => socket.close());
await page.route('**/mep-share', async route => {
  const request = route.request().postDataJSON(), action = request.action;
  if (action === 'markup') { if (request.remove) delete scenario.state.markups[request.markup.id]; else scenario.state.markups[request.markup.id] = request.markup; scenario.revision++; }
  if (action === 'reservation-lot') { scenario.state.reservationLots ??= {}; scenario.state.reservationLots[request.id] = request.lot; scenario.revision++; }
  await route.fulfill({ json: action === 'resolve' ? { publication: { id: 'xbox-test', name: 'Maquette de validation', revision: 1 }, role: 'editor', packageUrl: url + '/gamepad.zip', scenario, events: [], realtimeToken: '' } : { scenario } });
});
await page.route('**/gamepad.zip', route => route.fulfill({ body: Buffer.from(archive) }));
const frames = (count = 8) => page.evaluate(count => new Promise(resolve => { const next = () => --count <= 0 ? resolve() : requestAnimationFrame(next); requestAnimationFrame(next); }), count);
const input = async ({ axes = [0, 0, 0, 0], buttons = {}, connected = true } = {}) => {
  await page.evaluate(({ axes, buttons, connected }) => { window.__pads = connected ? [{ id: 'Xbox Wireless Controller', index: 0, mapping: 'standard', connected: true, axes, buttons: Array.from({ length: 17 }, (_, i) => ({ value: buttons[i] || 0, pressed: (buttons[i] || 0) > .5 })) }] : []; }, { axes, buttons, connected });
  await frames();
};
const tap = async index => { await input(); await input({ buttons: { [index]: 1 } }); await input(); };
const quickTap = index => page.evaluate(async index => {
  const frames = n => new Promise(resolve => { const tick = () => --n <= 0 ? resolve() : requestAnimationFrame(tick); requestAnimationFrame(tick); });
  const buttons = value => { window.__pads[0].axes = [0, 0, 0, 0]; window.__pads[0].buttons = Array.from({ length: 17 }, (_, i) => ({ value: i === index ? value : 0, pressed: i === index && !!value })); };
  buttons(0); await frames(2); buttons(1); await frames(3); buttons(0); await frames(3);
}, index);
const controllerFocus = async locator => {
  assert.equal(await locator.count(), 1, 'The requested controller target must exist');
  for (let count = 0; count < 160; count++) {
    if (await locator.evaluate(element => element === document.activeElement)) return;
    const keyboard = await page.locator('.gamepad-field-editor').count();
    const backward = keyboard && await locator.evaluate(target => {
      const buttons = Array.from(target.closest('.gamepad-field-editor').querySelectorAll('button:not(:disabled)'));
      const from = buttons.indexOf(document.activeElement), to = buttons.indexOf(target), size = buttons.length;
      return (from - to + size) % size < (to - from + size) % size;
    });
    await quickTap(keyboard ? backward ? 4 : 5 : 13);
  }
  throw new Error('Controller navigation could not reach ' + await locator.evaluate(element => element.outerHTML.slice(0, 200)));
};
const controllerClick = async locator => { await controllerFocus(locator); await quickTap(0); };
const controllerText = async (field, value) => {
  await controllerClick(field);
  const keyboard = page.locator('.gamepad-field-editor'); await keyboard.waitFor();
  await controllerClick(keyboard.getByRole('button', { name: 'Tout effacer', exact: true }));
  for (const key of value) await controllerClick(keyboard.getByRole('button', { name: key, exact: true }));
  await controllerClick(keyboard.getByRole('button', { name: 'Valider', exact: true }));
  await keyboard.waitFor({ state: 'hidden' });
};
const device = () => page.locator('main').getAttribute('data-input-device');
const state = () => page.evaluate(() => ({ position: window.__viewerTest.camera.position.toArray(), quaternion: window.__viewerTest.camera.quaternion.toArray(), selected: window.__viewerTest.selected }));
const distance = (a, b) => Math.hypot(...a.map((v, i) => v - b[i]));
const aimAtWall = async () => {
  await input();
  await page.evaluate(() => { const t = window.__viewerTest; t.camera.position.set(0, 4, 10); t.controls.target.set(0, 4, 0); t.camera.lookAt(t.controls.target); t.controls.update(); });
  await frames();
};
const checks = [];
let uiPhase = false;
async function check(name, run) { if (process.env.GAMEPAD_UI_ONLY && !uiPhase) return; await run(); checks.push(name); console.log('PASS', name); }
try {
  await page.goto(url + '/#/share/' + 'a'.repeat(64));
  await page.bringToFront();
  await page.locator('.model-opening').waitFor({ state: 'hidden', timeout: 90000 });
  await page.waitForFunction(() => !!window.__viewerTest);
  await frames(15);
  await check('neutral connection and drift do not move the camera or change input hints', async () => {
    const before = await state(); await input({ axes: [.1, -.1, .1, -.1] });
    assert.equal(await device(), 'keyboard'); assert.ok(distance(before.position, (await state()).position) < 1e-6);
    assert.ok(await page.locator('.gamepad-status').isVisible());
  });
  await check('orbit rotation, panning, zoom and stop use the real camera', async () => {
    await input({ axes: [0, 0, .7, .2] }); assert.equal(await device(), 'gamepad');
    assert.ok(await page.locator('.gamepad-reticle').isVisible());
    const rotated = await state(); await input({ axes: [.7, 0, 0, 0] }); assert.ok(distance(rotated.position, (await state()).position) > .01);
    const panned = await state(); await input({ buttons: { 7: .7 } }); assert.ok(distance(panned.position, (await state()).position) > .01);
    await input(); const stopped = await state(); await frames(12); assert.ok(distance(stopped.position, (await state()).position) < 1e-6);
  });
  await check('A selects the element at the reticle and B clears it', async () => {
    await aimAtWall(); await tap(0); assert.equal((await state()).selected, 1);
    await tap(1); assert.equal((await state()).selected, null);
  });
  await check('RB opens the context menu, D-pad focuses its buttons, B returns to aiming', async () => {
    await aimAtWall(); await tap(5);
    assert.ok(await page.locator('.element-context-menu').isVisible());
    assert.ok(await page.evaluate(() => document.activeElement?.closest('.element-context-menu') !== null));
    const first = await page.evaluate(() => document.activeElement.textContent); await tap(13);
    assert.notEqual(await page.evaluate(() => document.activeElement.textContent), first);
    await tap(1); assert.equal(await page.locator('.element-context-menu').count(), 0);
    assert.ok(await page.locator('.gamepad-reticle').isVisible());
  });
  await check('Menu freezes camera movement and A launches a measure then returns to the reticle', async () => {
    await tap(9); assert.equal(await page.locator('.gamepad-reticle').count(), 0);
    const before = await state(); await input({ axes: [1, 0, 1, 0] }); assert.ok(distance(before.position, (await state()).position) < 1e-6);
    await input(); await page.getByRole('button', { name: 'Mesurer', exact: true }).evaluate(button => button.focus());
    await tap(0); assert.ok(await page.locator('.gamepad-reticle').isVisible(), 'A measurement must leave menu mode');
    assert.match(await page.locator('.measure-controls').textContent(), /appuyez sur A/);
    assert.doesNotMatch(await page.locator('.measure-controls').textContent(), /Cliquez|Échap/);
    await aimAtWall(); await tap(0); assert.ok(await page.locator('.measure-label').count() > 0, 'A places the first measurement point');
    await tap(1);
  });
  await check('section picking returns to aiming and LB + triggers move the cut without zooming', async () => {
    await aimAtWall(); await tap(9);
    await page.locator('.view-toolbar').getByRole('button', { name: /^Coupe/ }).evaluate(button => button.focus());
    await tap(0); assert.ok(await page.locator('.gamepad-reticle').isVisible());
    assert.match(await page.locator('.section-controls [role=status]').textContent(), /appuyez sur A/);
    await tap(0); const panel = page.locator('.section-controls');
    assert.match(await panel.innerText(), /LB \+ LT \/ RT/);
    const offset = await panel.locator('output').innerText(), before = await state();
    await input({ buttons: { 4: 1, 7: 1 } }); await input();
    assert.notEqual(await panel.locator('output').innerText(), offset);
    assert.ok(distance(before.position, (await state()).position) < 1e-6, 'Cut adjustment must not zoom the camera');
    await tap(9); await controllerFocus(panel.getByRole('slider'));
    const sliderOffset = await panel.locator('output').innerText(); await quickTap(15);
    assert.notEqual(await panel.locator('output').innerText(), sliderOffset, 'Controller navigation must adjust the cut slider');
    await tap(1);
    await panel.getByRole('button', { name: 'Tout réafficher', exact: true }).click(); await input();
  });
  await check('keyboard takeover preserves focus and requires neutral controller before resuming', async () => {
    await input({ axes: [.7, 0, 0, 0] }); await page.keyboard.press('Escape'); await frames();
    assert.equal(await device(), 'keyboard'); const before = await state(); await frames(10);
    assert.ok(distance(before.position, (await state()).position) < 1e-6);
    await input(); await input({ axes: [.7, 0, 0, 0] }); assert.equal(await device(), 'gamepad'); await input();
    await page.mouse.move(15, 15); await frames(); assert.equal(await device(), 'keyboard');
  });
  await check('Maquette uses B to crouch, A to jump, RT to select and RS click to cancel', async () => {
    await tap(8); assert.ok((await page.locator('.statusbar').innerText()).includes('MARCHE'));
    await frames(25);
    const standing = await state(); await input({ buttons: { 1: 1 } }); assert.ok((await state()).position[1] < standing.position[1]);
    await input(); await frames(25);
    const grounded = await state(); await input({ buttons: { 0: 1 } }); assert.ok((await state()).position[1] > grounded.position[1]);
    assert.equal((await state()).selected, null, 'Jumping must not select an object');
    await input(); await frames(50);
    await aimAtWall(); await tap(7); assert.equal((await state()).selected, 1);
    await input({ buttons: { 1: 1 } }); assert.equal((await state()).selected, 1, 'Crouching must not clear selection');
    await input(); await tap(11); assert.equal((await state()).selected, null);
    assert.match(await page.locator('.gamepad-hint').innerText(), /LS clic.*Courir.*A.*Sauter.*B.*Accroupir.*RT.*Sélectionner/);
  });
  await check('LS click starts sprinting without needing to hold the click; releasing forward stops it', async () => {
    const movement = async (sprint = false) => {
      await tap(14); await frames(15);
      return page.evaluate(async sprint => {
        const t = window.__viewerTest;
        t.camera.lookAt(t.camera.position.x + 10, t.camera.position.y, t.camera.position.z);
        const set = (buttons = {}) => { window.__pads[0].axes = [0, -1, 0, 0]; window.__pads[0].buttons = Array.from({ length: 17 }, (_, i) => ({ value: buttons[i] || 0, pressed: !!buttons[i] })); };
        const frames = count => new Promise(resolve => { const tick = () => --count <= 0 ? resolve() : requestAnimationFrame(tick); requestAnimationFrame(tick); });
        // Start running, then release the stick click while continuing forward.
        if (sprint) { set({ 10: 1 }); await frames(2); }
        set(); const before = t.camera.position.clone(), start = t.elapsed;
        await frames(5);
        const speed = t.camera.position.distanceTo(before) / (t.elapsed - start);
        window.__pads[0].axes = [0, 0, 0, 0]; await frames(4);
        return speed;
      }, sprint);
    };
    const walk = await movement(), sprint = await movement(true), stopped = await movement();
    assert.ok(sprint > walk * 1.8, `Sprint must accelerate the camera: walk=${walk}, sprint=${sprint}`);
    assert.ok(stopped < sprint * .7, `Releasing forward must stop sprint: walk=${stopped}, sprint=${sprint}`);
  });
  await check('walking, look and collisions work; Y toggles flight, A/B control height and triggers do not', async () => {
    const facing = await state(); await input({ axes: [0, 0, .6, -.3] }); assert.ok(distance(facing.quaternion, (await state()).quaternion) > .01);
    await input(); await tap(14);
    await input({ axes: [0, -1, 0, 0] }); await frames(160); await input();
    const position = (await state()).position; assert.ok(position[2] > .5 && position[2] < 4.9, 'Walking must approach but not cross the wall: ' + position);
    await input({ buttons: { 3: 1 } }); await frames(20); assert.ok((await page.locator('.statusbar').innerText()).includes('VOL LIBRE'));
    await input();
    const before = await state(); await input({ buttons: { 0: 1 } }); assert.ok((await state()).position[1] > before.position[1]); await input();
    const raised = await state(); await input({ buttons: { 1: 1 } }); assert.ok((await state()).position[1] < raised.position[1]); await input();
    const neutral = await state(); await input({ buttons: { 6: 1, 7: 1 } });
    assert.ok(Math.abs((await state()).position[1] - neutral.position[1]) < 1e-6, 'Triggers must not move vertically'); await input();
  });
  await check('maquette tool prompts use RT, which places a measure; RS click cancels it', async () => {
    await tap(9);
    await page.getByRole('button', { name: 'Mesurer', exact: true }).evaluate(button => button.focus()); await tap(0);
    assert.match(await page.locator('.measure-controls').textContent(), /appuyez sur RT/);
    assert.match(await page.locator('.measure-controls').textContent(), /RS clic pour annuler/);
    await aimAtWall(); await tap(7); assert.ok(await page.locator('.measure-label').count() > 0);
    await tap(11); assert.equal(await page.locator('.measure-label').count(), 0);
    await page.locator('.measure-controls').getByRole('button', { name: 'Fermer', exact: true }).click(); await input();
  });
  await check('holding A on the menu ascent button raises the camera', async () => {
    await tap(9); await page.getByRole('button', { name: 'Monter', exact: true }).evaluate(button => button.focus());
    const before = await state(); await input({ buttons: { 0: 1 } });
    assert.ok((await state()).position[1] > before.position[1], 'Controller menu ascent must behave like the pointer button');
    await input(); await tap(1);
  });
  await check('maquette RT places a cut; LB + RT adjusts it without selecting or moving the camera', async () => {
    await aimAtWall(); await tap(9);
    await page.locator('.view-toolbar').getByRole('button', { name: /^Coupe/ }).evaluate(button => button.focus()); await tap(0);
    const panel = page.locator('.section-controls');
    assert.match(await panel.locator('[role=status]').textContent(), /appuyez sur RT\. RS clic pour annuler/);
    await tap(7);
    const offset = await panel.locator('output').innerText(), before = await state();
    await input({ buttons: { 4: 1, 7: 1 } }); await input();
    assert.notEqual(await panel.locator('output').innerText(), offset);
    assert.equal((await state()).selected, before.selected, 'Cut adjustment must not select an object');
    assert.ok(distance(before.position, (await state()).position) < 1e-6, 'Cut adjustment must not move the camera');
    await panel.getByRole('button', { name: 'Tout réafficher', exact: true }).click(); await input();
  });
  await check('disconnect during flight stops input and restores keyboard hints', async () => {
    await input({ axes: [.7, 0, 0, 0] }); await input({ connected: false });
    assert.equal(await device(), 'keyboard'); assert.equal(await page.locator('.gamepad-status').count(), 0);
    const before = await state(); await frames(15); assert.ok(distance(before.position, (await state()).position) < 1e-6);
  });
  await check('blur pauses movement until neutral even if the stick remains held', async () => {
    await input(); await input({ axes: [.7, 0, 0, 0] });
    await page.evaluate(() => window.dispatchEvent(new Event('blur'))); await frames();
    const before = await state(); await frames(10); assert.ok(distance(before.position, (await state()).position) < 1e-6);
    await input(); await input({ axes: [.7, 0, 0, 0] }); assert.ok(distance(before.position, (await state()).position) > .01); await input();
  });
  await check('text editing keeps focus and blocks camera movement and controller actions', async () => {
    await tap(8); await aimAtWall(); await tap(5);
    await page.locator('.element-context-menu').getByRole('button', { name: 'Annoter ici' }).evaluate(button => button.focus()); await tap(0);
    const text = page.locator('.markup-editor textarea'); await text.focus(); await page.keyboard.type('Essai Xbox');
    const before = await state(); await input({ axes: [1, 0, 1, 0], buttons: { 3: 1 } });
    assert.equal(await text.inputValue(), 'Essai Xbox'); assert.equal(await text.evaluate(element => element === document.activeElement), true);
    assert.equal(await device(), 'keyboard'); assert.ok(distance(before.position, (await state()).position) < 1e-6);
    await input(); await page.getByRole('button', { name: 'Annuler', exact: true }).click();
  });
  await check('controller help stays visible and clear of navigation on a narrow viewport', async () => {
    await page.setViewportSize({ width: 640, height: 800 }); await input(); await input({ axes: [.5, 0, 0, 0] }); await input();
    const hint = page.locator('.gamepad-hint'); assert.ok(await hint.isVisible());
    const h = await hint.boundingBox(), n = await page.locator('.navigation-switch').boundingBox();
    assert.ok(h.x >= 0 && h.x + h.width <= 640 && h.y + h.height <= 800);
    assert.ok(n.y + n.height <= h.y || h.y + h.height <= n.y, 'Navigation buttons and controller help must not overlap');
    const lot = await page.locator('.reservation-lot-toolbar').boundingBox();
    assert.ok(lot.y + lot.height <= h.y || h.y + h.height <= lot.y, 'Lots toolbar must not cover controller help');
    await page.screenshot({ path: 'test-results/xbox-narrow.png' });
    await tap(9);
    await page.locator('.section-controls').getByRole('button', { name: '+ Viser une face', exact: true }).evaluate(button => button.focus());
    await tap(0);
    assert.equal(await page.locator('.section-controls').isVisible(), false, 'The pointing panel must not hide the aimed face');
    assert.ok(await page.evaluate(() => { const r = document.querySelector('.three-mount canvas').getBoundingClientRect(); return !!document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)?.closest('.three-mount'); }));
    await page.screenshot({ path: 'test-results/xbox-narrow-aiming.png' });
    await tap(1); assert.ok(await page.locator('.section-controls').isVisible());
    await tap(8);
    const walkingHint = await page.locator('.gamepad-hint').boundingBox(), navigation = await page.locator('.navigation-switch').boundingBox();
    assert.ok(walkingHint.x >= 0 && walkingHint.x + walkingHint.width <= 640 && walkingHint.y + walkingHint.height <= 800);
    assert.ok(navigation.y + navigation.height <= walkingHint.y || walkingHint.y + walkingHint.height <= navigation.y, 'Maquette help must not cover navigation');
    await page.screenshot({ path: 'test-results/xbox-maquette-narrow.png' });
  });
  uiPhase = true;
  await page.setViewportSize({ width: 1440, height: 960 }); await input();
  if (!((await page.locator('.statusbar').innerText()).includes('NAVIGATION WEB'))) await quickTap(8);
  await check('controller navigation reaches lists, checkboxes and expandable inspection sections', async () => {
    await aimAtWall(); await quickTap(0); await quickTap(9);
    const lot = page.getByRole('combobox', { name: 'Lot actif', exact: true }); await controllerFocus(lot);
    const previous = await lot.inputValue(), camera = await state(); await quickTap(14);
    assert.notEqual(await lot.inputValue(), previous, 'Left changes the selected lot');
    assert.ok(distance(camera.position, (await state()).position) < 1e-6, 'Editing a list must not move the camera');
    const checkbox = page.getByRole('checkbox', { name: 'Masquer les autres lots' }); await controllerClick(checkbox);
    assert.equal(await checkbox.isChecked(), true); await quickTap(0); assert.equal(await checkbox.isChecked(), false);
    const summary = page.locator('.inspection-card summary').first(); await controllerClick(summary);
    assert.equal(await summary.evaluate(element => element.parentElement.open), false);
    await quickTap(0); assert.equal(await summary.evaluate(element => element.parentElement.open), true);
    await quickTap(9); await quickTap(1);
  });
  await check('controller-only reservation creation reaches every field and edits numbers, shape and comment', async () => {
    await aimAtWall(); await quickTap(5);
    await controllerClick(page.locator('.element-context-menu').getByRole('button', { name: 'Créer une réservation ici', exact: true }));
    const editor = page.locator('.markup-editor'); await editor.waitFor();
    const width = editor.getByLabel('Largeur (cm)', { exact: true }); await controllerFocus(width);
    const before = Number(await width.inputValue()); await quickTap(15); assert.ok(Number(await width.inputValue()) > before);
    await controllerText(width, '75'); assert.equal(await width.inputValue(), '75');
    const shape = editor.getByLabel('Forme', { exact: true }); await controllerFocus(shape); await quickTap(15);
    assert.equal(await shape.inputValue(), 'round');
    await controllerText(editor.getByLabel('Diamètre (cm)', { exact: true }), '50');
    await controllerFocus(shape); await quickTap(14); assert.equal(await shape.inputValue(), 'rectangle');
    await controllerText(editor.getByLabel('Commentaire (facultatif)', { exact: true }), 'test');
    assert.equal(await editor.locator('textarea').inputValue(), 'test');
    await quickTap(8);
    const appearance = page.locator('.appearance-toolbar button').first(), appearanceBefore = await appearance.innerText();
    await controllerClick(appearance); assert.notEqual(await appearance.innerText(), appearanceBefore, 'View must make other panels reachable during editing');
    await quickTap(8);
    await page.screenshot({ path: 'test-results/xbox-reservation-fields.png' });
  });
  await check('controller-only lot creation includes text, color, save and return to the reservation', async () => {
    await controllerClick(page.locator('.markup-editor').getByRole('button', { name: 'Gérer / créer un lot', exact: true }));
    const lots = page.locator('.lots-panel'); await controllerClick(lots.getByRole('button', { name: '+ Nouveau lot', exact: true }));
    await controllerText(lots.getByLabel('Nom du lot', { exact: true }), 'essai');
    await controllerClick(lots.getByLabel('Couleur du lot', { exact: true }));
    const keyboard = page.locator('.gamepad-field-editor');
    await controllerClick(keyboard.getByRole('button', { name: '#16a34a', exact: true }));
    await controllerClick(keyboard.getByRole('button', { name: 'Valider', exact: true }));
    assert.equal(await lots.getByLabel('Couleur du lot', { exact: true }).inputValue(), '#16a34a');
    await controllerClick(lots.getByRole('button', { name: 'Créer le lot', exact: true }));
    await page.waitForFunction(() => window.__viewerTest.draft?.lot && document.querySelector('.lots-panel input')?.value === 'essai');
    assert.ok(Object.values(scenario.state.reservationLots).some(lot => lot.name === 'essai' && lot.color === '#16a34a'));
    await quickTap(1); assert.equal(await lots.count(), 0); assert.ok(await page.locator('.markup-editor').isVisible());
  });
  await check('controller moves and resizes a reservation on its face while the camera stays still', async () => {
    await controllerClick(page.locator('.markup-editor').getByRole('button', { name: 'Déplacer', exact: true }));
    assert.equal(await page.locator('.markup-editor').isVisible(), false, 'The editor must not obscure the manipulated reservation');
    const camera = await state(), draft = await page.evaluate(() => window.__viewerTest.draft);
    await input({ axes: [1, 0, 0, 0] }); await input();
    let moved = await page.evaluate(() => window.__viewerTest.draft);
    assert.ok(moved.position[0] > draft.position[0]); assert.ok(Math.abs(moved.position[2] - draft.position[2]) < 1e-8);
    assert.ok(distance(camera.position, (await state()).position) < 1e-6, 'Editing must not walk or pan the camera');
    const beforeNudge = moved.position; await quickTap(15); moved = await page.evaluate(() => window.__viewerTest.draft);
    assert.ok(Math.abs((moved.position[0] - beforeNudge[0]) * 30.48 - 1) < 1e-6, 'D-pad nudges by 1 cm');
    await quickTap(2); const width = moved.widthCm;
    await input({ axes: [1, -1, 0, 0] }); await input();
    const resized = await page.evaluate(() => window.__viewerTest.draft);
    assert.ok(resized.widthCm > width && resized.heightCm > moved.heightCm);
    assert.deepEqual(resized.position, moved.position);
    await quickTap(9); assert.ok(await page.locator('.markup-editor').isVisible());
    assert.equal(Number(await page.locator('.markup-editor').getByLabel('Largeur (cm)', { exact: true }).inputValue()), resized.widthCm);
  });
  await check('cancelling a dimension target preserves the draft; picking a floor adds a working distance field', async () => {
    const pick = page.locator('.markup-editor').getByRole('button', { name: 'Ajouter une cote · choisir une face', exact: true });
    await controllerClick(pick); await quickTap(1);
    assert.ok(await page.evaluate(() => !!window.__viewerTest.draft), 'Cancelling a target must keep the reservation');
    await quickTap(9); await controllerClick(pick);
    await page.evaluate(() => { const t = window.__viewerTest; t.camera.position.set(0, 8, 5); t.controls.target.set(0, 0, 5); t.camera.lookAt(t.controls.target); t.controls.update(); }); await frames();
    await quickTap(0); await quickTap(9);
    const distanceField = page.locator('.reservation-dimension input'); assert.equal(await distanceField.count(), 1);
    await controllerFocus(distanceField); const before = Number(await distanceField.inputValue()); await quickTap(15); await quickTap(13);
    assert.ok(Number(await distanceField.inputValue()) > before);
    await controllerClick(page.locator('.markup-editor').getByRole('button', { name: 'Enregistrer', exact: true }));
    await page.waitForFunction(() => !window.__viewerTest.draft);
    const saved = Object.values(scenario.state.markups)[0]; assert.equal(saved.text, 'test'); assert.equal(saved.dimensions.length, 1);
    assert.ok(saved.widthCm > 75); assert.ok(saved.heightCm > 40);
  });
  await check('controller reaches reservation exports and analysis filters without a mouse', async () => {
    await controllerClick(page.locator('.view-toolbar').getByRole('button', { name: /^Annotation et Résa/ }));
    const download = page.waitForEvent('download');
    await controllerClick(page.locator('.markup-list').getByRole('button', { name: 'Exporter vers Revit', exact: true }));
    assert.ok((await download).suggestedFilename().length > 0);
    await controllerClick(page.locator('.markup-list').getByRole('button', { name: 'Fermer', exact: true }));
    await controllerClick(page.locator('.view-toolbar').getByRole('button', { name: 'Analyser les coupes', exact: true }));
    const panel = page.locator('.mep-impact-panel'); await panel.waitFor();
    if (await panel.locator('summary').count()) {
      await controllerClick(panel.locator('summary').first());
      const filter = panel.getByRole('checkbox', { name: 'Équipements uniquement' }); await controllerClick(filter); assert.equal(await filter.isChecked(), true);
      await controllerText(panel.getByRole('textbox', { name: 'Filtrer les éléments concernés' }), 'mur');
    }
    await quickTap(1); assert.equal(await panel.count(), 0);
  });
  await check('saved reservations can be modified and deleted with the controller in Maquette mode', async () => {
    await quickTap(9); await quickTap(8); await quickTap(9);
    const listButton = page.locator('.view-toolbar').getByRole('button', { name: /^Annotation et Résa/ });
    await controllerClick(listButton); await controllerClick(page.locator('.markup-list-item'));
    await controllerClick(page.locator('.markup-card').getByRole('button', { name: 'Modifier / déplacer', exact: true }));
    const editor = page.locator('.markup-editor');
    await controllerClick(editor.getByRole('button', { name: 'Ajouter une cote · choisir une face', exact: true }));
    await quickTap(11); assert.ok(await page.evaluate(() => !!window.__viewerTest.draft), 'RS cancellation must preserve the draft in Maquette mode');
    await quickTap(9); await controllerClick(editor.getByRole('button', { name: 'Déplacer', exact: true }));
    const previous = Object.values(scenario.state.markups)[0].position; await quickTap(14);
    const next = await page.evaluate(() => window.__viewerTest.draft.position); assert.ok(distance(previous, next) > .01);
    await quickTap(9); await controllerClick(editor.getByRole('button', { name: 'Enregistrer', exact: true }));
    await page.waitForFunction(() => !window.__viewerTest.draft);
    assert.ok(distance(Object.values(scenario.state.markups)[0].position, next) < 1e-8, 'Saving must preserve the edited position within floating-point precision');
    await controllerClick(listButton); await controllerClick(page.locator('.markup-list-item'));
    await controllerClick(page.locator('.markup-card').getByRole('button', { name: 'Supprimer', exact: true }));
    await page.waitForFunction(() => document.querySelector('.view-toolbar')?.textContent.includes('Résa (0)'));
    assert.equal(Object.keys(scenario.state.markups).length, 0);
  });
  assert.deepEqual(errors, [], 'No browser runtime errors');
  console.log(checks.length + ' browser integration checks passed.');
} catch (error) {
  await mkdir('test-results', { recursive: true });
  await page.screenshot({ path: 'test-results/xbox-failure.png' }).catch(() => {});
  console.error('Browser errors:', errors);
  throw error;
} finally { await browser.close(); }

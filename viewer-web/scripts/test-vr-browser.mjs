import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import * as THREE from 'three';
import { zipSync } from 'fflate';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const url = process.env.VIEWER_TEST_URL || 'http://127.0.0.1:3012';
// Reuse the existing, deterministic Revit wall/slab publication fixture.
const source = await readFile(new URL('./test-gamepad-browser.mjs', import.meta.url), 'utf8');
const fixture = source.slice(source.indexOf('const geometries ='), source.indexOf('// Real Chromium'));
const archive = new Function('THREE', 'zipSync', 'json', 'hash', fixture + '\nreturn archive;')(THREE, zipSync, value => new TextEncoder().encode(JSON.stringify(value)), data => createHash('sha256').update(data).digest('hex'));
const browser = await chromium.launch({ headless: true, channel: process.env.PLAYWRIGHT_CHANNEL || 'msedge', args: ['--enable-webgl', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
try {
  for (const mode of process.env.VR_ONLY ? ['vr'] : ['desktop', 'phone', 'denied', 'vr']) {
    const page = await browser.newPage(mode === 'phone' ? { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true } : {});
    const errors = []; page.on('pageerror', error => { errors.push(error.message); console.error(mode, error.message); });
    await page.addInitScript(mode => {
      window.__pads = []; window.__requests = 0;
      Object.defineProperty(navigator, 'getGamepads', { value: () => window.__pads });
      const buttons = () => Array.from({ length: 6 }, () => ({ pressed: false, touched: false, value: 0 }));
      if (mode === 'vr' || mode === 'denied') {
        const matrix = (x, y, z) => Float32Array.from([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1]);
        const projection = Float32Array.from([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1.00006, -1, 0, 0, -.060002, 0]);
        WebGL2RenderingContext.prototype.makeXRCompatible = async () => {};
        window.XRWebGLBinding = undefined;
        window.XRWebGLLayer = class {
          constructor(session, gl) { this.framebuffer = null; this.framebufferWidth = gl.drawingBufferWidth; this.framebufferHeight = gl.drawingBufferHeight; }
          getViewport(view) { return { x: view.eye === 'left' ? 0 : this.framebufferWidth / 2, y: 0, width: this.framebufferWidth / 2, height: this.framebufferHeight }; }
        };
        class Session extends EventTarget {
          visibilityState = 'visible'; environmentBlendMode = 'opaque'; renderState = {}; enabledFeatures = ['local-floor'];
          inputSources = ['left', 'right'].map(handedness => ({ handedness, targetRayMode: 'tracked-pointer', targetRaySpace: {}, gripSpace: {}, profiles: ['oculus-touch-v3'], gamepad: { mapping: 'xr-standard', axes: [0, 0, 0, 0], buttons: buttons() } }));
          ended = false;
          updateRenderState(state) { Object.assign(this.renderState, state); }
          async requestReferenceSpace() { return new EventTarget(); }
          requestAnimationFrame(callback) {
            return requestAnimationFrame(time => { if (!this.ended) callback(time, window.__frame = {
              session: this,
              getViewerPose: () => ({ views: ['left', 'right'].map(eye => ({ eye, transform: { matrix: matrix(eye === 'left' ? -.032 : .032, 1.6, 0) }, projectionMatrix: projection })) }),
              getPose: () => ({ transform: { matrix: matrix(0, 1.4, -.3) }, emulatedPosition: false }),
            }); });
          }
          cancelAnimationFrame(id) { cancelAnimationFrame(id); }
          async end() { if (!this.ended) { this.ended = true; this.dispatchEvent(new Event('end')); } }
        }
        const xr = new EventTarget();
        xr.isSessionSupported = async () => true;
        xr.requestSession = async () => {
          window.__requests++; if (mode === 'denied') throw new Error('permission denied');
          return window.__session = new Session();
        };
        Object.defineProperty(navigator, 'xr', { value: xr, configurable: true });
      } else Object.defineProperty(navigator, 'xr', { value: undefined, configurable: true });
    }, mode);
    await page.route('**/components/mep-viewer.tsx*', async route => {
      const response = await route.fetch(); let body = await response.text();
      const marker = 'controlsRef.current = controls;'; assert.ok(body.includes(marker));
      body = body.replace(marker, marker + ' window.__test = { camera, controls, renderer, get selected() { return selectedIndexRef.current; } };');
      await route.fulfill({ response, body });
    });
    await page.routeWebSocket('**', socket => socket.close());
    await page.route('**/mep-share', route => route.fulfill({ json: { publication: { id: 'vr-test', name: 'Maquette VR', revision: 1 }, role: 'viewer', packageUrl: url + '/gamepad.zip', scenario: { revision: 1, state: { valves: {}, sources: {}, markups: {} } }, events: [], realtimeToken: '' } }));
    await page.route('**/gamepad.zip', route => route.fulfill({ body: Buffer.from(archive) }));
    await page.goto(url + '/#/share/' + 'a'.repeat(64));
    await page.locator('.model-opening').waitFor({ state: 'hidden', timeout: 60000 });
    await page.waitForFunction(() => window.__test?.camera);
    const entry = page.getByRole('button', { name: 'Entrer en VR', exact: true });
    if (mode === 'desktop' || mode === 'phone') {
      assert.equal(await entry.count(), 0); assert.equal(await page.evaluate(() => window.__requests), 0);
      if (mode === 'desktop') await page.getByRole('button', { name: 'Maquette 3D', exact: true }).click();
      assert.equal(await page.evaluate(() => window.__test.renderer.xr.isPresenting), false);
    } else {
      await entry.waitFor(); assert.equal(await page.evaluate(() => window.__requests), 0);
      const before = await page.evaluate(() => ({ position: window.__test.camera.position.toArray(), quaternion: window.__test.camera.quaternion.toArray(), near: window.__test.camera.near, fov: window.__test.camera.fov }));
      await entry.click();
      if (mode === 'denied') {
        await page.getByRole('alert').filter({ hasText: 'La visite VR' }).waitFor();
        assert.equal(await page.evaluate(() => window.__test.renderer.xr.isPresenting), false);
        assert.equal(await entry.isEnabled(), true);
      } else {
        await page.waitForFunction(() => window.__test.renderer.xr.isPresenting && window.__test.renderer.xr.getCamera().cameras.length === 2).catch(async error => { console.error(await page.evaluate(() => ({ requests: window.__requests, presenting: window.__test.renderer.xr.isPresenting, eyes: window.__test.renderer.xr.getCamera().cameras.length, text: document.body.innerText.slice(-1500) }))); throw error; });
        await page.waitForTimeout(150);
        const initial = await page.evaluate(() => ({ world: window.__test.camera.getWorldPosition(window.__test.camera.position.clone()).toArray(), scale: window.__test.camera.parent.scale.x }));
        assert.ok(Math.abs(initial.scale - 1 / .3048) < 1e-8);
        assert.ok(Math.abs(initial.world[1] - 1.6 / .3048) < .1);
        await page.evaluate(() => {
          const event = new Event('inputsourceschange'); event.added = window.__session.inputSources; event.removed = []; window.__session.dispatchEvent(event);
        });
        await page.waitForTimeout(100);
        await page.evaluate(() => {
          const event = new Event('select'); event.inputSource = window.__session.inputSources[1]; event.frame = window.__frame; window.__session.dispatchEvent(event);
        });
        await page.waitForFunction(() => window.__test.selected === 1);
        // Start with neutral, then walk towards the wall with the left XR stick.
        await page.evaluate(() => window.__session.inputSources[0].gamepad.axes = [0, 0, 0, -1]);
        await page.waitForTimeout(250);
        const moved = await page.evaluate(() => window.__test.camera.getWorldPosition(window.__test.camera.position.clone()).toArray());
        assert.ok(Math.hypot(moved[0] - initial.world[0], moved[2] - initial.world[2]) > .1, 'XR walking must move in model coordinates');
        await page.evaluate(() => window.__session.inputSources[0].gamepad.axes = [0, 0, 0, 0]);
        await page.waitForTimeout(100);
        const yaw = await page.evaluate(() => window.__test.camera.parent.rotation.y);
        await page.evaluate(() => window.__session.inputSources[1].gamepad.axes = [0, 0, 1, 0]);
        await page.waitForTimeout(150);
        const turned = await page.evaluate(() => window.__test.camera.parent.rotation.y);
        assert.ok(Math.abs(turned - yaw + Math.PI / 6) < 1e-6);
        await page.waitForTimeout(150); assert.equal(await page.evaluate(() => window.__test.camera.parent.rotation.y), turned);
        await page.evaluate(() => window.__session.inputSources[1].gamepad.axes = [0, 0, 0, 0]);
        await page.getByRole('button', { name: 'Quitter la VR', exact: true }).click();
        await entry.waitFor(); await page.waitForFunction(() => !window.__test.renderer.xr.isPresenting);
        const after = await page.evaluate(() => ({ position: window.__test.camera.position.toArray(), quaternion: window.__test.camera.quaternion.toArray(), near: window.__test.camera.near, fov: window.__test.camera.fov, parent: !!window.__test.camera.parent }));
        assert.equal(after.parent, false); assert.deepEqual(after.position, before.position); assert.deepEqual(after.quaternion, before.quaternion); assert.equal(after.near, before.near); assert.equal(after.fov, before.fov);
        await entry.click(); await page.waitForFunction(() => window.__test.renderer.xr.isPresenting);
        await page.evaluate(() => window.__session.end()); await entry.waitFor();
      }
    }
    assert.deepEqual(errors, [], `${mode}: no browser errors`); console.log(`PASS ${mode}`); await page.close();
  }
} finally { await browser.close(); }

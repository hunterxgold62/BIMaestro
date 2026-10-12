import { test } from 'node:test';
import assert from 'node:assert/strict';
import { MepGamepad, stick, xbox, readBrowserPads } from '../lib/mep-gamepad.ts';

function pad({ index = 0, id = 'Xbox Wireless Controller', mapping = 'standard', connected = true, axes = [0, 0, 0, 0], buttons = {} } = {}) {
  return { index, id, mapping, connected, axes, buttons: Array.from({ length: 17 }, (_, i) => ({ value: buttons[i] || 0, pressed: (buttons[i] || 0) > .5 })) };
}
test('stick drift is ignored; analog strength and diagonal speed are bounded', () => {
  assert.deepEqual(stick(.1, -.1), [0, 0]);
  assert.deepEqual(stick(NaN, 1), [0, 0]);
  assert.deepEqual(stick(1, 0), [1, 0]);
  assert.ok(stick(.5, 0)[0] > 0 && stick(.5, 0)[0] < .5);
  assert.ok(Math.abs(Math.hypot(...stick(1, 1)) - 1) < 1e-12);
});
test('both sticks and triggers preserve Xbox direction and analog input', () => {
  const reader = new MepGamepad();
  const input = reader.read([pad({ axes: [.6, -1, -.4, .8], buttons: { [xbox.rt]: .7 } })]);
  assert.ok(input.moveX > 0 && input.moveY < 0 && input.lookX < 0 && input.lookY > 0);
  assert.ok(input.vertical > 0 && input.vertical < 1);
  assert.equal(input.active, true);
  assert.equal(reader.read([pad({ buttons: { [xbox.lt]: 1 } })]).vertical, -1);
  assert.equal(reader.read([pad({ buttons: { [xbox.lt]: .04, [xbox.rt]: .06 } })]).active, false);
});
test('actions fire once per press, including after release', () => {
  const reader = new MepGamepad(); reader.read([pad()]);
  assert.equal(reader.read([pad({ buttons: { [xbox.y]: 1 } })]).tapped[xbox.y], true);
  assert.equal(reader.read([pad({ buttons: { [xbox.y]: 1 } })]).tapped[xbox.y], false);
  assert.equal(reader.read([pad({ buttons: { [xbox.y]: 1 } })]).active, false);
  reader.read([pad()]);
  assert.equal(reader.read([pad({ buttons: { [xbox.y]: 1 } })]).tapped[xbox.y], true);
});
test('connecting while holding a button detects usage without firing the action', () => {
  const input = new MepGamepad().read([pad({ buttons: { [xbox.a]: 1 } })]);
  assert.equal(input.active, true); assert.equal(input.tapped[xbox.a], false);
});
test('disconnect stops every axis, trigger and held button immediately', () => {
  const reader = new MepGamepad(); reader.read([pad({ axes: [1, 0, 0, 0], buttons: { [xbox.lb]: 1, [xbox.rt]: 1 } })]);
  const input = reader.read([null, pad({ connected: false })]);
  assert.equal(input.name, null); assert.equal(input.moveX, 0); assert.equal(input.vertical, 0);
  assert.equal(input.active, false); assert.deepEqual(input.held, []);
});
test('reconnect does not inherit button edges from a disconnected pad', () => {
  const reader = new MepGamepad(); reader.read([pad()]); reader.read([]);
  assert.equal(reader.read([pad({ buttons: { [xbox.a]: 1 } })]).tapped[xbox.a], false);
  reader.read([pad()]);
  assert.equal(reader.read([pad({ buttons: { [xbox.a]: 1 } })]).tapped[xbox.a], true);
});
test('focus, loading or editing pauses input until all controls are released', () => {
  const reader = new MepGamepad(); reader.read([pad()]);
  const moving = pad({ axes: [1, 0, 0, 0], buttons: { [xbox.x]: 1 } });
  assert.equal(reader.read([moving], false).moveX, 0);
  assert.equal(reader.read([moving], true).moveX, 0);
  reader.read([pad()], true);
  const resumed = reader.read([moving], true);
  assert.equal(resumed.moveX, 1); assert.equal(resumed.tapped[xbox.x], true);
});
test('two cancelling triggers still count as activity and cannot rearm until released', () => {
  const reader = new MepGamepad(); reader.read([pad()], false);
  const triggers = pad({ buttons: { [xbox.lt]: .3, [xbox.rt]: .3 } });
  assert.equal(reader.read([triggers]).active, false);
  assert.equal(reader.read([pad({ axes: [1, 0, 0, 0] })]).moveX, 0);
  reader.read([pad()]);
  assert.equal(reader.read([triggers]).active, true);
});
test('unsupported mappings and disconnected entries do not control the viewer', () => {
  assert.equal(new MepGamepad().read([null, pad({ mapping: '' }), pad({ connected: false })]).name, null);
});
test('an active second controller can take over without phantom button actions', () => {
  const reader = new MepGamepad(); reader.read([pad()]);
  const second = pad({ index: 2, id: 'Xbox 2', buttons: { [xbox.y]: 1 } });
  const input = reader.read([pad(), null, second]);
  assert.equal(input.name, 'Xbox 2'); assert.equal(input.tapped[xbox.y], false);
  assert.equal(reader.read([pad(), pad({ index: 2, id: 'Xbox 2' })]).name, 'Xbox 2');
});
test('missing axes and buttons produce a neutral frame', () => {
  const input = new MepGamepad().read([{ ...pad(), axes: [], buttons: [] }]);
  assert.equal(input.active, false); assert.equal(input.vertical, 0);
});
test('a denied or absent browser Gamepad API fails safely', () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
  try {
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: {} });
    assert.deepEqual(readBrowserPads(), []);
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { getGamepads() { throw new Error('Permissions Policy'); } } });
    assert.deepEqual(readBrowserPads(), []);
  } finally { if (original) Object.defineProperty(globalThis, 'navigator', original); else delete globalThis.navigator; }
});

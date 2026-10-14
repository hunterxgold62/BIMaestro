import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import { supportsVr, SnapTurn, XR_FEET_PER_METRE, turnAboutHead, xrStick } from './mep-vr';

describe('VR capability and input', () => {
  it('does not enable VR on browsers without a headset or after permission errors', async () => {
    expect(await supportsVr()).toBe(false);
    expect(await supportsVr({ isSessionSupported: async () => false })).toBe(false);
    expect(await supportsVr({ isSessionSupported: async () => { throw new Error('denied'); } })).toBe(false);
    let mode = '';
    expect(await supportsVr({ isSessionSupported: async value => { mode = value; return true; } })).toBe(true);
    expect(mode).toBe('immersive-vr');
  });
  it('requires neutral before a 30 degree turn and release before another', () => {
    const snap = new SnapTurn();
    expect(snap.read(1)).toBe(0); snap.read(0);
    expect(snap.read(1)).toBeCloseTo(-Math.PI / 6);
    expect(snap.read(1)).toBe(0); expect(snap.read(.3)).toBe(0);
    snap.read(0); expect(snap.read(-1)).toBeCloseTo(Math.PI / 6);
  });
  it('uses XR thumbstick axes, ignoring touchpad and unknown layouts', () => {
    const source = (mapping: string, axes: number[]) => ({ gamepad: { mapping, axes } }) as unknown as XRInputSource;
    expect(xrStick(source('xr-standard', [1, 1, 0, 0]))).toEqual([0, 0]);
    expect(xrStick(source('standard', [0, 0, 1, 1]))).toEqual([0, 0]);
    expect(xrStick(source('xr-standard', [0, 0, 1, 0]))).toEqual([1, 0]);
  });
});
describe('VR coordinates in a Revit model', () => {
  it('renders a metre at life size and preserves the head position when turning', () => {
    const rig = new THREE.Group(), camera = new THREE.PerspectiveCamera();
    rig.scale.setScalar(XR_FEET_PER_METRE); rig.position.set(100, 40, -60); rig.add(camera);
    camera.position.set(.4, 1.6, -.2); rig.updateMatrixWorld(true);
    const before = camera.getWorldPosition(new THREE.Vector3());
    expect(before.y - 40).toBeCloseTo(1.6 / .3048);
    const modelPoint = new THREE.Vector3(100 + XR_FEET_PER_METRE, 40, -60);
    expect(rig.worldToLocal(modelPoint).x).toBeCloseTo(1);
    turnAboutHead(rig, camera, Math.PI / 6);
    expect(camera.getWorldPosition(new THREE.Vector3()).distanceTo(before)).toBeLessThan(1e-8);
  });
});

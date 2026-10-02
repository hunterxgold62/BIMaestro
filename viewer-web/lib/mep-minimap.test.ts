import { describe, it, expect, vi } from 'vitest';
import * as THREE from 'three';
import { MeshBVH } from 'three-mesh-bvh';
import { MepMiniMap } from './mep-minimap';

function setup(bvh = false) {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([0, 0, 0, 10, 0, 0, 0, 2, 0], 3));
  if (bvh) geometry.boundsTree = new MeshBVH(geometry);
  const mesh = new THREE.Mesh(geometry); mesh.position.x = 5;
  const camera = new THREE.PerspectiveCamera();
  const moveTo = vi.fn(); const lineTo = vi.fn();
  const ctx = new Proxy({ moveTo, lineTo }, { get: (target, key) => key in target ? target[key as keyof typeof target] : vi.fn() });
  const canvas = { width: 280, getContext: () => ctx } as unknown as HTMLCanvasElement;
  return { mesh, camera, canvas, moveTo, lineTo };
}

describe('local mini-map', () => {
  it.each([false, true])('slices transformed geometry using BVH=%s', bvh => {
    const { mesh, camera, canvas, moveTo, lineTo } = setup(bvh);
    new MepMiniMap().draw(canvas, camera, [mesh], 0, 1);
    const scale = 256 / (30 / .3048);
    expect(moveTo.mock.calls[0][0]).toBeCloseTo(140 + 10 * scale);
    expect(moveTo.mock.calls[0][1]).toBe(140);
    expect(lineTo.mock.calls[0][0]).toBeCloseTo(140 + 5 * scale);
  });
  it('excludes hidden geometry and other storeys', () => {
    const { mesh, camera, canvas, moveTo } = setup(true);
    const parent = new THREE.Group(); parent.add(mesh); parent.visible = false;
    const map = new MepMiniMap(); map.draw(canvas, camera, [mesh], 0, 1);
    expect(moveTo).toHaveBeenCalledTimes(1); // Camera arrow only.
    moveTo.mockClear(); parent.visible = true;
    map.draw(canvas, camera, [mesh], 1100, 5);
    expect(moveTo).toHaveBeenCalledTimes(1);
  });
});

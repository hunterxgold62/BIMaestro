import { it, expect, vi, afterEach } from 'vitest';
import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { gzipSync } from 'fflate';
import { resolveTiles } from './mep-api';
import { ModelStream } from './mep-stream';
import { digest } from './mep-package';
vi.mock('./mep-api', () => ({ resolveTiles: vi.fn() }));
vi.mock('fflate', async importOriginal => {
  const actual = await importOriginal<typeof import('fflate')>();
  return { ...actual, gunzip: (data: Uint8Array, callback: (error: null, data: Uint8Array) => void) => callback(null, actual.gunzipSync(data)) };
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.clearAllMocks(); });

async function fixture(count: number) {
  const data = gzipSync(new Uint8Array([1])), hash = await digest(data);
  const tiles = Array.from({ length: count }, (_, id) => ({ name: `tile-${String(id).padStart(5, '0')}.glb.gz`, size: data.length, sha256: hash, decodedBytes: 1, bounds: [id * 100, 0, 0, id * 100 + 1, 1, 1] as [number, number, number, number, number, number], elements: [7] }));
  vi.mocked(resolveTiles).mockImplementation(async (_token, _revision, names) => ({ tiles: names.map(name => ({ name, url: 'https://example.test/' + name, bytes: data.length, sha256: hash })) }));
  const fetcher = vi.fn(async () => new Response(data)); vi.stubGlobal('fetch', fetcher);
  vi.spyOn(GLTFLoader.prototype, 'parseAsync').mockImplementation(async () => {
    const scene = new THREE.Group(); scene.add(new THREE.Mesh(new THREE.BoxGeometry()));
    return { scene } as Awaited<ReturnType<GLTFLoader['parseAsync']>>;
  });
  return { tiles, fetcher };
}

it('waits for every detailed tile and retains edited walls for the entire session', async () => {
  const { tiles, fetcher } = await fixture(30);
  let finish!: () => void;
  const lastPreparation = new Promise<void>(resolve => { finish = resolve; });
  const models: THREE.Group[] = [];
  const add = vi.fn(async (model: THREE.Group) => { models.push(model); if (models.length === tiles.length) await lastPreparation; });
  const remove = vi.fn(), error = vi.fn(), changed = vi.fn();
  const stream = new ModelStream(tiles, 'test', 1, add, remove, changed, error);
  try {
    expect(stream.loading).toBe(true); stream.update(performance.now());
    await vi.waitFor(() => expect(add).toHaveBeenCalledTimes(30));
    expect(stream.count).toBe(29); expect(stream.loading).toBe(true); expect(stream.complete(7)).toBe(false);
    finish(); await vi.waitFor(() => expect(stream.loading).toBe(false));
    expect(stream.count).toBe(30); expect(stream.complete(7)).toBe(true);
    expect(vi.mocked(resolveTiles).mock.calls.every(call => call[2].length <= 24)).toBe(true);
    const wall = models[0].children[0] as THREE.Mesh;
    const cutGeometry = new THREE.BoxGeometry(.5, .5, .5); wall.geometry = cutGeometry;
    const downloads = fetcher.mock.calls.length, changes = changed.mock.calls.length;
    // Camera movement no longer participates in the loader API.
    for (let now = 100000; now < 1000000; now += 1000) stream.update(now);
    expect(wall.geometry).toBe(cutGeometry); expect(remove).not.toHaveBeenCalled();
    expect(fetcher).toHaveBeenCalledTimes(downloads); expect(changed).toHaveBeenCalledTimes(changes);
    expect(error).not.toHaveBeenCalled();
  } finally { finish(); stream.dispose(); }
});

it('stays incomplete after a download failure and retries without discarding prepared geometry', async () => {
  const { tiles, fetcher } = await fixture(2);
  fetcher.mockResolvedValueOnce(new Response('', { status: 503 }));
  const add = vi.fn(), remove = vi.fn(), error = vi.fn();
  const stream = new ModelStream(tiles, 'test', 1, add, remove, () => {}, error);
  try {
    stream.update(performance.now());
    await vi.waitFor(() => expect(error).toHaveBeenCalledOnce());
    await vi.waitFor(() => expect(stream.count).toBe(1));
    expect(stream.loading).toBe(true); expect(stream.complete(7)).toBe(false);
    stream.update(performance.now() + 31000);
    await vi.waitFor(() => expect(stream.loading).toBe(false));
    expect(add).toHaveBeenCalledTimes(2); expect(remove).not.toHaveBeenCalled();
  } finally { stream.dispose(); }
});

it('does not attach prepared geometry after the viewer is closed', async () => {
  const { tiles } = await fixture(1);
  let finish!: () => void;
  const preparation = new Promise<void>(resolve => { finish = resolve; });
  const add = vi.fn(() => preparation), remove = vi.fn(), changed = vi.fn();
  const stream = new ModelStream(tiles, 'test', 1, add, remove, changed, vi.fn());
  stream.update(performance.now());
  await vi.waitFor(() => expect(add).toHaveBeenCalledOnce());
  stream.dispose(); finish();
  await vi.waitFor(() => expect(remove).toHaveBeenCalledOnce());
  expect(stream.count).toBe(0); expect(changed).not.toHaveBeenCalled();
});

import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';
import ts from '../../../viewer-web/node_modules/typescript/lib/typescript.js';
const source = readFileSync(new URL('./assets.ts', import.meta.url), 'utf8');
const { outputText } = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } });
const { validateAssets, exportPaths } = await import('data:text/javascript;base64,' + Buffer.from(outputText).toString('base64'));
const assets = [{ name: 'index.zip', bytes: 100, sha256: 'a'.repeat(64) }, { name: 'tile-00000.glb.gz', bytes: 200, sha256: 'b'.repeat(64) }];
assert.equal(validateAssets(undefined, 100), null);
assert.deepEqual(validateAssets(assets, 300), assets);
assert.throws(() => validateAssets(assets, 299));
for (const patch of [{ name: '../index.zip' }, { name: 'index.zip' }, { bytes: -1 }, { bytes: 49 * 1024 * 1024 }, { sha256: 'invalid' }]) assert.throws(() => validateAssets([assets[0], { ...assets[1], ...patch }], 300));
assert.deepEqual(exportPaths({ storage_path: 'id/1/index.zip', manifest: { assets } }), ['id/1/index.zip', 'id/1/tile-00000.glb.gz']);
assert.deepEqual(exportPaths({ storage_path: 'id/1.bimaestro-mep.zip' }), ['id/1.bimaestro-mep.zip']);
// Synthetic declarations exercise the transfer contract without writing to R2.
const MiB = 1024 ** 2;
function tiledPackage(total) {
  const tiles = [{ ...assets[0], bytes: MiB }];
  for (let left = total - MiB; left > 0; ) {
    const bytes = Math.min(left, 8 * MiB);
    tiles.push({ ...assets[1], name: 'tile-' + String(tiles.length).padStart(5, '0') + '.glb.gz', bytes });
    left -= bytes;
  }
  return tiles;
}
for (const size of [600 * MiB, 2048 * MiB]) assert.deepEqual(validateAssets(tiledPackage(size), size), tiledPackage(size));
const oversized = 2048 * MiB + 1;
assert.throws(() => validateAssets(tiledPackage(oversized), oversized));
assert.throws(() => validateAssets([assets[0], { ...assets[1], bytes: 600 * MiB }], 600 * MiB + 100));
console.log('Progressive assets: validation and legacy cleanup passed');

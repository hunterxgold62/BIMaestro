import { describe, it, expect } from 'vitest';
import { manifestSchema, type ModelTile } from './mep-contract';

const tile = (n: number, x: number, elements = [n]): ModelTile => ({ name: `tile-${String(n).padStart(5, '0')}.glb.gz`, size: 100, decodedBytes: 1000, sha256: 'a'.repeat(64), bounds: [x, 0, 0, x + 1, 1, 1], elements });
describe('compatibilité des exports', () => {
  it('conserve les anciens exports et refuse les zones absentes ou dupliquées', () => {
    const base = { name: 'Test', units: 'feet', coordinateSystem: 'Y-up', files: {} };
    expect(manifestSchema.safeParse({ ...base, schemaVersion: 1 }).success).toBe(true);
    expect(manifestSchema.safeParse({ ...base, schemaVersion: 2 }).success).toBe(false);
    expect(manifestSchema.safeParse({ ...base, schemaVersion: 2, tiles: [tile(0, 0)] }).success).toBe(true);
    expect(manifestSchema.safeParse({ ...base, schemaVersion: 2, tiles: [tile(0, 0), tile(0, 0)] }).success).toBe(false);
  });
});

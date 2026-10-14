import { describe, expect, it } from 'vitest';
import { locateRoom, roomContains } from './mep-room';
import type { ViewerRoom } from './mep-contract';
const square = (a: number, b: number) => [[a, 0, a], [b, 0, a], [b, 0, b], [a, 0, b]];
const room: ViewerRoom = { key: 'r', name: 'Pomperie', number: '01', levelName: 'RDC', kind: 'room', baseElevation: 0, declaredTop: 6.56, navigationTop: 9.84, loops: [square(0, 10)] };
describe('repérage des locaux', () => {
  it('retient une pièce déclarée à 2m en vol à 2.8m sans changer sa hauteur', () => {
    expect(locateRoom([room], 5, 9.18, 5)?.approximate).toBe(true);
    expect(room.declaredTop).toBe(6.56);
  });
  it('ne confond pas les étages', () => {
    const upper = { ...room, key: 'upper', baseElevation: 9.84, declaredTop: 18, navigationTop: 20 };
    expect(locateRoom([room, upper], 5, 10, 5)?.room.key).toBe('upper');
  });
  it('exclut les trous et les positions hors emprise', () => {
    expect(roomContains({ ...room, loops: [square(0, 10), square(3, 7)] }, 5, 5)).toBe(false);
    expect(locateRoom([room], 11, 1, 5)).toBeNull();
  });
  it('refuse une identification ambiguë et préfère une pièce à son espace MEP', () => {
    expect(locateRoom([room, { ...room, key: 'other' }], 5, 1, 5)).toBeNull();
    expect(locateRoom([room, { ...room, kind: 'space', key: 's' }], 5, 1, 5)?.room.key).toBe('r');
  });
});

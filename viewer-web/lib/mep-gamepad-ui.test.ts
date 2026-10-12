import { expect, it } from 'vitest';
import { GamepadMenuRepeat } from './mep-gamepad-ui';
import { xbox, type PadInput } from './mep-gamepad';
const input = (held: number[], tapped: number[] = []): PadInput => ({ name: 'Xbox', moveX: 0, moveY: 0, lookX: 0, lookY: 0, vertical: 0, active: true, held: Array.from({ length: 17 }, (_, i) => held.includes(i)), tapped: Array.from({ length: 17 }, (_, i) => tapped.includes(i)) });
it('repeats directional adjustments after a delay and stops when released or disabled', () => {
  const reader = new GamepadMenuRepeat();
  expect(reader.read(input([xbox.right], [xbox.right]), 0, true)).toEqual([xbox.right]);
  expect(reader.read(input([xbox.right]), 399, true)).toEqual([]);
  expect(reader.read(input([xbox.right]), 400, true)).toEqual([xbox.right]);
  expect(reader.read(input([xbox.right]), 450, true)).toEqual([]);
  expect(reader.read(input([]), 500, true)).toEqual([]);
  expect(reader.read(input([xbox.right]), 900, true)).toEqual([]);
  reader.read(input([xbox.right], [xbox.right]), 1000, true);
  reader.read(input([xbox.right]), 1100, false);
  expect(reader.read(input([xbox.right]), 2000, true)).toEqual([]);
});

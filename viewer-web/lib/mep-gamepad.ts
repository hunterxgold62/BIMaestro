// Xbox controllers expose this layout through the browser's standard mapping.
// Ignore unknown mappings rather than guessing axes or triggering wrong actions.
export type Pad = Pick<Gamepad, 'id' | 'index' | 'connected' | 'mapping' | 'axes' | 'buttons'>;
export const xbox = { a: 0, b: 1, x: 2, y: 3, lb: 4, rb: 5, lt: 6, rt: 7, view: 8, menu: 9, ls: 10, rs: 11, up: 12, down: 13, left: 14, right: 15 } as const;
export type PadInput = {
  name: string | null;
  moveX: number; moveY: number; lookX: number; lookY: number; vertical: number;
  held: readonly boolean[]; tapped: readonly boolean[]; active: boolean;
};
export function stick(x = 0, y = 0, deadZone = .18): [number, number] {
  if (!Number.isFinite(x) || !Number.isFinite(y)) return [0, 0];
  const length = Math.hypot(x, y);
  if (length <= deadZone) return [0, 0];
  const scale = Math.min(1, (length - deadZone) / (1 - deadZone)) / length;
  return [x * scale, y * scale];
}
const trigger = (value = 0) => Number.isFinite(value) ? Math.max(0, Math.min(1, (value - .08) / .92)) : 0;
const idle = (name: string | null = null): PadInput => ({ name, moveX: 0, moveY: 0, lookX: 0, lookY: 0, vertical: 0, held: [], tapped: [], active: false });

export class MepGamepad {
  private key: string | null = null;
  private previous: boolean[] = [];
  private armed = true;
  read(pads: readonly (Pad | null)[], enabled = true): PadInput {
    const compatible = pads.filter((pad): pad is Pad => !!pad?.connected && pad.mapping === 'standard');
    const isActive = (pad: Pad) => {
      const [mx, my] = stick(pad.axes[0], pad.axes[1]);
      const [lx, ly] = stick(pad.axes[2], pad.axes[3]);
      return !!(mx || my || lx || ly || pad.buttons.some(button => button.pressed || button.value > .15));
    };
    const pad = compatible.find(isActive) || compatible.find(pad => `${pad.index}:${pad.id}` === this.key) || compatible[0];
    if (!pad) { this.key = null; this.previous = []; this.armed = true; return idle(); }
    const key = `${pad.index}:${pad.id}`;
    const held = pad.buttons.map(button => button.pressed || button.value > .5);
    const changed = this.key !== key;
    const tapped = held.map((value, index) => value && !changed && !this.previous[index]);
    this.key = key; this.previous = held;
    const [moveX, moveY] = stick(pad.axes[0], pad.axes[1]);
    const [lookX, lookY] = stick(pad.axes[2], pad.axes[3]);
    const up = trigger(pad.buttons[xbox.rt]?.value), down = trigger(pad.buttons[xbox.lt]?.value);
    const neutral = !moveX && !moveY && !lookX && !lookY && !up && !down && !held.some(Boolean);
    if (!enabled) { this.armed = false; return idle(pad.id); }
    // After a tab switch or text edit, require release before allowing movement.
    if (!this.armed) { if (neutral) this.armed = true; return idle(pad.id); }
    return { name: pad.id, moveX, moveY, lookX, lookY, vertical: up - down, held, tapped, active: !!(moveX || moveY || lookX || lookY || up || down || tapped.some(Boolean) || (changed && held.some(Boolean))) };
  }
}

export function readBrowserPads(): readonly (Gamepad | null)[] {
  try { return typeof navigator.getGamepads === 'function' ? Array.from(navigator.getGamepads()) : []; }
  catch { return []; } // Unsupported browser or a restrictive Permissions Policy.
}

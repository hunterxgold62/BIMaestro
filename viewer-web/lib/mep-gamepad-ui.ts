import { xbox, type PadInput } from './mep-gamepad';

export type GamepadField = HTMLInputElement | HTMLTextAreaElement;
const selector = 'button,a[href],input:not([type=hidden]),textarea,select,summary,[role=slider],[role=switch],[role=checkbox],[tabindex]:not([tabindex="-1"])';
export function gamepadScope(root: HTMLElement): HTMLElement {
  for (const selector of ['.gamepad-field-editor', '.mep-impact-panel', '.lots-panel', '.element-context-menu', '.markup-editor', '.markup-card']) {
    const panel = root.querySelector<HTMLElement>(selector);
    if (panel && getComputedStyle(panel).visibility !== 'hidden') return panel;
  }
  return root;
}
export function gamepadControls(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(selector)).filter(element =>
    !element.matches(':disabled,[aria-disabled=true],canvas') && !element.closest('[inert]') &&
    element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden');
}
export function focusGamepadControl(root: HTMLElement, direction: number) {
  const items = gamepadControls(root), index = items.indexOf(document.activeElement as HTMLElement);
  const next = items[index < 0 ? (direction > 0 ? 0 : items.length - 1) : (index + direction + items.length) % items.length];
  next?.focus({ preventScroll: true }); next?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
}
export function setGamepadValue(element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement, value: string) {
  const prototype = element instanceof HTMLSelectElement ? HTMLSelectElement.prototype : element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(prototype, 'value')?.set?.call(element, value);
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }));
}
export function adjustGamepadControl(element: HTMLElement | HTMLSelectElement, direction: number, fast = false): boolean {
  if (element instanceof HTMLSelectElement) {
    const options = Array.from(element.options).filter(option => !option.disabled && !option.closest('optgroup:disabled'));
    const index = options.findIndex(option => option.value === element.value);
    const next = options[Math.max(0, Math.min(options.length - 1, index + direction))];
    if (next) setGamepadValue(element, next.value);
    return true;
  }
  if (element instanceof HTMLInputElement && ['number', 'range'].includes(element.type)) {
    const step = element.step === 'any' ? .1 : Number(element.step || 1);
    const minimum = element.min === '' ? -Infinity : Number(element.min), maximum = element.max === '' ? Infinity : Number(element.max);
    const value = Math.max(minimum, Math.min(maximum, (Number(element.value) || 0) + direction * step * (fast ? 10 : 1)));
    setGamepadValue(element, String(Math.round(value * 1e8) / 1e8));
    return true;
  }
  if (element.matches('[role=slider]')) {
    element.dispatchEvent(new KeyboardEvent('keydown', { key: direction > 0 ? 'ArrowRight' : 'ArrowLeft', bubbles: true, cancelable: true, shiftKey: fast }));
    return true;
  }
  return false;
}
export function moveGamepadKey(root: HTMLElement, dx: number, dy: number) {
  const items = gamepadControls(root), focused = document.activeElement as HTMLElement;
  if (!items.includes(focused)) { focusGamepadControl(root, 1); return; }
  const rect = focused.getBoundingClientRect(), x = rect.x + rect.width / 2, y = rect.y + rect.height / 2;
  const candidates = items.filter(item => item !== focused).map(item => {
    const r = item.getBoundingClientRect(), vx = r.x + r.width / 2 - x, vy = r.y + r.height / 2 - y;
    return { item, along: dx ? vx * dx : vy * dy, across: Math.abs(dx ? vy : vx) };
  }).filter(item => item.along > 2).sort((a, b) => (a.along + a.across * 4) - (b.along + b.across * 4));
  candidates[0]?.item.focus({ preventScroll: true }); candidates[0]?.item.scrollIntoView({ block: 'nearest' });
}
export class GamepadMenuRepeat {
  private next = new Map<number, number>();
  read(pad: PadInput, now: number, enabled: boolean): number[] {
    if (!enabled) { this.next.clear(); return []; }
    const result: number[] = [];
    for (const key of [xbox.up, xbox.down, xbox.left, xbox.right]) {
      if (!pad.held[key]) this.next.delete(key);
      else if (pad.tapped[key]) { result.push(key); this.next.set(key, now + 400); }
      else if (now >= (this.next.get(key) ?? Infinity)) { result.push(key); this.next.set(key, now + 100); }
    }
    return result;
  }
}

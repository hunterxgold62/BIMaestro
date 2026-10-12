import { useEffect, useRef, useState } from 'react';
import { setGamepadValue, type GamepadField } from '@/lib/mep-gamepad-ui';

export function GamepadFieldEditor({ target, onClose }: { target: GamepadField; onClose: () => void }) {
  const [value, setValue] = useState(target.value), [upper, setUpper] = useState(false), [error, setError] = useState('');
  const root = useRef<HTMLDivElement>(null);
  const kind = target instanceof HTMLInputElement ? target.type : 'text';
  const label = target.getAttribute('aria-label') || target.labels?.[0]?.textContent || target.placeholder || 'Saisie';
  const keys = kind === 'number' ? '1234567890.-' : kind === 'color' ? '#0123456789abcdef' : 'azertyuiopqsdfghjklmwxcvbn0123456789éèàçù.,!?-_/';
  useEffect(() => { root.current?.querySelector<HTMLButtonElement>('button')?.focus(); }, []);
  const append = (text: string) => setValue(current => (current + text).slice(0, target.maxLength > 0 ? target.maxLength : 2000));
  const apply = () => {
    if (!target.isConnected) { onClose(); return; }
    if (kind === 'number' && (!value.trim() || !Number.isFinite(Number(value)) || (target instanceof HTMLInputElement && ((target.min !== '' && Number(value) < Number(target.min)) || (target.max !== '' && Number(value) > Number(target.max)))))) {
      setError('Saisissez une valeur dans les limites du champ.'); return;
    }
    if (kind === 'color' && !/^#[0-9a-f]{6}$/i.test(value)) { setError('Choisissez une couleur ou saisissez un code #RRGGBB.'); return; }
    setGamepadValue(target, value); target.dispatchEvent(new Event('change', { bubbles: true })); onClose();
  };
  return <div ref={root} className="gamepad-field-editor" role="dialog" aria-modal="true" aria-label={`Saisir : ${label}`}>
    <header><strong>{label}</strong><button data-editor-action="cancel" type="button" onClick={onClose}>Annuler · B</button></header>
    <output className="gamepad-field-value" aria-live="polite" style={kind === 'color' && /^#[0-9a-f]{6}$/i.test(value) ? { borderColor: value } : undefined}>{value || '…'}</output>
    <p>Croix : choisir · LB / RB : précédent / suivant · A : saisir · X : effacer{kind === 'text' ? ' · Y : espace' : ''}</p>
    {kind === 'color' && <div className="gamepad-palette">{['#0284c7', '#c2410c', '#7c3aed', '#16a34a', '#dc2626', '#eab308', '#db2777', '#0891b2', '#ffffff', '#111827'].map(color => <button type="button" key={color} aria-label={color} style={{ background: color }} onClick={() => setValue(color)}/>)}</div>}
    <div className={`gamepad-keyboard ${kind === 'number' ? 'numeric' : ''}`}>{Array.from(keys).map(key => <button type="button" key={key} onClick={() => append(upper ? key.toUpperCase() : key)}>{upper ? key.toUpperCase() : key}</button>)}</div>
    <div className="gamepad-keyboard-actions">
      {kind === 'text' && <><button type="button" onClick={() => setUpper(v => !v)}>Majuscule</button><button data-editor-action="space" type="button" onClick={() => append(' ')}>Espace · Y</button></>}
      <button data-editor-action="delete" type="button" onClick={() => setValue(v => v.slice(0, -1))}>Effacer · X</button>
      <button type="button" onClick={() => setValue('')}>Tout effacer</button>
      <button data-editor-action="apply" type="button" onClick={apply}>Valider</button>
    </div>
    {error && <p role="alert">{error}</p>}
  </div>;
}

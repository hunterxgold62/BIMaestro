'use client';
import { useEffect, useState } from 'react';
import { supportsVr } from '@/lib/mep-vr';
import { Button } from './ui/button';

export function VrEntry({ active, disabled, enter, exit }: { active: boolean; disabled: boolean; enter: () => Promise<void>; exit: () => Promise<void> }) {
  const [supported, setSupported] = useState(false), [pending, setPending] = useState(false), [error, setError] = useState('');
  useEffect(() => {
    let alive = true;
    const xr = window.isSecureContext ? navigator.xr : undefined;
    const check = () => void supportsVr(xr).then(value => { if (alive) setSupported(value); });
    check(); xr?.addEventListener('devicechange', check);
    return () => { alive = false; xr?.removeEventListener('devicechange', check); };
  }, []);
  if (!supported && !active) return null;
  return <span><Button size="sm" variant="secondary" disabled={pending || (!active && disabled)} onClick={() => {
    setError(''); setPending(true);
    void (active ? exit() : enter()).catch(() => setError('La visite VR n’a pas démarré. Autorisez la VR dans le navigateur du casque puis réessayez.')).finally(() => setPending(false));
  }}>{pending ? 'Connexion au casque…' : active ? 'Quitter la VR' : 'Entrer en VR'}</Button>{error && <span role="alert">{error}</span>}</span>;
}

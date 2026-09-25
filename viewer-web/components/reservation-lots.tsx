import { useState } from 'react';
import type { LotOption, ReservationLot } from '@/lib/mep-lots';

function LotForm({ lot, onSave, onSaved }: { lot?: LotOption; onSave: (id: string, lot: ReservationLot) => Promise<void>; onSaved: (id: string, created: boolean) => void }) {
  const [name, setName] = useState(lot?.name || '');
  const [color, setColor] = useState(lot?.color || '#0284c7');
  const [busy, setBusy] = useState(false), [error, setError] = useState('');
  return <form onSubmit={async event => {
    event.preventDefault(); if (busy) return; setBusy(true); setError('');
    const id = lot?.id || crypto.randomUUID();
    try { await onSave(id, { name: name.trim(), color }); onSaved(id, !lot); }
    catch (caught) { setError((caught as Error).message); }
    finally { setBusy(false); }
  }}>
    <label>Nom du lot<input required maxLength={40} value={name} onChange={event => setName(event.target.value)}/></label>
    <label>Couleur du lot<input type="color" value={color} onChange={event => setColor(event.target.value)}/></label>
    <p>Le nom et la couleur s’appliquent à toutes les réservations de ce lot, pour tous les utilisateurs.</p>
    {error && <p role="alert">{error}</p>}
    <button type="submit" disabled={busy || !name.trim()}>{busy ? 'Enregistrement…' : lot ? 'Enregistrer le lot' : 'Créer le lot'}</button>
  </form>;
}
export function ReservationLotsPanel({ lots, editable, onSave, onClose, onSaved }: { lots: LotOption[]; editable: boolean; onSave: (id: string, lot: ReservationLot) => Promise<void>; onClose: () => void; onSaved: (id: string, created: boolean) => void }) {
  const [selected, setSelected] = useState<string | null>(lots[0]?.id || null);
  const lot = lots.find(item => item.id === selected);
  return <aside className="lots-panel" aria-label="Gestion des lots">
    <header><strong>Lots de réservation</strong><button onClick={onClose} aria-label="Fermer les lots">Fermer</button></header>
    <div className="lots-tabs" role="tablist" aria-label="Lots">{lots.map(item => <button role="tab" aria-selected={item.id === selected} key={item.id} onClick={() => setSelected(item.id)}><span className="lot-swatch" style={{ background: item.color }}/>{item.name}</button>)}</div>
    {editable && <button onClick={() => setSelected(null)}>+ Nouveau lot</button>}
    {editable ? <LotForm key={`${selected}:${lot?.name}:${lot?.color}`} lot={lot} onSave={onSave} onSaved={(id, created) => { setSelected(id); onSaved(id, created); }}/>
      : <p>Les noms et les couleurs sont communs à tous. Utilisez un lien de modification pour les changer.</p>}
  </aside>;
}

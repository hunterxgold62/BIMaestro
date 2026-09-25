'use client';

import { Button } from '@/components/ui/button';
import { Slider } from '@/components/ui/slider';
import { maximumSections, type FaceSection } from '@/lib/mep-section';

type Props = {
  value: FaceSection[]; selected: string | null; picking: boolean; extent: number;
  onChange: (value: FaceSection[]) => void; onSelect: (id: string | null) => void; onPick: (picking: boolean) => void;
};
export function SectionControls({ value, selected, picking, extent, onChange, onSelect, onPick }: Props) {
  const active = value.find(item => item.id === selected);
  const update = (change: Partial<FaceSection>) => onChange(value.map(item => item.id === selected ? { ...item, ...change } : item));
  return <section className="section-controls" aria-label="Coupes de la maquette">
    <div className="section-heading"><strong>Coupes ({value.length}/{maximumSections})</strong>
      <Button size="sm" variant="ghost" disabled={!value.length} onClick={() => { onChange([]); onSelect(null); onPick(false); }}>Tout réafficher</Button></div>
    <Button aria-pressed={picking} disabled={value.length >= maximumSections && !picking}
      onClick={() => onPick(!picking)}>{picking ? 'Annuler la sélection' : '+ Cliquer une face'}</Button>
    <p role="status">{picking ? 'Cliquez une face du bâtiment. La coupe suit son orientation.' : 'Ajoutez plusieurs faces pour isoler la zone à examiner.'}</p>
    <p className="section-shortcut"><strong>Ctrl + molette</strong><span>Déplacer la coupe sélectionnée avec précision.</span></p>
    {value.length > 0 && <div className="section-list" role="group" aria-label="Choisir une coupe">
      {value.map(item => <Button key={item.id} size="sm" variant={item.id === selected ? 'default' : 'outline'} aria-pressed={item.id === selected}
        onClick={() => onSelect(item.id)}>{item.name}{item.enabled ? '' : ' · inactive'}</Button>)}
    </div>}
    {active && <>
      <label id="section-position-label">Décalage depuis la face <output>{(active.offset * .3048).toFixed(2)} m</output></label>
      <Slider aria-labelledby="section-position-label" value={[active.offset * .3048]} min={-extent} max={extent} step={.01}
        onValueChange={next => update({ offset: (Array.isArray(next) ? next[0] : next) / .3048 })}/>
      <div className="section-heading">
        <Button size="sm" variant="outline" aria-pressed={active.enabled} onClick={() => update({ enabled: !active.enabled })}>{active.enabled ? 'Désactiver' : 'Activer'}</Button>
        <Button size="sm" variant="outline" aria-pressed={active.inverted} onClick={() => update({ inverted: !active.inverted })}>Inverser le côté</Button>
        <Button size="sm" variant="ghost" onClick={() => { const remaining = value.filter(item => item.id !== selected); onChange(remaining); onSelect(remaining.at(-1)?.id ?? null); }}>Supprimer</Button>
      </div>
    </>}
  </section>;
}

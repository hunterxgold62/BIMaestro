import { useEffect, useState } from 'react';
import { dimensionDistance, moveToDimension, type Markup, type ReservationDimension } from '@/lib/mep-markup';
import type { LotOption } from '@/lib/mep-lots';

function DistanceField({ mark, dimension, onChange }: { mark: Markup; dimension: ReservationDimension; onChange: (mark: Markup) => void }) {
  const distance = Math.round(dimensionDistance(mark, dimension) * 10) / 10;
  const [value, setValue] = useState(String(distance));
  const [error, setError] = useState('');
  useEffect(() => { setValue(String(distance)); }, [distance]);
  const apply = () => {
    try { if (!value.trim()) throw new Error('Saisissez une distance.'); onChange(moveToDimension(mark, dimension, Number(value))); setError(''); }
    catch (caught) { setError((caught as Error).message); }
  };
  return <div className="reservation-dimension"><label>{dimension.elementName} · distance à l’axe (cm)
    <input type="number" required step="0.1" min="-100000" max="100000" aria-invalid={!!error} value={value} onChange={event => setValue(event.target.value)} onBlur={apply} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); event.currentTarget.blur(); } }}/></label>
    <div className="markup-actions"><button type="button" onClick={() => onChange({ ...mark, dimensions: mark.dimensions?.filter(item => item.id !== dimension.id) })}>Retirer</button></div>
    {error && <p role="alert">{error}</p>}
  </div>;
}

export function ReservationFields({ mark, lots, onChange, picking, onPick, onManageLots }: { mark: Markup; lots: LotOption[]; onChange: (mark: Markup) => void; picking: boolean; onPick: () => void; onManageLots: () => void }) {
  return <>
    <label>Forme<select value={mark.shape || 'rectangle'} onChange={event => onChange({ ...mark, shape: event.target.value as Markup['shape'], diameterCm: mark.diameterCm || mark.widthCm })}><option value="rectangle">Rectangulaire</option><option value="round">Ronde · carottage</option></select></label>
    <label>Lot<select value={mark.lot || 'MEP'} onChange={event => onChange({ ...mark, lot: event.target.value })}>{lots.map(lot => <option key={lot.id} value={lot.id}>{lot.name}</option>)}</select></label>
    <button className="manage-lots-link" type="button" onClick={onManageLots}>Gérer / créer un lot</button>
    <div className="markup-dimensions">{(mark.shape === 'round' ? ['diameterCm', 'depthCm'] as const : ['widthCm', 'heightCm', 'depthCm'] as const).map(key => <label key={key}>{{ widthCm: 'Largeur', heightCm: 'Hauteur', depthCm: 'Profondeur', diameterCm: 'Diamètre' }[key]} (cm)<input type="number" required min="1" max="1000" step="0.1" value={mark[key] || ''} onChange={event => onChange({ ...mark, [key]: Number(event.target.value) })}/></label>)}</div>
    <div className="reservation-dimensions"><strong>Cotations de position</strong><p>Distance perpendiculaire entre l’axe de la réservation, sur sa face de pose, et un sol ou un mur. Une cote appliquée déplace la réservation sur sa face ; les autres cotes sont recalculées.</p>
      {(mark.dimensions || []).map(dimension => <DistanceField key={dimension.id} mark={mark} dimension={dimension} onChange={onChange}/>)}
      <button type="button" disabled={(mark.dimensions?.length || 0) >= 3 && !picking} onClick={onPick}>{picking ? 'Annuler le choix de face' : 'Ajouter une cote · choisir une face'}</button>
      {picking && <p role="status">Cliquez sur la face du sol ou du mur de référence dans la maquette. Échap pour annuler.</p>}
    </div>
  </>;
}

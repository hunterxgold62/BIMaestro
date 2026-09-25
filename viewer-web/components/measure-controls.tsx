import { measureDistances, type MeasureMode, type TemporaryMeasure } from '@/lib/mep-measure';

export function MeasureControls({ mode, onMode, measures, picking, hasStart, onStart, onCancel, onRemove, onClear, onClose }: { mode: MeasureMode; onMode: (mode: MeasureMode) => void; measures: TemporaryMeasure[]; picking: boolean; hasStart: boolean; onStart: () => void; onCancel: () => void; onRemove: (id: string) => void; onClear: () => void; onClose: () => void }) {
  return <aside className="measure-controls" aria-label="Mesures temporaires">
    <header><strong>Mesurer dans la maquette</strong><button onClick={onClose}>Fermer</button></header>
    <p>Cliquez puis relâchez pour chaque point. Après le premier point, glissez pour tourner autour de lui. En mode horizontal, la cote suit l’horizontale de la vue au second clic. Mesures personnelles, effacées au rechargement.</p>
    <label className="measure-mode">Direction<select aria-label="Direction" value={mode} onChange={event => onMode(event.target.value as MeasureMode)}><option value="free">Libre · deux points</option><option value="horizontal">Horizontale · selon la vue</option></select></label>
    <button disabled={!picking && measures.length >= 20} onClick={picking ? onCancel : onStart}>{picking ? 'Annuler la mesure' : '+ Mesurer deux points'}</button>
    {picking && <p className="measure-prompt" role="status">{hasStart ? 'Cliquez le second point.' : 'Cliquez le premier point.'} Échap pour annuler.</p>}
    {measures.map((measure, index) => {
      const distance = measureDistances(measure.start, measure.end);
      return <div className="measure-result" key={measure.id}><strong>Mesure {index + 1}</strong><dl><div><dt>Directe</dt><dd>{distance.direct.toFixed(3)} m</dd></div><div><dt>Horizontale</dt><dd>{distance.horizontal.toFixed(3)} m</dd></div><div><dt>Verticale</dt><dd>{distance.vertical.toFixed(3)} m</dd></div></dl><button onClick={() => onRemove(measure.id)}>Retirer la mesure {index + 1}</button></div>;
    })}
    {!!measures.length && <button onClick={onClear}>Effacer toutes les mesures</button>}
  </aside>;
}

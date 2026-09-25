import { useState } from 'react';
import type { MepGraph, Scenario, MepAnalysisSettings } from '../lib/mep-contract';
import { updateAnalysis } from '../lib/mep-api';
import { downloadExchange } from '../lib/mep-exchange';

type Props = { graph: MepGraph | null; reportText: string; calculating: boolean; error?: string; isEditor: boolean; token: string | null;
  scenario: Scenario | null; publicationRevision?: number; selectedKey?: string; onScenario: (scenario: Scenario) => void;
  onClose: () => void; onReference: () => Promise<void>; onSelect: (key: string) => void };
const roles = ['À vérifier', 'Terminal', 'Retour', "Limite d'export", 'Bouchon'];
export function MepImpactPanel(props: Props) {
  const [equipmentOnly, setEquipmentOnly] = useState(false);
  const [search, setSearch] = useState('');
  const [count, setCount] = useState(100);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const report = props.graph?.impactReport;
  const name = (key: string) => props.graph?.elements.find(e => e.key === key)?.name || key;
  const items = (report?.items || []).filter(item => (!equipmentOnly || item.isEquipment) && `${item.name} ${item.elementId} ${item.systemName} ${item.change}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const save = async (patch: MepAnalysisSettings) => {
    if (!props.token || !props.scenario || !props.publicationRevision || !props.isEditor) return;
    setBusy(true); setError('');
    try { const next = await updateAnalysis(props.token, props.scenario.revision, props.publicationRevision, patch); props.onScenario(next.scenario); }
    catch (e) { setError(e instanceof Error ? e.message : 'Enregistrement impossible'); }
    finally { setBusy(false); }
  };
  const disabled = busy || props.calculating || !!props.error;
  return <section className="mep-impact-panel" aria-label="Analyse des coupures">
    <header><h2>Analyse des coupures</h2><button onClick={props.onClose}>Fermer</button></header>
    <p>Comparez les connexions et la circulation supposée à un état de référence.</p>
    {(props.error || error) && <p role="alert">{props.error || error}</p>}
    {props.calculating && <output>Recalcul en cours. Les résultats précédents sont masqués.</output>}
    {!props.graph && <p>Ouvrez une maquette publiée pour analyser son réseau. La démonstration présente uniquement le rendu indicatif.</p>}
    {report && <>
      <p><b>{report.equipmentLostArrivalCount}</b> équipements sans connexion à une arrivée · <b>{report.lostArrivalCount}</b> éléments au total · <b>{report.alternativeCount}</b> connexions maintenues par un autre chemin</p>
      <p>Référence : {report.referenceLabel}</p>
      <div className="impact-actions"><button disabled={disabled} onClick={async () => { setBusy(true); try { await props.onReference(); } catch(e) { setError(String(e)); } finally { setBusy(false); } }}>Prendre l’état actuel comme référence</button><button disabled={disabled || !props.reportText} onClick={() => downloadExchange(props.reportText, 'BIMaestro-analyse-coupures.txt')}>Exporter le compte rendu</button></div>
      <small>La référence choisie ici reste propre à cette consultation. Le compte rendu contient ses identifiants.</small>
      <details><summary>Hypothèses et limites du réseau</summary><ul>{report.assumptions.map(text => <li key={text}>{text}</li>)}</ul>
        <label><input type="checkbox" disabled={disabled || !props.isEditor} checked={props.graph?.allowImplicitTerminals !== false} onChange={e => void save({ allowImplicitTerminals: e.target.checked })}/> Autoriser les extrémités non renseignées comme débouchés possibles</label>
        <p>Extrémités de l’élément sélectionné :</p>
        {props.graph!.connectors.filter(c => c.elementKey === props.selectedKey && (!c.isConnected || props.graph!.elements.find(e => e.key === c.elementKey)?.connectorIndices.length === 1)).map(c => <label key={c.index}>Connecteur {c.index}<select aria-label={`Rôle du connecteur ${c.index}`} disabled={disabled || !props.isEditor || !c.persistentKey} value={c.endpointRole || 0} onChange={e => void save({ endpoints: { [c.persistentKey!]: Number(e.target.value) } })}>{roles.map((role,i) => <option key={role} value={i}>{role}</option>)}</select>{!c.persistentKey && <small>Republier depuis le plugin pour qualifier cette extrémité.</small>}</label>)}
        {!props.selectedKey && <p>Sélectionnez un élément dans la maquette pour qualifier ses extrémités.</p>}
        {!props.isEditor && <p>Les hypothèses partagées sont modifiables avec un lien d’édition.</p>}
      </details>
      <label><input type="checkbox" checked={equipmentOnly} onChange={e => setEquipmentOnly(e.target.checked)}/> Équipements uniquement</label>
      <input aria-label="Filtrer les éléments concernés" placeholder="Nom, identifiant ou système…" value={search} onChange={e => { setSearch(e.target.value); setCount(100); }}/>
      {!report.comparable && <p role="alert">Référence incompatible : choisissez une nouvelle référence pour comparer.</p>}
      {report.comparable && items.length === 0 && <p>Aucun changement correspondant aux filtres.</p>}
      <div className="impact-items">{items.slice(0,count).map(item => <article key={item.elementKey}>
        <button onClick={() => props.onSelect(item.elementKey)}>{item.name} · #{item.elementId}</button><strong>{item.change}</strong>
        <p>Avant : {item.before}<br/>Après : {item.after}</p>
        {item.limitingValveKeys.length > 0 && <p>Vannes fermées sur le chemin de référence : {item.limitingValveKeys.map(key => <button key={key} onClick={() => props.onSelect(key)}>{name(key)}</button>)}</p>}
        {item.alternativeRouteKeys.length > 0 && <p>Repères de l’autre chemin : {item.alternativeRouteKeys.map(key => <button key={key} onClick={() => props.onSelect(key)}>{name(key)}</button>)}</p>}
      </article>)}</div>
      {items.length > count && <button onClick={() => setCount(count+100)}>Afficher la suite ({items.length-count})</button>}
      <details><summary>Diagnostics de la maquette ({props.graph?.diagnostics?.filter(d => !d.isAggregate).length || 0})</summary>{props.graph?.diagnostics?.filter(d => !d.isAggregate).map(d => <article key={d.key}><button onClick={() => props.onSelect(d.elementKey)}>{d.title} — {name(d.elementKey)}</button><p>{d.explanation}</p></article>)}</details>
    </>}
  </section>;
}

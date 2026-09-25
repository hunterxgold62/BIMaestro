import type { MepGraph, MepSourceOverride, MepAnalysisSettings, MepAnalysisReference } from './mep-contract';

export const MEP_ENGINE_VERSION = 'mep-topology-2.0.1';
export type CalculationResult = { graph: MepGraph; reportText: string };
export type CalculationRequest = { graph: MepGraph; valves?: Record<string, boolean>; sources?: Record<string, MepSourceOverride>; reference?: MepAnalysisReference; setReference?: boolean } & MepAnalysisSettings;
let frame: HTMLIFrameElement | null = null;
let startup: Promise<void> | null = null;
let sequence = 0;
const pending = new Map<number, { resolve: (value: CalculationResult) => void; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout> }>();

function start(): Promise<void> {
  if (startup) return startup;
  startup = new Promise<void>((resolve, reject) => {
    const iframe = document.createElement('iframe');
    frame = iframe; iframe.hidden = true; iframe.title = 'Calcul local du réseau MEP'; iframe.src = '/mep-engine/index.html';
    const timeout = setTimeout(() => fail(new Error('Le moteur MEP ne répond pas. Rechargez pour réessayer.')), 90000);
    const fail = (error: Error) => {
      clearTimeout(timeout); window.removeEventListener('message', onMessage); iframe.remove(); frame = null; startup = null;
      for (const item of pending.values()) { clearTimeout(item.timer); item.reject(error); } pending.clear(); reject(error);
    };
    const onMessage = (event: MessageEvent) => {
      if (event.origin !== location.origin || event.source !== iframe.contentWindow) return;
      if (event.data?.type === 'mep-ready') { clearTimeout(timeout); resolve(); }
      if (event.data?.type === 'mep-error') fail(new Error('Chargement du moteur MEP impossible. Rechargez pour réessayer.'));
      if (event.data?.type !== 'mep-result') return;
      const item = pending.get(event.data.id); if (!item) return;
      clearTimeout(item.timer); pending.delete(event.data.id);
      if (event.data.error) { item.reject(new Error(`Calcul MEP impossible : ${event.data.error}`)); return; }
      try {
        const result = JSON.parse(event.data.result) as CalculationResult;
        if (result.graph.engineVersion !== MEP_ENGINE_VERSION) throw new Error('Version du moteur incompatible. Rechargez la page.');
        item.resolve(result);
      } catch (error) { item.reject(error instanceof Error ? error : new Error('Résultat MEP invalide')); }
    };
    window.addEventListener('message', onMessage); document.body.appendChild(iframe);
  });
  return startup;
}

// Both interfaces execute the same linked C# sources. Never fabricate a
// distance-based result when the runtime is unavailable.
export async function calculateMep(request: CalculationRequest): Promise<CalculationResult> {
  await start();
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('Calcul trop long : résultat indisponible.')); }, 120000);
    pending.set(id, { resolve, reject, timer });
    frame!.contentWindow!.postMessage({ type: 'mep-calculate', id, json: JSON.stringify(request) }, location.origin);
  });
}
export function pendingGraph(graph: MepGraph): MepGraph {
  return { ...graph, impactReport: undefined, diagnostics: [], valves: graph.valves.map(valve => ({ ...valve, upstreamState: 0, downstreamState: 0 })), elements: graph.elements.map(element => ({ ...element, flowState: 0, connectedToInlet: false, connectedToReturn: false,
    paths: element.paths.map(path => ({ ...path, flowState: 0, hasCirculation: false, directionState: 0, directionReason: '', directionExplanation: undefined })) })) };
}
export function canAnimate(path: { flowState?: string | number; hasCirculation?: boolean; directionState?: string | number } | undefined): boolean {
  return !!path && (path.flowState === 2 || String(path.flowState).toLowerCase() === 'supplied') && path.hasCirculation === true &&
    (path.directionState === 1 || String(path.directionState).toLowerCase() === 'resolved');
}
export function connectionLabel(element: { connectedToInlet?: boolean; connectedToReturn?: boolean }): string {
  return element.connectedToInlet ? element.connectedToReturn ? 'Relié à une arrivée et à un retour' : 'Relié à une arrivée'
    : element.connectedToReturn ? 'Relié à un retour uniquement' : 'Aucune arrivée ni retour accessible';
}

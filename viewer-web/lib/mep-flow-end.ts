import type { MepGraph } from './mep-contract';
import { canAnimate } from './mep-engine';

export type FlowEnd = { title: string; description: string; elementKey: string };

/** Explain only exported connectivity; never infer a blocker from visual proximity. */
export function explainFlowEnd(graph: MepGraph, key: string, ordinal: number, reverse: boolean): FlowEnd {
  const element = graph.elements.find(element => element.key === key), path = element?.paths[ordinal];
  const closed = new Set(graph.valves.filter(valve => valve.isEnabledAsValve && valve.isClosed).map(valve => valve.elementKey));
  const blocked = (elementKey: string): FlowEnd => ({ title: 'Vanne fermée', description: 'Cette vanne est fermée dans le scénario actuel. Le suivi s’arrête à cet endroit.', elementKey });
  if (closed.has(key)) return blocked(key);
  if (!path || !canAnimate(path)) return { title: 'Circulation non établie', description: 'Le calcul ne confirme plus une circulation orientée sur cette portion.', elementKey: key };
  const forward = reverse ? !path.flowForward : path.flowForward;
  const endpoint = forward ? path.endConnector : path.startConnector;
  const ports = new Set<number>(endpoint >= 0 ? [endpoint] : []);
  for (let changed = true; changed;) {
    changed = false;
    for (const connection of graph.connections) if (!connection.isInternal && connection.connectorA >= 0 && connection.connectorB >= 0) {
      if (ports.has(connection.connectorA) && !ports.has(connection.connectorB)) { ports.add(connection.connectorB); changed = true; }
      if (ports.has(connection.connectorB) && !ports.has(connection.connectorA)) { ports.add(connection.connectorA); changed = true; }
    }
  }
  const source = graph.sources.find(source => source.isActive &&
    (reverse ? source.boundaryKind === 0 || String(source.boundaryKind).toLowerCase() === 'inlet' : source.boundaryKind === 1 || String(source.boundaryKind).toLowerCase() === 'outlet') &&
    (source.elementKey === key || ports.has(reverse ? source.exitConnectorIndex : source.entryConnectorIndex)));
  if (source) return { title: reverse ? 'Arrivée atteinte' : 'Retour atteint', description: 'Le parcours rejoint une limite active définie dans la maquette.', elementKey: source.elementKey };
  const neighbors = graph.elements.filter(element => element.key !== key && element.connectorIndices.some(index => ports.has(index)));
  const continuing = neighbors.some(element => !closed.has(element.key) && element.paths.some(path => {
    const direction = reverse ? !path.flowForward : path.flowForward;
    return canAnimate(path) && ports.has(direction ? path.startConnector : path.endConnector);
  }));
  const gates = neighbors.filter(element => closed.has(element.key));
  if (!continuing && gates.length === 1) return blocked(gates[0].key);
  return { title: 'Fin du parcours établi', description: continuing
    ? 'Le suivi visuel ne trouve pas de prolongement continu sur cette branche. Cela ne signifie pas que le réseau est coupé.'
    : 'Aucun prolongement circulant orienté n’est établi sur cette branche. La cause ne peut pas être identifiée avec certitude.', elementKey: key };
}

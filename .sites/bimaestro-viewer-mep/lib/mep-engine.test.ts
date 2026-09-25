import { describe, expect, it } from 'vitest';
import type { MepConnection, MepElement, MepGraph } from './mep-contract';
import { applyScenario } from './mep-engine';

const point = { x: 0, y: 0, z: 0 };

function element(key: string, connectors: number[], persistentId = key): MepElement {
  return {
    key, persistentId, elementId: connectors[0] + 1, name: key, category: 'Canalisations',
    typeName: 'Test', systemKey: 'system', systemName: 'Essai', connectorIndices: connectors,
    flowState: 0,
    paths: connectors.length > 1 ? [{ elementKey: key, systemKey: 'system', startConnector: connectors[0], endConnector: connectors[1], points: [point, point], flowState: 0, hasCirculation: false, flowForward: true }] : [],
  };
}

function link(a: number, b: number, elementKey = '', internal = false): MepConnection {
  return { connectorA: a, connectorB: b, elementKey, isInternal: internal, isValveGateCandidate: internal };
}

function straightGraph(withSource = true, bypass = false): MepGraph {
  const elements = [element('source', [0]), element('pipe-a', [1, 2]), element('valve', [3, 4], 'valve-persistent'), element('pipe-b', [5, 6])];
  const connections = [link(0, 1), link(1, 2, 'pipe-a', true), link(2, 3), link(3, 4, 'valve', true), link(4, 5), link(5, 6, 'pipe-b', true)];
  if (bypass) {
    elements.push(element('bypass', [7, 8]));
    connections.push(link(2, 7), link(7, 8, 'bypass', true), link(8, 5));
  }
  return {
    connectors: Array.from({ length: bypass ? 9 : 7 }, (_, index) => ({ index, elementKey: '', systemKey: 'system', position: point })),
    connections, elements,
    valves: [{ elementKey: 'valve', isEnabledAsValve: true, isClosed: false }],
    sources: [{ elementKey: 'source', isActive: withSource, entryConnectorIndex: -1, exitConnectorIndex: 0 }],
    systems: [{ key: 'system', name: 'Essai', isVisible: true, elementCount: elements.length }],
  };
}

describe('moteur MEP web — cas de référence partagés avec le moteur C#', () => {
  it('alimente une chaîne ouverte et en déduit le sens', () => {
    const result = applyScenario(straightGraph());
    expect(result.elements.every((item) => item.flowState === 2)).toBe(true);
    expect(result.elements.find((item) => item.key === 'pipe-b')?.paths[0].flowForward).toBe(true);
  });

  it('une vanne fermée isole uniquement son aval', () => {
    const result = applyScenario(straightGraph(), { 'valve-persistent': true });
    expect(result.elements.find((item) => item.key === 'pipe-a')?.flowState).toBe(2);
    expect(result.elements.find((item) => item.key === 'pipe-b')?.flowState).toBe(1);
  });

  it('un bypass maintient l’alimentation autour d’une vanne fermée', () => {
    const result = applyScenario(straightGraph(true, true), { 'valve-persistent': true });
    expect(result.elements.find((item) => item.key === 'pipe-b')?.flowState).toBe(2);
  });

  it('un réseau sans source reste incertain', () => {
    const result = applyScenario(straightGraph(false));
    expect(result.elements.every((item) => item.flowState === 0)).toBe(true);
  });

  it('permet de définir une nouvelle arrivée depuis le scénario web', () => {
    const result = applyScenario(straightGraph(false), {}, { 'pipe-a': 'inlet' });
    expect(result.sources.some((source) => source.elementKey === 'pipe-a' && source.isActive)).toBe(true);
    expect(result.elements.find((item) => item.key === 'pipe-b')?.flowState).toBe(2);
  });

  it('un retour seul ne met pas le réseau sous pression', () => {
    const result = applyScenario(straightGraph(false), {}, { 'pipe-a': 'outlet' });
    expect(result.elements.every((item) => item.flowState === 0)).toBe(true);
  });

  it('ne modifie jamais le graphe exporté reçu', () => {
    const graph = straightGraph();
    applyScenario(graph, { 'valve-persistent': true });
    expect(graph.valves[0].isClosed).toBe(false);
  });

  it('conserve exactement le résultat Revit quand le scénario est inchangé', () => {
    const graph = straightGraph();
    const path = graph.elements.find((item) => item.key === 'pipe-b')!.paths[0];
    path.flowState = 2;
    path.hasCirculation = true;
    path.flowForward = false;
    path.directionState = 1;
    path.directionReason = 'Distribution depuis un collecteur alimenté vers la branche';

    const result = applyScenario(graph, { 'valve-persistent': false });
    const exported = result.elements.find((item) => item.key === 'pipe-b')!.paths[0];
    expect(exported.flowForward).toBe(false);
    expect(exported.directionReason).toBe('Distribution depuis un collecteur alimenté vers la branche');
  });

  it('une vanne modifiée ne retourne pas un sens déjà résolu par Revit', () => {
    const graph = straightGraph(true, true);
    const path = graph.elements.find((item) => item.key === 'pipe-b')!.paths[0];
    path.flowForward = false;
    path.directionState = 'Resolved';
    path.directionReason = 'Continuité Revit';

    const result = applyScenario(graph, { 'valve-persistent': true });
    const recalculated = result.elements.find((item) => item.key === 'pipe-b')!.paths[0];
    expect(recalculated.flowForward).toBe(false);
    expect(recalculated.directionReason).toBe('Continuité Revit');
  });
});

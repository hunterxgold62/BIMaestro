import type { MepGraph, MepSourceOverride } from './mep-contract';

const supplied = 2;
const isolated = 1;
const unknown = 0;

export function applyScenario(graph: MepGraph, valveState: Record<string, boolean> = {}, sourceState: Record<string, MepSourceOverride> = {}): MepGraph {
  const copy = structuredClone(graph);
  const elements = new Map(copy.elements.map((element) => [element.key, element]));
  const elementsById = new Map(copy.elements.map((element) => [element.key, element]));
  copy.elements.forEach((element) => { if (element.persistentId) elementsById.set(element.persistentId, element); });
  let scenarioChangesExportedState = false;
  for (const valve of copy.valves) {
    const element = elements.get(valve.elementKey);
    const id = element?.persistentId || element?.key || valve.elementKey;
    if (typeof valveState[id] === 'boolean') {
      scenarioChangesExportedState ||= valve.isClosed !== valveState[id];
      valve.isClosed = valveState[id];
    }
  }
  for (const [id, override] of Object.entries(sourceState)) {
    const element = elementsById.get(id);
    if (!element) continue;
    const existing = copy.sources.filter((source) => source.elementKey === element.key);
    const activeKinds = existing.filter((source) => source.isActive).map((source) => sourceKind(source.boundaryKind));
    const requestedKind = override === 'outlet' ? 'outlet' : override === 'inlet' ? 'inlet' : null;
    scenarioChangesExportedState ||= requestedKind === null
      ? activeKinds.length > 0
      : activeKinds.length !== 1 || activeKinds[0] !== requestedKind;
    existing.forEach((source) => { source.isActive = false; });
    if (override === 'none') continue;
    const boundaryKind = override === 'outlet' ? 'Outlet' : 'Inlet';
    let source = existing.find((candidate) => String(candidate.boundaryKind).toLowerCase() === boundaryKind.toLowerCase());
    const path = element.paths.find((candidate) => candidate.startConnector >= 0 && candidate.endConnector >= 0);
    if (!source) {
      source = { elementKey: element.key, systemKey: element.systemKey, name: element.name, boundaryKind, isActive: true, entryConnectorIndex: path?.startConnector ?? -1, exitConnectorIndex: path?.endConnector ?? -1 };
      copy.sources.push(source);
    }
    source.boundaryKind = boundaryKind;
    source.isActive = true;
  }

  // L'export contient déjà le résultat du solveur Revit complet : pompes,
  // retours, tés, collecteurs, DN, continuités et corrections manuelles. Tant
  // que le scénario web n'a réellement rien changé, ce résultat est la source
  // de vérité et ne doit pas être remplacé par une approximation navigateur.
  const hasAuthoritativeExport = copy.elements.some((element) =>
    element.paths.some((path) =>
      isResolvedDirection(path.directionState) ||
      Boolean(path.directionReason?.trim()),
    ),
  );
  if (!scenarioChangesExportedState && hasAuthoritativeExport) return copy;

  const closed = new Set(copy.valves.filter((valve) => valve.isEnabledAsValve && valve.isClosed).map((valve) => valve.elementKey));
  const adjacency = Array.from({ length: copy.connectors.length }, () => [] as number[]);
  const physicalAdjacency = Array.from({ length: copy.connectors.length }, () => [] as number[]);
  for (const connection of copy.connections) {
    if (connection.connectorA < 0 || connection.connectorB < 0 || connection.connectorA >= adjacency.length || connection.connectorB >= adjacency.length) continue;
    physicalAdjacency[connection.connectorA].push(connection.connectorB);
    physicalAdjacency[connection.connectorB].push(connection.connectorA);
    if (closed.has(connection.elementKey) && (connection.isInternal || connection.isValveGateCandidate)) continue;
    adjacency[connection.connectorA].push(connection.connectorB);
    adjacency[connection.connectorB].push(connection.connectorA);
  }
  const sourceSeeds = new Set<number>();
  const distance = Array.from({ length: copy.connectors.length }, () => -1);
  const queue: number[] = [];
  for (const source of copy.sources.filter((item) => item.isActive && (sourceKind(item.boundaryKind) === 'inlet'))) {
    const element = elements.get(source.elementKey);
    const seeds = source.exitConnectorIndex >= 0 ? [source.exitConnectorIndex] : element?.connectorIndices || [];
    for (const seed of seeds) if (seed >= 0 && seed < distance.length) {
      sourceSeeds.add(seed);
      if (distance[seed] < 0) { distance[seed] = 0; queue.push(seed); }
    }
  }
  for (let cursor = 0; cursor < queue.length; cursor++) {
    const current = queue[cursor];
    for (const next of adjacency[current]) if (distance[next] < 0) { distance[next] = distance[current] + 1; queue.push(next); }
  }
  const componentHasSource = Array.from({ length: copy.connectors.length }, () => false);
  const visited = Array.from({ length: copy.connectors.length }, () => false);
  for (let seed = 0; seed < copy.connectors.length; seed++) {
    if (visited[seed]) continue;
    const component: number[] = [];
    const pending = [seed];
    visited[seed] = true;
    let containsSource = false;
    for (let cursor = 0; cursor < pending.length; cursor++) {
      const current = pending[cursor];
      component.push(current);
      containsSource ||= sourceSeeds.has(current);
      for (const next of physicalAdjacency[current]) if (!visited[next]) { visited[next] = true; pending.push(next); }
    }
    for (const index of component) componentHasSource[index] = containsSource;
  }
  for (const element of copy.elements) {
    const reached = element.connectorIndices.some((index) => distance[index] >= 0);
    const couldBeSupplied = element.connectorIndices.some((index) => componentHasSource[index]);
    element.flowState = reached ? supplied : couldBeSupplied ? isolated : unknown;
    for (const path of element.paths) {
      const start = distance[path.startConnector] ?? -1;
      const end = distance[path.endConnector] ?? -1;
      path.flowState = reached ? supplied : couldBeSupplied ? isolated : unknown;
      path.hasCirculation = reached && start >= 0 && end >= 0 && start !== end;
      // Un sens résolu par Revit reste autoritaire lors d'une fermeture de
      // vanne sur le site. Le parcours par distance sert seulement de repli aux
      // anciens exports ou aux chemins que Revit avait laissés indéterminés.
      if (path.hasCirculation && !isResolvedDirection(path.directionState)) {
        path.flowForward = start < end;
        path.directionReason = 'Déduit depuis l’arrivée active du scénario web';
      }
    }
  }
  return copy;
}

function isResolvedDirection(value: number | string | undefined): boolean {
  return value === 1 || String(value).toLowerCase() === 'resolved';
}

function sourceKind(value: number | string | undefined): 'inlet' | 'outlet' {
  return value === 1 || String(value).toLowerCase() === 'outlet' ? 'outlet' : 'inlet';
}

import type { MepGraph } from './mep-contract';
import { decodeMepPoint } from './mep-point';
import { canAnimate } from './mep-engine';

export const flowPathKey = (elementKey: string, ordinal: number) => `${elementKey}\0${ordinal}`;
export type FlowPhase = { start: number; origin: number; spacing: number };
type Edge = { key: string; from: number; to: number; length: number; forward: boolean };

/** Follow exported connections, never proximity or display filters. */
function flowEdges(graph: MepGraph, connectorRoots?: Map<number, number>): Edge[] {
  const parent = new Map<number, number>();
  const root = (index: number): number => {
    if (!parent.has(index)) parent.set(index, index);
    let result = index;
    while (parent.get(result) !== result) result = parent.get(result)!;
    while (index !== result) { const next = parent.get(index)!; parent.set(index, result); index = next; }
    return result;
  };
  for (const connection of graph.connections) if (!connection.isInternal) {
    if (connection.connectorA < 0 || connection.connectorB < 0) continue;
    const a = root(connection.connectorA), b = root(connection.connectorB);
    parent.set(Math.max(a, b), Math.min(a, b));
  }
  const edges: Edge[] = [];
  if (connectorRoots) for (const connector of graph.connectors) connectorRoots.set(connector.index, root(connector.index));
  // Revit exports multi-port fittings as port -> centre arms. EndConnector=-1
  // is a virtual centre, local to that fitting, not an invalid/disconnected port.
  const hubKeys = [...new Set(graph.elements.flatMap(element => element.paths.map(path => `${element.key}\0${path.systemKey || element.systemKey}`)))].sort();
  const hubs = new Map(hubKeys.map((key, index) => [key, -index - 1]));
  const closed = new Set(graph.valves.filter(v => v.isEnabledAsValve && v.isClosed).map(v => v.elementKey));
  for (const element of graph.elements) element.paths.forEach((path, ordinal) => {
    if (closed.has(element.key) || !canAnimate(path) || !Number.isInteger(path.startConnector) || !Number.isInteger(path.endConnector) || (path.startConnector < 0 && path.endConnector < 0)) return;
    const points = path.points.map(decodeMepPoint).filter((p): p is [number, number, number] => !!p);
    let length = 0;
    for (let i = 1; i < points.length; i++) length += Math.hypot(...points[i].map((value, axis) => value - points[i - 1][axis]));
    if (length < .000001) return;
    const forward = path.flowForward !== false;
    const hub = hubs.get(`${element.key}\0${path.systemKey || element.systemKey}`)!;
    const start = path.startConnector >= 0 ? root(path.startConnector) : hub;
    const end = path.endConnector >= 0 ? root(path.endConnector) : hub;
    edges.push({ key: flowPathKey(element.key, ordinal), from: forward ? start : end, to: forward ? end : start, length, forward });
  });
  edges.sort((a, b) => a.key < b.key ? -1 : a.key > b.key ? 1 : 0);
  return edges;
}

export function flowPhases(graph: MepGraph): Map<string, FlowPhase> {
  const edges = flowEdges(graph);
  const outgoing = new Map<number, Edge[]>(), incoming = new Map<number, number>(), neighbors = new Map<number, Set<number>>();
  for (const edge of edges) {
    outgoing.set(edge.from, [...(outgoing.get(edge.from) || []), edge]);
    incoming.set(edge.to, (incoming.get(edge.to) || 0) + 1);
    for (const [a, b] of [[edge.from, edge.to], [edge.to, edge.from]]) {
      if (!neighbors.has(a)) neighbors.set(a, new Set());
      neighbors.get(a)!.add(b);
    }
  }
  const phases = new Map<string, FlowPhase>(), visited = new Set<number>();
  for (const seed of [...neighbors.keys()].sort((a, b) => a - b)) {
    if (visited.has(seed)) continue;
    const component: number[] = [seed]; visited.add(seed);
    for (let i = 0; i < component.length; i++) for (const next of neighbors.get(component[i]) || []) {
      if (!visited.has(next)) { visited.add(next); component.push(next); }
    }
    const circuit = component.every(node => incoming.get(node) === 1 && outgoing.get(node)?.length === 1);
    // A simple closed loop has an integral number of packets: no reset seam.
    const circuitLength = circuit ? component.reduce((sum, node) => sum + outgoing.get(node)![0].length, 0) : 0;
    const spacing = circuit ? circuitLength / Math.max(1, Math.round(circuitLength / 6)) : 6;
    const starts = component.filter(node => !incoming.has(node)).sort((a, b) => a - b);
    const distances = new Map<number, number>(), queue: number[] = [];
    const walk = (start: number) => {
      distances.set(start, 0); queue.push(start);
      for (let i = 0; i < queue.length; i++) {
        const node = queue[i], distance = distances.get(node)!;
        for (const edge of outgoing.get(node) || []) {
          if (!phases.has(edge.key)) phases.set(edge.key, { start: distance, origin: distance + (edge.forward ? 0 : edge.length), spacing });
          if (!distances.has(edge.to)) { distances.set(edge.to, distance + edge.length); queue.push(edge.to); }
        }
      }
      queue.length = 0;
    };
    for (const start of starts) if (!distances.has(start)) walk(start);
    for (const node of component) if (!distances.has(node)) walk(node);
  }
  return phases;
}

/** Earliest downstream arrival; a pulse splits at tees and never loops forever. */
export function flowTrace(graph: MepGraph, elementKey: string, reverse = false) {
  const connectorRoots = new Map<number, number>();
  const edges = flowEdges(graph, connectorRoots).map(edge => reverse ? { ...edge, from: edge.to, to: edge.from, forward: !edge.forward } : edge), outgoing = new Map<number, Edge[]>();
  for (const edge of edges) outgoing.set(edge.from, [...(outgoing.get(edge.from) || []), edge]);
  const result = new Map<string, { start: number; length: number; forward: boolean }>();
  const distances = new Map<number, number>();
  const heap: { node: number; distance: number }[] = [];
  const push = (node: number, distance: number) => {
    if (distance >= (distances.get(node) ?? Infinity)) return;
    distances.set(node, distance); heap.push({ node, distance });
    let i = heap.length - 1;
    while (i > 0) { const p = (i - 1) >> 1; if (heap[p].distance <= heap[i].distance) break; [heap[p], heap[i]] = [heap[i], heap[p]]; i = p; }
  };
  const pop = () => {
    const first = heap[0], last = heap.pop()!;
    if (heap.length) {
      heap[0] = last; let i = 0;
      while (true) {
        const l = i * 2 + 1, r = l + 1;
        let next = i;
        if (l < heap.length && heap[l].distance < heap[next].distance) next = l;
        if (r < heap.length && heap[r].distance < heap[next].distance) next = r;
        if (next === i) break;
        [heap[i], heap[next]] = [heap[next], heap[i]]; i = next;
      }
    }
    return first;
  };
  const selected = edges.filter(e => e.key.startsWith(`${elementKey}\0`));
  if (!selected.length) {
    const source = graph.sources.find(source => source.elementKey === elementKey && source.isActive);
    const index = source && (reverse ? source.entryConnectorIndex : source.exitConnectorIndex);
    if (index !== undefined && index >= 0) push(connectorRoots.get(index) ?? index, 0);
  }
  for (const edge of selected) distances.set(edge.from, 0);
  for (const edge of selected) {
    result.set(edge.key, { start: 0, length: edge.length, forward: edge.forward });
    push(edge.to, edge.length);
  }
  while (heap.length) {
    const { node, distance } = pop();
    if (distance !== distances.get(node)) continue;
    for (const edge of outgoing.get(node) || []) {
      if (!result.has(edge.key) || distance < result.get(edge.key)!.start) result.set(edge.key, { start: distance, length: edge.length, forward: edge.forward });
      push(edge.to, distance + edge.length);
    }
  }
  return result;
}

/** One moving lattice shared by connected elements, including short fittings. */
export function flowArrowDistance(start: number, length: number, spacing: number, slot: number, seconds: number) {
  const first = ((seconds * 2.4 - start) % spacing + spacing) % spacing;
  const distance = first + slot * spacing;
  return distance < length ? distance : null;
}

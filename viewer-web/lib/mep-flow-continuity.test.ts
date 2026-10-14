import { describe, expect, it } from 'vitest';
import type { MepGraph, MepElement } from './mep-contract';
import { flowPhases, flowPathKey, flowArrowDistance, flowTrace } from './mep-flow-continuity';
import { decodeMepPoint } from './mep-point';

const edge = (key: string, a: number, b: number, length: number, forward = true): MepElement => ({ key, elementId: a, name: key, category: 'Tuyaux', typeName: '', systemKey: 'water', systemName: 'Eau', connectorIndices: [a, b], flowState: 2, paths: [{ elementKey: key, systemKey: 'water', startConnector: a, endConnector: b, points: [[0, 0, 0], [length, 0, 0]], flowState: 2, hasCirculation: true, flowForward: forward, directionState: 1 }] });
const graph = (elements: MepElement[], links: [number, number][] = []): MepGraph => ({ elements, connectors: [], connections: links.map(([connectorA, connectorB]) => ({ connectorA, connectorB, isInternal: false, isValveGateCandidate: false, elementKey: '' })), valves: [], sources: [], systems: [] });
const phase = (g: MepGraph, key: string) => flowPhases(g).get(flowPathKey(key, 0))!;

describe('Connected flow presentation', () => {
  it('joins waves across separate connector IDs and reversed local geometry', () => {
    const g = graph([edge('a', 0, 1, 10), edge('b', 3, 2, 4, false)], [[1, 2]]);
    expect(phase(g, 'a').origin + 10).toBeCloseTo(phase(g, 'b').origin - 4);
    expect(phase(g, 'b').start).toBe(10);
    expect(flowPhases({ ...g, elements: [...g.elements].reverse() })).toEqual(flowPhases(g));
  });
  it('keeps a six-foot arrow lattice across a short fitting, without a permanent fitting arrow', () => {
    const g = graph([edge('a', 0, 1, 5.8), edge('b', 2, 3, .4), edge('c', 4, 5, 6)], [[1, 2], [3, 4]]);
    for (let time = 0; time < 10; time += .07) {
      const heads: number[] = [];
      for (const e of g.elements) {
        const p = phase(g, e.key), length = decodeMepPoint(e.paths[0].points[1])![0];
        for (let slot = 0; slot < 3; slot++) {
          const distance = flowArrowDistance(p.start, length, p.spacing, slot, time);
          if (distance !== null) heads.push(p.start + distance);
        }
      }
      heads.sort((a, b) => a - b);
      for (let i = 1; i < heads.length; i++) expect(heads[i] - heads[i - 1]).toBeCloseTo(6, 6);
    }
    expect(flowArrowDistance(5.8, .4, 6, 0, 1)).toBeNull();
  });
  it('starts both branches at the same junction phase', () => {
    const g = graph([edge('a', 0, 1, 8), edge('b', 2, 3, 5), edge('c', 4, 5, 7)], [[1, 2], [1, 4]]);
    expect(phase(g, 'b').start).toBe(8); expect(phase(g, 'c').start).toBe(8);
  });
  it('closes a simple loop with a whole number of wave periods', () => {
    const g = graph([edge('a', 0, 1, 4.2), edge('b', 2, 3, 5.4), edge('c', 4, 5, 3.1)], [[1, 2], [3, 4], [5, 0]]);
    const p = phase(g, 'c');
    expect((p.start + 3.1) / p.spacing).toBeCloseTo(2, 6);
  });
  it('does not animate an unresolved or stagnant path', () => {
    const g = graph([edge('a', 0, 1, 4)]);
    g.elements[0].paths[0].hasCirculation = false;
    expect(flowPhases(g).size).toBe(0);
  });
});

describe('Selected flow pulse', () => {
  it('starts from a boundary connector even when the source has no rendered path', () => {
    const g = graph([edge('pipe', 0, 1, 2)]);
    g.sources = [{ elementKey: 'boundary', isActive: true, entryConnectorIndex: 1, exitConnectorIndex: 0 }];
    expect(flowTrace(g, 'boundary').get(flowPathKey('pipe', 0))).toEqual({ start: 0, length: 2, forward: true });
    expect(flowTrace(g, 'boundary', true).get(flowPathKey('pipe', 0))).toEqual({ start: 0, length: 2, forward: false });
  });
  it('traces upstream from a return without reversing the actual flow', () => {
    const g = graph([edge('inlet', 0, 1, 2), edge('pump', 1, 2, 3), edge('return', 2, 3, 4)]);
    const routes = flowTrace(g, 'return', true);
    expect(routes.get(flowPathKey('pump', 0))).toEqual({ start: 4, length: 3, forward: false });
    expect(routes.get(flowPathKey('inlet', 0))?.start).toBe(7);
    expect(g.elements[0].paths[0].flowForward).toBe(true);
  });
  it('crosses real Revit tee arms with EndConnector=-1 and splits downstream', () => {
    const tee = edge('tee', 2, -1, 1);
    tee.connectorIndices = [2, 3, 4]; tee.isPipeFitting = true;
    tee.paths.push(edge('tee', 3, -1, 1, false).paths[0], edge('tee', 4, -1, 1, false).paths[0]);
    const g = graph([edge('selected', 0, 1, 3), tee, edge('left', 5, 6, 4), edge('right', 7, 8, 5)], [[1, 2], [3, 5], [4, 7]]);
    const routes = flowTrace(g, 'selected');
    expect(routes.get(flowPathKey('left', 0))?.start).toBe(5);
    expect(routes.get(flowPathKey('right', 0))?.start).toBe(5);
    expect(routes.size).toBe(6);
    const phases = flowPhases(g);
    expect(phases.get(flowPathKey('tee', 0))!.origin + 1).toBe(phases.get(flowPathKey('tee', 1))!.origin - 1);
    expect(flowPhases({ ...g, elements: [...g.elements].reverse() })).toEqual(phases);
  });
  it('keeps the virtual centres of unrelated fittings separate', () => {
    const g = graph([edge('selected', 0, 1, 3), edge('tee', 2, -1, 1), edge('unrelated', 3, -1, 1, false)], [[1, 2]]);
    expect(flowTrace(g, 'selected').has(flowPathKey('unrelated', 0))).toBe(false);
  });
  it('splits downstream, skips upstream siblings, and terminates a loop', () => {
    const g = graph([edge('selected', 0, 1, 2), edge('sibling', 0, 9, 3), edge('left', 1, 2, 4), edge('right', 1, 3, 5), edge('loop', 2, 0, 2)]);
    const routes = flowTrace(g, 'selected');
    expect(routes.has(flowPathKey('sibling', 0))).toBe(false);
    expect(routes.get(flowPathKey('left', 0))?.start).toBe(2);
    expect(routes.get(flowPathKey('right', 0))?.start).toBe(2);
    expect(routes.size).toBe(4);
  });
  it('keeps a bypass reachable when the gate is closed', () => {
    const g = graph([edge('selected', 0, 1, 2), edge('gate', 1, 2, 1), edge('bypass', 1, 4, 3), edge('join', 4, 2, 2), edge('downstream', 2, 3, 1)]);
    g.valves = [{ elementKey: 'gate', isEnabledAsValve: true, isClosed: true }];
    const routes = flowTrace(g, 'selected');
    expect(routes.has(flowPathKey('gate', 0))).toBe(false);
    expect(routes.get(flowPathKey('downstream', 0))?.start).toBe(7);
    g.elements[2].paths[0].hasCirculation = false;
    expect(flowTrace(g, 'selected').has(flowPathKey('downstream', 0))).toBe(false);
  });
  it('uses the shortest travel distance at a merge', () => {
    const g = graph([edge('selected', 0, 1, 2), edge('long', 1, 3, 10), edge('short', 1, 2, 3), edge('join', 2, 3, 3), edge('out', 3, 4, 1)]);
    expect(flowTrace(g, 'selected').get(flowPathKey('out', 0))?.start).toBe(8);
  });
});

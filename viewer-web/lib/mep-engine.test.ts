import { describe, it, expect } from 'vitest';
import { canAnimate, connectionLabel, pendingGraph } from './mep-engine';
import type { MepGraph } from './mep-contract';

// Solver cases are exercised in the browser against the C# regression corpus
// by scripts/test-mep-browser.cjs in the plugin repository.
describe('présentation du moteur partagé', () => {
  it('n’anime jamais un sens inconnu, contradictoire ou une branche stagnante', () => {
    for (const directionState of [0, 2, 'Unknown', 'Conflict']) expect(canAnimate({ flowState:2, hasCirculation:true, directionState })).toBe(false);
    expect(canAnimate({flowState:2,hasCirculation:false,directionState:1})).toBe(false);
    expect(canAnimate({flowState:1,hasCirculation:true,directionState:1})).toBe(false);
    expect(canAnimate({flowState:2,hasCirculation:true,directionState:1})).toBe(true);
    expect(canAnimate({flowState:'Supplied',hasCirculation:true,directionState:'Resolved'})).toBe(true);
  });
  it('distingue arrivée et retour sans affirmer une pression', () => {
    expect(connectionLabel({connectedToReturn:true})).toBe('Relié à un retour uniquement');
    expect(connectionLabel({connectedToInlet:true,connectedToReturn:true})).toBe('Relié à une arrivée et à un retour');
  });
  it('masque les anciens résultats pendant le calcul sans modifier l’export', () => {
    const graph = { elements:[{key:'p',flowState:2,paths:[{flowState:2,hasCirculation:true,directionState:1,directionReason:'ancien'}]}],connectors:[],connections:[],valves:[],sources:[],systems:[] } as unknown as MepGraph;
    expect(canAnimate(pendingGraph(graph).elements[0].paths[0])).toBe(false);
    expect(graph.elements[0].paths[0].hasCirculation).toBe(true);
  });
});

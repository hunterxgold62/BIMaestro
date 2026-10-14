import { describe, expect, it } from 'vitest';
import type { MepGraph } from './mep-contract';
import { explainFlowEnd } from './mep-flow-end';

function fixture(): MepGraph {
  return { elements: [{ key:'pipe',name:'Tuyau',connectorIndices:[0,1],paths:[{startConnector:0,endConnector:1,flowForward:true,flowState:2,hasCirculation:true,directionState:1}] },{key:'gate',name:'Vanne',connectorIndices:[2,3],paths:[]}], connections:[{connectorA:1,connectorB:2,isInternal:false}], valves:[{elementKey:'gate',isEnabledAsValve:true,isClosed:true}], sources:[] } as unknown as MepGraph;
}
describe('Flow ending explanation', () => {
  it('identifies a closed gate using exported connector links', () => { expect(explainFlowEnd(fixture(),'pipe',0,false).elementKey).toBe('gate'); expect(explainFlowEnd(fixture(),'pipe',0,false).title).toBe('Vanne fermée'); });
  it('does not declare that gate blocking when a bypass circulates', () => { const g=fixture(); g.elements.push({...g.elements[0],key:'bypass',connectorIndices:[1,4],paths:[{...g.elements[0].paths[0],startConnector:1,endConnector:4}]}); expect(explainFlowEnd(g,'pipe',0,false).title).toBe('Fin du parcours établi'); });
  it('reports an active return at the endpoint', () => { const g=fixture(); g.valves=[];g.sources=[{elementKey:'gate',isActive:true,boundaryKind:1,entryConnectorIndex:2,exitConnectorIndex:3}]; expect(explainFlowEnd(g,'pipe',0,false).title).toBe('Retour atteint'); });
  it('reports an inlet when exploring upstream', () => { const g=fixture();g.sources=[{elementKey:'inlet',isActive:true,boundaryKind:0,entryConnectorIndex:9,exitConnectorIndex:0}]; expect(explainFlowEnd(g,'pipe',0,true).title).toBe('Arrivée atteinte'); });
  it('leaves the cause uncertain without evidence', () => { const g=fixture();g.connections=[]; expect(explainFlowEnd(g,'pipe',0,false).title).toBe('Fin du parcours établi'); });
});

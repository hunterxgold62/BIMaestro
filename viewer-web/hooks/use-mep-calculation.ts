import { useEffect, useMemo, useState } from 'react';
import type { MepGraph, Scenario, MepAnalysisReference } from '../lib/mep-contract';
import { calculateMep, pendingGraph, type CalculationResult } from '../lib/mep-engine';

export function useMepCalculation(source: MepGraph | undefined, scenario: Scenario | null) {
  const [referenceState, setReference] = useState<{ source: MepGraph; reference?: MepAnalysisReference }>();
  const reference = referenceState && referenceState.source === source ? referenceState.reference : undefined;
  const [state, setState] = useState<{ source: MepGraph; key: string; result?: CalculationResult; error?: string }>();
  const key = JSON.stringify({ valves: scenario?.state.valves, sources: scenario?.state.sources, analysis: scenario?.state.analysis, reference });
  const waiting = useMemo(() => source ? pendingGraph(source) : null, [source]);
  useEffect(() => {
    if (!source) return;
    let cancelled = false;
    const request = JSON.parse(key);
    void calculateMep({ graph: source, valves: request.valves, sources: request.sources, ...request.analysis, reference: request.reference })
      .then(result => { if (!cancelled) setState({ source, key, result }); })
      .catch(error => { if (!cancelled) setState({ source, key, error: error instanceof Error ? error.message : 'Calcul indisponible' }); });
    return () => { cancelled = true; };
  }, [source, key]);
  const current = state?.source === source && state?.key === key ? state : undefined;
  const result = current?.result;
  return { graph: result?.graph ?? waiting, reportText: result?.reportText ?? '', calculating: !!source && !current,
    calculationError: current?.error,
    setReference: async () => {
      if (!result) return;
      const next = await calculateMep({ graph: result.graph, setReference: true });
      setReference({ source: source!, reference: next.graph.analysisReference });
    } };
}

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BIMaestro.VideoGames
{
    internal static class EquipmentAudit
    {
        public static int Run(string replayPath, string scenarioPath, string outputPath)
        {
            var state = JObject.Parse(File.ReadAllText(scenarioPath))["scenario"]["state"];
            GameMepGraphData Calculate(bool allOpen, long[] passageProbe = null)
            {
                var request = new GameMepCalculationRequest { Graph = GameMepReplayStore.Load(replayPath).Graph };
                request.Valves = state["valves"]?.ToObject<Dictionary<string, bool>>() ?? new Dictionary<string, bool>();
                request.Sources = state["sources"]?.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>();
                request.AllowImplicitTerminals = (bool?)state["analysis"]?["allowImplicitTerminals"];
                request.Endpoints = state["analysis"]?["endpoints"]?.ToObject<Dictionary<string, GameMepEndpointRole>>() ?? new Dictionary<string, GameMepEndpointRole>();
                if (allOpen)
                {
                    request.Valves.Clear();
                    foreach (var valve in request.Graph.Valves) valve.IsClosed = false;
                }
                // Read-only counterfactual: allow a two-port equipment's internal
                // traversal without imposing a flow direction or adding a source.
                foreach (long id in passageProbe ?? Array.Empty<long>())
                {
                    request.Graph.RebuildIndexes();
                    var candidate = request.Graph.FindElement(id);
                    if (candidate == null || candidate.ConnectorIndices.Count != 2)
                        throw new ArgumentException("The passage probe only accepts two-port elements.");
                    candidate.IsPipeJunction = true;
                }
                return GameMepCalculation.Run(request);
            }
            var graph = Calculate(false);
            var open = Calculate(true);
            var blocked = graph.Connections.Where(c => !c.IsInternal && !GameMepSystemTraversalPolicy.CanTraverse(graph, c)).ToArray();
            JObject Element(GameMepElementData e)
            {
                bool native = GameMepEquipmentDirectionPolicy.TryGetNativeFlowDirection(graph, e, out int entry, out int exit);
                var ports = e.ConnectorIndices.Select(i => graph.Connectors[i]).ToArray();
                var item = JObject.FromObject(new {
                    id=e.ElementId, name=e.Name, category=e.Category, system=e.SystemName,
                    totalPorts=ports.Length, connectedPorts=ports.Count(c=>c.IsConnected),
                    nativePair=native, entry=native?entry:-1, exit=native?exit:-1,
                    requiresValidation=e.RequiresPassageValidation, arrival=e.ConnectedToInlet, outlet=e.ConnectedToReturn,
                    circulating=e.Paths.Count(p=>p.HasCirculation),
                    circulatingAllValvesOpen=open.FindElement(e.Key).Paths.Count(p=>p.HasCirculation),
                    valveClosed=graph.FindValve(e.Key)?.IsClosed,
                    ports=ports.Select(c=>new { index=c.Index, key=c.Key, connected=c.IsConnected, direction=c.FlowDirection,
                        system=graph.FindSystem(c.SystemKey)?.Name, classification=graph.FindSystem(c.SystemKey)?.Classification,
                        role=c.EndpointRole.ToString(), paths=e.Paths.Where(p=>p.StartConnector==c.Index || p.EndConnector==c.Index)
                            .Select(p=>new { p.HasCirculation,p.FlowForward,p.StartConnector,p.EndConnector,p.DirectionReason }) }),
                    blockedAttachments=blocked.Count(c=>e.ConnectorIndices.Contains(c.ConnectorA)||e.ConnectorIndices.Contains(c.ConnectorB)),
                    blockedInternalPassages=graph.Connections.Count(c=>c.IsInternal && c.ElementKey==e.Key &&
                        graph.Connectors[c.ConnectorA].IsConnected && graph.Connectors[c.ConnectorB].IsConnected &&
                        !GameMepSystemTraversalPolicy.CanTraverse(graph,c)),
                    diagnostics=graph.Diagnostics.Where(d=>d.ElementKey==e.Key && !d.IsAggregate)
                        .Select(d=>new { kind=d.Kind.ToString(), severity=d.Severity.ToString(),d.Title,d.Explanation })
                });
                return item;
            }
            var equipment=graph.Elements.Where(e=>!e.IsPipeCurve && !e.IsPipeFitting && !e.IsPipeJunction).OrderBy(e=>e.ElementId).ToArray();
            var discontinuities = new JArray();
            bool? FlowOut(int index)
            {
                var c=graph.Connectors[index];
                var paths=graph.FindElement(c.ElementKey).Paths.Where(p=>p.HasCirculation && p.DirectionState==GameMepDirectionState.Resolved &&
                    (p.StartConnector==index || p.EndConnector==index)).ToArray();
                var values=paths.Select(p=>p.StartConnector==index ? !p.FlowForward : p.FlowForward).Distinct().ToArray();
                return values.Length==1?(bool?)values[0]:null;
            }
            foreach(var edge in graph.Connections.Where(c=>!c.IsInternal))
            {
                var a=FlowOut(edge.ConnectorA); var b=FlowOut(edge.ConnectorB);
                if(a.HasValue && b.HasValue && a==b)
                    discontinuities.Add(JObject.FromObject(new {
                        a=graph.FindElement(graph.Connectors[edge.ConnectorA].ElementKey).ElementId,
                        b=graph.FindElement(graph.Connectors[edge.ConnectorB].ElementKey).ElementId,
                        connectorA=edge.ConnectorA,connectorB=edge.ConnectorB,bothFlowOut=a.Value }));
            }
            var report=JObject.FromObject(new {
                engine=graph.EngineVersion,elements=graph.Elements.Count,equipmentCount=equipment.Length,
                connectors=graph.Connectors.Count,physicalConnections=graph.Connections.Count(c=>!c.IsInternal),
                activeSources=graph.Sources.Where(s=>s.IsActive).Select(s=>new {id=graph.FindElement(s.ElementKey).ElementId,s.Name,kind=s.BoundaryKind.ToString(),
                    usable=GameMepBoundaryPolicy.IsUsable(graph.FindElement(s.ElementKey),s)}),
                closedValves=graph.Valves.Where(v=>v.IsEnabledAsValve&&v.IsClosed).Select(v=>graph.FindElement(v.ElementKey).ElementId),
                blockedAttachments=blocked.Select(c=>new {
                    a=graph.FindElement(graph.Connectors[c.ConnectorA].ElementKey).ElementId,
                    b=graph.FindElement(graph.Connectors[c.ConnectorB].ElementKey).ElementId,
                    connectorA=c.ConnectorA,connectorB=c.ConnectorB,
                    systemA=graph.FindSystem(graph.Connectors[c.ConnectorA].SystemKey)?.Name,
                    systemB=graph.FindSystem(graph.Connectors[c.ConnectorB].SystemKey)?.Name }),
                diagnostics=graph.Diagnostics.Select(d=>new {id=graph.FindElement(d.ElementKey)?.ElementId,kind=d.Kind.ToString(),severity=d.Severity.ToString(),
                    d.Title,d.Explanation,d.IsAggregate,d.ShowInSmartMode})
            });
            report["equipment"]=new JArray(equipment.Select(Element));
            report["discontinuities"]=discontinuities;
            report["twoPortPassageProbes"] = new JArray(equipment.Where(e=>e.ConnectorIndices.Count==2 &&
                graph.Connections.Any(c=>c.IsInternal && c.ElementKey==e.Key &&
                    !GameMepSystemTraversalPolicy.CanTraverse(graph,c))).Select(e=> {
                    var probe=Calculate(true,new[]{e.ElementId});
                    return JObject.FromObject(new { id=e.ElementId,
                        affectedEquipment=equipment.Where(item=>open.FindElement(item.Key).Paths.Count(p=>p.HasCirculation)!=
                            probe.FindElement(item.Key).Paths.Count(p=>p.HasCirculation)).Select(item=>new {
                                id=item.ElementId,name=item.Name,
                                before=open.FindElement(item.Key).Paths.Count(p=>p.HasCirculation),
                                after=probe.FindElement(item.Key).Paths.Count(p=>p.HasCirculation)}) });
                }));
            File.WriteAllText(outputPath,report.ToString(Formatting.Indented));
            GameMepReplayStore.Save(graph,outputPath+".replay.json");
            Console.WriteLine($"Audit {graph.EngineVersion}: {graph.Elements.Count} elements; {equipment.Length} equipment; {blocked.Length} blocked physical connections; {discontinuities.Count} flow discontinuities.");
            return 0;
        }
    }
}

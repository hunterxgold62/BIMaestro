using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BIMaestro.VideoGames
{
    internal sealed class GameMepCalculationRequest
    {
        public GameMepGraphData Graph { get; set; }
        public Dictionary<string, bool> Valves { get; set; } = new Dictionary<string, bool>();
        public Dictionary<string, string> Sources { get; set; } = new Dictionary<string, string>();
        public bool? AllowImplicitTerminals { get; set; }
        public Dictionary<string, GameMepEndpointRole> Endpoints { get; set; } = new Dictionary<string, GameMepEndpointRole>();
        public GameMepAnalysisReference Reference { get; set; }
        public bool SetReference { get; set; }
    }
    internal static class GameMepCalculation
    {
        public static GameMepGraphData Run(GameMepCalculationRequest request)
        {
            var graph = request.Graph ?? throw new ArgumentException("Graphe MEP absent.");
            if (graph.Connectors.Count > 250000 || graph.Connections.Count > 1000000) throw new ArgumentException("Réseau trop volumineux pour ce calcul.");
            if (graph.Elements.Any(e => e == null || string.IsNullOrWhiteSpace(e.Key)) || graph.Elements.Select(e => e.Key).Distinct().Count() != graph.Elements.Count)
                throw new ArgumentException("Identifiants MEP absents ou dupliqués.");
            for (int i = 0; i < graph.Connectors.Count; i++) if (graph.Connectors[i].Index != i) throw new ArgumentException("Index des connecteurs invalide.");
            graph.RebuildIndexes();
            foreach (var element in graph.Elements) foreach (var path in element.Paths) path.FinalizePath();
            var engine = new GameMepSimulationEngine(graph);
            // Old publications gain a freshly calculated reference; stale
            // exported visual states are never used as solver constraints.
            if (request.Reference != null) graph.AnalysisReference = request.Reference;
            if (graph.AnalysisReference == null) engine.Recalculate();
            var byId = new Dictionary<string, GameMepElementData>(StringComparer.Ordinal);
            foreach (var element in graph.Elements)
            {
                byId[element.Key] = element;
                if (!string.IsNullOrEmpty(element.PersistentId)) byId[element.PersistentId] = element;
            }
            foreach (var pair in request.Valves)
            {
                var valve = byId.TryGetValue(pair.Key, out var element) ? graph.FindValve(element.Key) : null;
                if (valve == null || !valve.IsEnabledAsValve) { graph.SkippedScenarioEntryCount++; continue; }
                valve.IsClosed = pair.Value;
            }
            foreach (var pair in request.Sources)
            {
                if (!byId.TryGetValue(pair.Key, out var element) || !GameMepBoundaryPolicy.CanHostBoundary(element) ||
                    (pair.Value != "inlet" && pair.Value != "outlet" && pair.Value != "none")) { graph.SkippedScenarioEntryCount++; continue; }
                var candidates = graph.Sources.Where(s => s.ElementKey == element.Key).ToList();
                candidates.ForEach(s => s.IsActive = false);
                if (pair.Value == "none") continue;
                var kind = pair.Value == "outlet" ? GameMepBoundaryKind.Outlet : GameMepBoundaryKind.Inlet;
                var source = candidates.FirstOrDefault(s => s.BoundaryKind == kind);
                if (source == null)
                {
                    // No port choice means no fabricated directional boundary.
                    // The user can define the precise ports in the source model.
                    source = new GameMepSourceData { ElementKey = element.Key, SystemKey = element.SystemKey, Name = element.Name,
                        BoundaryKind = kind, IsUserCreated = true, WasManuallyOverridden = true };
                    graph.Sources.Add(source);
                }
                source.IsActive = true;
            }
            if (request.AllowImplicitTerminals.HasValue) graph.AllowImplicitTerminals = request.AllowImplicitTerminals.Value;
            var endpoints = graph.Connectors.Where(c => !string.IsNullOrEmpty(c.PersistentKey)).GroupBy(c => c.PersistentKey).ToDictionary(g => g.Key, g => g.First());
            foreach (var pair in request.Endpoints)
            {
                if (!endpoints.TryGetValue(pair.Key, out var connector) || !Enum.IsDefined(typeof(GameMepEndpointRole), pair.Value)) { graph.SkippedScenarioEntryCount++; continue; }
                connector.EndpointRole = pair.Value;
            }
            engine.Recalculate();
            if (request.SetReference) GameMepImpactAnalyzer.SetReference(graph, "Scénario de référence choisi");
            return graph;
        }
        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            ObjectCreationHandling = ObjectCreationHandling.Auto,
            NullValueHandling = NullValueHandling.Ignore
        };
    }
}

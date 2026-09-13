using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BIMaestro.VideoGames
{
    internal sealed class GameMepAnalysisReference
    {
        public string ModelFingerprint { get; set; } = string.Empty;
        public string ScenarioFingerprint { get; set; } = string.Empty;
        public string EngineVersion { get; set; } = string.Empty;
        public string Label { get; set; } = "Référence à l'ouverture";
        public IList<GameMepImpactState> Elements { get; set; } = new List<GameMepImpactState>();
        public IDictionary<string, bool> Valves { get; set; } = new Dictionary<string, bool>();
    }
    internal sealed class GameMepImpactState
    {
        public string ElementKey { get; set; } = string.Empty;
        public bool Arrival { get; set; }
        public bool Return { get; set; }
        public bool Circulation { get; set; }
        public IList<string> ArrivalRoute { get; set; } = new List<string>();
    }
    internal sealed class GameMepImpactItem
    {
        public string ElementKey { get; set; } = string.Empty;
        public long ElementId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public bool IsEquipment { get; set; }
        public string Change { get; set; } = string.Empty;
        public string Before { get; set; } = string.Empty;
        public string After { get; set; } = string.Empty;
        public IList<string> LimitingValveKeys { get; set; } = new List<string>();
        public IList<string> AlternativeRouteKeys { get; set; } = new List<string>();
    }
    internal sealed class GameMepImpactReport
    {
        public string EngineVersion { get; set; } = string.Empty;
        public string ModelFingerprint { get; set; } = string.Empty;
        public string ScenarioFingerprint { get; set; } = string.Empty;
        public string ReferenceFingerprint { get; set; } = string.Empty;
        public string ReferenceLabel { get; set; } = string.Empty;
        public bool Comparable { get; set; }
        public IList<string> Assumptions { get; set; } = new List<string>();
        public IList<string> ChangedValveKeys { get; set; } = new List<string>();
        public IList<GameMepImpactItem> Items { get; set; } = new List<GameMepImpactItem>();
        public int LostArrivalCount => Items.Count(item => item.Change == "Connexion à l'arrivée perdue");
        public int EquipmentLostArrivalCount => Items.Count(item => item.IsEquipment && item.Change == "Connexion à l'arrivée perdue");
        public int AlternativeCount => Items.Count(item => item.AlternativeRouteKeys.Count > 0);
    }

    internal static class GameMepImpactAnalyzer
    {
        public const string EngineVersion = "mep-topology-2.0.1";
        public static string ConnectionLabel(bool arrival, bool outlet) => arrival
            ? (outlet ? "Relié à une arrivée et à un retour" : "Relié à une arrivée")
            : (outlet ? "Relié à un retour uniquement" : "Aucune arrivée ni retour accessible");

        public static string StateLabel(GameMepElementData element) => ConnectionLabel(element.ConnectedToInlet, element.ConnectedToReturn);
        private static string StateLabel(GameMepImpactState state) => ConnectionLabel(state.Arrival, state.Return) +
            (state.Circulation ? " ; circulation supposée" : " ; circulation non établie");

        public static void SetReference(GameMepGraphData graph, string label)
        {
            var routes = ArrivalRoutes(graph);
            graph.AnalysisReference = new GameMepAnalysisReference
            {
                ModelFingerprint = ModelFingerprint(graph), ScenarioFingerprint = ScenarioFingerprint(graph),
                EngineVersion = EngineVersion, Label = label,
                Elements = graph.Elements.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => Capture(e, routes)).ToList(),
                Valves = graph.Valves.ToDictionary(v => v.ElementKey, v => v.IsClosed)
            };
            Refresh(graph);
        }

        public static void Refresh(GameMepGraphData graph)
        {
            if (graph.AnalysisReference == null)
            {
                SetReference(graph, "Référence à l'ouverture");
                return;
            }
            var reference = graph.AnalysisReference;
            var report = new GameMepImpactReport
            {
                EngineVersion = EngineVersion, ModelFingerprint = ModelFingerprint(graph),
                ScenarioFingerprint = ScenarioFingerprint(graph), ReferenceFingerprint = reference.ScenarioFingerprint,
                ReferenceLabel = reference.Label
            };
            graph.ImpactReport = report;
            report.Comparable = reference.EngineVersion == EngineVersion && reference.ModelFingerprint == report.ModelFingerprint;
            report.Assumptions.Add("Flux indicatifs : ni débit, ni pression, ni vitesse physique calculés.");
            report.Assumptions.Add(graph.AllowImplicitTerminals
                ? "Extrémités non renseignées considérées comme des débouchés possibles. À vérifier dans la maquette."
                : "Mode explicite : seules les arrivées, retours et extrémités qualifiées servent de limites de circulation.");
            report.Assumptions.Add("Un chemin alternatif prouve une connexion topologique, pas la capacité à fournir le débit requis.");
            if (graph.Elements.Any(e => e.RequiresPassageValidation)) report.Assumptions.Add("Équipements multivoies : traversée limitée aux couples imposés ou à l'unique paire In/Out raccordée reconnue ; ports annexes exclus, consulter les diagnostics.");
            if (graph.Valves.Any(v => !v.IsEnabledAsValve)) report.Assumptions.Add("Organes non validés comme vannes d'isolement : leur comportement spécialisé n'est pas simulé.");
            int unknownEnds = graph.Connectors.Count(c => !c.IsConnected && c.EndpointRole == GameMepEndpointRole.Unknown);
            int exportEnds = graph.Connectors.Count(c => c.EndpointRole == GameMepEndpointRole.ExportLimit);
            if (unknownEnds > 0) report.Assumptions.Add(unknownEnds + " connecteurs non renseignés : vérifier les limites du réseau.");
            if (exportEnds > 0) report.Assumptions.Add(exportEnds + " limites d'export : résultat incomplet au-delà du périmètre publié.");
            if (graph.DirectionConflictCount > 0) report.Assumptions.Add(graph.DirectionConflictCount + " sens contradictoires : flèches arrêtées sur ces chemins.");
            if (graph.SkippedScenarioEntryCount > 0) report.Assumptions.Add(graph.SkippedScenarioEntryCount + " réglages obsolètes ignorés : vérifier les éléments concernés.");
            if (!report.Comparable)
            {
                report.Assumptions.Add("La référence appartient à un autre réseau ou moteur. Définir une nouvelle référence avant de comparer.");
                return;
            }
            var before = reference.Elements.ToDictionary(e => e.ElementKey, StringComparer.Ordinal);
            var newlyClosed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var valve in graph.Valves.OrderBy(v => v.ElementKey, StringComparer.Ordinal))
            {
                if (reference.Valves.TryGetValue(valve.ElementKey, out bool old) && old != valve.IsClosed)
                {
                    report.ChangedValveKeys.Add(valve.ElementKey);
                    if (valve.IsEnabledAsValve && valve.IsClosed) newlyClosed.Add(valve.ElementKey);
                }
            }
            var routes = ArrivalRoutes(graph);
            foreach (var element in graph.Elements.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!before.TryGetValue(element.Key, out var old)) continue;
                var current = Capture(element, routes);
                var blocked = old.ArrivalRoute.Where(newlyClosed.Contains).Distinct().ToList();
                bool alternative = old.Arrival && current.Arrival && blocked.Count > 0 && current.ArrivalRoute.Count > 0 &&
                    !current.ArrivalRoute.Any(newlyClosed.Contains);
                if (old.Arrival == current.Arrival && old.Return == current.Return && old.Circulation == current.Circulation && !alternative) continue;
                report.Items.Add(new GameMepImpactItem
                {
                    ElementKey = element.Key, ElementId = element.ElementId, Name = element.Name, SystemName = element.SystemName,
                    IsEquipment = !element.IsPipeCurve && !element.IsPipeFitting && graph.FindValve(element.Key) == null,
                    Change = old.Arrival && !current.Arrival ? "Connexion à l'arrivée perdue" : !old.Arrival && current.Arrival ? "Connexion à l'arrivée rétablie"
                        : alternative ? "Connexion maintenue par un autre chemin" : old.Circulation && !current.Circulation ? "Circulation supposée interrompue" : "État de connexion ou circulation modifié",
                    Before = StateLabel(old), After = StateLabel(current), LimitingValveKeys = blocked,
                    AlternativeRouteKeys = alternative ? current.ArrivalRoute : new List<string>()
                });
            }
        }

        private static GameMepImpactState Capture(GameMepElementData element, IDictionary<string, IList<string>> routes) => new GameMepImpactState
        {
            ElementKey = element.Key, Arrival = element.ConnectedToInlet, Return = element.ConnectedToReturn,
            Circulation = element.Paths.Any(p => p.HasCirculation && p.DirectionState == GameMepDirectionState.Resolved),
            ArrivalRoute = routes.TryGetValue(element.Key, out var route) ? route : new List<string>()
        };

        // A deterministic witness route, not a claim that all alternative paths
        // or every possible isolation combination have been enumerated.
        private static IDictionary<string, IList<string>> ArrivalRoutes(GameMepGraphData graph)
        {
            int count = graph.Connectors.Count;
            var adjacency = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
            foreach (var edge in graph.Connections)
            {
                if (edge.ConnectorA < 0 || edge.ConnectorB < 0 || edge.ConnectorA >= count || edge.ConnectorB >= count || !GameMepSystemTraversalPolicy.CanTraverse(graph, edge)) continue;
                var valve = graph.FindValve(edge.ElementKey);
                if (valve != null && valve.IsEnabledAsValve && valve.IsClosed && (edge.IsInternal || edge.IsValveGateCandidate)) continue;
                adjacency[edge.ConnectorA].Add(edge.ConnectorB);
                adjacency[edge.ConnectorB].Add(edge.ConnectorA);
            }
            var parent = Enumerable.Repeat(-2, count).ToArray();
            var witness = new IList<string>[count];
            var stops = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (var source in graph.Sources.Where(s => s.IsActive && s.BoundaryKind == GameMepBoundaryKind.Inlet).OrderBy(s => s.ElementKey, StringComparer.Ordinal))
            {
                var element = graph.FindElement(source.ElementKey);
                if (!GameMepBoundaryPolicy.IsUsable(element, source)) continue;
                var seeds = source.HasExplicitDirection ? new[] { source.EntryConnectorIndex } : element.ConnectorIndices.ToArray();
                if (source.HasExplicitDirection) stops.Add(source.EntryConnectorIndex);
                foreach (int seed in seeds) if (seed >= 0 && seed < count && parent[seed] == -2) { parent[seed] = -1; witness[seed] = new[] { source.ElementKey }; queue.Enqueue(seed); }
            }
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in adjacency[current].OrderBy(i => graph.Connectors[i].Key, StringComparer.Ordinal).ThenBy(i => i))
                {
                    if (parent[next] != -2 || (stops.Contains(current) && graph.Connectors[current].ElementKey != graph.Connectors[next].ElementKey)) continue;
                    parent[next] = current;
                    string owner = graph.Connectors[next].ElementKey;
                    witness[next] = graph.FindValve(owner) != null && graph.Connectors[current].ElementKey != owner
                        ? witness[current].Concat(new[] { owner }).ToArray() : witness[current];
                    queue.Enqueue(next);
                }
            }
            var result = new Dictionary<string, IList<string>>(StringComparer.Ordinal);
            foreach (var element in graph.Elements)
            {
                int found = element.ConnectorIndices.Where(i => i >= 0 && i < count && parent[i] != -2).OrderBy(i => graph.Connectors[i].Key, StringComparer.Ordinal).DefaultIfEmpty(-1).First();
                if (found < 0) continue;
                // Store source/valve landmarks, not every pipe in every route:
                // a long network must not generate a quadratic export payload.
                result[element.Key] = witness[found].Concat(new[] { element.Key }).Distinct().ToList();
            }
            return result;
        }

        public static string ModelFingerprint(GameMepGraphData graph) => Hash(string.Join("\n", graph.Elements.Select(e => e.Key + ":" + e.SystemKey + ":" + e.RequiresPassageValidation + ":" + e.IsPipeJunction + ":" + e.ConnectorIndices.Count).OrderBy(s => s, StringComparer.Ordinal)) + "\n" +
            string.Join("\n", graph.Connectors.Select(c => c.Index + ":" + c.Key + ":" + c.ElementKey + ":" + c.SystemKey + ":" + c.FlowDirection + ":" + c.CrossSectionArea.ToString("G12", System.Globalization.CultureInfo.InvariantCulture)).OrderBy(s => s, StringComparer.Ordinal)) + "\n" +
            string.Join("\n", graph.Systems.Select(s => s.Key + ":" + s.Abbreviation + ":" + s.Classification).OrderBy(s => s, StringComparer.Ordinal)) + "\n" +
            string.Join("\n", graph.Connections.Select(e => Math.Min(e.ConnectorA, e.ConnectorB) + ":" + Math.Max(e.ConnectorA, e.ConnectorB) + ":" + e.ElementKey + ":" + e.IsInternal).OrderBy(s => s, StringComparer.Ordinal)));
        public static string ScenarioFingerprint(GameMepGraphData graph) => Hash(string.Join("\n", graph.Valves.Select(v => v.ElementKey + ":" + v.IsClosed + ":" + v.IsEnabledAsValve).Concat(
            graph.Sources.Select(s => s.ElementKey + ":" + s.IsActive + ":" + s.BoundaryKind + ":" + s.EntryConnectorIndex + ":" + s.ExitConnectorIndex)).Concat(
            graph.DirectionConstraints.Select(d => d.ElementKey + ":" + d.IsActive + ":" + d.Scope + ":" + d.EntryConnectorIndex + ":" + d.ExitConnectorIndex)).Concat(
            graph.Connectors.Select(c => c.Key + ":" + c.EndpointRole)).OrderBy(s => s, StringComparer.Ordinal)) + "\n" + graph.AllowImplicitTerminals);
        private static string Hash(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }

        public static string ToText(GameMepGraphData graph)
        {
            var report = graph.ImpactReport;
            if (report == null) return "Analyse indisponible : recalculer le réseau.";
            string Name(string key) { var e = graph.FindElement(key); return e == null ? key : e.Name + " [" + e.ElementId + "]"; }
            var lines = new List<string> { "BIMaestro — Analyse des coupures", "Modèle : " + graph.DocumentTitle,
                "Moteur : " + report.EngineVersion, "Réseau : " + report.ModelFingerprint, "Scénario : " + report.ScenarioFingerprint,
                "Référence : " + report.ReferenceLabel + " — " + report.ReferenceFingerprint, "", "Hypothèses et limites :" };
            lines.AddRange(report.Assumptions.Select(a => "- " + a));
            lines.Add("Vannes modifiées : " + string.Join(", ", report.ChangedValveKeys.Select(Name)));
            lines.Add(report.EquipmentLostArrivalCount + " équipements sans connexion à une arrivée ; " + report.LostArrivalCount + " éléments concernés au total.");
            lines.Add("Chemins alternatifs identifiés : " + report.AlternativeCount);
            if (report.Comparable && report.Items.Count == 0) lines.Add("Aucun changement d'accès ou de circulation détecté par rapport à la référence.");
            foreach (var item in report.Items)
            {
                lines.Add(""); lines.Add(Name(item.ElementKey) + " — " + item.SystemName + " — " + item.Change);
                lines.Add("Avant : " + item.Before); lines.Add("Après : " + item.After);
                if (item.LimitingValveKeys.Count > 0) lines.Add("Vannes fermées sur le chemin de référence : " + string.Join(", ", item.LimitingValveKeys.Select(Name)));
                if (item.AlternativeRouteKeys.Count > 0) lines.Add("Autre chemin (arrivée, vannes, élément) : " + string.Join(" → ", item.AlternativeRouteKeys.Select(Name)));
            }
            lines.Add(""); lines.Add("Diagnostics de la maquette :");
            lines.AddRange(graph.Diagnostics.Where(d => !d.IsAggregate).OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => Name(d.ElementKey) + " — " + d.Title + " : " + d.Explanation));
            return string.Join("\n", lines);
        }
    }
}

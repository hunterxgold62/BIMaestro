using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media.Media3D;

namespace BIMaestro.VideoGames
{
    internal enum GameMepDiagnosticSeverity
    {
        Information,
        Warning,
        Critical
    }

    internal enum GameMepDiagnosticKind
    {
        DirectionConflict,
        CheckValveDirectionMissing,
        AmbiguousFlowControl,
        UnknownPassThroughComponent,
        BranchWithoutSource,
        IncompatibleSystems,
        DisconnectedElement,
        OpenConnector,
        InvalidSavedSetting
    }

    internal sealed class GameMepDiagnosticData
    {
        public string Key { get; set; } = string.Empty;
        public GameMepDiagnosticKind Kind { get; set; }
        public GameMepDiagnosticSeverity Severity { get; set; }
        public string ElementKey { get; set; } = string.Empty;
        public int ConnectorIndex { get; set; } = -1;
        public string SystemKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
        public Point3D Position { get; set; }
        public bool HasPosition { get; set; }
        public int OccurrenceCount { get; set; } = 1;
        public bool ShowInSmartMode { get; set; } = true;
        public bool IsAggregate { get; set; }
    }

    internal static class GameMepDiagnosticAnalyzer
    {
        private const string UnassignedSystemToken = "NON_AFFECTE";

        private sealed class ComponentInfo
        {
            public int Index { get; set; }
            public IList<GameMepElementData> Elements { get; } =
                new List<GameMepElementData>();
        }

        public static void Refresh(GameMepGraphData graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var stopwatch = Stopwatch.StartNew();
            graph.Diagnostics.Clear();
            if (!graph.HasData)
            {
                graph.LastDiagnosticMilliseconds = 0.0;
                return;
            }

            graph.RebuildIndexes();
            Dictionary<string, int> componentByElement;
            IList<ComponentInfo> components = BuildComponents(
                graph,
                out componentByElement);

            AddDirectionConflicts(graph);
            AddFlowControlDiagnostics(graph);
            AddDisconnectedElements(graph);
            AddBranchWithoutSource(graph, components);
            AddOpenConnectors(graph, components, componentByElement);
            AddUnknownPassThroughComponents(
                graph,
                components,
                componentByElement);
            AddIncompatibleSystems(graph, components, componentByElement);
            AddInvalidSavedSettings(graph);

            stopwatch.Stop();
            graph.LastDiagnosticMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        private static IList<ComponentInfo> BuildComponents(
            GameMepGraphData graph,
            out Dictionary<string, int> componentByElement)
        {
            var neighbors = graph.Elements.ToDictionary(
                element => element.Key,
                element => new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);
            foreach (GameMepConnectionData connection in graph.Connections)
            {
                if (connection.ConnectorA < 0 || connection.ConnectorB < 0 ||
                    connection.ConnectorA >= graph.Connectors.Count ||
                    connection.ConnectorB >= graph.Connectors.Count)
                {
                    continue;
                }
                string first = graph.Connectors[connection.ConnectorA].ElementKey;
                string second = graph.Connectors[connection.ConnectorB].ElementKey;
                if (string.IsNullOrWhiteSpace(first) ||
                    string.IsNullOrWhiteSpace(second) ||
                    string.Equals(first, second, StringComparison.Ordinal) ||
                    !neighbors.ContainsKey(first) || !neighbors.ContainsKey(second))
                {
                    continue;
                }
                neighbors[first].Add(second);
                neighbors[second].Add(first);
            }

            componentByElement = new Dictionary<string, int>(StringComparer.Ordinal);
            var components = new List<ComponentInfo>();
            foreach (GameMepElementData root in graph.Elements
                .OrderBy(element => element.Key, StringComparer.Ordinal))
            {
                if (componentByElement.ContainsKey(root.Key))
                    continue;
                var component = new ComponentInfo { Index = components.Count };
                var queue = new Queue<string>();
                queue.Enqueue(root.Key);
                componentByElement[root.Key] = component.Index;
                while (queue.Count > 0)
                {
                    string key = queue.Dequeue();
                    GameMepElementData? element = graph.FindElement(key);
                    if (element != null)
                        component.Elements.Add(element);
                    foreach (string neighbor in neighbors[key]
                        .OrderBy(value => value, StringComparer.Ordinal))
                    {
                        if (componentByElement.ContainsKey(neighbor))
                            continue;
                        componentByElement[neighbor] = component.Index;
                        queue.Enqueue(neighbor);
                    }
                }
                components.Add(component);
            }
            return components;
        }

        private static void AddDirectionConflicts(GameMepGraphData graph)
        {
            foreach (GameMepElementData element in graph.Elements)
            {
                int count = element.Paths.Count(path =>
                    path.DirectionState == GameMepDirectionState.Conflict);
                if (count == 0)
                    continue;
                graph.Diagnostics.Add(CreateElementDiagnostic(
                    graph,
                    element,
                    GameMepDiagnosticKind.DirectionConflict,
                    GameMepDiagnosticSeverity.Critical,
                    "Conflit de sens",
                    count == 1
                        ? "Plusieurs chemins imposent des sens incompatibles sur cet élément."
                        : count + " chemins de cet élément ont un sens contradictoire.",
                    "direction|" + element.Key));
            }
        }

        private static void AddFlowControlDiagnostics(GameMepGraphData graph)
        {
            foreach (GameMepValveData valve in graph.Valves)
            {
                GameMepElementData? element = graph.FindElement(valve.ElementKey);
                if (element == null)
                    continue;
                if (valve.IsEnabledAsValve &&
                    valve.Kind == GameMepFlowControlKind.CheckValve &&
                    !valve.HasExplicitDirection)
                {
                    graph.Diagnostics.Add(CreateElementDiagnostic(
                        graph,
                        element,
                        GameMepDiagnosticKind.CheckValveDirectionMissing,
                        GameMepDiagnosticSeverity.Warning,
                        "Orientation du clapet à définir",
                        "Ce clapet n'est pas considéré comme incorrect : son sens n'a simplement pas encore été indiqué ou déduit avec une confiance suffisante.",
                        "check-direction|" + element.Key));
                }
                if (valve.Confidence == GameMepConfidence.High ||
                    valve.WasManuallyOverridden)
                {
                    continue;
                }
                bool detailedOnly = valve.Confidence == GameMepConfidence.Low &&
                    !valve.IsEnabledAsValve;
                graph.Diagnostics.Add(CreateElementDiagnostic(
                    graph,
                    element,
                    GameMepDiagnosticKind.AmbiguousFlowControl,
                    detailedOnly
                        ? GameMepDiagnosticSeverity.Information
                        : GameMepDiagnosticSeverity.Warning,
                    valve.Kind == GameMepFlowControlKind.CheckValve
                        ? "Clapet incertain"
                        : "Vanne ou accessoire ambigu",
                    "La classification automatique est " +
                        ToConfidenceText(valve.Confidence) +
                        ". Une validation manuelle est recommandée.",
                    "control|" + element.Key,
                    detailedOnly));
            }
        }

        private static void AddDisconnectedElements(GameMepGraphData graph)
        {
            foreach (GameMepElementData element in graph.Elements)
            {
                if (element.ConnectorIndices.Count == 0 ||
                    element.ConnectorIndices.Any(index =>
                        index >= 0 && index < graph.Connectors.Count &&
                        graph.Connectors[index].IsConnected))
                {
                    continue;
                }
                bool boundary = graph.Sources.Any(source =>
                    string.Equals(source.ElementKey, element.Key, StringComparison.Ordinal));
                graph.Diagnostics.Add(CreateElementDiagnostic(
                    graph,
                    element,
                    GameMepDiagnosticKind.DisconnectedElement,
                    boundary || element.ConnectorIndices.Count == 1
                        ? GameMepDiagnosticSeverity.Information
                        : GameMepDiagnosticSeverity.Warning,
                    "Élément déconnecté",
                    "Aucun connecteur de cet élément n'est raccordé physiquement dans Revit.",
                    "disconnected|" + element.Key,
                    boundary || element.ConnectorIndices.Count == 1));
            }
        }

        private static void AddBranchWithoutSource(
            GameMepGraphData graph,
            IList<ComponentInfo> components)
        {
            var activeSourceElements = new HashSet<string>(
                graph.Sources.Where(source =>
                        source.IsActive &&
                        source.BoundaryKind == GameMepBoundaryKind.Inlet)
                    .Select(source => source.ElementKey),
                StringComparer.Ordinal);
            foreach (ComponentInfo component in components)
            {
                if (component.Elements.Count == 0 ||
                    component.Elements.Any(element =>
                        activeSourceElements.Contains(element.Key)))
                {
                    continue;
                }
                GameMepElementData representative = ChooseRepresentative(component.Elements);
                graph.Diagnostics.Add(CreateElementDiagnostic(
                    graph,
                    representative,
                    GameMepDiagnosticKind.BranchWithoutSource,
                    GameMepDiagnosticSeverity.Warning,
                    "Branche sans source active",
                    component.Elements.Count +
                        " élément(s) appartiennent à une branche qui ne contient aucune arrivée active.",
                    "source|component-" + component.Index,
                    false,
                    component.Elements.Count));
            }
        }

        private static void AddOpenConnectors(
            GameMepGraphData graph,
            IList<ComponentInfo> components,
            IDictionary<string, int> componentByElement)
        {
            var grouped = new Dictionary<int, List<GameMepDiagnosticData>>();
            foreach (GameMepConnectorData connector in graph.Connectors
                .Where(item => !item.IsConnected))
            {
                GameMepElementData? element = graph.FindElement(connector.ElementKey);
                if (element == null)
                    continue;
                bool legitimate = IsLikelyLegitimateOpenConnector(graph, element, connector);
                var detailed = new GameMepDiagnosticData
                {
                    Key = "open|" + connector.Key,
                    Kind = GameMepDiagnosticKind.OpenConnector,
                    Severity = legitimate
                        ? GameMepDiagnosticSeverity.Information
                        : GameMepDiagnosticSeverity.Warning,
                    ElementKey = element.Key,
                    ConnectorIndex = connector.Index,
                    SystemKey = connector.SystemKey,
                    Title = legitimate
                        ? "Extrémité ouverte probablement légitime"
                        : "Connecteur ouvert",
                    Explanation = legitimate
                        ? "Cette extrémité correspond probablement à une limite, une source ou un terminal."
                        : "Ce connecteur n'est relié à aucun autre connecteur Revit.",
                    Position = connector.Position,
                    HasPosition = IsFinite(connector.Position),
                    ShowInSmartMode = false
                };
                graph.Diagnostics.Add(detailed);
                if (legitimate ||
                    !componentByElement.TryGetValue(element.Key, out int componentIndex))
                {
                    continue;
                }
                if (!grouped.TryGetValue(componentIndex, out List<GameMepDiagnosticData> list))
                {
                    list = new List<GameMepDiagnosticData>();
                    grouped[componentIndex] = list;
                }
                list.Add(detailed);
            }

            foreach (KeyValuePair<int, List<GameMepDiagnosticData>> pair in grouped)
            {
                if (pair.Value.Count == 0 || pair.Key < 0 || pair.Key >= components.Count)
                    continue;
                GameMepDiagnosticData first = pair.Value
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .First();
                graph.Diagnostics.Add(new GameMepDiagnosticData
                {
                    Key = "open|component-" + pair.Key,
                    Kind = GameMepDiagnosticKind.OpenConnector,
                    Severity = GameMepDiagnosticSeverity.Warning,
                    ElementKey = first.ElementKey,
                    ConnectorIndex = first.ConnectorIndex,
                    SystemKey = first.SystemKey,
                    Title = pair.Value.Count == 1
                        ? "1 connecteur ouvert dans cette branche"
                        : pair.Value.Count + " connecteurs ouverts dans cette branche",
                    Explanation = "Le diagnostic intelligent regroupe les extrémités non raccordées de cette branche.",
                    Position = first.Position,
                    HasPosition = first.HasPosition,
                    OccurrenceCount = pair.Value.Count,
                    ShowInSmartMode = true,
                    IsAggregate = true
                });
            }
        }

        private static void AddUnknownPassThroughComponents(
            GameMepGraphData graph,
            IList<ComponentInfo> components,
            IDictionary<string, int> componentByElement)
        {
            var groups = new Dictionary<string, List<GameMepDiagnosticData>>(
                StringComparer.Ordinal);
            foreach (GameMepElementData element in graph.Elements)
            {
                if (!IsUnknownPassThrough(graph, element))
                    continue;
                var detailed = CreateElementDiagnostic(
                    graph,
                    element,
                    GameMepDiagnosticKind.UnknownPassThroughComponent,
                    GameMepDiagnosticSeverity.Information,
                    "Composant traversant non classé",
                    "Le fluide traverse cet accessoire, mais son rôle fonctionnel n'est pas reconnu.",
                    "unknown|" + element.Key,
                    true);
                graph.Diagnostics.Add(detailed);
                int component = componentByElement.TryGetValue(element.Key, out int value)
                    ? value
                    : -1;
                string family = string.IsNullOrWhiteSpace(element.TypeName)
                    ? element.Category
                    : element.TypeName;
                string groupKey = component + "|" + family;
                if (!groups.TryGetValue(groupKey, out List<GameMepDiagnosticData> list))
                {
                    list = new List<GameMepDiagnosticData>();
                    groups[groupKey] = list;
                }
                list.Add(detailed);
            }

            foreach (KeyValuePair<string, List<GameMepDiagnosticData>> pair in groups)
            {
                GameMepDiagnosticData first = pair.Value
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .First();
                graph.Diagnostics.Add(new GameMepDiagnosticData
                {
                    Key = "unknown-group|" + pair.Key,
                    Kind = GameMepDiagnosticKind.UnknownPassThroughComponent,
                    Severity = GameMepDiagnosticSeverity.Information,
                    ElementKey = first.ElementKey,
                    SystemKey = first.SystemKey,
                    Title = pair.Value.Count == 1
                        ? "1 composant traversant non classé"
                        : pair.Value.Count + " composants traversants non classés",
                    Explanation = "Ces composants de même type sont regroupés dans le diagnostic intelligent.",
                    Position = first.Position,
                    HasPosition = first.HasPosition,
                    OccurrenceCount = pair.Value.Count,
                    ShowInSmartMode = true,
                    IsAggregate = true
                });
            }
        }

        private static void AddIncompatibleSystems(
            GameMepGraphData graph,
            IList<ComponentInfo> components,
            IDictionary<string, int> componentByElement)
        {
            var groups = new Dictionary<string, List<GameMepDiagnosticData>>(
                StringComparer.Ordinal);
            foreach (GameMepConnectionData connection in graph.Connections
                .Where(edge => !edge.IsInternal))
            {
                if (connection.ConnectorA < 0 || connection.ConnectorB < 0 ||
                    connection.ConnectorA >= graph.Connectors.Count ||
                    connection.ConnectorB >= graph.Connectors.Count)
                {
                    continue;
                }
                GameMepConnectorData firstConnector = graph.Connectors[connection.ConnectorA];
                GameMepConnectorData secondConnector = graph.Connectors[connection.ConnectorB];
                if (!AreIncompatibleSystems(
                        firstConnector.SystemKey,
                        secondConnector.SystemKey))
                {
                    continue;
                }
                GameMepElementData? element = graph.FindElement(firstConnector.ElementKey);
                if (element == null)
                    continue;
                string firstSystem = string.Compare(
                        firstConnector.SystemKey,
                        secondConnector.SystemKey,
                        StringComparison.Ordinal) <= 0
                    ? firstConnector.SystemKey
                    : secondConnector.SystemKey;
                string secondSystem = string.Equals(
                        firstSystem,
                        firstConnector.SystemKey,
                        StringComparison.Ordinal)
                    ? secondConnector.SystemKey
                    : firstConnector.SystemKey;
                int component = componentByElement.TryGetValue(element.Key, out int value)
                    ? value
                    : -1;
                string groupKey = component + "|" + firstSystem + "|" + secondSystem;
                var detailed = new GameMepDiagnosticData
                {
                    Key = "systems|" + connection.ConnectorA + "|" + connection.ConnectorB,
                    Kind = GameMepDiagnosticKind.IncompatibleSystems,
                    Severity = GameMepDiagnosticSeverity.Information,
                    ElementKey = element.Key,
                    ConnectorIndex = firstConnector.Index,
                    SystemKey = firstConnector.SystemKey,
                    Title = "Changement de système au raccordement",
                    Explanation = "Deux connecteurs physiquement liés appartiennent à des systèmes Revit différents. Cela peut être normal autour d'un équipement.",
                    Position = firstConnector.Position,
                    HasPosition = IsFinite(firstConnector.Position),
                    ShowInSmartMode = false
                };
                graph.Diagnostics.Add(detailed);
                if (!groups.TryGetValue(groupKey, out List<GameMepDiagnosticData> list))
                {
                    list = new List<GameMepDiagnosticData>();
                    groups[groupKey] = list;
                }
                list.Add(detailed);
            }

            foreach (KeyValuePair<string, List<GameMepDiagnosticData>> pair in groups)
            {
                GameMepDiagnosticData first = pair.Value
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .First();
                graph.Diagnostics.Add(new GameMepDiagnosticData
                {
                    Key = "systems-group|" + pair.Key,
                    Kind = GameMepDiagnosticKind.IncompatibleSystems,
                    Severity = GameMepDiagnosticSeverity.Information,
                    ElementKey = first.ElementKey,
                    ConnectorIndex = first.ConnectorIndex,
                    SystemKey = first.SystemKey,
                    Title = pair.Value.Count == 1
                        ? "1 raccordement entre systèmes différents"
                        : pair.Value.Count + " raccordements entre systèmes différents",
                    Explanation = "Vérifie ces transitions si elles ne correspondent pas à un équipement séparant volontairement deux systèmes.",
                    Position = first.Position,
                    HasPosition = first.HasPosition,
                    OccurrenceCount = pair.Value.Count,
                    ShowInSmartMode = true,
                    IsAggregate = true
                });
            }
        }

        private static void AddInvalidSavedSettings(GameMepGraphData graph)
        {
            if (graph.SkippedScenarioEntryCount <= 0)
                return;
            graph.Diagnostics.Add(new GameMepDiagnosticData
            {
                Key = "scenario|invalid-settings",
                Kind = GameMepDiagnosticKind.InvalidSavedSetting,
                Severity = GameMepDiagnosticSeverity.Warning,
                Title = "Ancien réglage devenu invalide",
                Explanation = graph.SkippedScenarioEntryCount +
                    " réglage(s) sauvegardé(s) ne correspondent plus à un élément ou un connecteur compatible de la maquette.",
                OccurrenceCount = graph.SkippedScenarioEntryCount,
                ShowInSmartMode = true
            });
        }

        private static bool IsLikelyLegitimateOpenConnector(
            GameMepGraphData graph,
            GameMepElementData element,
            GameMepConnectorData connector)
        {
            if (element.ConnectorIndices.Count == 1)
                return true;
            return graph.Sources.Any(source =>
                string.Equals(source.ElementKey, element.Key, StringComparison.Ordinal) &&
                (!source.HasExplicitDirection ||
                 source.EntryConnectorIndex == connector.Index ||
                 source.ExitConnectorIndex == connector.Index));
        }

        private static bool IsUnknownPassThrough(
            GameMepGraphData graph,
            GameMepElementData element)
        {
            if (element.IsPipeCurve || element.ConnectorIndices.Count < 2 ||
                graph.FindValve(element.Key) != null ||
                graph.Sources.Any(source =>
                    string.Equals(source.ElementKey, element.Key, StringComparison.Ordinal)))
            {
                return false;
            }
            string searchable = (element.Name + " " + element.TypeName + " " +
                element.Category).ToLowerInvariant();
            string[] knownTokens =
            {
                "coude", "elbow", "té", " tee", "raccord", "fitting",
                "réduction", "reduction", "transition", "union", "manchon",
                "compensateur", "filtre", "filter", "pompe", "pump",
                "échangeur", "echangeur", "exchanger", "bouchon", "cap",
                "collecteur", "collector", "flexible", "flex"
            };
            return !knownTokens.Any(token => searchable.Contains(token));
        }

        private static bool AreIncompatibleSystems(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) ||
                first.IndexOf(UnassignedSystemToken, StringComparison.OrdinalIgnoreCase) >= 0 ||
                second.IndexOf(UnassignedSystemToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            return !string.Equals(first, second, StringComparison.Ordinal);
        }

        private static GameMepElementData ChooseRepresentative(
            IEnumerable<GameMepElementData> elements)
        {
            return elements
                .OrderByDescending(element => element.IsVisible)
                .ThenByDescending(element => element.Paths.Count)
                .ThenBy(element => element.Key, StringComparer.Ordinal)
                .First();
        }

        private static GameMepDiagnosticData CreateElementDiagnostic(
            GameMepGraphData graph,
            GameMepElementData element,
            GameMepDiagnosticKind kind,
            GameMepDiagnosticSeverity severity,
            string title,
            string explanation,
            string key,
            bool detailedOnly = false,
            int occurrenceCount = 1,
            bool aggregate = false)
        {
            bool hasPosition = TryGetElementPosition(graph, element, out Point3D position);
            return new GameMepDiagnosticData
            {
                Key = key,
                Kind = kind,
                Severity = severity,
                ElementKey = element.Key,
                SystemKey = element.SystemKey,
                Title = title,
                Explanation = explanation,
                Position = position,
                HasPosition = hasPosition,
                OccurrenceCount = occurrenceCount,
                ShowInSmartMode = !detailedOnly,
                IsAggregate = aggregate
            };
        }

        private static bool TryGetElementPosition(
            GameMepGraphData graph,
            GameMepElementData element,
            out Point3D position)
        {
            GameMepPathData? path = element.Paths
                .OrderByDescending(item => item.IsVisible)
                .ThenByDescending(item => item.Length)
                .FirstOrDefault();
            if (path != null && path.Points.Count > 0)
            {
                position = path.MidPoint;
                return IsFinite(position);
            }
            IList<Point3D> points = element.ConnectorIndices
                .Where(index => index >= 0 && index < graph.Connectors.Count)
                .Select(index => graph.Connectors[index].Position)
                .Where(IsFinite)
                .ToList();
            if (points.Count == 0)
            {
                position = new Point3D();
                return false;
            }
            position = new Point3D(
                points.Average(point => point.X),
                points.Average(point => point.Y),
                points.Average(point => point.Z));
            return true;
        }

        private static bool IsFinite(Point3D point)
        {
            return !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
                   !double.IsNaN(point.Y) && !double.IsInfinity(point.Y) &&
                   !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);
        }

        private static string ToConfidenceText(GameMepConfidence confidence)
        {
            switch (confidence)
            {
                case GameMepConfidence.High:
                    return "élevée";
                case GameMepConfidence.Medium:
                    return "moyenne";
                default:
                    return "faible";
            }
        }
    }
}

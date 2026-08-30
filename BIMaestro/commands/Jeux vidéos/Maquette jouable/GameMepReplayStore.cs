using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace BIMaestro.VideoGames
{
    /// <summary>
    /// Cas de diagnostic autonome du calcul MEP. Le graphe contient uniquement
    /// la topologie et les polylignes nécessaires au calcul, jamais la géométrie
    /// complète de la maquette Revit.
    /// </summary>
    internal sealed class GameMepReplaySnapshot
    {
        public int SchemaVersion { get; set; } = GameMepReplayStore.SchemaVersion;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string DocumentLabel { get; set; } = string.Empty;
        public GameMepGraphData Graph { get; set; } = new GameMepGraphData();
        public IList<GameMepReplayPathState> CapturedPathStates { get; } =
            new List<GameMepReplayPathState>();
    }

    internal sealed class GameMepReplayPathState
    {
        public string ElementKey { get; set; } = string.Empty;
        public int PathOrdinal { get; set; }
        public int StartConnector { get; set; } = -1;
        public int EndConnector { get; set; } = -1;
        public GameMepFlowState FlowState { get; set; }
        public bool HasCirculation { get; set; }
        public bool FlowForward { get; set; }
        public GameMepDirectionState DirectionState { get; set; }
        public string DirectionReason { get; set; } = string.Empty;
    }

    internal sealed class GameMepReplayDifference
    {
        public string ElementKey { get; set; } = string.Empty;
        public long ElementId { get; set; }
        public string ElementName { get; set; } = string.Empty;
        public int PathOrdinal { get; set; }
        public string CapturedState { get; set; } = string.Empty;
        public string ReplayedState { get; set; } = string.Empty;
    }

    internal sealed class GameMepReplayResult
    {
        public int PathCount { get; set; }
        public int ReversedPathCount { get; set; }
        public int StateChangeCount { get; set; }
        public int CapturedVisibleDiscontinuityCount { get; set; }
        public int ReplayedVisibleDiscontinuityCount { get; set; }
        public IList<string> ReplayedVisibleDiscontinuities { get; } =
            new List<string>();
        public IList<GameMepReplayDifference> Differences { get; } =
            new List<GameMepReplayDifference>();
    }

    internal static class GameMepReplayStore
    {
        public const int SchemaVersion = 1;

        private static readonly JsonSerializerSettings JsonSettings =
            new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                ObjectCreationHandling = ObjectCreationHandling.Auto
            };

        public static GameMepReplaySnapshot Capture(GameMepGraphData graph)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));
            if (!graph.HasData)
                throw new InvalidOperationException("Le graphe MEP est vide.");

            var snapshot = new GameMepReplaySnapshot
            {
                DocumentLabel = graph.DocumentTitle ?? string.Empty,
                Graph = CloneGraph(graph)
            };
            CapturePathStates(graph, snapshot.CapturedPathStates);
            Sanitize(snapshot.Graph);
            PrepareGraph(snapshot.Graph);
            return snapshot;
        }

        public static void Save(GameMepGraphData graph, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Le chemin d'export est vide.", nameof(filePath));

            GameMepReplaySnapshot snapshot = Capture(graph);
            string json = JsonConvert.SerializeObject(snapshot, JsonSettings);
            File.WriteAllText(filePath, json);
        }

        public static GameMepReplaySnapshot Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Cas MEP introuvable.", filePath);

            string json = File.ReadAllText(filePath);
            GameMepReplaySnapshot? snapshot =
                JsonConvert.DeserializeObject<GameMepReplaySnapshot>(json, JsonSettings);
            if (snapshot == null || snapshot.Graph == null)
                throw new InvalidDataException("Le fichier ne contient pas de graphe MEP.");
            if (snapshot.SchemaVersion != SchemaVersion)
            {
                throw new InvalidDataException(
                    "Version de cas MEP non prise en charge : " +
                    snapshot.SchemaVersion + ".");
            }
            if (!snapshot.Graph.HasData)
                throw new InvalidDataException("Le graphe MEP importé est vide.");

            PrepareGraph(snapshot.Graph);
            return snapshot;
        }

        public static GameMepReplayResult Replay(GameMepReplaySnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.Graph == null || !snapshot.Graph.HasData)
                throw new InvalidDataException("Le cas ne contient pas de graphe MEP rejouable.");

            PrepareGraph(snapshot.Graph);
            int capturedDiscontinuities = CountVisibleDiscontinuities(
                snapshot.Graph);
            new GameMepSimulationEngine(snapshot.Graph).Recalculate();

            var result = new GameMepReplayResult
            {
                CapturedVisibleDiscontinuityCount = capturedDiscontinuities,
                ReplayedVisibleDiscontinuityCount = CountVisibleDiscontinuities(
                    snapshot.Graph)
            };
            CollectVisibleDiscontinuities(
                snapshot.Graph,
                result.ReplayedVisibleDiscontinuities);
            var captured = snapshot.CapturedPathStates.ToDictionary(
                PathIdentity,
                state => state,
                StringComparer.Ordinal);

            foreach (GameMepElementData element in snapshot.Graph.Elements)
            {
                for (int ordinal = 0; ordinal < element.Paths.Count; ordinal++)
                {
                    GameMepPathData path = element.Paths[ordinal];
                    result.PathCount++;
                    var current = CreatePathState(element.Key, ordinal, path);
                    if (!captured.TryGetValue(PathIdentity(current), out GameMepReplayPathState before))
                        continue;

                    bool reversed = before.FlowForward != current.FlowForward;
                    bool changed = reversed ||
                        before.FlowState != current.FlowState ||
                        before.HasCirculation != current.HasCirculation ||
                        before.DirectionState != current.DirectionState;
                    if (!changed)
                        continue;

                    if (reversed)
                        result.ReversedPathCount++;
                    result.StateChangeCount++;
                    result.Differences.Add(new GameMepReplayDifference
                    {
                        ElementKey = element.Key,
                        ElementId = element.ElementId,
                        ElementName = element.Name,
                        PathOrdinal = ordinal,
                        CapturedState = Describe(before),
                        ReplayedState = Describe(current)
                    });
                }
            }
            return result;
        }

        private static int CountVisibleDiscontinuities(GameMepGraphData graph)
        {
            var descriptions = new List<string>();
            CollectVisibleDiscontinuities(graph, descriptions);
            return descriptions.Count;
        }

        private static void CollectVisibleDiscontinuities(
            GameMepGraphData graph,
            IList<string> descriptions)
        {
            var externalFlowByConnector = new Dictionary<int, bool>();
            var ambiguousConnectors = new HashSet<int>();
            foreach (GameMepElementData element in graph.Elements)
            {
                foreach (GameMepPathData path in element.Paths.Where(item =>
                    item.FlowState == GameMepFlowState.Supplied &&
                    item.HasCirculation &&
                    item.DirectionState == GameMepDirectionState.Resolved))
                {
                    RecordExternalFlow(
                        path.StartConnector,
                        !path.FlowForward,
                        externalFlowByConnector,
                        ambiguousConnectors);
                    RecordExternalFlow(
                        path.EndConnector,
                        path.FlowForward,
                        externalFlowByConnector,
                        ambiguousConnectors);
                }
            }

            foreach (GameMepConnectionData edge in graph.Connections.Where(item =>
                !item.IsInternal))
            {
                if (ambiguousConnectors.Contains(edge.ConnectorA) ||
                    ambiguousConnectors.Contains(edge.ConnectorB) ||
                    !externalFlowByConnector.TryGetValue(
                        edge.ConnectorA, out bool first) ||
                    !externalFlowByConnector.TryGetValue(
                        edge.ConnectorB, out bool second))
                {
                    continue;
                }
                if (first == second)
                {
                    GameMepElementData? firstOwner = graph.FindElement(
                        graph.Connectors[edge.ConnectorA].ElementKey);
                    GameMepElementData? secondOwner = graph.FindElement(
                        graph.Connectors[edge.ConnectorB].ElementKey);
                    descriptions.Add(
                        (firstOwner?.Name ?? "?") + " [" +
                        (firstOwner?.ElementId ?? 0) + "] ↔ " +
                        (secondOwner?.Name ?? "?") + " [" +
                        (secondOwner?.ElementId ?? 0) + "]");
                }
            }
        }

        private static void RecordExternalFlow(
            int connector,
            bool flowsOutOfElement,
            IDictionary<int, bool> externalFlowByConnector,
            ISet<int> ambiguousConnectors)
        {
            if (connector < 0 || ambiguousConnectors.Contains(connector))
                return;
            if (externalFlowByConnector.TryGetValue(
                    connector, out bool existing) &&
                existing != flowsOutOfElement)
            {
                externalFlowByConnector.Remove(connector);
                ambiguousConnectors.Add(connector);
                return;
            }
            externalFlowByConnector[connector] = flowsOutOfElement;
        }

        public static string CreateSuggestedFileName(GameMepGraphData graph)
        {
            string label = graph?.DocumentTitle ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label))
                label = "maquette";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                label = label.Replace(invalid, '-');
            label = label.Trim();
            if (label.Length > 48)
                label = label.Substring(0, 48).Trim();
            return "BIMaestro-MEP-" + label + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bimaestro-mep.json";
        }

        private static GameMepGraphData CloneGraph(GameMepGraphData graph)
        {
            string json = JsonConvert.SerializeObject(graph, JsonSettings);
            GameMepGraphData? clone =
                JsonConvert.DeserializeObject<GameMepGraphData>(json, JsonSettings);
            if (clone == null)
                throw new InvalidOperationException("Impossible de copier le graphe MEP.");
            return clone;
        }

        private static void CapturePathStates(
            GameMepGraphData graph,
            IList<GameMepReplayPathState> destination)
        {
            foreach (GameMepElementData element in graph.Elements)
            {
                for (int ordinal = 0; ordinal < element.Paths.Count; ordinal++)
                {
                    destination.Add(CreatePathState(
                        element.Key,
                        ordinal,
                        element.Paths[ordinal]));
                }
            }
        }

        private static GameMepReplayPathState CreatePathState(
            string elementKey,
            int ordinal,
            GameMepPathData path)
        {
            return new GameMepReplayPathState
            {
                ElementKey = elementKey,
                PathOrdinal = ordinal,
                StartConnector = path.StartConnector,
                EndConnector = path.EndConnector,
                FlowState = path.FlowState,
                HasCirculation = path.HasCirculation,
                FlowForward = path.FlowForward,
                DirectionState = path.DirectionState,
                DirectionReason = path.DirectionReason ?? string.Empty
            };
        }

        private static string PathIdentity(GameMepReplayPathState state)
        {
            return state.ElementKey + "|" + state.PathOrdinal + "|" +
                state.StartConnector + "|" + state.EndConnector;
        }

        private static string Describe(GameMepReplayPathState state)
        {
            return (state.FlowForward ? "avant" : "arrière") + ", " +
                state.FlowState + ", " + state.DirectionState + ", " +
                (state.HasCirculation ? "circulation" : "stagnant");
        }

        private static void Sanitize(GameMepGraphData graph)
        {
            graph.ScenarioModelKey = string.Empty;
            graph.ScenarioCanPersist = false;
            graph.ScenarioPersistenceError = string.Empty;
            foreach (GameMepElementData element in graph.Elements)
                element.PersistentId = string.Empty;
            foreach (GameMepConnectorData connector in graph.Connectors)
                connector.PersistentKey = string.Empty;
        }

        private static void PrepareGraph(GameMepGraphData graph)
        {
            foreach (GameMepElementData element in graph.Elements)
            {
                foreach (GameMepPathData path in element.Paths)
                    path.FinalizePath();
            }
            graph.RebuildIndexes();
        }
    }
}

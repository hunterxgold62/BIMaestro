using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.VideoGames
{
    internal enum GameMepTraceMode
    {
        Upstream,
        Downstream,
        FullBranch
    }

    internal sealed class GameMepTraceBranchData
    {
        public string ElementKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public ISet<string> ElementKeys { get; } =
            new HashSet<string>(StringComparer.Ordinal);
    }

    internal sealed class GameMepNetworkTraceResult
    {
        public GameMepTraceMode Mode { get; set; }
        public string StartElementKey { get; set; } = string.Empty;
        public string SelectedBranchElementKey { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public ISet<string> ElementKeys { get; } =
            new HashSet<string>(StringComparer.Ordinal);
        public IList<GameMepTraceBranchData> Branches { get; } =
            new List<GameMepTraceBranchData>();
    }

    /// <summary>
    /// Calcule les trajets de consultation hors de Revit. Le graphe orienté
    /// reprend les sens déjà expliqués par le moteur MEP ; les raccordements
    /// externes restent traversables dans les deux sens, tandis que les
    /// chemins internes portent réellement le sens du fluide.
    /// </summary>
    internal static class GameMepNetworkTracer
    {
        private sealed class Arc
        {
            public int Next { get; set; }
            public int EdgeIndex { get; set; }
        }

        private sealed class TraversalResult
        {
            public ISet<string> ElementKeys { get; } =
                new HashSet<string>(StringComparer.Ordinal);
            public IDictionary<string, string> ParentByElement { get; } =
                new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public static GameMepNetworkTraceResult Build(
            GameMepGraphData graph,
            string startElementKey,
            GameMepTraceMode mode,
            string branchElementKey = "")
        {
            var result = new GameMepNetworkTraceResult
            {
                Mode = mode,
                StartElementKey = startElementKey ?? string.Empty,
                SelectedBranchElementKey = branchElementKey ?? string.Empty
            };
            if (graph == null || string.IsNullOrWhiteSpace(startElementKey))
            {
                result.Summary = "Élément de départ introuvable";
                return result;
            }

            GameMepElementData? start = graph.FindElement(startElementKey);
            if (start == null || !IsSystemVisible(graph, start))
            {
                result.Summary = start == null
                    ? "Élément de départ introuvable"
                    : "Le système de cet élément est masqué";
                return result;
            }

            List<Arc>[] directed = BuildDirectedAdjacency(graph);
            ISet<string> upstream = mode == GameMepTraceMode.Downstream
                ? new HashSet<string>(StringComparer.Ordinal)
                : TracePrimaryUpstream(graph, directed, start);
            TraversalResult downstream = mode == GameMepTraceMode.Upstream
                ? new TraversalResult()
                : TraceDownstream(graph, directed, start);

            BuildBranches(graph, directed, startElementKey, downstream, result);
            if (!string.IsNullOrWhiteSpace(branchElementKey))
            {
                GameMepTraceBranchData? selected = result.Branches.FirstOrDefault(
                    item => string.Equals(
                        item.ElementKey,
                        branchElementKey,
                        StringComparison.Ordinal));
                if (selected != null)
                {
                    downstream.ElementKeys.Clear();
                    foreach (string key in selected.ElementKeys)
                        downstream.ElementKeys.Add(key);
                    result.SelectedBranchElementKey = selected.ElementKey;
                }
                else
                {
                    result.SelectedBranchElementKey = string.Empty;
                }
            }

            foreach (string key in upstream)
                result.ElementKeys.Add(key);
            foreach (string key in downstream.ElementKeys)
                result.ElementKeys.Add(key);
            result.ElementKeys.Add(startElementKey);

            string modeLabel = mode == GameMepTraceMode.Upstream
                ? "vers la source"
                : mode == GameMepTraceMode.Downstream
                    ? "vers l’aval"
                    : "branche complète";
            result.Summary = "Suivi " + modeLabel + " : " +
                result.ElementKeys.Count + " élément(s)" +
                (result.Branches.Count > 1
                    ? " • " + result.Branches.Count + " branches accessibles"
                    : string.Empty);
            return result;
        }

        private static ISet<string> TracePrimaryUpstream(
            GameMepGraphData graph,
            IList<Arc>[] adjacency,
            GameMepElementData start)
        {
            var result = new HashSet<string>(StringComparer.Ordinal)
            {
                start.Key
            };
            GameMepPathData? path = start.Paths
                .Where(candidate => candidate.StartConnector >= 0 &&
                    candidate.EndConnector >= 0)
                .OrderBy(candidate => PathStableKey(candidate), StringComparer.Ordinal)
                .FirstOrDefault();
            if (path == null)
                return result;

            string sourceKey = path.DirectionExplanation.PrimarySourceElementKey;
            if (string.IsNullOrWhiteSpace(sourceKey))
            {
                foreach (string key in path.DirectionExplanation.UpstreamElementKeys)
                    result.Add(key);
                return result;
            }

            GameMepElementData? sourceElement = graph.FindElement(sourceKey);
            if (sourceElement == null || !IsSystemVisible(graph, sourceElement))
                return result;
            GameMepSourceData? source = graph.Sources
                .Where(item => item.IsActive &&
                    item.BoundaryKind == GameMepBoundaryKind.Inlet &&
                    string.Equals(item.ElementKey, sourceKey, StringComparison.Ordinal))
                .OrderBy(item => SourceStableKey(item), StringComparer.Ordinal)
                .FirstOrDefault();

            IEnumerable<int> sourceSeeds = source != null &&
                source.HasExplicitDirection &&
                sourceElement.ConnectorIndices.Contains(source.EntryConnectorIndex)
                    ? new[] { source.EntryConnectorIndex }
                    : sourceElement.ConnectorIndices;
            var externalStops = new HashSet<int>();
            if (source != null && source.HasExplicitDirection)
                externalStops.Add(source.EntryConnectorIndex);

            int target = path.FlowForward
                ? path.StartConnector
                : path.EndConnector;
            if (!IsValidConnector(graph, target))
            {
                foreach (string key in path.DirectionExplanation.UpstreamElementKeys)
                    result.Add(key);
                return result;
            }
            int[] predecessor = Enumerable.Repeat(
                -1, graph.Connectors.Count).ToArray();
            bool[] visited = new bool[graph.Connectors.Count];
            var queue = new Queue<int>();
            foreach (int seed in sourceSeeds
                .Where(index => IsValidConnector(graph, index))
                .OrderBy(index => ConnectorStableKey(graph, index),
                    StringComparer.Ordinal))
            {
                if (visited[seed])
                    continue;
                visited[seed] = true;
                queue.Enqueue(seed);
            }

            while (queue.Count > 0 && !visited[target])
            {
                int current = queue.Dequeue();
                foreach (Arc arc in adjacency[current])
                {
                    GameMepConnectionData edge = graph.Connections[arc.EdgeIndex];
                    if (externalStops.Contains(current) && !edge.IsInternal)
                        continue;
                    if (!CanVisitConnector(graph, arc.Next) || visited[arc.Next])
                        continue;
                    visited[arc.Next] = true;
                    predecessor[arc.Next] = current;
                    queue.Enqueue(arc.Next);
                }
            }

            if (!IsValidConnector(graph, target) || !visited[target])
            {
                foreach (string key in path.DirectionExplanation.UpstreamElementKeys)
                    result.Add(key);
                result.Add(sourceKey);
                return result;
            }

            var chain = new List<int>();
            var guard = new HashSet<int>();
            for (int current = target;
                current >= 0 && guard.Add(current);
                current = predecessor[current])
            {
                chain.Add(current);
            }
            chain.Reverse();
            foreach (int connector in chain)
            {
                string key = graph.Connectors[connector].ElementKey;
                if (!string.IsNullOrWhiteSpace(key))
                    result.Add(key);
            }
            result.Add(sourceKey);
            return result;
        }

        private static TraversalResult TraceDownstream(
            GameMepGraphData graph,
            IList<Arc>[] adjacency,
            GameMepElementData start,
            ISet<string>? blockedElementKeys = null)
        {
            var result = new TraversalResult();
            result.ElementKeys.Add(start.Key);
            IList<int> downstreamConnectors = start.Paths
                .Where(path => path.HasCirculation &&
                    path.StartConnector >= 0 && path.EndConnector >= 0)
                .Select(path => path.FlowForward
                    ? path.EndConnector
                    : path.StartConnector)
                .Distinct()
                .ToList();
            IEnumerable<int> seeds = downstreamConnectors.Count > 0
                ? downstreamConnectors
                : start.ConnectorIndices;

            bool[] visited = new bool[graph.Connectors.Count];
            var queue = new Queue<int>();
            foreach (int seed in seeds
                .Where(index => IsValidConnector(graph, index))
                .OrderBy(index => ConnectorStableKey(graph, index),
                    StringComparer.Ordinal))
            {
                if (visited[seed])
                    continue;
                visited[seed] = true;
                queue.Enqueue(seed);
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                string currentElement = graph.Connectors[current].ElementKey;
                if (!string.IsNullOrWhiteSpace(currentElement))
                    result.ElementKeys.Add(currentElement);
                foreach (Arc arc in adjacency[current])
                {
                    string nextOwner = IsValidConnector(graph, arc.Next)
                        ? graph.Connectors[arc.Next].ElementKey
                        : string.Empty;
                    if ((blockedElementKeys != null &&
                            blockedElementKeys.Contains(nextOwner)) ||
                        !CanVisitConnector(graph, arc.Next) ||
                        visited[arc.Next])
                        continue;
                    visited[arc.Next] = true;
                    string nextElement = graph.Connectors[arc.Next].ElementKey;
                    if (!string.IsNullOrWhiteSpace(nextElement))
                    {
                        result.ElementKeys.Add(nextElement);
                        if (!string.Equals(
                                currentElement,
                                nextElement,
                                StringComparison.Ordinal) &&
                            !result.ParentByElement.ContainsKey(nextElement) &&
                            !string.Equals(nextElement, start.Key,
                                StringComparison.Ordinal))
                        {
                            result.ParentByElement[nextElement] = currentElement;
                        }
                    }
                    queue.Enqueue(arc.Next);
                }
            }
            return result;
        }

        private static void BuildBranches(
            GameMepGraphData graph,
            IList<Arc>[] adjacency,
            string startElementKey,
            TraversalResult traversal,
            GameMepNetworkTraceResult result)
        {
            if (traversal.ElementKeys.Count <= 1)
                return;
            var children = new Dictionary<string, List<string>>(
                StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> relation in
                traversal.ParentByElement)
            {
                if (string.IsNullOrWhiteSpace(relation.Value))
                    continue;
                if (!children.TryGetValue(relation.Value, out List<string> list))
                {
                    list = new List<string>();
                    children.Add(relation.Value, list);
                }
                list.Add(relation.Key);
            }
            foreach (List<string> list in children.Values)
                list.Sort(StringComparer.Ordinal);

            var common = new HashSet<string>(StringComparer.Ordinal)
            {
                startElementKey
            };
            string current = startElementKey;
            while (children.TryGetValue(current, out List<string> next) &&
                next.Count == 1)
            {
                current = next[0];
                common.Add(current);
            }
            if (!children.TryGetValue(current, out List<string> branches) ||
                branches.Count < 2)
            {
                return;
            }

            foreach (string branchRoot in branches)
            {
                var branch = new GameMepTraceBranchData
                {
                    ElementKey = branchRoot,
                    Name = DisplayName(graph, branchRoot)
                };
                foreach (string key in common)
                    branch.ElementKeys.Add(key);
                GameMepElementData? branchElement = graph.FindElement(branchRoot);
                if (branchElement != null)
                {
                    var blockedBranches = new HashSet<string>(
                        branches.Where(key => !string.Equals(
                            key,
                            branchRoot,
                            StringComparison.Ordinal)),
                        StringComparer.Ordinal);
                    TraversalResult reachable = TraceDownstream(
                        graph,
                        adjacency,
                        branchElement,
                        blockedBranches);
                    foreach (string key in reachable.ElementKeys)
                        branch.ElementKeys.Add(key);
                }
                else
                    branch.ElementKeys.Add(branchRoot);
                result.Branches.Add(branch);
            }
        }

        private static List<Arc>[] BuildDirectedAdjacency(GameMepGraphData graph)
        {
            var result = new List<Arc>[graph.Connectors.Count];
            for (int index = 0; index < result.Length; index++)
                result[index] = new List<Arc>();

            for (int edgeIndex = 0;
                edgeIndex < graph.Connections.Count;
                edgeIndex++)
            {
                GameMepConnectionData edge = graph.Connections[edgeIndex];
                if (!IsValidConnector(graph, edge.ConnectorA) ||
                    !IsValidConnector(graph, edge.ConnectorB) ||
                    !GameMepSystemTraversalPolicy.CanTraverse(graph, edge))
                {
                    continue;
                }
                if (!edge.IsInternal)
                {
                    AddArc(result, edge.ConnectorA, edge.ConnectorB, edgeIndex);
                    AddArc(result, edge.ConnectorB, edge.ConnectorA, edgeIndex);
                    continue;
                }

                GameMepValveData? control = graph.FindValve(edge.ElementKey);
                if (control != null && control.IsEnabledAsValve)
                {
                    if (control.Kind == GameMepFlowControlKind.IsolationValve &&
                        control.IsClosed)
                    {
                        continue;
                    }
                    if (control.Kind == GameMepFlowControlKind.CheckValve &&
                        control.HasExplicitDirection)
                    {
                        if (MatchesEdge(edge,
                                control.EntryConnectorIndex,
                                control.ExitConnectorIndex))
                        {
                            AddArc(result,
                                control.EntryConnectorIndex,
                                control.ExitConnectorIndex,
                                edgeIndex);
                        }
                        continue;
                    }
                }

                GameMepElementData? element = graph.FindElement(edge.ElementKey);
                IList<GameMepPathData> matches = element?.Paths
                    .Where(path => MatchesEdge(
                        edge, path.StartConnector, path.EndConnector))
                    .OrderBy(path => PathStableKey(path), StringComparer.Ordinal)
                    .ToList() ?? new List<GameMepPathData>();
                bool oriented = false;
                foreach (GameMepPathData path in matches)
                {
                    if (!path.HasCirculation ||
                        path.DirectionState != GameMepDirectionState.Resolved)
                        continue;
                    AddArc(result,
                        path.FlowForward ? path.StartConnector : path.EndConnector,
                        path.FlowForward ? path.EndConnector : path.StartConnector,
                        edgeIndex);
                    oriented = true;
                }
                bool explicitlyStagnant = matches.Count > 0 &&
                    matches.All(path => !path.HasCirculation);
                if (!oriented && !explicitlyStagnant)
                {
                    AddArc(result, edge.ConnectorA, edge.ConnectorB, edgeIndex);
                    AddArc(result, edge.ConnectorB, edge.ConnectorA, edgeIndex);
                }
            }

            for (int connector = 0; connector < result.Length; connector++)
            {
                result[connector] = result[connector]
                    .OrderBy(arc => ConnectorStableKey(graph, arc.Next),
                        StringComparer.Ordinal)
                    .ThenBy(arc => arc.EdgeIndex)
                    .ToList();
            }
            return result;
        }

        private static void AddArc(
            IList<Arc>[] adjacency,
            int start,
            int end,
            int edgeIndex)
        {
            if (start < 0 || start >= adjacency.Length ||
                end < 0 || end >= adjacency.Length)
            {
                return;
            }
            adjacency[start].Add(new Arc
            {
                Next = end,
                EdgeIndex = edgeIndex
            });
        }

        private static bool CanVisitConnector(
            GameMepGraphData graph,
            int connectorIndex)
        {
            if (!IsValidConnector(graph, connectorIndex))
                return false;
            GameMepElementData? element = graph.FindElement(
                graph.Connectors[connectorIndex].ElementKey);
            return element == null || IsSystemVisible(graph, element);
        }

        private static bool IsSystemVisible(
            GameMepGraphData graph,
            GameMepElementData element)
        {
            GameMepSystemData? system = graph.FindSystem(element.SystemKey);
            return system == null || system.IsVisible;
        }

        private static bool IsValidConnector(
            GameMepGraphData graph,
            int index)
        {
            return index >= 0 && index < graph.Connectors.Count;
        }

        private static bool MatchesEdge(
            GameMepConnectionData edge,
            int first,
            int second)
        {
            return (edge.ConnectorA == first && edge.ConnectorB == second) ||
                (edge.ConnectorA == second && edge.ConnectorB == first);
        }

        private static string DisplayName(
            GameMepGraphData graph,
            string key)
        {
            GameMepElementData? element = graph.FindElement(key);
            return element == null || string.IsNullOrWhiteSpace(element.Name)
                ? key
                : element.Name;
        }

        private static string ConnectorStableKey(
            GameMepGraphData graph,
            int index)
        {
            GameMepConnectorData connector = graph.Connectors[index];
            if (!string.IsNullOrWhiteSpace(connector.PersistentKey))
                return connector.PersistentKey;
            if (!string.IsNullOrWhiteSpace(connector.Key))
                return connector.Key;
            return connector.ElementKey + "|" + index.ToString("D8");
        }

        private static string PathStableKey(GameMepPathData path)
        {
            return path.ElementKey + "|" +
                Math.Min(path.StartConnector, path.EndConnector).ToString("D8") +
                "|" + Math.Max(path.StartConnector, path.EndConnector).ToString("D8");
        }

        private static string SourceStableKey(GameMepSourceData source)
        {
            return source.ElementKey + "|" + source.EntryConnectorIndex.ToString("D8") +
                "|" + source.ExitConnectorIndex.ToString("D8");
        }
    }
}

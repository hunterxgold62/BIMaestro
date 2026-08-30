using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.VideoGames
{
    /// <summary>
    /// Construit une provenance lisible du sens sans aucun appel à Revit.
    /// Le chemin choisi est le plus court puis le plus petit selon les clés
    /// persistantes : il reste donc stable si l'ordre des objets change.
    /// </summary>
    internal static class GameMepDirectionExplanationBuilder
    {
        private sealed class Traversal
        {
            public GameMepSourceData Boundary { get; set; } = null!;
            public int[] Distance { get; set; } = Array.Empty<int>();
            public int[] Predecessor { get; set; } = Array.Empty<int>();
            public int[] PredecessorEdge { get; set; } = Array.Empty<int>();
            public bool[] HasAlternativePath { get; set; } = Array.Empty<bool>();
        }

        public static void Refresh(GameMepGraphData graph)
        {
            if (graph == null)
                return;

            List<int>[] adjacency = BuildAdjacency(graph);
            List<Traversal> inlets = graph.Sources
                .Where(item => item.IsActive &&
                    item.BoundaryKind == GameMepBoundaryKind.Inlet &&
                    GameMepBoundaryPolicy.IsUsable(
                        graph.FindElement(item.ElementKey), item))
                .GroupBy(item => item.ElementKey, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(item => item.HasExplicitDirection)
                    .ThenByDescending(item => item.WasManuallyOverridden)
                    .First())
                .OrderBy(item => SourceStableKey(graph, item), StringComparer.Ordinal)
                .Select(item => Traverse(graph, adjacency, item))
                .ToList();
            List<Traversal> outlets = graph.Sources
                .Where(item => item.IsActive &&
                    item.BoundaryKind == GameMepBoundaryKind.Outlet &&
                    GameMepBoundaryPolicy.IsUsable(
                        graph.FindElement(item.ElementKey), item))
                .GroupBy(item => item.ElementKey, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(item => item.HasExplicitDirection)
                    .ThenByDescending(item => item.WasManuallyOverridden)
                    .First())
                .OrderBy(item => SourceStableKey(graph, item), StringComparer.Ordinal)
                .Select(item => Traverse(graph, adjacency, item))
                .ToList();

            foreach (GameMepElementData element in graph.Elements
                .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                foreach (GameMepPathData path in element.Paths)
                    path.DirectionExplanation = BuildExplanation(
                        graph, adjacency, element, path, inlets, outlets);
            }
        }

        private static GameMepDirectionExplanationData BuildExplanation(
            GameMepGraphData graph,
            IList<int>[] adjacency,
            GameMepElementData element,
            GameMepPathData path,
            IList<Traversal> inlets,
            IList<Traversal> outlets)
        {
            var result = new GameMepDirectionExplanationData
            {
                Rule = string.IsNullOrWhiteSpace(path.DirectionReason)
                    ? "Aucune règle concluante"
                    : path.DirectionReason,
                Reliability = ReliabilityFor(graph, path),
                IsManual = HasManualRule(graph, path)
            };

            int upstream = path.FlowForward
                ? path.StartConnector
                : path.EndConnector;
            int downstream = path.FlowForward
                ? path.EndConnector
                : path.StartConnector;
            List<Traversal> candidates = inlets
                .Where(item => IsReached(item, upstream))
                .OrderBy(item => item.Distance[upstream])
                .ThenBy(item => SourceStableKey(graph, item.Boundary),
                    StringComparer.Ordinal)
                .ToList();

            if (candidates.Count > 0)
            {
                Traversal primary = candidates[0];
                result.PrimarySourceElementKey = primary.Boundary.ElementKey;
                result.PrimarySourceName = SourceDisplayName(graph, primary.Boundary);
                foreach (Traversal alternative in candidates.Skip(1))
                {
                    string name = SourceDisplayName(graph, alternative.Boundary);
                    if (!result.AlternativeSourceNames.Contains(name))
                        result.AlternativeSourceNames.Add(name);
                }
                result.HasAlternativeRoute =
                    primary.HasAlternativePath[upstream] || candidates.Count > 1;
                AppendUpstreamPath(
                    graph, primary, upstream, result, maximumElementCount: 24);
                AppendTraversalControls(graph, primary, upstream, result);
            }

            Traversal? influencingReturn = outlets
                .Where(item => IsReached(item, downstream))
                .OrderBy(item => item.Distance[downstream])
                .ThenBy(item => SourceStableKey(graph, item.Boundary),
                    StringComparer.Ordinal)
                .FirstOrDefault();
            if (influencingReturn != null)
            {
                result.InfluencingReturnName =
                    SourceDisplayName(graph, influencingReturn.Boundary);
                result.HasAlternativeRoute |=
                    influencingReturn.HasAlternativePath[downstream];
            }

            AppendElementControl(graph, element, result);
            if (path.FlowState != GameMepFlowState.Supplied ||
                !path.HasCirculation)
                AppendBlockingNeighbors(graph, adjacency, element, result);
            return result;
        }

        private static GameMepDirectionReliability ReliabilityFor(
            GameMepGraphData graph,
            GameMepPathData path)
        {
            if (HasManualRule(graph, path))
                return GameMepDirectionReliability.Manual;
            if (path.DirectionState != GameMepDirectionState.Resolved)
                return GameMepDirectionReliability.Ambiguous;
            return GameMepDirectionReliability.Inferred;
        }

        private static bool HasManualRule(
            GameMepGraphData graph,
            GameMepPathData path)
        {
            if (graph.DirectionConstraints.Any(item =>
                item.IsActive && item.HasExplicitDirection &&
                string.Equals(item.ElementKey, path.ElementKey,
                    StringComparison.Ordinal)))
            {
                return true;
            }
            if (graph.Sources.Any(item =>
                item.IsActive && item.HasExplicitDirection &&
                (item.IsUserCreated || item.WasManuallyOverridden) &&
                GameMepBoundaryPolicy.IsUsable(
                    graph.FindElement(item.ElementKey), item) &&
                string.Equals(item.ElementKey, path.ElementKey,
                    StringComparison.Ordinal)))
            {
                return true;
            }
            GameMepValveData? valve = graph.FindValve(path.ElementKey);
            return valve != null && valve.WasManuallyOverridden &&
                valve.HasExplicitDirection;
        }

        private static Traversal Traverse(
            GameMepGraphData graph,
            IList<int>[] adjacency,
            GameMepSourceData boundary)
        {
            int count = graph.Connectors.Count;
            var traversal = new Traversal
            {
                Boundary = boundary,
                Distance = Enumerable.Repeat(-1, count).ToArray(),
                Predecessor = Enumerable.Repeat(-1, count).ToArray(),
                PredecessorEdge = Enumerable.Repeat(-1, count).ToArray(),
                HasAlternativePath = new bool[count]
            };
            GameMepElementData? sourceElement = graph.FindElement(boundary.ElementKey);
            if (sourceElement == null)
                return traversal;

            int preferred = boundary.BoundaryKind == GameMepBoundaryKind.Inlet
                ? boundary.EntryConnectorIndex
                : boundary.ExitConnectorIndex;
            IEnumerable<int> seeds = boundary.HasExplicitDirection &&
                sourceElement.ConnectorIndices.Contains(preferred)
                    ? new[] { preferred }
                    : sourceElement.ConnectorIndices;
            var externalStops = new HashSet<int>();
            if (boundary.HasExplicitDirection &&
                sourceElement.ConnectorIndices.Contains(preferred))
            {
                externalStops.Add(preferred);
            }

            var queue = new Queue<int>();
            foreach (int seed in seeds
                .Where(index => index >= 0 && index < count)
                .OrderBy(index => ConnectorStableKey(graph, index),
                    StringComparer.Ordinal))
            {
                if (traversal.Distance[seed] >= 0)
                    continue;
                traversal.Distance[seed] = 0;
                queue.Enqueue(seed);
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int edgeIndex in adjacency[current])
                {
                    GameMepConnectionData edge = graph.Connections[edgeIndex];
                    int next = edge.ConnectorA == current
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (!GameMepSystemTraversalPolicy.CanTraverse(graph, edge) ||
                        (externalStops.Contains(current) && !edge.IsInternal) ||
                        IsBlocked(graph, edge))
                    {
                        continue;
                    }
                    int candidateDistance = traversal.Distance[current] + 1;
                    if (traversal.Distance[next] < 0)
                    {
                        traversal.Distance[next] = candidateDistance;
                        traversal.Predecessor[next] = current;
                        traversal.PredecessorEdge[next] = edgeIndex;
                        traversal.HasAlternativePath[next] =
                            traversal.HasAlternativePath[current];
                        queue.Enqueue(next);
                    }
                    else if (traversal.Distance[next] == candidateDistance &&
                        traversal.Predecessor[next] != current)
                    {
                        traversal.HasAlternativePath[next] = true;
                        if (ComparePredecessor(graph, current,
                                traversal.Predecessor[next]) < 0)
                        {
                            traversal.Predecessor[next] = current;
                            traversal.PredecessorEdge[next] = edgeIndex;
                        }
                    }
                }
            }
            return traversal;
        }

        private static List<int>[] BuildAdjacency(GameMepGraphData graph)
        {
            var result = new List<int>[graph.Connectors.Count];
            for (int index = 0; index < result.Length; index++)
                result[index] = new List<int>();
            for (int edgeIndex = 0; edgeIndex < graph.Connections.Count; edgeIndex++)
            {
                GameMepConnectionData edge = graph.Connections[edgeIndex];
                if (edge.ConnectorA < 0 || edge.ConnectorB < 0 ||
                    edge.ConnectorA >= result.Length ||
                    edge.ConnectorB >= result.Length)
                {
                    continue;
                }
                result[edge.ConnectorA].Add(edgeIndex);
                result[edge.ConnectorB].Add(edgeIndex);
            }
            for (int connector = 0; connector < result.Length; connector++)
            {
                int owner = connector;
                result[connector].Sort((first, second) =>
                    string.CompareOrdinal(
                        EdgeStableKey(graph, graph.Connections[first], owner),
                        EdgeStableKey(graph, graph.Connections[second], owner)));
            }
            return result;
        }

        private static bool IsBlocked(
            GameMepGraphData graph,
            GameMepConnectionData edge)
        {
            if (!edge.IsInternal || !edge.IsValveGateCandidate)
                return false;
            GameMepValveData? valve = graph.FindValve(edge.ElementKey);
            if (valve == null || !valve.IsEnabledAsValve)
                return false;
            return valve.IsClosed;
        }

        private static void AppendUpstreamPath(
            GameMepGraphData graph,
            Traversal traversal,
            int target,
            GameMepDirectionExplanationData result,
            int maximumElementCount)
        {
            var connectorPath = new List<int>();
            var visited = new HashSet<int>();
            int current = target;
            while (current >= 0 && current < traversal.Predecessor.Length &&
                visited.Add(current))
            {
                connectorPath.Add(current);
                current = traversal.Predecessor[current];
            }
            connectorPath.Reverse();
            string previousElement = string.Empty;
            foreach (int connectorIndex in connectorPath)
            {
                string elementKey = graph.Connectors[connectorIndex].ElementKey;
                if (string.IsNullOrWhiteSpace(elementKey) ||
                    string.Equals(elementKey, previousElement, StringComparison.Ordinal))
                {
                    continue;
                }
                previousElement = elementKey;
                GameMepElementData? element = graph.FindElement(elementKey);
                result.UpstreamElementKeys.Add(elementKey);
                result.UpstreamElementNames.Add(element == null ||
                    string.IsNullOrWhiteSpace(element.Name)
                        ? elementKey
                        : element.Name);
                if (result.UpstreamElementKeys.Count >= maximumElementCount)
                    break;
            }
        }

        private static void AppendTraversalControls(
            GameMepGraphData graph,
            Traversal traversal,
            int target,
            GameMepDirectionExplanationData result)
        {
            var visited = new HashSet<int>();
            int current = target;
            while (current >= 0 && current < traversal.Predecessor.Length &&
                visited.Add(current))
            {
                int edgeIndex = traversal.PredecessorEdge[current];
                if (edgeIndex >= 0 && edgeIndex < graph.Connections.Count)
                {
                    GameMepConnectionData edge = graph.Connections[edgeIndex];
                    if (edge.IsValveGateCandidate)
                    {
                        GameMepValveData? valve = graph.FindValve(edge.ElementKey);
                        if (valve != null && valve.IsEnabledAsValve)
                            AddControlLabel(graph, valve, result);
                    }
                }
                current = traversal.Predecessor[current];
            }
        }

        private static void AppendElementControl(
            GameMepGraphData graph,
            GameMepElementData element,
            GameMepDirectionExplanationData result)
        {
            GameMepValveData? valve = graph.FindValve(element.Key);
            if (valve != null && valve.IsEnabledAsValve)
                AddControlLabel(graph, valve, result);
        }

        private static void AppendBlockingNeighbors(
            GameMepGraphData graph,
            IList<int>[] adjacency,
            GameMepElementData element,
            GameMepDirectionExplanationData result)
        {
            foreach (int connector in element.ConnectorIndices
                .Where(index => index >= 0 && index < adjacency.Length))
            {
                foreach (int edgeIndex in adjacency[connector])
                {
                    GameMepConnectionData edge = graph.Connections[edgeIndex];
                    AddClosedControlFromEdge(graph, edge, result);
                    int neighbor = edge.ConnectorA == connector
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (neighbor < 0 || neighbor >= adjacency.Length)
                        continue;
                    foreach (int neighborEdgeIndex in adjacency[neighbor])
                    {
                        AddClosedControlFromEdge(
                            graph,
                            graph.Connections[neighborEdgeIndex],
                            result);
                    }
                }
            }
        }

        private static void AddClosedControlFromEdge(
            GameMepGraphData graph,
            GameMepConnectionData edge,
            GameMepDirectionExplanationData result)
        {
            if (!edge.IsValveGateCandidate)
                return;
            GameMepValveData? valve = graph.FindValve(edge.ElementKey);
            if (valve != null && valve.IsEnabledAsValve && valve.IsClosed)
                AddControlLabel(graph, valve, result);
        }

        private static void AddControlLabel(
            GameMepGraphData graph,
            GameMepValveData valve,
            GameMepDirectionExplanationData result)
        {
            GameMepElementData? element = graph.FindElement(valve.ElementKey);
            string name = element == null || string.IsNullOrWhiteSpace(element.Name)
                ? valve.ElementKey
                : element.Name;
            string label = "Vanne " +
                (valve.IsClosed ? "fermée : " : "ouverte : ") + name;
            if (!result.LimitingControls.Contains(label))
                result.LimitingControls.Add(label);
        }

        private static bool IsReached(Traversal traversal, int connector)
        {
            return connector >= 0 && connector < traversal.Distance.Length &&
                traversal.Distance[connector] >= 0;
        }

        private static int ComparePredecessor(
            GameMepGraphData graph,
            int first,
            int second)
        {
            if (second < 0)
                return -1;
            return string.CompareOrdinal(
                ConnectorStableKey(graph, first),
                ConnectorStableKey(graph, second));
        }

        private static string SourceDisplayName(
            GameMepGraphData graph,
            GameMepSourceData source)
        {
            GameMepElementData? element = graph.FindElement(source.ElementKey);
            if (element != null)
            {
                string currentName = string.IsNullOrWhiteSpace(element.Name)
                    ? source.ElementKey
                    : element.Name;
                return element.ElementId > 0
                    ? currentName + " (ID Revit " + element.ElementId + ")"
                    : currentName;
            }
            return string.IsNullOrWhiteSpace(source.Name)
                ? source.ElementKey
                : source.Name;
        }

        private static string SourceStableKey(
            GameMepGraphData graph,
            GameMepSourceData source)
        {
            return (source.ElementKey ?? string.Empty) + "|" +
                ConnectorStableKey(graph,
                    source.BoundaryKind == GameMepBoundaryKind.Inlet
                        ? source.EntryConnectorIndex
                        : source.ExitConnectorIndex) + "|" +
                source.BoundaryKind;
        }

        private static string EdgeStableKey(
            GameMepGraphData graph,
            GameMepConnectionData edge,
            int ownerConnector)
        {
            int other = edge.ConnectorA == ownerConnector
                ? edge.ConnectorB
                : edge.ConnectorA;
            return ConnectorStableKey(graph, other) + "|" +
                (edge.IsInternal ? "0" : "1") + "|" +
                (edge.ElementKey ?? string.Empty);
        }

        private static string ConnectorStableKey(GameMepGraphData graph, int index)
        {
            if (index < 0 || index >= graph.Connectors.Count)
                return "~";
            GameMepConnectorData connector = graph.Connectors[index];
            if (!string.IsNullOrWhiteSpace(connector.PersistentKey))
                return connector.PersistentKey;
            if (!string.IsNullOrWhiteSpace(connector.Key))
                return connector.Key;
            return (connector.ElementKey ?? string.Empty) + "|" + index;
        }
    }
}

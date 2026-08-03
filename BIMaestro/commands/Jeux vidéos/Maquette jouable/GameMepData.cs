using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BIMaestro.VideoGames
{
    internal enum GameMepFlowState
    {
        Unknown,
        Isolated,
        Supplied
    }

    internal enum GameMepConfidence
    {
        Low,
        Medium,
        High
    }

    internal sealed class GameMepConnectorData
    {
        public int Index { get; set; }
        public string Key { get; set; } = string.Empty;
        public string ElementKey { get; set; } = string.Empty;
        public string SystemKey { get; set; } = string.Empty;
        public Point3D Position { get; set; }
        public Vector3D Direction { get; set; }
        public bool HasDirection { get; set; }
        public bool IsConnected { get; set; }
        public string FlowDirection { get; set; } = string.Empty;

        public void Translate(Vector3D delta)
        {
            Position += delta;
        }
    }

    internal sealed class GameMepConnectionData
    {
        public int ConnectorA { get; set; }
        public int ConnectorB { get; set; }
        public bool IsInternal { get; set; }
        public bool IsValveGateCandidate { get; set; }
        public string ElementKey { get; set; } = string.Empty;
    }

    internal sealed class GameMepPathData
    {
        public string ElementKey { get; set; } = string.Empty;
        public string SystemKey { get; set; } = string.Empty;
        public bool IsVisible { get; set; }
        public int StartConnector { get; set; } = -1;
        public int EndConnector { get; set; } = -1;
        public IList<Point3D> Points { get; } = new List<Point3D>();
        public IList<double> CumulativeLengths { get; } = new List<double>();
        public double Length { get; private set; }
        public GameMepFlowState FlowState { get; set; }
        public bool FlowForward { get; set; } = true;

        public Point3D MidPoint => Sample(0.5);

        public void FinalizePath()
        {
            CumulativeLengths.Clear();
            Length = 0.0;
            if (Points.Count == 0)
                return;

            CumulativeLengths.Add(0.0);
            for (int index = 1; index < Points.Count; index++)
            {
                Length += (Points[index] - Points[index - 1]).Length;
                CumulativeLengths.Add(Length);
            }
        }

        public Point3D Sample(double normalizedDistance)
        {
            if (Points.Count == 0)
                return new Point3D();
            if (Points.Count == 1 || Length <= 1e-9)
                return Points[0];

            double target = Math.Max(0.0, Math.Min(1.0, normalizedDistance)) * Length;
            int segment = 1;
            while (segment < CumulativeLengths.Count &&
                   CumulativeLengths[segment] < target)
            {
                segment++;
            }
            segment = Math.Min(segment, Points.Count - 1);
            double firstLength = CumulativeLengths[segment - 1];
            double segmentLength = CumulativeLengths[segment] - firstLength;
            double amount = segmentLength <= 1e-9
                ? 0.0
                : (target - firstLength) / segmentLength;
            Point3D first = Points[segment - 1];
            Point3D second = Points[segment];
            return new Point3D(
                first.X + (second.X - first.X) * amount,
                first.Y + (second.Y - first.Y) * amount,
                first.Z + (second.Z - first.Z) * amount);
        }

        public void Translate(Vector3D delta)
        {
            for (int index = 0; index < Points.Count; index++)
                Points[index] = Points[index] + delta;
        }
    }

    internal sealed class GameMepElementData
    {
        public string Key { get; set; } = string.Empty;
        public long ElementId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string SystemKey { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public string Classification { get; set; } = string.Empty;
        public bool IsVisible { get; set; }
        public bool IsPipeCurve { get; set; }
        public IList<int> ConnectorIndices { get; } = new List<int>();
        public IList<GameMepPathData> Paths { get; } = new List<GameMepPathData>();
        public GameMepFlowState FlowState { get; set; }
    }

    internal sealed class GameMepValveData
    {
        public string ElementKey { get; set; } = string.Empty;
        public GameMepConfidence Confidence { get; set; }
        public string DetectionReason { get; set; } = string.Empty;
        public bool IsEnabledAsValve { get; set; }
        public bool InitiallyEnabledAsValve { get; set; }
        public bool IsClosed { get; set; }
        public bool WasManuallyOverridden { get; set; }
        public GameMepFlowState UpstreamState { get; set; }
        public GameMepFlowState DownstreamState { get; set; }
    }

    internal sealed class GameMepSourceData
    {
        public string ElementKey { get; set; } = string.Empty;
        public string SystemKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public GameMepConfidence Confidence { get; set; }
        public bool IsActive { get; set; }
        public bool InitiallyActive { get; set; }
        public bool WasManuallyOverridden { get; set; }
        public bool IsUserCreated { get; set; }
        /// <summary>
        /// Pour une arrivée définie sur une canalisation, connecteur par
        /// lequel le fluide entre dans le tronçon. Une valeur négative
        /// conserve le comportement des équipements sources automatiques.
        /// </summary>
        public int EntryConnectorIndex { get; set; } = -1;
        public int ExitConnectorIndex { get; set; } = -1;

        public bool HasExplicitDirection =>
            EntryConnectorIndex >= 0 &&
            ExitConnectorIndex >= 0 &&
            EntryConnectorIndex != ExitConnectorIndex;
    }

    internal sealed class GameMepSystemData
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Classification { get; set; } = string.Empty;
        public Color Color { get; set; } = Color.FromRgb(40, 190, 230);
        public bool IsVisible { get; set; } = true;
        public int ElementCount { get; set; }
    }

    internal sealed class GameMepGraphData
    {
        private readonly Dictionary<string, GameMepElementData> _elementsByKey =
            new Dictionary<string, GameMepElementData>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameMepValveData> _valvesByElement =
            new Dictionary<string, GameMepValveData>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameMepSystemData> _systemsByKey =
            new Dictionary<string, GameMepSystemData>(StringComparer.Ordinal);

        public IList<GameMepConnectorData> Connectors { get; } =
            new List<GameMepConnectorData>();
        public IList<GameMepConnectionData> Connections { get; } =
            new List<GameMepConnectionData>();
        public IList<GameMepElementData> Elements { get; } =
            new List<GameMepElementData>();
        public IList<GameMepValveData> Valves { get; } =
            new List<GameMepValveData>();
        public IList<GameMepSourceData> Sources { get; } =
            new List<GameMepSourceData>();
        public IList<GameMepSystemData> Systems { get; } =
            new List<GameMepSystemData>();

        public double ExtractionMilliseconds { get; set; }
        public string ExtractionError { get; set; } = string.Empty;
        public string DocumentTitle { get; set; } = string.Empty;
        public double LastCalculationMilliseconds { get; set; }
        public int OpenConnectorCount { get; set; }
        public int UncertainValveCount => Valves.Count(v =>
            v.Confidence == GameMepConfidence.Low && !v.WasManuallyOverridden);
        public bool HasData => Elements.Count > 0 && Connectors.Count > 0;

        public void RebuildIndexes()
        {
            _elementsByKey.Clear();
            foreach (GameMepElementData element in Elements)
                _elementsByKey[element.Key] = element;

            _valvesByElement.Clear();
            foreach (GameMepValveData valve in Valves)
                _valvesByElement[valve.ElementKey] = valve;

            _systemsByKey.Clear();
            foreach (GameMepSystemData system in Systems)
                _systemsByKey[system.Key] = system;
        }

        public GameMepElementData? FindElement(string key)
        {
            if (_elementsByKey.Count != Elements.Count)
                RebuildIndexes();
            return _elementsByKey.TryGetValue(key, out GameMepElementData element)
                ? element
                : null;
        }

        public GameMepElementData? FindElement(long elementId)
        {
            GameMepElementData? match = null;
            foreach (GameMepElementData element in Elements)
            {
                if (element.ElementId != elementId)
                    continue;
                if (match != null)
                    return null;
                match = element;
            }
            return match;
        }

        public GameMepValveData? FindValve(string elementKey)
        {
            if (_valvesByElement.Count != Valves.Count)
                RebuildIndexes();
            return _valvesByElement.TryGetValue(elementKey, out GameMepValveData valve)
                ? valve
                : null;
        }

        public GameMepSystemData? FindSystem(string key)
        {
            if (_systemsByKey.Count != Systems.Count)
                RebuildIndexes();
            return _systemsByKey.TryGetValue(key, out GameMepSystemData system)
                ? system
                : null;
        }

        public void Translate(Vector3D delta)
        {
            foreach (GameMepConnectorData connector in Connectors)
                connector.Translate(delta);
            foreach (GameMepElementData element in Elements)
            {
                foreach (GameMepPathData path in element.Paths)
                    path.Translate(delta);
            }
        }
    }

    internal sealed class GameMepSimulationEngine
    {
        private readonly GameMepGraphData _graph;
        private List<int>[]? _adjacentEdges;

        public GameMepSimulationEngine(GameMepGraphData graph)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        }

        public void Recalculate()
        {
            var stopwatch = Stopwatch.StartNew();
            EnsureAdjacency();
            int count = _graph.Connectors.Count;
            var reached = new bool[count];
            var distance = new int[count];
            for (int index = 0; index < distance.Length; index++)
                distance[index] = -1;

            var queue = new Queue<int>();
            var activeSourceSystems = new HashSet<string>(StringComparer.Ordinal);
            var directedSourceEntries = new HashSet<int>();
            foreach (GameMepSourceData source in _graph.Sources)
            {
                if (!source.IsActive)
                    continue;
                GameMepElementData? sourceElement = _graph.FindElement(source.ElementKey);
                if (sourceElement == null)
                    continue;

                activeSourceSystems.Add(source.SystemKey ?? string.Empty);
                foreach (int connectorIndex in sourceElement.ConnectorIndices)
                {
                    if (connectorIndex >= 0 && connectorIndex < _graph.Connectors.Count)
                    {
                        activeSourceSystems.Add(
                            _graph.Connectors[connectorIndex].SystemKey ?? string.Empty);
                    }
                }
                IEnumerable<int> seedConnectors = source.HasExplicitDirection &&
                    sourceElement.ConnectorIndices.Contains(source.EntryConnectorIndex)
                        ? new[] { source.EntryConnectorIndex }
                        : sourceElement.ConnectorIndices;
                if (source.HasExplicitDirection &&
                    sourceElement.ConnectorIndices.Contains(source.EntryConnectorIndex))
                {
                    directedSourceEntries.Add(source.EntryConnectorIndex);
                }
                foreach (int connectorIndex in seedConnectors)
                {
                    if (connectorIndex < 0 || connectorIndex >= count || reached[connectorIndex])
                        continue;
                    reached[connectorIndex] = true;
                    distance[connectorIndex] = 0;
                    queue.Enqueue(connectorIndex);
                }
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int edgeIndex in _adjacentEdges![current])
                {
                    GameMepConnectionData edge = _graph.Connections[edgeIndex];
                    if (IsBlockedByValve(edge))
                        continue;
                    // Le connecteur d'entrée représente la limite de la
                    // maquette. On entre dans le tuyau par sa liaison interne,
                    // sans repartir artificiellement vers le réseau amont.
                    if (directedSourceEntries.Contains(current) && !edge.IsInternal)
                        continue;
                    int next = edge.ConnectorA == current
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (next < 0 || next >= count || reached[next])
                        continue;
                    reached[next] = true;
                    distance[next] = distance[current] + 1;
                    queue.Enqueue(next);
                }
            }

            bool hasAnySource = activeSourceSystems.Count > 0;
            foreach (GameMepElementData element in _graph.Elements)
            {
                bool supplied = element.ConnectorIndices.Any(index =>
                    index >= 0 && index < reached.Length && reached[index]);
                bool sourceExistsForSystem =
                    activeSourceSystems.Contains(element.SystemKey ?? string.Empty) ||
                    (string.IsNullOrWhiteSpace(element.SystemKey) && hasAnySource);
                element.FlowState = supplied
                    ? GameMepFlowState.Supplied
                    : sourceExistsForSystem
                        ? GameMepFlowState.Isolated
                        : GameMepFlowState.Unknown;

                int nearestReachedDistance = element.ConnectorIndices
                    .Where(index => index >= 0 && index < distance.Length && distance[index] >= 0)
                    .Select(index => distance[index])
                    .DefaultIfEmpty(int.MaxValue)
                    .Min();

                foreach (GameMepPathData path in element.Paths)
                {
                    path.FlowState = element.FlowState;
                    if (path.StartConnector >= 0 && path.EndConnector >= 0 &&
                        path.StartConnector < distance.Length &&
                        path.EndConnector < distance.Length &&
                        distance[path.StartConnector] >= 0 &&
                        distance[path.EndConnector] >= 0)
                    {
                        path.FlowForward =
                            distance[path.StartConnector] <= distance[path.EndConnector];
                    }
                    else if (path.StartConnector >= 0 &&
                        path.StartConnector < distance.Length &&
                        distance[path.StartConnector] >= 0 &&
                        nearestReachedDistance != int.MaxValue)
                    {
                        // Les tés sont représentés par un chemin de chaque
                        // connecteur vers leur centre. Le connecteur atteint en
                        // premier est l'entrée ; les autres chemins sont lus du
                        // centre vers leurs sorties.
                        path.FlowForward =
                            distance[path.StartConnector] == nearestReachedDistance;
                    }
                }
            }

            foreach (GameMepValveData valve in _graph.Valves)
            {
                GameMepElementData? element = _graph.FindElement(valve.ElementKey);
                bool sourceExistsForValveSystem = element != null &&
                    (activeSourceSystems.Contains(element.SystemKey ?? string.Empty) ||
                     element.ConnectorIndices.Any(index =>
                        index >= 0 && index < _graph.Connectors.Count &&
                        activeSourceSystems.Contains(
                            _graph.Connectors[index].SystemKey ?? string.Empty)));
                if (element == null || element.ConnectorIndices.Count == 0 ||
                    !sourceExistsForValveSystem)
                {
                    valve.UpstreamState = GameMepFlowState.Unknown;
                    valve.DownstreamState = GameMepFlowState.Unknown;
                    continue;
                }

                int reachedCount = element.ConnectorIndices.Count(index =>
                    index >= 0 && index < reached.Length && reached[index]);
                valve.UpstreamState = reachedCount > 0
                    ? GameMepFlowState.Supplied
                    : GameMepFlowState.Isolated;
                valve.DownstreamState = reachedCount == element.ConnectorIndices.Count
                    ? GameMepFlowState.Supplied
                    : GameMepFlowState.Isolated;
            }

            stopwatch.Stop();
            _graph.LastCalculationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        private void EnsureAdjacency()
        {
            if (_adjacentEdges != null &&
                _adjacentEdges.Length == _graph.Connectors.Count)
            {
                return;
            }

            _adjacentEdges = new List<int>[_graph.Connectors.Count];
            for (int index = 0; index < _adjacentEdges.Length; index++)
                _adjacentEdges[index] = new List<int>();

            for (int edgeIndex = 0; edgeIndex < _graph.Connections.Count; edgeIndex++)
            {
                GameMepConnectionData edge = _graph.Connections[edgeIndex];
                if (edge.ConnectorA < 0 || edge.ConnectorB < 0 ||
                    edge.ConnectorA >= _adjacentEdges.Length ||
                    edge.ConnectorB >= _adjacentEdges.Length)
                {
                    continue;
                }
                _adjacentEdges[edge.ConnectorA].Add(edgeIndex);
                _adjacentEdges[edge.ConnectorB].Add(edgeIndex);
            }
        }

        private bool IsBlockedByValve(GameMepConnectionData edge)
        {
            if (!edge.IsInternal || !edge.IsValveGateCandidate)
                return false;
            GameMepValveData? valve = _graph.FindValve(edge.ElementKey);
            return valve != null && valve.IsEnabledAsValve && valve.IsClosed;
        }
    }
}

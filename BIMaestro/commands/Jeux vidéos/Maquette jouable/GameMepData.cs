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

    internal enum GameMepBoundaryKind
    {
        Inlet,
        Outlet
    }

    internal enum GameMepDirectionState
    {
        Unknown,
        Resolved,
        Conflict
    }

    internal sealed class GameMepConnectorData
    {
        public int Index { get; set; }
        public string Key { get; set; } = string.Empty;
        public string PersistentKey { get; set; } = string.Empty;
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
        public GameMepDirectionState DirectionState { get; set; }
        public string DirectionReason { get; set; } = string.Empty;

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
        public string PersistentId { get; set; } = string.Empty;
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
        public GameMepBoundaryKind BoundaryKind { get; set; } =
            GameMepBoundaryKind.Inlet;
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

    internal sealed class GameMepDirectionConstraintData
    {
        public string ElementKey { get; set; } = string.Empty;
        public int EntryConnectorIndex { get; set; } = -1;
        public int ExitConnectorIndex { get; set; } = -1;
        public bool IsActive { get; set; } = true;
        public bool WasManuallyOverridden { get; set; } = true;

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
        public IList<GameMepDirectionConstraintData> DirectionConstraints { get; } =
            new List<GameMepDirectionConstraintData>();
        public IList<GameMepSystemData> Systems { get; } =
            new List<GameMepSystemData>();

        public double ExtractionMilliseconds { get; set; }
        public string ExtractionError { get; set; } = string.Empty;
        public string DocumentTitle { get; set; } = string.Empty;
        public string ScenarioModelKey { get; set; } = string.Empty;
        public bool ScenarioCanPersist { get; set; }
        public int RestoredSourceCount { get; set; }
        public int RestoredValveCount { get; set; }
        public int RestoredDirectionConstraintCount { get; set; }
        public int SkippedScenarioEntryCount { get; set; }
        public string ScenarioPersistenceError { get; set; } = string.Empty;
        public double LastCalculationMilliseconds { get; set; }
        public int OpenConnectorCount { get; set; }
        public int UncertainValveCount => Valves.Count(v =>
            v.Confidence == GameMepConfidence.Low && !v.WasManuallyOverridden);
        public int DirectionConflictCount => Elements
            .SelectMany(element => element.Paths)
            .Count(path => path.DirectionState == GameMepDirectionState.Conflict);
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
            var activeSourceSystems = new HashSet<string>(StringComparer.Ordinal);
            var inletSeeds = new List<int>();
            var inletExternalStops = new HashSet<int>();
            var outletSeeds = new List<int>();
            var outletExternalStops = new HashSet<int>();

            foreach (GameMepSourceData boundary in _graph.Sources
                .Where(item => item.IsActive))
            {
                GameMepElementData? element = _graph.FindElement(boundary.ElementKey);
                if (element == null)
                    continue;

                if (boundary.BoundaryKind == GameMepBoundaryKind.Inlet)
                {
                    activeSourceSystems.Add(boundary.SystemKey ?? string.Empty);
                    foreach (int connector in element.ConnectorIndices)
                    {
                        if (connector >= 0 && connector < count)
                            activeSourceSystems.Add(
                                _graph.Connectors[connector].SystemKey ?? string.Empty);
                    }
                    AddBoundarySeeds(boundary, element, boundary.EntryConnectorIndex,
                        inletSeeds, inletExternalStops);
                }
                else
                {
                    AddBoundarySeeds(boundary, element, boundary.ExitConnectorIndex,
                        outletSeeds, outletExternalStops);
                }
            }

            int[] supplyDistance = ComputeDistances(
                inletSeeds,
                inletExternalStops,
                new Dictionary<int, string>());

            // Une pompe ne crée pas de fluide. Sa contrainte ajoute uniquement
            // un couple bas/haut au calcul visuel du sens.
            var highSeeds = new List<int>(inletSeeds);
            var highInternalStops = new Dictionary<int, string>();
            var lowSeeds = new List<int>(outletSeeds);
            var lowInternalStops = new Dictionary<int, string>();
            foreach (GameMepDirectionConstraintData constraint in
                _graph.DirectionConstraints.Where(item =>
                    item.IsActive && item.HasExplicitDirection))
            {
                if (constraint.ExitConnectorIndex >= 0 &&
                    constraint.ExitConnectorIndex < count)
                {
                    highSeeds.Add(constraint.ExitConnectorIndex);
                    highInternalStops[constraint.ExitConnectorIndex] =
                        constraint.ElementKey;
                }
                if (constraint.EntryConnectorIndex >= 0 &&
                    constraint.EntryConnectorIndex < count)
                {
                    lowSeeds.Add(constraint.EntryConnectorIndex);
                    lowInternalStops[constraint.EntryConnectorIndex] =
                        constraint.ElementKey;
                }
            }

            int[] highDistance = ComputeDistances(
                highSeeds,
                inletExternalStops,
                highInternalStops);
            int[] lowDistance = ComputeDistances(
                lowSeeds,
                outletExternalStops,
                lowInternalStops);

            bool hasAnyInlet = activeSourceSystems.Count > 0;
            foreach (GameMepElementData element in _graph.Elements)
            {
                bool supplied = element.ConnectorIndices.Any(index =>
                    index >= 0 && index < supplyDistance.Length &&
                    supplyDistance[index] >= 0);
                bool inletExistsForSystem =
                    activeSourceSystems.Contains(element.SystemKey ?? string.Empty) ||
                    (string.IsNullOrWhiteSpace(element.SystemKey) && hasAnyInlet);
                element.FlowState = supplied
                    ? GameMepFlowState.Supplied
                    : inletExistsForSystem
                        ? GameMepFlowState.Isolated
                        : GameMepFlowState.Unknown;

                foreach (GameMepPathData path in element.Paths)
                {
                    path.FlowState = element.FlowState;
                    if (TryGetImposedDirection(path, out bool imposedForward,
                            out string imposedReason))
                    {
                        path.FlowForward = imposedForward;
                        path.DirectionState = GameMepDirectionState.Resolved;
                        path.DirectionReason = imposedReason;
                    }
                    else if (TryResolvePathDirection(
                            element,
                            path,
                            highDistance,
                            lowDistance,
                            out bool resolvedForward,
                            out bool conflict,
                            out string reason))
                    {
                        path.FlowForward = resolvedForward;
                        path.DirectionState = GameMepDirectionState.Resolved;
                        path.DirectionReason = reason;
                    }
                    else
                    {
                        // FlowForward est volontairement conservé : une zone
                        // ambiguë ne change donc jamais de sens arbitrairement.
                        path.DirectionState = conflict
                            ? GameMepDirectionState.Conflict
                            : GameMepDirectionState.Unknown;
                        path.DirectionReason = conflict
                            ? "Conflit entre plusieurs chemins de circulation"
                            : "Arrivée ou retour insuffisant pour déterminer le sens";
                    }
                }
            }

            UpdateValveStates(activeSourceSystems, supplyDistance);
            stopwatch.Stop();
            _graph.LastCalculationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        private void AddBoundarySeeds(
            GameMepSourceData boundary,
            GameMepElementData element,
            int preferredConnector,
            IList<int> seeds,
            ISet<int> externalStops)
        {
            if (boundary.HasExplicitDirection &&
                element.ConnectorIndices.Contains(preferredConnector))
            {
                seeds.Add(preferredConnector);
                externalStops.Add(preferredConnector);
                return;
            }
            foreach (int connector in element.ConnectorIndices)
                seeds.Add(connector);
        }

        private int[] ComputeDistances(
            IEnumerable<int> seeds,
            ISet<int> externalStops,
            IDictionary<int, string> internalStops)
        {
            int count = _graph.Connectors.Count;
            var distance = Enumerable.Repeat(-1, count).ToArray();
            var queue = new Queue<int>();
            foreach (int seed in seeds.Distinct().OrderBy(index => index))
            {
                if (seed < 0 || seed >= count || distance[seed] >= 0)
                    continue;
                distance[seed] = 0;
                queue.Enqueue(seed);
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int edgeIndex in _adjacentEdges![current])
                {
                    GameMepConnectionData edge = _graph.Connections[edgeIndex];
                    if (IsBlockedByValve(edge) ||
                        (externalStops.Contains(current) && !edge.IsInternal) ||
                        (internalStops.TryGetValue(current, out string elementKey) &&
                         edge.IsInternal &&
                         string.Equals(edge.ElementKey, elementKey, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    int next = edge.ConnectorA == current
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (next < 0 || next >= count || distance[next] >= 0)
                        continue;
                    distance[next] = distance[current] + 1;
                    queue.Enqueue(next);
                }
            }
            return distance;
        }

        private bool TryGetImposedDirection(
            GameMepPathData path,
            out bool forward,
            out string reason)
        {
            foreach (GameMepSourceData boundary in _graph.Sources.Where(item =>
                item.IsActive && item.HasExplicitDirection &&
                string.Equals(item.ElementKey, path.ElementKey, StringComparison.Ordinal)))
            {
                if (TryMatchDirection(path, boundary.EntryConnectorIndex,
                        boundary.ExitConnectorIndex, out forward))
                {
                    reason = boundary.BoundaryKind == GameMepBoundaryKind.Inlet
                        ? "Sens imposé par l'arrivée"
                        : "Sens imposé vers le retour";
                    return true;
                }
            }
            foreach (GameMepDirectionConstraintData constraint in
                _graph.DirectionConstraints.Where(item =>
                    item.IsActive && item.HasExplicitDirection &&
                    string.Equals(item.ElementKey, path.ElementKey, StringComparison.Ordinal)))
            {
                if (TryMatchDirection(path, constraint.EntryConnectorIndex,
                        constraint.ExitConnectorIndex, out forward))
                {
                    reason = "Sens imposé par la pompe ou l'équipement";
                    return true;
                }
            }
            forward = path.FlowForward;
            reason = string.Empty;
            return false;
        }

        private static bool TryMatchDirection(
            GameMepPathData path,
            int entry,
            int exit,
            out bool forward)
        {
            if (path.StartConnector == entry && path.EndConnector == exit)
            {
                forward = true;
                return true;
            }
            if (path.StartConnector == exit && path.EndConnector == entry)
            {
                forward = false;
                return true;
            }
            forward = path.FlowForward;
            return false;
        }

        private static bool TryResolvePathDirection(
            GameMepElementData element,
            GameMepPathData path,
            int[] highDistance,
            int[] lowDistance,
            out bool forward,
            out bool conflict,
            out string reason)
        {
            forward = path.FlowForward;
            conflict = false;
            reason = string.Empty;
            int start = path.StartConnector;
            int end = path.EndConnector;
            if (start < 0 || start >= highDistance.Length)
                return false;

            if (end >= 0 && end < highDistance.Length)
            {
                bool hs = highDistance[start] >= 0;
                bool he = highDistance[end] >= 0;
                bool ls = lowDistance[start] >= 0;
                bool le = lowDistance[end] >= 0;
                if (hs && he && ls && le)
                {
                    double ps = (double)lowDistance[start] /
                        Math.Max(1, highDistance[start] + lowDistance[start]);
                    double pe = (double)lowDistance[end] /
                        Math.Max(1, highDistance[end] + lowDistance[end]);
                    if (Math.Abs(ps - pe) > 1e-9)
                    {
                        forward = ps > pe;
                        reason = "Déduit entre une arrivée et un retour";
                        return true;
                    }
                    conflict = true;
                    return false;
                }
                if (hs && he && highDistance[start] != highDistance[end])
                {
                    forward = highDistance[start] < highDistance[end];
                    reason = "Déduit depuis l'arrivée la plus proche";
                    return true;
                }
                if (ls && le && lowDistance[start] != lowDistance[end])
                {
                    forward = lowDistance[start] > lowDistance[end];
                    reason = "Déduit vers le retour le plus proche";
                    return true;
                }
                conflict = (hs && he) || (ls && le);
                return false;
            }

            var validHigh = element.ConnectorIndices
                .Where(index => index >= 0 && index < highDistance.Length &&
                    highDistance[index] >= 0)
                .Select(index => highDistance[index])
                .ToList();
            if (highDistance[start] >= 0 && validHigh.Count > 0)
            {
                int minimum = validHigh.Min();
                if (highDistance[start] > minimum)
                {
                    forward = false;
                    reason = "Branche déduite depuis l'arrivée";
                    return true;
                }
                if (validHigh.Count(value => value == minimum) == 1)
                {
                    forward = true;
                    reason = "Branche principale depuis l'arrivée";
                    return true;
                }
                conflict = true;
            }
            return false;
        }

        private void UpdateValveStates(
            ISet<string> activeSourceSystems,
            int[] supplyDistance)
        {
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
                    index >= 0 && index < supplyDistance.Length &&
                    supplyDistance[index] >= 0);
                valve.UpstreamState = reachedCount > 0
                    ? GameMepFlowState.Supplied
                    : GameMepFlowState.Isolated;
                valve.DownstreamState = reachedCount == element.ConnectorIndices.Count
                    ? GameMepFlowState.Supplied
                    : GameMepFlowState.Isolated;
            }
        }

        private void RecalculateLegacy()
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

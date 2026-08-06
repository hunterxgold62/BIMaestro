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

    internal enum GameMepDirectionReliability
    {
        Reliable,
        Inferred,
        Ambiguous,
        Manual
    }

    internal sealed class GameMepDirectionExplanationData
    {
        public GameMepDirectionReliability Reliability { get; set; } =
            GameMepDirectionReliability.Ambiguous;
        public string PrimarySourceElementKey { get; set; } = string.Empty;
        public string PrimarySourceName { get; set; } = string.Empty;
        public IList<string> AlternativeSourceNames { get; } = new List<string>();
        public string InfluencingReturnName { get; set; } = string.Empty;
        public IList<string> UpstreamElementKeys { get; } = new List<string>();
        public IList<string> UpstreamElementNames { get; } = new List<string>();
        public IList<string> LimitingControls { get; } = new List<string>();
        public string Rule { get; set; } = string.Empty;
        public bool HasAlternativeRoute { get; set; }
        public bool IsManual { get; set; }
    }

    internal enum GameMepDirectionConstraintScope
    {
        LocalOverride,
        EquipmentPressureRise
    }

    internal enum GameMepFlowControlKind
    {
        IsolationValve,
        CheckValve
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
        /// <summary>
        /// Indique qu'un trajet participe réellement à une circulation entre
        /// une arrivée et un retour. Une portion atteignable depuis une arrivée
        /// peut rester alimentée tout en étant stagnante devant une vanne fermée.
        /// </summary>
        public bool HasCirculation { get; set; } = true;
        public bool FlowForward { get; set; } = true;
        public GameMepDirectionState DirectionState { get; set; }
        public string DirectionReason { get; set; } = string.Empty;
        public GameMepDirectionExplanationData DirectionExplanation { get; set; } =
            new GameMepDirectionExplanationData();

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
        public GameMepFlowControlKind Kind { get; set; } =
            GameMepFlowControlKind.IsolationValve;
        public GameMepConfidence Confidence { get; set; }
        public string DetectionReason { get; set; } = string.Empty;
        public bool IsEnabledAsValve { get; set; }
        public bool InitiallyEnabledAsValve { get; set; }
        public bool IsClosed { get; set; }
        public bool WasManuallyOverridden { get; set; }
        public int EntryConnectorIndex { get; set; } = -1;
        public int ExitConnectorIndex { get; set; } = -1;
        public int InitiallyEntryConnectorIndex { get; set; } = -1;
        public int InitiallyExitConnectorIndex { get; set; } = -1;
        public GameMepFlowState UpstreamState { get; set; }
        public GameMepFlowState DownstreamState { get; set; }

        public bool HasExplicitDirection =>
            EntryConnectorIndex >= 0 &&
            ExitConnectorIndex >= 0 &&
            EntryConnectorIndex != ExitConnectorIndex;
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
        public GameMepDirectionConstraintScope Scope { get; set; } =
            GameMepDirectionConstraintScope.LocalOverride;
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
        public IList<GameMepDiagnosticData> Diagnostics { get; } =
            new List<GameMepDiagnosticData>();
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
        public double LastDiagnosticMilliseconds { get; set; }
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
            var activeBoundarySystems = new HashSet<string>(StringComparer.Ordinal);
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

                // Une arrivée alimente son système depuis l'amont. Un retour
                // défini par l'utilisateur active lui aussi son propre système,
                // mais le parcours est alors calculé à rebours depuis la sortie.
                // Cela permet de représenter deux systèmes Revit distincts
                // (aller froid / retour chaud) sans les reconnecter artificiellement.
                activeBoundarySystems.Add(boundary.SystemKey ?? string.Empty);
                foreach (int connector in element.ConnectorIndices)
                {
                    if (connector >= 0 && connector < count)
                    {
                        activeBoundarySystems.Add(
                            _graph.Connectors[connector].SystemKey ?? string.Empty);
                    }
                }

                if (boundary.BoundaryKind == GameMepBoundaryKind.Inlet)
                {
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
                new Dictionary<int, string>(),
                false);
            int[] returnDistance = ComputeDistances(
                outletSeeds,
                outletExternalStops,
                new Dictionary<int, string>(),
                true);
            var activeBoundaryDistance = new int[count];
            for (int index = 0; index < count; index++)
            {
                int fromArrival = supplyDistance[index];
                int towardReturn = returnDistance[index];
                activeBoundaryDistance[index] = fromArrival < 0
                    ? towardReturn
                    : towardReturn < 0
                        ? fromArrival
                        : Math.Min(fromArrival, towardReturn);
            }

            // Une pompe ne crée pas de fluide. Sa contrainte ajoute uniquement
            // un couple bas/haut au calcul visuel du sens.
            var highSeeds = new List<int>(inletSeeds);
            var highInternalStops = new Dictionary<int, string>();
            var lowSeeds = new List<int>(outletSeeds);
            var lowInternalStops = new Dictionary<int, string>();
            foreach (GameMepDirectionConstraintData constraint in
                _graph.DirectionConstraints.Where(item =>
                    item.IsActive && item.HasExplicitDirection &&
                    item.Scope ==
                        GameMepDirectionConstraintScope.EquipmentPressureRise))
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
                highInternalStops,
                false);
            int[] lowDistance = ComputeDistances(
                lowSeeds,
                outletExternalStops,
                lowInternalStops,
                true);

            bool hasAnyBoundary = activeBoundarySystems.Count > 0;
            foreach (GameMepElementData element in _graph.Elements)
            {
                bool supplied = element.ConnectorIndices.Any(index =>
                    index >= 0 && index < activeBoundaryDistance.Length &&
                    activeBoundaryDistance[index] >= 0);
                bool boundaryExistsForSystem =
                    activeBoundarySystems.Contains(element.SystemKey ?? string.Empty) ||
                    (string.IsNullOrWhiteSpace(element.SystemKey) && hasAnyBoundary);
                element.FlowState = supplied
                    ? GameMepFlowState.Supplied
                    : boundaryExistsForSystem
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

            ApplyCirculationPotential(
                inletSeeds,
                outletSeeds,
                new HashSet<int>(inletExternalStops.Concat(outletExternalStops)),
                highDistance,
                lowDistance);
            UpdateValveStates(activeBoundarySystems, activeBoundaryDistance);
            GameMepDirectionExplanationBuilder.Refresh(_graph);
            GameMepDiagnosticAnalyzer.Refresh(_graph);
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
            IDictionary<int, string> internalStops,
            bool reverseCheckValveDirection)
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
                    int next = edge.ConnectorA == current
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (!IsSystemCompatibleEdge(edge) ||
                        IsBlockedByFlowControl(
                            edge,
                            current,
                            next,
                            reverseCheckValveDirection) ||
                        (externalStops.Contains(current) && !edge.IsInternal) ||
                        (internalStops.TryGetValue(current, out string elementKey) &&
                         edge.IsInternal &&
                         string.Equals(edge.ElementKey, elementKey, StringComparison.Ordinal)))
                    {
                        continue;
                    }
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
            GameMepValveData? checkValve = _graph.FindValve(path.ElementKey);
            if (checkValve != null &&
                checkValve.IsEnabledAsValve &&
                checkValve.Kind == GameMepFlowControlKind.CheckValve &&
                checkValve.HasExplicitDirection &&
                TryMatchDirection(
                    path,
                    checkValve.EntryConnectorIndex,
                    checkValve.ExitConnectorIndex,
                    out forward))
            {
                reason = "Sens imposé par le clapet anti-retour";
                return true;
            }
            foreach (GameMepDirectionConstraintData constraint in
                _graph.DirectionConstraints.Where(item =>
                    item.IsActive && item.HasExplicitDirection &&
                    item.Scope == GameMepDirectionConstraintScope.LocalOverride &&
                    string.Equals(item.ElementKey, path.ElementKey, StringComparison.Ordinal)))
            {
                if (TryMatchDirection(path, constraint.EntryConnectorIndex,
                        constraint.ExitConnectorIndex, out forward))
                {
                    reason = "Correction locale manuelle, sans effet sur le reste du reseau";
                    return true;
                }
            }
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
                    item.Scope ==
                        GameMepDirectionConstraintScope.EquipmentPressureRise &&
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

        /// <summary>
        /// Sépare une portion réellement traversée d'une portion simplement
        /// atteignable depuis une arrivée. Le calcul travaille sur le réseau
        /// ouvert et considère les extrémités libres comme des sorties implicites.
        /// Une branche terminée par une vanne fermée n'est pas une sortie : elle
        /// reste alimentée, mais aucune flèche ne s'y déplace.
        /// </summary>
        private void ApplyCirculationPotential(
            IEnumerable<int> inletSeedValues,
            IEnumerable<int> outletSeedValues,
            ISet<int> externalStops,
            int[] highDistance,
            int[] lowDistance)
        {
            int connectorCount = _graph.Connectors.Count;
            var inletSeeds = new HashSet<int>(inletSeedValues.Where(index =>
                index >= 0 && index < connectorCount));
            var outletSeeds = new HashSet<int>(outletSeedValues.Where(index =>
                index >= 0 && index < connectorCount));
            var explicitOutletSeeds = new HashSet<int>(outletSeeds);

            // Une extrémité topologique non renseignée représente une sortie
            // implicite. Les connecteurs d'une vanne d'isolement fermée sont
            // volontairement exclus : seule cette coupure interne doit rendre
            // la branche amont stagnante.
            var closedValveConnectors = new HashSet<int>(_graph.Valves
                .Where(valve => valve.IsEnabledAsValve && valve.IsClosed &&
                    valve.Kind == GameMepFlowControlKind.IsolationValve)
                .SelectMany(valve =>
                {
                    GameMepElementData? element =
                        _graph.FindElement(valve.ElementKey);
                    return element != null
                        ? element.ConnectorIndices
                        : Enumerable.Empty<int>();
                }));
            var topologyDegree = new int[connectorCount];
            foreach (GameMepConnectionData edge in _graph.Connections)
            {
                if (edge.ConnectorA < 0 || edge.ConnectorA >= connectorCount ||
                    edge.ConnectorB < 0 || edge.ConnectorB >= connectorCount)
                {
                    continue;
                }
                topologyDegree[edge.ConnectorA]++;
                topologyDegree[edge.ConnectorB]++;
            }
            for (int index = 0; index < connectorCount; index++)
            {
                if (!inletSeeds.Contains(index) && topologyDegree[index] <= 1 &&
                    !closedValveConnectors.Contains(index))
                {
                    outletSeeds.Add(index);
                }
            }

            // Sans couple arrivée/retour, conserver le comportement historique :
            // le moteur ne doit pas faire disparaître tous les flux d'un ancien
            // scénario qui ne possède encore que des arrivées.
            if (inletSeeds.Count == 0 || outletSeeds.Count == 0)
            {
                foreach (GameMepPathData path in _graph.Elements.SelectMany(item => item.Paths))
                {
                    path.HasCirculation =
                        path.FlowState == GameMepFlowState.Supplied &&
                        path.DirectionState == GameMepDirectionState.Resolved;
                }
                return;
            }

            int edgeCount = _graph.Connections.Count;
            var allowed = new bool[edgeCount];
            for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
            {
                GameMepConnectionData edge = _graph.Connections[edgeIndex];
                bool valid = edge.ConnectorA >= 0 && edge.ConnectorA < connectorCount &&
                    edge.ConnectorB >= 0 && edge.ConnectorB < connectorCount;
                allowed[edgeIndex] = valid &&
                    IsSystemCompatibleEdge(edge) &&
                    !IsBlockedByValve(edge) &&
                    !(!edge.IsInternal &&
                      (externalStops.Contains(edge.ConnectorA) ||
                       externalStops.Contains(edge.ConnectorB)));
            }

            var component = Enumerable.Repeat(-1, connectorCount).ToArray();
            var componentHasInlet = new List<bool>();
            var componentHasOutlet = new List<bool>();
            var componentHasExplicitOutlet = new List<bool>();
            int componentIndex = 0;
            for (int seed = 0; seed < connectorCount; seed++)
            {
                if (component[seed] >= 0)
                    continue;
                bool hasInlet = false;
                bool hasOutlet = false;
                bool hasExplicitOutlet = false;
                var queue = new Queue<int>();
                component[seed] = componentIndex;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    hasInlet |= inletSeeds.Contains(current);
                    hasOutlet |= outletSeeds.Contains(current);
                    hasExplicitOutlet |= explicitOutletSeeds.Contains(current);
                    foreach (int edgeIndex in _adjacentEdges![current])
                    {
                        if (!allowed[edgeIndex])
                            continue;
                        GameMepConnectionData edge = _graph.Connections[edgeIndex];
                        int next = edge.ConnectorA == current
                            ? edge.ConnectorB
                            : edge.ConnectorA;
                        if (component[next] >= 0)
                            continue;
                        component[next] = componentIndex;
                        queue.Enqueue(next);
                    }
                }
                componentHasInlet.Add(hasInlet);
                componentHasOutlet.Add(hasOutlet);
                componentHasExplicitOutlet.Add(hasExplicitOutlet);
                componentIndex++;
            }

            bool IsRelevantConnector(int index)
            {
                if (index < 0 || index >= connectorCount)
                    return false;
                int id = component[index];
                return id >= 0 &&
                    ((componentHasInlet[id] && componentHasOutlet[id]) ||
                     componentHasExplicitOutlet[id]);
            }

            bool IsReturnOnlyConnector(int index)
            {
                if (index < 0 || index >= connectorCount)
                    return false;
                int id = component[index];
                return id >= 0 && componentHasExplicitOutlet[id] &&
                    !componentHasInlet[id];
            }

            // Élagage du graphe : toute feuille qui n'est ni une arrivée ni un
            // retour ne peut appartenir à un trajet de circulation. L'opération
            // est répétée jusqu'au té ou à la boucle utile la plus proche.
            var retained = (bool[])allowed.Clone();
            var degree = new int[connectorCount];
            for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
            {
                if (!retained[edgeIndex])
                    continue;
                GameMepConnectionData edge = _graph.Connections[edgeIndex];
                if (!IsRelevantConnector(edge.ConnectorA))
                {
                    retained[edgeIndex] = false;
                    continue;
                }
                degree[edge.ConnectorA]++;
                degree[edge.ConnectorB]++;
            }
            var pruneQueue = new Queue<int>();
            var removed = new bool[connectorCount];
            for (int index = 0; index < connectorCount; index++)
            {
                if (IsRelevantConnector(index) &&
                    !IsReturnOnlyConnector(index) &&
                    !inletSeeds.Contains(index) && !outletSeeds.Contains(index) &&
                    degree[index] <= 1)
                {
                    pruneQueue.Enqueue(index);
                }
            }
            while (pruneQueue.Count > 0)
            {
                int current = pruneQueue.Dequeue();
                if (removed[current] || inletSeeds.Contains(current) ||
                    outletSeeds.Contains(current) || degree[current] > 1)
                {
                    continue;
                }
                removed[current] = true;
                foreach (int edgeIndex in _adjacentEdges![current])
                {
                    if (!retained[edgeIndex])
                        continue;
                    retained[edgeIndex] = false;
                    GameMepConnectionData edge = _graph.Connections[edgeIndex];
                    int next = edge.ConnectorA == current
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    degree[current]--;
                    degree[next]--;
                    if (!removed[next] && !inletSeeds.Contains(next) &&
                        !outletSeeds.Contains(next) && degree[next] <= 1)
                    {
                        pruneQueue.Enqueue(next);
                    }
                }
            }

            var potential = new double[connectorCount];
            var fixedPotential = new bool[connectorCount];
            for (int index = 0; index < connectorCount; index++)
            {
                bool inlet = inletSeeds.Contains(index);
                bool outlet = outletSeeds.Contains(index);
                fixedPotential[index] = inlet || outlet;
                if (inlet && outlet)
                    potential[index] = 0.5;
                else if (inlet)
                    potential[index] = 1.0;
                else if (outlet)
                    potential[index] = 0.0;
                else if (index < highDistance.Length && index < lowDistance.Length &&
                    highDistance[index] >= 0 && lowDistance[index] >= 0)
                {
                    potential[index] = (double)lowDistance[index] /
                        Math.Max(1, highDistance[index] + lowDistance[index]);
                }
                else
                {
                    potential[index] = 0.5;
                }
            }

            // Une vanne d'isolement ouverte n'est ni une source, ni une perte
            // de charge calculée, ni une contrainte de sens. Ses connecteurs
            // représentent donc un même nœud fonctionnel. Les maintenir au
            // même potentiel évite qu'une boucle ou l'ordre des éléments crée
            // une inversion artificielle exactement au passage de la vanne.
            List<int[]> transparentValveGroups = _graph.Valves
                .Where(valve => valve.IsEnabledAsValve && !valve.IsClosed &&
                    valve.Kind == GameMepFlowControlKind.IsolationValve)
                .Select(valve => _graph.FindElement(valve.ElementKey))
                .Where(element => element != null)
                .Select(element => element!.ConnectorIndices
                    .Where(index => index >= 0 && index < connectorCount)
                    .Distinct()
                    .ToArray())
                .Where(group => group.Length >= 2)
                .ToList();

            double EqualizeTransparentValves()
            {
                double maximumChange = 0.0;
                foreach (int[] group in transparentValveGroups)
                {
                    int[] fixedIndices = group.Where(index =>
                        fixedPotential[index]).ToArray();
                    double commonPotential = fixedIndices.Length > 0
                        ? fixedIndices.Average(index => potential[index])
                        : group.Average(index => potential[index]);
                    foreach (int index in group)
                    {
                        if (fixedPotential[index])
                            continue;
                        maximumChange = Math.Max(maximumChange,
                            Math.Abs(commonPotential - potential[index]));
                        potential[index] = commonPotential;
                    }
                }
                return maximumChange;
            }

            EqualizeTransparentValves();

            // Le potentiel par distance fournit une excellente initialisation.
            // Quelques passes de relaxation suffisent ensuite à rendre les
            // boucles et les dérivations indépendantes de l'ordre des objets.
            const int maximumIterations = 320;
            const double relaxation = 1.35;
            for (int iteration = 0; iteration < maximumIterations; iteration++)
            {
                double maximumChange = 0.0;
                for (int index = 0; index < connectorCount; index++)
                {
                    if (!IsRelevantConnector(index) || removed[index] ||
                        fixedPotential[index])
                    {
                        continue;
                    }
                    double total = 0.0;
                    int neighborCount = 0;
                    foreach (int edgeIndex in _adjacentEdges![index])
                    {
                        if (!retained[edgeIndex])
                            continue;
                        GameMepConnectionData edge = _graph.Connections[edgeIndex];
                        int next = edge.ConnectorA == index
                            ? edge.ConnectorB
                            : edge.ConnectorA;
                        total += potential[next];
                        neighborCount++;
                    }
                    if (neighborCount == 0)
                        continue;
                    double average = total / neighborCount;
                    double nextValue = potential[index] +
                        relaxation * (average - potential[index]);
                    nextValue = Math.Max(0.0, Math.Min(1.0, nextValue));
                    maximumChange = Math.Max(maximumChange,
                        Math.Abs(nextValue - potential[index]));
                    potential[index] = nextValue;
                }
                maximumChange = Math.Max(maximumChange,
                    EqualizeTransparentValves());
                if (maximumChange < 1e-7)
                    break;
            }

            var retainedExternalConnector = new bool[connectorCount];
            for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
            {
                if (!retained[edgeIndex])
                    continue;
                GameMepConnectionData edge = _graph.Connections[edgeIndex];
                if (!edge.IsInternal)
                {
                    retainedExternalConnector[edge.ConnectorA] = true;
                    retainedExternalConnector[edge.ConnectorB] = true;
                }
            }

            foreach (GameMepElementData element in _graph.Elements)
            {
                foreach (GameMepPathData path in element.Paths)
                {
                    if (path.FlowState != GameMepFlowState.Supplied ||
                        !IsRelevantConnector(path.StartConnector))
                    {
                        path.HasCirculation = false;
                        continue;
                    }

                    // Sur un système de retour distinct, la sortie déclarée
                    // par l'utilisateur est l'unique condition aux limites. Le
                    // gradient aller/retour n'existe donc pas dans ce composant :
                    // la portée vers le retour et le sens résolu suffisent.
                    if (IsReturnOnlyConnector(path.StartConnector))
                    {
                        path.HasCirculation =
                            path.DirectionState == GameMepDirectionState.Resolved;
                        continue;
                    }

                    if (!TryGetPathPotentialDifference(
                            element,
                            path,
                            retained,
                            retainedExternalConnector,
                            potential,
                            out double difference))
                    {
                        path.HasCirculation = false;
                        if (path.FlowState == GameMepFlowState.Supplied)
                            path.DirectionReason =
                                "Sous pression, stagnation devant une vanne fermée";
                        continue;
                    }

                    path.HasCirculation = Math.Abs(difference) > 1e-5;
                    if (!path.HasCirculation)
                    {
                        path.DirectionReason =
                            "Potentiel équilibré : fluide stagnant";
                        continue;
                    }

                    // Le potentiel décide uniquement si le fluide circule ou
                    // stagne. Le sens a déjà été établi depuis les arrivées,
                    // retours, clapets et corrections manuelles : une boucle
                    // numérique ne doit jamais retourner une flèche locale.
                }
            }

            bool TryGetPathPotentialDifference(
                GameMepElementData element,
                GameMepPathData path,
                bool[] retainedEdges,
                bool[] retainedExternal,
                double[] values,
                out double difference)
            {
                difference = 0.0;
                int start = path.StartConnector;
                int end = path.EndConnector;
                if (start < 0 || start >= connectorCount || removed[start])
                    return false;

                if (end >= 0 && end < connectorCount)
                {
                    bool retainedPathEdge = _adjacentEdges![start].Any(edgeIndex =>
                    {
                        if (!retainedEdges[edgeIndex])
                            return false;
                        GameMepConnectionData edge = _graph.Connections[edgeIndex];
                        int next = edge.ConnectorA == start
                            ? edge.ConnectorB
                            : edge.ConnectorA;
                        return next == end && edge.IsInternal &&
                            string.Equals(edge.ElementKey, path.ElementKey,
                                StringComparison.Ordinal);
                    });
                    if (!retainedPathEdge || removed[end])
                        return false;

                    // Les deux connecteurs d'une vanne ouverte ont été rendus
                    // équipotentiels. Pour conserver une animation continue à
                    // travers son corps, lire le gradient sur ses voisins
                    // extérieurs plutôt que de réintroduire une chute interne.
                    if (IsTransparentIsolationValve(path.ElementKey))
                    {
                        double startSide = GetExternalSidePotential(
                            start, path.ElementKey, retainedEdges, values);
                        double endSide = GetExternalSidePotential(
                            end, path.ElementKey, retainedEdges, values);
                        difference = startSide - endSide;
                    }
                    else
                    {
                        difference = values[start] - values[end];
                    }
                    return true;
                }

                // Les tés et certains accessoires utilisent un chemin depuis
                // chaque connecteur vers un centre géométrique virtuel.
                if (!retainedExternal[start] && !inletSeeds.Contains(start) &&
                    !outletSeeds.Contains(start))
                {
                    return false;
                }
                List<int> useful = element.ConnectorIndices.Where(index =>
                    index >= 0 && index < connectorCount && !removed[index] &&
                    IsRelevantConnector(index) &&
                    (retainedExternal[index] || inletSeeds.Contains(index) ||
                     outletSeeds.Contains(index))).ToList();
                if (useful.Count < 2)
                    return false;
                double center = useful.Average(index => values[index]);
                difference = values[start] - center;
                return true;
            }

            double GetExternalSidePotential(
                int connector,
                string elementKey,
                bool[] retainedEdges,
                double[] values)
            {
                var neighbors = new List<double>();
                foreach (int edgeIndex in _adjacentEdges![connector])
                {
                    if (!retainedEdges[edgeIndex])
                        continue;
                    GameMepConnectionData edge = _graph.Connections[edgeIndex];
                    if (edge.IsInternal && string.Equals(
                            edge.ElementKey, elementKey, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    int next = edge.ConnectorA == connector
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (next >= 0 && next < values.Length)
                        neighbors.Add(values[next]);
                }
                return neighbors.Count > 0
                    ? neighbors.Average()
                    : values[connector];
            }
        }

        private bool IsTransparentIsolationValve(string elementKey)
        {
            GameMepValveData? valve = _graph.FindValve(elementKey);
            return valve != null && valve.IsEnabledAsValve &&
                !valve.IsClosed &&
                valve.Kind == GameMepFlowControlKind.IsolationValve;
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
                    int next = edge.ConnectorA == current
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (!IsSystemCompatibleEdge(edge) ||
                        IsBlockedByFlowControl(edge, current, next, false))
                        continue;
                    // Le connecteur d'entrée représente la limite de la
                    // maquette. On entre dans le tuyau par sa liaison interne,
                    // sans repartir artificiellement vers le réseau amont.
                    if (directedSourceEntries.Contains(current) && !edge.IsInternal)
                        continue;
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

        private bool IsSystemCompatibleEdge(GameMepConnectionData edge)
        {
            if (edge.ConnectorA < 0 || edge.ConnectorB < 0 ||
                edge.ConnectorA >= _graph.Connectors.Count ||
                edge.ConnectorB >= _graph.Connectors.Count)
            {
                return false;
            }

            string first = _graph.Connectors[edge.ConnectorA].SystemKey ??
                string.Empty;
            string second = _graph.Connectors[edge.ConnectorB].SystemKey ??
                string.Empty;
            if (string.Equals(first, second, StringComparison.Ordinal))
                return true;

            // Un raccord sans système propre peut transmettre le réseau qui
            // l'entoure. Deux systèmes Revit explicitement différents restent
            // en revanche deux calculs indépendants, même s'ils se rencontrent
            // dans une pompe ou un autre équipement multi-systèmes.
            return IsUnassignedSystem(first) || IsUnassignedSystem(second);
        }

        private static bool IsUnassignedSystem(string systemKey)
        {
            return string.IsNullOrWhiteSpace(systemKey) ||
                string.Equals(systemKey, "MEP|NON_AFFECTE",
                    StringComparison.Ordinal);
        }

        private bool IsBlockedByFlowControl(
            GameMepConnectionData edge,
            int current,
            int next,
            bool reverseCheckValveDirection)
        {
            if (!edge.IsInternal || !edge.IsValveGateCandidate)
                return false;
            GameMepValveData? valve = _graph.FindValve(edge.ElementKey);
            if (valve == null || !valve.IsEnabledAsValve)
                return false;
            if (valve.Kind == GameMepFlowControlKind.IsolationValve)
                return valve.IsClosed;
            if (!valve.HasExplicitDirection)
                return false;

            int allowedStart = reverseCheckValveDirection
                ? valve.ExitConnectorIndex
                : valve.EntryConnectorIndex;
            int allowedEnd = reverseCheckValveDirection
                ? valve.EntryConnectorIndex
                : valve.ExitConnectorIndex;
            return current != allowedStart || next != allowedEnd;
        }
    }
}

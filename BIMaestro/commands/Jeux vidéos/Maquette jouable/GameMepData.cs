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
        IsolationValve
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
        /// <summary>
        /// Section du connecteur en unités internes Revit (pieds carrés).
        /// Elle sert uniquement à pondérer les jonctions multi-voies : un petit
        /// piquage ne doit pas avoir la même influence qu'un gros collecteur.
        /// Une valeur nulle conserve le comportement topologique historique.
        /// </summary>
        public double CrossSectionArea { get; set; }

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
        public bool IsPipeFitting { get; set; }
        /// <summary>
        /// Vrai uniquement pour un raccord de canalisation multi-voies
        /// (té, piquage, culotte ou croix). Un équipement à plusieurs ports ne
        /// doit pas être assimilé à un nœud de mélange hydraulique.
        /// </summary>
        public bool IsPipeJunction { get; set; }
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

    /// <summary>
    /// Règles communes aux arrivées et retours hydrauliques. Un raccord fait
    /// circuler ou répartit le fluide : il ne peut jamais créer une frontière
    /// hydraulique, même si un ancien scénario contient encore cette entrée.
    /// </summary>
    internal static class GameMepBoundaryPolicy
    {
        public static bool CanHostBoundary(GameMepElementData? element)
        {
            return element != null &&
                element.ConnectorIndices.Count > 0 &&
                !element.IsPipeFitting &&
                !element.IsPipeJunction;
        }

        public static bool IsUsable(
            GameMepElementData? element,
            GameMepSourceData? boundary)
        {
            if (!CanHostBoundary(element) || boundary == null)
                return false;
            if (!boundary.HasExplicitDirection)
                return true;
            return element!.ConnectorIndices.Contains(boundary.EntryConnectorIndex) &&
                element.ConnectorIndices.Contains(boundary.ExitConnectorIndex);
        }
    }

    internal static class GameMepEquipmentDirectionPolicy
    {
        public static bool TryGetNativePumpDirection(
            GameMepGraphData graph,
            GameMepElementData? element,
            out int entryConnector,
            out int exitConnector)
        {
            entryConnector = -1;
            exitConnector = -1;
            if (graph == null || element == null ||
                element.ConnectorIndices.Count != 2 || !IsPumpLike(element))
            {
                return false;
            }

            foreach (int connectorIndex in element.ConnectorIndices)
            {
                if (connectorIndex < 0 || connectorIndex >= graph.Connectors.Count)
                    return false;
                string direction = graph.Connectors[connectorIndex].FlowDirection ??
                    string.Empty;
                if (string.Equals(direction, "In", StringComparison.OrdinalIgnoreCase))
                    entryConnector = connectorIndex;
                else if (string.Equals(
                    direction, "Out", StringComparison.OrdinalIgnoreCase))
                {
                    exitConnector = connectorIndex;
                }
            }
            return entryConnector >= 0 && exitConnector >= 0 &&
                entryConnector != exitConnector;
        }

        private static bool IsPumpLike(GameMepElementData element)
        {
            string identity = (element.Name ?? string.Empty) + " " +
                (element.TypeName ?? string.Empty);
            return identity.IndexOf("pompe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("pump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf(
                    "circulateur", StringComparison.OrdinalIgnoreCase) >= 0;
        }
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
        public string Abbreviation { get; set; } = string.Empty;
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
        public int DiameterAwareJunctionCount { get; set; }
        public int DiameterDirectedPathCount { get; set; }
        public int DiameterInferredInletCount { get; set; }
        public IDictionary<string, Dictionary<int, bool>> StableHeaderDirections { get; } =
            new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);
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

    internal static class GameMepSystemTraversalPolicy
    {
        private const string UnassignedSystemKey = "MEP|NON_AFFECTE";

        public static bool CanTraverse(
            GameMepGraphData graph,
            GameMepConnectionData edge)
        {
            if (edge.ConnectorA < 0 || edge.ConnectorB < 0 ||
                edge.ConnectorA >= graph.Connectors.Count ||
                edge.ConnectorB >= graph.Connectors.Count)
            {
                return false;
            }

            string first = graph.Connectors[edge.ConnectorA].SystemKey ??
                string.Empty;
            string second = graph.Connectors[edge.ConnectorB].SystemKey ??
                string.Empty;
            if (string.Equals(first, second, StringComparison.Ordinal) ||
                IsUnassigned(first) || IsUnassigned(second))
            {
                return true;
            }

            if (!edge.IsInternal)
            {
                GameMepElementData? firstOwner = graph.FindElement(
                    graph.Connectors[edge.ConnectorA].ElementKey);
                GameMepElementData? secondOwner = graph.FindElement(
                    graph.Connectors[edge.ConnectorB].ElementKey);
                if ((firstOwner?.IsPipeJunction ?? false) ||
                    (secondOwner?.IsPipeJunction ?? false))
                {
                    // Une liaison physique avec un té/piquage est précisément
                    // le point où deux systèmes Revit peuvent se mélanger.
                    return true;
                }
                return HaveSameFunctionalType(graph, first, second);
            }
            if (string.IsNullOrWhiteSpace(edge.ElementKey))
                return HaveSameFunctionalType(graph, first, second);

            GameMepElementData? owner = graph.FindElement(edge.ElementKey);
            if (owner?.IsPipeJunction ?? false)
                return true;
            if (owner != null && owner.ConnectorIndices.Count == 2 &&
                HaveSameFunctionalType(graph, first, second))
            {
                return true;
            }

            // Une pompe peut séparer deux instances de système Revit tout en
            // faisant circuler le même fluide. La contrainte de pression créée
            // explicitement par l'utilisateur constitue l'autorisation de
            // franchir cette frontière, uniquement entre ses deux connecteurs.
            if (graph.DirectionConstraints.Any(constraint =>
                constraint.IsActive &&
                constraint.HasExplicitDirection &&
                constraint.Scope ==
                    GameMepDirectionConstraintScope.EquipmentPressureRise &&
                string.Equals(
                    constraint.ElementKey,
                    edge.ElementKey,
                    StringComparison.Ordinal) &&
                MatchesPair(
                    edge,
                    constraint.EntryConnectorIndex,
                    constraint.ExitConnectorIndex)))
            {
                return true;
            }

            return owner != null &&
                GameMepEquipmentDirectionPolicy.TryGetNativePumpDirection(
                    graph,
                    owner,
                    out int nativeEntry,
                    out int nativeExit) &&
                MatchesPair(edge, nativeEntry, nativeExit);
        }

        private static bool HaveSameFunctionalType(
            GameMepGraphData graph,
            string firstKey,
            string secondKey)
        {
            GameMepSystemData? first = graph.FindSystem(firstKey);
            GameMepSystemData? second = graph.FindSystem(secondKey);
            if (first == null || second == null ||
                string.IsNullOrWhiteSpace(first.Abbreviation) ||
                string.IsNullOrWhiteSpace(second.Abbreviation) ||
                !string.Equals(
                    first.Abbreviation.Trim(),
                    second.Abbreviation.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // L'abréviation est un regroupement métier choisi par l'utilisateur,
            // mais la classification Revit reste le garde-fou hydraulique.
            return string.IsNullOrWhiteSpace(first.Classification) ||
                string.IsNullOrWhiteSpace(second.Classification) ||
                string.Equals(
                    first.Classification.Trim(),
                    second.Classification.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnassigned(string systemKey)
        {
            return string.IsNullOrWhiteSpace(systemKey) ||
                string.Equals(
                    systemKey,
                    UnassignedSystemKey,
                    StringComparison.Ordinal);
        }

        private static bool MatchesPair(
            GameMepConnectionData edge,
            int first,
            int second)
        {
            return (edge.ConnectorA == first && edge.ConnectorB == second) ||
                (edge.ConnectorA == second && edge.ConnectorB == first);
        }
    }

    internal sealed class GameMepSimulationEngine
    {
        private const string TwoPortFittingContinuityReason =
            "Continuité avec les canalisations autour du composant à deux ports";
        private const string PipeJunctionContinuityReason =
            "Continuité avec les canalisations autour du té";
        private const string NativePumpSuctionContinuityReason =
            "Aspiration propagée depuis le port d'entrée de la pompe";
        private const string NativePumpDischargeContinuityReason =
            "Refoulement propagé depuis le port de sortie de la pompe";
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
                if (!GameMepBoundaryPolicy.IsUsable(element, boundary))
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
                new Dictionary<int, string>());
            int[] returnDistance = ComputeDistances(
                outletSeeds,
                outletExternalStops,
                new Dictionary<int, string>());
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
            var pumpSuctionSeeds = new List<int>();
            var pumpSuctionInternalStops = new Dictionary<int, string>();
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
                    pumpSuctionSeeds.Add(constraint.EntryConnectorIndex);
                    pumpSuctionInternalStops[constraint.EntryConnectorIndex] =
                        constraint.ElementKey;
                }
            }
            foreach (GameMepElementData pump in _graph.Elements)
            {
                bool hasManualPumpDirection = _graph.DirectionConstraints.Any(item =>
                    item.IsActive && item.HasExplicitDirection &&
                    item.Scope ==
                        GameMepDirectionConstraintScope.EquipmentPressureRise &&
                    string.Equals(item.ElementKey, pump.Key, StringComparison.Ordinal));
                if (hasManualPumpDirection ||
                    !GameMepEquipmentDirectionPolicy.TryGetNativePumpDirection(
                        _graph,
                        pump,
                        out int nativeEntry,
                        out int nativeExit))
                {
                    continue;
                }
                highSeeds.Add(nativeExit);
                highInternalStops[nativeExit] = pump.Key;
                lowSeeds.Add(nativeEntry);
                lowInternalStops[nativeEntry] = pump.Key;
                pumpSuctionSeeds.Add(nativeEntry);
                pumpSuctionInternalStops[nativeEntry] = pump.Key;
            }

            int[] highDistance = ComputeDistances(
                highSeeds,
                inletExternalStops,
                highInternalStops);
            int[] lowDistance = ComputeDistances(
                lowSeeds,
                outletExternalStops,
                lowInternalStops);
            int[] pumpSuctionDistance = ComputeDistances(
                pumpSuctionSeeds,
                new HashSet<int>(),
                pumpSuctionInternalStops);

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
                returnDistance,
                highDistance,
                lowDistance,
                pumpSuctionDistance);
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
                    int next = edge.ConnectorA == current
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (!IsSystemCompatibleEdge(edge) ||
                        IsBlockedByFlowControl(edge) ||
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
                GameMepBoundaryPolicy.IsUsable(
                    _graph.FindElement(item.ElementKey), item) &&
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
            GameMepElementData? owner = _graph.FindElement(path.ElementKey);
            bool hasManualPumpDirection = _graph.DirectionConstraints.Any(item =>
                item.IsActive && item.HasExplicitDirection &&
                item.Scope == GameMepDirectionConstraintScope.EquipmentPressureRise &&
                string.Equals(item.ElementKey, path.ElementKey, StringComparison.Ordinal));
            if (!hasManualPumpDirection &&
                GameMepEquipmentDirectionPolicy.TryGetNativePumpDirection(
                    _graph,
                    owner,
                    out int nativeEntry,
                    out int nativeExit) &&
                TryMatchDirection(path, nativeEntry, nativeExit, out forward))
            {
                reason = "Sens natif de la pompe : aspiration puis refoulement";
                return true;
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
            int[] declaredReturnDistance,
            int[] highDistance,
            int[] lowDistance,
            int[] pumpSuctionDistance)
        {
            int connectorCount = _graph.Connectors.Count;
            var inletSeeds = new HashSet<int>(inletSeedValues.Where(index =>
                index >= 0 && index < connectorCount));
            var outletSeeds = new HashSet<int>(outletSeedValues.Where(index =>
                index >= 0 && index < connectorCount));
            var explicitInletSeeds = new HashSet<int>(inletSeeds);
            var explicitOutletSeeds = new HashSet<int>(outletSeeds);

            // Les raccords à trois voies ou plus sont des nœuds de mélange.
            // La section de rôle sera affinée sur le premier tronçon droit de
            // chaque bras : un té 3 x DN 200 suivi immédiatement d'un réducteur
            // vers un DN 300 doit être reconnu comme 200/200/300.
            const double significantAreaRatio = 1.5625; // rapport de DN 1,25
            var candidateJunctionPortAreas = new Dictionary<
                string,
                Dictionary<int, double>>(StringComparer.Ordinal);
            var junctionPortWeights = new Dictionary<string, Dictionary<int, double>>(
                StringComparer.Ordinal);
            foreach (GameMepElementData junction in _graph.Elements.Where(item =>
                item.IsPipeJunction && item.ConnectorIndices.Count >= 3))
            {
                int[] ports = junction.ConnectorIndices.Where(index =>
                    index >= 0 && index < connectorCount).Distinct().ToArray();
                if (ports.Length < 3)
                    continue;

                candidateJunctionPortAreas[junction.Key] = ports.ToDictionary(
                    index => index,
                    index => _graph.Connectors[index].CrossSectionArea);
            }
            _graph.DiameterAwareJunctionCount = 0;
            _graph.DiameterDirectedPathCount = 0;
            _graph.DiameterInferredInletCount = 0;

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
            var implicitOutletSeeds = new HashSet<int>();
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
                    if (!explicitOutletSeeds.Contains(index))
                        implicitOutletSeeds.Add(index);
                }
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

            // Pré-indexer les seules contraintes qui rendent un bras impropre à
            // une inférence globale. Une correction locale reste locale et sera
            // protégée plus tard par TryGetImposedDirection.
            var restrictedArmElementKeys = new HashSet<string>(
                _graph.DirectionConstraints.Where(item => item.IsActive &&
                    item.Scope ==
                        GameMepDirectionConstraintScope.EquipmentPressureRise)
                .Select(item => item.ElementKey),
                StringComparer.Ordinal);
            var inletAnchorSeeds = new HashSet<int>(explicitInletSeeds);
            var pumpSuctionAnchorSeeds = new HashSet<int>();
            foreach (GameMepDirectionConstraintData constraint in
                _graph.DirectionConstraints.Where(item => item.IsActive &&
                    item.HasExplicitDirection &&
                    item.Scope ==
                        GameMepDirectionConstraintScope.EquipmentPressureRise))
            {
                if (constraint.ExitConnectorIndex >= 0 &&
                    constraint.ExitConnectorIndex < connectorCount)
                {
                    // La sortie d'une pompe est un ancrage amont fiable pour le
                    // bras local, sans être transformée en source globale.
                    inletAnchorSeeds.Add(constraint.ExitConnectorIndex);
                }
                if (constraint.EntryConnectorIndex >= 0 &&
                    constraint.EntryConnectorIndex < connectorCount)
                {
                    pumpSuctionAnchorSeeds.Add(constraint.EntryConnectorIndex);
                }
            }
            foreach (GameMepElementData pump in _graph.Elements)
            {
                bool hasManualPumpDirection = _graph.DirectionConstraints.Any(item =>
                    item.IsActive && item.HasExplicitDirection &&
                    item.Scope ==
                        GameMepDirectionConstraintScope.EquipmentPressureRise &&
                    string.Equals(item.ElementKey, pump.Key, StringComparison.Ordinal));
                if (hasManualPumpDirection ||
                    !GameMepEquipmentDirectionPolicy.TryGetNativePumpDirection(
                        _graph,
                        pump,
                        out int nativeEntry,
                        out int nativeExit))
                {
                    continue;
                }
                restrictedArmElementKeys.Add(pump.Key);
                pumpSuctionAnchorSeeds.Add(nativeEntry);
                inletAnchorSeeds.Add(nativeExit);
            }
            var junctionArms = new Dictionary<
                string,
                Dictionary<int, HashSet<int>>>(StringComparer.Ordinal);
            var junctionSimpleArms = new Dictionary<
                string,
                Dictionary<int, bool>>(StringComparer.Ordinal);
            var junctionEffectiveAreas = new Dictionary<
                string,
                Dictionary<int, double>>(StringComparer.Ordinal);
            var junctionTerminalJunctions = new Dictionary<
                string,
                Dictionary<int, string>>(StringComparer.Ordinal);
            foreach (string junctionKey in candidateJunctionPortAreas.Keys.OrderBy(
                key => key, StringComparer.Ordinal))
            {
                GameMepElementData? junction = _graph.FindElement(junctionKey);
                if (junction == null)
                    continue;

                var arms = new Dictionary<int, HashSet<int>>();
                var simpleArms = new Dictionary<int, bool>();
                var effectiveAreas = new Dictionary<int, double>();
                var terminalJunctions = new Dictionary<int, string>();
                foreach (int port in junction.ConnectorIndices.Distinct())
                {
                    bool simple = TryCollectJunctionArm(
                        junction,
                        port,
                        allowed,
                        explicitInletSeeds,
                        explicitOutletSeeds,
                        implicitOutletSeeds,
                        restrictedArmElementKeys,
                        out HashSet<int> arm,
                        out double effectiveArea,
                        out string terminalJunctionKey);
                    arms[port] = arm;
                    simpleArms[port] = simple;
                    effectiveAreas[port] = effectiveArea;
                    terminalJunctions[port] = terminalJunctionKey;
                }
                junctionArms[junctionKey] = arms;
                junctionSimpleArms[junctionKey] = simpleArms;
                junctionEffectiveAreas[junctionKey] = effectiveAreas;
                junctionTerminalJunctions[junctionKey] = terminalJunctions;

                double maximumArea = effectiveAreas.Values.Max();
                double minimumArea = effectiveAreas.Values.Min();
                if (minimumArea <= 1e-12 ||
                    maximumArea / minimumArea < significantAreaRatio)
                {
                    continue;
                }
                junctionPortWeights[junctionKey] = effectiveAreas.ToDictionary(
                    item => item.Key,
                    item => Math.Max(0.05, item.Value / maximumArea));
            }
            _graph.DiameterAwareJunctionCount = junctionPortWeights.Count;

            // Limiter l'influence DN aux bras réellement reliés au raccord.
            // On s'arrête à une autre jonction ou à une frontière : un té de
            // fusion ne doit jamais retourner une distribution située plus loin
            // dans le même système Revit.
            var diameterInfluencedElementKeys = new HashSet<string>(
                StringComparer.Ordinal);
            var diameterForcedElementKeys = new HashSet<string>(
                StringComparer.Ordinal);
            var diameterActivatedJunctionKeys = new HashSet<string>(
                StringComparer.Ordinal);
            var diameterForcedJunctionPorts = new HashSet<int>();
            var diameterHeaderContinuityElementKeys = new HashSet<string>(
                StringComparer.Ordinal);
            var diameterHeaderElementDirections = new Dictionary<string, bool>(
                StringComparer.Ordinal);
            var diameterJunctionPortDirections = new Dictionary<int, bool>();
            var diameterProtectedJunctionPorts = new HashSet<int>();
            var geometricHeaderContinuityElementKeys = new HashSet<string>(
                StringComparer.Ordinal);
            var geometricProtectedJunctionPorts = new HashSet<int>();
            var inferredInletCandidates = new HashSet<int>();
            foreach (string junctionKey in junctionPortWeights.Keys.OrderBy(
                key => key, StringComparer.Ordinal))
            {
                GameMepElementData? junction = _graph.FindElement(junctionKey);
                if (junction == null ||
                    !junctionArms.TryGetValue(junctionKey,
                        out Dictionary<int, HashSet<int>> arms) ||
                    !junctionSimpleArms.TryGetValue(junctionKey,
                        out Dictionary<int, bool> simpleArms) ||
                    !junctionEffectiveAreas.TryGetValue(junctionKey,
                        out Dictionary<int, double> effectiveAreas) ||
                    !junctionTerminalJunctions.TryGetValue(junctionKey,
                        out Dictionary<int, string> terminalJunctions))
                {
                    continue;
                }

                // Les bras doivent rester indépendants hors du té. Si un bypass
                // les rejoint ailleurs, le DN ne réoriente aucun de leurs paths.
                HashSet<int>[] armSets = arms.Values.ToArray();
                bool armsOverlap = false;
                for (int first = 0; first < armSets.Length && !armsOverlap; first++)
                {
                    for (int second = first + 1; second < armSets.Length; second++)
                    {
                        if (!armSets[first].Overlaps(armSets[second]))
                            continue;
                        armsOverlap = true;
                        break;
                    }
                }
                string[] terminalKeys = terminalJunctions.Values.Where(key =>
                        !string.IsNullOrWhiteSpace(key))
                    .ToArray();
                if (terminalKeys.Length != terminalKeys.Distinct(
                        StringComparer.Ordinal).Count())
                {
                    armsOverlap = true;
                }
                if (armsOverlap)
                    continue;

                // Sur un retour hydraulique, le sens est fixé par l'aspiration
                // des pompes. Une variation de DN reste une information de
                // section et ne doit jamais transformer un bras en arrivée.
                // La continuité géométrique des tés est traitée juste après.
                if (IsReturnHydronic(junction))
                    continue;

                if (junction.ConnectorIndices.Count != 3 ||
                    simpleArms.Values.Any(simple => !simple) ||
                    junction.ConnectorIndices.Any(index => index < 0 ||
                        index >= connectorCount ||
                        !_graph.Connectors[index].IsConnected))
                {
                    continue;
                }

                int[] ports = junction.ConnectorIndices.Distinct().ToArray();
                int[] orderedPorts = ports.OrderByDescending(index =>
                    effectiveAreas[index]).ToArray();
                double largestArea = effectiveAreas[orderedPorts[0]];
                double secondArea = effectiveAreas[orderedPorts[1]];
                double smallestArea = effectiveAreas[orderedPorts[2]];
                if (secondArea <= 1e-12 || smallestArea <= 1e-12)
                {
                    continue;
                }

                int[] pumpSuctionPorts = IsReturnHydronic(junction)
                    ? ports.Where(port => IsStrictlyCloserToBoundary(
                        port,
                        ports.Where(other => other != port),
                        pumpSuctionDistance)).ToArray()
                    : Array.Empty<int>();
                if (pumpSuctionPorts.Length == 1)
                {
                    int suctionPort = pumpSuctionPorts[0];
                    diameterActivatedJunctionKeys.Add(junctionKey);
                    diameterJunctionPortDirections[suctionPort] = false;
                    diameterProtectedJunctionPorts.Add(suctionPort);
                    AddArmPipeElements(
                        arms[suctionPort], diameterInfluencedElementKeys);
                    AddArmPipeElements(
                        arms[suctionPort], diameterForcedElementKeys);
                    diameterForcedJunctionPorts.Add(suctionPort);
                }

                bool hasUniqueSmallPort =
                    largestArea / secondArea < significantAreaRatio &&
                    secondArea / smallestArea >= significantAreaRatio;
                if (hasUniqueSmallPort)
                {
                    // Piquage sur un collecteur continu : les deux gros bras
                    // gardent leur sens historique. Seule la petite branche peut
                    // être résolue par le DN, et uniquement si une arrivée ou une
                    // sortie de pompe la situe réellement côté amont.
                    int smallPort = orderedPorts[2];
                    int[] largePorts = orderedPorts.Take(2).ToArray();
                    HashSet<int> smallArm = arms[smallPort];
                    bool returnNetwork = IsReturnHydronic(junction);
                    bool smallReachesPumpSuction = returnNetwork &&
                        (smallArm.Overlaps(pumpSuctionAnchorSeeds) ||
                         IsStrictlyCloserToBoundary(
                             smallPort, largePorts, pumpSuctionDistance));
                    bool smallHasInlet = smallArm.Overlaps(inletAnchorSeeds) ||
                        IsStrictlyCloserToBoundary(
                            smallPort, largePorts, highDistance);
                    bool smallHasOutlet =
                        smallArm.Overlaps(explicitOutletSeeds) ||
                        smallReachesPumpSuction;
                    bool smallIsActiveInlet = smallHasInlet && !smallHasOutlet;

                    Dictionary<int, bool>? headerDirections = null;
                    if (!smallIsActiveInlet && TryReadResolvedHeaderDirections(
                            junction, largePorts, arms,
                            out Dictionary<int, bool> stable))
                    {
                        // Vanne du petit DN fermée : mémoriser le sens réellement
                        // résolu du collecteur. Plusieurs pompes en DN200/150 ne
                        // pourront ensuite plus le retourner à la réouverture.
                        _graph.StableHeaderDirections[junctionKey] =
                            new Dictionary<int, bool>(stable);
                        headerDirections = stable;
                    }
                    else if (_graph.StableHeaderDirections.TryGetValue(
                            junctionKey, out Dictionary<int, bool> remembered) &&
                        largePorts.All(remembered.ContainsKey))
                    {
                        headerDirections = new Dictionary<int, bool>(remembered);
                    }
                    else if (TryBuildHeaderDirectionsFromBoundary(
                            largePorts,
                            declaredReturnDistance,
                            out Dictionary<int, bool> declaredDirections))
                    {
                        // Contrairement à lowDistance, cette distance ne contient
                        // aucune entrée de pompe : seul le retour déclaré décide.
                        headerDirections = declaredDirections;
                    }
                    else if (TryReadResolvedHeaderDirections(
                            junction, largePorts, arms,
                            out Dictionary<int, bool> currentDirections))
                    {
                        headerDirections = currentDirections;
                    }

                    if (headerDirections != null)
                    {
                        diameterActivatedJunctionKeys.Add(junctionKey);
                        foreach (int headerPort in largePorts)
                        {
                            AddArmPipeElements(
                                arms[headerPort],
                                diameterHeaderContinuityElementKeys);
                            diameterJunctionPortDirections[headerPort] =
                                headerDirections[headerPort];
                            diameterProtectedJunctionPorts.Add(headerPort);
                            AddHeaderArmDirections(
                                headerPort,
                                arms[headerPort],
                                headerDirections[headerPort],
                                diameterHeaderElementDirections);
                            ExtendHeaderBackbone(
                                junctionKey,
                                headerPort,
                                headerDirections[headerPort],
                                geometricContinuity: false);
                        }
                    }
                    if (smallIsActiveInlet)
                    {
                        diameterActivatedJunctionKeys.Add(junctionKey);
                        AddArmPipeElements(
                            smallArm, diameterInfluencedElementKeys);
                        diameterJunctionPortDirections[smallPort] = true;
                        diameterProtectedJunctionPorts.Add(smallPort);
                    }
                    else if (smallReachesPumpSuction)
                    {
                        // Sur un retour hydraulique, le petit bras raccordé à
                        // l'entrée In d'une pompe est aspiré depuis le centre du
                        // té. Le DN ne doit jamais le convertir en arrivée.
                        diameterActivatedJunctionKeys.Add(junctionKey);
                        AddArmPipeElements(
                            smallArm, diameterInfluencedElementKeys);
                        diameterJunctionPortDirections[smallPort] = false;
                        diameterProtectedJunctionPorts.Add(smallPort);
                    }
                    continue;
                }

                bool hasUniqueLargePort =
                    largestArea / secondArea >= significantAreaRatio &&
                    secondArea / smallestArea <= significantAreaRatio;
                if (!hasUniqueLargePort)
                    continue;

                int largePort = orderedPorts[0];
                int[] smallPorts = orderedPorts.Skip(1).ToArray();
                if (smallPorts.Any(diameterProtectedJunctionPorts.Contains))
                    continue;
                HashSet<int> largeArm = arms[largePort];
                bool largeHasOutlet = largeArm.Overlaps(explicitOutletSeeds) ||
                    IsStrictlyCloserToBoundary(
                        largePort, smallPorts, lowDistance);
                bool largeHasInlet = largeArm.Overlaps(inletAnchorSeeds) ||
                    IsStrictlyCloserToBoundary(
                        largePort, smallPorts, highDistance);
                if (!largeHasOutlet || largeHasInlet)
                    continue;

                bool hasExplicitSmallInlet = false;
                bool validMerge = true;
                var localCandidates = new List<int>();
                foreach (int smallPort in smallPorts)
                {
                    HashSet<int> arm = arms[smallPort];
                    bool hasInlet = arm.Overlaps(inletAnchorSeeds);
                    bool hasOutlet = arm.Overlaps(explicitOutletSeeds);
                    int[] implicitLeaves = implicitOutletSeeds.Where(
                        arm.Contains).ToArray();
                    if (hasOutlet || (hasInlet && implicitLeaves.Length > 0) ||
                        (!hasInlet && implicitLeaves.Length != 1))
                    {
                        validMerge = false;
                        break;
                    }
                    if (hasInlet)
                        hasExplicitSmallInlet = true;
                    else
                        localCandidates.Add(implicitLeaves[0]);
                }
                if (validMerge && hasExplicitSmallInlet)
                {
                    diameterActivatedJunctionKeys.Add(junctionKey);
                    diameterJunctionPortDirections[largePort] = false;
                    diameterProtectedJunctionPorts.Add(largePort);
                    foreach (int smallPort in smallPorts)
                    {
                        diameterJunctionPortDirections[smallPort] = true;
                        diameterProtectedJunctionPorts.Add(smallPort);
                    }
                    foreach (HashSet<int> arm in arms.Values)
                    {
                        AddArmPipeElements(
                            arm, diameterInfluencedElementKeys);
                    }
                    foreach (int candidate in localCandidates)
                    {
                        inferredInletCandidates.Add(candidate);
                        foreach (KeyValuePair<int, HashSet<int>> arm in arms.Where(
                            item => item.Value.Contains(candidate)))
                        {
                            AddArmPipeElements(
                                arm.Value, diameterForcedElementKeys);
                            diameterForcedJunctionPorts.Add(arm.Key);
                        }
                    }
                }
            }

            // Un té standard possède deux ports opposés qui forment le
            // collecteur et un troisième port latéral. Même à DN identiques,
            // une branche courte ou un équipement voisin ne doit jamais
            // retourner le tronçon compris entre deux tés. La géométrie Revit
            // est ici plus fiable que la moyenne du centre virtuel.
            foreach (string junctionKey in candidateJunctionPortAreas.Keys.OrderBy(
                key => key, StringComparer.Ordinal))
            {
                GameMepElementData? junction = _graph.FindElement(junctionKey);
                if (junction == null || junction.ConnectorIndices.Count != 3 ||
                    !junctionArms.TryGetValue(junctionKey,
                        out Dictionary<int, HashSet<int>> arms) ||
                    !junctionSimpleArms.TryGetValue(junctionKey,
                        out Dictionary<int, bool> simpleArms) ||
                    simpleArms.Values.Any(simple => !simple) ||
                    !TrySelectCollinearHeaderPorts(junction, out int[] headerPorts) ||
                    headerPorts.All(diameterProtectedJunctionPorts.Contains))
                {
                    continue;
                }

                Dictionary<int, bool>? headerDirections = null;
                if (IsReturnHydronic(junction) &&
                    TryBuildHeaderDirectionsFromBoundary(
                        headerPorts,
                        pumpSuctionDistance,
                        out Dictionary<int, bool> pumpSuctionDirections))
                {
                    headerDirections = pumpSuctionDirections;
                }
                else if (TryBuildHeaderDirectionsFromBoundary(
                        headerPorts,
                        declaredReturnDistance,
                        out Dictionary<int, bool> returnDirections))
                {
                    headerDirections = returnDirections;
                }
                else if (TryBuildHeaderDirectionsFromInlet(
                        headerPorts,
                        highDistance,
                        out Dictionary<int, bool> inletDirections))
                {
                    headerDirections = inletDirections;
                }
                else if (TryReadResolvedHeaderDirections(
                        junction,
                        headerPorts,
                        arms,
                        out Dictionary<int, bool> resolvedDirections))
                {
                    headerDirections = resolvedDirections;
                }
                if (headerDirections == null ||
                    headerPorts.Any(port =>
                        diameterProtectedJunctionPorts.Contains(port) &&
                        diameterJunctionPortDirections.TryGetValue(
                            port, out bool existing) &&
                        existing != headerDirections[port]))
                {
                    continue;
                }

                diameterActivatedJunctionKeys.Add(junctionKey);
                foreach (int headerPort in headerPorts)
                {
                    bool towardCenter = headerDirections[headerPort];
                    diameterJunctionPortDirections[headerPort] = towardCenter;
                    diameterProtectedJunctionPorts.Add(headerPort);
                    geometricProtectedJunctionPorts.Add(headerPort);
                    AddArmPipeElements(
                        arms[headerPort],
                        diameterHeaderContinuityElementKeys);
                    AddArmPipeElements(
                        arms[headerPort],
                        geometricHeaderContinuityElementKeys);
                    AddHeaderArmDirections(
                        headerPort,
                        arms[headerPort],
                        towardCenter,
                        diameterHeaderElementDirections);
                    ExtendHeaderBackbone(
                        junctionKey,
                        headerPort,
                        towardCenter,
                        geometricContinuity: true);
                }
            }

            // Un simple contraste de DN ne suffit jamais à retourner un réseau.
            // Seuls un piquage amont démontré ou une fusion locale validée gardent
            // la pondération hydraulique. Les divisions ordinaires, notamment
            // celles situées autour des pompes, restent entièrement historiques.
            foreach (string inactiveKey in junctionPortWeights.Keys.Where(key =>
                    !diameterActivatedJunctionKeys.Contains(key)).ToArray())
            {
                junctionPortWeights.Remove(inactiveKey);
            }
            _graph.DiameterAwareJunctionCount = junctionPortWeights.Count;

            // Toutes les décisions ci-dessus sont fondées exclusivement sur les
            // frontières initiales. Une entrée inférée au té A ne peut donc pas
            // déclencher en cascade une nouvelle inférence au té B.
            foreach (int candidate in inferredInletCandidates)
            {
                if (explicitOutletSeeds.Contains(candidate))
                    continue;
                outletSeeds.Remove(candidate);
                inletSeeds.Add(candidate);
                _graph.DiameterInferredInletCount++;
            }

            // Sans couple arrivée/retour, conserver le comportement historique :
            // le moteur ne doit pas faire disparaître tous les flux d'un ancien
            // scénario qui ne possède encore que des arrivées.
            if ((inletSeeds.Count == 0 && explicitOutletSeeds.Count == 0) ||
                outletSeeds.Count == 0)
            {
                foreach (GameMepPathData path in _graph.Elements.SelectMany(item => item.Paths))
                {
                    path.HasCirculation =
                        path.FlowState == GameMepFlowState.Supplied &&
                        path.DirectionState == GameMepDirectionState.Resolved;
                }
                return;
            }

            var component = Enumerable.Repeat(-1, connectorCount).ToArray();
            var componentHasInlet = new List<bool>();
            var componentHasOutlet = new List<bool>();
            var componentHasExplicitOutlet = new List<bool>();
            var componentIsReturnOnly = new List<bool>();
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
                componentIsReturnOnly.Add(hasExplicitOutlet && !hasInlet);
                componentIndex++;
            }

            // Dans un système Revit de retour distinct, les extrémités
            // ouvertes jouent le rôle des points d'entrée implicites et le
            // retour choisi reste la sortie. On obtient ainsi un vrai trajet
            // complet au lieu d'animer arbitrairement tout le composant.
            for (int index = 0; index < connectorCount; index++)
            {
                int id = component[index];
                if (id < 0 || !componentIsReturnOnly[id] ||
                    explicitOutletSeeds.Contains(index) ||
                    !outletSeeds.Contains(index))
                {
                    continue;
                }
                outletSeeds.Remove(index);
                inletSeeds.Add(index);
                componentHasInlet[id] = true;
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
                    double totalWeight = 0.0;
                    foreach (int edgeIndex in _adjacentEdges![index])
                    {
                        if (!retained[edgeIndex])
                            continue;
                        GameMepConnectionData edge = _graph.Connections[edgeIndex];
                        int next = edge.ConnectorA == index
                            ? edge.ConnectorB
                            : edge.ConnectorA;
                        double weight = GetRelaxationWeight(edge);
                        total += potential[next] * weight;
                        totalWeight += weight;
                    }
                    if (totalWeight <= 1e-12)
                        continue;
                    double average = total / totalWeight;
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
                    // Invariant de rendu : le corps d'une vanne d'isolement
                    // fermée ne transporte jamais de particule, quel que soit
                    // le type de condition aux limites autour d'elle.
                    if (IsClosedIsolationValve(path.ElementKey))
                    {
                        path.HasCirculation = false;
                        path.DirectionReason =
                            "Vanne fermée : fluide stagnant";
                        continue;
                    }

                    if (path.FlowState != GameMepFlowState.Supplied ||
                        !IsRelevantConnector(path.StartConnector))
                    {
                        path.HasCirculation = false;
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
                    if (path.EndConnector >= 0 &&
                        diameterHeaderContinuityElementKeys.Contains(
                            path.ElementKey) &&
                        !TryGetImposedDirection(path, out _, out _) &&
                        diameterHeaderElementDirections.TryGetValue(
                            path.ElementKey, out bool headerForward))
                    {
                        // Corriger uniquement une contradiction. Un gros tronçon
                        // déjà cohérent garde ainsi sa raison et son sens initial.
                        path.HasCirculation = true;
                        if (path.DirectionState !=
                                GameMepDirectionState.Resolved ||
                            path.FlowForward != headerForward)
                        {
                            path.FlowForward = headerForward;
                            path.DirectionState =
                                GameMepDirectionState.Resolved;
                            bool geometricContinuity =
                                geometricHeaderContinuityElementKeys.Contains(
                                    path.ElementKey);
                            path.DirectionReason = geometricContinuity
                                ? "Continuité géométrique du collecteur à travers les tés"
                                : "Continuité du gros DN vers le retour aspiré";
                            if (!geometricContinuity)
                                _graph.DiameterDirectedPathCount++;
                        }
                    }
                    if (path.HasCirculation && path.EndConnector >= 0 &&
                        diameterInfluencedElementKeys.Contains(path.ElementKey) &&
                        (path.DirectionState != GameMepDirectionState.Resolved ||
                         diameterForcedElementKeys.Contains(path.ElementKey)) &&
                        !TryGetImposedDirection(path, out _, out _))
                    {
                        // La pondération DN doit atteindre les canalisations qui
                        // portent les grandes flèches visibles, et pas seulement
                        // les quelques centimètres dessinés dans le raccord.
                        path.FlowForward = difference > 0.0;
                        path.DirectionState = GameMepDirectionState.Resolved;
                        path.DirectionReason =
                            "Gradient propagé depuis une jonction de DN différents";
                        _graph.DiameterDirectedPathCount++;
                    }
                    if (path.EndConnector < 0 && element.IsPipeJunction &&
                        diameterActivatedJunctionKeys.Contains(element.Key) &&
                        diameterJunctionPortDirections.TryGetValue(
                            path.StartConnector, out bool junctionForward) &&
                        !TryGetImposedDirection(path, out _, out _))
                    {
                        path.HasCirculation = true;
                        path.FlowForward = junctionForward;
                        path.DirectionState = GameMepDirectionState.Resolved;
                        bool geometricContinuity =
                            geometricProtectedJunctionPorts.Contains(
                                path.StartConnector);
                        path.DirectionReason = geometricContinuity
                            ? (junctionForward
                                ? "Entrée du collecteur (continuité géométrique au té)"
                                : "Sortie du collecteur (continuité géométrique au té)")
                            : (junctionForward
                                ? "Piquage vers le collecteur (continuité du gros DN)"
                                : "Sortie du collecteur (continuité du gros DN)");
                    }
                    else if (path.HasCirculation && path.EndConnector < 0 &&
                        element.IsPipeJunction &&
                        diameterActivatedJunctionKeys.Contains(element.Key) &&
                        (path.DirectionState != GameMepDirectionState.Resolved ||
                         diameterForcedJunctionPorts.Contains(
                             path.StartConnector)))
                    {
                        // Les chemins d'un té/piquage vont du connecteur vers
                        // un centre géométrique virtuel. Le signe du gradient
                        // permet donc de représenter plusieurs injections qui
                        // convergent, ou plusieurs retours aspirés vers un même
                        // collecteur. Le même gradient est ensuite propagé aux
                        // canalisations automatiques du composant.
                        path.FlowForward = difference > 0.0;
                        path.DirectionState = GameMepDirectionState.Resolved;
                        path.DirectionReason = junctionPortWeights.ContainsKey(
                                element.Key)
                            ? (path.FlowForward
                                ? "Piquage vers le collecteur (sections/DN comparés)"
                                : "Sortie du collecteur (sections/DN comparés)")
                            : "Sens déduit au centre de la jonction";
                    }

                    bool hasProtectedHeaderDirection =
                        path.EndConnector >= 0 &&
                        diameterHeaderContinuityElementKeys.Contains(
                            path.ElementKey) &&
                        diameterHeaderElementDirections.ContainsKey(
                            path.ElementKey);
                    bool hasProtectedJunctionDirection =
                        path.EndConnector < 0 && element.IsPipeJunction &&
                        diameterActivatedJunctionKeys.Contains(element.Key) &&
                        diameterProtectedJunctionPorts.Contains(
                            path.StartConnector);
                    if (path.HasCirculation &&
                        !hasProtectedHeaderDirection &&
                        !hasProtectedJunctionDirection &&
                        !TryGetImposedDirection(path, out _, out _))
                    {
                        // Le calcul initial utilise les frontières les plus
                        // proches. Dans une boucle ou avec plusieurs retours,
                        // cette approximation peut créer une inversion isolée.
                        // Le potentiel relaxé représente le réseau complet : il
                        // devient donc l'autorité finale pour les flèches libres.
                        bool potentialForward = difference > 0.0;
                        if (path.DirectionState !=
                                GameMepDirectionState.Resolved ||
                            path.FlowForward != potentialForward)
                        {
                            path.FlowForward = potentialForward;
                            path.DirectionState =
                                GameMepDirectionState.Resolved;
                            path.DirectionReason =
                                "Gradient hydraulique final entre arrivée et retour";
                        }
                    }
                    if (!path.HasCirculation)
                    {
                        path.DirectionReason =
                            "Potentiel équilibré : fluide stagnant";
                        continue;
                    }

                    // Hors d'un composant contenant une jonction DN significative,
                    // les chemins classiques conservent le comportement historique.
                }
            }

            AlignTwoPortPipeFittings();
            AlignPipeJunctions();
            AlignNativePumpBranches();

            void AddArmPipeElements(
                IEnumerable<int> connectors,
                ISet<string> destination)
            {
                foreach (int connector in connectors)
                {
                    if (connector < 0 || connector >= connectorCount)
                        continue;
                    string ownerKey = _graph.Connectors[connector].ElementKey;
                    GameMepElementData? owner = _graph.FindElement(ownerKey);
                    // Un MEPCurve hôte peut lui-même représenter le piquage.
                    // Son chemin principal ne doit jamais être retourné par la
                    // petite branche raccordée dessus.
                    if (owner?.IsPipeCurve == true && !owner.IsPipeJunction)
                        destination.Add(ownerKey);
                }
            }

            void AddHeaderArmDirections(
                int headerPort,
                ISet<int> arm,
                bool portFlowsTowardCenter,
                IDictionary<string, bool> destination)
            {
                Dictionary<int, int> distances = BuildArmDistances(
                    headerPort, arm);

                foreach (GameMepElementData owner in arm.Where(index =>
                        index >= 0 && index < connectorCount)
                    .Select(index => _graph.FindElement(
                        _graph.Connectors[index].ElementKey))
                    .Where(item => item?.IsPipeCurve == true &&
                        !item.IsPipeJunction)
                    .Cast<GameMepElementData>()
                    .Distinct())
                {
                    GameMepPathData? path = owner.Paths.FirstOrDefault(item =>
                        item.EndConnector >= 0 &&
                        distances.ContainsKey(item.StartConnector) &&
                        distances.ContainsKey(item.EndConnector) &&
                        distances[item.StartConnector] !=
                            distances[item.EndConnector]);
                    if (path == null)
                        continue;
                    bool forwardTowardJunction =
                        distances[path.StartConnector] >
                        distances[path.EndConnector];
                    destination[owner.Key] = portFlowsTowardCenter
                        ? forwardTowardJunction
                        : !forwardTowardJunction;
                }
            }

            Dictionary<int, int> BuildArmDistances(
                int headerPort,
                ISet<int> arm)
            {
                var distances = new Dictionary<int, int>();
                var queue = new Queue<int>();
                distances[headerPort] = 0;
                queue.Enqueue(headerPort);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    foreach (int edgeIndex in _adjacentEdges![current])
                    {
                        if (edgeIndex < 0 || edgeIndex >= allowed.Length ||
                            !allowed[edgeIndex])
                        {
                            continue;
                        }
                        GameMepConnectionData edge =
                            _graph.Connections[edgeIndex];
                        if (edge.IsInternal && string.Equals(
                                edge.ElementKey,
                                _graph.Connectors[headerPort].ElementKey,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        int next = edge.ConnectorA == current
                            ? edge.ConnectorB
                            : edge.ConnectorA;
                        if (!arm.Contains(next) || distances.ContainsKey(next))
                            continue;
                        distances[next] = distances[current] + 1;
                        queue.Enqueue(next);
                    }
                }
                return distances;
            }

            void ExtendHeaderBackbone(
                string initialJunctionKey,
                int initialPort,
                bool portFlowsTowardCenter,
                bool geometricContinuity)
            {
                string currentJunctionKey = initialJunctionKey;
                int currentPort = initialPort;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (int depth = 0; depth < 64; depth++)
                {
                    string visitKey = currentJunctionKey + "|" + currentPort;
                    if (!visited.Add(visitKey) ||
                        !junctionArms.TryGetValue(currentJunctionKey,
                            out Dictionary<int, HashSet<int>> currentArms) ||
                        !junctionTerminalJunctions.TryGetValue(currentJunctionKey,
                            out Dictionary<int, string> currentTerminals) ||
                        !currentArms.TryGetValue(currentPort,
                            out HashSet<int> currentArm) ||
                        !currentTerminals.TryGetValue(currentPort,
                            out string terminalKey) ||
                        string.IsNullOrWhiteSpace(terminalKey))
                    {
                        return;
                    }

                    GameMepElementData? nextJunction =
                        _graph.FindElement(terminalKey);
                    if (nextJunction == null || !nextJunction.IsPipeJunction ||
                        !junctionArms.TryGetValue(terminalKey,
                            out Dictionary<int, HashSet<int>> nextArms) ||
                        !junctionSimpleArms.TryGetValue(terminalKey,
                            out Dictionary<int, bool> nextSimpleArms) ||
                        !junctionEffectiveAreas.TryGetValue(terminalKey,
                            out Dictionary<int, double> nextAreas))
                    {
                        return;
                    }

                    int[] incomingPorts = nextJunction.ConnectorIndices.Where(
                        index => index >= 0 && index < connectorCount &&
                            currentArm.Contains(index)).Distinct().ToArray();
                    if (incomingPorts.Length != 1)
                        return;
                    int incomingPort = incomingPorts[0];
                    int continuationPort = SelectBackboneContinuation(
                        nextJunction, incomingPort, nextAreas);
                    if (continuationPort < 0 ||
                        !nextSimpleArms.TryGetValue(
                            continuationPort, out bool continuationIsSimple) ||
                        !continuationIsSimple ||
                        !nextArms.TryGetValue(
                            continuationPort, out HashSet<int> continuationArm))
                    {
                        return;
                    }

                    bool incomingDirection = !portFlowsTowardCenter;
                    if ((diameterJunctionPortDirections.TryGetValue(
                                incomingPort, out bool existingIncoming) &&
                            existingIncoming != incomingDirection) ||
                        (diameterJunctionPortDirections.TryGetValue(
                                continuationPort, out bool existingContinuation) &&
                            existingContinuation != portFlowsTowardCenter))
                    {
                        return;
                    }

                    diameterActivatedJunctionKeys.Add(terminalKey);
                    diameterJunctionPortDirections[incomingPort] =
                        incomingDirection;
                    diameterJunctionPortDirections[continuationPort] =
                        portFlowsTowardCenter;
                    diameterProtectedJunctionPorts.Add(incomingPort);
                    diameterProtectedJunctionPorts.Add(continuationPort);
                    if (geometricContinuity)
                    {
                        geometricProtectedJunctionPorts.Add(incomingPort);
                        geometricProtectedJunctionPorts.Add(continuationPort);
                    }
                    foreach (int sidePort in nextJunction.ConnectorIndices.Where(
                        port => port != incomingPort &&
                            port != continuationPort))
                    {
                        if (!diameterJunctionPortDirections.ContainsKey(sidePort) &&
                            nextArms.TryGetValue(
                                sidePort, out HashSet<int> sideArm) &&
                            TryReadResolvedArmDirection(
                                sidePort, sideArm, out bool sideDirection))
                        {
                            // Le croisement peut porter un autre réseau latéral.
                            // Son petit chemin interne reprend son propre tuyau,
                            // sans propager le sens du collecteur sur cette branche.
                            diameterJunctionPortDirections[sidePort] = sideDirection;
                        }
                    }
                    AddArmPipeElements(
                        continuationArm,
                        diameterHeaderContinuityElementKeys);
                    if (geometricContinuity)
                    {
                        AddArmPipeElements(
                            continuationArm,
                            geometricHeaderContinuityElementKeys);
                    }
                    AddHeaderArmDirections(
                        continuationPort,
                        continuationArm,
                        portFlowsTowardCenter,
                        diameterHeaderElementDirections);

                    currentJunctionKey = terminalKey;
                    currentPort = continuationPort;
                }
            }

            bool TrySelectCollinearHeaderPorts(
                GameMepElementData junction,
                out int[] ports)
            {
                ports = Array.Empty<int>();
                int[] candidates = junction.ConnectorIndices.Where(index =>
                        index >= 0 && index < connectorCount &&
                        _graph.Connectors[index].HasDirection)
                    .Distinct().ToArray();
                if (candidates.Length != 3)
                    return false;

                var pairs = new List<Tuple<int, int, double>>();
                for (int first = 0; first < candidates.Length; first++)
                {
                    for (int second = first + 1; second < candidates.Length; second++)
                    {
                        pairs.Add(Tuple.Create(
                            candidates[first],
                            candidates[second],
                            Vector3D.DotProduct(
                                _graph.Connectors[candidates[first]].Direction,
                                _graph.Connectors[candidates[second]].Direction)));
                    }
                }
                Tuple<int, int, double>[] ordered = pairs
                    .OrderBy(pair => pair.Item3)
                    .ToArray();
                if (ordered.Length < 2 || ordered[0].Item3 > -0.75 ||
                    ordered[1].Item3 - ordered[0].Item3 < 0.2)
                {
                    return false;
                }
                ports = new[] { ordered[0].Item1, ordered[0].Item2 };
                return true;
            }

            int SelectBackboneContinuation(
                GameMepElementData junction,
                int incomingPort,
                IReadOnlyDictionary<int, double> effectiveAreas)
            {
                if (!effectiveAreas.TryGetValue(
                        incomingPort, out double incomingArea) ||
                    incomingArea <= 1e-12)
                {
                    return -1;
                }
                int[] candidates = junction.ConnectorIndices.Where(port =>
                        port != incomingPort && port >= 0 &&
                        port < connectorCount &&
                        effectiveAreas.TryGetValue(port, out double area) &&
                        area > 1e-12 &&
                        Math.Max(area, incomingArea) /
                            Math.Min(area, incomingArea) < significantAreaRatio)
                    .Distinct().ToArray();
                if (candidates.Length == 1)
                    return candidates[0];
                if (candidates.Length == 0 ||
                    !_graph.Connectors[incomingPort].HasDirection)
                {
                    return -1;
                }

                Vector3D incomingDirection =
                    _graph.Connectors[incomingPort].Direction;
                var aligned = candidates.Where(port =>
                        _graph.Connectors[port].HasDirection)
                    .Select(port => new
                    {
                        Port = port,
                        Dot = Vector3D.DotProduct(
                            incomingDirection,
                            _graph.Connectors[port].Direction)
                    })
                    .OrderBy(item => item.Dot)
                    .ToArray();
                if (aligned.Length == 0 || aligned[0].Dot > -0.5 ||
                    (aligned.Length > 1 &&
                     aligned[1].Dot - aligned[0].Dot < 0.2))
                {
                    return -1;
                }
                return aligned[0].Port;
            }

            static bool TryBuildHeaderDirectionsFromBoundary(
                int[] largePorts,
                int[] distances,
                out Dictionary<int, bool> directions)
            {
                directions = new Dictionary<int, bool>();
                if (largePorts.Length != 2 ||
                    largePorts.Any(port => port < 0 ||
                        port >= distances.Length || distances[port] < 0) ||
                    distances[largePorts[0]] == distances[largePorts[1]])
                {
                    return false;
                }
                directions[largePorts[0]] =
                    distances[largePorts[0]] > distances[largePorts[1]];
                directions[largePorts[1]] = !directions[largePorts[0]];
                return true;
            }

            static bool TryBuildHeaderDirectionsFromInlet(
                int[] headerPorts,
                int[] distances,
                out Dictionary<int, bool> directions)
            {
                directions = new Dictionary<int, bool>();
                if (headerPorts.Length != 2 ||
                    headerPorts.Any(port => port < 0 ||
                        port >= distances.Length || distances[port] < 0) ||
                    distances[headerPorts[0]] == distances[headerPorts[1]])
                {
                    return false;
                }
                directions[headerPorts[0]] =
                    distances[headerPorts[0]] < distances[headerPorts[1]];
                directions[headerPorts[1]] = !directions[headerPorts[0]];
                return true;
            }

            bool TryReadResolvedHeaderDirections(
                GameMepElementData junction,
                int[] largePorts,
                IReadOnlyDictionary<int, HashSet<int>> arms,
                out Dictionary<int, bool> directions)
            {
                directions = new Dictionary<int, bool>();
                foreach (int port in largePorts)
                {
                    if (arms.TryGetValue(port, out HashSet<int> arm) &&
                        TryReadResolvedArmDirection(
                            port, arm, out bool armDirection))
                    {
                        directions[port] = armDirection;
                        continue;
                    }

                    GameMepPathData? path = junction.Paths.FirstOrDefault(item =>
                        item.StartConnector == port && item.EndConnector < 0 &&
                        item.DirectionState == GameMepDirectionState.Resolved);
                    if (path != null)
                    {
                        directions[port] = path.FlowForward;
                        continue;
                    }

                    return false;
                }
                // Un collecteur continu possède exactement une entrée et une
                // sortie. Deux flèches vers le centre ou deux flèches vers
                // l'extérieur sont précisément le défaut à ne pas mémoriser.
                return directions.Values.Distinct().Count() == 2;
            }

            bool TryReadResolvedArmDirection(
                int port,
                ISet<int> arm,
                out bool portFlowsTowardCenter)
            {
                portFlowsTowardCenter = false;
                Dictionary<int, int> distances = BuildArmDistances(port, arm);
                GameMepPathData? visiblePath = arm.Where(index =>
                        index >= 0 && index < connectorCount)
                    .Select(index => _graph.FindElement(
                        _graph.Connectors[index].ElementKey))
                    .Where(item => item?.IsPipeCurve == true &&
                        !item.IsPipeJunction)
                    .Cast<GameMepElementData>()
                    .SelectMany(item => item.Paths)
                    .Where(item => item.EndConnector >= 0 &&
                        item.DirectionState == GameMepDirectionState.Resolved &&
                        distances.ContainsKey(item.StartConnector) &&
                        distances.ContainsKey(item.EndConnector) &&
                        distances[item.StartConnector] !=
                            distances[item.EndConnector])
                    .OrderBy(item => Math.Min(
                        distances[item.StartConnector],
                        distances[item.EndConnector]))
                    .FirstOrDefault();
                if (visiblePath == null)
                    return false;
                bool forwardTowardJunction =
                    distances[visiblePath.StartConnector] >
                    distances[visiblePath.EndConnector];
                portFlowsTowardCenter = visiblePath.FlowForward ==
                    forwardTowardJunction;
                return true;
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
                double center = GetJunctionCenterPotential(element, useful, values);
                difference = values[start] - center;
                return true;
            }

            double GetRelaxationWeight(GameMepConnectionData edge)
            {
                if (!edge.IsInternal || string.IsNullOrWhiteSpace(edge.ElementKey) ||
                    !junctionPortWeights.TryGetValue(
                        edge.ElementKey,
                        out Dictionary<int, double> weights) ||
                    !weights.TryGetValue(edge.ConnectorA, out double first) ||
                    !weights.TryGetValue(edge.ConnectorB, out double second))
                {
                    return 1.0;
                }

                // La moyenne géométrique conserve une liaison réelle entre le
                // piquage et le collecteur, tout en empêchant le petit DN de
                // retourner artificiellement le potentiel du gros DN.
                return Math.Sqrt(first * second);
            }

            double GetJunctionCenterPotential(
                GameMepElementData element,
                IList<int> useful,
                double[] values)
            {
                if (!junctionPortWeights.TryGetValue(
                        element.Key,
                        out Dictionary<int, double> weights))
                {
                    return useful.Average(index => values[index]);
                }

                double weightedTotal = 0.0;
                double totalWeight = 0.0;
                foreach (int index in useful)
                {
                    double weight = weights.TryGetValue(index, out double value)
                        ? value
                        : 1.0;
                    weightedTotal += values[index] * weight;
                    totalWeight += weight;
                }
                return totalWeight > 1e-12
                    ? weightedTotal / totalWeight
                    : useful.Average(index => values[index]);
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

        private bool IsReturnHydronic(GameMepElementData element)
        {
            string classification = element.Classification ?? string.Empty;
            if (classification.IndexOf(
                    "ReturnHydronic",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            GameMepSystemData? system = _graph.FindSystem(element.SystemKey);
            return (system?.Classification ?? string.Empty).IndexOf(
                "ReturnHydronic",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsStrictlyCloserToBoundary(
            int candidate,
            IEnumerable<int> otherPorts,
            int[] distances)
        {
            if (candidate < 0 || candidate >= distances.Length ||
                distances[candidate] < 0)
            {
                return false;
            }

            int comparisonCount = 0;
            foreach (int other in otherPorts)
            {
                if (other < 0 || other >= distances.Length ||
                    distances[other] < 0 ||
                    distances[candidate] >= distances[other])
                {
                    return false;
                }
                comparisonCount++;
            }
            return comparisonCount > 0;
        }

        private bool TryCollectJunctionArm(
            GameMepElementData junction,
            int startConnector,
            bool[] allowedEdges,
            ISet<int> explicitInletSeeds,
            ISet<int> explicitOutletSeeds,
            ISet<int> implicitOutletSeeds,
            ISet<string> restrictedElementKeys,
            out HashSet<int> arm,
            out double effectiveArea,
            out string terminalJunctionKey)
        {
            arm = new HashSet<int>();
            terminalJunctionKey = string.Empty;
            effectiveArea = startConnector >= 0 &&
                startConnector < _graph.Connectors.Count
                    ? _graph.Connectors[startConnector].CrossSectionArea
                    : 0.0;
            if (startConnector < 0 || startConnector >= _graph.Connectors.Count ||
                _adjacentEdges == null)
            {
                return false;
            }

            bool simple = true;
            int nearestPipeDistance = int.MaxValue;
            var traversedEdges = new HashSet<int>();
            var queue = new Queue<int>();
            var distance = new Dictionary<int, int>();
            arm.Add(startConnector);
            distance[startConnector] = 0;
            queue.Enqueue(startConnector);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                GameMepConnectorData connector = _graph.Connectors[current];
                GameMepElementData? owner =
                    _graph.FindElement(connector.ElementKey);

                // Le connecteur du té peut conserver le diamètre nominal avant
                // un réducteur. Le premier MEPCurve rencontré sur le bras porte
                // le DN effectif du tronçon réellement dessiné par l'utilisateur.
                // Pour un piquage natif, le MEPCurve hôte est aussi la jonction.
                // Ne pas utiliser ses autres ports comme aire de repli : un port
                // Curve sans DN hériterait sinon à tort du DN du collecteur.
                if (owner?.IsPipeCurve == true &&
                    !string.Equals(owner.Key, junction.Key, StringComparison.Ordinal) &&
                    distance.TryGetValue(current, out int currentDistance))
                {
                    double pipeArea = connector.CrossSectionArea;
                    if (pipeArea <= 1e-12)
                    {
                        pipeArea = owner.ConnectorIndices.Where(index =>
                                index >= 0 && index < _graph.Connectors.Count)
                            .Select(index =>
                                _graph.Connectors[index].CrossSectionArea)
                            .Where(area => area > 1e-12)
                            .DefaultIfEmpty(0.0)
                            .Max();
                    }
                    if (pipeArea > 1e-12 &&
                        currentDistance <= nearestPipeDistance)
                    {
                        if (currentDistance < nearestPipeDistance)
                            effectiveArea = pipeArea;
                        else
                            effectiveArea = Math.Max(effectiveArea, pipeArea);
                        nearestPipeDistance = currentDistance;
                    }
                }

                if (current != startConnector && owner?.IsPipeJunction == true)
                {
                    // Une seconde jonction est une frontière locale sûre : le
                    // gradient peut atteindre ce segment sans traverser le té
                    // suivant. Son rôle aval reste lisible via lowDistance.
                    if (string.IsNullOrWhiteSpace(terminalJunctionKey))
                        terminalJunctionKey = owner.Key;
                    else if (!string.Equals(
                            terminalJunctionKey,
                            owner.Key,
                            StringComparison.Ordinal))
                    {
                        simple = false;
                    }
                    continue;
                }

                bool isBoundary = current != startConnector &&
                    (explicitInletSeeds.Contains(current) ||
                     explicitOutletSeeds.Contains(current) ||
                     implicitOutletSeeds.Contains(current));
                if (isBoundary)
                    continue;

                if (owner != null && !string.Equals(
                        owner.Key, junction.Key, StringComparison.Ordinal) &&
                    restrictedElementKeys.Contains(owner.Key))
                {
                    // Une pompe est une frontière directionnelle,
                    // pas une raison d'invalider tout le bras. L'analyse locale
                    // s'arrête ici et TryGetImposedDirection garde la priorité.
                    continue;
                }

                var candidates = new List<int>();
                foreach (int edgeIndex in _adjacentEdges[current])
                {
                    if (edgeIndex < 0 || edgeIndex >= allowedEdges.Length ||
                        !allowedEdges[edgeIndex])
                    {
                        continue;
                    }
                    GameMepConnectionData edge = _graph.Connections[edgeIndex];
                    if (edge.IsInternal && string.Equals(
                            edge.ElementKey, junction.Key,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    candidates.Add(edgeIndex);
                }
                if (candidates.Count > 2)
                {
                    simple = false;
                    continue;
                }

                foreach (int edgeIndex in candidates)
                {
                    GameMepConnectionData edge = _graph.Connections[edgeIndex];
                    int next = edge.ConnectorA == current
                        ? edge.ConnectorB
                        : edge.ConnectorA;
                    if (next < 0 || next >= _graph.Connectors.Count)
                    {
                        simple = false;
                        continue;
                    }
                    traversedEdges.Add(edgeIndex);
                    if (junction.ConnectorIndices.Contains(next) &&
                        next != startConnector)
                    {
                        // Les deux bras se rejoignent hors du chemin interne du
                        // té : c'est un bypass, pas une fusion déductible du DN.
                        simple = false;
                        continue;
                    }
                    if (arm.Add(next))
                    {
                        distance[next] = distance[current] + 1;
                        queue.Enqueue(next);
                    }
                }
            }

            // Pour une chaîne simple, E = V - 1. Une arête supplémentaire
            // révèle une boucle même si elle rejoint le bras loin du raccord.
            if (traversedEdges.Count >= arm.Count)
                simple = false;
            return simple;
        }

        private void AlignTwoPortPipeFittings()
        {
            if (_adjacentEdges == null)
                return;

            GameMepElementData[] fittings = _graph.Elements.Where(
                    IsPassiveTwoPortComponent)
                .ToArray();
            for (int pass = 0; pass < fittings.Length; pass++)
            {
                bool changed = false;
                foreach (GameMepElementData fitting in fittings)
                {
                    GameMepPathData? path = fitting.Paths.FirstOrDefault(item =>
                        item.EndConnector >= 0 &&
                        fitting.ConnectorIndices.Contains(item.StartConnector) &&
                        fitting.ConnectorIndices.Contains(item.EndConnector));
                    if (path == null ||
                        path.FlowState != GameMepFlowState.Supplied ||
                        IsClosedIsolationValve(path.ElementKey) ||
                        TryGetImposedDirection(path, out _, out _))
                    {
                        continue;
                    }

                    bool hasStart = TryReadExternalFlowTowardFitting(
                        path.StartConnector,
                        fitting.Key,
                        out bool startFlowsTowardFitting,
                        out bool startIsAuthoritative);
                    bool hasEnd = TryReadExternalFlowTowardFitting(
                        path.EndConnector,
                        fitting.Key,
                        out bool endFlowsTowardFitting,
                        out bool endIsAuthoritative);

                    bool hasAuthoritativeStart = hasStart &&
                        startIsAuthoritative;
                    bool hasAuthoritativeEnd = hasEnd && endIsAuthoritative;
                    if (!hasAuthoritativeStart && !hasAuthoritativeEnd)
                        continue;

                    bool forwardFromStart = startFlowsTowardFitting;
                    bool forwardFromEnd = !endFlowsTowardFitting;
                    if (hasAuthoritativeStart && hasAuthoritativeEnd &&
                        forwardFromStart != forwardFromEnd)
                    {
                        continue;
                    }

                    bool forward = hasAuthoritativeStart
                        ? forwardFromStart
                        : forwardFromEnd;
                    if (path.FlowForward != forward ||
                        path.DirectionState != GameMepDirectionState.Resolved ||
                        !string.Equals(path.DirectionReason,
                            TwoPortFittingContinuityReason,
                            StringComparison.Ordinal))
                    {
                        changed = true;
                    }

                    path.FlowForward = forward;
                    path.HasCirculation = true;
                    path.DirectionState = GameMepDirectionState.Resolved;
                    path.DirectionReason = TwoPortFittingContinuityReason;
                }
                if (!changed)
                    break;
            }
        }

        private void AlignPipeJunctions()
        {
            if (_adjacentEdges == null)
                return;

            foreach (GameMepElementData junction in _graph.Elements.Where(item =>
                item.IsPipeJunction && item.ConnectorIndices.Count >= 3))
            {
                foreach (GameMepPathData path in junction.Paths)
                {
                    if (path.FlowState != GameMepFlowState.Supplied ||
                        IsClosedIsolationValve(path.ElementKey) ||
                        TryGetImposedDirection(path, out _, out _))
                    {
                        continue;
                    }

                    bool hasStart = TryReadExternalFlowTowardFitting(
                        path.StartConnector,
                        junction.Key,
                        out bool startFlowsTowardJunction,
                        out bool startIsAuthoritative);
                    if (path.EndConnector < 0)
                    {
                        if (!hasStart || !startIsAuthoritative)
                            continue;
                        bool changed = path.FlowForward !=
                                startFlowsTowardJunction ||
                            path.DirectionState != GameMepDirectionState.Resolved;
                        path.FlowForward = startFlowsTowardJunction;
                        path.HasCirculation = true;
                        path.DirectionState = GameMepDirectionState.Resolved;
                        if (changed)
                            path.DirectionReason = PipeJunctionContinuityReason;
                        continue;
                    }

                    bool hasEnd = TryReadExternalFlowTowardFitting(
                        path.EndConnector,
                        junction.Key,
                        out bool endFlowsTowardJunction,
                        out bool endIsAuthoritative);
                    bool authoritativeStart = hasStart && startIsAuthoritative;
                    bool authoritativeEnd = hasEnd && endIsAuthoritative;
                    if (!authoritativeStart && !authoritativeEnd)
                        continue;

                    bool forwardFromStart = startFlowsTowardJunction;
                    bool forwardFromEnd = !endFlowsTowardJunction;
                    if (authoritativeStart && authoritativeEnd &&
                        forwardFromStart != forwardFromEnd)
                    {
                        continue;
                    }
                    bool forward = authoritativeStart
                        ? forwardFromStart
                        : forwardFromEnd;
                    bool directionChanged = path.FlowForward != forward ||
                        path.DirectionState != GameMepDirectionState.Resolved;
                    path.FlowForward = forward;
                    path.HasCirculation = true;
                    path.DirectionState = GameMepDirectionState.Resolved;
                    if (directionChanged)
                        path.DirectionReason = PipeJunctionContinuityReason;
                }
            }
        }

        private void AlignNativePumpBranches()
        {
            if (_adjacentEdges == null)
                return;

            foreach (GameMepElementData pump in _graph.Elements)
            {
                if (!GameMepEquipmentDirectionPolicy.TryGetNativePumpDirection(
                        _graph, pump, out int entryConnector,
                        out int exitConnector))
                {
                    continue;
                }

                PropagateNativePumpPort(
                    pump.Key, entryConnector, isSuction: true);
                PropagateNativePumpPort(
                    pump.Key, exitConnector, isSuction: false);
                PropagateNativePumpDischargeNetwork(
                    pump.Key, exitConnector);
            }
        }

        private void PropagateNativePumpDischargeNetwork(
            string pumpKey,
            int exitConnector)
        {
            var pending = new Queue<KeyValuePair<int, string>>();
            pending.Enqueue(new KeyValuePair<int, string>(
                exitConnector, pumpKey));
            var visitedElements = new HashSet<string>(StringComparer.Ordinal)
            {
                pumpKey
            };

            while (pending.Count > 0)
            {
                KeyValuePair<int, string> step = pending.Dequeue();
                if (!TryGetSingleExternalNeighbor(
                        step.Key, step.Value, out int neighborConnector) ||
                    neighborConnector < 0 ||
                    neighborConnector >= _graph.Connectors.Count)
                {
                    continue;
                }

                GameMepElementData? neighbor = _graph.FindElement(
                    _graph.Connectors[neighborConnector].ElementKey);
                if (neighbor == null || !visitedElements.Add(neighbor.Key))
                    continue;

                // Une autre pompe est une nouvelle frontière de pression. Le
                // refoulement courant ne doit jamais la traverser à rebours.
                if (GameMepEquipmentDirectionPolicy.TryGetNativePumpDirection(
                        _graph, neighbor, out _, out _))
                {
                    continue;
                }

                if (neighbor.IsPipeJunction &&
                    neighbor.ConnectorIndices.Count >= 3)
                {
                    foreach (GameMepPathData junctionPath in neighbor.Paths)
                    {
                        if (junctionPath.EndConnector >= 0 ||
                            junctionPath.FlowState != GameMepFlowState.Supplied ||
                            !junctionPath.HasCirculation ||
                            TryGetImposedDirection(junctionPath, out _, out _))
                        {
                            continue;
                        }

                        bool isArrivalPort = junctionPath.StartConnector ==
                            neighborConnector;
                        junctionPath.FlowForward = isArrivalPort;
                        junctionPath.DirectionState =
                            GameMepDirectionState.Resolved;
                        junctionPath.DirectionReason =
                            NativePumpDischargeContinuityReason;

                        if (!isArrivalPort)
                        {
                            pending.Enqueue(new KeyValuePair<int, string>(
                                junctionPath.StartConnector,
                                neighbor.Key));
                        }
                    }
                    continue;
                }

                bool canPropagate = neighbor.IsPipeCurve ||
                    IsPassiveTwoPortComponent(neighbor);
                GameMepPathData? path = neighbor.Paths.FirstOrDefault(item =>
                    item.EndConnector >= 0 &&
                    (item.StartConnector == neighborConnector ||
                     item.EndConnector == neighborConnector));
                if (!canPropagate || path == null ||
                    path.FlowState != GameMepFlowState.Supplied ||
                    !path.HasCirculation ||
                    IsClosedIsolationValve(neighbor.Key) ||
                    TryGetImposedDirection(path, out _, out _))
                {
                    continue;
                }

                bool nearPumpIsStart =
                    path.StartConnector == neighborConnector;
                path.FlowForward = nearPumpIsStart;
                path.DirectionState = GameMepDirectionState.Resolved;
                path.DirectionReason = NativePumpDischargeContinuityReason;
                int farConnector = nearPumpIsStart
                    ? path.EndConnector
                    : path.StartConnector;
                pending.Enqueue(new KeyValuePair<int, string>(
                    farConnector, neighbor.Key));
            }
        }

        private void PropagateNativePumpPort(
            string pumpKey,
            int pumpConnector,
            bool isSuction)
        {
            if (_adjacentEdges == null || pumpConnector < 0 ||
                pumpConnector >= _graph.Connectors.Count)
            {
                return;
            }

            string reason = isSuction
                ? NativePumpSuctionContinuityReason
                : NativePumpDischargeContinuityReason;
            int currentConnector = pumpConnector;
            string currentElementKey = pumpKey;
            var visitedElements = new HashSet<string>(StringComparer.Ordinal)
            {
                pumpKey
            };

            while (TryGetSingleExternalNeighbor(
                currentConnector,
                currentElementKey,
                out int neighborConnector))
            {
                if (neighborConnector < 0 ||
                    neighborConnector >= _graph.Connectors.Count)
                {
                    return;
                }

                GameMepElementData? neighbor = _graph.FindElement(
                    _graph.Connectors[neighborConnector].ElementKey);
                if (neighbor == null || !visitedElements.Add(neighbor.Key))
                    return;

                if (neighbor.IsPipeJunction &&
                    neighbor.ConnectorIndices.Count >= 3)
                {
                    GameMepPathData? junctionPath = neighbor.Paths.FirstOrDefault(
                        item => item.StartConnector == neighborConnector &&
                            item.EndConnector < 0);
                    if (junctionPath == null ||
                        junctionPath.FlowState != GameMepFlowState.Supplied ||
                        !junctionPath.HasCirculation ||
                        TryGetImposedDirection(junctionPath, out _, out _))
                    {
                        return;
                    }

                    // Sur l'aspiration, le fluide quitte le centre du té vers
                    // la pompe. Au refoulement, il entre dans le té depuis la
                    // pompe. La propagation s'arrête à ce premier embranchement.
                    junctionPath.FlowForward = !isSuction;
                    junctionPath.DirectionState =
                        GameMepDirectionState.Resolved;
                    junctionPath.DirectionReason = reason;
                    return;
                }

                bool canPropagate = neighbor.IsPipeCurve ||
                    IsPassiveTwoPortComponent(neighbor);
                GameMepPathData? path = neighbor.Paths.FirstOrDefault(item =>
                    item.EndConnector >= 0 &&
                    (item.StartConnector == neighborConnector ||
                     item.EndConnector == neighborConnector));
                if (!canPropagate || path == null ||
                    path.FlowState != GameMepFlowState.Supplied ||
                    !path.HasCirculation ||
                    IsClosedIsolationValve(neighbor.Key) ||
                    TryGetImposedDirection(path, out _, out _))
                {
                    return;
                }

                bool nearPumpIsStart =
                    path.StartConnector == neighborConnector;
                path.FlowForward = isSuction
                    ? !nearPumpIsStart
                    : nearPumpIsStart;
                path.DirectionState = GameMepDirectionState.Resolved;
                path.DirectionReason = reason;

                currentConnector = nearPumpIsStart
                    ? path.EndConnector
                    : path.StartConnector;
                currentElementKey = neighbor.Key;
            }
        }

        private bool TryGetSingleExternalNeighbor(
            int connector,
            string ownerKey,
            out int neighborConnector)
        {
            neighborConnector = -1;
            if (_adjacentEdges == null || connector < 0 ||
                connector >= _adjacentEdges.Length)
            {
                return false;
            }

            foreach (int edgeIndex in _adjacentEdges[connector])
            {
                GameMepConnectionData edge = _graph.Connections[edgeIndex];
                if (edge.IsInternal || string.Equals(
                        edge.ElementKey, ownerKey, StringComparison.Ordinal) ||
                    !IsSystemCompatibleEdge(edge) ||
                    IsBlockedByFlowControl(edge))
                {
                    continue;
                }

                int candidate = edge.ConnectorA == connector
                    ? edge.ConnectorB
                    : edge.ConnectorA;
                if (neighborConnector >= 0 && candidate != neighborConnector)
                    return false;
                neighborConnector = candidate;
            }
            return neighborConnector >= 0;
        }

        private bool TryReadExternalFlowTowardFitting(
            int fittingConnector,
            string fittingKey,
            out bool flowsTowardFitting,
            out bool isAuthoritative)
        {
            flowsTowardFitting = false;
            isAuthoritative = false;
            if (_adjacentEdges == null || fittingConnector < 0 ||
                fittingConnector >= _adjacentEdges.Length)
            {
                return false;
            }

            bool found = false;
            foreach (int edgeIndex in _adjacentEdges[fittingConnector])
            {
                GameMepConnectionData edge = _graph.Connections[edgeIndex];
                if (edge.IsInternal || string.Equals(
                        edge.ElementKey, fittingKey, StringComparison.Ordinal))
                {
                    continue;
                }

                int neighborConnector = edge.ConnectorA == fittingConnector
                    ? edge.ConnectorB
                    : edge.ConnectorA;
                if (neighborConnector < 0 ||
                    neighborConnector >= _graph.Connectors.Count)
                {
                    continue;
                }

                GameMepElementData? neighbor = _graph.FindElement(
                    _graph.Connectors[neighborConnector].ElementKey);
                IEnumerable<GameMepPathData> neighborPaths = neighbor?.Paths ??
                    Enumerable.Empty<GameMepPathData>();
                foreach (GameMepPathData neighborPath in neighborPaths.Where(item =>
                    item.HasCirculation &&
                    item.DirectionState == GameMepDirectionState.Resolved &&
                    (item.StartConnector == neighborConnector ||
                     item.EndConnector == neighborConnector)))
                {
                    bool current = neighborPath.StartConnector ==
                            neighborConnector
                        ? !neighborPath.FlowForward
                        : neighborPath.FlowForward;
                    if (found && flowsTowardFitting != current)
                        return false;
                    flowsTowardFitting = current;
                    isAuthoritative |= !IsPassiveTwoPortComponent(neighbor) ||
                        TryGetImposedDirection(neighborPath, out _, out _) ||
                        string.Equals(
                            neighborPath.DirectionReason,
                            TwoPortFittingContinuityReason,
                            StringComparison.Ordinal);
                    found = true;
                }
            }
            return found;
        }

        private static bool IsPassiveTwoPortComponent(
            GameMepElementData? element)
        {
            if (element == null || element.IsPipeJunction ||
                element.ConnectorIndices.Count != 2 ||
                element.Paths.Count != 1)
            {
                return false;
            }

            // Revit classe les coudes/réductions comme raccords, mais les
            // brides, manchons et vannes comme accessoires de canalisation.
            // Hydrauliquement, lorsqu'ils sont ouverts et sans sens imposé,
            // tous sont des composants passifs à deux ports : ils doivent
            // prolonger le sens des canalisations, jamais le retourner.
            if (element.IsPipeFitting)
                return true;
            string category = element.Category ?? string.Empty;
            bool isPipeAccessory = category.IndexOf(
                    "Accessoire de canalisation",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                category.IndexOf(
                    "Pipe Accessor",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            return isPipeAccessory;
        }

        private bool IsClosedIsolationValve(string elementKey)
        {
            GameMepValveData? valve = _graph.FindValve(elementKey);
            return valve != null && valve.IsEnabledAsValve &&
                valve.IsClosed &&
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
                if (!GameMepBoundaryPolicy.IsUsable(sourceElement, source))
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
                        IsBlockedByFlowControl(edge))
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
            return GameMepSystemTraversalPolicy.CanTraverse(_graph, edge);
        }

        private bool IsBlockedByFlowControl(GameMepConnectionData edge)
        {
            if (!edge.IsInternal || !edge.IsValveGateCandidate)
                return false;
            GameMepValveData? valve = _graph.FindValve(edge.ElementKey);
            if (valve == null || !valve.IsEnabledAsValve)
                return false;
            return valve.IsClosed;
        }
    }
}

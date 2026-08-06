using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using MediaColor = System.Windows.Media.Color;

namespace BIMaestro.VideoGames
{
    /// <summary>
    /// Lit toutes les informations Revit pendant l'exécution de la commande et
    /// retourne exclusivement des DTO. La fenêtre jouable ne conserve ainsi
    /// aucun Element ni Connector Revit.
    /// </summary>
    internal static class RevitGameMepExtractor
    {
        private const string UnassignedSystemKey = "MEP|NON_AFFECTE";

        private sealed class RawConnector
        {
            public Connector Connector { get; set; } = null!;
            public int GraphIndex { get; set; }
            public string Key { get; set; } = string.Empty;
        }

        private sealed class SourceScore
        {
            public GameMepElementData Element { get; set; } = null!;
            public int Score { get; set; }
            public bool IsBaseEquipment { get; set; }
        }

        public static GameMepGraphData Extract(Document document, GameSceneData scene)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (scene == null) throw new ArgumentNullException(nameof(scene));

            var stopwatch = Stopwatch.StartNew();
            var graph = new GameMepGraphData
            {
                DocumentTitle = document.Title ?? string.Empty
            };
            try
            {
                ConfigureScenarioIdentity(document, graph);
            }
            catch (Exception exception)
            {
                // L'identité sert uniquement à la sauvegarde locale. Elle ne
                // doit jamais empêcher l'extraction fonctionnelle du réseau.
                graph.ScenarioCanPersist = false;
                graph.ScenarioModelKey = string.Empty;
                graph.ScenarioPersistenceError = exception.Message;
                GameRuntimeDiagnostics.Write(
                    "Identité persistante MEP indisponible, extraction poursuivie",
                    exception);
            }
            var visibleElementKeys = new HashSet<string>(
                scene.Elements.Select(element => element.Key),
                StringComparer.Ordinal);
            var connectorByKey = new Dictionary<string, RawConnector>(StringComparer.Ordinal);
            var rawConnectors = new List<RawConnector>();
            var sourceScores = new List<SourceScore>();

            foreach (Element element in CollectCandidates(document))
            {
                IList<Connector> connectors = GetPipingConnectors(element);
                if (connectors.Count == 0)
                    continue;

                string elementKey = CreateElementKey(document, element.Id);
                var elementData = new GameMepElementData
                {
                    Key = elementKey,
                    PersistentId = SafeText(() => element.UniqueId),
                    ElementId = element.Id.GetIdLongValue(),
                    Name = SafeText(() => element.Name),
                    Category = SafeText(() => element.Category?.Name),
                    TypeName = GetTypeName(document, element),
                    IsPipeCurve = element is MEPCurve,
                    IsVisible = visibleElementKeys.Contains(elementKey)
                };

                var connectorSystems = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Connector connector in connectors)
                {
                    Point3D origin = ToPoint(connector.Origin);
                    string connectorKey = CreateConnectorKey(element.Id, connector, origin);
                    GetSystemMetadata(
                        document,
                        connector,
                        graph,
                        out string systemKey,
                        out string systemName,
                        out string classification);

                    int connectorIndex = graph.Connectors.Count;
                    var connectorData = new GameMepConnectorData
                    {
                        Index = connectorIndex,
                        Key = connectorKey,
                        PersistentKey = SafeGet(
                            () => CreatePersistentConnectorKey(
                                element,
                                connector,
                                origin),
                            string.Empty),
                        ElementKey = elementKey,
                        SystemKey = systemKey,
                        Position = origin,
                        IsConnected = SafeGet(() => connector.IsConnected, false),
                        FlowDirection = SafeText(() => connector.Direction.ToString())
                    };
                    SetConnectorDirection(connector, connectorData);
                    graph.Connectors.Add(connectorData);
                    elementData.ConnectorIndices.Add(connectorIndex);

                    if (!connectorSystems.ContainsKey(systemKey))
                        connectorSystems[systemKey] = 0;
                    connectorSystems[systemKey]++;
                    if (string.IsNullOrWhiteSpace(elementData.SystemName) &&
                        !string.IsNullOrWhiteSpace(systemName))
                    {
                        elementData.SystemName = systemName;
                        elementData.Classification = classification;
                    }

                    var raw = new RawConnector
                    {
                        Connector = connector,
                        GraphIndex = connectorIndex,
                        Key = connectorKey
                    };
                    rawConnectors.Add(raw);
                    connectorByKey[connectorKey] = raw;
                }

                elementData.SystemKey = connectorSystems.Count == 0
                    ? UnassignedSystemKey
                    : connectorSystems
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                        .First().Key;
                GameMepSystemData? primarySystem = graph.FindSystem(elementData.SystemKey);
                if (primarySystem != null)
                {
                    elementData.SystemName = primarySystem.Name;
                    elementData.Classification = primarySystem.Classification;
                    primarySystem.ElementCount++;
                }

                GameMepValveData? valve = DetectValve(
                    element,
                    elementData,
                    connectors.Count,
                    graph);
                if (valve != null)
                    graph.Valves.Add(valve);

                BuildPaths(element, elementData, graph);
                BuildInternalConnections(elementData, valve, graph);
                graph.Elements.Add(elementData);

                SourceScore? sourceScore = ScoreSource(element, elementData, connectors);
                if (sourceScore != null)
                    sourceScores.Add(sourceScore);
            }

            BuildPhysicalConnections(rawConnectors, connectorByKey, graph);
            ResolveUnassignedSystems(graph);
            BuildSources(sourceScores, graph);
            graph.OpenConnectorCount = graph.Connectors.Count(connector => !connector.IsConnected);
            graph.RebuildIndexes();
            stopwatch.Stop();
            graph.ExtractionMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return graph;
        }

        private static IEnumerable<Element> CollectCandidates(Document document)
        {
            var byId = new Dictionary<long, Element>();
            // Certaines bibliothèques MEP rangent des composants raccordés
            // dans une catégorie personnalisée. Le domaine des connecteurs,
            // et non le nom de catégorie, reste donc l'arbitre final.
            foreach (Element element in new FilteredElementCollector(document)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType())
            {
                byId[element.Id.GetIdLongValue()] = element;
            }

            // Ne pas créer ici un tableau BuiltInCategory à partir de constantes.
            // Revit 2023 expose cette énumération sur 32 bits, tandis que Revit
            // 2024+ la charge sur 64 bits. Un tableau préinitialisé dans une DLL
            // compilée en 2023 provoque alors RuntimeHelpers.InitializeArray :
            // « La valeur n'est pas comprise dans la plage attendue ».
            // La résolution par nom construit chaque valeur avec le type exact
            // fourni par la version de Revit qui héberge la DLL.
            string[] categoryNames =
            {
                "OST_PipeCurves",
                "OST_FlexPipeCurves",
                "OST_PipeFitting",
                "OST_PipeAccessory",
                "OST_MechanicalEquipment",
                "OST_PlumbingFixtures",
                "OST_Sprinklers"
            };
            foreach (string categoryName in categoryNames)
            {
                try
                {
                    var category = (BuiltInCategory)Enum.Parse(
                        typeof(BuiltInCategory),
                        categoryName,
                        false);
                    foreach (Element element in new FilteredElementCollector(document)
                        .OfCategory(category)
                        .WhereElementIsNotElementType())
                    {
                        byId[element.Id.GetIdLongValue()] = element;
                    }
                }
                catch
                {
                    // Une catégorie indisponible dans un gabarit ne doit pas
                    // empêcher l'analyse des autres réseaux.
                }
            }

            return byId.Values.OrderBy(element => element.Id.GetIdLongValue()).ToList();
        }

        private static IList<Connector> GetPipingConnectors(Element element)
        {
            ConnectorManager? manager = null;
            try
            {
                if (element is MEPCurve curve)
                    manager = curve.ConnectorManager;
                else if (element is FamilyInstance family)
                    manager = family.MEPModel?.ConnectorManager;
                else
                    manager = GetConnectorManagerByReflection(element);
            }
            catch { }

            var result = new List<Connector>();
            if (manager?.Connectors == null)
                return result;

            try
            {
                foreach (Connector connector in manager.Connectors)
                {
                    if (connector == null || connector.Domain != Domain.DomainPiping)
                        continue;
                    if (connector.ConnectorType == ConnectorType.Logical)
                        continue;
                    result.Add(connector);
                }
            }
            catch { }

            return result
                .OrderBy(connector => SafeGet(() => connector.Id, int.MaxValue))
                .ThenBy(connector => SafeGet(() => connector.Origin.X, 0.0))
                .ThenBy(connector => SafeGet(() => connector.Origin.Y, 0.0))
                .ThenBy(connector => SafeGet(() => connector.Origin.Z, 0.0))
                .ToList();
        }

        private static ConnectorManager? GetConnectorManagerByReflection(Element element)
        {
            try
            {
                PropertyInfo? property = element.GetType().GetProperty("ConnectorManager");
                return property?.GetValue(element, null) as ConnectorManager;
            }
            catch
            {
                return null;
            }
        }

        private static void GetSystemMetadata(
            Document document,
            Connector connector,
            GameMepGraphData graph,
            out string systemKey,
            out string systemName,
            out string classification)
        {
            MEPSystem? system = null;
            try { system = connector.MEPSystem; } catch { }
            if (system == null)
            {
                systemKey = UnassignedSystemKey;
                systemName = "Réseau non affecté";
                classification = "Canalisation";
            }
            else
            {
                systemKey = "MEP|" + system.Id.GetIdLongValue();
                systemName = SafeText(() => system.Name);
                classification = GetSystemClassification(document, system);
            }

            if (graph.FindSystem(systemKey) != null)
                return;

            graph.Systems.Add(new GameMepSystemData
            {
                Key = systemKey,
                Name = string.IsNullOrWhiteSpace(systemName)
                    ? "Réseau sans nom"
                    : systemName,
                Classification = string.IsNullOrWhiteSpace(classification)
                    ? "Canalisation"
                    : classification,
                Color = GetSystemColor(document, system, systemName, classification)
            });
            graph.RebuildIndexes();
        }

        private static string GetSystemClassification(Document document, MEPSystem system)
        {
            try
            {
                Element? systemType = document.GetElement(system.GetTypeId());
                if (systemType != null)
                {
                    PropertyInfo? property = systemType.GetType().GetProperty("SystemClassification");
                    object? value = property?.GetValue(systemType, null);
                    if (value != null)
                        return value.ToString() ?? string.Empty;
                    return systemType.Name ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        private static MediaColor GetSystemColor(
            Document document,
            MEPSystem? system,
            string systemName,
            string classification)
        {
            try
            {
                if (system != null)
                {
                    Element? systemType = document.GetElement(system.GetTypeId());
                    PropertyInfo? property = systemType?.GetType().GetProperty("LineColor");
                    object? value = property?.GetValue(systemType, null);
                    if (value is Autodesk.Revit.DB.Color revitColor && revitColor.IsValid)
                        return MediaColor.FromRgb(revitColor.Red, revitColor.Green, revitColor.Blue);
                }
            }
            catch { }

            string text = (systemName + " " + classification).ToLowerInvariant();
            if (ContainsAny(text, "chaud", "chauff", "ecs", "hot", "heating", "vapeur"))
                return MediaColor.FromRgb(255, 96, 48);
            if (ContainsAny(text, "froid", "glac", "cold", "chilled", "ef", "rafra"))
                return MediaColor.FromRgb(30, 181, 255);

            MediaColor[] palette =
            {
                MediaColor.FromRgb(54, 211, 153),
                MediaColor.FromRgb(255, 196, 87),
                MediaColor.FromRgb(190, 115, 255),
                MediaColor.FromRgb(44, 207, 214),
                MediaColor.FromRgb(255, 112, 173),
                MediaColor.FromRgb(130, 183, 255)
            };
            int hash = 17;
            foreach (char character in text)
                hash = unchecked(hash * 31 + character);
            return palette[(hash & int.MaxValue) % palette.Length];
        }

        private static void SetConnectorDirection(
            Connector connector,
            GameMepConnectorData connectorData)
        {
            try
            {
                XYZ basis = connector.CoordinateSystem?.BasisZ;
                if (basis == null || basis.GetLength() < 1e-9)
                    return;
                connectorData.Direction = new Vector3D(basis.X, basis.Y, basis.Z);
                connectorData.Direction.Normalize();
                connectorData.HasDirection = true;
            }
            catch { }
        }

        private static GameMepValveData? DetectValve(
            Element element,
            GameMepElementData data,
            int connectorCount,
            GameMepGraphData graph)
        {
            int score = 0;
            var reasons = new List<string>();
            bool pipeAccessory = element.Category?.Id.GetIdValue() ==
                (int)BuiltInCategory.OST_PipeAccessory;
            if (pipeAccessory)
            {
                score++;
                reasons.Add("accessoire de canalisation");
            }
            if (connectorCount >= 2)
            {
                score++;
                reasons.Add(connectorCount + " connecteurs");
            }

            string partType = GetPartType(element);
            if (ContainsAny(partType.ToLowerInvariant(), "valve", "valv", "damper"))
            {
                score += 6;
                reasons.Add("type de pièce vanne");
            }

            string searchable = (data.Name + " " + data.TypeName + " " +
                SafeText(() => (element as FamilyInstance)?.Symbol?.FamilyName))
                .ToLowerInvariant();
            bool checkValve = connectorCount == 2 && ContainsAny(
                searchable + " " + partType.ToLowerInvariant(),
                "clapet", "anti-retour", "anti retour", "check valve",
                "non-return", "non return", "nonreturn", " nrv");
            if (checkValve)
            {
                int entry = data.ConnectorIndices.Where(index =>
                        index >= 0 && index < graph.Connectors.Count &&
                        string.Equals(
                            graph.Connectors[index].FlowDirection,
                            "In",
                            StringComparison.OrdinalIgnoreCase))
                    .DefaultIfEmpty(-1)
                    .First();
                int exit = data.ConnectorIndices.Where(index =>
                        index >= 0 && index < graph.Connectors.Count &&
                        string.Equals(
                            graph.Connectors[index].FlowDirection,
                            "Out",
                            StringComparison.OrdinalIgnoreCase))
                    .DefaultIfEmpty(-1)
                    .First();
                bool hasDirection = entry >= 0 && exit >= 0 && entry != exit;
                return new GameMepValveData
                {
                    ElementKey = data.Key,
                    Kind = GameMepFlowControlKind.CheckValve,
                    Confidence = hasDirection
                        ? GameMepConfidence.High
                        : GameMepConfidence.Medium,
                    DetectionReason = hasDirection
                        ? "clapet anti-retour, sens lu dans les connecteurs Revit"
                        : "clapet anti-retour, sens à confirmer",
                    IsEnabledAsValve = true,
                    InitiallyEnabledAsValve = true,
                    EntryConnectorIndex = entry,
                    ExitConnectorIndex = exit,
                    InitiallyEntryConnectorIndex = entry,
                    InitiallyExitConnectorIndex = exit
                };
            }
            if (ContainsAny(
                searchable,
                "vanne", "valve", "robinet", "papillon", "opercule",
                "soupape", "ball valve", "gate valve", "butterfly"))
            {
                score += 4;
                reasons.Add("nom de famille/type");
            }

            if (score < 2)
                return null;

            GameMepConfidence confidence = score >= 7
                ? GameMepConfidence.High
                : score >= 5
                    ? GameMepConfidence.Medium
                    : GameMepConfidence.Low;
            bool enabledAsValve = confidence != GameMepConfidence.Low;
            return new GameMepValveData
            {
                ElementKey = data.Key,
                Kind = GameMepFlowControlKind.IsolationValve,
                Confidence = confidence,
                DetectionReason = string.Join(", ", reasons),
                IsEnabledAsValve = enabledAsValve,
                InitiallyEnabledAsValve = enabledAsValve
            };
        }

        private static string GetPartType(Element element)
        {
            try
            {
                object? mepModel = (element as FamilyInstance)?.MEPModel;
                PropertyInfo? property = mepModel?.GetType().GetProperty("PartType");
                return property?.GetValue(mepModel, null)?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static SourceScore? ScoreSource(
            Element element,
            GameMepElementData data,
            IList<Connector> connectors)
        {
            bool mechanicalEquipment = element.Category?.Id.GetIdValue() ==
                (int)BuiltInCategory.OST_MechanicalEquipment;
            if (!mechanicalEquipment)
            {
                string categoryName = SafeText(() => element.Category?.Name)
                    .ToLowerInvariant();
                mechanicalEquipment = ContainsAny(
                    categoryName,
                    "équipement de génie climatique",
                    "equipement de genie climatique",
                    "mechanical equipment");
            }
            if (!mechanicalEquipment)
                return null;

            int score = 0;
            bool baseEquipment = connectors.Any(connector => IsBaseEquipment(connector, element.Id));
            if (baseEquipment)
                score += 6;
            if (connectors.Any(connector =>
                SafeGet(() => connector.Direction, FlowDirectionType.Bidirectional) ==
                FlowDirectionType.Out))
            {
                score += 2;
            }

            string searchable = (data.Name + " " + data.TypeName + " " +
                SafeText(() => (element as FamilyInstance)?.Symbol?.FamilyName))
                .ToLowerInvariant();
            // Une pompe met le fluide en mouvement mais ne crée pas la
            // matière qui entre dans le périmètre de la maquette. Elle reste
            // donc un élément traversant et n'est jamais proposée comme
            // source fonctionnelle automatique.
            if (ContainsAny(searchable, "pompe", "pump", "circulateur"))
                return null;
            if (ContainsAny(
                searchable,
                "chaudi", "boiler", "chiller",
                "groupe froid", "heat pump", "pac ", "échangeur", "echangeur"))
            {
                score += 4;
            }

            if (score < 2)
                return null;
            return new SourceScore
            {
                Element = data,
                Score = score,
                IsBaseEquipment = baseEquipment
            };
        }

        private static bool IsBaseEquipment(Connector connector, ElementId elementId)
        {
            try
            {
                MEPSystem? system = connector.MEPSystem;
                PropertyInfo? property = system?.GetType().GetProperty("BaseEquipment");
                Element? equipment = property?.GetValue(system, null) as Element;
                return equipment != null && equipment.Id == elementId;
            }
            catch
            {
                return false;
            }
        }

        private static void BuildSources(
            IList<SourceScore> scores,
            GameMepGraphData graph)
        {
            foreach (IGrouping<string, SourceScore> group in scores
                .GroupBy(score => score.Element.SystemKey ?? string.Empty))
            {
                foreach (SourceScore candidate in group
                    .OrderByDescending(score => score.Score)
                    .ThenBy(score => score.Element.ElementId)
                    .Take(12))
                {
                    graph.Sources.Add(new GameMepSourceData
                    {
                        ElementKey = candidate.Element.Key,
                        SystemKey = candidate.Element.SystemKey,
                        Name = string.IsNullOrWhiteSpace(candidate.Element.Name)
                            ? "Source #" + candidate.Element.ElementId
                            : candidate.Element.Name,
                        Confidence = candidate.Score >= 6
                            ? GameMepConfidence.High
                            : candidate.Score >= 4
                                ? GameMepConfidence.Medium
                                : GameMepConfidence.Low,
                        // Les propositions restent volontairement décochées.
                        // La frontière physique du projet ne peut pas être
                        // devinée de façon fiable depuis les seules familles.
                        IsActive = false,
                        InitiallyActive = false
                    });
                }
            }
        }

        private static void BuildPaths(
            Element element,
            GameMepElementData data,
            GameMepGraphData graph)
        {
            if (element.Location is LocationCurve locationCurve)
            {
                IList<XYZ> points = SafeGet(
                    () => locationCurve.Curve.Tessellate(),
                    new List<XYZ>());
                if (points.Count >= 2)
                {
                    var path = NewPath(data);
                    foreach (XYZ point in points)
                        path.Points.Add(ToPoint(point));
                    path.StartConnector = FindNearestConnector(
                        graph,
                        data.ConnectorIndices,
                        path.Points[0]);
                    path.EndConnector = FindNearestConnector(
                        graph,
                        data.ConnectorIndices,
                        path.Points[path.Points.Count - 1]);
                    path.FinalizePath();
                    data.Paths.Add(path);
                    return;
                }
            }

            if (data.ConnectorIndices.Count == 1)
                return;
            if (data.ConnectorIndices.Count == 2)
            {
                var path = NewPath(data);
                path.StartConnector = data.ConnectorIndices[0];
                path.EndConnector = data.ConnectorIndices[1];
                path.Points.Add(graph.Connectors[path.StartConnector].Position);
                path.Points.Add(graph.Connectors[path.EndConnector].Position);
                path.FinalizePath();
                data.Paths.Add(path);
                return;
            }

            Point3D center = AveragePosition(graph, data.ConnectorIndices);
            foreach (int connectorIndex in data.ConnectorIndices)
            {
                var path = NewPath(data);
                path.StartConnector = connectorIndex;
                path.Points.Add(graph.Connectors[connectorIndex].Position);
                path.Points.Add(center);
                path.FinalizePath();
                data.Paths.Add(path);
            }
        }

        private static GameMepPathData NewPath(GameMepElementData data)
        {
            return new GameMepPathData
            {
                ElementKey = data.Key,
                SystemKey = data.SystemKey,
                IsVisible = data.IsVisible
            };
        }

        private static void BuildInternalConnections(
            GameMepElementData element,
            GameMepValveData? valve,
            GameMepGraphData graph)
        {
            for (int first = 0; first < element.ConnectorIndices.Count; first++)
            {
                for (int second = first + 1; second < element.ConnectorIndices.Count; second++)
                {
                    string firstSystem = graph.Connectors[
                        element.ConnectorIndices[first]].SystemKey;
                    string secondSystem = graph.Connectors[
                        element.ConnectorIndices[second]].SystemKey;
                    bool compatibleSystems =
                        string.Equals(firstSystem, secondSystem, StringComparison.Ordinal) ||
                        string.Equals(firstSystem, UnassignedSystemKey, StringComparison.Ordinal) ||
                        string.Equals(secondSystem, UnassignedSystemKey, StringComparison.Ordinal);
                    if (!compatibleSystems)
                        continue;

                    graph.Connections.Add(new GameMepConnectionData
                    {
                        ConnectorA = element.ConnectorIndices[first],
                        ConnectorB = element.ConnectorIndices[second],
                        IsInternal = true,
                        IsValveGateCandidate = valve != null,
                        ElementKey = element.Key
                    });
                }
            }
        }

        private static void BuildPhysicalConnections(
            IList<RawConnector> rawConnectors,
            IDictionary<string, RawConnector> connectorByKey,
            GameMepGraphData graph)
        {
            var created = new HashSet<string>(StringComparer.Ordinal);
            foreach (RawConnector raw in rawConnectors)
            {
                try
                {
                    foreach (Connector connected in raw.Connector.AllRefs)
                    {
                        if (connected == null || connected.Owner == null ||
                            connected.Owner.Id == raw.Connector.Owner.Id ||
                            connected.Domain != Domain.DomainPiping ||
                            connected.ConnectorType == ConnectorType.Logical)
                        {
                            continue;
                        }

                        Point3D connectedOrigin = ToPoint(connected.Origin);
                        string key = CreateConnectorKey(
                            connected.Owner.Id,
                            connected,
                            connectedOrigin);
                        if (!connectorByKey.TryGetValue(key, out RawConnector target))
                            continue;
                        int low = Math.Min(raw.GraphIndex, target.GraphIndex);
                        int high = Math.Max(raw.GraphIndex, target.GraphIndex);
                        string pairKey = low + "|" + high;
                        if (!created.Add(pairKey))
                            continue;

                        graph.Connections.Add(new GameMepConnectionData
                        {
                            ConnectorA = low,
                            ConnectorB = high,
                            IsInternal = false
                        });
                    }
                }
                catch { }
            }
        }

        private static void ResolveUnassignedSystems(GameMepGraphData graph)
        {
            var physicalNeighbors = new List<int>[graph.Connectors.Count];
            for (int index = 0; index < physicalNeighbors.Length; index++)
                physicalNeighbors[index] = new List<int>();
            foreach (GameMepConnectionData connection in graph.Connections)
            {
                if (connection.IsInternal ||
                    connection.ConnectorA < 0 || connection.ConnectorB < 0 ||
                    connection.ConnectorA >= physicalNeighbors.Length ||
                    connection.ConnectorB >= physicalNeighbors.Length)
                {
                    continue;
                }
                physicalNeighbors[connection.ConnectorA].Add(connection.ConnectorB);
                physicalNeighbors[connection.ConnectorB].Add(connection.ConnectorA);
            }

            // Plusieurs raccords Revit n'ont pas de MEPSystem propre alors que
            // leurs tuyaux voisins sont correctement affectés. Trois passes
            // suffisent à propager l'information à travers ces petits groupes.
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                foreach (GameMepElementData element in graph.Elements)
                {
                    if (!string.Equals(
                        element.SystemKey,
                        UnassignedSystemKey,
                        StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (int connectorIndex in element.ConnectorIndices)
                    {
                        foreach (int neighborIndex in physicalNeighbors[connectorIndex])
                        {
                            string key = graph.Connectors[neighborIndex].SystemKey;
                            if (string.IsNullOrWhiteSpace(key) ||
                                string.Equals(key, UnassignedSystemKey, StringComparison.Ordinal))
                            {
                                continue;
                            }
                            if (!counts.ContainsKey(key))
                                counts[key] = 0;
                            counts[key]++;
                        }
                    }
                    if (counts.Count == 0)
                        continue;

                    string inferredKey = counts
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                        .First().Key;
                    element.SystemKey = inferredKey;
                    GameMepSystemData? system = graph.FindSystem(inferredKey);
                    if (system != null)
                    {
                        element.SystemName = system.Name;
                        element.Classification = system.Classification;
                    }
                    foreach (int connectorIndex in element.ConnectorIndices)
                        graph.Connectors[connectorIndex].SystemKey = inferredKey;
                    foreach (GameMepPathData path in element.Paths)
                        path.SystemKey = inferredKey;
                    changed = true;
                }
                if (!changed)
                    break;
            }

            foreach (GameMepSystemData system in graph.Systems)
                system.ElementCount = 0;
            foreach (GameMepElementData element in graph.Elements)
            {
                GameMepSystemData? system = graph.FindSystem(element.SystemKey);
                if (system != null)
                    system.ElementCount++;
            }
            foreach (GameMepSystemData system in graph.Systems
                .Where(system => system.ElementCount == 0)
                .ToList())
            {
                graph.Systems.Remove(system);
            }
            graph.RebuildIndexes();
        }

        private static int FindNearestConnector(
            GameMepGraphData graph,
            IEnumerable<int> connectorIndices,
            Point3D point)
        {
            int nearest = -1;
            double distance = double.MaxValue;
            foreach (int index in connectorIndices)
            {
                double candidate = (graph.Connectors[index].Position - point).LengthSquared;
                if (candidate >= distance)
                    continue;
                distance = candidate;
                nearest = index;
            }
            return nearest;
        }

        private static Point3D AveragePosition(
            GameMepGraphData graph,
            IEnumerable<int> connectorIndices)
        {
            double x = 0.0;
            double y = 0.0;
            double z = 0.0;
            int count = 0;
            foreach (int index in connectorIndices)
            {
                Point3D point = graph.Connectors[index].Position;
                x += point.X;
                y += point.Y;
                z += point.Z;
                count++;
            }
            return count == 0
                ? new Point3D()
                : new Point3D(x / count, y / count, z / count);
        }

        private static string CreateElementKey(Document document, ElementId elementId)
        {
            string documentKey = document.PathName;
            if (string.IsNullOrWhiteSpace(documentKey))
                documentKey = document.Title;
            return documentKey + "|" + elementId.GetIdLongValue();
        }

        private static void ConfigureScenarioIdentity(
            Document document,
            GameMepGraphData graph)
        {
            string stableIdentity = string.Empty;
            try
            {
                if (document.IsModelInCloud)
                {
                    ModelPath cloudPath = document.GetCloudModelPath();
                    string projectGuid = ReadModelPathGuid(cloudPath, "GetProjectGUID");
                    string modelGuid = ReadModelPathGuid(cloudPath, "GetModelGUID");
                    if (!string.IsNullOrWhiteSpace(projectGuid) &&
                        !string.IsNullOrWhiteSpace(modelGuid))
                    {
                        stableIdentity = "cloud|" + projectGuid + "|" + modelGuid;
                    }
                    else
                    {
                        string cloudPathText = SafeGet(
                            () => ModelPathUtils.ConvertModelPathToUserVisiblePath(cloudPath),
                            string.Empty);
                        if (!string.IsNullOrWhiteSpace(cloudPathText))
                            stableIdentity = "cloud|" + cloudPathText;
                    }
                }
            }
            catch
            {
                stableIdentity = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(stableIdentity))
            {
                try
                {
                    if (document.IsWorkshared)
                    {
                        ModelPath centralPath = document.GetWorksharingCentralModelPath();
                        if (centralPath != null)
                        {
                            string central =
                                ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
                            if (!string.IsNullOrWhiteSpace(central))
                                stableIdentity = "central|" + central;
                        }
                    }
                }
                catch
                {
                    stableIdentity = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(stableIdentity))
            {
                string documentPath = SafeGet(() => document.PathName, string.Empty);
                if (!string.IsNullOrWhiteSpace(documentPath))
                {
                    try { documentPath = Path.GetFullPath(documentPath); }
                    catch { }
                    stableIdentity = "file|" + documentPath;
                }
            }

            if (!string.IsNullOrWhiteSpace(stableIdentity))
            {
                graph.ScenarioModelKey = NormalizeScenarioIdentity(stableIdentity);
                graph.ScenarioCanPersist = true;
                return;
            }

            // Une maquette non enregistrée ne reçoit jamais de fichier disque.
            // Cette clé permet seulement de conserver son scénario si la
            // fenêtre jouable est relancée dans la même session Revit.
            graph.ScenarioModelKey = "session|" +
                document.GetHashCode().ToString(CultureInfo.InvariantCulture) + "|" +
                (document.Title ?? "Projet sans nom");
            graph.ScenarioCanPersist = false;
        }

        private static string NormalizeScenarioIdentity(string identity)
        {
            return (identity ?? string.Empty)
                .Trim()
                .Replace('\\', '/')
                .ToUpperInvariant();
        }

        private static string ReadModelPathGuid(ModelPath modelPath, string methodName)
        {
            if (modelPath == null)
                return string.Empty;
            try
            {
                object value = modelPath.GetType()
                    .GetMethod(methodName, Type.EmptyTypes)
                    ?.Invoke(modelPath, null);
                return value is Guid guid && guid != Guid.Empty
                    ? guid.ToString("N", CultureInfo.InvariantCulture)
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CreatePersistentConnectorKey(
            Element owner,
            Connector connector,
            Point3D origin)
        {
            string ownerId = SafeText(() => owner.UniqueId);
            int connectorId = SafeGet(() => connector.Id, -1);
            if (connectorId >= 0)
            {
                return ownerId + "|connector|" +
                    connectorId.ToString(CultureInfo.InvariantCulture);
            }

            return ownerId + "|position|" +
                origin.X.ToString("R", CultureInfo.InvariantCulture) + "|" +
                origin.Y.ToString("R", CultureInfo.InvariantCulture) + "|" +
                origin.Z.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string CreateConnectorKey(
            ElementId ownerId,
            Connector connector,
            Point3D origin)
        {
            int id = SafeGet(() => connector.Id, -1);
            return ownerId.GetIdLongValue() + "|" + id + "|" +
                Math.Round(origin.X, 6) + "|" +
                Math.Round(origin.Y, 6) + "|" +
                Math.Round(origin.Z, 6);
        }

        private static string GetTypeName(Document document, Element element)
        {
            try
            {
                ElementId typeId = element.GetTypeId();
                return typeId == ElementId.InvalidElementId
                    ? string.Empty
                    : document.GetElement(typeId)?.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Point3D ToPoint(XYZ point)
        {
            return new Point3D(point.X, point.Y, point.Z);
        }

        private static bool ContainsAny(string text, params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                if (text.Contains(candidate))
                    return true;
            }
            return false;
        }

        private static string SafeText(Func<string?> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static T SafeGet<T>(Func<T> getter, T fallback)
        {
            try { return getter(); }
            catch { return fallback; }
        }
    }
}

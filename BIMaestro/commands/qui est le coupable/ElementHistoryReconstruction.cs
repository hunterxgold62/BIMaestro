using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    // Persist native creation data, independently of category or preview mode.
    // These DTOs contain no live Revit objects or numeric element IDs.
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    internal sealed class HistoryRecipe
    {
        public int Version { get; set; } = 1;
        public string Kind { get; set; }
        public HistoryNativeArtifact Native { get; set; }
        public string SystemName { get; set; }
        public List<string> SystemMembers { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int ElectricalSystemType { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int ElectricalConnectionType { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int ElectricalStartSlot { get; set; }
        public string ElectricalCircuitNumber { get; set; }
        public List<double[]> ElectricalPath { get; set; }
        public List<HistoryCircuit> ElectricalCircuits { get; set; }
        public List<HistoryGeometryRelation> GeometryRelations { get; set; }
        [JsonIgnore]
        internal string ExistingElectricalSystem { get; set; }
        [JsonIgnore]
        internal bool ElectricalChanged { get; set; }
        public string Type { get; set; }
        public string Level { get; set; }
        public string Host { get; set; }
        public double LevelElevation { get; set; }
        public double[] Point { get; set; }
        public double Rotation { get; set; }
        public bool HandFlipped { get; set; }
        public bool FacingFlipped { get; set; }
        public bool Flipped { get; set; }
        public int StructuralType { get; set; }
        public double Height { get; set; }
        public double Offset { get; set; }
        public int LocationLine { get; set; }
        public List<List<HistoryCurve>> Loops { get; set; }
        public List<HistoryParameter> Parameters { get; set; }
        public bool RequiresMeshPreview { get; set; }
        public List<string> RestorationOrigins { get; set; }
        public bool WallStructural { get; set; }
        public bool FloorStructural { get; set; }
        public HistoryNetwork Network { get; set; }
        public List<HistoryConnection> Connections { get; set; }
        public double[] BasisX { get; set; }
        public double[] BasisZ { get; set; }
        public List<string> CaptureWarnings { get; set; }
        public bool Mirrored { get; set; }
        public List<HistoryPort> Ports { get; set; }
        public List<HistoryCurve> SketchCurves { get; set; }
        public List<HistoryRoofEdge> RoofEdges { get; set; }
        public string Placement { get; set; }
        public string FaceReference { get; set; }
        public double[] FaceNormal { get; set; }
        public List<double[]> AdaptivePoints { get; set; }
        public bool AdaptiveFlipped { get; set; }
        public bool WorkPlaneFlipped { get; set; }
        public bool WallProfile { get; set; }
        public List<double[]> ShapePoints { get; set; }
        public List<HistoryCurve> ShapeCreases { get; set; }
    }

    internal sealed class HistoryCircuit
    {
        public string SourceUniqueId { get; set; }
        public HistoryRecipe Recipe { get; set; }
    }

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    internal sealed class HistoryCurve
    {
        public double[] Start { get; set; }
        public double[] End { get; set; }
        public double[] Mid { get; set; }
        public string SourceUniqueId { get; set; }
        public List<string> RestorationOrigins { get; set; }
    }

    internal sealed class HistoryRoofEdge
    {
        public bool DefinesSlope { get; set; }
        public double Slope { get; set; }
        public double Offset { get; set; }
    }

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    internal sealed class HistoryParameter
    {
        public int BuiltIn { get; set; }
        public string Definition { get; set; }
        public string Shared { get; set; }
        public string Name { get; set; }
        public int Storage { get; set; }
        public double Number { get; set; }
        public string Text { get; set; }
        public string Reference { get; set; }
    }

    internal static partial class ElementHistoryReconstruction
    {
        internal static HistoryRecipe Capture(Element element, Action<string> diagnostic = null)
        {
            string detail = null;
            var recipe = element != null && ElementHistoryNativeArchive.RequiresAggregate(element)
                ? ElementHistoryNativeArchive.Capture(element) : CaptureCore(element, message => detail = message);
            if (recipe == null && element != null)
                try { recipe = ElementHistoryNativeArchive.Capture(element); } catch (Exception ex) { detail = ex.Message; }
            if (recipe != null && (element is FamilyInstance || element is Group))
            {
                try
                {
                    var members = element is FamilyInstance family ? new[] { family }
                        : ElementHistoryNativeArchive.Aggregate(element.Document, element).OfType<FamilyInstance>();
                    var circuits = members.Where(f => f.MEPModel != null
                            && !ElementHistoryRestoration.IsCalorifuge(f.Category?.Name, f.Symbol?.Family?.Name))
                        .SelectMany(f => (f.MEPModel.GetElectricalSystems() ?? new HashSet<ElectricalSystem>())
                            .Concat(f.MEPModel.GetAssignedElectricalSystems() ?? new HashSet<ElectricalSystem>()))
                        .GroupBy(s => s.UniqueId).Select(g => g.First()).ToList();
                    if (circuits.Count > 0) recipe.ElectricalCircuits = circuits.Select(s => new HistoryCircuit
                        { SourceUniqueId = s.UniqueId, Recipe = CaptureCore(s, diagnostic) }).Where(c => c.Recipe != null).ToList();
                }
                catch (Exception ex)
                {
                    if (recipe.CaptureWarnings == null) recipe.CaptureWarnings = new List<string>();
                    recipe.CaptureWarnings.Add("Circuits électriques non enregistrés : " + ex.Message);
                }
            }
            if (recipe == null)
            {
                var instance = element as FamilyInstance;
                string context = element?.GetType().Name ?? "Élément absent";
                try
                {
                    if (instance != null)
                        context += "; placement=" + instance.Symbol.Family.FamilyPlacementType
                            + "; miroir=" + instance.Mirrored + "; sous-composant=" + (instance.SuperComponent != null);
                }
                catch { /* Diagnostics must not break snapshot capture. */ }
                diagnostic?.Invoke(detail ?? ("Capture non prise en charge : " + context + "."));
            }
            if (recipe != null)
            {
                var warnings = recipe.CaptureWarnings ?? new List<string>();
                var members = ElementHistoryNativeArchive.RequiresAggregate(element)
                    ? ElementHistoryNativeArchive.Aggregate(element.Document, element) : new[] { element };
                recipe.GeometryRelations = members.SelectMany(member => ElementHistoryRelations.Capture(member, warnings))
                    .GroupBy(ElementHistoryRelations.Key).Select(g => g.First()).ToList();
                if (recipe.GeometryRelations.Count == 0) recipe.GeometryRelations = null;
                if (warnings.Count > 0) recipe.CaptureWarnings = warnings;
            }
            return recipe;
        }

        private static HistoryRecipe CaptureCore(Element element, Action<string> diagnostic)
        {
            try
            {
                var doc = element.Document;
                if (element is ElectricalSystem electrical)
                    return new HistoryRecipe { Kind = "electrical_system", Host = electrical.BaseEquipment?.UniqueId,
                        ElectricalSystemType = (int)electrical.SystemType,
                        ElectricalConnectionType = (int)electrical.CircuitConnectionType,
                        ElectricalStartSlot = electrical.BaseEquipment == null ? 0 : electrical.StartSlot,
                        ElectricalCircuitNumber = electrical.CircuitNumber,
                        ElectricalPath = electrical.BaseEquipment != null && electrical.CircuitPathMode == ElectricalCircuitPathMode.Custom
                            ? electrical.GetCircuitPath().Select(Pack).ToList() : null,
                        RestorationOrigins = ElementHistoryRestoration.GetOrigins(element), RequiresMeshPreview = true,
                        Parameters = CaptureParameters(element), SystemMembers = electrical.Elements.Cast<Element>().Select(e => e.UniqueId).OrderBy(id => id, StringComparer.Ordinal).ToList() };
                if (element is PipingSystem pipeSystem)
                    return new HistoryRecipe { Kind = "pipe_system", Type = doc.GetElement(pipeSystem.GetTypeId())?.UniqueId,
                        RestorationOrigins = ElementHistoryRestoration.GetOrigins(element), RequiresMeshPreview = true,
                        SystemName = pipeSystem.Name, SystemMembers = pipeSystem.PipingNetwork.Cast<Element>().Select(e => e.UniqueId).ToList() };
                if (element is MechanicalSystem ductSystem)
                    return new HistoryRecipe { Kind = "duct_system", Type = doc.GetElement(ductSystem.GetTypeId())?.UniqueId,
                        RestorationOrigins = ElementHistoryRestoration.GetOrigins(element), RequiresMeshPreview = true,
                        SystemName = ductSystem.Name, SystemMembers = ductSystem.DuctNetwork.Cast<Element>().Select(e => e.UniqueId).ToList() };
                if (element is MEPCurve) return ElementHistoryNetwork.Capture(element);
                if (doc.IsFamilyDocument) return null;
                if (element is ModelCurve model && element.Category?.Id.GetIdLongValue() != (int)BuiltInCategory.OST_SketchLines)
                {
                    var curve = CaptureCurve(model.GeometryCurve);
                    var curveLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => Math.Abs(l.ProjectElevation - model.GeometryCurve.GetEndPoint(0).Z)).FirstOrDefault();
                    if (curve == null || curveLevel == null || model.IsReferenceLine || model.LineStyle == null) return null;
                    var plane = model.SketchPlane.GetPlane();
                    return new HistoryRecipe { Kind = "model_curve", Type = model.LineStyle.UniqueId, Level = curveLevel.UniqueId,
                        Point = Pack(plane.Origin), BasisZ = Pack(plane.Normal),
                        Loops = new List<List<HistoryCurve>> { new List<HistoryCurve> { curve } },
                        RestorationOrigins = ElementHistoryRestoration.GetOrigins(model) };
                }
                if (element is FootPrintRoof || element is Ceiling)
                    return CaptureSketchHost(element, diagnostic);
                if (element is FamilyInstance extendedFamily && extendedFamily.Symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased
                    && extendedFamily.Symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBasedHosted)
                    return CaptureExtendedFamily(extendedFamily, diagnostic);
                if (!(element is FamilyInstance || element is Wall || element is Floor)) return null;
                var type = doc.GetElement(element.GetTypeId());
                var level = FindLevel(element);
                if (type == null || level == null) { diagnostic?.Invoke("Type ou niveau de référence introuvable à la capture."); return null; }
                // Keep a mesh for cuts/joins, but retain the native placement recipe
                // so the host itself can still be restored with its deleted instances.
                bool hasCuts = JoinGeometryUtils.GetJoinedElements(doc, element).Count != 0
                    || (InstanceVoidCutUtils.CanBeCutWithVoid(element)
                        && InstanceVoidCutUtils.GetCuttingVoidInstances(element).Count != 0);
                var recipe = new HistoryRecipe
                {
                    Type = type.UniqueId, Level = level.UniqueId,
                    LevelElevation = level.ProjectElevation,
                    RequiresMeshPreview = hasCuts,
                    RestorationOrigins = ElementHistoryRestoration.GetOrigins(element)
                };
                if (element is FamilyInstance instance)
                {
                    var placement = instance.Symbol.Family.FamilyPlacementType;
                    if (instance.Symbol.Family.IsInPlace || instance.SuperComponent != null
                        || !(instance.Location is LocationPoint location)
                        || (placement != FamilyPlacementType.OneLevelBased
                            && placement != FamilyPlacementType.OneLevelBasedHosted)) return null;
                    // Face/workplane/adaptive/two-level families need a different placement recipe.
                    if (placement == FamilyPlacementType.OneLevelBasedHosted && instance.Host == null) return null;
                    recipe.Kind = "family";
                    recipe.Host = instance.Host?.UniqueId;
                    recipe.Point = Pack(location.Point);
                    // The saved transform is authoritative. LocationPoint.Rotation is
                    // not available for every 3D MEP placement and must not discard it.
                    recipe.Rotation = 0;
                    recipe.HandFlipped = instance.HandFlipped;
                    recipe.FacingFlipped = instance.FacingFlipped;
                    recipe.StructuralType = (int)instance.StructuralType;
                    recipe.Parameters = CaptureParameters(instance);
                    recipe.BasisX = Pack(instance.GetTransform().BasisX);
                    recipe.BasisZ = Pack(instance.GetTransform().BasisZ);
                    recipe.Mirrored = instance.Mirrored;
                    recipe.CaptureWarnings = new List<string>();
                    recipe.Connections = ElementHistoryNetwork.CaptureConnections(instance, recipe.CaptureWarnings);
                    recipe.Ports = ElementHistoryNetwork.CaptureFamilyPorts(instance);
                }
                else if (element is Wall wall)
                {
                    if (wall.WallType.Kind != WallKind.Basic
                        || Math.Abs(Value(wall, BuiltInParameter.WALL_SINGLE_SLANT_ANGLE_FROM_VERTICAL)) > 1e-8
                        || Integer(wall, BuiltInParameter.WALL_CROSS_SECTION) != (int)WallCrossSection.Vertical
                        || Integer(wall, BuiltInParameter.WALL_TOP_IS_ATTACHED) != 0
                        || Integer(wall, BuiltInParameter.WALL_BOTTOM_IS_ATTACHED) != 0
                        || !(wall.Location is LocationCurve location)) return null;
                    recipe.Kind = "wall";
                    if (wall.SketchId != ElementId.InvalidElementId)
                    {
                        var sketch = doc.GetElement(wall.SketchId) as Sketch;
                        if (sketch == null || sketch.Profile.Size != 1) return null;
                        recipe.WallProfile = true;
                        recipe.BasisZ = Pack(wall.Orientation);
                        recipe.Loops = sketch.Profile.Cast<CurveArray>().Select(l => l.Cast<Curve>().Select(CaptureCurve).ToList()).ToList();
                        if (recipe.Loops.SelectMany(l => l).Any(c => c == null)) return null;
                        recipe.SketchCurves = CaptureSketchCurves(doc, sketch);
                    }
                    else
                    {
                        var curve = CaptureCurve(location.Curve);
                        if (curve == null) return null;
                        recipe.Loops = new List<List<HistoryCurve>> { new List<HistoryCurve> { curve } };
                    }
                    recipe.Height = Value(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    if (recipe.Height <= 0) return null;
                    recipe.Offset = Value(wall, BuiltInParameter.WALL_BASE_OFFSET);
                    recipe.LocationLine = Integer(wall, BuiltInParameter.WALL_KEY_REF_PARAM);
                    recipe.Flipped = wall.Flipped;
                    recipe.WallStructural = Integer(wall, BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT) != 0;
                    recipe.RequiresMeshPreview |= wall.FindInserts(true, true, true, true).Count != 0;
                }
                else if (element is Floor floor)
                {
                    // Floors with a slope arrow need its placement data. Shape-edited
                    // floors retain their editable vertices and user split lines.
                    if (Math.Abs(Value(floor, BuiltInParameter.ROOF_SLOPE)) > 1e-8
                        || !(doc.GetElement(floor.SketchId) is Sketch sketch)) return null;
                    recipe.Kind = "floor";
                    recipe.Offset = Value(floor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                    recipe.FloorStructural = Integer(floor, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL) != 0;
                    recipe.RequiresMeshPreview |= floor.FindInserts(true, true, true, true).Count != 0;
                    recipe.Loops = new List<List<HistoryCurve>>();
                    int count = 0;
                    foreach (CurveArray loop in sketch.Profile)
                    {
                        var curves = new List<HistoryCurve>();
                        foreach (Curve curve in loop)
                        {
                            count++;
                            var item = CaptureCurve(curve);
                            if (item == null) return null;
                            curves.Add(item);
                        }
                        recipe.Loops.Add(curves);
                    }
                    if (count == 0) return null;
                    recipe.SketchCurves = CaptureSketchCurves(doc, sketch);
                    if (IsShapeEdited(floor)) CaptureSlabShape(GetShapeEditor(floor), recipe);
                }
                else return null;
                return recipe;
            }
            catch (Exception ex) { diagnostic?.Invoke("Échec de capture " + element?.GetType().Name + " : " + ex.Message); return null; }
        }

        internal static Element FindOriginal(Document doc, string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return null;
            try { return doc.GetElement(uniqueId); }
            catch { return null; }
        }

        internal static Level FindLevel(Element element)
        {
            var doc = element.Document;
            var level = doc.GetElement(element.LevelId) as Level;
            if (level != null) return level;
            foreach (var key in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                BuiltInParameter.RBS_START_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM })
            {
                var p = element.get_Parameter(key);
                if (p?.StorageType != StorageType.ElementId) continue;
                level = doc.GetElement(p.AsElementId()) as Level;
                if (level != null) return level;
            }
            return null;
        }

        internal static HistoryRecipe ReadRecipe(object raw)
        {
            try
            {
                var recipe = raw as HistoryRecipe ?? (raw as JObject ?? JObject.FromObject(raw)).ToObject<HistoryRecipe>();
                if (recipe?.Version == 1 && recipe.Kind == "native_archive" && recipe.Native != null) return recipe;
                if (recipe?.Version == 1 && recipe.Kind == "electrical_system" && recipe.SystemMembers?.Count > 0
                    && Enum.IsDefined(typeof(ElectricalSystemType), recipe.ElectricalSystemType)) return recipe;
                if (recipe?.Version == 1 && (recipe.Kind == "pipe_system" || recipe.Kind == "duct_system")
                    && !string.IsNullOrEmpty(recipe.Type) && recipe.SystemMembers != null) return recipe;
                return recipe != null && recipe.Version == 1 && !string.IsNullOrEmpty(recipe.Type)
                    && !string.IsNullOrEmpty(recipe.Level)
                    && new[] { "family", "wall", "floor", "network", "roof", "ceiling", "model_curve" }.Contains(recipe.Kind)
                    ? recipe : null;
            }
            catch { return null; }
        }

        // Caller owns a real transaction and decides whether to commit. Never substitutes
        // a mesh/DirectShape for a native element when the user requests restoration.
        internal static Element RestoreNative(Document doc, HistoryRecipe recipe)
        {
            if (recipe.SystemMembers != null) return RestoreSystem(doc, recipe);
            string reason = null;
            var element = Create(doc, recipe, message => reason = message);
            if (element == null) throw new InvalidOperationException(reason ?? "Unsupported element placement.");
            doc.Regenerate();
            if (element.get_BoundingBox(null) == null)
                throw new InvalidOperationException("The restored element has no geometry.");
            return element;
        }

        private static Element RestoreSystem(Document doc, HistoryRecipe recipe)
        {
            if (recipe.Kind == "electrical_system") return RestoreElectricalSystem(doc, recipe);
            var type = FindOriginal(doc, recipe.Type);
            var domain = recipe.Kind == "pipe_system" ? Domain.DomainPiping : Domain.DomainHvac;
            var ports = recipe.SystemMembers.Select(id => FindOriginal(doc,id)).Where(e => e != null)
                .SelectMany(ElementHistoryNetwork.Ports).Where(p => p.Domain == domain && p.ConnectorType != ConnectorType.Logical).ToList();
            if (ports.Count == 0 && recipe.SystemMembers.Count > 0) throw new InvalidOperationException("Aucun composant du système n’a pu être retrouvé.");
            var systems = ports.Select(p => p.MEPSystem).Where(s => s != null && s.GetTypeId() == type.Id)
                .GroupBy(s => s.UniqueId).Select(g => g.First()).ToList();
            MEPSystem system = systems.Count == 1 ? systems[0] : recipe.Kind == "pipe_system"
                ? (MEPSystem)PipingSystem.Create(doc,type.Id) : MechanicalSystem.Create(doc,type.Id);
            var connectors = new ConnectorSet();
            foreach(var port in ports.Where(p => p.MEPSystem == null || p.MEPSystem.Id != system.Id)) connectors.Insert(port);
            if (connectors.Size > 0) system.Add(connectors);
            if (!string.IsNullOrEmpty(recipe.SystemName)) system.Name = recipe.SystemName;
            return system;
        }

        private static Element RestoreElectricalSystem(Document doc, HistoryRecipe recipe)
        {
            var members = recipe.SystemMembers.Select(id => FindOriginal(doc, id)).ToList();
            if (members.Any(e => !(e is FamilyInstance)))
                throw new InvalidOperationException("Un équipement du circuit électrique est absent. Restaurez tous ses équipements avant le circuit.");
            var ids = new HashSet<ElementId>(members.Select(e => e.Id));
            var kind = (ElectricalSystemType)recipe.ElectricalSystemType;
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(s => s.SystemType == kind && s.Elements.Cast<Element>().Any(e => ids.Contains(e.Id))).ToList();
            var historical = FindOriginal(doc, recipe.ExistingElectricalSystem) as ElectricalSystem;
            if (historical != null && !existing.Any(s => s.Id == historical.Id)) existing.Add(historical);
            // Do not detach equipment from a surviving circuit or silently merge two circuits.
            if (existing.Count > 1 || existing.Any(s => s.SystemType != kind ||
                (s.Id == historical?.Id ? s.Elements.Cast<Element>().Any(e => !ids.Contains(e.Id))
                    : !ids.SetEquals(s.Elements.Cast<Element>().Select(e => e.Id)))))
                throw new InvalidOperationException("Des équipements appartiennent déjà à un autre circuit électrique.");
            var system = existing.SingleOrDefault() ?? ElectricalSystem.Create(doc, ids.ToList(), kind);
            if (system == null) throw new InvalidOperationException("Revit n’a pas pu recréer le circuit électrique.");
            var before = historical == null ? null : JsonConvert.SerializeObject(CaptureCore(system, null));
            var memberIds = new HashSet<ElementId>(system.Elements.Cast<Element>().Select(e => e.Id));
            var additions = new ElementSet();
            foreach (var member in members.Where(e => !memberIds.Contains(e.Id))) additions.Insert(member);
            if (additions.Size > 0) system.AddToCircuit(additions);
            if (historical != null)
            {
                // Restore missing membership without overwriting later user edits to
                // the surviving circuit's load name, sizing, path or panel position.
                if (!string.IsNullOrEmpty(recipe.Host))
                {
                    var panel = FindOriginal(doc, recipe.Host) as FamilyInstance;
                    if (panel == null || (system.BaseEquipment != null && system.BaseEquipment.Id != panel.Id))
                        throw new InvalidOperationException("Le tableau actuel du circuit ne correspond pas au tableau historique.");
                    if (system.BaseEquipment == null) system.SelectPanel(panel);
                }
                if (!ids.SetEquals(system.Elements.Cast<Element>().Select(e => e.Id)))
                    throw new InvalidOperationException("Revit n’a pas rétabli tous les équipements du circuit existant.");
                recipe.ElectricalChanged = before != JsonConvert.SerializeObject(CaptureCore(system, null));
                return system;
            }
            if (!string.IsNullOrEmpty(recipe.Host))
            {
                var panel = FindOriginal(doc, recipe.Host) as FamilyInstance;
                if (panel == null) throw new InvalidOperationException("Le tableau électrique du circuit est absent.");
                if (system.BaseEquipment != null && system.BaseEquipment.Id != panel.Id)
                    throw new InvalidOperationException("Le circuit existant est raccordé à un autre tableau électrique.");
                if (system.BaseEquipment == null) system.SelectPanel(panel);
                ApplyParameters(doc, system, recipe.Parameters);
                system.CircuitConnectionType = (CircuitConnectionType)recipe.ElectricalConnectionType;
                doc.Regenerate();
                if (recipe.ElectricalStartSlot > 0 && system.StartSlot != recipe.ElectricalStartSlot)
                {
                    var otherSlots = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                        .Where(s => s.Id != system.Id && s.BaseEquipment?.Id == panel.Id)
                        .ToDictionary(s => s.UniqueId, s => s.StartSlot);
                    var schedule = new FilteredElementCollector(doc).OfClass(typeof(PanelScheduleView)).Cast<PanelScheduleView>()
                        .FirstOrDefault(v => !v.IsTemplate && v.GetPanel() == panel.Id);
                    var temporary = schedule == null;
                    if (temporary) schedule = PanelScheduleView.CreateInstanceView(doc, panel.Id);
                    IList<int> rows, cols, targetRows, targetCols;
                    schedule.GetCellsBySlotNumber(system.StartSlot, out rows, out cols);
                    schedule.GetCellsBySlotNumber(recipe.ElectricalStartSlot, out targetRows, out targetCols);
                    if (rows.Count == 0 || targetRows.Count == 0
                        || targetRows.Select((r, i) => schedule.GetCircuitIdByCell(r, targetCols[i]))
                            .Any(id => id != ElementId.InvalidElementId && id != system.Id)
                        || !schedule.CanMoveSlotTo(rows[0], cols[0], targetRows[0], targetCols[0]))
                        throw new InvalidOperationException("L’emplacement historique du circuit dans le tableau est indisponible.");
                    schedule.MoveSlotTo(rows[0], cols[0], targetRows[0], targetCols[0]);
                    if (temporary) doc.Delete(schedule.Id);
                    doc.Regenerate();
                    if (system.StartSlot != recipe.ElectricalStartSlot)
                        throw new InvalidOperationException("L’emplacement historique du circuit n’a pas pu être rétabli.");
                    if (otherSlots.Any(pair => ((ElectricalSystem)doc.GetElement(pair.Key)).StartSlot != pair.Value))
                        throw new InvalidOperationException("Le déplacement affecterait un autre circuit du tableau.");
                }
                if (recipe.ElectricalPath != null) system.SetCircuitPath(recipe.ElectricalPath.Select(Unpack).ToList());
                if (!string.IsNullOrEmpty(recipe.ElectricalCircuitNumber) && system.CircuitNumber != recipe.ElectricalCircuitNumber)
                    throw new InvalidOperationException("La numérotation actuelle du tableau ne permet pas de rétablir le numéro historique du circuit.");
            }
            else
            {
                if (system.BaseEquipment != null)
                    throw new InvalidOperationException("Le circuit existant est déjà raccordé à un tableau.");
                ApplyParameters(doc, system, recipe.Parameters);
            }
            if (!ids.SetEquals(system.Elements.Cast<Element>().Select(e => e.Id)))
                throw new InvalidOperationException("Revit n’a pas rétabli tous les équipements du circuit.");
            recipe.ElectricalChanged = before != null && before != JsonConvert.SerializeObject(CaptureCore(system, null));
            return system;
        }

        // The enclosing visualization transaction owns the eventual DirectShape only.
        // Native elements and every side effect (cuts, joins, activated types) are rolled
        // back before returning; only copied point coordinates survive the subtransaction.
        internal static List<List<XYZ>> Reconstruct(Document doc, object raw, Action<string> diagnostic = null)
        {
            if (raw == null || !doc.IsModifiable) return null;
            var recipe = ReadRecipe(raw);
            if (recipe == null || recipe.RequiresMeshPreview) return null;
            using (var temporary = new SubTransaction(doc))
            {
                temporary.Start();
                try
                {
                    var element = Create(doc, recipe, diagnostic);
                    if (element == null) return null;
                    doc.Regenerate();
                    var triangles = new List<List<XYZ>>();
                    Collect(element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), triangles,
                        DateTime.UtcNow.AddSeconds(2));
                    if (triangles.Count == 0) diagnostic?.Invoke("Reconstructed element has no visible geometry.");
                    return triangles.OrderByDescending(TriangleArea).Take(2400).ToList();
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception ex) { diagnostic?.Invoke(ex.ToString()); return null; }
                finally
                {
                    if (temporary.GetStatus() == TransactionStatus.Started) temporary.RollBack();
                }
            }
        }

        private static Element Create(Document doc, HistoryRecipe recipe, Action<string> diagnostic)
        {
            var type = doc.GetElement(recipe.Type);
            var level = doc.GetElement(recipe.Level) as Level;
            if (type == null || level == null) { diagnostic?.Invoke("Type or level is missing."); return null; }
            double offset = recipe.Offset + recipe.LevelElevation - level.ProjectElevation;
            if (recipe.Kind == "model_curve")
            {
                var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(Unpack(recipe.BasisZ), Unpack(recipe.Point)));
                var curve = doc.Create.NewModelCurve(RestoreCurve(recipe.Loops.Single().Single()), plane);
                curve.LineStyle = type;
                return curve;
            }
            if (recipe.Kind == "network") return ElementHistoryNetwork.Create(doc, recipe);
            if (recipe.Kind == "family" && type is FamilySymbol symbol)
            {
                var host = string.IsNullOrEmpty(recipe.Host) ? null : doc.GetElement(recipe.Host);
                if (!string.IsNullOrEmpty(recipe.Host) && host == null) { diagnostic?.Invoke("Host is missing."); return null; }
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                if (!string.IsNullOrEmpty(recipe.Placement)) return CreateExtendedFamily(doc, symbol, level, recipe);
                XYZ point = Unpack(recipe.Point);
                var instance = host == null
                    ? doc.Create.NewFamilyInstance(point, symbol, level, (StructuralType)recipe.StructuralType)
                    : doc.Create.NewFamilyInstance(point, symbol, host, level, (StructuralType)recipe.StructuralType);
                ApplyParameters(doc, instance, recipe.Parameters);
                doc.Regenerate();
                if (instance.HandFlipped != recipe.HandFlipped && !instance.flipHand() && recipe.BasisX == null)
                { diagnostic?.Invoke("Cannot restore hand flip."); return null; }
                if (instance.FacingFlipped != recipe.FacingFlipped && !instance.flipFacing() && recipe.BasisX == null)
                { diagnostic?.Invoke("Cannot restore facing flip."); return null; }
                if (!(instance.Location is LocationPoint location)) { diagnostic?.Invoke("Location is not a point."); return null; }
                if (recipe.BasisX != null && instance.Mirrored != recipe.Mirrored)
                {
                    ElementTransformUtils.MirrorElements(doc, new[] { instance.Id }, Plane.CreateByNormalAndOrigin(XYZ.BasisX, location.Point), false);
                    doc.Regenerate();
                    if (instance.Mirrored != recipe.Mirrored)
                        throw new InvalidOperationException("Impossible de rétablir le miroir de la famille.");
                }
                if (recipe.BasisX != null && recipe.BasisZ != null)
                    ElementHistoryNetwork.Orient(doc, instance, Unpack(recipe.BasisX), Unpack(recipe.BasisZ));
                else
                {
                    var angle = recipe.Rotation - location.Rotation;
                    if (Math.Abs(angle) > 1e-8)
                        ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(location.Point, location.Point + XYZ.BasisZ), angle);
                }
                // Placement parameters and changed levels can shift the insertion point.
                ElementTransformUtils.MoveElement(doc, instance.Id, point - ((LocationPoint)instance.Location).Point);
                try { ElementHistoryNetwork.RestoreFamilyPorts(doc, instance, recipe); }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (InvalidOperationException) { return ElementHistoryNetwork.RebuildWithSizingStubs(doc, instance, recipe); }
                return instance;
            }
            if (recipe.Kind == "wall" && type is WallType)
            {
                Curve curve = recipe.WallProfile ? null : RestoreCurve(recipe.Loops.Single().Single());
                var wall = recipe.WallProfile
                    ? Wall.Create(doc, recipe.Loops.Single().Select(RestoreCurve).ToList(), type.Id, level.Id, recipe.WallStructural, Unpack(recipe.BasisZ))
                    : Wall.Create(doc, curve, type.Id, level.Id, recipe.Height, offset, recipe.Flipped, recipe.WallStructural);
                WallUtils.DisallowWallJoinAtEnd(wall, 0);
                WallUtils.DisallowWallJoinAtEnd(wall, 1);
                wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(recipe.LocationLine);
                if (recipe.WallProfile) { if (wall.Flipped != recipe.Flipped) wall.Flip(); }
                else ((LocationCurve)wall.Location).Curve = curve;
                return wall;
            }
            if (recipe.Kind == "floor" && type is FloorType)
            {
                var loops = recipe.Loops.Select(items => CurveLoop.Create(items.Select(RestoreCurve).ToList())).ToList();
                var floor = Floor.Create(doc, loops, type.Id, level.Id);
                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(offset);
                floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(recipe.FloorStructural ? 1 : 0);
                if (recipe.ShapePoints != null) RestoreSlabShape(doc, GetShapeEditor(floor), recipe);
                return floor;
            }
            if (recipe.Kind == "roof" || recipe.Kind == "ceiling") return CreateSketchHost(doc, type as ElementType, level, recipe);
            return null;
        }

        internal static List<HistoryParameter> CaptureParameters(Element element)
        {
            var values = new List<HistoryParameter>();
            foreach (Parameter parameter in element.Parameters)
            {
                if (!parameter.HasValue || parameter.StorageType == StorageType.None) continue;
                long id = parameter.Id.GetIdLongValue();
                // Attachment-dependent parameters do not exist on an unattached
                // column. The relation pass restores these through ColumnAttachment.
                if (id < 0 && ((BuiltInParameter)id).ToString().StartsWith("COLUMN_", StringComparison.Ordinal)
                    && ((BuiltInParameter)id).ToString().Contains("ATTACHMENT")) continue;
                // Connected fittings can expose their instance dimensions as read-only.
                // Retain custom numeric values; replay only if writable on the new,
                // disconnected instance. Derived/formula values remain read-only.
                if (parameter.IsReadOnly && !(element is FamilyInstance family && id >= 0
                    && parameter.StorageType == StorageType.Double
                    && (!(family.MEPModel is ElectricalEquipment) || IsGeometricParameter(parameter)))) continue;
                // Instance identity and host/level placement are handled separately.
                if (id == (int)BuiltInParameter.ALL_MODEL_MARK || id == (int)BuiltInParameter.ELEM_TYPE_PARAM
                    || id == (int)BuiltInParameter.FAMILY_LEVEL_PARAM) continue;
                var projectDefinition = id >= 0 ? element.Document.GetElement(parameter.Id) as ParameterElement : null;
                if (projectDefinition?.GetDefinition()?.Name != parameter.Definition.Name) projectDefinition = null;
                var value = new HistoryParameter
                {
                    BuiltIn = id < 0 ? checked((int)id) : 0,
                    Shared = parameter.IsShared ? parameter.GUID.ToString() : null,
                    // Family-local parameter IDs can collide with unrelated project
                    // elements. Only a real ParameterElement defines a project parameter.
                    Definition = projectDefinition?.UniqueId,
                    Storage = (int)parameter.StorageType
                };
                // Non-shared family parameters have no project ParameterElement.
                // Their name is scoped to this exact FamilySymbol, never to all families.
                if (value.BuiltIn == 0 && value.Shared == null && (value.Definition == null || element is FamilyInstance))
                    value.Name = parameter.Definition.Name;
                switch (parameter.StorageType)
                {
                    case StorageType.Double: value.Number = parameter.AsDouble(); break;
                    case StorageType.Integer: value.Number = parameter.AsInteger(); break;
                    case StorageType.String: value.Text = parameter.AsString(); break;
                    case StorageType.ElementId:
                        var reference = parameter.AsElementId();
                        value.Number = reference.GetIdLongValue();
                        if (value.Number >= 0)
                        {
                            var target = element.Document.GetElement(reference);
                            // System instances are regenerated by Revit from physical connections.
                            if (target is MEPSystem) continue;
                            value.Reference = target?.UniqueId;
                            if (value.Reference == null) continue;
                            value.Number = 0;
                        }
                        break;
                }
                values.Add(value);
            }
            return values;
        }

        private static bool IsGeometricParameter(Parameter parameter)
        {
            // Electrical panel load-classification totals appear only after circuits
            // exist. They are computed values, unlike solver-controlled fitting sizes.
            try
            {
                var units = UnitUtils.GetValidUnits(parameter.Definition.GetDataType());
                return units.Contains(UnitTypeId.Feet) || units.Contains(UnitTypeId.Radians)
                    || units.Contains(UnitTypeId.SquareFeet) || units.Contains(UnitTypeId.CubicFeet);
            }
            catch { return false; }
        }

        internal static void ApplyParameters(Document doc, Element element, List<HistoryParameter> values)
        {
            foreach (var value in (values ?? new List<HistoryParameter>())
                .OrderBy(v => v.BuiltIn == (int)BuiltInParameter.RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM ? 0 : 1))
            {
                Parameter parameter = value.Shared != null ? element.get_Parameter(new Guid(value.Shared))
                    : value.BuiltIn < 0 ? element.get_Parameter((BuiltInParameter)value.BuiltIn)
                    : (!string.IsNullOrEmpty(value.Definition) && doc.GetElement(value.Definition) is ParameterElement definition
                        ? element.get_Parameter(definition.GetDefinition()) : null);
                if (parameter == null && value.Name != null) parameter = element.GetParameters(value.Name).SingleOrDefault();
                if (parameter == null) throw new InvalidOperationException("Paramètre historique absent : "
                    + (value.Name ?? (value.BuiltIn < 0 ? ((BuiltInParameter)value.BuiltIn).ToString() : value.Definition ?? value.Shared)) + ".");
                if (parameter.IsReadOnly) continue;
                switch ((StorageType)value.Storage)
                {
                    case StorageType.Double:
                        if (Math.Abs(parameter.AsDouble() - value.Number) > 1e-9 && !parameter.Set(value.Number))
                            throw new InvalidOperationException("Historical dimension could not be restored.");
                        break;
                    case StorageType.Integer:
                        if (parameter.AsInteger() != (int)value.Number && !parameter.Set((int)value.Number))
                            throw new InvalidOperationException("Historical integer could not be restored.");
                        break;
                    case StorageType.String:
                        if (parameter.AsString() != value.Text && !parameter.Set(value.Text ?? string.Empty))
                            throw new InvalidOperationException("Historical text could not be restored.");
                        break;
                    case StorageType.ElementId:
                        var id = value.Reference == null ? ElementIdExtensions.CreateElementId((int)value.Number)
                            : doc.GetElement(value.Reference)?.Id;
                        if (id == null) throw new InvalidOperationException("Historical parameter reference is missing.");
                        if (parameter.AsElementId() != id && !parameter.Set(id))
                            throw new InvalidOperationException("Historical reference could not be restored.");
                        if (value.BuiltIn == (int)BuiltInParameter.RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM) doc.Regenerate();
                        break;
                }
            }
        }

        private static void Collect(GeometryElement geometry, List<List<XYZ>> triangles, DateTime deadline)
        {
            if (geometry == null) return;
            foreach (GeometryObject item in geometry)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                if (item is GeometryInstance instance) Collect(instance.GetInstanceGeometry(), triangles, deadline);
                else if (item is Solid solid)
                    foreach (Face face in solid.Faces) AddMesh(face.Triangulate(), triangles, deadline);
                else if (item is Mesh mesh) AddMesh(mesh, triangles, deadline);
            }
        }

        private static void AddMesh(Mesh mesh, List<List<XYZ>> triangles, DateTime deadline)
        {
            for (int index = 0; index < mesh.NumTriangles; index++)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                var triangle = mesh.get_Triangle(index);
                triangles.Add(Enumerable.Range(0, 3).Select(i => Unpack(Pack(triangle.get_Vertex(i)))).ToList());
                if (triangles.Count >= 4800)
                {
                    triangles.Sort((a, b) => TriangleArea(b).CompareTo(TriangleArea(a)));
                    triangles.RemoveRange(2400, triangles.Count - 2400);
                }
            }
        }

        private static double TriangleArea(List<XYZ> triangle) =>
            (triangle[1] - triangle[0]).CrossProduct(triangle[2] - triangle[0]).GetLength();

        private static bool IsShapeEdited(Floor floor)
        {
            return GetShapeEditor(floor)?.IsEnabled == true;
        }

        private static SlabShapeEditor GetShapeEditor(Floor floor)
        {
#if REVIT2024 || REVIT2025_OR_GREATER
            return floor.GetSlabShapeEditor();
#else
            return floor.SlabShapeEditor;
#endif
        }

        internal static HistoryCurve CaptureCurve(Curve curve)
        {
            if (!(curve is Line) && !(curve is Arc) || !curve.IsBound) return null;
            return new HistoryCurve { Start = Pack(curve.GetEndPoint(0)), End = Pack(curve.GetEndPoint(1)),
                Mid = curve is Arc ? Pack(curve.Evaluate(0.5, true)) : null };
        }
        internal static Curve RestoreCurve(HistoryCurve curve) => curve.Mid == null
            ? (Curve)Line.CreateBound(Unpack(curve.Start), Unpack(curve.End))
            : Arc.Create(Unpack(curve.Start), Unpack(curve.End), Unpack(curve.Mid));
        private static double[] Pack(XYZ p) => new[] { p.X, p.Y, p.Z };
        private static XYZ Unpack(double[] p) => new XYZ(p[0], p[1], p[2]);
        private static double Value(Element e, BuiltInParameter p) => e.get_Parameter(p)?.AsDouble() ?? 0;
        private static int Integer(Element e, BuiltInParameter p) => e.get_Parameter(p)?.AsInteger() ?? 0;
    }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    // Only recipes with a supported placement are captured. Complex hosts keep the
    // historical mesh. These DTOs contain no live Revit objects or numeric element IDs.
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    internal sealed class HistoryRecipe
    {
        public int Version { get; set; } = 1;
        public string Kind { get; set; }
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
    }

    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    internal sealed class HistoryCurve
    {
        public double[] Start { get; set; }
        public double[] End { get; set; }
        public double[] Mid { get; set; }
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

    internal static class ElementHistoryReconstruction
    {
        internal static HistoryRecipe Capture(Element element, Action<string> diagnostic = null)
        {
            string detail = null;
            var recipe = CaptureCore(element, message => detail = message);
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
            return recipe;
        }

        private static HistoryRecipe CaptureCore(Element element, Action<string> diagnostic)
        {
            try
            {
                var doc = element.Document;
                if (element is MEPCurve) return ElementHistoryNetwork.Capture(element);
                if (doc.IsFamilyDocument || !(element is FamilyInstance || element is Wall || element is Floor)) return null;
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
                    if (wall.WallType.Kind != WallKind.Basic || wall.SketchId != ElementId.InvalidElementId
                        || Math.Abs(Value(wall, BuiltInParameter.WALL_SINGLE_SLANT_ANGLE_FROM_VERTICAL)) > 1e-8
                        || Integer(wall, BuiltInParameter.WALL_CROSS_SECTION) != (int)WallCrossSection.Vertical
                        || Integer(wall, BuiltInParameter.WALL_TOP_IS_ATTACHED) != 0
                        || Integer(wall, BuiltInParameter.WALL_BOTTOM_IS_ATTACHED) != 0
                        || !(wall.Location is LocationCurve location)) return null;
                    var curve = CaptureCurve(location.Curve);
                    if (curve == null) return null;
                    recipe.Kind = "wall";
                    recipe.Loops = new List<List<HistoryCurve>> { new List<HistoryCurve> { curve } };
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
                    // Sloped/shape-edited floors retain their mesh, including their cut geometry.
                    if (Math.Abs(Value(floor, BuiltInParameter.ROOF_SLOPE)) > 1e-8
                        || IsShapeEdited(floor)
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
                            if (++count > 256) return null;
                            var item = CaptureCurve(curve);
                            if (item == null) return null;
                            curves.Add(item);
                        }
                        recipe.Loops.Add(curves);
                    }
                    if (count == 0) return null;
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
                return recipe != null && recipe.Version == 1 && !string.IsNullOrEmpty(recipe.Type)
                    && !string.IsNullOrEmpty(recipe.Level)
                    && new[] { "family", "wall", "floor", "network" }.Contains(recipe.Kind)
                    ? recipe : null;
            }
            catch { return null; }
        }

        // Caller owns a real transaction and decides whether to commit. Never substitutes
        // a mesh/DirectShape for a native element when the user requests restoration.
        internal static Element RestoreNative(Document doc, HistoryRecipe recipe)
        {
            string reason = null;
            var element = Create(doc, recipe, message => reason = message);
            if (element == null) throw new InvalidOperationException(reason ?? "Unsupported element placement.");
            doc.Regenerate();
            if (element.get_BoundingBox(null) == null)
                throw new InvalidOperationException("The restored element has no geometry.");
            return element;
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
            if (recipe.Kind == "network") return ElementHistoryNetwork.Create(doc, recipe);
            if (recipe.Kind == "family" && type is FamilySymbol symbol)
            {
                var host = string.IsNullOrEmpty(recipe.Host) ? null : doc.GetElement(recipe.Host);
                if (!string.IsNullOrEmpty(recipe.Host) && host == null) { diagnostic?.Invoke("Host is missing."); return null; }
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
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
                Curve curve = RestoreCurve(recipe.Loops.Single().Single());
                var wall = Wall.Create(doc, curve, type.Id, level.Id, recipe.Height, offset, recipe.Flipped, recipe.WallStructural);
                WallUtils.DisallowWallJoinAtEnd(wall, 0);
                WallUtils.DisallowWallJoinAtEnd(wall, 1);
                wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(recipe.LocationLine);
                ((LocationCurve)wall.Location).Curve = curve;
                return wall;
            }
            if (recipe.Kind == "floor" && type is FloorType)
            {
                var loops = recipe.Loops.Select(items => CurveLoop.Create(items.Select(RestoreCurve).ToList())).ToList();
                var floor = Floor.Create(doc, loops, type.Id, level.Id);
                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(offset);
                floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(recipe.FloorStructural ? 1 : 0);
                return floor;
            }
            return null;
        }

        internal static List<HistoryParameter> CaptureParameters(Element element)
        {
            var values = new List<HistoryParameter>();
            foreach (Parameter parameter in element.Parameters)
            {
                if (!parameter.HasValue || parameter.StorageType == StorageType.None) continue;
                long id = parameter.Id.GetIdLongValue();
                // Connected fittings can expose their instance dimensions as read-only.
                // Retain custom numeric values; replay only if writable on the new,
                // disconnected instance. Derived/formula values remain read-only.
                if (parameter.IsReadOnly && !(element is FamilyInstance && id >= 0 && parameter.StorageType == StorageType.Double)) continue;
                // Instance identity and host/level placement are handled separately.
                if (id == (int)BuiltInParameter.ALL_MODEL_MARK || id == (int)BuiltInParameter.ELEM_TYPE_PARAM
                    || id == (int)BuiltInParameter.FAMILY_LEVEL_PARAM) continue;
                var value = new HistoryParameter
                {
                    BuiltIn = id < 0 ? checked((int)id) : 0,
                    Shared = parameter.IsShared ? parameter.GUID.ToString() : null,
                    Definition = id >= 0 ? element.Document.GetElement(parameter.Id)?.UniqueId : null,
                    Storage = (int)parameter.StorageType
                };
                // Non-shared family parameters have no project ParameterElement.
                // Their name is scoped to this exact FamilySymbol, never to all families.
                if (value.BuiltIn == 0 && value.Shared == null && value.Definition == null)
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

        internal static void ApplyParameters(Document doc, Element element, List<HistoryParameter> values)
        {
            foreach (var value in values ?? new List<HistoryParameter>())
            {
                Parameter parameter = value.Shared != null ? element.get_Parameter(new Guid(value.Shared))
                    : value.BuiltIn < 0 ? element.get_Parameter((BuiltInParameter)value.BuiltIn)
                    : value.Name != null ? element.GetParameters(value.Name).SingleOrDefault()
                    : (doc.GetElement(value.Definition) is ParameterElement definition ? element.get_Parameter(definition.GetDefinition()) : null);
                if (parameter == null) throw new InvalidOperationException("Historical parameter is missing.");
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
#if REVIT2024 || REVIT2025_OR_GREATER
            return floor.GetSlabShapeEditor()?.IsEnabled == true;
#else
            return floor.SlabShapeEditor?.IsEnabled == true;
#endif
        }

        private static HistoryCurve CaptureCurve(Curve curve)
        {
            if (!(curve is Line) && !(curve is Arc) || !curve.IsBound) return null;
            return new HistoryCurve { Start = Pack(curve.GetEndPoint(0)), End = Pack(curve.GetEndPoint(1)),
                Mid = curve is Arc ? Pack(curve.Evaluate(0.5, true)) : null };
        }
        private static Curve RestoreCurve(HistoryCurve curve) => curve.Mid == null
            ? (Curve)Line.CreateBound(Unpack(curve.Start), Unpack(curve.End))
            : Arc.Create(Unpack(curve.Start), Unpack(curve.End), Unpack(curve.Mid));
        private static double[] Pack(XYZ p) => new[] { p.X, p.Y, p.Z };
        private static XYZ Unpack(double[] p) => new XYZ(p[0], p[1], p[2]);
        private static double Value(Element e, BuiltInParameter p) => e.get_Parameter(p)?.AsDouble() ?? 0;
        private static int Integer(Element e, BuiltInParameter p) => e.get_Parameter(p)?.AsInteger() ?? 0;
    }
}

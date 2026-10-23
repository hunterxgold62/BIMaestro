using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryReconstruction
    {
        // These values are cheap enough for the opening checkpoint's light inventory.
        // Never enumerate all parameters or geometry for this placement-only capture.
        internal static void CaptureHistoricalPlacement(Element element, HistoryRecipe recipe, bool includeBoundingBox = true)
        {
            // NewGroup may choose another logical origin even when all member
            // coordinates are identical. Members carry the physical placement.
            if (element == null || recipe == null || element is Group) return;
            if (recipe.HistoricalPlacement == null)
            {
                recipe.HistoricalPlacement = ReadHistoricalPlacement(element, includeBoundingBox);
                if (element is FamilyInstance)
                {
                    var elevations = new List<HistoryParameter>();
                    foreach (var name in new[] { "INSTANCE_ELEVATION_PARAM", "INSTANCE_FREE_HOST_OFFSET_PARAM",
                        "FAMILY_BASE_LEVEL_OFFSET_PARAM", "FAMILY_TOP_LEVEL_OFFSET_PARAM" })
                    {
                        if (!Enum.TryParse(name, out BuiltInParameter builtIn)) continue;
                        var parameter = element.get_Parameter(builtIn);
                        if (parameter?.HasValue == true && parameter.StorageType == StorageType.Double)
                            elevations.Add(new HistoryParameter { BuiltIn = (int)builtIn,
                                Storage = (int)StorageType.Double, Number = parameter.AsDouble() });
                    }
                    if (elevations.Count > 0) recipe.HistoricalPlacement.ElevationParameters = elevations;
                }
            }
            else if (includeBoundingBox && recipe.HistoricalPlacement.BBox == null)
            {
                var box = element.get_BoundingBox(null);
                if (box != null) recipe.HistoricalPlacement.BBox = new List<double[]> { Pack(box.Min), Pack(box.Max) };
            }
        }

        private static HistoryPlacement ReadHistoricalPlacement(Element element, bool includeBoundingBox)
        {
            var placement = new HistoryPlacement { LevelSourceUniqueId = FindLevel(element)?.UniqueId,
                HostSourceUniqueId = (element as FamilyInstance)?.Host?.UniqueId };
            if (element.Location is LocationPoint point) placement.Point = Pack(point.Point);
            else if (element.Location is LocationCurve curve) placement.Curve = CaptureCurve(curve.Curve);
            if (element is FamilyInstance family)
            {
                var frame = family.GetTransform();
                placement.Origin = Pack(frame.Origin); placement.BasisX = Pack(frame.BasisX);
                placement.BasisY = Pack(frame.BasisY); placement.BasisZ = Pack(frame.BasisZ);
                placement.HandFlipped = family.HandFlipped; placement.FacingFlipped = family.FacingFlipped;
                placement.Mirrored = family.Mirrored;
            }
            if (includeBoundingBox)
            {
                var box = element.get_BoundingBox(null);
                if (box != null) placement.BBox = new List<double[]> { Pack(box.Min), Pack(box.Max) };
            }
            return placement;
        }

        private static HistoryPlacement SavedHistoricalPlacement(HistoryRecipe recipe)
        {
            if (recipe?.HistoricalPlacement != null) return recipe.HistoricalPlacement;
            // Old persisted recipes still carry their family point and two frame axes.
            return recipe?.Kind == "family" ? new HistoryPlacement { Point = recipe.Point,
                BasisX = recipe.BasisX, BasisZ = recipe.BasisZ, Mirrored = recipe.Mirrored,
                HandFlipped = recipe.HandFlipped, FacingFlipped = recipe.FacingFlipped } : null;
        }

        internal static bool VerifyHistoricalPlacement(Element element, HistoryRecipe recipe, out string failure,
            bool includeBoundingBox = false)
        {
            failure = null;
            if (element is Group) return true;
            var saved = SavedHistoricalPlacement(recipe);
            if (saved == null) return true;
            if (element == null || !element.IsValidObject) { failure = "L’objet restauré est absent."; return false; }
            if (!string.IsNullOrEmpty(saved.LevelSourceUniqueId) && !NativeReferenceMatches(element.Document, saved.LevelSourceUniqueId, FindLevel(element)))
            { failure = "Le niveau historique de l’objet n’a pas été rétabli."; return false; }
            if (!string.IsNullOrEmpty(saved.HostSourceUniqueId) && (!(element is FamilyInstance hosted) || !NativeReferenceMatches(element.Document, saved.HostSourceUniqueId, hosted.Host)))
            { failure = "L’hôte historique de l’objet n’a pas été rétabli."; return false; }
            failure = saved.Difference(ReadHistoricalPlacement(element, includeBoundingBox), includeBoundingBox);
            return failure == null;
        }

        // Caller must restrict this helper to objects created by this restoration and
        // own an atomic transaction/group. It must not move a retained original neighbor.
        internal static void RestoreHistoricalPlacement(Document doc, Element element, HistoryRecipe recipe)
        {
            if (!(element is FamilyInstance instance))
            {
                var plain = SavedHistoricalPlacement(recipe);
                if (plain == null || element is Group) return;
                if (!doc.IsModifiable) throw new InvalidOperationException("Le recalage historique nécessite une transaction.");
                if (VerifyHistoricalPlacement(element, recipe, out _, true)) return;
                if (ElementHistoryNetwork.Ports(element).Any(port => port.IsConnected))
                    throw new InvalidOperationException("Le recalage déplacerait un objet déjà connecté ; opération annulée.");
                if (element.Location is LocationCurve locationCurve && plain.Curve != null)
                    locationCurve.Curve = RestoreCurve(plain.Curve);
                else if (element.Location is LocationPoint locationPoint && plain.Point != null)
                    ElementTransformUtils.MoveElement(doc, element.Id, Unpack(plain.Point) - locationPoint.Point);
                else throw new InvalidOperationException("Le placement historique de cet objet natif ne peut pas être recalé.");
                doc.Regenerate();
                if (!VerifyHistoricalPlacement(element, recipe, out var plainFailure, true)) throw new InvalidOperationException(plainFailure);
                return;
            }
            var saved = SavedHistoricalPlacement(recipe);
            if (saved == null) return;
            if (!doc.IsModifiable) throw new InvalidOperationException("Le recalage historique nécessite une transaction.");
            ReapplyHistoricalDimensions(doc, instance, recipe);
            if (VerifyHistoricalPlacement(instance, recipe, out _)
                && (saved.ElevationParameters ?? new List<HistoryParameter>()).All(value =>
                {
                    var parameter = instance.get_Parameter((BuiltInParameter)value.BuiltIn);
                    return parameter?.StorageType == StorageType.Double && Math.Abs(parameter.AsDouble() - value.Number) <= 1e-7;
                })) return;
            if (instance.GroupId != ElementId.InvalidElementId || instance.SuperComponent != null)
                throw new InvalidOperationException("Un composant généré ne peut pas être recalé indépendamment de son ensemble.");
            if (ElementHistoryNetwork.Ports(instance).Any(port => port.IsConnected))
                throw new InvalidOperationException("Le recalage déplacerait une famille déjà connectée ; opération annulée.");
            foreach (var elevation in saved.ElevationParameters ?? new List<HistoryParameter>())
            {
                var parameter = instance.get_Parameter((BuiltInParameter)elevation.BuiltIn);
                if (parameter?.StorageType == StorageType.Double && !parameter.IsReadOnly
                    && Math.Abs(parameter.AsDouble() - elevation.Number) > 1e-9) parameter.Set(elevation.Number);
            }
            doc.Regenerate();
            if (saved.HandFlipped.HasValue && instance.HandFlipped != saved.HandFlipped.Value)
            {
                if (!instance.CanFlipHand || !instance.flipHand()) throw new InvalidOperationException("Inversion de main historique impossible.");
            }
            if (saved.FacingFlipped.HasValue && instance.FacingFlipped != saved.FacingFlipped.Value)
            {
                if (!instance.CanFlipFacing || !instance.flipFacing()) throw new InvalidOperationException("Inversion de face historique impossible.");
            }
            doc.Regenerate();
            if (saved.Mirrored.HasValue && instance.Mirrored != saved.Mirrored.Value)
            {
                var origin = (instance.Location as LocationPoint)?.Point ?? instance.GetTransform().Origin;
                ElementTransformUtils.MirrorElements(doc, new[] { instance.Id }, Plane.CreateByNormalAndOrigin(XYZ.BasisX, origin), false);
                doc.Regenerate();
            }
            if (instance.Location is LocationPoint location)
            {
                if (saved.BasisX != null && saved.BasisZ != null)
                    ElementHistoryNetwork.Orient(doc, instance, Unpack(saved.BasisX), Unpack(saved.BasisZ));
                if (saved.Point != null)
                {
                    var delta = Unpack(saved.Point) - ((LocationPoint)instance.Location).Point;
                    if (delta.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, instance.Id, delta);
                }
            }
            else if (instance.Location is LocationCurve curve && saved.Curve != null)
                curve.Curve = RestoreCurve(saved.Curve);
            doc.Regenerate();
            if (!VerifyHistoricalPlacement(instance, recipe, out var failure)) throw new InvalidOperationException(failure);
        }

        private static Parameter HistoricalParameter(Document doc, Element element, HistoryParameter value)
        {
            var parameter = value.Shared != null ? element.get_Parameter(new Guid(value.Shared))
                : value.BuiltIn < 0 ? element.get_Parameter((BuiltInParameter)value.BuiltIn)
                : (!string.IsNullOrEmpty(value.Definition) && doc.GetElement(value.Definition) is ParameterElement definition
                    ? element.get_Parameter(definition.GetDefinition()) : null);
            return parameter ?? (value.Name == null ? null : element.GetParameters(value.Name).SingleOrDefault());
        }

        private static void ReapplyHistoricalDimensions(Document doc, FamilyInstance instance, HistoryRecipe recipe)
        {
            // Connected MEP fittings have a dedicated port-sizing solver. For other
            // families, replay dimensions after metadata/formula updates, which can
            // otherwise overwrite an earlier length/height on a new instance.
            if (instance.MEPModel != null || recipe.Parameters == null) return;
            var dimensions = recipe.Parameters.Where(value => value.Storage == (int)StorageType.Double)
                .Where(value => { var parameter = HistoricalParameter(doc, instance, value);
                    return parameter != null && IsGeometricParameter(parameter); }).ToList();
            if (dimensions.Count == 0) return;
            bool MatchesDimensions() => dimensions.All(value =>
            {
                var parameter = HistoricalParameter(doc, instance, value);
                return parameter != null && Math.Abs(parameter.AsDouble() - value.Number) <= 1e-7;
            });
            if (MatchesDimensions()) return;
            for (int pass = 0; pass < 2; pass++)
            {
                ApplyParameters(doc, instance, dimensions);
                doc.Regenerate();
                if (MatchesDimensions()) break;
            }
            foreach (var value in dimensions)
            {
                var parameter = HistoricalParameter(doc, instance, value);
                if (parameter == null || Math.Abs(parameter.AsDouble() - value.Number) > 1e-7)
                    throw new InvalidOperationException("Dimension historique non conservée après régénération : "
                        + (value.Name ?? parameter?.Definition.Name ?? value.Definition ?? value.Shared) + ".");
            }
        }

        private static readonly System.Reflection.MethodInfo ShapeAddPoint =
            typeof(SlabShapeEditor).GetMethod("AddPoint", new[] { typeof(XYZ) })
            ?? typeof(SlabShapeEditor).GetMethod("DrawPoint", new[] { typeof(XYZ) });
        private static readonly System.Reflection.MethodInfo ShapeAddSplitLine =
            typeof(SlabShapeEditor).GetMethod("AddSplitLine", new[] { typeof(SlabShapeVertex), typeof(SlabShapeVertex) })
            ?? typeof(SlabShapeEditor).GetMethod("DrawSplitLine", new[] { typeof(SlabShapeVertex), typeof(SlabShapeVertex) });

        private static object InvokeShapeMethod(System.Reflection.MethodInfo method, SlabShapeEditor editor, object[] arguments)
        {
            if (method == null) throw new NotSupportedException("Cette version de Revit ne permet pas de recréer les points ou lignes de forme du sol.");
            try { return method.Invoke(editor, arguments); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static void CaptureSlabShape(SlabShapeEditor editor, HistoryRecipe recipe)
        {
            recipe.ShapePoints = editor.SlabShapeVertices.Cast<SlabShapeVertex>().Select(v => Pack(v.Position)).ToList();
            recipe.ShapeCreases = editor.SlabShapeCreases.Cast<SlabShapeCrease>().Where(c => c.CreaseType == SlabShapeCreaseType.UserDrawn)
                .Select(c => CaptureCurve(c.Curve)).ToList();
            if (recipe.ShapeCreases.Any(c => c == null)) throw new InvalidOperationException("Ligne de forme du sol non prise en charge.");
        }

        private static void RestoreSlabShape(Document doc, SlabShapeEditor editor, HistoryRecipe recipe)
        {
            if (editor == null) throw new InvalidOperationException("L’éditeur de forme du sol est indisponible.");
            doc.Regenerate(); editor.Enable(); doc.Regenerate();
            var vertices = editor.SlabShapeVertices.Cast<SlabShapeVertex>().ToList();
            if (vertices.Count == 0) throw new InvalidOperationException("Le sol recréé ne possède pas de sommets de forme.");
            double baseZ = vertices[0].Position.Z;
            bool SameXY(XYZ a, double[] b) => Math.Abs(a.X - b[0]) < 1e-5 && Math.Abs(a.Y - b[1]) < 1e-5;
            // Insert every point into the initially flat top face before changing
            // elevations; otherwise the shape editor can reject points off that face.
            foreach (var saved in recipe.ShapePoints)
            {
                if (vertices.Any(v => SameXY(v.Position, saved))) continue;
                var vertex = (SlabShapeVertex)InvokeShapeMethod(ShapeAddPoint, editor,
                    new object[] { new XYZ(saved[0], saved[1], baseZ) });
                if (vertex == null) throw new InvalidOperationException("Un point de forme du sol n’a pas pu être recréé.");
                vertices = editor.SlabShapeVertices.Cast<SlabShapeVertex>().ToList();
            }
            foreach (var crease in recipe.ShapeCreases ?? new List<HistoryCurve>())
            {
                var a = vertices.Single(v => SameXY(v.Position, crease.Start));
                var b = vertices.Single(v => SameXY(v.Position, crease.End));
                InvokeShapeMethod(ShapeAddSplitLine, editor, new object[] { a, b });
            }
            foreach (var saved in recipe.ShapePoints)
            {
                var vertex = editor.SlabShapeVertices.Cast<SlabShapeVertex>().Single(v => SameXY(v.Position, saved));
                editor.ModifySubElement(vertex, saved[2] - baseZ);
            }
            doc.Regenerate();
            foreach (var saved in recipe.ShapePoints)
                if (!editor.SlabShapeVertices.Cast<SlabShapeVertex>().Any(v => v.Position.DistanceTo(Unpack(saved)) < 1e-5))
                    throw new InvalidOperationException("La forme recréée du sol ne correspond pas à la forme enregistrée.");
        }

        private static HistoryRecipe CaptureSketchHost(Element element, Action<string> diagnostic, bool includeDetails = true)
        {
            var doc = element.Document;
            var level = FindLevel(element);
            if (level == null || doc.GetElement(element.GetTypeId()) == null) return null;
            var recipe = new HistoryRecipe
            {
                Type = doc.GetElement(element.GetTypeId()).UniqueId, Level = level.UniqueId,
                LevelElevation = level.ProjectElevation, RestorationOrigins = ElementHistoryRestoration.GetOrigins(element),
                RequiresMeshPreview = !includeDetails || JoinGeometryUtils.GetJoinedElements(doc, element).Count > 0
                    || (element is HostObject host && host.FindInserts(true, true, true, true).Count > 0),
                Loops = new List<List<HistoryCurve>>()
            };
            if (element is FootPrintRoof roof)
            {
                if (GetShapeEditor(roof)?.IsEnabled == true)
                { diagnostic?.Invoke("Toiture modifiée par points : données de forme non prises en charge."); return null; }
                recipe.Kind = "roof";
                recipe.Offset = Value(roof, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                recipe.RoofEdges = new List<HistoryRoofEdge>();
                recipe.SketchCurves = new List<HistoryCurve>();
                foreach (ModelCurveArray loop in roof.GetProfiles())
                {
                    var curves = new List<HistoryCurve>();
                    foreach (ModelCurve model in loop)
                    {
                        var curve = CaptureCurve(model.GeometryCurve);
                        if (curve == null) return null;
                        curve.SourceUniqueId = model.UniqueId;
                        curve.RestorationOrigins = ElementHistoryRestoration.GetOrigins(model);
                        curves.Add(curve);
                        recipe.SketchCurves.Add(curve);
                        bool slope = roof.get_DefinesSlope(model);
                        recipe.RoofEdges.Add(new HistoryRoofEdge
                        {
                            DefinesSlope = slope, Slope = slope ? roof.get_SlopeAngle(model) : 0,
                            Offset = roof.get_Offset(model)
                        });
                    }
                    recipe.Loops.Add(curves);
                }
            }
            else if (element is Ceiling ceiling)
            {
                var sketch = doc.GetElement(ceiling.SketchId) as Sketch;
                if (sketch == null) return null;
                if (Math.Abs(Value(ceiling, BuiltInParameter.ROOF_SLOPE)) > 1e-8)
                { diagnostic?.Invoke("Plafond incliné : axe de pente non enregistré."); return null; }
                recipe.Kind = "ceiling";
                recipe.Offset = Value(ceiling, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                foreach (CurveArray loop in sketch.Profile)
                {
                    var curves = loop.Cast<Curve>().Select(CaptureCurve).ToList();
                    if (curves.Any(c => c == null)) return null;
                    recipe.Loops.Add(curves);
                }
                recipe.SketchCurves = CaptureSketchCurves(doc, sketch);
            }
            return recipe.Loops.Count > 0 ? recipe : null;
        }

        private static Element CreateSketchHost(Document doc, ElementType type, Level level, HistoryRecipe recipe)
        {
            double offset = recipe.Offset + recipe.LevelElevation - level.ProjectElevation;
            if (recipe.Kind == "ceiling" && type is CeilingType)
            {
                var ceiling = Ceiling.Create(doc, recipe.Loops.Select(l => CurveLoop.Create(l.Select(RestoreCurve).ToList())).ToList(), type.Id, level.Id);
                ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM).Set(offset);
                return ceiling;
            }
            if (recipe.Kind != "roof" || !(type is RoofType)) return null;
            // NewFootPrintRoof accepts multiple closed loops in one CurveArray.
            // Project the footprint to the level; its height is a separate parameter.
            var footprint = new CurveArray();
            foreach (var curve in recipe.Loops.SelectMany(l => l))
            {
                var native = RestoreCurve(curve);
                footprint.Append(native.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, level.ProjectElevation - native.GetEndPoint(0).Z))));
            }
            var mapping = new ModelCurveArray();
            var roof = doc.Create.NewFootPrintRoof(footprint, level, (RoofType)type, out mapping);
            if (recipe.RoofEdges == null || recipe.RoofEdges.Count != mapping.Size)
                throw new InvalidOperationException("Le contour de toiture recréé ne correspond pas au contour historique.");
            for (int i = 0; i < mapping.Size; i++)
            {
                var model = mapping.get_Item(i);
                var edge = recipe.RoofEdges[i];
                roof.set_DefinesSlope(model, edge.DefinesSlope);
                if (edge.DefinesSlope) roof.set_SlopeAngle(model, edge.Slope);
                roof.set_Offset(model, edge.Offset);
            }
            roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM).Set(offset);
            return roof;
        }

        internal static List<HistoryCurve> CaptureSketchCurves(Document doc, Sketch sketch)
        {
            var result = new List<HistoryCurve>();
            foreach (var model in sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>())
            {
                var curve = CaptureCurve(model.GeometryCurve);
                if (curve == null) continue;
                curve.SourceUniqueId = model.UniqueId;
                curve.RestorationOrigins = ElementHistoryRestoration.GetOrigins(model);
                result.Add(curve);
            }
            return result;
        }

        internal static IEnumerable<ModelCurve> SketchCurves(Document doc, Element element)
        {
            if (element is FootPrintRoof roof)
                return roof.GetProfiles().Cast<ModelCurveArray>().SelectMany(l => l.Cast<ModelCurve>());
            var id = element is Floor floor ? floor.SketchId : element is Ceiling ceiling ? ceiling.SketchId
                : element is Wall wall ? wall.SketchId : ElementId.InvalidElementId;
            var sketch = doc.GetElement(id) as Sketch;
            return sketch == null ? Enumerable.Empty<ModelCurve>() : sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>();
        }

        private static HistoryRecipe CaptureExtendedFamily(FamilyInstance instance, Action<string> diagnostic)
        {
            if (instance.SuperComponent != null || instance.Symbol.Family.IsInPlace) return null;
            var doc = instance.Document;
            var level = FindLevel(instance) ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => Math.Abs(l.ProjectElevation - instance.GetTransform().Origin.Z)).FirstOrDefault();
            if (level == null) return null;
            var placement = instance.Symbol.Family.FamilyPlacementType;
            var recipe = new HistoryRecipe
            {
                Kind = "family", Type = instance.Symbol.UniqueId, Level = level.UniqueId,
                RequiresMeshPreview = JoinGeometryUtils.GetJoinedElements(doc, instance).Count > 0,
                LevelElevation = level.ProjectElevation, Host = instance.Host?.UniqueId,
                Placement = placement.ToString(), Parameters = CaptureParameters(instance),
                BasisX = Pack(instance.GetTransform().BasisX), BasisZ = Pack(instance.GetTransform().BasisZ),
                HandFlipped = instance.HandFlipped, FacingFlipped = instance.FacingFlipped, Mirrored = instance.Mirrored,
                StructuralType = (int)instance.StructuralType, RestorationOrigins = ElementHistoryRestoration.GetOrigins(instance),
                CaptureWarnings = new List<string>(), WorkPlaneFlipped = placement == FamilyPlacementType.WorkPlaneBased
                    && instance.CanFlipWorkPlane && instance.IsWorkPlaneFlipped
            };
            if (placement == FamilyPlacementType.Adaptive)
            {
                recipe.AdaptivePoints = AdaptiveComponentInstanceUtils.GetInstancePointElementRefIds(instance)
                    .Select(id => Pack(((ReferencePoint)doc.GetElement(id)).Position)).ToList();
                // With no placement points, moving the instance changes its own
                // transform. The native archive preserves that transform exactly.
                if (recipe.AdaptivePoints.Count == 0) return null;
                recipe.AdaptiveFlipped = AdaptiveComponentInstanceUtils.IsInstanceFlipped(instance);
                recipe.Host = null;
            }
            else if ((placement == FamilyPlacementType.CurveBased || placement == FamilyPlacementType.CurveDrivenStructural)
                && instance.Location is LocationCurve curveLocation)
            {
                var curve = CaptureCurve(curveLocation.Curve);
                if (curve == null) return null;
                recipe.Loops = new List<List<HistoryCurve>> { new List<HistoryCurve> { curve } };
                recipe.Parameters = ParametersForCurveDrivenFamily(recipe);
            }
            else if (instance.Location is LocationPoint pointLocation && placement == FamilyPlacementType.TwoLevelsBased)
                recipe.Point = Pack(pointLocation.Point);
            else if (instance.Location is LocationPoint faceLocation && placement == FamilyPlacementType.WorkPlaneBased
                && instance.HostFace != null && instance.Host != null)
            {
                recipe.Point = Pack(faceLocation.Point);
                recipe.FaceReference = instance.HostFace.ConvertToStableRepresentation(doc);
                var face = instance.Host.GetGeometryObjectFromReference(instance.HostFace) as Face;
                var projection = face?.Project(faceLocation.Point);
                if (projection == null) return null;
                recipe.FaceNormal = Pack(face.ComputeNormal(projection.UVPoint));
            }
            else
            {
                diagnostic?.Invoke("Placement de famille non enregistré : " + placement + ".");
                return null;
            }
            recipe.Connections = ElementHistoryNetwork.CaptureConnections(instance, recipe.CaptureWarnings);
            recipe.Ports = ElementHistoryNetwork.CaptureFamilyPorts(instance);
            return recipe;
        }

        private static List<HistoryParameter> ParametersForCurveDrivenFamily(HistoryRecipe recipe)
        {
            if (recipe.Placement != FamilyPlacementType.CurveDrivenStructural.ToString() || recipe.Loops == null)
                return recipe.Parameters;
            // The absolute axis already determines both end elevations. Replaying
            // their level offsets can be rejected by Revit's structural placement
            // solver on a newly created beam. Keep every other historical value.
            return (recipe.Parameters ?? new List<HistoryParameter>())
                .Where(parameter => parameter.BuiltIn != (int)BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION
                    && parameter.BuiltIn != (int)BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION).ToList();
        }

        private static void VerifyCurveDrivenFamilyPlacement(Document doc, FamilyInstance instance, HistoryRecipe recipe)
        {
            if (recipe.Placement != FamilyPlacementType.CurveDrivenStructural.ToString() || recipe.Loops == null) return;
            doc.Regenerate();
            var expected = RestoreCurve(recipe.Loops.Single().Single());
            var actual = (instance.Location as LocationCurve)?.Curve;
            const double tolerance = 1e-5;
            bool SameEnd(int actualEnd, int expectedEnd) => actual.GetEndPoint(actualEnd)
                .DistanceTo(expected.GetEndPoint(expectedEnd)) < tolerance;
            if (actual == null || !actual.IsBound
                || !((SameEnd(0, 0) && SameEnd(1, 1)) || (SameEnd(0, 1) && SameEnd(1, 0)))
                || actual.Evaluate(0.5, true).DistanceTo(expected.Evaluate(0.5, true)) >= tolerance)
                throw new InvalidOperationException("La courbe historique de la poutre n’a pas pu être rétablie exactement.");
        }

        private static FamilyInstance CreateExtendedFamily(Document doc, FamilySymbol symbol, Level level, HistoryRecipe recipe)
        {
            FamilyInstance instance;
            if (recipe.Placement == FamilyPlacementType.Adaptive.ToString())
            {
                instance = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc, symbol);
                var ids = AdaptiveComponentInstanceUtils.GetInstancePointElementRefIds(instance);
                if (recipe.AdaptivePoints == null || ids.Count != recipe.AdaptivePoints.Count)
                    throw new InvalidOperationException("Le nombre de points adaptatifs a changé.");
                for (int i = 0; i < ids.Count; i++) ((ReferencePoint)doc.GetElement(ids[i])).Position = Unpack(recipe.AdaptivePoints[i]);
                AdaptiveComponentInstanceUtils.SetInstanceFlipped(instance, recipe.AdaptiveFlipped);
            }
            else if (recipe.Placement == FamilyPlacementType.WorkPlaneBased.ToString())
            {
                var host = doc.GetElement(recipe.Host);
                var reference = FindHostFace(doc, host, recipe);
                if (reference == null) throw new InvalidOperationException("La face du support historique n’a pas été retrouvée.");
                instance = doc.Create.NewFamilyInstance(reference, Unpack(recipe.Point), Unpack(recipe.BasisX), symbol);
                if (instance.CanFlipWorkPlane && instance.IsWorkPlaneFlipped != recipe.WorkPlaneFlipped) instance.IsWorkPlaneFlipped = recipe.WorkPlaneFlipped;
            }
            else if (recipe.Loops != null)
                instance = doc.Create.NewFamilyInstance(RestoreCurve(recipe.Loops.Single().Single()), symbol, level, (StructuralType)recipe.StructuralType);
            else
                instance = doc.Create.NewFamilyInstance(Unpack(recipe.Point), symbol, level, (StructuralType)recipe.StructuralType);
            // Also handle historical recipes captured before the endpoint-offset
            // exclusion was introduced, without mutating their persisted payload.
            ApplyParameters(doc, instance, ParametersForCurveDrivenFamily(recipe));
            doc.Regenerate();
            if (instance.HandFlipped != recipe.HandFlipped && instance.CanFlipHand) instance.flipHand();
            if (instance.FacingFlipped != recipe.FacingFlipped && instance.CanFlipFacing) instance.flipFacing();
            if (recipe.Placement == FamilyPlacementType.TwoLevelsBased.ToString())
            {
                if (instance.Mirrored != recipe.Mirrored)
                    ElementTransformUtils.MirrorElements(doc, new[] { instance.Id }, Plane.CreateByNormalAndOrigin(XYZ.BasisX, Unpack(recipe.Point)), false);
                doc.Regenerate();
                ElementHistoryNetwork.Orient(doc, instance, Unpack(recipe.BasisX), Unpack(recipe.BasisZ));
                ElementTransformUtils.MoveElement(doc, instance.Id, Unpack(recipe.Point) - ((LocationPoint)instance.Location).Point);
            }
            // Curve and face placements constrain the orientation themselves.
            // Verify instead of silently accepting a mirrored or flipped placement.
            if (recipe.Placement != FamilyPlacementType.Adaptive.ToString() && instance.Mirrored != recipe.Mirrored)
                throw new InvalidOperationException("Le miroir de cette famille n’a pas pu être rétabli.");
            ElementHistoryNetwork.RestoreFamilyPorts(doc, instance, recipe);
            VerifyCurveDrivenFamilyPlacement(doc, instance, recipe);
            return instance;
        }

        private static Reference FindHostFace(Document doc, Element host, HistoryRecipe recipe)
        {
            if (host == null) return null;
            var point = Unpack(recipe.Point);
            var normal = Unpack(recipe.FaceNormal);
            bool Matches(Face face)
            {
                var projection = face?.Project(point);
                return projection != null && projection.Distance < 1e-5 && face.IsInside(projection.UVPoint)
                    && face.ComputeNormal(projection.UVPoint).DistanceTo(normal) < 1e-4;
            }
            try
            {
                var reference = Reference.ParseFromStableRepresentation(doc, recipe.FaceReference);
                if (reference.ElementId == host.Id && Matches(host.GetGeometryObjectFromReference(reference) as Face)) return reference;
            }
            catch { }
            // A restored host has a new UniqueId. Match its actual geometry rather
            // than substituting identifiers into an obsolete stable reference.
            var matches = new List<Reference>();
            foreach (var solid in host.get_Geometry(new Options { ComputeReferences = true }).OfType<Solid>())
                foreach (Face face in solid.Faces)
                    if (Matches(face) && face.Reference != null) matches.Add(face.Reference);
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}

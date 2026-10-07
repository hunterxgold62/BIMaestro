using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryReconstruction
    {
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
            // elevations; otherwise DrawPoint can reject points off that face.
            foreach (var saved in recipe.ShapePoints)
            {
                if (vertices.Any(v => SameXY(v.Position, saved))) continue;
                var vertex = editor.DrawPoint(new XYZ(saved[0], saved[1], baseZ));
                if (vertex == null) throw new InvalidOperationException("Un point de forme du sol n’a pas pu être recréé.");
                vertices = editor.SlabShapeVertices.Cast<SlabShapeVertex>().ToList();
            }
            foreach (var crease in recipe.ShapeCreases ?? new List<HistoryCurve>())
            {
                var a = vertices.Single(v => SameXY(v.Position, crease.Start));
                var b = vertices.Single(v => SameXY(v.Position, crease.End));
                editor.DrawSplitLine(a, b);
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

        private static HistoryRecipe CaptureSketchHost(Element element, Action<string> diagnostic)
        {
            var doc = element.Document;
            var level = FindLevel(element);
            if (level == null || doc.GetElement(element.GetTypeId()) == null) return null;
            var recipe = new HistoryRecipe
            {
                Type = doc.GetElement(element.GetTypeId()).UniqueId, Level = level.UniqueId,
                LevelElevation = level.ProjectElevation, RestorationOrigins = ElementHistoryRestoration.GetOrigins(element),
                RequiresMeshPreview = JoinGeometryUtils.GetJoinedElements(doc, element).Count > 0
                    || (element is HostObject host && host.FindInserts(true, true, true, true).Count > 0),
                Loops = new List<List<HistoryCurve>>()
            };
            if (element is FootPrintRoof roof)
            {
#if REVIT2024 || REVIT2025_OR_GREATER
                if (roof.GetSlabShapeEditor()?.IsEnabled == true)
#else
                if (roof.SlabShapeEditor?.IsEnabled == true)
#endif
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
            ApplyParameters(doc, instance, recipe.Parameters);
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

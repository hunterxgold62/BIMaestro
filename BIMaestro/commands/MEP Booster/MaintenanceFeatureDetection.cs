using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal static class MaintenanceFeatureDetection
    {
        private sealed class Snapshot
        {
            internal readonly List<MaintenanceFeatureObservation> Features = new List<MaintenanceFeatureObservation>();
            internal readonly List<MaintenanceFeatureDimension> Dimensions = new List<MaintenanceFeatureDimension>();
            internal readonly List<string> Warnings = new List<string>();
        }
        private static readonly ConditionalWeakTable<Document, Dictionary<string, Snapshot>> Cache = new ConditionalWeakTable<Document, Dictionary<string, Snapshot>>();
        internal static void ClearCache(Document document) { if (document != null) Cache.Remove(document); }

        internal static MaintenanceFeatureSuggestion Detect(Document document, FamilyInstance source, MaintenanceAccessOperation operation)
        {
            if (document == null || source == null) throw new ArgumentException("L’équipement doit appartenir au document actif.");
            // Revit can return different managed wrappers for the same native
            // document. Resolve the persistent element identity in this document.
            source = document.GetElement(source.UniqueId) as FamilyInstance;
            if (source == null) throw new ArgumentException("L’équipement est introuvable dans le document actif.");
            var read = new Snapshot();
            string component = source.Symbol.Family.Name + " / " + source.Symbol.Name;
            ReadParameters(source, component, "instance", read);
            ReadParameters(source.Symbol, component, "type", read);
            ReadElement(source, Transform.Identity, component, "maquette", read);
            foreach (ElementId id in source.GetSubComponentIds())
                if (document.GetElement(id) is FamilyInstance nested)
                {
                    string name = nested.Symbol.Family.Name + " / " + nested.Symbol.Name;
                    ReadParameters(nested, name, "sous-composant partagé", read);
                    ReadParameters(nested.Symbol, name, "type du sous-composant", read);
                    ReadElement(nested, Transform.Identity, name, "maquette", read, true);
                }
            // With IncludeNonVisibleObjects=false the project instance is the
            // authority for the currently modeled handle position. A family
            // editor may still expose both its open and closed solid forms.
            foreach(var observation in read.Features)observation.ProjectVisible=true;
            var connectors = ReadConnectors(source);
            // A project instance often flattens non-shared nested components.
            // Read their names/parameters in a separate, unsaved family document.
            // Only detached scalar/vector observations survive the rollback.
            Snapshot probe = ProbeFamily(document, source);
            Transform world = source.GetTransform();
            var transformedParts=new Dictionary<XYZ[],XYZ[]>();
            XYZ[] Part(XYZ[] points)
            {
                if(points==null)return null;
                if(!transformedParts.TryGetValue(points,out XYZ[] mapped))
                {mapped=points.Select(world.OfPoint).ToArray();transformedParts[points]=mapped;}
                return mapped;
            }
            foreach (var o in probe.Features)
                read.Features.Add(new MaintenanceFeatureObservation { Center = world.OfPoint(o.Center), Axis = world.OfVector(o.Axis).Normalize(), Radius = o.Radius,
                    AxialLength = o.AxialLength, Coverage = o.Coverage, Cylinder = o.Cylinder, Linear = o.Linear, ComponentExplicit = o.ComponentExplicit, Component = o.Component, Origin = o.Origin,
                    CenterlineRadius = o.CenterlineRadius, RimThickness = o.RimThickness,
                    ComponentPoints = Part(o.ComponentPoints), CurvePoints = o.CurvePoints?.Select(world.OfPoint).ToArray(),
                    ComponentFrame = o.ComponentFrame == null ? null : world.Multiply(o.ComponentFrame) });
            read.Dimensions.AddRange(probe.Dimensions); read.Warnings.AddRange(probe.Warnings);
            var suggestion = MaintenanceFeatureRecognition.Infer(operation, read.Features, read.Dimensions, connectors);
            suggestion.Warnings.AddRange(read.Warnings.Distinct());
            return suggestion;
        }

        private static Snapshot ProbeFamily(Document host, FamilyInstance source)
        {
            var cache = Cache.GetValue(host, _ => new Dictionary<string, Snapshot>());
            var values = source.Parameters.Cast<Parameter>().Where(p => p.StorageType == StorageType.Double || p.StorageType == StorageType.Integer || p.StorageType == StorageType.String)
                .Select(p => p.Definition.Name + "=" + (p.StorageType == StorageType.Double ? p.AsDouble().ToString("R", CultureInfo.InvariantCulture)
                    : p.StorageType == StorageType.Integer ? p.AsInteger().ToString(CultureInfo.InvariantCulture) : p.AsString())).OrderBy(p => p).ToArray();
            string version = "";
            // VersionGuid is available on current APIs; reflection keeps this
            // cache fingerprint compatible with older Revit reference assemblies.
            try { version = typeof(Element).GetProperty("VersionGuid")?.GetValue(source.Symbol)?.ToString() ?? ""; } catch { }
            string key = source.Symbol.UniqueId + ":" + version + ":" + string.Join("|", values);
            if (cache.TryGetValue(key, out Snapshot cached)) return cached;
            var result = new Snapshot();
            if (host.IsModifiable || !source.Symbol.Family.IsEditable)
            { result.Warnings.Add("Lecture des sous-familles indisponible : repères basés uniquement sur la géométrie de l’instance."); return result; }
            Document family = null;
            try
            {
                family = host.EditFamily(source.Symbol.Family);
                var manager = family.FamilyManager;
                var type = manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == source.Symbol.Name);
                if (type == null) { result.Warnings.Add("Type de famille introuvable pour la lecture des composants."); return result; }
                using (var transaction = new Transaction(family, "Lecture temporaire des repères de maintenance"))
                {
                    transaction.Start(); manager.CurrentType = type;
                    foreach (FamilyParameter p in manager.Parameters)
                    {
                        if (!p.IsInstance || p.IsReadOnly || p.IsDeterminedByFormula) continue;
                        Parameter actual = source.LookupParameter(p.Definition.Name);
                        if (actual == null || actual.StorageType != p.StorageType) continue;
                        try
                        {
                            if (p.StorageType == StorageType.Double) manager.Set(p, actual.AsDouble());
                            else if (p.StorageType == StorageType.Integer) manager.Set(p, actual.AsInteger());
                            else if (p.StorageType == StorageType.String && actual.AsString() != null) manager.Set(p, actual.AsString());
                        }
                        catch (Exception error) when (GeometryError(error))
                        { result.Warnings.Add("Le paramètre d’instance « " + p.Definition.Name + " » n’a pas pu être reproduit dans la lecture de famille."); }
                    }
                    family.Regenerate();
                    foreach (FamilyInstance nested in new FilteredElementCollector(family).OfClass(typeof(FamilyInstance)))
                    {
                        string name = nested.Symbol.Family.Name + " / " + nested.Symbol.Name;
                        ReadParameters(nested, name, "famille temporaire, instance", result);
                        ReadParameters(nested.Symbol, name, "famille temporaire, type", result);
                        ReadElement(nested, Transform.Identity, name, "famille temporaire du type sélectionné", result, true);
                    }
                    // Own forms retain provenance instead of merging a cap with
                    // the rest of the body. Null/void forms do not stop siblings.
                    foreach (GenericForm form in new FilteredElementCollector(family).OfClass(typeof(GenericForm)))
                    {
                        if (!form.IsSolid) continue;
                        ReadElement(form, Transform.Identity, source.Symbol.Family.Name + " / " + form.GetType().Name + " " + BoosterIds.Value(form.Id), "forme de famille", result);
                    }
                    foreach (CurveElement curve in new FilteredElementCollector(family).OfClass(typeof(CurveElement)))
                        if (!(curve is SymbolicCurve)) ReadCurve(curve.GeometryCurve, Transform.Identity, source.Symbol.Family.Name + " / courbe modèle", "famille temporaire", result);
                    transaction.RollBack();
                }
            }
            catch (Exception error) when (GeometryError(error))
            { result.Warnings.Add("Lecture interne de famille indisponible : " + error.GetType().Name + ". Les ambiguïtés restent à confirmer manuellement."); }
            finally { if (family != null) family.Close(false); }
            if (cache.Count > 64) cache.Clear();
            cache[key] = result;
            return result;
        }

        private static void ReadParameters(Element source, string component, string origin, Snapshot result)
        {
            foreach (Parameter parameter in source.Parameters)
            {
                if (parameter.StorageType != StorageType.Double) continue;
                var kind = MaintenanceFeatureRecognition.DimensionKind(parameter.Definition.Name);
                bool angle=kind==MaintenanceFeatureDimensionKind.LeverRotation;
                if(parameter.Definition.GetDataType()!=(angle?SpecTypeId.Angle:SpecTypeId.Length))continue;
                double value = parameter.AsDouble();
                if(angle)value=value*180/Math.PI;
                if (!kind.HasValue || (!angle&&value<=0) ||Math.Abs(value)>(angle?720:100))continue;
                result.Dimensions.Add(new MaintenanceFeatureDimension { Kind = kind.Value, Name = parameter.Definition.Name,
                    Value = value, Component = component, Origin = origin });
            }
        }

        private static List<MaintenanceFeatureConnector> ReadConnectors(FamilyInstance source)
        {
            var result = new List<MaintenanceFeatureConnector>();
            var manager = source.MEPModel?.ConnectorManager;
            if (manager == null) return result;
            foreach (Connector connector in manager.Connectors)
            {
                if (connector.ConnectorType == ConnectorType.Logical || connector.Domain != Domain.DomainPiping) continue;
                try { result.Add(new MaintenanceFeatureConnector { Center = connector.Origin, Axis = connector.CoordinateSystem.BasisZ.Normalize(),
                    Radius = connector.Shape == ConnectorProfileType.Round ? connector.Radius : Math.Max(connector.Width, connector.Height) / 2 }); }
                catch (Exception error) when (GeometryError(error)) { }
            }
            return result;
        }

        private static void ReadElement(Element element, Transform transform, string component, string origin, Snapshot result, bool explicitComponent = false)
        {
            int first = result.Features.Count;
            try
            {
                using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false })
                using (var geometry = element.get_Geometry(options))
                    if (geometry != null) Read(geometry, transform, component, 0);
                void Read(GeometryElement geometry, Transform coordinates, string path, int depth)
                {
                    if (depth > 12) return;
                    foreach (GeometryObject item in geometry)
                    {
                        if (item is GeometryInstance instance)
                        {
                            using (var symbol = instance.GetSymbolGeometry())
                                if (symbol != null) Read(symbol, coordinates.Multiply(instance.Transform), path, depth + 1);
                        }
                        else if (item is Solid solid && solid.Volume > 1e-12)
                        {
                            var points = new List<XYZ>();
                            int start = result.Features.Count;
                            foreach (Face face in solid.Faces)
                            {
                                try
                                {
                                    var facePoints = FacePoints(face);
                                    points.AddRange(facePoints.Select(coordinates.OfPoint));
                                    if (face is CylindricalFace cylinder)
                                        AddFace(cylinder.Origin, cylinder.Axis, cylinder.get_Radius(0).GetLength(), facePoints, true);
                                    else if (face is RevolvedFace revolution)
                                    {
                                        XYZ axis = revolution.Axis.Normalize();
                                        double radius = facePoints.Count == 0 ? 0 : facePoints.Max(q => Radial(q - revolution.Origin, axis));
                                        var observed = AddFace(revolution.Origin, axis, radius, facePoints, false);
                                        if (observed != null)
                                        {
                                            double inner = facePoints.Min(q => Radial(q - revolution.Origin, axis));
                                            double section = radius - inner;
                                            if (inner > radius * .5 && section >= 2 / 304.8
                                                && Math.Abs(section - observed.AxialLength) <= Math.Max(2 / 304.8, section * .2))
                                            { observed.CenterlineRadius = (radius + inner) / 2; observed.RimThickness = section; }
                                        }
                                    }
                                    foreach (EdgeArray loop in face.EdgeLoops)
                                    foreach (Edge edge in loop) using (Curve curve = edge.AsCurve())
                                        if (curve is Arc) ReadCurve(curve, coordinates, path, origin, result);
                                }
                                catch (Exception error) when (GeometryError(error)) { }
                            }
                            XYZ[] detached = points.ToArray();
                            if (detached.Length > 0)
                                result.Features.Add(new MaintenanceFeatureObservation { Center = detached.Aggregate(XYZ.Zero, (a, b) => a + b) / detached.Length,
                                    Axis = coordinates.OfVector(XYZ.BasisZ).Normalize(), Component = path, Origin = origin, ComponentExplicit = explicitComponent });
                            for (int i = start; i < result.Features.Count; i++) result.Features[i].ComponentPoints = detached;
                            MaintenanceFeatureObservation AddFace(XYZ rawOrigin, XYZ rawAxis, double radius, List<XYZ> vertices, bool cylinder)
                            {
                                if (vertices.Count == 0 || radius < .001) return null;
                                XYZ axis = rawAxis.Normalize();
                                double low = vertices.Min(q => (q - rawOrigin).DotProduct(axis)), high = vertices.Max(q => (q - rawOrigin).DotProduct(axis));
                                var observed = new MaintenanceFeatureObservation { Center = coordinates.OfPoint(rawOrigin + axis * ((low + high) / 2)),
                                    Axis = coordinates.OfVector(axis).Normalize(), Radius = radius, AxialLength = high - low,
                                    Coverage = Coverage(vertices, rawOrigin, axis), Cylinder = cylinder, ComponentExplicit = explicitComponent, Component = path, Origin = origin };
                                result.Features.Add(observed); return observed;
                            }
                        }
                        else if (item is Curve curve) ReadCurve(curve, coordinates, path, origin, result);
                    }
                }
            }
            catch (Exception error) when (GeometryError(error))
            { result.Warnings.Add("Géométrie partielle pour " + component + " : " + error.GetType().Name + "."); }
            if (element is FamilyInstance familyInstance)
                for (int i = first; i < result.Features.Count; i++)
                { result.Features[i].ComponentFrame = transform.Multiply(familyInstance.GetTransform()); result.Features[i].ComponentExplicit = explicitComponent; }
        }

        private static List<XYZ> FacePoints(Face face)
        {
            using (Mesh mesh = face.Triangulate())
            {
                if (mesh != null && mesh.Vertices.Count > 0) return mesh.Vertices.ToList();
            }
            var points = new List<XYZ>();
            foreach (EdgeArray loop in face.EdgeLoops) foreach (Edge edge in loop) points.AddRange(edge.Tessellate());
            return points;
        }
        private static void ReadCurve(Curve curve, Transform coordinates, string component, string origin, Snapshot result)
        {
            if (curve is Arc arc)
                result.Features.Add(new MaintenanceFeatureObservation { Center = coordinates.OfPoint(arc.Center), Axis = coordinates.OfVector(arc.Normal).Normalize(),
                    Radius = arc.Radius, Coverage = Math.Min(2 * Math.PI, arc.Length / arc.Radius), Component = component, Origin = origin,
                    CurvePoints = arc.Tessellate().Select(coordinates.OfPoint).ToArray() });
            else if (curve is Line && curve.IsBound && curve.Length > .1)
            {
                XYZ a = coordinates.OfPoint(curve.GetEndPoint(0)), b = coordinates.OfPoint(curve.GetEndPoint(1));
                result.Features.Add(new MaintenanceFeatureObservation { Center = (a + b) / 2, Axis = (b - a).Normalize(), AxialLength = (b - a).GetLength(),
                    Linear = true, Component = component, Origin = origin, ComponentPoints = new[] { a, b } });
            }
        }
        private static double Coverage(List<XYZ> points, XYZ origin, XYZ axis)
        {
            XYZ x = XYZ.BasisZ.CrossProduct(axis); if (x.GetLength() < 1e-6) x = XYZ.BasisX; x = x.Normalize(); XYZ y = axis.CrossProduct(x);
            double[] angles = points.Select(p => p - origin).Where(p => Radial(p, axis) > .001)
                .Select(p => Math.Atan2(p.DotProduct(y), p.DotProduct(x))).OrderBy(a => a).ToArray();
            if (angles.Length < 3) return 0;
            double gap = angles[0] + 2 * Math.PI - angles[angles.Length - 1];
            for (int i = 1; i < angles.Length; i++) gap = Math.Max(gap, angles[i] - angles[i - 1]);
            return 2 * Math.PI - gap;
        }
        private static double Radial(XYZ point, XYZ axis) => (point - axis * point.DotProduct(axis)).GetLength();
        private static bool GeometryError(Exception error) => error is Autodesk.Revit.Exceptions.ApplicationException || error is ArgumentException || error is InvalidOperationException;
    }
}

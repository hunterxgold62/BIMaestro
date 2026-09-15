using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    // Native coordinates remain in Revit. The model selects observed contours by opaque id.
    internal sealed class CodexSelectionGeometry
    {
        private sealed class Snapshot
        {
            internal string ElementUniqueId, Signature;
            internal int Face, Loop;
        }
        private readonly Dictionary<string, Snapshot> contours = new Dictionary<string, Snapshot>();

        internal object Read(UIApplication app, Document doc)
        {
            contours.Clear();
            var selected = app.ActiveUIDocument.Selection.GetElementIds();
            var elements = new List<object>();
            int remainingEdges = 400;
            foreach (var id in selected.Take(20))
            {
                var element = doc.GetElement(id);
                if (element == null) continue;
                var loops = new List<object>();
                string error = null;
                if (element is Floor floor)
                {
                    try
                    {
                        var faces = HostObjectUtils.GetTopFaces(floor);
                        if (faces.Count > 30) throw new InvalidOperationException("Plus de 30 faces supérieures : lecture des contours non disponible pour ce sol.");
                        for (int f = 0; f < faces.Count; f++)
                        {
                            var face = floor.GetGeometryObjectFromReference(faces[f]) as Face;
                            if (face == null) continue;
                            var faceLoops = face.GetEdgesAsCurveLoops();
                            if (faceLoops.Count > 30) throw new InvalidOperationException("Plus de 30 contours sur une face.");
                            for (int l = 0; l < faceLoops.Count; l++)
                            {
                                using (var loop = faceLoops[l])
                                {
                                    var curves = loop.ToList();
                                    if (curves.Count > Math.Min(200, remainingEdges)) { loops.Add(new { unavailable = "Limite de lecture atteinte (200 arêtes par contour, 400 au total). Réduire la sélection.", face_index = f, loop_index = l }); continue; }
                                    remainingEdges -= curves.Count;
                                    string key = Guid.NewGuid().ToString("N");
                                    contours[key] = new Snapshot { ElementUniqueId = floor.UniqueId, Face = f, Loop = l, Signature = Signature(curves) };
                                    bool horizontal = face is PlanarFace plane && Math.Abs(plane.FaceNormal.Z) > 0.999999;
                                    loops.Add(new { contour_id = key, face_index = f, loop_index = l, horizontal,
                                        counterclockwise_from_above = horizontal ? (bool?)loop.IsCounterclockwise(XYZ.BasisZ) : null,
                                        can_create_walls = horizontal && curves.All(c => c.IsBound && (c is Line || c is Arc)),
                                        edges = curves.Select((c, i) => new { edge_index = i, geometry = Describe(c) }).ToArray() });
                                }
                            }
                        }
                    }
                    catch (Exception ex) { error = ex.Message; }
                }
                var box = element.get_BoundingBox(null);
                var corners = box == null ? null : Enumerable.Range(0, 8).Select(i => Mm(box.Transform.OfPoint(new XYZ(
                    (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z)))).ToArray();
                elements.Add(new { id = id.ToString(), name = element.Name, category = element.Category?.Name,
                    bounding_box_corners_mm = corners,
                    location_point_mm = element.Location is LocationPoint point ? Mm(point.Point) : null,
                    location_curve = element.Location is LocationCurve location ? Describe(location.Curve) : null,
                    floor_top_contours = loops, error });
            }
            return new { coordinate_system = "Coordonnées INTERNES Revit en mm, Z vertical. Pas les coordonnées partagées. Les contours sont ceux des faces supérieures, réservations comprises. Ne pas supposer que le premier contour est extérieur. Plusieurs sols ne sont pas fusionnés.",
                selected_count = selected.Count, truncated = selected.Count > 20, elements,
                wall_types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic)
                    .Select(t => new { id = t.Id.ToString(), name = t.Name, width_mm = t.Width * 304.8 }).Take(200).ToArray() };
        }

        internal List<Curve> Resolve(UIApplication app, Document doc, JArray requests)
        {
            var curves = new List<Curve>();
            try
            {
                foreach (var token in requests)
                {
                    var request = token as JObject;
                    CodexFamilyDesign.Keys(request, "contour_id", "edge_indices");
                    string key = CodexFamilyDesign.String(request, "contour_id", 50);
                    if (!contours.TryGetValue(key, out var snapshot)) throw new InvalidOperationException("Contour inconnu ou expiré. Relire revit_selection_geometry.");
                    var floor = doc.GetElement(snapshot.ElementUniqueId) as Floor;
                    if (floor == null || !app.ActiveUIDocument.Selection.GetElementIds().Contains(floor.Id))
                        throw new InvalidOperationException("Le sol doit toujours être sélectionné. Relire la sélection avant de créer.");
                    var faces = HostObjectUtils.GetTopFaces(floor);
                    if (snapshot.Face >= faces.Count) throw new InvalidOperationException("Le sol a changé. Relire ses contours.");
                    var face = floor.GetGeometryObjectFromReference(faces[snapshot.Face]) as PlanarFace;
                    if (face == null || Math.Abs(face.FaceNormal.Z) < 0.999999) throw new InvalidOperationException("Les murs sur un sol incliné ou non plan ne sont pas pris en charge.");
                    var loops = face.GetEdgesAsCurveLoops();
                    if (snapshot.Loop >= loops.Count) throw new InvalidOperationException("Le contour a changé. Relire la sélection.");
                    using (var loop = loops[snapshot.Loop])
                    {
                        var edges = loop.ToList();
                        if (Signature(edges) != snapshot.Signature) throw new InvalidOperationException("La géométrie du sol a changé. Relire ses contours.");
                        var indices = CodexFamilyDesign.Items(request, "edge_indices", 0, 200).Select(t => {
                            double value = CodexFamilyDesign.Scalar(t, "edge_index", 0, edges.Count - 1);
                            if (value != Math.Truncate(value)) throw new InvalidOperationException("Indice d'arête entier attendu.");
                            return (int)value;
                        }).ToList();
                        if (indices.Count == 0) indices = Enumerable.Range(0, edges.Count).ToList();
                        if (indices.Distinct().Count() != indices.Count) throw new InvalidOperationException("Arête demandée plusieurs fois.");
                        foreach (int index in indices)
                        {
                            var edge = edges[index];
                            if (!edge.IsBound || !(edge is Line || edge is Arc) || Math.Abs(edge.GetEndPoint(0).Z - edge.GetEndPoint(1).Z) > 1e-6)
                                throw new InvalidOperationException("Arête " + index + " incompatible : seuls les segments et arcs horizontaux sont pris en charge.");
                            curves.Add(edge.Clone());
                            if (curves.Count > 200) throw new InvalidOperationException("Maximum 200 murs par opération.");
                        }
                    }
                }
                var unique = new HashSet<string>();
                foreach (var curve in curves)
                {
                    string a = JsonConvert.SerializeObject(Mm(curve.GetEndPoint(0))), b = JsonConvert.SerializeObject(Mm(curve.GetEndPoint(1)));
                    string key = string.CompareOrdinal(a, b) < 0 ? a + b : b + a;
                    key += JsonConvert.SerializeObject(Mm(curve.Evaluate(0.5, true)));
                    if (!unique.Add(key)) throw new InvalidOperationException("Deux arêtes choisies se superposent. Choisir une seule fois la limite commune aux sols.");
                }
                return curves;
            }
            catch { foreach (var curve in curves) curve.Dispose(); throw; }
        }

        private static string Signature(IEnumerable<Curve> curves) => JsonConvert.SerializeObject(curves.Select(Describe));
        private static object Describe(Curve curve)
        {
            var points = curve.Tessellate();
            return new { kind = curve.GetType().Name, length_mm = curve.Length * 304.8,
                start_mm = curve.IsBound ? Mm(curve.GetEndPoint(0)) : null, end_mm = curve.IsBound ? Mm(curve.GetEndPoint(1)) : null,
                midpoint_mm = curve.IsBound ? Mm(curve.Evaluate(0.5, true)) : null,
                center_mm = curve is Arc arc ? Mm(arc.Center) : null, radius_mm = curve is Arc circle ? (double?)(circle.Radius * 304.8) : null,
                samples_mm = points.Count <= 8 ? points.Select(Mm).ToArray() : Enumerable.Range(0, 8).Select(i => Mm(points[i * (points.Count - 1) / 7])).ToArray(), samples_truncated = points.Count > 8 };
        }
        private static double[] Mm(XYZ point) => new[] { point.X * 304.8, point.Y * 304.8, point.Z * 304.8 };
    }
}

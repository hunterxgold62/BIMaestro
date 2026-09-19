using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal static class CodexProfileBuilder
    {
        internal static XYZ[] Points(ParametricPart part, Dictionary<string, double> values, bool top = false)
        {
            var uv = part.ProfileAxes; double height = (top ? part.Max : part.Min)[part.Axis].Value(values);
            return part.Profile.Select(p => (Basis(part.Axis) * height + Basis(uv[0]) * p[0].Value(values) + Basis(uv[1]) * p[1].Value(values)) / 304.8).ToArray();
        }
        internal static CurveArray Profile(ParametricPart part, Dictionary<string, double> values)
        {
            var points = Points(part, values); var result = new CurveArray();
            for (int i = 0; i < points.Length; i++) result.Append(CodexCreationGuard.CreateLine(points[i], points[(i + 1) % points.Length]));
            return result;
        }
        internal static void Constrain(Document doc, Extrusion extrusion, ParametricPart part, Dictionary<string, double> values, CodexParametricBuilder constraints)
        {
            // Fixed decorative profiles need no vertex drivers. Hundreds of
            // intersecting reference lines trigger expensive Revit auto-joins.
            if (part.Profile.All(p => p.All(e => e.Terms.Count == 0))) return;
            var points = Points(part, values); var uv = part.ProfileAxes;
            doc.Regenerate();
            var view = constraints.ProfileView(part.Axis);
            for (int i = 0; i < points.Length; i++)
            {
                CodexCreationGuard.Check();
                var start = points[i]; var end = points[(i + 1) % points.Length];
                var edge = extrusion.Sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>().Single(c => Same(c.GeometryCurve, start, end));
                int endpoint = edge.GeometryCurve.GetEndPoint(0).DistanceTo(start) < 1e-6 ? 0 : 1;
                for (int a = 0; a < 2; a++)
                {
                    var direction = Basis(uv[1 - a]);
                    var guide = doc.FamilyCreate.NewModelCurve(CodexCreationGuard.CreateLine(start - direction * (5 / 304.8), start + direction * (5 / 304.8)), extrusion.Sketch.SketchPlane);
                    guide.ChangeToReferenceLine();
                    var plane = constraints.PlaneAt(uv[a], part.Profile[i][a]); doc.Regenerate();
                    // Regeneration may replace native curve references.
                    edge = extrusion.Sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>().Single(c => Same(c.GeometryCurve, start, end));
                    endpoint = edge.GeometryCurve.GetEndPoint(0).DistanceTo(start) < 1e-6 ? 0 : 1;
                    doc.FamilyCreate.NewAlignment(view, plane.GetReference(), guide.GeometryCurve.Reference);
                    doc.FamilyCreate.NewAlignment(view, guide.GeometryCurve.Reference, edge.GeometryCurve.GetEndPointReference(endpoint));
                }
            }
        }
        internal static void Check(Extrusion extrusion, ParametricPart part, Dictionary<string, double> values)
        {
            using (var geometry = extrusion.get_Geometry(new Options { IncludeNonVisibleObjects = true }))
            {
                var actual = geometry.OfType<Solid>().SelectMany(s => s.Edges.Cast<Edge>()).SelectMany(e => e.AsCurve().Tessellate()).ToArray();
                foreach (var point in Points(part, values).Concat(Points(part, values, true)))
                    if (!actual.Any(p => p.DistanceTo(point) * 304.8 < 0.5)) throw new InvalidOperationException("Un sommet du profil ne suit pas sa contrainte : " + part.Name);
            }
        }
        private static bool Same(Curve curve, XYZ a, XYZ b) => curve.GetEndPoint(0).DistanceTo(a) < 1e-6 && curve.GetEndPoint(1).DistanceTo(b) < 1e-6 || curve.GetEndPoint(0).DistanceTo(b) < 1e-6 && curve.GetEndPoint(1).DistanceTo(a) < 1e-6;
        private static XYZ Basis(int axis) => axis == 0 ? XYZ.BasisX : axis == 1 ? XYZ.BasisY : XYZ.BasisZ;
    }
}

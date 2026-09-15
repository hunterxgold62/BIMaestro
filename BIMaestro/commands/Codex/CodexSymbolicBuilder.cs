using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class CodexSymbolicBuilder
    {
        private readonly List<Tuple<FamilySymbolicSpec, SymbolicCurve[]>> outlines = new List<Tuple<FamilySymbolicSpec, SymbolicCurve[]>>();
        private readonly CodexParametricDesign design;
        internal CodexSymbolicBuilder(Document doc, CodexParametricDesign design, CodexParametricBuilder constraints)
        {
            this.design = design;
            foreach (var spec in design.Symbols)
            {
                var part = design.Parts.Single(p => p.Name == spec.Component); var uv = Enumerable.Range(0, 3).Where(a => a != spec.Axis).ToArray();
                var points = Points(part, spec.Axis, design.Initial); var curves = new SymbolicCurve[4];
                var work = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(Basis(spec.Axis), XYZ.Zero));
                try
                {
                    for (int i = 0; i < 4; i++)
                    {
                        using (var line = Line.CreateBound(points[i], points[(i + 1) % 4])) curves[i] = doc.FamilyCreate.NewSymbolicCurve(line, work);
                        using (var visibility = new FamilyElementVisibility(FamilyElementVisibilityType.ViewSpecific)
                        { IsShownInCoarse = spec.Coarse, IsShownInMedium = spec.Medium, IsShownInFine = spec.Fine }) curves[i].SetVisibility(visibility);
                        if (!string.IsNullOrEmpty(spec.VisibleParameter)) constraints.AssociateBoolean(curves[i].get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), spec.VisibleParameter);
                    }
                    doc.Regenerate();
                    var view = constraints.ProfileView(spec.Axis);
                    for (int i = 0; i < 4; i++)
                    {
                        int axis = i % 2 == 0 ? uv[1] : uv[0]; var bound = i == 0 || i == 3 ? part.Min[axis] : part.Max[axis];
                        var reference = constraints.PlaneAt(axis, bound).GetReference(); doc.Regenerate();
                        doc.FamilyCreate.NewAlignment(view, reference, curves[i].GeometryCurve.Reference);
                        var curve = curves[i].GeometryCurve;
                        for (int end = 0; end < 2; end++)
                            doc.FamilyCreate.NewAlignment(view, curves[(i + (end == 0 ? 3 : 1)) % 4].GeometryCurve.Reference, curve.GetEndPointReference(end));
                    }
                    outlines.Add(Tuple.Create(spec, curves));
                }
                catch (Exception ex) { throw new InvalidOperationException("Contour symbolique de « " + spec.Component + " » : " + ex.Message, ex); }
            }
        }
        internal void Check(Dictionary<string, double> values)
        {
            foreach (var pair in outlines)
            {
                var expected = Points(design.Parts.Single(p => p.Name == pair.Item1.Component), pair.Item1.Axis, values);
                for (int i = 0; i < 4; i++)
                {
                    var curve = pair.Item2[i].GeometryCurve;
                    if (curve.GetEndPoint(0).DistanceTo(expected[i]) * 304.8 > 0.5 || curve.GetEndPoint(1).DistanceTo(expected[(i + 1) % 4]) * 304.8 > 0.5)
                        throw new InvalidOperationException("Le contour 2D ne suit pas les dimensions : " + pair.Item1.Component);
                    var spec = pair.Item1;
                    using (var visibility = pair.Item2[i].GetVisibility())
                        if (visibility.IsShownInCoarse != spec.Coarse || visibility.IsShownInMedium != spec.Medium || visibility.IsShownInFine != spec.Fine)
                            throw new InvalidOperationException("Le contour 2D ne respecte pas les niveaux de détail : " + spec.Component);
                    if (!string.IsNullOrEmpty(spec.VisibleParameter) && pair.Item2[i].get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM).AsInteger() != (int)values[spec.VisibleParameter])
                        throw new InvalidOperationException("Le contour 2D ne suit pas sa visibilité : " + spec.Component);
                }
            }
        }
        private static XYZ[] Points(ParametricPart part, int axis, Dictionary<string, double> values)
        {
            var uv = Enumerable.Range(0, 3).Where(a => a != axis).ToArray();
            double u0 = part.Min[uv[0]].Value(values), u1 = part.Max[uv[0]].Value(values), v0 = part.Min[uv[1]].Value(values), v1 = part.Max[uv[1]].Value(values);
            return new[] { new[] { u0, v0 }, new[] { u1, v0 }, new[] { u1, v1 }, new[] { u0, v1 } }.Select(p => (Basis(uv[0]) * p[0] + Basis(uv[1]) * p[1]) / 304.8).ToArray();
        }
        private static XYZ Basis(int axis) => axis == 0 ? XYZ.BasisX : axis == 1 ? XYZ.BasisY : XYZ.BasisZ;
    }
}

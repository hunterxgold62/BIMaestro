using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    // Drive the sketch vertices with native trigonometric length formulas. Adding
    // locked right angles to an already rectangular sketch overconstrains Revit.
    // The public angle and dimensions remain editable without the add-in.
    internal sealed class CodexAngularBarBuilder
    {
        private readonly Document doc;
        private readonly int axis;
        private readonly double[] initialSize;
        private readonly double initialAngle;
        private readonly FamilyParameter[] sizes = new FamilyParameter[3];
        private readonly FamilyParameter angle;
        private readonly Extrusion extrusion;

        internal CodexAngularBarBuilder(Document doc, double[] size, int axis, double degrees, FamilyParameter material)
        {
            this.doc = doc; this.axis = axis; initialSize = size; initialAngle = degrees;
            var manager = doc.FamilyManager;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Section)).ToArray();
            var byAxis = Enumerable.Range(0, 3).Select(a => views.FirstOrDefault(v => Math.Abs(v.ViewDirection.DotProduct(Basis(a))) > 0.999)).ToArray();
            if (byAxis.Any(v => v == null)) throw new InvalidOperationException("Le gabarit doit fournir des vues selon X, Y et Z pour les cotes angulaires.");
            for (int a = 0; a < 3; a++)
            {
                XYZ start, end, cut;
                if (a == 0) { start = -XYZ.BasisY; end = XYZ.BasisY; cut = XYZ.BasisZ; }
                else if (a == 1) { start = -XYZ.BasisX; end = XYZ.BasisX; cut = XYZ.BasisZ; }
                else { start = -XYZ.BasisX; end = XYZ.BasisX; cut = XYZ.BasisY; }
                var origin = doc.FamilyCreate.NewReferencePlane(start, end, cut, a == 2 ? byAxis[1] : byAxis[2]);
                origin.Name = "BIM_Origine_" + "XYZ"[a]; origin.Pinned = true;
                origin.get_Parameter(BuiltInParameter.ELEM_IS_REFERENCE).Set((int)FamilyInstanceReferenceType.StrongReference);
                sizes[a] = CodexParameterBuilder.GetOrAdd(manager, "Dimension" + "XYZ"[a], GroupTypeId.Geometry, SpecTypeId.Length, false);
                manager.Set(sizes[a], size[a] / 304.8);
            }
            angle = CodexParameterBuilder.GetOrAdd(manager, "Inclinaison", GroupTypeId.Geometry, SpecTypeId.Angle, false);
            manager.Set(angle, degrees * Math.PI / 180);
            int u = (axis + 1) % 3, vAxis = (axis + 2) % 3;
            var rotate = Transform.CreateRotation(Basis(axis), degrees * Math.PI / 180);
            XYZ du = rotate.OfVector(Basis(u)), dv = rotate.OfVector(Basis(vAxis));
            double width = size[u] / 304.8, height = size[vAxis] / 304.8;
            var points = new[] { XYZ.Zero, du * width, du * width + dv * height, dv * height };
            var profile = new CurveArray();
            for (int i = 0; i < 4; i++) profile.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
            var profiles = new CurveArrArray(); profiles.Append(profile);
            var workPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(Basis(axis), XYZ.Zero));
            extrusion = doc.FamilyCreate.NewExtrusion(true, profiles, workPlane, size[axis] / 304.8);
            manager.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), sizes[axis]);
            manager.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), material);
            doc.Regenerate();
            var sketchLines = extrusion.Sketch.GetAllElements().Select(id => doc.GetElement(id)).OfType<ModelCurve>().ToArray();
            var edges = Enumerable.Range(0, 4).Select(i => sketchLines.Single(e => SameEdge(e.GeometryCurve, points[i], points[(i + 1) % 4]))).ToArray();
            var baseU = ReferenceLine(workPlane, Basis(u));
            var baseV = ReferenceLine(workPlane, Basis(vAxis));
            var curve0 = edges[0].GeometryCurve;
            var pivot = curve0.GetEndPointReference(curve0.GetEndPoint(0).DistanceTo(XYZ.Zero) < 1e-6 ? 0 : 1);
            if (pivot == null) throw new InvalidOperationException("Référence du pivot d'inclinaison absente.");
            doc.FamilyCreate.NewAlignment(byAxis[axis], baseU.GeometryCurve.Reference, pivot);
            doc.FamilyCreate.NewAlignment(byAxis[axis], baseV.GeometryCurve.Reference, pivot);

            string w = "Dimension" + "XYZ"[u], h = "Dimension" + "XYZ"[vAxis];
            var formulas = new[] { new[] { "0 mm", "0 mm" },
                new[] { w + " * cos(Inclinaison)", w + " * sin(Inclinaison)" },
                new[] { w + " * cos(Inclinaison) - " + h + " * sin(Inclinaison)", w + " * sin(Inclinaison) + " + h + " * cos(Inclinaison)" },
                new[] { "-" + h + " * sin(Inclinaison)", h + " * cos(Inclinaison)" } };
            // A fixed datum outside the permitted 100 m dimensions keeps labeled
            // lengths positive even when a rotated corner crosses local zero.
            const double datumMm = 200000;
            var axes = new[] { u, vAxis };
            var datums = axes.Select(a => CoordinatePlane(byAxis[axis], a, -datumMm / 304.8, "BIM_Datum_" + "XYZ"[a])).ToArray();
            foreach (var datum in datums) datum.Pinned = true;
            for (int i = 1; i <= 3; i++)
            {
                int endpoint = edges[i].GeometryCurve.GetEndPoint(0).DistanceTo(points[i]) < 1e-6 ? 0 : 1;
                for (int a = 0; a < 2; a++)
                {
                    double coordinate = points[i].DotProduct(Basis(axes[a]));
                    var target = CoordinatePlane(byAxis[axis], axes[a], coordinate, "BIM_Sommet_" + i + "_" + a);
                    var driver = CodexParameterBuilder.NewInternal(manager, "BIM_Position_" + i + "_" + a, GroupTypeId.Constraints, SpecTypeId.Length, true);
                    manager.Set(driver, datumMm / 304.8 + coordinate);
                    manager.SetFormula(driver, CodexParameterBuilder.NativeFormula("200000 mm + (" + formulas[i][a] + ")", new Dictionary<string, FamilyParameter> { ["DimensionX"] = sizes[0], ["DimensionY"] = sizes[1], ["DimensionZ"] = sizes[2], ["Inclinaison"] = angle }));
                    doc.Regenerate();
                    var refs = new ReferenceArray(); refs.Append(datums[a].GetReference()); refs.Append(target.GetReference());
                    var offset = Basis(axes[1 - a]) * (3 + i * 0.1);
                    using (var dimensionLine = Line.CreateBound(offset - Basis(axes[a]) * (datumMm / 304.8), offset + Basis(axes[a]) * coordinate))
                        doc.FamilyCreate.NewLinearDimension(byAxis[axis], dimensionLine, refs).FamilyLabel = driver;
                    var guide = doc.FamilyCreate.NewModelCurve(Line.CreateBound(points[i] - Basis(axes[1 - a]) * 2, points[i] + Basis(axes[1 - a]) * 2), workPlane);
                    guide.ChangeToReferenceLine(); doc.Regenerate();
                    doc.FamilyCreate.NewAlignment(byAxis[axis], target.GetReference(), guide.GeometryCurve.Reference);
                    doc.FamilyCreate.NewAlignment(byAxis[axis], guide.GeometryCurve.Reference, edges[i].GeometryCurve.GetEndPointReference(endpoint));
                }
            }
            doc.Regenerate(); Check(size, degrees);
        }
        private ReferencePlane CoordinatePlane(View view, int coordinateAxis, double coordinate, string name)
        {
            int other = Enumerable.Range(0, 3).Single(a => a != axis && a != coordinateAxis);
            var center = Basis(coordinateAxis) * coordinate;
            var plane = doc.FamilyCreate.NewReferencePlane(center - Basis(other) * 2, center + Basis(other) * 2, Basis(axis), view);
            plane.Name = name; return plane;
        }
        private ModelCurve ReferenceLine(SketchPlane plane, XYZ direction)
        {
            var line = doc.FamilyCreate.NewModelCurve(Line.CreateBound(-direction, direction), plane);
            line.ChangeToReferenceLine(); line.Pinned = true; doc.Regenerate(); return line;
        }
        private static bool SameEdge(Curve curve, XYZ start, XYZ end) =>
            curve.GetEndPoint(0).DistanceTo(start) < 1e-6 && curve.GetEndPoint(1).DistanceTo(end) < 1e-6 ||
            curve.GetEndPoint(1).DistanceTo(start) < 1e-6 && curve.GetEndPoint(0).DistanceTo(end) < 1e-6;

        internal void Flex(double testAngle)
        {
            for (int a = 0; a < 3; a++)
            {
                var size = (double[])initialSize.Clone(); size[a] += Math.Max(1, size[a] * 0.1);
                Apply(size, initialAngle); Apply(initialSize, initialAngle);
            }
            Apply(initialSize, testAngle); Apply(initialSize, initialAngle);
            Apply(initialSize.Select(v => v + Math.Max(1, v * 0.1)).ToArray(), testAngle); Apply(initialSize, initialAngle);
        }
        private void Apply(double[] size, double degrees)
        {
            var errors = new List<string>();
            using (var transaction = new Transaction(doc, "Tester les dimensions et l'inclinaison de la barre"))
            {
                transaction.Start();
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new CodexFamilyBuilder.Failures(errors)).SetClearAfterRollback(true));
                for (int a = 0; a < 3; a++) doc.FamilyManager.Set(sizes[a], size[a] / 304.8);
                doc.FamilyManager.Set(angle, degrees * Math.PI / 180);
                doc.Regenerate(); Check(size, degrees);
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Test de barre inclinée annulé : " + string.Join(" ; ", errors));
                Check(size, degrees);
            }
        }
        private void Check(double[] size, double degrees)
        {
            var expected = Enumerable.Range(0, 8).Select(c => ParametricArray.Rotate(Enumerable.Range(0, 3).Select(a => (c & (1 << a)) == 0 ? 0 : size[a]).ToArray(), axis, degrees)).ToArray();
            var box = extrusion.get_BoundingBox(null);
            if (box == null) throw new InvalidOperationException("Encombrement de la barre inclinée absent.");
            var corners = Enumerable.Range(0, 8).Select(c => box.Transform.OfPoint(new XYZ((c & 1) == 0 ? box.Min.X : box.Max.X, (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z))).ToArray();
            for (int a = 0; a < 3; a++)
                if (Math.Abs(corners.Min(p => p.DotProduct(Basis(a))) * 304.8 - expected.Min(p => p[a])) > 0.5 || Math.Abs(corners.Max(p => p.DotProduct(Basis(a))) * 304.8 - expected.Max(p => p[a])) > 0.5)
                    throw new InvalidOperationException("La barre ne suit pas ses dimensions ou son angle (" + degrees + " degrés).");
            using (var geometry = extrusion.get_Geometry(new Options()))
            {
                double volume = size.Aggregate(1d, (a, b) => a * b);
                if (Math.Abs(geometry.OfType<Solid>().Sum(s => s.Volume) * Math.Pow(304.8, 3) - volume) > Math.Max(1, volume * 0.001)) throw new InvalidOperationException("Le profil incliné ne reste pas rectangulaire.");
                CheckNormals(geometry, axis, degrees);
            }
        }
        internal static void CheckNormals(GeometryElement geometry, int axis, double degrees)
        {
            var normals = Normals(geometry).ToArray();
            for (int a = 0; a < 3; a++)
            {
                var direction = ParametricArray.Rotate(Enumerable.Range(0, 3).Select(i => i == a ? 1d : 0).ToArray(), axis, degrees);
                var expected = new XYZ(direction[0], direction[1], direction[2]);
                if (!normals.Any(n => Math.Abs(n.DotProduct(expected)) > 0.999999)) throw new InvalidOperationException("L'orientation des faces ne suit pas l'angle demandé.");
            }
        }
        private static IEnumerable<XYZ> Normals(GeometryElement geometry)
        {
            foreach (var item in geometry)
                if (item is Solid solid)
                { foreach (var face in solid.Faces.Cast<Face>().OfType<PlanarFace>()) yield return face.FaceNormal; }
                else if (item is GeometryInstance instance)
                    using (var nested = instance.GetInstanceGeometry()) foreach (var normal in Normals(nested)) yield return normal;
        }
        private static XYZ Basis(int axis) => axis == 0 ? XYZ.BasisX : axis == 1 ? XYZ.BasisY : XYZ.BasisZ;
    }
}

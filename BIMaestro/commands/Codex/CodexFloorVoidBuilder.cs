using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    // Floor proxies cannot be cut with FamilyCreate.NewOpening. Keep the void
    // unattached and establish the cut on the placed instance in the project.
    internal sealed class CodexFloorVoidBuilder
    {
        private readonly Document doc;
        private readonly FamilyHostOpeningSpec spec;
        private readonly Extrusion extrusion;

        internal CodexFloorVoidBuilder(Document doc, FamilyHostOpeningSpec spec,
            Dictionary<string, double> initial, CodexParametricBuilder constraints)
        {
            this.doc = doc; this.spec = spec;
            if (new FilteredElementCollector(doc).OfClass(typeof(Floor)).GetElementCount() != 1)
                throw new InvalidOperationException("Le gabarit doit contenir exactement un sol hôte pour host_opening.");
            var cut = doc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
            if (cut == null || cut.IsReadOnly)
                throw new InvalidOperationException("Cette catégorie ne permet pas Couper avec des vides au chargement. Utiliser une catégorie compatible, par exemple generic.");
            cut.Set(1);
            var points = Points(initial);
            using (var profile = new CurveArray())
            using (var profiles = new CurveArrArray())
            {
                for (int i = 0; i < 4; i++) profile.Append(CodexCreationGuard.CreateLine(points[i], points[(i + 1) % 4]));
                profiles.Append(profile);
                var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                extrusion = doc.FamilyCreate.NewExtrusion(false, profiles, plane, (spec.Max[2].Value(initial) - spec.Min[2].Value(initial)) / 304.8);
            }
            var start = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            // Set end first so a negative lower bound does not invert the extrusion.
            start.Set(Math.Min(0, spec.Min[2].Value(initial)) / 304.8);
            end.Set(spec.Max[2].Value(initial) / 304.8);
            start.Set(spec.Min[2].Value(initial) / 304.8);
            doc.Regenerate();
            if (constraints != null)
            {
                constraints.AssociateLength(start, spec.Min[2]);
                constraints.AssociateLength(end, spec.Max[2]);
                var curves = extrusion.Sketch.Profile.Cast<CurveArray>().SelectMany(a => a.Cast<Curve>()).ToArray();
                for (int i = 0; i < 4; i++)
                {
                    int axis = i % 2 == 0 ? 1 : 0;
                    var expression = i == 0 || i == 3 ? spec.Min[axis] : spec.Max[axis];
                    var curve = curves.SingleOrDefault(c => Matches(c, points[i], points[(i + 1) % 4]));
                    if (curve?.Reference == null) throw new InvalidOperationException("Référence d'esquisse du vide de sol introuvable.");
                    var reference = constraints.PlaneAt(axis, expression).GetReference();
                    doc.Regenerate();
                    doc.FamilyCreate.NewAlignment(constraints.ProfileView(2), reference, curve.Reference);
                }
            }
            doc.Regenerate(); Check(initial);
        }

        internal void Check(Dictionary<string, double> values)
        {
            var expected = Points(values);
            var curves = extrusion.Sketch.Profile.Cast<CurveArray>().SelectMany(a => a.Cast<Curve>()).ToArray();
            if (extrusion.IsSolid || curves.Length != 4 ||
                Enumerable.Range(0, 4).Any(i => !curves.Any(c => Matches(c, expected[i], expected[(i + 1) % 4]))) ||
                Math.Abs(extrusion.StartOffset * 304.8 - spec.Min[2].Value(values)) > 0.5 ||
                Math.Abs(extrusion.EndOffset * 304.8 - spec.Max[2].Value(values)) > 0.5)
                throw new InvalidOperationException("Le vide de sol ne suit pas ses dimensions paramétriques.");
        }

        internal object Report() => new { created = true, kind = "floor_unattached_void", void_id = extrusion.Id.ToString(),
            boundary = "XYZ", parametric_dimensions_verified = true, through_host_verified = false,
            project_instance_verified = false, requires_project_cut = true,
            next = "Placer la famille sur le sol, sélectionner le sol et l'instance puis appeler revit_cut_floor_with_family, ou utiliser Couper la géométrie dans Revit." };

        // Caller owns the transaction. Reject non-intersecting cuts instead of
        // announcing success merely because a relationship was created.
        internal static double Cut(Document project, Floor floor, FamilyInstance instance)
        {
            if (!InstanceVoidCutUtils.CanBeCutWithVoid(floor) || !InstanceVoidCutUtils.IsVoidInstanceCuttingElement(instance))
                throw new InvalidOperationException("Le sol ou la famille ne permet pas la découpe par un vide non attaché.");
            if (InstanceVoidCutUtils.InstanceVoidCutExists(floor, instance)) return 0;
            double before = Volume(floor);
            InstanceVoidCutUtils.AddInstanceVoidCut(project, floor, instance);
            project.Regenerate();
            double removed = before - Volume(floor);
            if (!InstanceVoidCutUtils.InstanceVoidCutExists(floor, instance) || removed <= 1e-9)
                throw new InvalidOperationException("Le vide ne découpe aucun volume du sol. Vérifier le placement et la profondeur du vide.");
            return removed;
        }

        internal object VerifyProjectPlacement(IEnumerable<Dictionary<string, double>> cases)
        {
            Document project = null;
            try
            {
                project = doc.Application.NewProjectDocument(UnitSystem.Metric);
                var family = doc.LoadFamily(project);
                var symbol = family.GetFamilySymbolIds().Select(project.GetElement).OfType<FamilySymbol>().First();
                Floor floor; FamilyInstance instance; double before, thickness;
                using (var t = new Transaction(project, "Test temporaire — vide de sol"))
                {
                    t.Start();
                    var level = Level.Create(project, 0);
                    var type = new FilteredElementCollector(project).OfClass(typeof(FloorType)).Cast<FloorType>().First(f => !f.IsFoundationSlab);
                    var points = new[] { new XYZ(-20,-20,0), new XYZ(20,-20,0), new XYZ(20,20,0), new XYZ(-20,20,0) };
                    var loop = new CurveLoop();
                    for (int i = 0; i < 4; i++) loop.Append(CodexCreationGuard.CreateLine(points[i], points[(i + 1) % 4]));
                    floor = Floor.Create(project, new[] { loop }, type.Id, level.Id);
                    project.Regenerate(); before = Volume(floor);
                    var box = floor.get_BoundingBox(null); thickness = box.Max.Z - box.Min.Z;
                    symbol.Activate(); project.Regenerate();
                    instance = project.Create.NewFamilyInstance(new XYZ(0,0,box.Max.Z), symbol, floor, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    project.Regenerate(); Cut(project, floor, instance);
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Découpe de test annulée.");
                }
                int count = 0;
                foreach (var values in cases)
                {
                    using (var t = new Transaction(project, "Test temporaire — variation du vide de sol"))
                    {
                        t.Start();
                        foreach (var pair in values)
                        {
                            var driver = CodexParameterBuilder.FindExisting(doc.FamilyManager, pair.Key);
                            if (driver == null || !string.IsNullOrEmpty(driver.Formula)) continue;
                            var target = (driver.IsInstance ? (Element)instance : symbol).LookupParameter(driver.Definition.Name);
                            if (target == null || target.IsReadOnly) throw new InvalidOperationException("Paramètre de test inaccessible : " + pair.Key);
                            var kind = driver.Definition.GetDataType();
                            if (target.StorageType == StorageType.Integer) target.Set((int)pair.Value);
                            else target.Set(kind == SpecTypeId.Length ? pair.Value / 304.8 : kind == SpecTypeId.Angle ? pair.Value * Math.PI / 180 : pair.Value);
                        }
                        if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Variation du vide annulée.");
                    }
                    double expected = (spec.Max[0].Value(values) - spec.Min[0].Value(values)) / 304.8 *
                        (spec.Max[1].Value(values) - spec.Min[1].Value(values)) / 304.8 * thickness;
                    if (Math.Abs(before - Volume(floor) - expected) > Math.Max(1e-6, expected * 0.005))
                        throw new InvalidOperationException("Le vide ne traverse pas le sol du projet temporaire aux dimensions attendues.");
                    count++;
                }
                return new { verified = true, scenarios = count, temporary_project_saved = false, project_cut_applied = true };
            }
            finally { if (project != null && project.IsValidObject) project.Close(false); }
        }

        private static double Volume(Element element)
        {
            using (var geometry = element.get_Geometry(new Options()))
                return geometry.OfType<Solid>().Where(s => s.Volume > 0).Sum(s => s.Volume);
        }
        private XYZ[] Points(Dictionary<string, double> values)
        {
            double x0 = spec.Min[0].Value(values) / 304.8, x1 = spec.Max[0].Value(values) / 304.8;
            double y0 = spec.Min[1].Value(values) / 304.8, y1 = spec.Max[1].Value(values) / 304.8;
            return new[] { new XYZ(x0,y0,0), new XYZ(x1,y0,0), new XYZ(x1,y1,0), new XYZ(x0,y1,0) };
        }
        private static double DistanceXY(XYZ a, XYZ b) => Math.Sqrt(Math.Pow(a.X-b.X,2) + Math.Pow(a.Y-b.Y,2));
        private static bool Matches(Curve c, XYZ a, XYZ b) => c is Line &&
            (DistanceXY(c.GetEndPoint(0), a) < 0.5/304.8 && DistanceXY(c.GetEndPoint(1), b) < 0.5/304.8 ||
             DistanceXY(c.GetEndPoint(1), a) < 0.5/304.8 && DistanceXY(c.GetEndPoint(0), b) < 0.5/304.8);
    }
}

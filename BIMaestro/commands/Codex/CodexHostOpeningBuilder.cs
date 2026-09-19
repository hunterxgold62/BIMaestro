using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class CodexHostOpeningBuilder
    {
        private readonly Document doc;
        private readonly FamilyHostOpeningSpec spec;
        private readonly Wall wall;
        private readonly Opening opening;
        private readonly double uncutVolume;
        private readonly CodexFloorVoidBuilder floorVoid;

        internal CodexHostOpeningBuilder(Document doc, FamilyHostOpeningSpec spec,
            Dictionary<string, double> initial, CodexParametricBuilder constraints = null)
        {
            this.doc = doc; this.spec = spec;
            if (spec.IsFloor)
            {
                floorVoid = new CodexFloorVoidBuilder(doc, spec, initial, constraints);
                return;
            }
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().ToArray();
            if (walls.Length != 1) throw new InvalidOperationException("Le gabarit doit contenir exactement un mur hôte pour host_opening.");
            wall = walls[0];
            var line = (wall.Location as LocationCurve)?.Curve as Line;
            if (line == null || Math.Abs(line.Direction.DotProduct(XYZ.BasisX)) < 0.999999)
                throw new InvalidOperationException("host_opening exige un mur droit parallèle à X dans le gabarit.");
            if (new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>().Any(o => o.Host?.Id == wall.Id))
                throw new InvalidOperationException("Le mur du gabarit possède déjà une ouverture ; utiliser le modèle générique (mur) sans ouverture.");
            doc.Regenerate(); uncutVolume = Volume();
            var previous = new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).ToElementIds().ToHashSet();
            var points = Points(initial, line.GetEndPoint(0).Y);
            using (var profile = new CurveArray())
            {
                for (int i = 0; i < 4; i++) profile.Append(CodexCreationGuard.CreateLine(points[i], points[(i + 1) % 4]));
                opening = doc.FamilyCreate.NewOpening(wall, profile);
            }
            if (opening == null) throw new InvalidOperationException("Revit n'a pas créé l'ouverture du mur.");
            doc.Regenerate();
            if (constraints != null)
            {
                var curves = new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                    .Where(c => !previous.Contains(c.Id)).ToArray();
                for (int i = 0; i < 4; i++)
                {
                    int axis = i % 2 == 0 ? 2 : 0;
                    int index = axis == 0 ? 0 : 1;
                    var expression = i == 0 || i == 3 ? spec.Min[index] : spec.Max[index];
                    var matches = curves.Where(c => Matches(c.GeometryCurve, points[i], points[(i + 1) % 4])).ToArray();
                    if (matches.Length != 1 || matches[0].GeometryCurve.Reference == null)
                        throw new InvalidOperationException("Référence d'esquisse introuvable pour contraindre la limite " + i + " de host_opening.");
                    var reference = constraints.PlaneAt(axis, expression).GetReference(); doc.Regenerate();
                    doc.FamilyCreate.NewAlignment(constraints.ProfileView(1), reference, matches[0].GeometryCurve.Reference);
                }
            }
            doc.Regenerate(); Check(initial);
        }

        internal void Check(Dictionary<string, double> values)
        {
            if (floorVoid != null) { floorVoid.Check(values); return; }
            var curves = opening.BoundaryCurves.Cast<Curve>().ToArray();
            if (curves.Length != 4) throw new InvalidOperationException("Le contour de host_opening n'est plus rectangulaire.");
            var expected = Points(values, curves[0].GetEndPoint(0).Y);
            for (int i = 0; i < 4; i++)
                if (!curves.Any(c => Matches(c, expected[i], expected[(i + 1) % 4])))
                    throw new InvalidOperationException("L'ouverture du mur ne suit pas ses dimensions paramétriques.");
            double expectedCut = (spec.Max[0].Value(values) - spec.Min[0].Value(values)) / 304.8 *
                (spec.Max[1].Value(values) - spec.Min[1].Value(values)) / 304.8 * wall.Width;
            double actualCut = uncutVolume - Volume();
            if (Math.Abs(actualCut - expectedCut) > Math.Max(1e-6, expectedCut * 0.005))
                throw new InvalidOperationException("La baie ne traverse pas entièrement le mur hôte, ou dépasse ses limites. Vérifier min_xz/max_xz dans le gabarit.");
        }

        internal object Report() => floorVoid != null ? floorVoid.Report() : new { created = true, kind = "native_wall_opening", opening_id = opening.Id.ToString(),
            host_id = wall.Id.ToString(), through_host_verified = true, boundary = "XZ", project_instance_verified = false };

        // Native test harness only: the user's project is never used or saved.
        internal object VerifyProjectPlacement(IEnumerable<Dictionary<string, double>> cases)
        {
            if (floorVoid != null) return floorVoid.VerifyProjectPlacement(cases);
            Document project = null;
            try
            {
                project = doc.Application.NewProjectDocument(UnitSystem.Metric);
                var loaded = doc.LoadFamily(project);
                var symbol = loaded.GetFamilySymbolIds().Select(project.GetElement).OfType<FamilySymbol>().First();
                var wallType = new FilteredElementCollector(project).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
                Wall host; FamilyInstance instance; double before;
                using (var t = new Transaction(project, "Test temporaire — placer la fenêtre"))
                {
                    t.Start();
                    var level = Level.Create(project, 0);
                    host = Wall.Create(project, CodexCreationGuard.CreateLine(new XYZ(-20, 0, 0), new XYZ(20, 0, 0)), wallType.Id, level.Id, 20, 0, false, false);
                    project.Regenerate(); before = ElementVolume(host);
                    symbol.Activate(); project.Regenerate();
                    instance = project.Create.NewFamilyInstance(XYZ.Zero, symbol, host, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Placement de test annulé.");
                }
                int count = 0;
                foreach (var values in cases)
                {
                    using (var t = new Transaction(project, "Test temporaire — dimensions de la baie"))
                    {
                        t.Start();
                        foreach (var pair in values)
                        {
                            var driver = CodexParameterBuilder.FindExisting(doc.FamilyManager, pair.Key);
                            if (driver == null || !string.IsNullOrEmpty(driver.Formula)) continue;
                            var target = (driver.IsInstance ? (Element)instance : symbol).LookupParameter(driver.Definition.Name);
                            if (target == null || target.IsReadOnly) throw new InvalidOperationException("Paramètre de test inaccessible dans le projet : " + pair.Key);
                            var kind = driver.Definition.GetDataType();
                            if (target.StorageType == StorageType.Integer) target.Set((int)pair.Value);
                            else target.Set(kind == SpecTypeId.Length ? pair.Value / 304.8 : kind == SpecTypeId.Angle ? pair.Value * Math.PI / 180 : pair.Value);
                        }
                        if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Variation en projet annulée.");
                    }
                    double expected = (spec.Max[0].Value(values) - spec.Min[0].Value(values)) / 304.8 *
                        (spec.Max[1].Value(values) - spec.Min[1].Value(values)) / 304.8 * host.Width;
                    if (Math.Abs(before - ElementVolume(host) - expected) > Math.Max(1e-6, expected * 0.005))
                        throw new InvalidOperationException("La famille chargée ne découpe pas la baie attendue dans le mur du projet temporaire.");
                    count++;
                }
                return new { verified = true, scenarios = count, temporary_project_saved = false };
            }
            finally { if (project != null && project.IsValidObject) project.Close(false); }
        }

        private double Volume()
        {
            return ElementVolume(wall);
        }
        private static double ElementVolume(Element element)
        {
            using (var geometry = element.get_Geometry(new Options()))
                return geometry.OfType<Solid>().Where(s => s.Volume > 0).Sum(s => s.Volume);
        }
        private XYZ[] Points(Dictionary<string, double> values, double y)
        {
            double x0 = spec.Min[0].Value(values) / 304.8, x1 = spec.Max[0].Value(values) / 304.8;
            double z0 = spec.Min[1].Value(values) / 304.8, z1 = spec.Max[1].Value(values) / 304.8;
            return new[] { new XYZ(x0, y, z0), new XYZ(x1, y, z0), new XYZ(x1, y, z1), new XYZ(x0, y, z1) };
        }
        private static double DistanceXZ(XYZ a, XYZ b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
        private static bool Matches(Curve curve, XYZ a, XYZ b) => curve is Line &&
            (DistanceXZ(curve.GetEndPoint(0), a) < 0.5 / 304.8 && DistanceXZ(curve.GetEndPoint(1), b) < 0.5 / 304.8 ||
             DistanceXZ(curve.GetEndPoint(1), a) < 0.5 / 304.8 && DistanceXZ(curve.GetEndPoint(0), b) < 0.5 / 304.8);
    }
}

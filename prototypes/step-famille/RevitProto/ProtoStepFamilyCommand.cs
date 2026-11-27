using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace BIMaestro.ProtoStep
{
    // Prototype : à partir d'un STEP fabricant et de sa description simplifiée (spec.json, produite par spec.mjs),
    // crée deux familles « Équipement mécanique » :
    //   1. légère   : volumes natifs (boîtes / cylindres) + connecteurs MEP + paramètres ;
    //   2. détaillée : STEP importé, redressé et calé au sol + les mêmes connecteurs.
    [Transaction(TransactionMode.Manual)]
    public class ProtoStepFamilyCommand : IExternalCommand
    {
        private const double Mm = 1 / 304.8;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application; var app = uiapp.Application;
            string dir = Path.GetDirectoryName(typeof(ProtoStepFamilyCommand).Assembly.Location);
            string specPath = Path.Combine(dir, "UT-L60.spec.json");
            var spec = JsonDocument.Parse(File.ReadAllText(specPath, Encoding.UTF8)).RootElement;
            string outDir = Path.Combine(dir, "sorties"); Directory.CreateDirectory(outDir);
            string template = FindTemplate(app);
            var report = new StringBuilder();
            report.AppendLine("Gabarit : " + Path.GetFileName(template));
            string light = Path.Combine(outDir, "UT-L60 - légère.rfa");
            string detailed = Path.Combine(outDir, "UT-L60 - détaillée (STEP).rfa");

            // 1. Famille légère
            var sw = Stopwatch.StartNew();
            try
            {
                var doc = app.NewFamilyDocument(template);
                using (var t = new Transaction(doc, "Famille légère"))
                {
                    t.Start();
                    var mats = Materials(doc);
                    int ok = 0, ko = 0;
                    foreach (var p in spec.GetProperty("primitives").EnumerateArray())
                    {
                        try { var e = Primitive(doc, p); if (e == null) { ko++; continue; } SetMaterial(e, mats, p.GetProperty("group").GetString()); ok++; }
                        catch { ko++; }
                    }
                    report.AppendLine($"Légère : {ok} volumes créés, {ko} ignorés");
                    Connectors(doc, spec, mats, report);
                    Parameters(doc, spec);
                    t.Commit();
                }
                Save(doc, light);
                report.AppendLine($"Légère : {sw.Elapsed.TotalSeconds:0} s, {new FileInfo(light).Length / 1024} Ko");
            }
            catch (Exception ex) { report.AppendLine("Légère : ÉCHEC " + ex.Message); }

            // 2. Famille détaillée (import STEP)
            sw.Restart();
            try
            {
                if (!OptionalFunctionalityUtils.IsSTEPImportLinkAvailable())
                    throw new InvalidOperationException("le module d'import STEP n'est pas installé dans ce Revit");
                string step = spec.GetProperty("step").GetString();
                if (!File.Exists(step)) throw new FileNotFoundException("STEP introuvable", step);
                var doc = app.NewFamilyDocument(template);
                using (var t = new Transaction(doc, "Famille détaillée"))
                {
                    t.Start();
                    var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate);
                    var opts = new STEPImportOptions { Placement = ImportPlacement.Origin };
                    var id = doc.Import(step, opts, view);
                    doc.Regenerate();
                    var inst = doc.GetElement(id);
                    if (inst.Pinned) inst.Pinned = false;
                    var bb = inst.get_BoundingBox(null);
                    report.AppendLine("Détaillée : import brut " + Dims(bb));
                    // Le STEP SolidWorks est en Y vertical : on le redresse si la hauteur attendue est sur Y.
                    var exp = spec.GetProperty("expectedBBoxMm");
                    double expHeight = (exp.GetProperty("max")[2].GetDouble() - exp.GetProperty("min")[2].GetDouble()) * Mm;
                    double ey = bb.Max.Y - bb.Min.Y, ez = bb.Max.Z - bb.Min.Z;
                    if (Math.Abs(ey - expHeight) < Math.Abs(ez - expHeight))
                    {
                        ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(XYZ.Zero, XYZ.BasisX), Math.PI / 2);
                        doc.Regenerate(); bb = inst.get_BoundingBox(null);
                        report.AppendLine("Détaillée : redressé (Y vertical -> Z)");
                    }
                    var target = new XYZ(exp.GetProperty("min")[0].GetDouble() * Mm, exp.GetProperty("min")[1].GetDouble() * Mm, exp.GetProperty("min")[2].GetDouble() * Mm);
                    ElementTransformUtils.MoveElement(doc, id, target - bb.Min);
                    doc.Regenerate(); bb = inst.get_BoundingBox(null);
                    report.AppendLine("Détaillée : calé " + Dims(bb) + $" (écart au calage attendu {(bb.Min - target).GetLength() / Mm:0} mm)");
                    var mats = Materials(doc);
                    Connectors(doc, spec, mats, report);
                    Parameters(doc, spec);
                    t.Commit();
                }
                Save(doc, detailed);
                report.AppendLine($"Détaillée : {sw.Elapsed.TotalSeconds:0} s, {new FileInfo(detailed).Length / 1024} Ko");
            }
            catch (Exception ex) { report.AppendLine("Détaillée : ÉCHEC " + ex.Message); }

            File.WriteAllText(Path.Combine(outDir, "rapport.txt"), report.ToString(), new UTF8Encoding(true));
            if (File.Exists(light)) { try { uiapp.OpenAndActivateDocument(light); } catch { } }
            TaskDialog.Show("Proto STEP → famille", report + "\nDossier : " + outDir);
            return Result.Succeeded;
        }

        private static string FindTemplate(Autodesk.Revit.ApplicationServices.Application app)
        {
            var names = new[] { "Equipement mécanique métrique.rft", "Équipement mécanique métrique.rft", "Metric Mechanical Equipment.rft", "Mechanical Equipment.rft" };
            var roots = new[] { app.FamilyTemplatePath, @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\French", @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\English" };
            foreach (var r in roots.Where(Directory.Exists))
                foreach (var n in names) { var f = Directory.EnumerateFiles(r, n, SearchOption.AllDirectories).FirstOrDefault(); if (f != null) return f; }
            throw new FileNotFoundException("Gabarit « Equipement mécanique métrique.rft » introuvable");
        }

        private static XYZ V(JsonElement a, double k = Mm) => new XYZ(a[0].GetDouble() * k, a[1].GetDouble() * k, a[2].GetDouble() * k);

        private static Extrusion Primitive(Document doc, JsonElement p)
        {
            if (p.GetProperty("kind").GetString() == "box")
            {
                XYZ a = V(p.GetProperty("min")), b = V(p.GetProperty("max"));
                if (b.X - a.X < 2 * Mm || b.Y - a.Y < 2 * Mm || b.Z - a.Z < 2 * Mm) return null;
                var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, a.Z));
                var pts = new[] { new XYZ(a.X, a.Y, a.Z), new XYZ(b.X, a.Y, a.Z), new XYZ(b.X, b.Y, a.Z), new XYZ(a.X, b.Y, a.Z) };
                var loop = new CurveArray();
                for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                var prof = new CurveArrArray(); prof.Append(loop);
                return doc.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(doc, plane), b.Z - a.Z);
            }
            XYZ s = V(p.GetProperty("a")), e = V(p.GetProperty("b"));
            double r = p.GetProperty("r").GetDouble() * Mm, len = (e - s).GetLength();
            if (len < 2 * Mm || r < 2 * Mm) return null;
            return Cylinder(doc, s, (e - s).Normalize(), r, len);
        }

        private static Extrusion Cylinder(Document doc, XYZ origin, XYZ dir, double r, double len)
        {
            var plane = Plane.CreateByNormalAndOrigin(dir, origin);
            var loop = new CurveArray();
            loop.Append(Arc.Create(plane, r, 0, Math.PI));
            loop.Append(Arc.Create(plane, r, Math.PI, 2 * Math.PI));
            var prof = new CurveArrArray(); prof.Append(loop);
            return doc.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(doc, plane), len);
        }

        private static readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>
        {
            ["Corps"] = new Color(154, 163, 173), ["Brûleur"] = new Color(217, 83, 79), ["Gaz"] = new Color(240, 173, 78),
            ["Recirculation"] = new Color(200, 204, 210), ["Fumées"] = new Color(85, 91, 99), ["Eau"] = new Color(58, 123, 213),
            ["Connecteur"] = new Color(26, 156, 74),
        };

        private static Dictionary<string, ElementId> Materials(Document doc)
        {
            var map = new Dictionary<string, ElementId>();
            foreach (var kv in Colors)
            {
                string name = "Proto STEP - " + kv.Key;
                var m = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
                var id = m?.Id ?? Material.Create(doc, name);
                var mat = (Material)doc.GetElement(id); mat.Color = kv.Value; mat.SurfaceForegroundPatternColor = kv.Value;
                map[kv.Key] = id;
            }
            return map;
        }

        private static void SetMaterial(Element e, Dictionary<string, ElementId> mats, string group)
        {
            if (mats.TryGetValue(group, out var id)) e.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(id);
        }

        private static void Connectors(Document doc, JsonElement spec, Dictionary<string, ElementId> mats, StringBuilder report)
        {
            foreach (var c in spec.GetProperty("connectors").EnumerateArray())
            {
                string name = c.GetProperty("name").GetString();
                try
                {
                    XYZ p = V(c.GetProperty("p")), dir = V(c.GetProperty("dir"), 1).Normalize();
                    double r = c.GetProperty("diameter").GetDouble() / 2 * Mm, thk = 10 * Mm;
                    // Pastille de raccordement : sa face extérieure porte le connecteur, normale sortante.
                    var disc = Cylinder(doc, p - dir * thk, dir, r + 5 * Mm, thk);
                    SetMaterial(disc, mats, "Connecteur");
                    doc.Regenerate();
                    var face = disc.get_Geometry(new Options { ComputeReferences = true }).OfType<Solid>()
                        .SelectMany(s => s.Faces.OfType<PlanarFace>()).First(f => f.FaceNormal.IsAlmostEqualTo(dir, 1e-6));
                    string sys = c.GetProperty("system").GetString();
                    ConnectorElement ce = sys switch
                    {
                        "SupplyHydronic" => ConnectorElement.CreatePipeConnector(doc, PipeSystemType.SupplyHydronic, face.Reference),
                        "ReturnHydronic" => ConnectorElement.CreatePipeConnector(doc, PipeSystemType.ReturnHydronic, face.Reference),
                        "Flue" => ConnectorElement.CreateDuctConnector(doc, DuctSystemType.ExhaustAir, ConnectorProfileType.Round, face.Reference),
                        _ => ConnectorElement.CreatePipeConnector(doc, PipeSystemType.OtherPipe, face.Reference),
                    };
                    var radius = ce.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
                    if (radius != null && !radius.IsReadOnly) radius.Set(r);
                    else ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER)?.Set(2 * r);
                    ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.Set(name);
                    report.AppendLine("  connecteur : " + name);
                }
                catch (Exception ex) { report.AppendLine("  connecteur ÉCHEC " + name + " : " + ex.Message); }
            }
        }

        private static void Parameters(Document doc, JsonElement spec)
        {
            var fm = doc.FamilyManager;
            if (fm.CurrentType == null) fm.NewType(spec.GetProperty("name").GetString());
            foreach (var kv in spec.GetProperty("parameters").EnumerateObject())
            {
                var fp = fm.get_Parameter(kv.Name) ?? fm.AddParameter(kv.Name, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                fm.Set(fp, kv.Value.GetString());
            }
        }

        private static void Save(Document doc, string path)
        {
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            doc.Close(false);
        }

        private static string Dims(BoundingBoxXYZ bb) =>
            $"{(bb.Max.X - bb.Min.X) / Mm:0} x {(bb.Max.Y - bb.Min.Y) / Mm:0} x {(bb.Max.Z - bb.Min.Z) / Mm:0} mm";
    }
}

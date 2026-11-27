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
    // Version 2, générique : la géométrie vient du STEP lui-même (import Revit), simplifiée pièce par pièce.
    //   - « fidèle » : solides du STEP repris en géométrie de famille (FreeForm), sans visserie / soudures / petites pièces ;
    //   - « légère » : les mêmes solides retriangulés grossièrement (moins de faces, silhouette conservée).
    // Connecteurs : extrémités libres détectées par spec2.mjs, validées par l'utilisateur dans une fenêtre.
    [Transaction(TransactionMode.Manual)]
    public class ProtoStepV2Command : IExternalCommand
    {
        private const double Mm = 1 / 304.8;
        // On n'écarte que ce qui ne se voit pas à l'échelle d'un projet. L'isolant reste : c'est souvent l'enveloppe visible des tuyauteries.
        private static readonly HashSet<string> Dropped = new HashSet<string> { "fixation", "soudure", "joint" };

        private sealed class Part { public string Name, Group, Role; public double[] Min, Max; }
        private sealed class Piece { public Solid Solid; public Part Part; public double DiagMm; public int Level; }

        // 3 = Grossier/Moyen/Fin, 2 = Moyen/Fin, 1 = Fin seulement
        internal static void SetLevel(Element e, int level)
        {
            if (!(e is GenericForm g)) return;
            var v = new FamilyElementVisibility(FamilyElementVisibilityType.Model) { IsShownInCoarse = level >= 3, IsShownInMedium = level >= 2, IsShownInFine = true };
            g.SetVisibility(v);
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application; var app = uiapp.Application;
            string dir = Path.GetDirectoryName(typeof(ProtoStepV2Command).Assembly.Location);
            var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "spec2.json"), Encoding.UTF8)).RootElement;
            string name = spec.GetProperty("name").GetString();
            string step = spec.GetProperty("step").GetString();

            // 1. Validation des raccordements
            var rows = spec.GetProperty("connectors").EnumerateArray().Select(c => new ConnectorRow
            {
                Keep = c.GetProperty("keep").GetBoolean(), Name = c.GetProperty("name").GetString(), Label = c.GetProperty("label").GetString(),
                Diameter = c.GetProperty("diameter").GetInt32(), Part = c.GetProperty("part").GetString(), Why = c.GetProperty("why").GetString(),
                Score = c.GetProperty("score").GetDouble(),
                P = c.GetProperty("p").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Dir = c.GetProperty("dir").EnumerateArray().Select(v => v.GetDouble()).ToArray(),
            }).ToList();
            var win = new ConnectorReviewWindow(rows, name);
            new System.Windows.Interop.WindowInteropHelper(win).Owner = uiapp.MainWindowHandle;
            if (win.ShowDialog() != true) return Result.Cancelled;
            var connectors = win.Accepted;

            var parts = spec.GetProperty("parts").EnumerateArray().Select(p => new Part
            {
                Name = p.GetProperty("name").GetString(), Group = p.GetProperty("group").GetString(), Role = p.GetProperty("role").GetString(),
                Min = p.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Max = p.GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray(),
            }).ToList();

            string outDir = Path.Combine(dir, "sorties-v2"); Directory.CreateDirectory(outDir);
            string fidele = Path.Combine(outDir, name + " - fidèle.rfa"), legere = Path.Combine(outDir, name + " - légère.rfa");
            string template = FindTemplate(app);
            var report = new StringBuilder();
            report.AppendLine($"{name} — {connectors.Count} connecteurs validés");
            var sw = Stopwatch.StartNew();
            List<Piece> kept = null;
            try
            {
                if (!OptionalFunctionalityUtils.IsSTEPImportLinkAvailable()) throw new InvalidOperationException("module d'import STEP absent");
                // 2. Famille fidèle : import, redressement, extraction des solides, tri
                var doc = app.NewFamilyDocument(template);
                using (var t = new Transaction(doc, "Famille fidèle"))
                {
                    t.Start();
                    var all = ImportSolids(doc, step, spec, report);
                    kept = Select(all, parts, spec, 1.0, report);
                    var mats = Materials(doc, kept.Select(k => k.Part?.Group ?? "Autres"));
                    int ok = 0, ko = 0;
                    foreach (var k in kept)
                    {
                        try { var e = FreeFormElement.Create(doc, k.Solid); SetMaterial(e, mats, k.Part?.Group ?? "Autres"); SetLevel(e, k.Level); ok++; }
                        catch { ko++; }
                    }
                    report.AppendLine($"Fidèle : {ok} solides repris, {ko} refusés par Revit");
                    AddOpenMeshes(doc, report);
                    Connectors(doc, connectors, mats, report);
                    Parameters(doc, spec);
                    t.Commit();
                }
                Save(doc, fidele);
                report.AppendLine($"Fidèle : {sw.Elapsed.TotalSeconds:0} s, {new FileInfo(fidele).Length / 1024} Ko");
            }
            catch (Exception ex) { report.AppendLine("Fidèle : ÉCHEC " + ex.Message); }

            // 3. Famille à poids cible : toutes les pièces, les plus « chères » simplifiées en premier
            const double TargetMo = 10;
            if (kept != null && File.Exists(fidele))
            {
                sw.Restart();
                try
                {
                    var items = kept.Select(k => new WeightBudget.Item { Name = k.Part?.Name ?? "?", Group = k.Part?.Group ?? "Autres", Solid = k.Solid, DiagMm = k.DiagMm, Level = k.Level }).ToList();
                    foreach (var tris in OpenMeshes)
                    {
                        var pts = tris.SelectMany(t => t).ToList();
                        double[] mn = { pts.Min(p => p.X) / Mm, pts.Min(p => p.Y) / Mm, pts.Min(p => p.Z) / Mm }, mx = { pts.Max(p => p.X) / Mm, pts.Max(p => p.Y) / Mm, pts.Max(p => p.Z) / Mm };
                        var part = Match(mn, mx, parts);
                        items.Add(new WeightBudget.Item { Name = part?.Name ?? "?", Group = part?.Group ?? "Autres", Tris = tris, Level = 3,
                            DiagMm = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(mx[i] - mn[i], 2))) });
                    }
                    // calibrage : le poids de la famille fidèle (mêmes pièces) donne le coût d'une « unité »
                    const double fixedBytes = 450 * 1024;
                    double bytesPerUnit = (new FileInfo(fidele).Length - fixedBytes) / Math.Max(1, items.Sum(WeightBudget.RawUnits));
                    foreach (var i in items) i.Cost = WeightBudget.RawUnits(i) * bytesPerUnit;
                    WeightBudget.Top(items, report);

                    double target = TargetMo * 1024 * 1024;
                    string temp = Path.Combine(outDir, "_essai.rfa");
                    long size = 0; int pass = 0;
                    foreach (var relCell in new[] { 0.02, 0.04, 0.07, 0.12 })
                    {
                        pass++;
                        var simplify = WeightBudget.Plan(items, bytesPerUnit, target * 0.9, relCell, fixedBytes);
                        double estimate = fixedBytes + items.Sum(i => simplify.Contains(i) ? i.Simple.Count * bytesPerUnit : i.Cost);
                        var doc = app.NewFamilyDocument(template);
                        int solidsKept = 0, meshesKept = 0, simplified = 0, failed = 0;
                        using (var t = new Transaction(doc, "Famille poids cible"))
                        {
                            t.Start();
                            var mats = Materials(doc, items.Select(i => i.Group));
                            var cat = WeightBudget.ShapeCategory(doc);
                            foreach (var i in items)
                            {
                                mats.TryGetValue(i.Group, out var mat);
                                if (simplify.Contains(i)) { if (WeightBudget.AddMesh(doc, cat, i.Simple, mat)) simplified++; else failed++; }
                                else if (i.Solid != null)
                                {
                                    try { var e = FreeFormElement.Create(doc, i.Solid); SetMaterial(e, mats, i.Group); SetLevel(e, i.Level); solidsKept++; } catch { failed++; }
                                }
                                else if (WeightBudget.AddMesh(doc, cat, i.Tris, mat)) meshesKept++; else failed++;
                            }
                            Connectors(doc, connectors, mats, new StringBuilder());
                            Parameters(doc, spec);
                            t.Commit();
                        }
                        Save(doc, temp);
                        size = new FileInfo(temp).Length;
                        report.AppendLine($"Passe {pass} (cellule {relCell * 100:0} % de chaque pièce) : {simplified} pièces simplifiées, {solidsKept} solides et {meshesKept} maillages intacts, {failed} échecs → {size / 1024.0 / 1024:0.0} Mo (estimé {estimate / 1024 / 1024:0.0})");
                        if (size <= target) break;
                        bytesPerUnit *= size / Math.Max(estimate, 1); // recalage de l'estimation sur le poids réel
                        foreach (var i in items) i.Cost = WeightBudget.RawUnits(i) * bytesPerUnit;
                    }
                    legere = Path.Combine(outDir, $"{name} - {TargetMo:0} Mo.rfa");
                    if (File.Exists(legere)) File.Delete(legere);
                    File.Move(temp, legere);
                    report.AppendLine($"Poids cible : {sw.Elapsed.TotalSeconds:0} s, {size / 1024.0 / 1024:0.0} Mo {(size <= target ? "(objectif atteint)" : "(objectif NON atteint)")}");
                }
                catch (Exception ex) { report.AppendLine("Poids cible : ÉCHEC " + ex.Message); }
            }

            File.WriteAllText(Path.Combine(outDir, "rapport.txt"), report.ToString(), new UTF8Encoding(true));
            foreach (var f in new[] { legere, fidele }) if (File.Exists(f)) { try { uiapp.OpenAndActivateDocument(f); } catch { } }
            TaskDialog.Show("Proto STEP → famille (v2)", report + "\nDossier : " + outDir);
            return Result.Succeeded;
        }

        // Importe le STEP, le redresse selon l'axe vertical d'origine, le cale sur l'encombrement attendu,
        // récupère ses solides (en coordonnées famille) puis supprime l'import.
        private static List<Solid> ImportSolids(Document doc, string step, JsonElement spec, StringBuilder report)
        {
            var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate);
            var id = doc.Import(step, new STEPImportOptions { Placement = ImportPlacement.Origin }, view);
            doc.Regenerate();
            var inst = doc.GetElement(id); if (inst.Pinned) inst.Pinned = false;
            if (spec.GetProperty("upAxis").GetString() == "Y")
            {
                ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(XYZ.Zero, XYZ.BasisX), Math.PI / 2);
                doc.Regenerate();
            }
            var exp = spec.GetProperty("expectedBBoxMm").GetProperty("min");
            var target = new XYZ(exp[0].GetDouble() * Mm, exp[1].GetDouble() * Mm, exp[2].GetDouble() * Mm);
            var bb = inst.get_BoundingBox(null);
            ElementTransformUtils.MoveElement(doc, id, target - bb.Min);
            doc.Regenerate();
            OpenMeshes.Clear();
            var solids = new List<Solid>(); int rawSolids = 0, meshes = 0, meshOk = 0;
            void Add(Solid s)
            {
                // Revit fusionne souvent plusieurs corps en un seul solide : on le redécoupe en volumes séparés.
                IList<Solid> parts;
                try { parts = SolidUtils.SplitVolumes(s); } catch { parts = new[] { s }; }
                foreach (var p in parts) if (p.Volume > 1e-9) solids.Add(SolidUtils.Clone(p));
            }
            void Walk(GeometryElement ge)
            {
                foreach (var o in ge)
                {
                    if (o is Solid s && s.Volume > 1e-9) { rawSolids++; Add(s); }
                    else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
                    else if (o is Mesh m)
                    {
                        meshes++; var ms = MeshToSolid(m);
                        if (ms != null) { meshOk++; Add(ms); }
                        else OpenMeshes.Add(Enumerable.Range(0, m.NumTriangles).Select(i => { var t = m.get_Triangle(i); return new[] { t.get_Vertex(0), t.get_Vertex(1), t.get_Vertex(2) }; }).ToList());
                    }
                }
            }
            Walk(inst.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
            doc.Delete(id);
            report.AppendLine($"Import : {rawSolids} solides bruts → {solids.Count} volumes séparés ; {meshes} maillages dont {meshOk} convertis en solides, {OpenMeshes.Count} gardés en forme directe");
            return solids;
        }

        // Maillages ouverts (non convertibles en solide) : gardés tels quels, en forme directe dans la famille.
        private static readonly List<List<XYZ[]>> OpenMeshes = new List<List<XYZ[]>>();

        private static int AddOpenMeshes(Document doc, StringBuilder report)
        {
            if (!DirectShape.IsSupportedDocument(doc)) { report.AppendLine("  formes directes non prises en charge dans ce gabarit"); return 0; }
            var cat = new[] { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_GenericModel }
                .Select(c => new ElementId(c)).FirstOrDefault(c => DirectShape.IsValidCategoryId(c, doc));
            if (cat == null) { report.AppendLine("  aucune catégorie valide pour les formes directes"); return 0; }
            int ok = 0;
            foreach (var tris in OpenMeshes)
            {
                try
                {
                    var b = new TessellatedShapeBuilder { Target = TessellatedShapeBuilderTarget.Mesh, Fallback = TessellatedShapeBuilderFallback.Salvage };
                    b.OpenConnectedFaceSet(false);
                    foreach (var t in tris) b.AddFace(new TessellatedFace(t.ToList(), ElementId.InvalidElementId));
                    b.CloseConnectedFaceSet(); b.Build();
                    var geo = b.GetBuildResult().GetGeometricalObjects();
                    if (geo.Count == 0) continue;
                    var ds = DirectShape.CreateElement(doc, cat); ds.SetShape(geo); ok++;
                }
                catch { }
            }
            report.AppendLine($"  formes directes : {ok} / {OpenMeshes.Count}");
            return ok;
        }

        private static Part Match(double[] mn, double[] mx, List<Part> parts)
        {
            Part best = null; double bestErr = double.MaxValue;
            foreach (var p in parts)
            {
                if (Enumerable.Range(0, 3).Any(k => mn[k] < p.Min[k] - 20 || mx[k] > p.Max[k] + 20)) continue;
                double err = Enumerable.Range(0, 3).Sum(k => (mn[k] - p.Min[k]) + (p.Max[k] - mx[k]));
                if (err < bestErr) { bestErr = err; best = p; }
            }
            return best;
        }

        // Pièces que Revit importe en maillage (souvent les surfaces complexes) : reconstruites en solide si le maillage est fermé.
        internal static Solid MeshToSolid(Mesh m)
        {
            try
            {
                var b = new TessellatedShapeBuilder { Target = TessellatedShapeBuilderTarget.Solid, Fallback = TessellatedShapeBuilderFallback.Abort };
                b.OpenConnectedFaceSet(true);
                for (int i = 0; i < m.NumTriangles; i++)
                {
                    var t = m.get_Triangle(i);
                    b.AddFace(new TessellatedFace(new List<XYZ> { t.get_Vertex(0), t.get_Vertex(1), t.get_Vertex(2) }, ElementId.InvalidElementId));
                }
                b.CloseConnectedFaceSet();
                b.Build();
                return b.GetBuildResult().GetGeometricalObjects().OfType<Solid>().FirstOrDefault(x => x.Volume > 1e-9);
            }
            catch { return null; }
        }

        // Associe chaque solide à une pièce du STEP (par encombrement) puis écarte visserie, soudures, joints, isolant
        // et les pièces trop petites par rapport à l'ensemble.
        // Tolérance k (fenêtre de validation) : 0,25 = très détaillé … 4 = très simplifié.
        // Classes de taille (diagonale rapportée à celle de l'ensemble) :
        //   < 0,3 %·k écartée · < 0,8 %·k visible en Fin · < 2,5 %·k visible en Moyen et Fin · sinon partout.
        // Les organes (vannes, soupapes, sondes) gagnent une classe.
        private static List<Piece> Select(List<Solid> solids, List<Part> parts, JsonElement spec, double k, StringBuilder report)
        {
            var e = spec.GetProperty("expectedBBoxMm");
            double globalDiag = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(e.GetProperty("max")[i].GetDouble() - e.GetProperty("min")[i].GetDouble(), 2)));
            double drop = Math.Max(10, 0.003 * k * globalDiag), fine = 0.008 * k * globalDiag, medium = 0.025 * k * globalDiag;
            var kept = new List<Piece>(); var why = new Dictionary<string, int>(); int unmatched = 0;
            double volAll = 0, volKept = 0;
            foreach (var s in solids)
            {
                var b = s.GetBoundingBox(); var tf = b.Transform;
                var c = new[] { tf.OfPoint(b.Min), tf.OfPoint(b.Max) };
                double[] mn = { Math.Min(c[0].X, c[1].X) / Mm, Math.Min(c[0].Y, c[1].Y) / Mm, Math.Min(c[0].Z, c[1].Z) / Mm };
                double[] mx = { Math.Max(c[0].X, c[1].X) / Mm, Math.Max(c[0].Y, c[1].Y) / Mm, Math.Max(c[0].Z, c[1].Z) / Mm };
                double diag = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Math.Pow(mx[k] - mn[k], 2)));
                // pièce la plus proche dont l'encombrement contient le solide
                Part best = null; double bestErr = double.MaxValue;
                foreach (var p in parts)
                {
                    if (Enumerable.Range(0, 3).Any(k => mn[k] < p.Min[k] - 20 || mx[k] > p.Max[k] + 20)) continue;
                    double err = Enumerable.Range(0, 3).Sum(k => (mn[k] - p.Min[k]) + (p.Max[k] - mx[k]));
                    if (err < bestErr) { bestErr = err; best = p; }
                }
                if (best == null) unmatched++;
                volAll += s.Volume;
                bool organ = best?.Role == "organe" || best?.Role == "instrument";
                double size = organ ? diag * 2.5 : diag; // un organe compte comme une pièce plus grande : il reste visible plus tôt
                string reason = null;
                if (best != null && Dropped.Contains(best.Role)) reason = best.Role;
                if (reason != null) { why[reason] = why.TryGetValue(reason, out var n) ? n + 1 : 1; continue; }
                // Tout reste visible à tous les niveaux, sauf les toutes petites pièces réservées au niveau Fin.
                int level = size < drop ? 1 : 3;
                kept.Add(new Piece { Solid = s, Part = best, DiagMm = diag, Level = level }); volKept += s.Volume;
            }
            report.AppendLine($"Tri : {kept.Count} volumes gardés sur {solids.Count} ({100 * volKept / Math.Max(volAll, 1e-9):0.0} % du volume)");
            report.AppendLine($"  visibles partout {kept.Count(p => p.Level == 3)} · Fin seul (< {drop:0} mm) {kept.Count(p => p.Level == 1)}");
            report.AppendLine("  écartés : " + string.Join(", ", why.Select(kv => $"{kv.Key} {kv.Value}")) + (unmatched > 0 ? $" ; {unmatched} volumes sans pièce reconnue" : ""));
            foreach (var g in kept.GroupBy(k => k.Part?.Group ?? "Autres").OrderByDescending(g => g.Count()))
                report.AppendLine($"  {g.Key} : {g.Count()}");
            return kept;
        }

        // Retriangulation grossière d'un solide : la finesse dépend de la taille de la pièce.
        private static Solid Coarse(Solid s, double diagMm)
        {
            try
            {
                if (s.Faces.Size <= 12) return null; // déjà simple
                var ctrl = new SolidOrShellTessellationControls { LevelOfDetail = diagMm > 2000 ? 0.15 : 0.0, MinAngleInTriangle = 0.15, MinExternalAngleBetweenTriangles = 0.35 };
                var tri = SolidUtils.TessellateSolidOrShell(s, ctrl);
                var b = new TessellatedShapeBuilder { Target = TessellatedShapeBuilderTarget.Solid, Fallback = TessellatedShapeBuilderFallback.Abort };
                b.OpenConnectedFaceSet(true);
                int faces = 0;
                for (int i = 0; i < tri.ShellComponentCount; i++)
                {
                    var sc = tri.GetShellComponent(i);
                    for (int t = 0; t < sc.TriangleCount; t++)
                    {
                        var tr = sc.GetTriangle(t);
                        b.AddFace(new TessellatedFace(new List<XYZ> { sc.GetVertex(tr.VertexIndex0), sc.GetVertex(tr.VertexIndex1), sc.GetVertex(tr.VertexIndex2) }, ElementId.InvalidElementId));
                        faces++;
                    }
                }
                b.CloseConnectedFaceSet();
                if (faces >= s.Faces.Size * 6) return null; // pas plus léger
                b.Build();
                return b.GetBuildResult().GetGeometricalObjects().OfType<Solid>().FirstOrDefault(x => x.Volume > 1e-9);
            }
            catch { return null; }
        }

        internal static string FindTemplate(Autodesk.Revit.ApplicationServices.Application app)
        {
            var names = new[] { "Equipement mécanique métrique.rft", "Équipement mécanique métrique.rft", "Metric Mechanical Equipment.rft", "Mechanical Equipment.rft" };
            var roots = new[] { app.FamilyTemplatePath, @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\French", @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\English" };
            foreach (var r in roots.Where(Directory.Exists))
                foreach (var n in names) { var f = Directory.EnumerateFiles(r, n, SearchOption.AllDirectories).FirstOrDefault(); if (f != null) return f; }
            throw new FileNotFoundException("Gabarit « Equipement mécanique métrique.rft » introuvable");
        }

        // Une couleur par sous-ensemble du STEP (palette fixe, attribuée dans l'ordre).
        private static readonly Color[] Palette =
        {
            new Color(154, 163, 173), new Color(217, 83, 79), new Color(240, 173, 78), new Color(58, 123, 213), new Color(92, 184, 92),
            new Color(85, 91, 99), new Color(155, 89, 182), new Color(26, 188, 156), new Color(200, 204, 210), new Color(230, 126, 34),
        };

        internal static Dictionary<string, ElementId> Materials(Document doc, IEnumerable<string> groups)
        {
            var map = new Dictionary<string, ElementId>(); int i = 0;
            foreach (var g in groups.Distinct().Concat(new[] { "Connecteur" }))
            {
                string name = ("STEP - " + g).Length > 60 ? ("STEP - " + g).Substring(0, 60) : "STEP - " + g;
                name = new string(name.Select(ch => "{}[]|;<>?`~\\:".Contains(ch) ? '-' : ch).ToArray());
                if (map.ContainsKey(g)) continue;
                var id = new FilteredElementCollector(doc).OfClass(typeof(Material)).FirstOrDefault(x => x.Name == name)?.Id ?? Material.Create(doc, name);
                var m = (Material)doc.GetElement(id);
                m.Color = g == "Connecteur" ? new Color(26, 156, 74) : Palette[i++ % Palette.Length];
                m.SurfaceForegroundPatternColor = m.Color;
                map[g] = id;
            }
            return map;
        }

        internal static void SetMaterial(Element e, Dictionary<string, ElementId> mats, string group)
        {
            if (mats.TryGetValue(group, out var id)) e.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(id);
        }

        internal static Extrusion Disc(Document doc, XYZ origin, XYZ dir, double r, double len)
        {
            var plane = Plane.CreateByNormalAndOrigin(dir, origin);
            var loop = new CurveArray();
            loop.Append(Arc.Create(plane, r, 0, Math.PI));
            loop.Append(Arc.Create(plane, r, Math.PI, 2 * Math.PI));
            var prof = new CurveArrArray(); prof.Append(loop);
            return doc.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(doc, plane), len);
        }

        internal static void Connectors(Document doc, List<ConnectorRow> rows, Dictionary<string, ElementId> mats, StringBuilder report)
        {
            foreach (var c in rows)
            {
                try
                {
                    var p = new XYZ(c.P[0] * Mm, c.P[1] * Mm, c.P[2] * Mm); var dir = new XYZ(c.Dir[0], c.Dir[1], c.Dir[2]).Normalize();
                    double r = c.Diameter / 2.0 * Mm, thk = 10 * Mm;
                    var disc = Disc(doc, p - dir * thk, dir, r + 5 * Mm, thk);
                    SetMaterial(disc, mats, "Connecteur");
                    doc.Regenerate();
                    var face = disc.get_Geometry(new Options { ComputeReferences = true }).OfType<Solid>()
                        .SelectMany(s => s.Faces.OfType<PlanarFace>()).First(f => f.FaceNormal.IsAlmostEqualTo(dir, 1e-4));
                    ConnectorElement ce = c.Kind == "Gaine"
                        ? ConnectorElement.CreateDuctConnector(doc, c.Label switch
                            {
                                "Départ" => DuctSystemType.SupplyAir,
                                "Retour" => DuctSystemType.ReturnAir,
                                "Fumées" or "Évent" => DuctSystemType.ExhaustAir,
                                _ => DuctSystemType.OtherAir,
                            }, ConnectorProfileType.Round, face.Reference)
                        : c.Label switch
                        {
                            "Départ" => ConnectorElement.CreatePipeConnector(doc, PipeSystemType.SupplyHydronic, face.Reference),
                            "Retour" => ConnectorElement.CreatePipeConnector(doc, PipeSystemType.ReturnHydronic, face.Reference),
                            "Vidange" => ConnectorElement.CreatePipeConnector(doc, PipeSystemType.Sanitary, face.Reference),
                            _ => ConnectorElement.CreatePipeConnector(doc, PipeSystemType.OtherPipe, face.Reference),
                        };
                    var radius = ce.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
                    if (radius != null && !radius.IsReadOnly) radius.Set(r); else ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER)?.Set(2 * r);
                    ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.Set(c.Name);
                    report.AppendLine("  connecteur : " + c.Name);
                }
                catch (Exception ex) { report.AppendLine("  connecteur ÉCHEC " + c.Name + " : " + ex.Message); }
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

        internal static void Save(Document doc, string path)
        {
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            doc.Close(false);
        }
    }
}

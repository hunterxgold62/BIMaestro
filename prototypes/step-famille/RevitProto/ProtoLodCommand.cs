using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
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
    // Une famille, trois niveaux de détail :
    //   Grossier = une boîte d'encombrement · Moyen = volumes simples (boîtes / cylindres) · Fin = le modèle CAO importé tel quel.
    // Fonctionne pour STEP et SAT : toute l'analyse (volumes, raccordements libres) se fait sur la géométrie importée,
    // sans avoir besoin des noms de pièces (absents des SAT).
    [Transaction(TransactionMode.Manual)]
    public class ProtoLodCommand : IExternalCommand
    {
        private const double Mm = 1 / 304.8;

        private sealed class Circle { public XYZ C, N; public double R; }
        private sealed class Piece
        {
            public XYZ Min, Max; public List<Circle> Circles = new List<Circle>(); public List<(XYZ o, XYZ axis, double r)> Cyls = new List<(XYZ, XYZ, double)>();
            public Solid Solid; public bool IsMesh; public int Faces;
            public List<XYZ[]> Tris;      // triangles d'une pièce lue en maillage
            public bool Simplified;       // maillage ressoudé / simplifié puis reconverti en solide
            public bool Bolt;             // boulon, écrou ou tige filetée reconnu par sa forme
            public Color Color;          // couleur d'origine (CAO) dominante, si connue
            public Solid FineSolid;       // maillage seulement ressoudé (forme exacte), pour le niveau Fin en pièces
            // Pièce « complexe » : maillage, ou beaucoup de faces pour sa taille (filetages, logos, surfaces fabricant découpées)
            public bool Complex => IsMesh || Solid == null || (!Simplified && Faces > 150 && Faces / Math.Max(Diag * 0.3048, 0.05) > 400);
            public double Diag => (Max - Min).GetLength();
            public double Vol => (Max.X - Min.X) * (Max.Y - Min.Y) * (Max.Z - Min.Z);
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application; var app = uiapp.Application;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Fichier CAO fabricant", Filter = "CAO 3D (*.step;*.stp;*.sat)|*.step;*.stp;*.sat" };
            if (dlg.ShowDialog() != true) return Result.Cancelled;
            string file = dlg.FileName, name = Path.GetFileNameWithoutExtension(file);
            bool sat = Path.GetExtension(file).Equals(".sat", StringComparison.OrdinalIgnoreCase);
            string dir = Path.GetDirectoryName(typeof(ProtoLodCommand).Assembly.Location);
            string outDir = Path.Combine(dir, "sorties-lod"); Directory.CreateDirectory(outDir);
            var report = new StringBuilder(); var sw = Stopwatch.StartNew();
            report.AppendLine(Path.GetFileName(file) + $" ({new FileInfo(file).Length / 1024 / 1024} Mo)");

            string origin = Header(file, sat);
            bool upY = System.Text.RegularExpressions.Regex.IsMatch(origin, "solidworks|inventor|creo|pro/?e", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            report.AppendLine($"Logiciel d'origine : {origin} → axe vertical {(upY ? "Y (redressé)" : "Z")}");

            // Description par noms disponible (STEP analysé par spec2.mjs) ?
            JsonElement? spec = null;
            var specPath = Path.Combine(dir, "spec2.json");
            if (!sat && File.Exists(specPath))
            {
                var s = JsonDocument.Parse(File.ReadAllText(specPath, Encoding.UTF8)).RootElement;
                if (string.Equals(Path.GetFullPath(s.GetProperty("step").GetString()), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) spec = s;
            }

            var doc = app.NewFamilyDocument(ProtoStepV2Command.FindTemplate(app));
            string outPath = Path.Combine(outDir, name + " - LOD.rfa");
            try
            {
                List<ConnectorRow> rows; ElementId importId = ElementId.InvalidElementId; List<Piece> pieces; XYZ gMin, gMax, center = XYZ.Zero;
                var markers = new Dictionary<ElementId, ConnectorRow>();
                string stage = "import";
                using (var t = new Transaction(doc, "Import CAO"))
                {
                    t.Start();
                    try
                    {
                        var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate);
                        try
                        {
                            importId = sat
                                ? doc.Import(file, new SATImportOptions { Placement = ImportPlacement.Origin, Unit = ImportUnit.Millimeter }, view)
                                : doc.Import(file, new STEPImportOptions { Placement = ImportPlacement.Origin }, view);
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine($"Import Revit refusé ({ex.GetType().Name}) : {(string.IsNullOrWhiteSpace(ex.Message) ? "aucun message" : ex.Message)}");
                            if (!ShapeImporter.IsServiceAvailable()) throw;
                        }
                        if (importId != ElementId.InvalidElementId)
                        {
                            stage = "redressement"; doc.Regenerate();
                            var inst = doc.GetElement(importId); if (inst.Pinned) inst.Pinned = false;
                            if (upY) { ElementTransformUtils.RotateElement(doc, importId, Line.CreateBound(XYZ.Zero, XYZ.BasisX), Math.PI / 2); doc.Regenerate(); }
                            var bb = inst.get_BoundingBox(null);
                            XYZ move = spec.HasValue ? V(spec.Value.GetProperty("expectedBBoxMm").GetProperty("min")) - bb.Min : new XYZ(0, 0, -bb.Min.Z); // sol à Z = 0
                            ElementTransformUtils.MoveElement(doc, importId, move);
                            doc.Regenerate();
                            stage = "analyse";
                            pieces = Pieces(doc, inst.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), Transform.Identity, report);
                        }
                        else
                        {
                            // Secours : conversion directe du fichier en géométrie (pas d'objet import, le niveau Fin sera fait de solides)
                            stage = "conversion de secours";
                            var si = new ShapeImporter(); si.SetDefaultLengthUnit(ImportUnit.Millimeter);
                            var geos = si.Convert(doc, file);
                            report.AppendLine($"Conversion de secours (ShapeImporter) : {geos.Count} objets");
                            var rot = upY ? Transform.CreateRotation(XYZ.BasisX, Math.PI / 2) : Transform.Identity;
                            stage = "analyse";
                            pieces = Pieces(doc, geos, rot, report);
                            double minZ = pieces.Min(p => p.Min.Z);
                            pieces = pieces.Select(p => Shift(p, new XYZ(0, 0, -minZ))).ToList();
                        }
                    }
                    catch (Exception ex) { throw new InvalidOperationException($"étape « {stage} » : {ex.GetType().Name} {ex.Message}", ex); }
                    stage = "maillages"; ConvertMeshes(pieces, report);
                    stage = "boulons"; MarkBolts(pieces, report);
                    gMin = new XYZ(pieces.Min(p => p.Min.X), pieces.Min(p => p.Min.Y), pieces.Min(p => p.Min.Z));
                    gMax = new XYZ(pieces.Max(p => p.Max.X), pieces.Max(p => p.Max.Y), pieces.Max(p => p.Max.Z));
                    // Point d'insertion : centre de l'appareil, au sol (croise les plans de référence centraux du gabarit)
                    stage = "centrage";
                    var d = new XYZ(-(gMin.X + gMax.X) / 2, -(gMin.Y + gMax.Y) / 2, 0);
                    if (importId != ElementId.InvalidElementId) ElementTransformUtils.MoveElement(doc, importId, d);
                    pieces = pieces.Select(p => Shift(p, d)).ToList();
                    gMin += d; gMax += d; center = d;
                    report.AppendLine($"Encombrement : {(gMax.X - gMin.X) / Mm:0} × {(gMax.Y - gMin.Y) / Mm:0} × {(gMax.Z - gMin.Z) / Mm:0} mm ; origine au centre, au sol");
                    t.Commit();
                }
                report.AppendLine($"Import et analyse : {sw.Elapsed.TotalSeconds:0} s");

                // Raccordements proposés : noms du STEP s'ils sont connus, sinon détection géométrique seule (choisis plus tard à la souris)
                rows = spec.HasValue ? FromSpec(spec.Value) : DetectEnds(pieces);
                if (spec.HasValue) foreach (var r in rows) r.P = new[] { r.P[0] + center.X / Mm, r.P[1] + center.Y / Mm, r.P[2] + center.Z / Mm };
                report.AppendLine(spec.HasValue ? "Raccordements : proposés d'après l'analyse du STEP (noms)" : $"Raccordements : {rows.Count} proposés par la géométrie");

                // Choix des raccordements dans une vue 3D simplifiée : clic = rouge <-> vert
                var picker = new ConnectorPicker3DWindow(Preview(pieces, gMin, gMax), rows, name);
                new System.Windows.Interop.WindowInteropHelper(picker).Owner = uiapp.MainWindowHandle;
                if (picker.ShowDialog() != true) { doc.Close(false); return Result.Cancelled; }
                var accepted = picker.Accepted;
                report.AppendLine($"Raccordements retenus : {accepted.Count}");

                // Niveau Fin : toujours en pièces Revit séparées, couleurs d'origine (le repérage reste dans le code pour les contrôles)
                bool finePieces = true, marking = false;

                using (var t = new Transaction(doc, "Niveaux de détail"))
                {
                    t.Start();
                    var mats = ProtoStepV2Command.Materials(doc, new[] { "Encombrement", "Pièces", "Pièces complexes simplifiées" });
                    var colorMats = new Dictionary<string, ElementId>();
                    ElementId neutral = MatFor(doc, new Color(190, 192, 196), colorMats);
                    int coloured = 0, neutralCount = 0;
                    // couleur d'une pièce : la couleur d'origine (CAO) si connue, sinon gris neutre ; repérage en option
                    void Paint(Element e, Piece p, string markGroup)
                    {
                        if (marking) { ProtoStepV2Command.SetMaterial(e, mats, markGroup); return; }
                        var id = p?.Color != null ? MatFor(doc, p.Color, colorMats) : neutral;
                        if (p?.Color != null) coloured++; else neutralCount++;
                        e.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(id);
                    }
                    // Fin : le modèle CAO tel quel (objet import), ou ses solides si l'import a dû passer par le secours
                    if (finePieces)
                    {
                        int ff = 0; var open = new List<List<XYZ[]>>(); Element fineRef = null;
                        foreach (var p in pieces)
                        {
                            var fs = p.Simplified ? p.FineSolid : p.Solid;
                            if (fs != null)
                            {
                                try { var e = FreeFormElement.Create(doc, fs); Paint(e, p, "Pièces"); e.SetVisibility(Vis(false, false, true)); fineRef ??= e; ff++; continue; }
                                catch { }
                            }
                            var tris = p.Tris ?? (p.Solid != null ? WeightBudget.Triangles(p.Solid) : null);
                            if (tris != null && tris.Count > 0) open.Add(tris);
                        }
                        string nested = open.Count > 0 ? NestedMeshes(app, doc, open, name, outDir, fineRef, report) : "aucune";
                        if (importId != ElementId.InvalidElementId) doc.Delete(importId);
                        report.AppendLine($"Fin en pièces : {ff} pièces Revit (solides), {open.Count} maillages ouverts → famille imbriquée : {nested}");
                    }
                    else if (importId != ElementId.InvalidElementId) ((ImportInstance)doc.GetElement(importId)).SetVisibility(Vis(false, false, true));
                    else
                    {
                        int fin = 0;
                        foreach (var p in pieces.Where(p => p.Solid != null))
                            try { var e = FreeFormElement.Create(doc, p.Solid); Paint(e, p, "Pièces"); e.SetVisibility(Vis(false, false, true)); fin++; } catch { }
                        report.AppendLine($"Fin (secours) : {fin} solides, {pieces.Count(p => p.IsMesh)} maillages non repris");
                    }
                    // Grossier : boîte d'encombrement
                    var box = Box(doc, gMin, gMax); Paint(box, null, "Encombrement"); box.SetVisibility(Vis(true, false, false));
                    // Moyen : les vraies pièces, sauf les complexes remplacées par une boîte ou un cylindre ; les toutes petites sont omises
                    double tiny = 0.003 * (gMax - gMin).GetLength();
                    int real = 0, simple = 0, skipped = 0;
                    foreach (var p in pieces)
                    {
                        if (p.Diag < tiny || p.Bolt) { skipped++; continue; }
                        GenericForm e = null; bool isSimple = p.Complex;
                        if (!isSimple) { try { e = FreeFormElement.Create(doc, p.Solid); } catch { isSimple = true; } }
                        if (isSimple) { try { e = Primitive(doc, p).Item1; } catch { } }
                        if (e == null) { skipped++; continue; }
                        Paint(e, p, isSimple ? "Pièces complexes simplifiées" : "Pièces"); e.SetVisibility(Vis(false, true, false));
                        if (isSimple) simple++; else real++;
                    }
                    if (!marking) report.AppendLine($"Couleurs : {colorMats.Count - 1} couleurs d'origine reprises ; {coloured} éléments colorés, {neutralCount} en gris neutre");
                    report.AppendLine($"Moyen : {real} pièces réelles, {simple} pièces complexes simplifiées (boîte / cylindre), {skipped} omises (visserie ou < {tiny / Mm:0} mm)");
                    // Plans de référence sur l'encombrement
                    ReferencePlanes(doc, gMin, gMax, report);
                    ProtoStepV2Command.Connectors(doc, accepted, mats, report);
                    var fm = doc.FamilyManager; if (fm.CurrentType == null) fm.NewType(name);
                    var src = fm.get_Parameter("Source CAO") ?? fm.AddParameter("Source CAO", GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                    fm.Set(src, Path.GetFileName(file));
                    t.Commit();
                }
                ProtoStepV2Command.Save(doc, outPath);
                report.AppendLine($"Famille : {sw.Elapsed.TotalSeconds:0} s, {new FileInfo(outPath).Length / 1024.0 / 1024:0.0} Mo");
                try { uiapp.OpenAndActivateDocument(outPath); } catch { }
            }
            catch (Exception ex) { report.AppendLine("ÉCHEC : " + ex.Message); try { if (doc.IsValidObject) doc.Close(false); } catch { } }

            File.WriteAllText(Path.Combine(outDir, name + " - rapport.txt"), report.ToString(), new UTF8Encoding(true));
            TaskDialog.Show("Proto CAO → famille LOD", report + "\nDossier : " + outDir);
            return Result.Succeeded;
        }

        private sealed class IdFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            private readonly Func<Element, bool> ok; public IdFilter(Func<Element, bool> ok) { this.ok = ok; }
            public bool AllowElement(Element e) => ok(e);
            public bool AllowReference(Reference r, XYZ p) => true;
        }

        // 1. cliquer les pastilles justes (les vertes sont déjà sélectionnées) ; 2. ajouter des raccordements oubliés en cliquant
        // le bord circulaire d'un tube ou d'une bride ; 3. nommer le réseau de chacun.
        private static List<ConnectorRow> PickConnectors(UIDocument uidoc, Dictionary<ElementId, ConnectorRow> markers, double topZ, string name)
        {
            var doc = uidoc.Document;
            var v3 = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate);
            if (v3 != null)
            {
                try { uidoc.ActiveView = v3; } catch { }
                using (var t = new Transaction(doc, "Vue")) { t.Start(); try { v3.DetailLevel = ViewDetailLevel.Medium; } catch { } t.Commit(); }
            }
            var chosen = new List<ConnectorRow>();
            var pre = markers.Where(kv => kv.Value.Keep && doc.GetElement(kv.Key) != null).Select(kv => new Reference(doc.GetElement(kv.Key))).ToList();
            try
            {
                var picked = uidoc.Selection.PickObjects(Autodesk.Revit.UI.Selection.ObjectType.Element, new IdFilter(e => markers.ContainsKey(e.Id)),
                    "Raccordements : cliquez les pastilles justes (vertes = déjà proposées, cliquez pour retirer / ajouter), puis « Terminer »", pre);
                chosen.AddRange(picked.Select(r => markers[r.ElementId]));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { chosen.AddRange(markers.Values.Where(r => r.Keep)); }

            var mid = new XYZ(0, 0, topZ / 2);
            while (true)
            {
                Reference r;
                try
                {
                    r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Edge, new IdFilter(e => !markers.ContainsKey(e.Id)),
                        "Raccordement oublié ? Cliquez le bord circulaire du tube ou de la bride (Échap pour terminer)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                var arc = (doc.GetElement(r).GetGeometryObjectFromReference(r) as Edge)?.AsCurve() as Arc;
                if (arc == null) { TaskDialog.Show("Raccordement", "Ce bord n'est pas circulaire : cliquez le cercle d'extrémité du tube ou de la bride."); continue; }
                var n = arc.Normal.Normalize(); if (n.DotProduct(arc.Center - mid) < 0) n = -n; // vers l'extérieur de l'appareil
                int dn = OD.OrderBy(o => Math.Abs(o.od - 2 * arc.Radius / Mm)).First().dn;
                chosen.Add(new ConnectorRow
                {
                    Keep = true, Name = $"À qualifier DN{dn}", Label = "À qualifier", Diameter = dn, Part = "ajouté à la main", Why = $"cercle cliqué Ø{2 * arc.Radius / Mm:0} mm",
                    P = new[] { arc.Center.X / Mm, arc.Center.Y / Mm, arc.Center.Z / Mm }, Dir = new[] { n.X, n.Y, n.Z },
                });
            }
            if (chosen.Count == 0) return chosen;
            foreach (var c in chosen) c.Keep = true;
            var win = new ConnectorReviewWindow(chosen, name,
                "Raccordements que vous avez choisis. Donnez le réseau de chacun (départ, retour, gaz…) et corrigez le DN si besoin. Décochez pour en retirer un.");
            new System.Windows.Interop.WindowInteropHelper(win).Owner = uidoc.Application.MainWindowHandle;
            return win.ShowDialog() == true ? win.Accepted : chosen;
        }

        // Modèle d'aperçu pour la fenêtre 3D : chaque pièce triangulée puis fortement simplifiée, regroupée par couleur, en mètres.
        private static List<ConnectorPicker3DWindow.MeshPart> Preview(List<Piece> pieces, XYZ gMin, XYZ gMax)
        {
            double gd = (gMax - gMin).GetLength();
            var parts = new Dictionary<string, ConnectorPicker3DWindow.MeshPart>();
            foreach (var p in pieces)
            {
                if (p.Diag < 0.004 * gd || p.Bolt) continue;
                List<XYZ[]> tris = null;
                try { tris = p.Tris ?? (p.Solid != null ? WeightBudget.Triangles(p.Solid) : null); } catch { }
                if (tris == null || tris.Count == 0) continue;
                tris = WeightBudget.Cluster(tris, Math.Max(0.004 * gd, 0.04 * p.Diag));
                var c = p.Color ?? new Color(185, 188, 194);
                string key = $"{c.Red},{c.Green},{c.Blue}";
                if (!parts.TryGetValue(key, out var part)) parts[key] = part = new ConnectorPicker3DWindow.MeshPart { Color = System.Windows.Media.Color.FromRgb(c.Red, c.Green, c.Blue) };
                foreach (var t in tris) foreach (var v in t) part.Tris.Add(new System.Windows.Media.Media3D.Point3D(v.X * 0.3048, v.Y * 0.3048, v.Z * 0.3048));
            }
            return parts.Values.ToList();
        }

        private static void ReferencePlanes(Document doc, XYZ a, XYZ b, StringBuilder report)
        {
            int ok = 0; double m = 300 * Mm;
            var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate);
            var elev = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && Math.Abs(v.ViewDirection.Y) > 0.9);
            void Add(XYZ p1, XYZ p2, XYZ cut, View v, string n)
            {
                if (v == null) return;
                try { var rp = doc.FamilyCreate.NewReferencePlane(p1, p2, cut, v); rp.Name = n; ok++; } catch { }
            }
            Add(new XYZ(a.X, a.Y - m, 0), new XYZ(a.X, b.Y + m, 0), XYZ.BasisZ, plan, "Encombrement gauche");
            Add(new XYZ(b.X, a.Y - m, 0), new XYZ(b.X, b.Y + m, 0), XYZ.BasisZ, plan, "Encombrement droite");
            Add(new XYZ(a.X - m, a.Y, 0), new XYZ(b.X + m, a.Y, 0), XYZ.BasisZ, plan, "Encombrement avant");
            Add(new XYZ(a.X - m, b.Y, 0), new XYZ(b.X + m, b.Y, 0), XYZ.BasisZ, plan, "Encombrement arrière");
            Add(new XYZ(a.X - m, 0, b.Z), new XYZ(b.X + m, 0, b.Z), XYZ.BasisY, elev, "Encombrement haut");
            report.AppendLine($"Plans de référence : {ok}/5");
        }

        private static XYZ V(JsonElement a) => new XYZ(a[0].GetDouble() * Mm, a[1].GetDouble() * Mm, a[2].GetDouble() * Mm);
        private static FamilyElementVisibility Vis(bool coarse, bool medium, bool fine) =>
            new FamilyElementVisibility(FamilyElementVisibilityType.Model) { IsShownInCoarse = coarse, IsShownInMedium = medium, IsShownInFine = fine };

        private static string Header(string file, bool sat)
        {
            try
            {
                using var r = new StreamReader(file, Encoding.Latin1);
                var buf = new char[4000]; int n = r.Read(buf, 0, buf.Length); var head = new string(buf, 0, n);
                if (sat) return head.Split('\n').Skip(1).FirstOrDefault()?.Trim() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(head, @"FILE_NAME\s*\(([\s\S]*?)\)\s*;");
                return string.Join(" ", System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, "'([^']*)'").Cast<System.Text.RegularExpressions.Match>().Select(x => x.Groups[1].Value).Where(x => x.Length > 0).TakeLast(2));
            }
            catch { return ""; }
        }

        // --- Géométrie importée -> pièces (encombrement, cercles, cylindres) --------------------------------
        // --- Maillages -> solides : ressoudure des sommets, puis simplification si possible ------------------
        private static Solid TrySolid(List<XYZ[]> tris)
        {
            if (tris.Count < 4) return null;
            try
            {
                var b = new TessellatedShapeBuilder { Target = TessellatedShapeBuilderTarget.Solid, Fallback = TessellatedShapeBuilderFallback.Abort };
                b.OpenConnectedFaceSet(true);
                foreach (var t in tris) { try { b.AddFace(new TessellatedFace(t.ToList(), ElementId.InvalidElementId)); } catch { } }
                b.CloseConnectedFaceSet(); b.Build();
                return b.GetBuildResult().GetGeometricalObjects().OfType<Solid>().FirstOrDefault(s => s.Volume > 1e-9);
            }
            catch { return null; }
        }

        private static void ConvertMeshes(List<Piece> pieces, StringBuilder report)
        {
            int total = 0, simplified = 0, welded = 0, trisBefore = 0, trisAfter = 0;
            foreach (var p in pieces.Where(p => p.IsMesh && p.Tris != null))
            {
                total++; trisBefore += p.Tris.Count;
                // ressoudure (0,5 mm) : referme les bords sans changer la forme → version exacte pour le niveau Fin
                var weld = WeightBudget.Cluster(p.Tris, 0.5 * Mm);
                p.FineSolid = TrySolid(weld);
                // version simplifiée (sommets regroupés à 2 % de la taille de la pièce, 3 mm minimum) → niveau Moyen
                var light = WeightBudget.Cluster(p.Tris, Math.Max(3 * Mm, 0.02 * p.Diag));
                var s = TrySolid(light); List<XYZ[]> used = light;
                if (s == null) { s = p.FineSolid; used = weld; if (s != null) welded++; }
                else simplified++;
                if (s == null) continue;
                p.Solid = s; p.Faces = s.Faces.Size; p.IsMesh = false; p.Simplified = true; trisAfter += used.Count;
            }
            if (total > 0)
                report.AppendLine($"Maillages : {total} ; {simplified} simplifiés et refermés, {welded} seulement ressoudés, {total - simplified - welded} restés ouverts (boîte en Moyen) ; triangles {trisBefore} → {trisAfter} pour les pièces converties");
        }

        // Maillages ouverts : une forme directe ne se masque pas par niveau de détail ; on les range dans une famille
        // imbriquée (modèle générique) dont l'instance, elle, reçoit la visibilité « Fin seul ».
        private static string NestedMeshes(Autodesk.Revit.ApplicationServices.Application app, Document host, List<List<XYZ[]>> meshes, string name, string outDir, Element fineRef, StringBuilder report)
        {
            try
            {
                string tpl = new[] { app.FamilyTemplatePath, @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\French", @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\English" }
                    .Where(Directory.Exists).SelectMany(r => new[] { "Modèle générique métrique.rft", "Metric Generic Model.rft" }.Select(n => Directory.EnumerateFiles(r, n, SearchOption.AllDirectories).FirstOrDefault()))
                    .FirstOrDefault(f => f != null) ?? throw new FileNotFoundException("gabarit Modèle générique introuvable");
                var nd = app.NewFamilyDocument(tpl);
                int ok = 0;
                using (var t = new Transaction(nd, "Maillages"))
                {
                    t.Start();
                    var cat = WeightBudget.ShapeCategory(nd);
                    foreach (var m in meshes) if (WeightBudget.AddMesh(nd, cat, m, null)) ok++;
                    t.Commit();
                }
                string path = Path.Combine(outDir, name + " - maillages Fin.rfa");
                ProtoStepV2Command.Save(nd, path);
                if (!host.LoadFamily(path, out Family fam)) fam = new FilteredElementCollector(host).OfClass(typeof(Family)).Cast<Family>().First(f => f.Name == Path.GetFileNameWithoutExtension(path));
                var sym = (FamilySymbol)host.GetElement(fam.GetFamilySymbolIds().First());
                if (!sym.IsActive) sym.Activate();
                var inst = host.FamilyCreate.NewFamilyInstance(XYZ.Zero, sym, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                // visibilité « Fin seul » : recopiée depuis une pièce déjà réglée ainsi
                var src = fineRef?.get_Parameter(BuiltInParameter.GEOM_VISIBILITY_PARAM); var dst = inst.get_Parameter(BuiltInParameter.GEOM_VISIBILITY_PARAM);
                bool vis = src != null && dst != null && !dst.IsReadOnly && dst.Set(src.AsInteger());
                return $"{ok}/{meshes.Count} maillages{(vis ? ", visible en Fin seul" : ", ATTENTION visibilité non réglée (visible partout)")}";
            }
            catch (Exception ex) { report.AppendLine("  famille imbriquée : ÉCHEC " + ex.Message); return "échec"; }
        }

        // --- Boulons / écrous / tiges filetées reconnus par leur forme -----------------------------------
        // Petite pièce autour d'un petit cylindre (rayon 3 à 20 mm), pas plus large que ~4,5 rayons,
        // placée sur le cercle de perçage d'une bride voisine (cercle bien plus grand, même axe de perçage).
        private static void MarkBolts(List<Piece> pieces, StringBuilder report)
        {
            var flanges = pieces.Where(p => p.Circles.Any(c => c.R >= 40 * Mm)).ToList();
            int bolts = 0;
            foreach (var p in pieces)
            {
                if (p.Solid == null || p.Simplified) continue;
                var d = p.Max - p.Min; double[] dims = { d.X, d.Y, d.Z };
                if (dims.Max() > 300 * Mm) continue;
                foreach (var c in p.Cyls.Where(c => c.r >= 3 * Mm && c.r <= 20 * Mm).OrderByDescending(c => c.r))
                {
                    double[] ax = { Math.Abs(c.axis.X), Math.Abs(c.axis.Y), Math.Abs(c.axis.Z) };
                    int k = Array.IndexOf(ax, ax.Max()); if (ax[k] < 0.999) continue;
                    var cross = Enumerable.Range(0, 3).Where(i => i != k).Select(i => dims[i]).ToArray();
                    if (cross.Max() > 4.5 * c.r + 2 * Mm) continue;
                    var axis = k == 0 ? XYZ.BasisX : k == 1 ? XYZ.BasisY : XYZ.BasisZ;
                    var mid = (p.Min + p.Max) / 2;
                    bool onPcd = flanges.Any(f => f != p && f.Circles.Any(fc =>
                        fc.R >= 2.5 * c.r && fc.N.CrossProduct(axis).GetLength() < 0.02 &&
                        Math.Abs((mid - fc.C).DotProduct(axis)) < dims[k] / 2 + 60 * Mm &&
                        ((mid - fc.C) - axis * (mid - fc.C).DotProduct(axis)).GetLength() is double r && r > 0.4 * fc.R && r < 1.05 * fc.R));
                    if (onPcd) { p.Bolt = true; bolts++; break; }
                }
            }
            report.AppendLine($"Visserie reconnue par la forme : {bolts} pièces (boulons, écrous, tiges) → visibles en Fin seulement");
        }

        // Couleur d'origine d'une face ou d'un maillage importé : matériau s'il y en a un, sinon couleur du style graphique (calque CAO).
        private static Color ColorOf(Document doc, ElementId mat, ElementId gs)
        {
            try
            {
                if (mat != null && mat != ElementId.InvalidElementId && doc.GetElement(mat) is Material m && m.Color.IsValid) return m.Color;
                if (gs != null && gs != ElementId.InvalidElementId && doc.GetElement(gs) is GraphicsStyle g && g.GraphicsStyleCategory?.LineColor is Color c && c.IsValid) return c;
            }
            catch { }
            return null;
        }

        private static ElementId MatFor(Document doc, Color c, Dictionary<string, ElementId> cache)
        {
            string key = $"{c.Red},{c.Green},{c.Blue}";
            if (cache.TryGetValue(key, out var id)) return id;
            string name = $"CAO - {c.Red:000}-{c.Green:000}-{c.Blue:000}";
            id = new FilteredElementCollector(doc).OfClass(typeof(Material)).FirstOrDefault(x => x.Name == name)?.Id ?? Material.Create(doc, name);
            var mat = (Material)doc.GetElement(id); mat.Color = c; mat.SurfaceForegroundPatternColor = c;
            cache[key] = id; return id;
        }

        private static Piece Shift(Piece p, XYZ d)
        {
            var tf = Transform.CreateTranslation(d);
            return new Piece
            {
                Min = p.Min + d, Max = p.Max + d, IsMesh = p.IsMesh, Faces = p.Faces, Color = p.Color, Simplified = p.Simplified, Bolt = p.Bolt,
                FineSolid = p.FineSolid == null ? null : SolidUtils.CreateTransformed(p.FineSolid, tf),
                Tris = p.Tris?.Select(t => new[] { t[0] + d, t[1] + d, t[2] + d }).ToList(),
                Solid = p.Solid == null ? null : SolidUtils.CreateTransformed(p.Solid, tf),
                Circles = p.Circles.Select(c => new Circle { C = c.C + d, N = c.N, R = c.R }).ToList(),
                Cyls = p.Cyls.Select(c => (c.o + d, c.axis, c.r)).ToList(),
            };
        }

        private static List<Piece> Pieces(Document doc, IEnumerable<GeometryObject> objs, Transform tf, StringBuilder report)
        {
            var list = new List<Piece>(); int solids = 0, meshes = 0;
            bool identity = tf.IsIdentity;
            void AddSolid(Solid s)
            {
                if (!identity) s = SolidUtils.CreateTransformed(s, tf);
                IList<Solid> parts; try { parts = SolidUtils.SplitVolumes(s); } catch { parts = new[] { s }; }
                foreach (var v0 in parts)
                {
                    if (v0.Volume < 1e-9) continue;
                    var v = SolidUtils.Clone(v0);
                    var b = v.GetBoundingBox(); var c0 = b.Transform.OfPoint(b.Min); var c1 = b.Transform.OfPoint(b.Max);
                    var p = new Piece { Min = new XYZ(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y), Math.Min(c0.Z, c1.Z)), Max = new XYZ(Math.Max(c0.X, c1.X), Math.Max(c0.Y, c1.Y), Math.Max(c0.Z, c1.Z)), Solid = v, Faces = v.Faces.Size };
                    var areas = new Dictionary<string, (Color c, double a)>();
                    foreach (Face f in v0.Faces) { var col = ColorOf(doc, f.MaterialElementId, f.GraphicsStyleId); if (col == null) continue; var k = $"{col.Red},{col.Green},{col.Blue}"; areas[k] = areas.TryGetValue(k, out var x) ? (col, x.a + f.Area) : (col, f.Area); }
                    if (areas.Count > 0) p.Color = areas.Values.OrderByDescending(x => x.a).First().c;
                    var keys = new HashSet<string>();
                    foreach (Edge e in v.Edges)
                        if (e.AsCurve() is Arc a && a.Radius >= 8 * Mm)
                        {
                            var key = $"{Math.Round(a.Center.X / Mm)},{Math.Round(a.Center.Y / Mm)},{Math.Round(a.Center.Z / Mm)},{Math.Round(a.Radius / Mm)}";
                            if (keys.Add(key)) p.Circles.Add(new Circle { C = a.Center, N = a.Normal.Normalize(), R = a.Radius });
                        }
                    foreach (Face f in v.Faces)
                        if (f is CylindricalFace cf) p.Cyls.Add((cf.Origin, cf.Axis.Normalize(), cf.get_Radius(0).GetLength()));
                    list.Add(p); solids++;
                }
            }
            void Walk(IEnumerable<GeometryObject> ge)
            {
                foreach (var o in ge)
                {
                    if (o is Solid s && s.Volume > 1e-9) AddSolid(s);
                    else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
                    else if (o is Mesh m0 && m0.Vertices.Count > 0)
                    {
                        meshes++;
                        var m = identity ? m0 : m0.get_Transformed(tf);
                        var v = m.Vertices;
                        var tris = new List<XYZ[]>(m.NumTriangles);
                        for (int i = 0; i < m.NumTriangles; i++) { var t = m.get_Triangle(i); tris.Add(new[] { t.get_Vertex(0), t.get_Vertex(1), t.get_Vertex(2) }); }
                        list.Add(new Piece { IsMesh = true, Tris = tris, Color = ColorOf(doc, m0.MaterialElementId, m0.GraphicsStyleId), Min = new XYZ(v.Min(q => q.X), v.Min(q => q.Y), v.Min(q => q.Z)), Max = new XYZ(v.Max(q => q.X), v.Max(q => q.Y), v.Max(q => q.Z)) });
                    }
                }
            }
            Walk(objs);
            report.AppendLine($"Analyse : {solids} volumes, {meshes} maillages");
            return list;
        }

        // --- Moyen : boîte ou cylindre selon la forme dominante ----------------------------------------------
        private static (GenericForm, bool) Primitive(Document doc, Piece p)
        {
            var d = p.Max - p.Min; double[] dims = { d.X, d.Y, d.Z };
            (XYZ o, XYZ axis, double r)? best = null; int bestAx = -1;
            foreach (var c in p.Cyls)
            {
                double[] ax = { Math.Abs(c.axis.X), Math.Abs(c.axis.Y), Math.Abs(c.axis.Z) };
                int k = Array.IndexOf(ax, ax.Max()); if (ax[k] < 0.999) continue;
                var cross = Enumerable.Range(0, 3).Where(i => i != k).Select(i => dims[i]).ToArray();
                if (2 * c.r < 0.7 * cross.Max() || 2 * c.r > 1.05 * cross.Max() + 2 * Mm || cross.Min() < 0.7 * 2 * c.r) continue;
                if (best == null || c.r > best.Value.r) { best = c; bestAx = k; }
            }
            if (best != null)
            {
                var b = best.Value;
                double[] o = { b.o.X, b.o.Y, b.o.Z }, mn = { p.Min.X, p.Min.Y, p.Min.Z }, mx = { p.Max.X, p.Max.Y, p.Max.Z };
                var a = (double[])o.Clone(); a[bestAx] = mn[bestAx];
                var dir = bestAx == 0 ? XYZ.BasisX : bestAx == 1 ? XYZ.BasisY : XYZ.BasisZ;
                double len = mx[bestAx] - mn[bestAx];
                if (len < 2 * Mm) return (null, false);
                return (ProtoStepV2Command.Disc(doc, new XYZ(a[0], a[1], a[2]), dir, b.r, len), true);
            }
            if (d.X < 2 * Mm || d.Y < 2 * Mm || d.Z < 2 * Mm) return (null, false);
            return (Box(doc, p.Min, p.Max), false);
        }

        private static Extrusion Box(Document doc, XYZ a, XYZ b)
        {
            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, a.Z));
            var pts = new[] { new XYZ(a.X, a.Y, a.Z), new XYZ(b.X, a.Y, a.Z), new XYZ(b.X, b.Y, a.Z), new XYZ(a.X, b.Y, a.Z) };
            var loop = new CurveArray(); for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
            var prof = new CurveArrArray(); prof.Append(loop);
            return doc.FamilyCreate.NewExtrusion(true, prof, SketchPlane.Create(doc, plane), b.Z - a.Z);
        }

        private static List<ConnectorRow> FromSpec(JsonElement spec) =>
            spec.GetProperty("connectors").EnumerateArray().Select(c => new ConnectorRow
            {
                Keep = c.GetProperty("keep").GetBoolean(), Name = c.GetProperty("name").GetString(), Label = c.GetProperty("label").GetString(),
                Diameter = c.GetProperty("diameter").GetInt32(), Part = c.GetProperty("part").GetString(), Why = c.GetProperty("why").GetString(),
                P = c.GetProperty("p").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Dir = c.GetProperty("dir").EnumerateArray().Select(v => v.GetDouble()).ToArray(),
            }).ToList();

        // --- Raccordements libres, géométrie seule (même logique que spec2.mjs) ----------------------------
        private static readonly (int dn, double od)[] OD =
        {
            (15, 21.3), (20, 26.9), (25, 33.7), (32, 42.4), (40, 48.3), (50, 60.3), (65, 76.1), (80, 88.9), (100, 114.3), (125, 139.7), (150, 168.3),
            (200, 219.1), (250, 273), (300, 323.9), (350, 355.6), (400, 406.4), (450, 457), (500, 508), (600, 610), (700, 711), (800, 813), (900, 914), (1000, 1016),
        };

        private sealed class End { public Piece P; public XYZ C, N; public double ROut, RIn; public int Dn; public string How; public bool Recessed, Free; }

        private static List<ConnectorRow> DetectEnds(List<Piece> pieces)
        {
            var ends = new List<End>();
            foreach (var p in pieces)
            {
                if (p.Circles.Count == 0) continue;
                // cercles regroupés par axe (même direction, même droite support)
                var axes = new List<(XYZ n, XYZ foot, List<(Circle c, double t)> cs)>();
                foreach (var c in p.Circles)
                {
                    var n = c.N; if (Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? n.X < 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? n.Y < 0 : n.Z < 0) n = -n;
                    double t = c.C.DotProduct(n); var foot = c.C - n * t;
                    var ax = axes.FirstOrDefault(a => a.n.CrossProduct(n).GetLength() < 0.01 && (a.foot - foot).GetLength() < 2 * Mm);
                    if (ax.cs == null) { ax = (n, foot, new List<(Circle, double)>()); axes.Add(ax); }
                    ax.cs.Add((c, t));
                }
                foreach (var ax in axes)
                {
                    double rMaxSame = axes.Where(a => a.n.CrossProduct(ax.n).GetLength() < 0.01).SelectMany(a => a.cs).Max(x => x.c.R);
                    if (ax.cs.Max(x => x.c.R) < 0.35 * rMaxSame) continue; // trous de boulons
                    double tmin = ax.cs.Min(x => x.t), tmax = ax.cs.Max(x => x.t);
                    foreach (var (tEnd, s) in new[] { (tmin, -1.0), (tmax, 1.0) })
                    {
                        var at = ax.cs.Where(x => Math.Abs(x.t - tEnd) < 3 * Mm).ToList();
                        double rOut = at.Max(x => x.c.R), rIn = at.Min(x => x.c.R);
                        if (rOut < 10 * Mm) continue;
                        var n = ax.n * s; var center = ax.foot + ax.n * tEnd;
                        double reach = Math.Max(Proj(p.Min, n, center), Proj(p.Max, n, center));
                        bool mated = pieces.Any(o => o != p && Near(o, center, rOut + 15 * Mm) && Beyond(o, center, n) &&
                            o.Circles.Any(c => c.N.CrossProduct(n).GetLength() < 0.02 && Math.Abs((c.C - center).DotProduct(n)) < 12 * Mm &&
                                               (c.C - center).CrossProduct(n).GetLength() < 3 * Mm && c.R > 0.5 * rIn && c.R < 1.6 * rOut));
                        if (mated) continue;
                        int dn = 0; string how = "géométrie";
                        foreach (var x in ax.cs) foreach (var (d, od) in OD) if (Math.Abs(2 * x.c.R / Mm - od) / od < 0.03 && d > dn) dn = d;
                        if (dn == 0) { double bore = 2 * rIn / Mm; dn = OD.OrderBy(o => Math.Abs(o.od - bore)).First().dn; how = "alésage (approx.)"; }
                        ends.Add(new End { P = p, C = center, N = n, ROut = rOut, RIn = rIn, Dn = dn, How = how, Recessed = reach > 5 * Mm });
                    }
                }
            }
            // doublons, perçages traversants
            var dedup = new List<End>();
            foreach (var e in ends.OrderByDescending(e => e.ROut)) if (!dedup.Any(u => (u.C - e.C).GetLength() < 25 * Mm && u.N.DotProduct(e.N) > 0.99)) dedup.Add(e);
            var uniq = dedup.Where(e => !dedup.Any(o => o != e && o.P == e.P && o.N.DotProduct(e.N) < -0.99 &&
                (o.C - e.C).GetLength() < Math.Max(60 * Mm, 0.5 * e.ROut) && (o.C - e.C).CrossProduct(e.N).GetLength() < 3 * Mm)).ToList();
            double total = Vol(pieces);
            var obstacles = pieces.Where(o => o.Vol < 0.2 * total).ToList();
            foreach (var e in uniq)
            {
                double L = Math.Min(600 * Mm, Math.Max(150 * Mm, 3 * e.ROut)); e.Free = true;
                for (double s = 20 * Mm; s <= L && e.Free; s += 20 * Mm) { var q = e.C + e.N * s; if (obstacles.Any(o => o != e.P && Inside(o, q))) e.Free = false; }
            }
            return uniq.Select(e =>
            {
                bool flange = e.ROut > 1.4 * e.RIn;
                double score = (flange ? 3 : 0) + (e.Free ? 3 : 0) + (e.Recessed ? -2 : 1) + Math.Log10(e.Dn);
                return new ConnectorRow
                {
                    Keep = flange && e.Free && (e.Recessed ? e.Dn >= 100 : e.Dn >= 25), Name = $"À qualifier DN{e.Dn}", Label = "À qualifier", Diameter = e.Dn, Score = score,
                    Part = $"pièce {(e.P.Max - e.P.Min).X / Mm:0}×{(e.P.Max - e.P.Min).Y / Mm:0}×{(e.P.Max - e.P.Min).Z / Mm:0} mm",
                    Why = $"DN par {e.How}{(flange ? " ; bride" : "")}{(e.Free ? " ; dégagé" : " ; encombré")}{(e.Recessed ? " ; en retrait" : "")}",
                    P = new[] { e.C.X / Mm, e.C.Y / Mm, e.C.Z / Mm }, Dir = new[] { e.N.X, e.N.Y, e.N.Z },
                };
            }).OrderByDescending(r => r.Score).Take(40).ToList();
        }

        private static double Proj(XYZ corner, XYZ n, XYZ c) =>
            Math.Max((corner.X - c.X) * n.X, 0) + Math.Max((corner.Y - c.Y) * n.Y, 0) + Math.Max((corner.Z - c.Z) * n.Z, 0);
        private static bool Beyond(Piece o, XYZ c, XYZ n) =>
            Math.Max((o.Min.X - c.X) * n.X, (o.Max.X - c.X) * n.X) + Math.Max((o.Min.Y - c.Y) * n.Y, (o.Max.Y - c.Y) * n.Y) + Math.Max((o.Min.Z - c.Z) * n.Z, (o.Max.Z - c.Z) * n.Z) > 3 * Mm;
        private static bool Near(Piece o, XYZ c, double tol) =>
            c.X >= o.Min.X - tol && c.X <= o.Max.X + tol && c.Y >= o.Min.Y - tol && c.Y <= o.Max.Y + tol && c.Z >= o.Min.Z - tol && c.Z <= o.Max.Z + tol;
        private static bool Inside(Piece o, XYZ q) => Near(o, q, 1 * Mm);
        private static double Vol(List<Piece> ps)
        {
            var mn = new XYZ(ps.Min(p => p.Min.X), ps.Min(p => p.Min.Y), ps.Min(p => p.Min.Z)); var mx = new XYZ(ps.Max(p => p.Max.X), ps.Max(p => p.Max.Y), ps.Max(p => p.Max.Z));
            return (mx.X - mn.X) * (mx.Y - mn.Y) * (mx.Z - mn.Z);
        }
    }
}

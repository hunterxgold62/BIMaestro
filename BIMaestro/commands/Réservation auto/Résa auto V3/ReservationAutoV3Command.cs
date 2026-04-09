#region Imports
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Dynamo.Applications;
using Dynamo.Applications.Properties;
using Licensing;
#endregion

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class ReservationAutoV3Command : BaseTrackedCommand
    {
        protected override string ButtonId => "ReservationAutoV3Command";

        // Evite de spammer des warnings
        private static bool _voidCutWarnShown = false;

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIApplication uiApp = data.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // Load config
                var cfg = ReservationAutoV3ConfigStore.LoadOrDefault();

                // Auto-détection : si V1/V2 déjà dans la maquette, on s’en sert
                EnsureAutoConfigFromProject(doc, cfg);
                ReservationAutoV3ConfigStore.Save(cfg, out _);

                // UI
                var win = new ReservationAutoV3Window(doc, cfg);
                if (win.ShowDialog() != true)
                    return Result.Cancelled;

                cfg = win.Config ?? cfg;

                // Resolve profile according to run
                var prof = win.SelectedExecutionProfile ?? GetProfileForRun(cfg, win.SelectedHost, win.SelectedShape);
                if (prof == null || string.IsNullOrWhiteSpace(prof.FamilyName))
                {
                    TaskDialog.Show("BIMaestro",
                        "Aucune famille configurée pour ce cas.\nVa dans Configuration et charge un .RFA puis mappe les paramètres.");
                    return Result.Cancelled;
                }

                if (!TryResolveSymbol(doc, prof, out var reservationSymbol))
                {
                    TaskDialog.Show("BIMaestro",
                        "La famille configurée n’est pas chargée dans ce projet.\nCharge-la (onglet Configuration) ou corrige le mapping.");
                    return Result.Cancelled;
                }

                bool isWall = win.SelectedHost == ReservationAutoV3Window.HostTarget.Mur;
                bool isRect = win.SelectedShape == ReservationAutoV3Window.ShapeTarget.Rectangulaire;

                if (win.AutomatiqueEnabled && !isWall)
                {
                    TaskDialog.Show("BIMaestro", "Mode automatique : disponible uniquement pour les murs.");
                    return Result.Cancelled;
                }

                using (var t = new Transaction(doc, "Réservations Auto V3"))
                {
                    t.Start();
                    if (!reservationSymbol.IsActive) reservationSymbol.Activate();

                    if (!win.AutomatiqueEnabled)
                        RunManual(uiDoc, doc, win, cfg, prof, reservationSymbol);
                    else
                        RunAutomatic(doc, win, cfg, prof, reservationSymbol);

                    t.Commit();
                }

                // Dynamo
                if (win.DynamoAutoEnabled && !string.IsNullOrWhiteSpace(cfg.DynamoPath))
                    TryRunDynamo(data, cfg.DynamoPath);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // =========================
        // AUTO CONFIG (V1/V2 déjà dans maquette)
        // =========================
        private static void EnsureAutoConfigFromProject(Document doc, ReservationAutoV3Config cfg)
        {
            if (doc == null || cfg == null) return;

            EnsureProfile(doc, cfg.WallRect, new[]
            {
                "CML_Réservation rectangulaire verticale",
                "CML_Réservation rectangulaire murale",
                "Réservation rectangulaire murale"
            }, ProfileKind.WallRect);

            EnsureProfile(doc, cfg.FloorRect, new[]
            {
                "CML_Réservation rectangulaire horizontale",
                "CML_Réservation rectangulaire sol",
                "Réservation rectangulaire sol"
            }, ProfileKind.FloorRect);

            EnsureProfile(doc, cfg.WallCirc, new[]
            {
                "CML_Réservation circulaire verticale",
                "CML_Réservation circulaire murale",
                "Réservation circulaire murale"
            }, ProfileKind.WallCirc);

            EnsureProfile(doc, cfg.FloorCirc, new[]
            {
                "CML_Réservation circulaire horizontale",
                "CML_Réservation circulaire sol",
                "Réservation circulaire sol"
            }, ProfileKind.FloorCirc);
        }

        private enum ProfileKind { WallRect, WallCirc, FloorRect, FloorCirc }

        private static void EnsureProfile(Document doc, ProfileConfig p, string[] familyCandidates, ProfileKind kind)
        {
            if (p == null) return;

            if (!string.IsNullOrWhiteSpace(p.FamilyName))
            {
                if (TryResolveSymbol(doc, p, out _))
                    return;
            }

            foreach (var famName in familyCandidates)
            {
                var sym = FindFirstSymbolByFamilyName(doc, famName);
                if (sym == null) continue;

                p.FamilyName = sym.Family.Name;
                p.TypeName = sym.Name;

                AutoMapParameters(sym, p, kind);
                return;
            }
        }

        private static FamilySymbol FindFirstSymbolByFamilyName(Document doc, string familyName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(familyName)) return null;

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s?.Family?.Name != null)
                .Where(s => FamilyNameContains(s.Family.Name, familyName))
                .ToList();

            return symbols.FirstOrDefault();
        }

        private static bool FamilyNameContains(string currentFamilyName, string expectedName)
        {
            string current = RemoveCmlPrefix(currentFamilyName);
            string expected = RemoveCmlPrefix(expectedName);
            return current.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RemoveCmlPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.StartsWith("CML_", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(4)
                : value;
        }

        private static void AutoMapParameters(FamilySymbol sym, ProfileConfig p, ProfileKind kind)
        {
            if (sym == null || p == null) return;

            var names = sym.Parameters
                .Cast<Parameter>()
                .Select(x => x.Definition?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            string Pick(params string[] keys)
            {
                foreach (var k in keys)
                {
                    var hit = names.FirstOrDefault(n => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!string.IsNullOrWhiteSpace(hit))
                        return hit;
                }
                return "";
            }

            p.ParamDepth = string.IsNullOrWhiteSpace(p.ParamDepth) ? Pick("prof", "depth", "épais", "epais") : p.ParamDepth;

            if (kind == ProfileKind.WallCirc || kind == ProfileKind.FloorCirc)
            {
                p.ParamDiameter = string.IsNullOrWhiteSpace(p.ParamDiameter) ? Pick("diam", "ø") : p.ParamDiameter;
                return;
            }

            if (string.IsNullOrWhiteSpace(p.ParamLength))
            {
                p.ParamLength = Pick("long", "length");
                if (string.IsNullOrWhiteSpace(p.ParamLength))
                    p.ParamLength = Pick("larg", "width");
            }

            if (kind == ProfileKind.WallRect)
            {
                p.ParamHeight = string.IsNullOrWhiteSpace(p.ParamHeight) ? Pick("haut", "height") : p.ParamHeight;
            }
            else
            {
                p.ParamWidth = string.IsNullOrWhiteSpace(p.ParamWidth) ? Pick("larg", "width") : p.ParamWidth;
                if (string.IsNullOrWhiteSpace(p.ParamWidth))
                    p.ParamWidth = Pick("long", "length");
            }
        }

        // =========================
        // PROFILE / SYMBOL
        // =========================
        private static ProfileConfig GetProfileForRun(ReservationAutoV3Config cfg,
            ReservationAutoV3Window.HostTarget host, ReservationAutoV3Window.ShapeTarget shape)
        {
            if (cfg == null) return null;

            return (host, shape) switch
            {
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Rectangulaire) => cfg.WallRect,
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Circulaire) => cfg.WallCirc,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Rectangulaire) => cfg.FloorRect,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Circulaire) => cfg.FloorCirc,
                _ => null
            };
        }

        private static bool TryResolveSymbol(Document doc, ProfileConfig prof, out FamilySymbol symbol)
        {
            symbol = null;
            if (doc == null || prof == null) return false;
            if (string.IsNullOrWhiteSpace(prof.FamilyName)) return false;

            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s?.Family?.Name != null)
                .Where(s => string.Equals(s.Family.Name, prof.FamilyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!candidates.Any())
                return false;

            if (!string.IsNullOrWhiteSpace(prof.TypeName))
            {
                symbol = candidates.FirstOrDefault(s =>
                    string.Equals(s.Name, prof.TypeName, StringComparison.OrdinalIgnoreCase));
            }

            symbol ??= candidates.FirstOrDefault();
            return symbol != null;
        }

        // =========================
        // MANUAL
        // =========================
        private void RunManual(UIDocument uiDoc, Document doc,
            ReservationAutoV3Window win,
            ReservationAutoV3Config cfg,
            ProfileConfig prof,
            FamilySymbol reservationSymbol)
        {
            bool isWall = win.SelectedHost == ReservationAutoV3Window.HostTarget.Mur;
            bool isRect = win.SelectedShape == ReservationAutoV3Window.ShapeTarget.Rectangulaire;

            TaskDialog.Show("BIMaestro",
                $"Mode manuel : sélectionne l’objet, puis le {(isWall ? "mur" : "sol")}.\nECHAP pour arrêter.");

            while (true)
            {
                List<(Element el, Transform tr)> picked;
                try
                {
                    picked = PickElements(uiDoc, doc, win, multi: win.MultiEnabled && isRect);
                }
                catch
                {
                    break;
                }

                if (picked == null || picked.Count == 0)
                    break;

                Element host;
                try
                {
                    var rHost = uiDoc.Selection.PickObject(ObjectType.Element,
                        isWall ? "Sélectionne le mur (ESC pour annuler)" : "Sélectionne le sol (ESC pour annuler)");
                    host = doc.GetElement(rHost);
                }
                catch
                {
                    break;
                }

                if (isWall && host is not Wall)
                {
                    TaskDialog.Show("Erreur", "Ce n’est pas un mur.");
                    continue;
                }
                if (!isWall && host is not Floor)
                {
                    TaskDialog.Show("Erreur", "Ce n’est pas un sol.");
                    continue;
                }

                var level = doc.GetElement(host.LevelId) as Level
                            ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();

                if (picked.Count > 1 && isRect)
                {
                    CreateRectReservation_Multi(doc, host, level, reservationSymbol, win.SelectedObject, picked, cfg, prof, isWall, win.NormeEnabled);
                }
                else
                {
                    var (el, tr) = picked.First();
                    CreateReservation_Single(doc, host, level, reservationSymbol, win.SelectedObject, el, tr, cfg, prof, isWall, isRect, win.NormeEnabled);
                }

                var td = new TaskDialog("BIMaestro")
                {
                    MainInstruction = "Créer une autre réservation ?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };
                if (td.Show() != TaskDialogResult.Yes)
                    break;
            }
        }

        // =========================
        // AUTO (mur only)
        // =========================
        private void RunAutomatic(Document doc,
            ReservationAutoV3Window win,
            ReservationAutoV3Config cfg,
            ProfileConfig prof,
            FamilySymbol reservationSymbol)
        {
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .ToList();

            if (!walls.Any())
            {
                TaskDialog.Show("BIMaestro", "Aucun mur trouvé.");
                return;
            }

            List<Element> targets = win.SelectedObject switch
            {
                ReservationAutoV3Window.ObjectType.Canalisation => new FilteredElementCollector(doc)
                    .OfClass(typeof(Pipe))
                    .Cast<Element>()
                    .ToList(),

                ReservationAutoV3Window.ObjectType.Gaine => new FilteredElementCollector(doc)
                    .OfClass(typeof(Duct))
                    .Cast<Element>()
                    .ToList(),

                ReservationAutoV3Window.ObjectType.Porte => new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<Element>()
                    .ToList(),

                ReservationAutoV3Window.ObjectType.Fenetre => new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<Element>()
                    .ToList(),

                _ => new List<Element>()
            };

            if (!targets.Any())
            {
                TaskDialog.Show("BIMaestro", "Aucun objet trouvé.");
                return;
            }

            int created = 0;

            foreach (var wall in walls)
            {
                var bbWall = wall.get_BoundingBox(null);
                if (bbWall == null) continue;

                var level = doc.GetElement(wall.LevelId) as Level
                            ?? new FilteredElementCollector(doc)
                                .OfClass(typeof(Level))
                                .Cast<Level>()
                                .FirstOrDefault();

                foreach (var el in targets)
                {
                    var bbEl = el.get_BoundingBox(null);
                    if (bbEl == null) continue;

                    var bbInt = IntersectBoundingBoxes(bbWall, bbEl);
                    if (bbInt == null) continue;

                    XYZ center = (bbInt.Min + bbInt.Max) * 0.5;
                    center = ProjectPointOntoWallPlane(wall, center);

                    var fi = doc.Create.NewFamilyInstance(
                        center,
                        reservationSymbol,
                        wall,
                        level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    ApplySizing(
                        fi,
                        wall,
                        bbInt,
                        cfg,
                        prof,
                        win.SelectedObject,
                        el,
                        Transform.Identity,
                        win.SelectedShape == ReservationAutoV3Window.ShapeTarget.Rectangulaire,
                        win.NormeEnabled);

                    ApplyVerticalPlacementCorrection(
                        doc,
                        fi,
                        reservationSymbol,
                        wall,
                        prof,
                        bbInt,
                        cfg,
                        win.SelectedObject,
                        el,
                        win.SelectedShape == ReservationAutoV3Window.ShapeTarget.Rectangulaire,
                        win.NormeEnabled);

                    ForceVoidCutSafe(doc, wall, fi);
                    created++;
                }
            }

            TaskDialog.Show("BIMaestro", $"Réservations créées : {created}");
        }

        // =========================
        // Picking helpers
        // =========================
        private List<(Element el, Transform tr)> PickElements(UIDocument uiDoc, Document doc, ReservationAutoV3Window win, bool multi)
        {
            if (!multi)
            {
                Reference r;
                if (win.SelectedObject == ReservationAutoV3Window.ObjectType.Canalisation
                    || win.SelectedObject == ReservationAutoV3Window.ObjectType.Gaine)
                    r = PickSingleMepCurveBySource(uiDoc, doc, win.SelectedPipeSource, win.SelectedObject);
                else
                    r = uiDoc.Selection.PickObject(ObjectType.Element, "Sélectionne l’objet (ESC pour annuler)");

                if (!TryResolveReference(uiDoc, r, out var el, out var tr))
                    return new List<(Element, Transform)>();

                return new List<(Element, Transform)> { (el, tr) };
            }
            else
            {
                IList<Reference> refs = GetMepCurveReferencesBySource(uiDoc, doc, win.SelectedPipeSource, win.SelectedObject);

                var list = new List<(Element, Transform)>();
                foreach (var rr in refs)
                {
                    if (TryResolveReference(uiDoc, rr, out var el, out var tr))
                        list.Add((el, tr));
                }
                return list;
            }
        }

        // =========================
        // Create reservations
        // =========================
        private void CreateReservation_Single(Document doc,
            Element host, Level level, FamilySymbol sym,
            ReservationAutoV3Window.ObjectType objType,
            Element el, Transform trToHost,
            ReservationAutoV3Config cfg, ProfileConfig prof,
            bool isWall, bool isRect, bool normeEnabled)
        {
            var bbHost = host.get_BoundingBox(null);
            var bbEl = GetBoundingBoxInHostCoordinates(el, trToHost);
            if (bbHost == null || bbEl == null) return;

            var bbInt = IntersectBoundingBoxes(bbHost, bbEl);
            if (bbInt == null) return;

            XYZ center = (bbInt.Min + bbInt.Max) * 0.5;

            if (isWall && host is Wall w)
                center = ProjectPointOntoWallPlane(w, center);
            else if (!isWall && host is Floor f)
                center = GetPlacementPointOnFloor(f, center);

            var fi = doc.Create.NewFamilyInstance(
                center, sym, host, level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            AlignReservationOrientationIfNeeded(doc, fi, sym, host, center,
                isWall ? GetWallDirectionXY(host as Wall) : GetElementDirectionXY(el, trToHost));

            ApplySizing(fi, host, bbInt, cfg, prof, objType, el, trToHost, isRect, normeEnabled);

            ApplyVerticalPlacementCorrection(doc, fi, sym, host, prof, bbInt, cfg, objType, el, isRect, normeEnabled);

            ForceVoidCutSafe(doc, host, fi);
        }

        private void CreateRectReservation_Multi(Document doc,
            Element host, Level level, FamilySymbol sym,
            ReservationAutoV3Window.ObjectType objType,
            List<(Element el, Transform tr)> elems,
            ReservationAutoV3Config cfg, ProfileConfig prof,
            bool isWall, bool normeEnabled)
        {
            var bbHost = host.get_BoundingBox(null);
            if (bbHost == null) return;

            var clipped = elems
                .Select(t => GetBoundingBoxInHostCoordinates(t.el, t.tr))
                .Where(bb => bb != null)
                .Select(bb => IntersectBoundingBoxes(bbHost, bb))
                .Where(bb => bb != null)
                .ToList();

            if (!clipped.Any()) return;

            double minX = clipped.Min(bb => bb.Min.X);
            double minY = clipped.Min(bb => bb.Min.Y);
            double minZ = clipped.Min(bb => bb.Min.Z);
            double maxX = clipped.Max(bb => bb.Max.X);
            double maxY = clipped.Max(bb => bb.Max.Y);
            double maxZ = clipped.Max(bb => bb.Max.Z);

            var unionInt = new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };

            XYZ center = (unionInt.Min + unionInt.Max) * 0.5;

            if (isWall && host is Wall w)
                center = ProjectPointOntoWallPlane(w, center);
            else if (!isWall && host is Floor f)
                center = GetPlacementPointOnFloor(f, center);

            var fi = doc.Create.NewFamilyInstance(
                center, sym, host, level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            AlignReservationOrientationIfNeeded(doc, fi, sym, host, center,
               isWall
                   ? GetWallDirectionXY(host as Wall)
                   : GetPreferredFloorAxisXY(unionInt, elems));

            ApplySizing_MultiRect(fi, host, unionInt, cfg, prof, objType, normeEnabled);

            ApplyVerticalPlacementCorrection(doc, fi, sym, host, prof, unionInt, cfg, objType, null, true, normeEnabled);

            ForceVoidCutSafe(doc, host, fi);
        }

        // =========================
        // Sizing
        // =========================
        private void ApplySizing(FamilyInstance fi,
            Element host,
            BoundingBoxXYZ bbIntersect,
            ReservationAutoV3Config cfg, ProfileConfig prof,
            ReservationAutoV3Window.ObjectType objType,
            Element intersecting, Transform trToHost,
            bool isRect, bool normeEnabled)
        {
            if (fi == null || host == null || bbIntersect == null) return;

            bool isPipeOrDuct = objType == ReservationAutoV3Window.ObjectType.Canalisation
                                || objType == ReservationAutoV3Window.ObjectType.Gaine;

            double oversizeFt = MmToFt(isPipeOrDuct ? cfg.OversizeMm_PipeDuct : 0.0);
            double depthFt = GetHostDepth(host);

            var world = ToWorldBoundingBox(bbIntersect);
            if (world == null) return;

            if (!isRect)
            {
                double diamFt = CalculateDiameterForElement(intersecting, objType, oversizeFt);
                if (diamFt <= 1e-9)
                {
                    diamFt = CalculateFallbackDiameter(host, world, depthFt, oversizeFt);
                }

                if (normeEnabled) diamFt = RoundToNearest50mm(diamFt);

                TrySet(fi, prof.ParamDiameter, diamFt, "Diamètre", "COM_Diamètre", "Diameter");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
                return;
            }

            if (host is Wall wall)
            {
                XYZ wallDir = GetWallDirection(wall);

                var corners = new List<XYZ>
                {
                    new XYZ(world.Min.X, world.Min.Y, world.Min.Z),
                    new XYZ(world.Min.X, world.Max.Y, world.Min.Z),
                    new XYZ(world.Max.X, world.Min.Y, world.Min.Z),
                    new XYZ(world.Max.X, world.Max.Y, world.Min.Z)
                };
                var projs = corners.Select(c => c.DotProduct(wallDir)).ToList();

                double len = (projs.Max() - projs.Min()) + oversizeFt;
                double hgt = (world.Max.Z - world.Min.Z) + oversizeFt;

                if (normeEnabled)
                {
                    len = RoundToNearest50mm(len);
                    hgt = RoundToNearest50mm(hgt);
                }

                TrySet(fi, prof.ParamLength, len, "Longueur", "COM_Longueur", "Largeur", "COM_Largeur", "Length", "Width");
                TrySet(fi, prof.ParamHeight, hgt, "Hauteur", "COM_Hauteur", "Height");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
            }
            else if (host is Floor)
            {
                double len = (world.Max.X - world.Min.X) + oversizeFt;
                double wid = (world.Max.Y - world.Min.Y) + oversizeFt;

                if (normeEnabled)
                {
                    len = RoundToNearest50mm(len);
                    wid = RoundToNearest50mm(wid);
                }

                TrySet(fi, prof.ParamLength, len, "Longueur", "COM_Longueur", "Length");
                TrySet(fi, prof.ParamWidth, wid, "Largeur", "COM_Largeur", "Width");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
            }
        }

        private void ApplySizing_MultiRect(FamilyInstance fi,
            Element host,
            BoundingBoxXYZ unionIntersect,
            ReservationAutoV3Config cfg, ProfileConfig prof,
            ReservationAutoV3Window.ObjectType objType,
            bool normeEnabled)
        {
            if (fi == null || host == null || unionIntersect == null) return;

            bool isPipeOrDuct = objType == ReservationAutoV3Window.ObjectType.Canalisation
                                || objType == ReservationAutoV3Window.ObjectType.Gaine;

            double oversizeFt = MmToFt(isPipeOrDuct ? cfg.OversizeMm_PipeDuct : 0.0);
            double depthFt = GetHostDepth(host);

            var world = ToWorldBoundingBox(unionIntersect);
            if (world == null) return;

            if (host is Wall wall)
            {
                XYZ wallDir = GetWallDirection(wall);

                var corners = new List<XYZ>
                {
                    new XYZ(world.Min.X, world.Min.Y, world.Min.Z),
                    new XYZ(world.Min.X, world.Max.Y, world.Min.Z),
                    new XYZ(world.Max.X, world.Min.Y, world.Min.Z),
                    new XYZ(world.Max.X, world.Max.Y, world.Min.Z)
                };
                var projs = corners.Select(c => c.DotProduct(wallDir)).ToList();

                double len = (projs.Max() - projs.Min()) + oversizeFt;
                double hgt = (world.Max.Z - world.Min.Z) + oversizeFt;

                if (normeEnabled)
                {
                    len = RoundToNearest50mm(len);
                    hgt = RoundToNearest50mm(hgt);
                }

                TrySet(fi, prof.ParamLength, len, "Longueur", "COM_Longueur", "Largeur", "COM_Largeur", "Length", "Width");
                TrySet(fi, prof.ParamHeight, hgt, "Hauteur", "COM_Hauteur", "Height");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
            }
            else if (host is Floor)
            {
                double len = (world.Max.X - world.Min.X) + oversizeFt;
                double wid = (world.Max.Y - world.Min.Y) + oversizeFt;

                if (normeEnabled)
                {
                    len = RoundToNearest50mm(len);
                    wid = RoundToNearest50mm(wid);
                }

                TrySet(fi, prof.ParamLength, len, "Longueur", "COM_Longueur", "Length");
                TrySet(fi, prof.ParamWidth, wid, "Largeur", "COM_Largeur", "Width");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
            }
        }

        private static bool TrySet(FamilyInstance fi, string preferredName, double val, params string[] fallbacks)
        {
            bool Try(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                var p = fi.LookupParameter(name);
                if (p != null && !p.IsReadOnly)
                {
                    p.Set(val);
                    return true;
                }
                return false;
            }

            if (Try(preferredName)) return true;
            foreach (var fb in fallbacks ?? Array.Empty<string>())
                if (Try(fb)) return true;

            return false;
        }

        // =========================
        // CORRECTION DE POSITION VERTICALE
        // =========================
        private void ApplyVerticalPlacementCorrection(
    Document doc,
    FamilyInstance fi,
    FamilySymbol symbol,
    Element host,
    ProfileConfig prof,
    BoundingBoxXYZ bbIntersect,
    ReservationAutoV3Config cfg,
    ReservationAutoV3Window.ObjectType objType,
    Element intersecting,
    bool isRect,
    bool normeEnabled)
        {
            if (doc == null || fi == null || symbol == null || host == null || prof == null || bbIntersect == null)
                return;

            // ✅ IMPORTANT :
            // on n'applique la logique de placement vertical QUE sur les profils perso
            bool isPersoProfile = string.IsNullOrWhiteSpace(prof.TypeName)
                ? false
                : prof.FamilyName != null && (
                    prof.FamilyName.IndexOf("perso", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prof.TypeName.IndexOf("perso", StringComparison.OrdinalIgnoreCase) >= 0);

            // En pratique le plus fiable n'est pas le nom "perso" mais le fait que le profil
            // vienne d'une config utilisateur. Comme ici on ne reçoit pas directement cette info,
            // on protège au moins les V1/V2 connues.
            bool isBuiltInKnown =
                FamilyNameContains(prof.FamilyName, "CML_Réservation rectangulaire verticale") ||
                FamilyNameContains(prof.FamilyName, "CML_Réservation rectangulaire horizontale") ||
                FamilyNameContains(prof.FamilyName, "CML_Réservation circulaire verticale") ||
                FamilyNameContains(prof.FamilyName, "CML_Réservation circulaire horizontale") ||
                FamilyNameContains(prof.FamilyName, "Réservation rectangulaire murale") ||
                FamilyNameContains(prof.FamilyName, "Réservation rectangulaire sol") ||
                FamilyNameContains(prof.FamilyName, "Réservation circulaire murale") ||
                FamilyNameContains(prof.FamilyName, "Réservation circulaire sol");

            if (isBuiltInKnown)
                return;

            VerticalPlacementMode mode = ResolveVerticalPlacementMode(symbol, prof);

            // si on est sur Auto sans décalage manuel et qu'on ne veut pas impacter les cas standards,
            // on peut sortir si l'auto conclut "Center"
            double verticalSizeFt = ComputeReferenceVerticalSize(host, bbIntersect, cfg, objType, intersecting, isRect, normeEnabled);
            double shiftFt = MmToFt(prof.VerticalPlacementOffsetMm);

            switch (mode)
            {
                // ✅ INVERSION CORRIGÉE PAR RAPPORT À MA VERSION PRÉCÉDENTE
                // Si la référence de famille est en bas, il faut monter la famille
                // et ton test montre que c'est actuellement "Haut" qui donne le bon résultat.
                case VerticalPlacementMode.Top:
                    shiftFt += verticalSizeFt * 0.5;
                    break;

                // Si la référence est en haut, il faut descendre la famille
                case VerticalPlacementMode.Bottom:
                    shiftFt -= verticalSizeFt * 0.5;
                    break;

                case VerticalPlacementMode.Center:
                case VerticalPlacementMode.Auto:
                default:
                    break;
            }

            if (Math.Abs(shiftFt) < 1e-9)
                return;

            ElementTransformUtils.MoveElement(doc, fi.Id, XYZ.BasisZ * shiftFt);
        }

        private VerticalPlacementMode ResolveVerticalPlacementMode(FamilySymbol symbol, ProfileConfig prof)
        {
            if (prof == null)
                return VerticalPlacementMode.Center;

            if (prof.VerticalPlacementMode != VerticalPlacementMode.Auto)
                return prof.VerticalPlacementMode;

            return DetectVerticalPlacementModeFromSymbol(symbol);
        }

        private VerticalPlacementMode DetectVerticalPlacementModeFromSymbol(FamilySymbol symbol)
{
    try
    {
        if (symbol == null)
            return VerticalPlacementMode.Center;

        var opts = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };

        GeometryElement geo = symbol.get_Geometry(opts);
        if (geo == null)
            return VerticalPlacementMode.Center;

        bool found = false;
        double minZ = double.MaxValue;
        double maxZ = double.MinValue;

        void Accumulate(GeometryElement g)
        {
            if (g == null) return;

            foreach (GeometryObject obj in g)
            {
                if (obj is GeometryInstance gi)
                {
                    Accumulate(gi.GetInstanceGeometry());
                    continue;
                }

                BoundingBoxXYZ bb = GetGeometryBoundingBox(obj);
                if (bb == null) continue;

                Transform t = bb.Transform ?? Transform.Identity;

                var corners = new[]
                {
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                    new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                    new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                    new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                    new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
                }.Select(p => t.OfPoint(p));

                foreach (var p in corners)
                {
                    found = true;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.Z > maxZ) maxZ = p.Z;
                }
            }
        }

        Accumulate(geo);

        if (!found)
            return VerticalPlacementMode.Center;

        double centerZ = (minZ + maxZ) * 0.5;
        double height = maxZ - minZ;
        if (height < 1e-9)
            return VerticalPlacementMode.Center;

        double tolerance = height * 0.15;

        if (Math.Abs(centerZ) <= tolerance)
            return VerticalPlacementMode.Center;

        if (minZ >= -tolerance && maxZ > tolerance)
            return VerticalPlacementMode.Bottom;

        if (maxZ <= tolerance && minZ < -tolerance)
            return VerticalPlacementMode.Top;

        if (Math.Abs(minZ) < Math.Abs(maxZ))
            return VerticalPlacementMode.Bottom;

        if (Math.Abs(maxZ) < Math.Abs(minZ))
            return VerticalPlacementMode.Top;

        return VerticalPlacementMode.Center;
    }
    catch
    {
        return VerticalPlacementMode.Center;
    }
}

        private BoundingBoxXYZ GetGeometryBoundingBox(GeometryObject obj)
        {
            throw new NotImplementedException();
        }

        private double ComputeReferenceVerticalSize(
            Element host,
            BoundingBoxXYZ bbIntersect,
            ReservationAutoV3Config cfg,
            ReservationAutoV3Window.ObjectType objType,
            Element intersecting,
            bool isRect,
            bool normeEnabled)
        {
            if (host == null || bbIntersect == null)
                return 0.0;

            double depthFt = GetHostDepth(host);

            if (host is Floor)
                return depthFt;

            bool isPipeOrDuct = objType == ReservationAutoV3Window.ObjectType.Canalisation
                                || objType == ReservationAutoV3Window.ObjectType.Gaine;

            double oversizeFt = MmToFt(isPipeOrDuct ? cfg.OversizeMm_PipeDuct : 0.0);
            var world = ToWorldBoundingBox(bbIntersect);
            if (world == null)
                return 0.0;

            if (!isRect)
            {
                double diamFt = CalculateDiameterForElement(intersecting, objType, oversizeFt);
                if (diamFt <= 1e-9)
                    diamFt = CalculateFallbackDiameter(host, world, depthFt, oversizeFt);

                if (normeEnabled)
                    diamFt = RoundToNearest50mm(diamFt);

                return diamFt;
            }

            double hgt = (world.Max.Z - world.Min.Z) + oversizeFt;
            if (normeEnabled)
                hgt = RoundToNearest50mm(hgt);

            return hgt;
        }

        // =========================
        // VOID CUT FORCE
        // =========================
        private void ForceVoidCutSafe(Document doc, Element host, FamilyInstance fi)
        {
            if (doc == null || host == null || fi == null) return;

            if (TryForceVoidCut(doc, host, fi))
                return;

            try
            {
                doc.Regenerate();
            }
            catch { }

            if (TryForceVoidCut(doc, host, fi))
                return;

            if (!_voidCutWarnShown)
            {
                _voidCutWarnShown = true;
                TaskDialog.Show("BIMaestro",
                    "Info découpe (familles 'vide') :\n" +
                    "Certaines familles ne coupent pas si l’option famille n’est pas activée.\n\n" +
                    "Vérifie dans l’éditeur de famille :\n" +
                    "- 'Cut with Voids When Loaded'\n" +
                    "- le vide est bien en 'Cut Geometry'\n" +
                    "- la catégorie/support autorise la coupe.\n\n" +
                    "Le plugin a tenté de forcer la coupe automatiquement.");
            }
        }

        private static bool TryForceVoidCut(Document doc, Element host, FamilyInstance fi)
        {
            try
            {
                var asm = typeof(Element).Assembly;
                var t = asm.GetType("Autodesk.Revit.DB.InstanceVoidCutUtils");
                if (t == null) return false;

                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "AddInstanceVoidCut")
                    .ToList();

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 3) continue;

                    bool ok0 = ps[0].ParameterType == typeof(Document);
                    bool ok1 = typeof(Element).IsAssignableFrom(ps[1].ParameterType);
                    bool ok2 = typeof(Element).IsAssignableFrom(ps[2].ParameterType);

                    if (!ok0 || !ok1 || !ok2) continue;

                    m.Invoke(null, new object[] { doc, host, fi });
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        // =========================
        // Host depth
        // =========================
        private static double GetHostDepth(Element host)
        {
            if (host is Wall w) return w.Width;

            if (host is Floor f)
            {
                var p = f.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
                if (p != null && p.StorageType == StorageType.Double)
                    return p.AsDouble();

                var bb = f.get_BoundingBox(null);
                if (bb != null)
                    return Math.Abs(bb.Max.Z - bb.Min.Z);
            }

            return 0.0;
        }

        // =========================
        // Geometry helpers
        // =========================
        private static double MmToFt(double mm) => mm / 304.8;

        private static BoundingBoxXYZ IntersectBoundingBoxes(BoundingBoxXYZ bb1, BoundingBoxXYZ bb2)
        {
            var w1 = ToWorldBoundingBox(bb1);
            var w2 = ToWorldBoundingBox(bb2);
            if (w1 == null || w2 == null) return null;

            double minX = Math.Max(w1.Min.X, w2.Min.X);
            double maxX = Math.Min(w1.Max.X, w2.Max.X);
            if (minX > maxX) return null;

            double minY = Math.Max(w1.Min.Y, w2.Min.Y);
            double maxY = Math.Min(w1.Max.Y, w2.Max.Y);
            if (minY > maxY) return null;

            double minZ = Math.Max(w1.Min.Z, w2.Min.Z);
            double maxZ = Math.Min(w1.Max.Z, w2.Max.Z);
            if (minZ > maxZ) return null;

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        private static BoundingBoxXYZ ToWorldBoundingBox(BoundingBoxXYZ bb)
        {
            if (bb == null) return null;

            Transform t = bb.Transform ?? Transform.Identity;
            var corners = new List<XYZ>
            {
                new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
            }.Select(p => t.OfPoint(p)).ToList();

            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);
            double minZ = corners.Min(p => p.Z);
            double maxX = corners.Max(p => p.X);
            double maxY = corners.Max(p => p.Y);
            double maxZ = corners.Max(p => p.Z);

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        private static BoundingBoxXYZ GetBoundingBoxInHostCoordinates(Element elem, Transform transformToHost)
        {
            if (elem == null) return null;

            var bb = elem.get_BoundingBox(null);
            if (bb == null) return null;

            if (transformToHost == null || transformToHost.IsIdentity)
                return bb;

            var corners = new List<XYZ>
            {
                new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
            }.Select(p => transformToHost.OfPoint(p)).ToList();

            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);
            double minZ = corners.Min(p => p.Z);
            double maxX = corners.Max(p => p.X);
            double maxY = corners.Max(p => p.Y);
            double maxZ = corners.Max(p => p.Z);

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        private static XYZ GetWallDirection(Wall wall)
        {
            if (wall?.Location is LocationCurve lc && lc.Curve is Line line)
            {
                var d = line.Direction;
                if (d != null && d.GetLength() > 1e-9)
                    return d.Normalize();
            }
            return XYZ.BasisX;
        }

        private static XYZ GetWallDirectionXY(Wall wall)
        {
            if (wall?.Location is LocationCurve lc && lc.Curve is Line line)
            {
                XYZ d = line.Direction;
                d = new XYZ(d.X, d.Y, 0.0);
                if (!d.IsZeroLength()) return d.Normalize();
            }

            XYZ n = wall?.Orientation ?? XYZ.BasisY;
            XYZ dir = n.CrossProduct(XYZ.BasisZ);
            dir = new XYZ(dir.X, dir.Y, 0.0);
            if (!dir.IsZeroLength()) return dir.Normalize();

            return XYZ.BasisX;
        }

        private static XYZ GetElementDirectionXY(Element e, Transform trToHost)
        {
            if (e == null) return null;

            XYZ d = null;
            if (e.Location is LocationCurve lc && lc.Curve is Line ln)
                d = ln.Direction;
            else if (e is FamilyInstance fi && fi.HandOrientation != null && !fi.HandOrientation.IsZeroLength())
                d = fi.HandOrientation;

            if (d == null || d.IsZeroLength()) return null;

            if (trToHost != null && !trToHost.IsIdentity)
                d = trToHost.OfVector(d);

            d = new XYZ(d.X, d.Y, 0.0);
            if (d.IsZeroLength()) return null;
            return d.Normalize();
        }

        private static XYZ GetAverageDirectionXY(IEnumerable<Element> elements, Dictionary<ElementId, Transform> transformMap)
        {
            if (elements == null) return null;

            List<XYZ> dirs = new List<XYZ>();
            foreach (var e in elements)
            {
                Transform tr = Transform.Identity;
                if (e != null && transformMap != null && transformMap.TryGetValue(e.Id, out var found) && found != null)
                    tr = found;

                XYZ d = GetElementDirectionXY(e, tr);
                if (d != null && !d.IsZeroLength())
                    dirs.Add(d.Normalize());
            }

            if (dirs.Count == 0) return null;

            XYZ refDir = dirs[0];
            XYZ sum = XYZ.Zero;
            foreach (var d in dirs)
            {
                XYZ dd = d;
                if (dd.DotProduct(refDir) < 0) dd = dd.Negate();
                sum += dd;
            }

            if (sum.IsZeroLength()) return refDir;
            return sum.Normalize();
        }

        private static XYZ GetPreferredFloorAxisXY(BoundingBoxXYZ unionIntersect, List<(Element el, Transform tr)> elems)
        {
            if (unionIntersect != null)
            {
                double dx = Math.Abs(unionIntersect.Max.X - unionIntersect.Min.X);
                double dy = Math.Abs(unionIntersect.Max.Y - unionIntersect.Min.Y);

                if (Math.Abs(dx - dy) > 1e-6)
                    return dx >= dy ? XYZ.BasisX : XYZ.BasisY;
            }

            if (elems != null && elems.Count > 0)
                return GetAverageDirectionXY(elems.Select(x => x.el), elems.ToDictionary(x => x.el.Id, x => x.tr));

            return XYZ.BasisX;
        }

        private static void AlignReservationOrientationIfNeeded(Document doc, FamilyInstance inst, FamilySymbol symbol, Element host, XYZ origin, XYZ axisX)
        {
            if (doc == null || inst == null || symbol == null || host == null || origin == null || axisX == null) return;

            if (IsOpeningFamily(symbol))
                return;

            AlignInstanceXToAxisXY(doc, inst, origin, axisX);
        }

        private static bool IsOpeningFamily(FamilySymbol symbol)
        {
            string famName = symbol?.Family?.Name ?? string.Empty;
            string typeName = symbol?.Name ?? string.Empty;

            bool hasOpeningKeyword = famName.IndexOf("ouverture", StringComparison.OrdinalIgnoreCase) >= 0
                                     || famName.IndexOf("opening", StringComparison.OrdinalIgnoreCase) >= 0
                                     || typeName.IndexOf("ouverture", StringComparison.OrdinalIgnoreCase) >= 0
                                     || typeName.IndexOf("opening", StringComparison.OrdinalIgnoreCase) >= 0;

            return hasOpeningKeyword;
        }

        private static void AlignInstanceXToAxisXY(Document doc, FamilyInstance inst, XYZ origin, XYZ axisX)
        {
            if (doc == null || inst == null || axisX == null || axisX.IsZeroLength()) return;

            XYZ desired = new XYZ(axisX.X, axisX.Y, 0.0);
            if (desired.IsZeroLength()) return;
            desired = desired.Normalize();

            XYZ current = inst.HandOrientation;
            current = new XYZ(current.X, current.Y, 0.0);
            if (current.IsZeroLength()) return;
            current = current.Normalize();

            double dot = Math.Max(-1.0, Math.Min(1.0, current.DotProduct(desired)));
            double angle = Math.Acos(dot);

            double crossZ = current.X * desired.Y - current.Y * desired.X;
            if (crossZ < 0) angle = -angle;

            if (Math.Abs(angle) < 1e-6) return;

            Line axis = Line.CreateUnbound(origin, XYZ.BasisZ);
            ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle);
        }

        private static XYZ ProjectPointOntoWallPlane(Wall wall, XYZ point)
        {
            if (wall == null || point == null) return point;

            XYZ normal = wall.Orientation;
            if (normal == null || normal.GetLength() < 1e-9) return point;
            normal = normal.Normalize();

            XYZ origin = null;
            if (wall.Location is LocationCurve lc && lc.Curve != null)
                origin = lc.Curve.Evaluate(0.5, true);

            origin ??= point;
            double offset = normal.DotProduct(point - origin);

            return point - normal * offset;
        }

        private static XYZ GetPlacementPointOnFloor(Floor floor, XYZ fallbackCenter)
        {
            if (floor == null) return fallbackCenter;

            var bb = floor.get_BoundingBox(null);
            if (bb == null) return fallbackCenter;

            double z = (bb.Min.Z + bb.Max.Z) * 0.5;
            return new XYZ(fallbackCenter.X, fallbackCenter.Y, z);
        }

        // =========================
        // Diameter & rounding
        // =========================
        private static double CalculateDiameterForElement(Element elem, ReservationAutoV3Window.ObjectType objType, double oversizeFt)
        {
            if (objType == ReservationAutoV3Window.ObjectType.Canalisation && elem is Pipe p)
            {
                double d = p.LookupParameter("Diamètre")?.AsDouble() ?? 0.0;
                double iso = p.LookupParameter("Epaisseur d'isolation")?.AsDouble() ?? 0.0;
                return d + 2 * iso + oversizeFt;
            }

            if (objType == ReservationAutoV3Window.ObjectType.Gaine && elem is Duct dct)
            {
                double d = dct.LookupParameter("Diamètre")?.AsDouble() ?? 0.0;
                double iso = dct.LookupParameter("Epaisseur d'isolation")?.AsDouble() ?? 0.0;
                if (d > 1e-9)
                    return d + 2 * iso + oversizeFt;

                double w = dct.LookupParameter("Largeur")?.AsDouble()
                           ?? dct.LookupParameter("Width")?.AsDouble()
                           ?? 0.0;
                double h = dct.LookupParameter("Hauteur")?.AsDouble()
                           ?? dct.LookupParameter("Height")?.AsDouble()
                           ?? 0.0;

                if (w > 1e-9 || h > 1e-9)
                    return Math.Max(w, h) + 2 * iso + oversizeFt;
            }

            if (objType == ReservationAutoV3Window.ObjectType.Autre && elem is FamilyInstance fi)
            {
                double d = fi.LookupParameter("Diamètre")?.AsDouble()
                           ?? fi.LookupParameter("Diameter")?.AsDouble()
                           ?? 0.0;
                if (d > 1e-9)
                    return d + oversizeFt;

                double w = fi.LookupParameter("Largeur")?.AsDouble()
                           ?? fi.LookupParameter("Width")?.AsDouble()
                           ?? 0.0;
                double h = fi.LookupParameter("Hauteur")?.AsDouble()
                           ?? fi.LookupParameter("Height")?.AsDouble()
                           ?? 0.0;

                if (w > 1e-9 || h > 1e-9)
                    return Math.Max(w, h) + oversizeFt;
            }

            return 0.0;
        }

        private static double CalculateFallbackDiameter(Element host, BoundingBoxXYZ world, double depthFt, double oversizeFt)
        {
            if (world == null) return oversizeFt;

            var dims = new[]
            {
                Math.Abs(world.Max.X - world.Min.X),
                Math.Abs(world.Max.Y - world.Min.Y),
                Math.Abs(world.Max.Z - world.Min.Z)
            };

            double diam = dims.Max();
            if (host is Floor || host is Wall)
            {
                var byDistanceToDepth = dims.OrderBy(d => Math.Abs(d - depthFt)).ToList();
                if (byDistanceToDepth.Count >= 3)
                    diam = Math.Max(byDistanceToDepth[1], byDistanceToDepth[2]);
            }

            return diam + oversizeFt;
        }

        private static double RoundToNearest50mm(double valueInFeet)
        {
            double mm = valueInFeet * 304.8;
            double mmRounded = Math.Ceiling(mm / 50.0) * 50.0;
            return mmRounded / 304.8;
        }

        // =========================
        // Dynamo
        // =========================
        private static void TryRunDynamo(ExternalCommandData commandData, string dynPath)
        {
            try
            {
                if (!File.Exists(dynPath)) return;

                DynamoRevit dynamoRevit = new DynamoRevit();
                DynamoRevitCommandData dynCmdData = new DynamoRevitCommandData(commandData);
                dynCmdData.JournalData = new Dictionary<string, string>
                {
                    { JournalKeys.ShowUiKey,         false.ToString() },
                    { JournalKeys.AutomationModeKey, false.ToString() },
                    { JournalKeys.DynPathKey,        dynPath },
                    { JournalKeys.DynPathExecuteKey, true.ToString()  },
                    { JournalKeys.ForceManualRunKey, true.ToString()  },
                    { JournalKeys.ModelShutDownKey,  true.ToString()  },
                    { JournalKeys.ModelNodesInfo,    false.ToString() }
                };

                dynamoRevit.ExecuteCommand(dynCmdData);
            }
            catch
            {
            }
        }

        // =========================
        // Link/host pipe selection
        // =========================
        private class HostMepCurveSelectionFilter : ISelectionFilter
        {
            private readonly ReservationAutoV3Window.ObjectType _objectType;

            public HostMepCurveSelectionFilter(ReservationAutoV3Window.ObjectType objectType)
            {
                _objectType = objectType;
            }

            public bool AllowElement(Element elem)
            {
                return _objectType switch
                {
                    ReservationAutoV3Window.ObjectType.Canalisation => elem is Pipe,
                    ReservationAutoV3Window.ObjectType.Gaine => elem is Duct,
                    _ => false
                };
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private class LinkPipeSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            private readonly ReservationAutoV3Window.PipeSource _pipeSource;
            private readonly ReservationAutoV3Window.ObjectType _objectType;

            public LinkPipeSelectionFilter(Document doc, ReservationAutoV3Window.PipeSource pipeSource, ReservationAutoV3Window.ObjectType objectType)
            {
                _doc = doc;
                _pipeSource = pipeSource;
                _objectType = objectType;
            }

            private static bool IsIfcLink(RevitLinkInstance linkInstance)
            {
                try
                {
                    var extRef = linkInstance.GetExternalFileReference();
                    if (extRef != null)
                    {
                        string kind = extRef.ExternalFileReferenceType.ToString();
                        if (string.Equals(kind, "IFC", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }

                var linkDoc = linkInstance.GetLinkDocument();
                string pathOrName = (linkDoc?.PathName ?? linkInstance.Name ?? string.Empty).ToLowerInvariant();
                return pathOrName.EndsWith(".ifc") || pathOrName.Contains(".ifc");
            }

            private static bool IsRvtLink(RevitLinkInstance linkInstance)
            {
                try
                {
                    var extRef = linkInstance.GetExternalFileReference();
                    if (extRef != null && extRef.ExternalFileReferenceType == ExternalFileReferenceType.RevitLink)
                        return true;
                }
                catch { }

                var linkDoc = linkInstance.GetLinkDocument();
                string pathOrName = (linkDoc?.PathName ?? linkInstance.Name ?? string.Empty).ToLowerInvariant();
                bool hasIfc = pathOrName.Contains(".ifc");
                bool hasRvt = pathOrName.EndsWith(".rvt");
                return hasRvt && !hasIfc;
            }

            private bool MatchesExpectedLinkType(RevitLinkInstance linkInstance)
            {
                return _pipeSource switch
                {
                    ReservationAutoV3Window.PipeSource.LienIFC => IsIfcLink(linkInstance),
                    ReservationAutoV3Window.PipeSource.LienRVT => IsRvtLink(linkInstance),
                    _ => false
                };
            }

            public bool AllowElement(Element elem)
            {
                var linkInstance = elem as RevitLinkInstance;
                if (linkInstance == null) return false;
                return linkInstance.GetLinkDocument() != null && MatchesExpectedLinkType(linkInstance);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference == null) return false;

                Element linkElem = _doc.GetElement(reference.ElementId);
                var linkInstance = linkElem as RevitLinkInstance;
                if (linkInstance == null) return false;

                Document linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null) return false;

                Element linkedElem = linkDoc.GetElement(reference.LinkedElementId);
                if (!MatchesExpectedLinkType(linkInstance) || linkedElem == null)
                    return false;

                return _objectType switch
                {
                    ReservationAutoV3Window.ObjectType.Canalisation => linkedElem is Pipe,
                    ReservationAutoV3Window.ObjectType.Gaine => linkedElem is Duct,
                    _ => false
                };
            }
        }

        private static Reference PickSingleMepCurveBySource(UIDocument uiDoc, Document doc,
            ReservationAutoV3Window.PipeSource pipeSource,
            ReservationAutoV3Window.ObjectType objectType)
        {
            var hostFilter = new HostMepCurveSelectionFilter(objectType);
            var linkFilter = new LinkPipeSelectionFilter(doc, pipeSource, objectType);

            string objectName = objectType == ReservationAutoV3Window.ObjectType.Gaine ? "gaine" : "canalisation";

            return pipeSource switch
            {
                ReservationAutoV3Window.PipeSource.Maquette => uiDoc.Selection.PickObject(
                    ObjectType.Element, hostFilter,
                    $"Sélectionne la {objectName} (maquette)"),

                ReservationAutoV3Window.PipeSource.LienIFC or ReservationAutoV3Window.PipeSource.LienRVT => uiDoc.Selection.PickObject(
                    ObjectType.LinkedElement, linkFilter,
                    $"Sélectionne la {objectName} (lien)"),

                _ => uiDoc.Selection.PickObject(ObjectType.Element, hostFilter, $"Sélectionne la {objectName}")
            };
        }

        private static IList<Reference> GetMepCurveReferencesBySource(UIDocument uiDoc, Document doc,
            ReservationAutoV3Window.PipeSource pipeSource,
            ReservationAutoV3Window.ObjectType objectType)
        {
            var hostFilter = new HostMepCurveSelectionFilter(objectType);
            var linkFilter = new LinkPipeSelectionFilter(doc, pipeSource, objectType);
            string objectLabelPlural = objectType == ReservationAutoV3Window.ObjectType.Gaine ? "gaines" : "canalisations";

            return pipeSource switch
            {
                ReservationAutoV3Window.PipeSource.Maquette => uiDoc.Selection.PickObjects(
                    ObjectType.Element, hostFilter,
                    $"Sélectionne les {objectLabelPlural} (CTRL + clic, ESC pour terminer)"),

                ReservationAutoV3Window.PipeSource.LienIFC or ReservationAutoV3Window.PipeSource.LienRVT => uiDoc.Selection.PickObjects(
                    ObjectType.LinkedElement, linkFilter,
                    $"Sélectionne les {objectLabelPlural} (lien) (CTRL + clic, ESC pour terminer)"),

                _ => null
            };
        }

        private static bool TryResolveReference(UIDocument uiDoc, Reference reference, out Element element, out Transform transformToHost)
        {
            element = null;
            transformToHost = Transform.Identity;

            if (reference == null)
                return false;

            if (reference.LinkedElementId != ElementId.InvalidElementId)
            {
                var linkInstance = uiDoc.Document.GetElement(reference.ElementId) as RevitLinkInstance;
                if (linkInstance == null)
                    return false;

                Document linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null)
                    return false;

                element = linkDoc.GetElement(reference.LinkedElementId);
                transformToHost = linkInstance.GetTotalTransform();
                return element != null;
            }

            element = uiDoc.Document.GetElement(reference);
            transformToHost = Transform.Identity;
            return element != null;
        }
    }
}
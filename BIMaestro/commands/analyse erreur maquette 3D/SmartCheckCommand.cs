using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Licensing;


namespace Analyse
{
    [Transaction(TransactionMode.ReadOnly)]
    public class SmartCheckCommand : BaseTrackedCommand
    {
        public static readonly string Smart3DName = "BIMastro – SmartCheck 3D";
        protected override string ButtonId => "SmartCheckCommand";


        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            var issues = new List<ModelIssue>();

            try { issues.AddRange(FindFloatingWallsWithContext(doc, tolMm: 20, embedMm: 30)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", $"Scan murs : {ex.Message}"); }

            try { issues.AddRange(FindMepThroughWallsWithoutReservation(doc, volTolMm3: 1500)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", $"Scan traversées : {ex.Message}"); }

            try { issues.AddRange(FindUnconnectedMEP(doc)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", $"Scan raccords ouverts : {ex.Message}"); }

            var handler = new SmartExternalHandler(uiapp);
            var extEvent = ExternalEvent.Create(handler);

            var win = new SmartCheckWindow(issues, extEvent, handler);
            win.Show();
            return Result.Succeeded;
        }

        // ---------- Détection : murs flottants/superposés/noyés ----------
        private static IEnumerable<ModelIssue> FindFloatingWallsWithContext(Document doc, double tolMm, double embedMm)
        {
            double tolFt = MmToFeet(tolMm);
            double embedFt = MmToFeet(embedMm);

            var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
                .Select(f => new { F = f, BB = f.get_BoundingBox(null) })
                .Where(x => x.BB != null)
                .Select(x => new { x.F, x.BB, TopZ = x.BB.Max.Z })
                .ToList();

            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.get_BoundingBox(null) != null).ToList();

            foreach (var w in walls)
            {
                var wbb = w.get_BoundingBox(null);
                double baseZ = wbb.Min.Z;

                var underFloors = floors.Where(f => Overlap2D(wbb, f.BB)).ToList();
                bool hasSupport = underFloors.Any(f => Math.Abs(f.TopZ - baseZ) <= tolFt);

                // Mur sur mur
                if (!hasSupport)
                {
                    foreach (var other in walls)
                    {
                        if (other.Id == w.Id) continue;
                        var obb = other.get_BoundingBox(null);
                        if (!Overlap2D(wbb, obb)) continue;
                        if (Math.Abs(obb.Max.Z - baseZ) <= tolFt)
                        {
                            yield return new ModelIssue
                            {
                                ElementId = w.Id,
                                RelatedId = other.Id,
                                Kind = IssueKind.WallOnWall,
                                Category = "Murs superposés",
                                Message = $"Mur #{w.Id.IntegerValue} posé sur mur #{other.Id.IntegerValue} (±{tolMm}mm).",
                                BBox = wbb
                            };
                            goto NextWall;
                        }
                    }
                }

                // Mur noyé dans un sol
                if (!hasSupport && underFloors.Any())
                {
                    foreach (var f in underFloors)
                    {
                        if (f.TopZ - baseZ >= embedFt)
                        {
                            yield return new ModelIssue
                            {
                                ElementId = w.Id,
                                RelatedId = f.F.Id,
                                Kind = IssueKind.WallEmbeddedInFloor,
                                Category = "Mur noyé dans sol",
                                Message = $"Mur #{w.Id.IntegerValue} empiète de {FeetToMm(f.TopZ - baseZ):F0} mm dans plancher #{f.F.Id.IntegerValue}.",
                                BBox = wbb
                            };
                            goto NextWall;
                        }
                    }
                }

                // Flottant (aucun support)
                if (!hasSupport)
                {
                    yield return new ModelIssue
                    {
                        ElementId = w.Id,
                        Kind = IssueKind.WallFloating,
                        Category = "Murs flottants",
                        Message = $"Mur #{w.Id.IntegerValue} : base sans support à ±{tolMm}mm.",
                        BBox = wbb
                    };
                }

            NextWall:;
            }
        }

        // ---------- Traversées MEP ↔ murs sans réservation ----------
        private static IEnumerable<ModelIssue> FindMepThroughWallsWithoutReservation(Document doc, double volTolMm3)
        {
            double volTolFt3 = Mm3ToFt3(volTolMm3);

            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.get_BoundingBox(null) != null).ToList();

            var mep = new List<Element>();
            mep.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Pipe)).ToElements());
            mep.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Duct)).ToElements());
            mep.AddRange(new FilteredElementCollector(doc).OfClass(typeof(CableTray)).ToElements());
            mep.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Conduit)).ToElements());

            var openings = new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>()
                .Where(o => o.get_BoundingBox(null) != null).ToList();

            var sleeves = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi =>
                {
                    var fam = fi.Symbol?.Family?.Name ?? "";
                    var n = (fam + " " + fi.Name).ToLowerInvariant();
                    return n.Contains("reser") || n.Contains("réser") || n.Contains("sleeve")
                        || n.Contains("fourreau") || n.Contains("manchon") || n.Contains("trémie")
                        || n.Contains("opening");
                })
                .Where(fi => fi.get_BoundingBox(null) != null)
                .ToList();

            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = true, DetailLevel = ViewDetailLevel.Fine };

            foreach (var e in mep)
            {
                var ebb = e.get_BoundingBox(null);
                if (ebb == null) continue;
                var sMep = GetMainSolid(e, opt);
                if (sMep == null) continue;

                foreach (var w in walls)
                {
                    var wbb = w.get_BoundingBox(null);
                    if (!Overlap3D(ebb, wbb)) continue;

                    var sWall = GetMainSolid(w, opt);
                    if (sWall == null) continue;

                    Solid inter = null;
                    try { inter = BooleanOperationsUtils.ExecuteBooleanOperation(sMep, sWall, BooleanOperationsType.Intersect); }
                    catch { inter = null; }

                    if (inter == null || inter.Volume < volTolFt3) continue;

                    var ibb = inter.GetBoundingBox();
                    bool hasReservation = openings.Any(o => Overlap3D(ibb, o.get_BoundingBox(null)))
                                       || sleeves.Any(fi => Overlap3D(ibb, fi.get_BoundingBox(null)));

                    if (!hasReservation)
                    {
                        yield return new ModelIssue
                        {
                            ElementId = e.Id,       // MEP
                            RelatedId = w.Id,       // Mur
                            Kind = IssueKind.MepThroughWallNoSleeve,
                            Category = "Traversée sans réservation",
                            Message = $"{NiceType(e)} #{e.Id.IntegerValue} traverse Mur #{w.Id.IntegerValue} sans réservation détectée.",
                            BBox = ibb
                        };
                    }
                }
            }
        }

        // ---------- Connecteurs ouverts (MEPCurve) ----------
        private static IEnumerable<ModelIssue> FindUnconnectedMEP(Document doc)
        {
            IEnumerable<MEPCurve> curves = new FilteredElementCollector(doc)
                .OfClass(typeof(MEPCurve)).Cast<MEPCurve>();

            foreach (var c in curves)
            {
                ModelIssue candidate = null;
                try
                {
                    var conns = c.ConnectorManager?.Connectors;
                    if (conns == null) continue;

                    bool hasOpen = false;
                    foreach (Connector k in conns) { if (!k.IsConnected) { hasOpen = true; break; } }

                    if (hasOpen)
                    {
                        candidate = new ModelIssue
                        {
                            ElementId = c.Id,
                            Kind = IssueKind.MepUnconnected,
                            Category = "Raccords ouverts",
                            Message = $"{NiceType(c)} #{c.Id.IntegerValue} : au moins un connecteur est ouvert.",
                            BBox = c.get_BoundingBox(null)
                        };
                    }
                }
                catch { /* ignore */ }

                if (candidate != null) yield return candidate; // CS1626-safe
            }
        }

        // ---------- Utils ----------
        private static Solid GetMainSolid(Element e, Options opt)
        {
            var geo = e.get_Geometry(opt);
            if (geo == null) return null;
            Solid best = null;

            foreach (var obj in geo)
            {
                if (obj is Solid s && s.Volume > 1e-9) { if (best == null || s.Volume > best.Volume) best = s; }
                if (obj is GeometryInstance gi)
                {
                    var g2 = gi.GetInstanceGeometry();
                    foreach (var o2 in g2)
                        if (o2 is Solid s2 && s2.Volume > 1e-9) { if (best == null || s2.Volume > best.Volume) best = s2; }
                }
            }
            return best;
        }

        private static bool Overlap2D(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return false;
            bool x = a.Min.X <= b.Max.X && a.Max.X >= b.Min.X;
            bool y = a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;
            return x && y;
        }
        private static bool Overlap3D(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return false;
            bool x = a.Min.X <= b.Max.X && a.Max.X >= b.Min.X;
            bool y = a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;
            bool z = a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
            return x && y && z;
        }

        private static string NiceType(Element e) => e.Category?.Name ?? e.GetType().Name;
        private static double MmToFeet(double mm) => mm / 304.8;
        private static double FeetToMm(double ft) => ft * 304.8;
        private static double Mm3ToFt3(double mm3) => mm3 / Math.Pow(304.8, 3);


    }

    // ---------- Handler : focus robuste + ShowAll atomique ----------
    public class SmartExternalHandler : IExternalEventHandler
    {
        private readonly UIApplication _uiapp;

        public SmartAction Action { get; set; } = SmartAction.SelectOnly;
        public ElementId IssueId { get; set; } = ElementId.InvalidElementId;
        public ElementId RelatedId { get; set; } = ElementId.InvalidElementId;   // mur traversé
        public IssueKind CurrentKind { get; set; } = IssueKind.WallFloating;
        public BoundingBoxXYZ IssueBox { get; set; }
        public IList<ElementId> AllIssueIds { get; set; } = new List<ElementId>();
        public bool ShowAllMode { get; set; } = false;     // si ON, on ne reset pas les overrides au focus
        public bool ShowAllEnabled { get; set; } = false;  // état demandé par le toggle
        public bool AutoSectionBox { get; set; } = true;

        public SmartExternalHandler(UIApplication app) { _uiapp = app; }

        public void Execute(UIApplication app)
        {
            var uidoc = _uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            switch (Action)
            {
                case SmartAction.Ensure3D:
                    {
                        var v = EnsureSmart3D(doc);
                        uidoc.ActiveView = v;
                        break;
                    }

                case SmartAction.FocusIssue:
                    {
                        var v = EnsureSmart3D(doc);
                        uidoc.ActiveView = v;

                        BoundingBoxXYZ focusBox = null;
                        using (var t = new Transaction(doc, "BIMastro Focus"))
                        {
                            t.Start();
                            if (!ShowAllMode) ClearOverrides(uidoc, v);
                            focusBox = FocusIn3D(uidoc, v, IssueId, RelatedId, CurrentKind, IssueBox, AutoSectionBox);
                            doc.Regenerate();
                            t.Commit();
                        }
                        ZoomTo(uidoc, v, focusBox, IssueId);
                        break;
                    }

                case SmartAction.ShowAllApply:
                    {
                        var v = EnsureSmart3D(doc);
                        uidoc.ActiveView = v;

                        using (var t = new Transaction(doc, "BIMastro ShowAll APPLY"))
                        {
                            t.Start();
                            TryDisableSectionBox(v);    // vision d’ensemble
                            ClearOverrides(uidoc, v);   // reset complet d’abord (évite les résidus)
                            if (ShowAllEnabled)
                            {
                                ShowAllIssues(uidoc, v, AllIssueIds);
                            }
                            t.Commit();
                        }
                        break;
                    }

                case SmartAction.MarkIgnored:
                case SmartAction.SelectOnly:
                default:
                    {
                        if (IssueId != ElementId.InvalidElementId)
                        {
                            var el = doc.GetElement(IssueId);
                            if (el != null)
                            {
                                uidoc.Selection.SetElementIds(new List<ElementId> { el.Id });
                                var uiview = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
                                var bb = el.get_BoundingBox(uidoc.ActiveView) ?? el.get_BoundingBox(null);
                                if (uiview != null && bb != null) uiview.ZoomAndCenterRectangle(bb.Min, bb.Max);
                            }
                        }
                        break;
                    }
            }
        }

        public string GetName() => "BIMastro.SmartExternalHandler";

        private View3D EnsureSmart3D(Document doc)
        {
            var v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name.Equals(SmartCheckCommand.Smart3DName, StringComparison.OrdinalIgnoreCase));

            if (v == null)
            {
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                using (var t = new Transaction(doc, "Créer vue SmartCheck 3D"))
                {
                    t.Start();
                    v = View3D.CreateIsometric(doc, vft.Id);
                    v.Name = SmartCheckCommand.Smart3DName;
                    t.Commit();
                }
            }

            // Réglages de la vue : Détail Fine + Couleur uniforme (fallback Shaded)
            using (var t2 = new Transaction(doc, "Réglages SmartCheck 3D"))
            {
                t2.Start();
                try { v.DetailLevel = ViewDetailLevel.Fine; } catch { }
                try { v.DisplayStyle = (DisplayStyle)Enum.Parse(typeof(DisplayStyle), "FlatColors", true); }
                catch { try { v.DisplayStyle = DisplayStyle.Shading; } catch { } }
                t2.Commit();
            }

            return v;
        }

        private static void TryEnableSectionBox(View3D v) { try { v.IsSectionBoxActive = true; } catch { } }
        private static void TryDisableSectionBox(View3D v) { try { v.IsSectionBoxActive = false; } catch { } }

        /// <summary>
        /// Focus : si traversée sans réservation → emphase MEP + Mur, tout le reste à 85% dans la vue.
        /// Retourne la BBox de focus (union si MEP+mur).
        /// </summary>
        private BoundingBoxXYZ FocusIn3D(UIDocument uidoc, View3D v,
            ElementId id, ElementId related, IssueKind kind, BoundingBoxXYZ box, bool setSection)
        {
            var doc = uidoc.Document;

            var ids = new List<ElementId>();
            if (id != ElementId.InvalidElementId) ids.Add(id);
            if (related != ElementId.InvalidElementId) ids.Add(related);
            if (ids.Count > 0) uidoc.Selection.SetElementIds(ids);

            // Détermine la boîte de focus (union si possible)
            BoundingBoxXYZ focus = box;
            if (related != ElementId.InvalidElementId)
            {
                var rel = doc.GetElement(related);
                var rbb = rel?.get_BoundingBox(null);
                if (rbb != null && box != null) focus = Union(new[] { box, rbb });
                else if (rbb != null) focus = rbb;
            }
            if (focus == null && id != ElementId.InvalidElementId)
            {
                var el = doc.GetElement(id);
                focus = el?.get_BoundingBox(null);
            }

            // Section box
            if (setSection && focus != null)
            {
                var min = focus.Min; var max = focus.Max; var pad = (max - min) * 0.1;
                var b = new BoundingBoxXYZ { Min = min - pad, Max = max + pad };
                v.SetSectionBox(b);
                TryEnableSectionBox(v);
            }

            // Overrides
            var emphasize = new OverrideGraphicSettings();
            emphasize.SetProjectionLineColor(new Color(255, 0, 0));
#if REVIT2022_OR_LATER
            emphasize.SetProjectionLineWeight(8);
#endif
            emphasize.SetSurfaceTransparency(0);

            var fade = new OverrideGraphicSettings();
            fade.SetSurfaceTransparency(85);
            fade.SetHalftone(true);

            if (kind == IssueKind.MepThroughWallNoSleeve)
            {
                // MEP + Mur en clair
                foreach (var eid in ids.Distinct())
                    v.SetElementOverrides(eid, emphasize);

                // Le reste transparent (dans la vue)
                var allIds = new FilteredElementCollector(doc, v.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds();

                var keep = new HashSet<ElementId>(ids);
                foreach (var oid in allIds)
                    if (!keep.Contains(oid))
                        v.SetElementOverrides(oid, fade);
            }
            else
            {
                if (id != ElementId.InvalidElementId)
                    v.SetElementOverrides(id, emphasize);
            }

            return focus;
        }

        private void ZoomTo(UIDocument uidoc, View3D v, BoundingBoxXYZ focusBox, ElementId fallbackId)
        {
            var uiview = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == v.Id)
                      ?? uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == uidoc.ActiveView.Id);
            if (uiview == null) return;

            BoundingBoxXYZ bb = focusBox;
            if (bb == null && fallbackId != ElementId.InvalidElementId)
            {
                var el = uidoc.Document.GetElement(fallbackId);
                bb = el?.get_BoundingBox(v) ?? el?.get_BoundingBox(null);
            }
            if (bb == null) return;

            try { uiview.ZoomAndCenterRectangle(bb.Min, bb.Max); } catch { }
        }

        private void ShowAllIssues(UIDocument uidoc, View3D v, IList<ElementId> issueIds)
        {
            var doc = uidoc.Document;

            var err = new OverrideGraphicSettings();
            err.SetProjectionLineColor(new Color(255, 0, 0));
#if REVIT2022_OR_LATER
            err.SetProjectionLineWeight(8);
#endif
            err.SetSurfaceTransparency(0);
            foreach (var id in issueIds.Distinct())
                v.SetElementOverrides(id, err);

            var allIds = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().ToElementIds();
            var set = new HashSet<ElementId>(issueIds);
            var otherIds = allIds.Where(i => !set.Contains(i)).ToList();

            var fade = new OverrideGraphicSettings();
            fade.SetSurfaceTransparency(85);
            fade.SetHalftone(true);
            foreach (var oid in otherIds)
                v.SetElementOverrides(oid, fade);

            TryDisableSectionBox(v); // pas de section box en mode “Afficher tout”
        }

        private void ClearOverrides(UIDocument uidoc, View3D v)
        {
            var doc = uidoc.Document;
            var allIds = new FilteredElementCollector(doc, v.Id)
                .WhereElementIsNotElementType().ToElementIds();
            var neutral = new OverrideGraphicSettings();
            foreach (var id in allIds) v.SetElementOverrides(id, neutral);
        }

        private static BoundingBoxXYZ Union(IEnumerable<BoundingBoxXYZ> bbs)
        {
            BoundingBoxXYZ u = null;
            foreach (var bb in bbs)
            {
                if (u == null) u = new BoundingBoxXYZ { Min = bb.Min, Max = bb.Max };
                else
                {
                    u.Min = new XYZ(Math.Min(u.Min.X, bb.Min.X), Math.Min(u.Min.Y, bb.Min.Y), Math.Min(u.Min.Z, bb.Min.Z));
                    u.Max = new XYZ(Math.Max(u.Max.X, bb.Max.X), Math.Max(u.Max.Y, bb.Max.Y), Math.Max(u.Max.Z, bb.Max.Z));
                }
            }
            return u;
        }
    }
}

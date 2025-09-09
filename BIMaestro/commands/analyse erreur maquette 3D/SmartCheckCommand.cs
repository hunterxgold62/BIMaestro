using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    [Transaction(TransactionMode.ReadOnly)]
    public class SmartCheckCommand : BaseTrackedCommand
    {
        public static readonly string Smart3DName = "BIMastro – SmartCheck 3D";
        protected override string ButtonId => "BaseTrackedCommand";

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
                            BBox = ibb               // <-- BB de l'intersection (serrée)
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
}

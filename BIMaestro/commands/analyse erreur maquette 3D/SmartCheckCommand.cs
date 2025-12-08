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
        protected override string ButtonId => "SmartCheckCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            var issues = new List<ModelIssue>();

            try { issues.AddRange(FindFloatingWallsWithContext(doc, tolMm: 20, embedMm: 30)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", $"Scan murs : {ex.Message}"); }

            try { issues.AddRange(FindMepThroughWallsWithoutReservation(doc, volTolMm3: 1500)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", $"Scan traversées : {ex.Message}"); }

            try { issues.AddRange(FindLinkPipeClashes(doc, extraTolMm: 5)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", $"Scan liens : {ex.Message}"); }

            try { issues.AddRange(FindUnconnectedMEP(doc)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", $"Scan raccords ouverts : {ex.Message}"); }

            var docKey = SmartCheckState.GetDocKey(doc);
            SmartCheckState.RestoreIgnored(docKey, issues);

            var handler = new SmartExternalHandler(uiapp);
            var extEvent = ExternalEvent.Create(handler);

            var win = new SmartCheckWindow(issues, extEvent, handler, docKey);
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
        // ---------- Tuyaux (+ isolant) en collision avec les liens IFC/RVT ----------
        private static IEnumerable<ModelIssue> FindLinkPipeClashes(Document doc, double extraTolMm)
        {
            double tolFt = MmToFeet(extraTolMm);

            var pipes = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>()
                .Where(p => p.get_BoundingBox(null) != null)
                .ToList();
            if (!pipes.Any()) yield break;

            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = true, DetailLevel = ViewDetailLevel.Fine };

            var linkBBoxes = GetLinkSolidBBoxes(doc, opt).ToList();
            if (linkBBoxes.Count == 0) yield break;

            var seen = new HashSet<string>();

            foreach (var pipe in pipes)
            {
                var bb = pipe.get_BoundingBox(null);
                if (bb == null) continue;

                double iso = GetPipeInsulation(pipe);
                var padded = PadBoundingBox(bb, iso + tolFt);
                if (padded == null) continue;

                var paddedSolid = BoxToSolid(padded);
                var pipeSolid = GetMainSolid(pipe, opt);

                foreach (var link in linkBBoxes)
                {
                    if (!Overlap3D(padded, link.BBox)) continue;

                    bool intersects = false;
                    if (link.Solid != null && paddedSolid != null)
                    {
                        try
                        {
                            var interSolid = BooleanOperationsUtils.ExecuteBooleanOperation(
                                paddedSolid, link.Solid, BooleanOperationsType.Intersect);
                            intersects = interSolid != null && interSolid.Volume > 1e-9;
                        }
                        catch { intersects = false; }
                    }
                    else if (pipeSolid != null && link.Solid != null)
                    {
                        try
                        {
                            var interSolid = BooleanOperationsUtils.ExecuteBooleanOperation(
                                pipeSolid, link.Solid, BooleanOperationsType.Intersect);
                            intersects = interSolid != null && interSolid.Volume > 1e-9;
                        }
                        catch { intersects = false; }
                    }
                    else
                    {
                        intersects = Overlap3D(padded, link.BBox);
                    }

                    if (!intersects) continue;

                    var key = $"{pipe.Id.IntegerValue}:{link.LinkId.IntegerValue}:{link.LinkedElementId.IntegerValue}";
                    if (!seen.Add(key)) continue;

                    var inter = IntersectBox(padded, link.BBox);
                    yield return new ModelIssue
                    {
                        ElementId = pipe.Id,
                        RelatedId = link.LinkId,
                        Kind = IssueKind.LinkPipeClash,
                        Category = "Collisions tuyaux/liens",
                        Message = $"Tuyau #{pipe.Id.IntegerValue} en collision avec lien '{link.Name}' (Id {link.LinkId.IntegerValue})",
                        BBox = inter ?? padded
                    };
                }
            }
        }

        private class LinkSolidInfo
        {
            public ElementId LinkId { get; set; }
            public string Name { get; set; }
            public BoundingBoxXYZ BBox { get; set; }
            public Solid Solid { get; set; }
            public ElementId LinkedElementId { get; set; } = ElementId.InvalidElementId;
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
                            BBox = ibb               // BB de l'intersection (serrée)
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

                if (candidate != null) yield return candidate;
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
        private static BoundingBoxXYZ IntersectBox(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (!Overlap3D(a, b)) return null;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z)),
                Max = new XYZ(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z))
            };
        }

        private static BoundingBoxXYZ PadBoundingBox(BoundingBoxXYZ bb, double pad)
        {
            if (bb == null) return null;
            var p = new XYZ(pad, pad, pad);
            return new BoundingBoxXYZ { Min = bb.Min - p, Max = bb.Max + p };
        }

        private static Solid BoxToSolid(BoundingBoxXYZ bb)
        {
            if (bb == null) return null;
            var min = bb.Min; var max = bb.Max;
            var p0 = new XYZ(min.X, min.Y, min.Z);
            var p1 = new XYZ(max.X, min.Y, min.Z);
            var p2 = new XYZ(max.X, max.Y, min.Z);
            var p3 = new XYZ(min.X, max.Y, min.Z);

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));

            var height = max.Z - min.Z;
            if (height <= 0) return null;

            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, height);
        }

        private static double GetPipeInsulation(Pipe pipe)
        {
            if (pipe == null) return 0.0;

            double iso = 0.0;
            try { iso = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_INSULATION_THICKNESS)?.AsDouble() ?? 0.0; } catch { }

            if (iso <= 0)
            {
                var pIso = pipe.LookupParameter("Epaisseur d'isolation")
                           ?? pipe.LookupParameter("Epaisseur d’isolation")
                           ?? pipe.LookupParameter("Insulation Thickness");
                if (pIso != null && pIso.StorageType == StorageType.Double)
                    iso = pIso.AsDouble();
            }

            return Math.Max(0.0, iso);
        }

        private static IEnumerable<LinkSolidInfo> GetLinkSolidBBoxes(Document doc, Options opt)
        {
            var list = new List<LinkSolidInfo>();

            var revitLinks = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>();
            foreach (var link in revitLinks)
            {
                try
                {
                    var tr = link.GetTotalTransform() ?? Transform.Identity;
                    var geo = link.get_Geometry(opt);
                    CollectSolidBBoxes(geo, tr, link.Name, link.Id, ElementId.InvalidElementId, list);

                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc != null)
                    {
                        var linkedElems = new FilteredElementCollector(linkDoc)
                            .WhereElementIsNotElementType()
                            .Where(e => e.get_BoundingBox(null) != null);

                        foreach (var e in linkedElems)
                        {
                            var solid = GetMainSolid(e, opt);
                            if (solid == null) continue;

                            var tSolid = SolidUtils.CreateTransformed(solid, tr);
                            var tbb = tSolid?.GetBoundingBox();
                            if (tbb != null)
                            {
                                list.Add(new LinkSolidInfo
                                {
                                    LinkId = link.Id,
                                    Name = link.Name,
                                    BBox = tbb,
                                    Solid = tSolid,
                                    LinkedElementId = e.Id
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>();
            foreach (var imp in imports)
            {
                try
                {
                    var tr = imp.GetTransform() ?? Transform.Identity;
                    var geo = imp.get_Geometry(opt);
                    CollectSolidBBoxes(geo, tr, imp.Name, imp.Id, ElementId.InvalidElementId, list);
                }
                catch { }
            }

            return list;
        }

        private static void CollectSolidBBoxes(GeometryElement geo, Transform tr, string name, ElementId linkId, ElementId linkedElemId, IList<LinkSolidInfo> target)
        {
            if (geo == null || target == null) return;
            var current = tr ?? Transform.Identity;

            foreach (var obj in geo)
            {
                if (obj is Solid s && s.Volume > 1e-9)
                {
                    var bb = s.GetBoundingBox();
                    bb = TransformBox(bb, current);
                    var ts = SolidUtils.CreateTransformed(s, current);
                    if (bb != null && ts != null)
                    {
                        target.Add(new LinkSolidInfo
                        {
                            LinkId = linkId,
                            Name = name,
                            BBox = bb,
                            Solid = ts,
                            LinkedElementId = linkedElemId
                        });
                    }
                }
                else if (obj is GeometryInstance gi)
                {
                    var nested = current.Multiply(gi.Transform ?? Transform.Identity);
                    CollectSolidBBoxes(gi.GetInstanceGeometry(), nested, name, linkId, linkedElemId, target);
                }
            }
        }

        private static BoundingBoxXYZ TransformBox(BoundingBoxXYZ bb, Transform tr)
        {
            if (bb == null) return null;
            if (tr == null || tr.IsIdentity) return bb;

            var pts = new List<XYZ>
            {
                bb.Min,
                bb.Max,
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z)
            };

            var tPts = pts.Select(p => tr.OfPoint(p)).ToList();
            double minX = tPts.Min(p => p.X); double maxX = tPts.Max(p => p.X);
            double minY = tPts.Min(p => p.Y); double maxY = tPts.Max(p => p.Y);
            double minZ = tPts.Min(p => p.Z); double maxZ = tPts.Max(p => p.Z);

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }


        private static string NiceType(Element e) => e?.Category?.Name ?? e?.GetType().Name ?? "Élément";
        private static double MmToFeet(double mm) => mm / 304.8;
        private static double FeetToMm(double ft) => ft * 304.8;
        private static double Mm3ToFt3(double mm3) => mm3 / Math.Pow(304.8, 3);
    }
}

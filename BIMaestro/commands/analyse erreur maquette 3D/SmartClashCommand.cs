using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    [Transaction(TransactionMode.ReadOnly)]
    public class SmartClashCommand : BaseTrackedCommand
    {
        public static readonly string Smart3DName = "BIMaestro – SmartCheck 3D";
        protected override string ButtonId => "SmartClashCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            var issues = new List<ModelIssue>();

            try { issues.AddRange(FindFloatingWallsWithContext(doc, tolMm: 20, embedMm: 30)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", UiLanguage.T($"Scan murs : {ex.Message}", $"Wall scan: {ex.Message}")); }

            try { issues.AddRange(FindMepThroughWallsWithoutReservation(doc, volTolMm3: 1500)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", UiLanguage.T($"Scan traversées : {ex.Message}", $"Penetration scan: {ex.Message}")); }

            try { issues.AddRange(FindLinkPipeClashes(doc, extraTolMm: 5)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", UiLanguage.T($"Scan liens : {ex.Message}", $"Link scan: {ex.Message}")); }

            try { issues.AddRange(FindUnconnectedMEP(doc)); }
            catch (Exception ex) { TaskDialog.Show("Smart Check", UiLanguage.T($"Scan raccords ouverts : {ex.Message}", $"Open connector scan: {ex.Message}")); }

            var docKey = SmartCheckState.GetDocKey(doc);
            EnrichIssues(doc, issues);
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
                                Message = $"Mur #{w.Id.GetIdValue()} posé sur mur #{other.Id.GetIdValue()} (±{tolMm}mm).",
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
                                Message = $"Mur #{w.Id.GetIdValue()} empiète de {FeetToMm(f.TopZ - baseZ):F0} mm dans plancher #{f.F.Id.GetIdValue()}.",
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
                        Message = $"Mur #{w.Id.GetIdValue()} : base sans support à ±{tolMm}mm.",
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

            var aggregated = new Dictionary<string, (BoundingBoxXYZ Box, int Count, LinkSolidInfo Info, ElementId PipeId)>();

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

                    var inter = IntersectBox(padded, link.BBox);

                    var key = $"{pipe.Id.GetIdValue()}:{link.LinkId.GetIdValue()}";
                    if (aggregated.TryGetValue(key, out var agg))
                    {
                        aggregated[key] = (UnionBoxes(agg.Box, inter ?? padded), agg.Count + 1, agg.Info, agg.PipeId);
                    }
                    else
                    {
                        aggregated[key] = (inter ?? padded, 1, link, pipe.Id);
                    }
                }
            }

            foreach (var entry in aggregated.Values)
            {
                var info = entry.Info;
                var countSuffix = entry.Count > 1 ? $" ({entry.Count} éléments)" : string.Empty;

                yield return new ModelIssue
                {
                    ElementId = entry.PipeId,
                    RelatedId = info?.LinkId ?? ElementId.InvalidElementId,
                    Kind = IssueKind.LinkPipeClash,
                    Category = "Collisions tuyaux/liens",
                    Message = $"Tuyau #{entry.PipeId.GetIdValue()} en collision avec lien '{info?.Name}' (Id {(info?.LinkId.GetIdValue() ?? 0)}){countSuffix}",
                    LinkName = info?.Name,
                    BBox = entry.Box
                };
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
                            Message = $"{NiceType(e)} #{e.Id.GetIdValue()} traverse Mur #{w.Id.GetIdValue()} sans réservation détectée.",
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
                            Message = $"{NiceType(c)} #{c.Id.GetIdValue()} : au moins un connecteur est ouvert.",
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
                var meshBoxes = new Dictionary<string, LinkSolidInfo>();

                try
                {
                    var tr = link.GetTotalTransform() ?? Transform.Identity;
                    var geo = link.get_Geometry(opt);
                    CollectSolidBBoxes(geo, tr, link.Name, link.Id, ElementId.InvalidElementId, list, meshBoxes);

                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc != null)
                    {
                        var linkedElems = new FilteredElementCollector(linkDoc)
                            .WhereElementIsNotElementType()
                            .Where(e => e.get_BoundingBox(null) != null);

                        foreach (var e in linkedElems)
                        {
                            var solid = GetMainSolid(e, opt);
                            var ebb = e.get_BoundingBox(null);
                            var transformedBb = TransformBox(ebb, tr);

                            if (solid != null)
                            {
                                var tSolid = SolidUtils.CreateTransformed(solid, tr);
                                transformedBb ??= tSolid?.GetBoundingBox();

                                if (transformedBb != null)
                                {
                                    list.Add(new LinkSolidInfo
                                    {
                                        LinkId = link.Id,
                                        Name = link.Name,
                                        BBox = transformedBb,
                                        Solid = tSolid,
                                        LinkedElementId = e.Id
                                    });
                                }
                            }
                            else if (transformedBb != null)
                            {
                                list.Add(new LinkSolidInfo
                                {
                                    LinkId = link.Id,
                                    Name = link.Name,
                                    BBox = transformedBb,
                                    Solid = null,
                                    LinkedElementId = e.Id
                                });
                            }
                        }
                    }
                }
                catch { }

                // Ajout des BB fusionnées issues des maillages du lien
                foreach (var merged in meshBoxes.Values)
                {
                    list.Add(merged);
                }
            }

            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>();
            foreach (var imp in imports)
            {
                var meshBoxes = new Dictionary<string, LinkSolidInfo>();

                try
                {
                    var tr = imp.GetTransform() ?? Transform.Identity;
                    var geo = imp.get_Geometry(opt);
                    CollectSolidBBoxes(geo, tr, imp.Name, imp.Id, ElementId.InvalidElementId, list, meshBoxes);
                }
                catch { }

                foreach (var merged in meshBoxes.Values)
                {
                    list.Add(merged);
                }
            }

            return list;
        }

        private static void CollectSolidBBoxes(GeometryElement geo, Transform tr, string name, ElementId linkId, ElementId linkedElemId, IList<LinkSolidInfo> target, IDictionary<string, LinkSolidInfo> meshBoxes)
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
                else if (obj is Mesh m)
                {
                    var bb = MeshToBoundingBox(m);
                    bb = TransformBox(bb, current);
                    if (bb != null)
                    {
                        var key = $"{linkId.GetIdValue()}:{linkedElemId.GetIdValue()}";
                        if (meshBoxes != null && meshBoxes.TryGetValue(key, out var existing))
                        {
                            existing.BBox = UnionBoxes(existing.BBox, bb);
                        }
                        else if (meshBoxes != null)
                        {
                            var info = new LinkSolidInfo
                            {
                                LinkId = linkId,
                                Name = name,
                                BBox = bb,
                                Solid = null,
                                LinkedElementId = linkedElemId
                            };
                            meshBoxes[key] = info;
                        }
                    }
                }
                else if (obj is GeometryInstance gi)
                {
                    var nested = current.Multiply(gi.Transform ?? Transform.Identity);
                    CollectSolidBBoxes(gi.GetInstanceGeometry(), nested, name, linkId, linkedElemId, target, meshBoxes);
                }
            }
        }

        private static BoundingBoxXYZ MeshToBoundingBox(Mesh mesh)
        {
            if (mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0) return null;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (XYZ v in mesh.Vertices)
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }

            if (minX == double.MaxValue) return null;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
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

        private static BoundingBoxXYZ UnionBoxes(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null) return b;
            if (b == null) return a;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
                Max = new XYZ(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z))
            };
        }


        private static string NiceType(Element e) => e?.Category?.Name ?? e?.GetType().Name ?? "Élément";

        private static void EnrichIssues(Document doc, IList<ModelIssue> issues)
        {
            if (doc == null || issues == null) return;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            foreach (var issue in issues)
            {
                if (issue == null) continue;

                var element = issue.ElementId != null && issue.ElementId != ElementId.InvalidElementId
                    ? doc.GetElement(issue.ElementId)
                    : null;
                var related = issue.RelatedId != null && issue.RelatedId != ElementId.InvalidElementId
                    ? doc.GetElement(issue.RelatedId)
                    : null;

                issue.ElementCategory = element?.Category?.Name ?? issue.ElementCategory;
                issue.ElementTypeName = GetElementTypeLabel(doc, element) ?? issue.ElementTypeName;
                issue.LevelName = GetLevelName(doc, element, issue.BBox, levels) ?? issue.LevelName;

                if (string.IsNullOrWhiteSpace(issue.LinkName))
                {
                    if (related is RevitLinkInstance || related is ImportInstance)
                        issue.LinkName = CleanLinkName(related.Name);
                    else if (issue.Kind == IssueKind.LinkPipeClash)
                        issue.LinkName = "Lien externe";
                }
            }
        }

        private static string GetElementTypeLabel(Document doc, Element element)
        {
            if (doc == null || element == null) return null;

            if (element is FamilyInstance fi)
            {
                var family = fi.Symbol?.Family?.Name;
                var type = fi.Symbol?.Name;
                if (!string.IsNullOrWhiteSpace(family) && !string.IsNullOrWhiteSpace(type))
                    return family + " - " + type;
                if (!string.IsNullOrWhiteSpace(type)) return type;
                if (!string.IsNullOrWhiteSpace(family)) return family;
            }

            try
            {
                var typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var typeEl = doc.GetElement(typeId);
                    if (!string.IsNullOrWhiteSpace(typeEl?.Name))
                        return typeEl.Name;
                }
            }
            catch { }

            return !string.IsNullOrWhiteSpace(element.Name) ? element.Name : element.Category?.Name;
        }

        private static string GetLevelName(Document doc, Element element, BoundingBoxXYZ box, IList<Level> levels)
        {
            if (doc == null) return null;

            var level = GetLevelFromElementId(doc, element);
            if (level != null) return level.Name;

            level = GetLevelFromParameters(doc, element);
            if (level != null) return level.Name;

            if (box != null && levels != null && levels.Count > 0)
            {
                var z = box.Min.Z;
                var closest = levels
                    .OrderBy(l => Math.Abs(l.Elevation - z))
                    .FirstOrDefault();
                if (closest != null) return closest.Name;
            }

            return null;
        }

        private static Level GetLevelFromElementId(Document doc, Element element)
        {
            if (doc == null || element == null) return null;
            try
            {
                var levelId = element.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                    return doc.GetElement(levelId) as Level;
            }
            catch { }

            return null;
        }

        private static Level GetLevelFromParameters(Document doc, Element element)
        {
            if (doc == null || element == null) return null;

            var ids = new[]
            {
                BuiltInParameter.FAMILY_LEVEL_PARAM,
                BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                BuiltInParameter.WALL_BASE_CONSTRAINT,
                BuiltInParameter.RBS_START_LEVEL_PARAM,
                BuiltInParameter.SCHEDULE_LEVEL_PARAM
            };

            foreach (var bip in ids)
            {
                try
                {
                    var p = element.get_Parameter(bip);
                    if (p != null && p.StorageType == StorageType.ElementId)
                    {
                        var level = doc.GetElement(p.AsElementId()) as Level;
                        if (level != null) return level;
                    }
                }
                catch { }
            }

            return null;
        }

        private static string CleanLinkName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return name.Replace(".rvt", string.Empty).Replace(".ifc", string.Empty).Trim();
        }

        private static double MmToFeet(double mm) => mm / 304.8;
        private static double FeetToMm(double ft) => ft * 304.8;
        private static double Mm3ToFt3(double mm3) => mm3 / Math.Pow(304.8, 3);
    }
}

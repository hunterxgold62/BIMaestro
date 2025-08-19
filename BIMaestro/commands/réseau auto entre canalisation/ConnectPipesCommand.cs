using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;

namespace BIMaestro.Routing
{
    [Transaction(TransactionMode.Manual)]
    public class ConnectPipesCommand : IExternalCommand
    {
        // marges XY (en mm) utilisées pour l’itération d’A*
        static readonly double[] XY_MARGINS_MM = { 1600, 3200, 5600 };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            try
            {
                // 0) exactement 2 canalisations
                var (p1, p2) = PickExactlyTwoPipes(uidoc);
                if (p1 == null || p2 == null)
                {
                    TaskDialog.Show("Connexion canalisations", "Sélectionne exactement deux canalisations.");
                    return Result.Cancelled;
                }

                // 1) infos référence (type, niveau, DN, système)
                var refInfo = PipeRefInfo.FromPipe(doc, p1);
                double dnMm = FtToMm(refInfo.Diameter);

                // --- paramètres dépendants du DN ---
                double clearMm = Math.Max(25, dnMm * 0.5 + Clamp(dnMm * 0.05, 15, 50)); // ~ rayon + marge 15..50
                double snapTolMm = Clamp(dnMm * 0.06, 20, 60);
                double minSegMm = Math.Max(120, dnMm * 1.5);
                double jogTolMm = Math.Max(80, dnMm * 0.8);
                double detourPadMm = Math.Max(80, dnMm * 0.25);
                double exemptMm = Math.Max(250, dnMm * 1.0);
                double gridFastMm = Clamp(dnMm * 0.9, 250, 450);
                double gridFineMm = Clamp(dnMm * 0.65, 180, 320);
                double zStepMm = Clamp(dnMm * 0.75, 200, 400);
                double corridorMm = Math.Max(2500, dnMm * 6);

                // boîte globale large (pour couvrir l’enveloppe max de recherche)
                double expandGlobalMm = Math.Max(1600, XY_MARGINS_MM.Max() + corridorMm + 3000);

                // 2) connecteurs
                var c1 = GetBestConnector(p1, GetPipeCenter(p2));
                var c2 = GetBestConnector(p2, GetPipeCenter(p1));
                if (c1 == null || c2 == null)
                {
                    TaskDialog.Show("Connexion canalisations", "Connecteurs introuvables.");
                    return Result.Cancelled;
                }
                var start = (XYZ)c1.Origin;
                var end = (XYZ)c2.Origin;

                // 3) collecte BRUTE des murs (non gonflés)
                var bboxGlobal = MakeOutline(start, end, expandGlobalMm);
                var wallsRaw = CollectWallAabbsInOutline(doc, bboxGlobal);

                // 4) candidates (rapide + A* + contournements)
                var routes = new List<RouteCandidate>
                {
                    // on force les extrémités dès la création (sécurité)
                    new RouteCandidate { Label = "Route rapide (X→Y→Z)", Points = ForceEndpoints(BuildOrthogonalRoute(start, end, preferXY:true).Points, start, end) , Length = 0 },
                    new RouteCandidate { Label = "Route rapide (Y→X→Z)", Points = ForceEndpoints(BuildOrthogonalRoute(start, end, preferXY:false).Points, start, end) , Length = 0 }
                };

                int[] toStepsFast = XYMarginsToSteps(gridFastMm);
                int[] toStepsFine = XYMarginsToSteps(gridFineMm);

                var astarFast = AStarMulti(start, end, wallsRaw, gridFastMm, zStepMm, clearMm, exemptMm, toStepsFast, 1.0);
                if (astarFast != null)
                {
                    astarFast.Points = PostProcess(astarFast.Points, wallsRaw, clearMm, snapTolMm, jogTolMm, minSegMm);
                    astarFast.Points = ForceEndpoints(astarFast.Points, start, end); // <<< impose les bouts
                    routes.Add(astarFast);
                }

                var astarFine = AStarMulti(start, end, wallsRaw, gridFineMm, zStepMm, clearMm, exemptMm, toStepsFine, 1.06);
                if (astarFine != null)
                {
                    astarFine.Points = PostProcess(astarFine.Points, wallsRaw, clearMm, snapTolMm, jogTolMm, minSegMm);
                    astarFine.Points = ForceEndpoints(astarFine.Points, start, end); // <<< impose les bouts
                    routes.Add(astarFine);
                }

                var detours = BuildDetoursAroundBlockingWalls(start, end, wallsRaw, clearMm, detourPadMm);
                foreach (var d in detours)
                {
                    var fixedPts = ForceEndpoints(d.Points, start, end); // <<< impose les bouts
                    routes.Add(new RouteCandidate
                    {
                        Label = "Contournement direct",
                        Points = PostProcess(fixedPts, wallsRaw, clearMm, snapTolMm, jogTolMm, minSegMm),
                        Length = d.Length
                    });
                }

                if (routes.Count == 0)
                {
                    TaskDialog.Show("Connexion canalisations", "Aucun itinéraire généré.");
                    return Result.Cancelled;
                }

                // 5) aperçu + choix
                var colors = new[] { new Color(255, 0, 0), new Color(0, 180, 0), new Color(0, 110, 255), new Color(255, 180, 0) };
                var previews = new List<List<ElementId>>();
                using (var tPrev = new Transaction(doc, "Aperçu itinéraires"))
                {
                    tPrev.Start();
                    for (int i = 0; i < routes.Count && i < 4; i++)
                        previews.Add(CreatePreviewModelCurves(doc, routes[i].Points, colors[i], doc.ActiveView));
                    tPrev.Commit();
                }
                int chosen = PickPreviewRouteIndex(uidoc, previews);
                if (chosen < 0)
                {
                    using (var tDel = new Transaction(doc, "Nettoyage aperçu"))
                    { tDel.Start(); foreach (var set in previews) doc.Delete(set); tDel.Commit(); }
                    return Result.Cancelled;
                }
                var route = routes[chosen];

                // 6) création réseau + coudes
                using (var t = new Transaction(doc, "Connecter canalisations"))
                {
                    t.Start();

                    foreach (var set in previews) if (set.Count > 0) doc.Delete(set);

                    var pts = new List<XYZ>(route.Points);
                    // on réimpose aussi ici (ça l’était déjà dans ton code, je garde)
                    pts[0] = start; pts[pts.Count - 1] = end;

                    RemoveShortSegments(pts, MmToFt(minSegMm));

                    var created = BuildPipePath(doc, pts, refInfo);
                    TryElbowOrConnect(doc, created.FirstOrDefault(), start, c1);
                    TryElbowOrConnect(doc, created.LastOrDefault(), end, c2);

                    t.Commit();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ---------- NOUVEAU : impose start/end pour les aperçus ----------
        private static List<XYZ> ForceEndpoints(List<XYZ> pts, XYZ start, XYZ end)
        {
            if (pts == null || pts.Count == 0)
                return new List<XYZ> { start, end };

            pts[0] = start;
            pts[pts.Count - 1] = end;
            CleanupDuplicates(pts);
            CleanupCollinear(pts);
            return pts;
        }

        // ---------------- DN helpers ----------------
        private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));
        private static int[] XYMarginsToSteps(double gridMm)
        {
            return XY_MARGINS_MM.Select(m => Math.Max(6, (int)Math.Round(m / gridMm))).ToArray();
        }

        // ---------------- Post-process ----------------
        private static List<XYZ> PostProcess(List<XYZ> pts, List<BoundingBoxXYZ> wallsRaw, double clearMm, double snapTolMm, double jogTolMm, double minSegMm)
        {
            double clearFt = MmToFt(clearMm);

            var p = SmoothRectilinear(pts, wallsRaw, clearFt);
            p = SnapPolylineToWallOffsets(p, wallsRaw, clearMm, snapTolMm);
            p = CollapseAdjacentCorners(p, wallsRaw, clearFt, jogTolMm);

            // >>> NOUVEAU : optimisation des transitions Z (réduit coudes et doubles remontées)
            p = OptimizeZTransitions(p, wallsRaw, clearFt);

            CleanupDuplicates(p); CleanupCollinear(p);
            RemoveShortSegments(p, MmToFt(minSegMm));
            return p;
        }

        // ---------- Optimiseur Z (nouveau) ----------
        private static List<XYZ> OptimizeZTransitions(List<XYZ> pts, List<BoundingBoxXYZ> wallsRaw, double clearFt)
        {
            if (pts == null || pts.Count < 3) return pts;

            var start = pts.First();
            var end = pts.Last();

            var cands = new List<List<XYZ>>();

            // 4 stratégies : plateau à end.Z, start.Z, max, min
            cands.Add(RebasePathZ(pts, wallsRaw, clearFt, end.Z));
            cands.Add(RebasePathZ(pts, wallsRaw, clearFt, start.Z));
            cands.Add(RebasePathZ(pts, wallsRaw, clearFt, Math.Max(start.Z, end.Z)));
            cands.Add(RebasePathZ(pts, wallsRaw, clearFt, Math.Min(start.Z, end.Z)));

            // garde aussi l’original
            cands.Add(new List<XYZ>(pts));

            // filtre candidats invalides (collisions déjà testées dans Rebase, mais on sécurise)
            var valid = cands.Where(c => c != null && IsPolylineClear(c, wallsRaw, clearFt)).ToList();
            if (valid.Count == 0) return pts;

            // coût : longueur + α*nb_coudes + β*nb_transitions_Z
            double alpha = MmToFt(400);  // pénalité coude
            double beta = MmToFt(800);  // pénalité transition Z (plus chère)
            List<XYZ> best = valid[0]; double bestCost = PathCost(best, alpha, beta);

            for (int i = 1; i < valid.Count; i++)
            {
                double cost = PathCost(valid[i], alpha, beta);
                if (cost < bestCost) { best = valid[i]; bestCost = cost; }
            }
            return best;
        }

        private static List<XYZ> RebasePathZ(List<XYZ> pts, List<BoundingBoxXYZ> wallsRaw, double clearFt, double targetZ)
        {
            var start = pts.First(); var end = pts.Last();

            var list = new List<XYZ>();
            list.Add(start);

            // vertical au départ si besoin
            if (Math.Abs(start.Z - targetZ) > 1e-9)
                list.Add(new XYZ(start.X, start.Y, targetZ));

            // plateau : points intermédiaires en XY, tous à targetZ
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var q = pts[i];
                list.Add(new XYZ(q.X, q.Y, targetZ));
            }

            // vertical à l’arrivée si besoin
            if (Math.Abs(end.Z - targetZ) > 1e-9)
                list.Add(new XYZ(end.X, end.Y, targetZ));

            list.Add(end);

            CleanupDuplicates(list); CleanupCollinear(list);

            // collision check avec marge
            if (!IsPolylineClear(list, wallsRaw, clearFt)) return null;

            // petit lissage après rebase
            list = SmoothRectilinear(list, wallsRaw, clearFt);
            CleanupDuplicates(list); CleanupCollinear(list);
            return list;
        }

        private static double PathCost(List<XYZ> pts, double alpha, double beta)
            => PolyLength(pts) + alpha * CountElbows(pts) + beta * CountZTransitions(pts);

        private static int CountElbows(List<XYZ> pts)
        {
            int count = 0;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var u = pts[i] - pts[i - 1];
                var v = pts[i + 1] - pts[i];
                var cp = u.CrossProduct(v);
                if (cp.GetLength() > 1e-9) count++;
            }
            return count;
        }

        private static int CountZTransitions(List<XYZ> pts)
        {
            int count = 0;
            for (int i = 0; i < pts.Count - 1; i++)
                if (Math.Abs(pts[i + 1].Z - pts[i].Z) > 1e-9) count++;
            return count;
        }

        // ---------------- Sélection & infos pipe ----------------
        private static (Pipe, Pipe) PickExactlyTwoPipes(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var selected = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).OfType<Pipe>().ToList();
            if (selected.Count >= 2) return (selected[0], selected[1]);

            var filter = new PipeFilter();
            while (selected.Count < 2)
            {
                try
                {
                    var r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, filter,
                        selected.Count == 0 ? "Sélectionne la 1ère canalisation" : "Sélectionne la 2ème canalisation");
                    var e = doc.GetElement(r) as Pipe;
                    if (e != null && !selected.Contains(e)) selected.Add(e);
                }
                catch { break; }
            }
            if (selected.Count < 2) return (null, null);
            return (selected[0], selected[1]);
        }
        private class PipeFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        { public bool AllowElement(Element e) => e is Pipe; public bool AllowReference(Reference r, XYZ p) => true; }

        private static XYZ GetPipeCenter(Pipe p)
        {
            var lc = p.Location as LocationCurve;
            if (lc?.Curve != null) return (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) / 2.0;
            var bb = p.get_BoundingBox(null);
            return bb != null ? (bb.Min + bb.Max) / 2.0 : XYZ.Zero;
        }

        private static Connector GetBestConnector(Pipe pipe, XYZ towards)
        {
            var cm = pipe.ConnectorManager; if (cm == null) return null;
            Connector bestFree = null, bestAny = null; double dF = double.MaxValue, dA = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                if (c.Domain != Domain.DomainPiping) continue;
                double d = c.Origin.DistanceTo(towards);
                if (!c.IsConnected && d < dF) { dF = d; bestFree = c; }
                if (d < dA) { dA = d; bestAny = c; }
            }
            return bestFree ?? bestAny;
        }

        private struct PipeRefInfo
        {
            public ElementId SystemTypeId, PipeTypeId, LevelId;
            public double Diameter; // ft
            public static PipeRefInfo FromPipe(Document doc, Pipe p)
            {
                var info = new PipeRefInfo
                {
                    PipeTypeId = p.GetTypeId(),
                    LevelId = p.ReferenceLevel?.Id ?? FindNearestLevelId(doc, (p.Location as LocationCurve)?.Curve?.GetEndPoint(0) ?? XYZ.Zero),
                    Diameter = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0.05,
                    SystemTypeId = p.MEPSystem?.GetTypeId()
                        ?? new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId()
                        ?? ElementId.InvalidElementId
                };
                return info;
            }
        }
        private static ElementId FindNearestLevelId(Document doc, XYZ pt)
        {
            var lv = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            if (lv.Count == 0) return ElementId.InvalidElementId;
            Level best = lv[0]; double dz = Math.Abs(best.Elevation - pt.Z);
            foreach (var L in lv) { var d = Math.Abs(L.Elevation - pt.Z); if (d < dz) { dz = d; best = L; } }
            return best.Id;
        }

        // ---------------- Obstacles (murs) ----------------
        private static Outline MakeOutline(XYZ a, XYZ b, double inflateMm)
        {
            double inf = MmToFt(inflateMm);
            return new Outline(
                new XYZ(Math.Min(a.X, b.X) - inf, Math.Min(a.Y, b.Y) - inf, Math.Min(a.Z, b.Z) - inf),
                new XYZ(Math.Max(a.X, b.X) + inf, Math.Max(a.Y, b.Y) + inf, Math.Max(a.Z, b.Z) + inf));
        }

        // Collecte brute (AABB non gonflées)
        private static List<BoundingBoxXYZ> CollectWallAabbsInOutline(Document doc, Outline global)
        {
            var filter = new BoundingBoxIntersectsFilter(global);
            var walls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WherePasses(filter)
                .WhereElementIsNotElementType();

            var list = new List<BoundingBoxXYZ>();
            foreach (var w in walls)
            {
                var bb = w.get_BoundingBox(null);
                if (bb == null) continue;

                list.Add(new BoundingBoxXYZ
                {
                    Min = new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                    Max = new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
                });
            }
            return list;
        }

        // --- géométrie 2D utilitaire (inchangé, abrégé ici) ---
        private static double DistanceSegmentAabb2D(XYZ p0, XYZ p1, XYZ bmin, XYZ bmax)
        {
            if (SegmentAabbIntersect2D(p0, p1, bmin, bmax)) return 0.0;
            var rectEdges = new (XYZ, XYZ)[]
            {
                (new XYZ(bmin.X, bmin.Y, 0), new XYZ(bmax.X, bmin.Y, 0)),
                (new XYZ(bmax.X, bmin.Y, 0), new XYZ(bmax.X, bmax.Y, 0)),
                (new XYZ(bmax.X, bmax.Y, 0), new XYZ(bmin.X, bmax.Y, 0)),
                (new XYZ(bmin.X, bmax.Y, 0), new XYZ(bmin.X, bmin.Y, 0))
            };
            double best = double.MaxValue;
            foreach (var e in rectEdges)
                best = Math.Min(best, DistanceSegmentSegment2D(p0, p1, e.Item1, e.Item2));
            return best;
        }
        private static double DistancePointAabb2D(XYZ p, XYZ bmin, XYZ bmax)
        {
            double dx = (p.X < bmin.X) ? (bmin.X - p.X) : (p.X > bmax.X ? p.X - bmax.X : 0);
            double dy = (p.Y < bmin.Y) ? (bmin.Y - p.Y) : (p.Y > bmax.Y ? p.Y - bmax.Y : 0);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        private static bool SegmentAabbIntersect2D(XYZ p0, XYZ p1, XYZ bmin, XYZ bmax)
        {
            double x0 = p0.X, y0 = p0.Y, x1 = p1.X, y1 = p1.Y;
            const int INSIDE = 0, LEFT = 1, RIGHT = 2, BOTTOM = 4, TOP = 8;
            int Code(double x, double y)
            {
                int c = INSIDE;
                if (x < bmin.X) c |= LEFT; else if (x > bmax.X) c |= RIGHT;
                if (y < bmin.Y) c |= BOTTOM; else if (y > bmax.Y) c |= TOP;
                return c;
            }
            int c0 = Code(x0, y0), c1 = Code(x1, y1);
            while (true)
            {
                if ((c0 | c1) == 0) return true;
                if ((c0 & c1) != 0) return false;
                double x = 0, y = 0; int outcode = c0 != 0 ? c0 : c1;
                if ((outcode & TOP) != 0) { x = x0 + (x1 - x0) * (bmax.Y - y0) / (y1 - y0); y = bmax.Y; }
                else if ((outcode & BOTTOM) != 0) { x = x0 + (x1 - x0) * (bmin.Y - y0) / (y1 - y0); y = bmin.Y; }
                else if ((outcode & RIGHT) != 0) { y = y0 + (y1 - y0) * (bmax.X - x0) / (x1 - x0); x = bmax.X; }
                else { y = y0 + (y1 - y0) * (bmin.X - x0) / (x1 - x0); x = bmin.X; }
                if (outcode == c0) { x0 = x; y0 = y; c0 = Code(x0, y0); } else { x1 = x; y1 = y; c1 = Code(x1, y1); }
            }
        }
        private static double DistanceSegmentSegment2D(XYZ a0, XYZ a1, XYZ b0, XYZ b1)
        {
            XYZ A0 = new XYZ(a0.X, a0.Y, 0), A1 = new XYZ(a1.X, a1.Y, 0), B0 = new XYZ(b0.X, b0.Y, 0), B1 = new XYZ(b1.X, b1.Y, 0);
            if (SegmentsIntersect2D(A0, A1, B0, B1)) return 0;
            double d(XYZ p, XYZ q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
            double DistPointSeg(XYZ p, XYZ s0, XYZ s1)
            {
                var v = new XYZ(s1.X - s0.X, s1.Y - s0.Y, 0); var w = new XYZ(p.X - s0.X, p.Y - s0.Y, 0);
                double c1 = (w.X * v.X + w.Y * v.Y); if (c1 <= 0) return d(p, s0);
                double c2 = (v.X * v.X + v.Y * v.Y); if (c2 <= c1) return d(p, s1);
                double t = c1 / c2; var proj = new XYZ(s0.X + t * v.X, s0.Y + t * v.Y, 0); return d(p, proj);
            }
            return Math.Min(
                Math.Min(DistPointSeg(A0, B0, B1), DistPointSeg(A1, B0, B1)),
                Math.Min(DistPointSeg(B0, A0, A1), DistPointSeg(B1, A0, A1)));
        }
        private static bool SegmentsIntersect2D(XYZ p, XYZ p2, XYZ q, XYZ q2)
        {
            double o(XYZ a, XYZ b, XYZ c) { return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X); }
            bool on(XYZ a, XYZ b, XYZ c)
            {
                return Math.Abs(o(a, b, c)) < 1e-12 &&
                                         Math.Min(a.X, b.X) - 1e-12 <= c.X && c.X <= Math.Max(a.X, b.X) + 1e-12 &&
                                         Math.Min(a.Y, b.Y) - 1e-12 <= c.Y && c.Y <= Math.Max(a.Y, b.Y) + 1e-12;
            }
            double o1 = o(p, p2, q), o2 = o(p, p2, q2), o3 = o(q, q2, p), o4 = o(q, q2, p2);
            if (o1 * o2 < 0 && o3 * o4 < 0) return true;
            if (on(p, p2, q) || on(p, p2, q2) || on(q, q2, p) || on(q, q2, p2)) return true;
            return false;
        }

        // ---------------- Routes candidates ----------------
        private class RouteCandidate { public string Label; public List<XYZ> Points; public double Length; }

        private static RouteCandidate BuildOrthogonalRoute(XYZ start, XYZ end, bool preferXY)
        {
            var pts = new List<XYZ> { start }; XYZ p = start;
            if (preferXY) { p = new XYZ(end.X, p.Y, p.Z); pts.Add(p); p = new XYZ(p.X, end.Y, p.Z); pts.Add(p); }
            else { p = new XYZ(p.X, end.Y, p.Z); pts.Add(p); p = new XYZ(end.X, p.Y, p.Z); pts.Add(p); }
            p = new XYZ(p.X, p.Y, end.Z); pts.Add(p);
            CleanupCollinear(pts);
            return new RouteCandidate { Label = preferXY ? "Route rapide (X→Y→Z)" : "Route rapide (Y→X→Z)", Points = pts, Length = PolyLength(pts) };
        }

        // ---------------- A* (avec filtre dynamique) ----------------
        private class Node { public XYZ P; public double G; public double F; public Node(XYZ p, double g, double f) { P = p; G = g; F = f; } }
        private class XyzEq : IEqualityComparer<XYZ>
        {
            const double Eps = 1e-06; public bool Equals(XYZ a, XYZ b) => a != null && b != null && a.DistanceTo(b) <= Eps;
            public int GetHashCode(XYZ p) { long qx = (long)Math.Round(p.X * 1e6), qy = (long)Math.Round(p.Y * 1e6), qz = (long)Math.Round(p.Z * 1e6); unchecked { return (int)(qx ^ (qy << 1) ^ (qz << 2)); } }
        }

        private static RouteCandidate AStarMulti(
            XYZ start, XYZ end, List<BoundingBoxXYZ> wallsRaw,
            double gridStepMm, double zStepMm, double clearMm, double exRadMm,
            int[] xyMarginSteps, double heuristicBias)
        {
            foreach (int m in xyMarginSteps)
            {
                var r = AStarTry(start, end, wallsRaw, gridStepMm, zStepMm, clearMm, exRadMm, heuristicBias, m);
                if (r != null) return r;
            }
            return null;
        }

        private static RouteCandidate AStarTry(
            XYZ start, XYZ end, List<BoundingBoxXYZ> wallsRaw,
            double gridStepMm, double zStepMm, double clearMm, double exRadMm, double heuristicBias, int xyMarginSteps)
        {
            double step = MmToFt(gridStepMm);
            double zStep = MmToFt(zStepMm);
            double clear = MmToFt(clearMm);
            double exRad = MmToFt(exRadMm);

            double minZ = Math.Min(start.Z, end.Z) - 2 * zStep - clear;
            double maxZ = Math.Max(start.Z, end.Z) + 2 * zStep + clear;

            var min = new XYZ(Math.Min(start.X, end.X) - xyMarginSteps * step,
                              Math.Min(start.Y, end.Y) - xyMarginSteps * step, minZ);
            var max = new XYZ(Math.Max(start.X, end.X) + xyMarginSteps * step,
                              Math.Max(start.Y, end.Y) + xyMarginSteps * step, maxZ);

            var searchObs = FilterAabbsToRange(wallsRaw, min, max, clear);

            XYZ Snap(XYZ p) => new XYZ(
                Math.Round((p.X - min.X) / step) * step + min.X,
                Math.Round((p.Y - min.Y) / step) * step + min.Y,
                Math.Round((p.Z - min.Z) / zStep) * zStep + min.Z);

            var s = Snap(start); var g = Snap(end);
            var open = new List<Node> { new Node(s, 0, s.DistanceTo(g)) };
            var came = new Dictionary<XYZ, XYZ>(new XyzEq());
            var gsc = new Dictionary<XYZ, double>(new XyzEq()) { { s, 0 } };
            var moves = new[] { new XYZ(step, 0, 0), new XYZ(-step, 0, 0), new XYZ(0, step, 0), new XYZ(0, -step, 0), new XYZ(0, 0, zStep), new XYZ(0, 0, -zStep) };
            var eq = new XyzEq(); int it = 0, itMax = 70000;

            while (open.Count > 0 && it++ < itMax)
            {
                open.Sort((a, b) => a.F.CompareTo(b.F));
                var cur = open[0]; open.RemoveAt(0);

                if (eq.Equals(cur.P, g))
                {
                    var path = Reconstruct(came, cur.P); path.Insert(0, s);
                    path[0] = start; path[path.Count - 1] = end; CleanupCollinear(path);
                    return new RouteCandidate { Label = "Route évite-objets (A*)", Points = path, Length = PolyLength(path) };
                }

                foreach (var d in moves)
                {
                    var np = cur.P + d;
                    if (np.X < min.X || np.X > max.X || np.Y < min.Y || np.Y > max.Y || np.Z < min.Z || np.Z > max.Z) continue;

                    bool exempt = (np.DistanceTo(start) <= exRad) || (np.DistanceTo(end) <= exRad);
                    if (!exempt && !IsFree(np, searchObs, clear)) continue;

                    double gTent = gsc[cur.P] + d.GetLength();
                    if (!gsc.TryGetValue(np, out double prev) || gTent < prev)
                    {
                        came[np] = cur.P; gsc[np] = gTent;
                        double f = gTent + heuristicBias * np.DistanceTo(g);
                        var ex = open.FirstOrDefault(n => eq.Equals(n.P, np));
                        if (ex == null) open.Add(new Node(np, gTent, f)); else { ex.G = gTent; ex.F = f; }
                    }
                }
            }
            return null;

            static List<XYZ> Reconstruct(Dictionary<XYZ, XYZ> came, XYZ cur)
            {
                var list = new List<XYZ> { cur }; var eq2 = new XyzEq();
                while (came.TryGetValue(cur, out var prev)) { list.Add(prev); cur = prev; }
                list.Reverse(); return list;
            }
        }

        private static List<BoundingBoxXYZ> FilterAabbsToRange(List<BoundingBoxXYZ> boxes, XYZ min, XYZ max, double pad)
        {
            var outList = new List<BoundingBoxXYZ>();
            foreach (var b in boxes)
            {
                if (!((b.Max.X + pad < min.X) || (b.Min.X - pad > max.X)
                    || (b.Max.Y + pad < min.Y) || (b.Min.Y - pad > max.Y)
                    || (b.Max.Z + pad < min.Z) || (b.Min.Z - pad > max.Z)))
                {
                    outList.Add(b);
                }
            }
            return outList;
        }

        private static bool IsFree(XYZ p, List<BoundingBoxXYZ> obs, double cl)
        {
            foreach (var b in obs)
            {
                if (p.X >= b.Min.X - cl && p.X <= b.Max.X + cl &&
                    p.Y >= b.Min.Y - cl && p.Y <= b.Max.Y + cl &&
                    p.Z >= b.Min.Z - cl && p.Z <= b.Max.Z + cl) return false;
            }
            return true;
        }

        // ---------------- Contournement déterministe ----------------
        private static List<RouteCandidate> BuildDetoursAroundBlockingWalls(
            XYZ start, XYZ end, List<BoundingBoxXYZ> wallsRaw, double clearMm, double padMm)
        {
            var list = new List<RouteCandidate>();
            if (wallsRaw == null || wallsRaw.Count == 0) return list;

            double clear = MmToFt(clearMm);

            var blockers = new List<BoundingBoxXYZ>();
            foreach (var w in wallsRaw)
            {
                var min = new XYZ(w.Min.X - clear, w.Min.Y - clear, w.Min.Z);
                var max = new XYZ(w.Max.X + clear, w.Max.Y + clear, w.Max.Z);
                if (SegmentAabbIntersect2D(start, end, min, max)) blockers.Add(w);
            }
            if (blockers.Count == 0) return list;

            var union = UnionAabbs(blockers);
            double pad = MmToFt(padMm);

            double yBelow = union.Min.Y - clear - pad;
            double yAbove = union.Max.Y + clear + pad;
            var optBelow = new List<XYZ> { start, new XYZ(start.X, yBelow, start.Z), new XYZ(end.X, yBelow, end.Z), end };
            var optAbove = new List<XYZ> { start, new XYZ(start.X, yAbove, start.Z), new XYZ(end.X, yAbove, end.Z), end };

            double xLeft = union.Min.X - clear - pad;
            double xRight = union.Max.X + clear + pad;
            var optLeft = new List<XYZ> { start, new XYZ(xLeft, start.Y, start.Z), new XYZ(xLeft, end.Y, end.Z), end };
            var optRight = new List<XYZ> { start, new XYZ(xRight, start.Y, start.Z), new XYZ(xRight, end.Y, end.Z), end };

            var candidates = new List<List<XYZ>>();
            foreach (var opt in new[] { optBelow, optAbove, optLeft, optRight })
            {
                CleanupDuplicates(opt); CleanupCollinear(opt);
                if (IsPolylineClear(opt, wallsRaw, clear)) candidates.Add(opt);
            }

            candidates.Sort((a, b) => PolyLength(a).CompareTo(PolyLength(b)));
            for (int i = 0; i < Math.Min(2, candidates.Count); i++)
                list.Add(new RouteCandidate { Label = "Contournement direct", Points = candidates[i], Length = PolyLength(candidates[i]) });

            return list;
        }
        private static BoundingBoxXYZ UnionAabbs(List<BoundingBoxXYZ> boxes)
        {
            var u = new BoundingBoxXYZ
            {
                Min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue),
                Max = new XYZ(double.MinValue, double.MinValue, double.MinValue)
            };
            foreach (var b in boxes)
            {
                u.Min = new XYZ(Math.Min(u.Min.X, b.Min.X), Math.Min(u.Min.Y, b.Min.Y), Math.Min(u.Min.Z, b.Min.Z));
                u.Max = new XYZ(Math.Max(u.Max.X, b.Max.X), Math.Max(u.Max.Y, b.Max.Y), Math.Max(u.Max.Z, b.Max.Z));
            }
            return u;
        }

        // ---------------- Lissage / Snap / Fusion coins ----------------
        private static List<XYZ> SmoothRectilinear(List<XYZ> path, List<BoundingBoxXYZ> wallsRaw, double clearFt)
        {
            if (path == null || path.Count <= 2) return path;
            CleanupDuplicates(path); CleanupCollinear(path);

            var result = new List<XYZ> { path[0] };
            int i = 0;
            while (i < path.Count - 1)
            {
                int bestJ = i + 1; List<XYZ> bestPatch = null;
                for (int j = path.Count - 1; j > i; j--)
                {
                    var patch = RectilinearVisiblePatch(path[i], path[j], wallsRaw, clearFt);
                    if (patch != null) { bestJ = j; bestPatch = patch; break; }
                }
                if (bestPatch != null) { for (int k = 1; k < bestPatch.Count; k++) result.Add(bestPatch[k]); i = bestJ; }
                else { result.Add(path[i + 1]); i++; }
            }
            CleanupDuplicates(result); CleanupCollinear(result);
            return result;
        }

        private static List<XYZ> SnapPolylineToWallOffsets(List<XYZ> pts, List<BoundingBoxXYZ> wallsRaw, double clearMm, double tolMm)
        {
            if (pts.Count < 3) return pts;
            double clear = MmToFt(clearMm), tol = MmToFt(tolMm);

            for (int i = 1; i < pts.Count - 1; i++)
            {
                var p = pts[i];
                foreach (var w in wallsRaw)
                {
                    double x1 = w.Min.X - clear, x2 = w.Max.X + clear;
                    if (Math.Abs(p.X - x1) <= tol) { p = new XYZ(x1, p.Y, p.Z); }
                    else if (Math.Abs(p.X - x2) <= tol) { p = new XYZ(x2, p.Y, p.Z); }

                    double y1 = w.Min.Y - clear, y2 = w.Max.Y + clear;
                    if (Math.Abs(p.Y - y1) <= tol) { p = new XYZ(p.X, y1, p.Z); }
                    else if (Math.Abs(p.Y - y2) <= tol) { p = new XYZ(p.X, y2, p.Z); }
                }
                pts[i] = p;
            }
            CleanupDuplicates(pts); CleanupCollinear(pts);
            return pts;
        }

        private static List<XYZ> CollapseAdjacentCorners(List<XYZ> pts, List<BoundingBoxXYZ> wallsRaw, double clearFt, double jogTolMm)
        {
            if (pts.Count < 4) return pts;
            double tol = MmToFt(jogTolMm);

            int i = 1;
            while (i < pts.Count - 2)
            {
                var a = pts[i - 1]; var b = pts[i]; var c = pts[i + 1]; var d = pts[i + 2];

                bool corner1 = IsAxisRightAngle(a, b, c);
                bool corner2 = IsAxisRightAngle(b, c, d);
                if (corner1 && corner2 && b.DistanceTo(c) <= tol)
                {
                    var p1 = new XYZ(b.X, c.Y, (b.Z + c.Z) * 0.5);
                    var p2 = new XYZ(c.X, b.Y, (b.Z + c.Z) * 0.5);

                    if (IsPolylineClear(new List<XYZ> { a, p1, d }, wallsRaw, clearFt)) { pts[i] = p1; pts.RemoveAt(i + 1); continue; }
                    if (IsPolylineClear(new List<XYZ> { a, p2, d }, wallsRaw, clearFt)) { pts[i] = p2; pts.RemoveAt(i + 1); continue; }
                }
                i++;
            }
            CleanupDuplicates(pts); CleanupCollinear(pts);
            return pts;
        }
        private static bool IsAxisRightAngle(XYZ a, XYZ b, XYZ c)
        {
            var u = b - a; var v = c - b;
            bool axisU = (Math.Abs(u.X) < 1e-9) || (Math.Abs(u.Y) < 1e-9) || (Math.Abs(u.Z) < 1e-9);
            bool axisV = (Math.Abs(v.X) < 1e-9) || (Math.Abs(v.Y) < 1e-9) || (Math.Abs(v.Z) < 1e-9);
            return axisU && axisV && u.CrossProduct(v).GetLength() > 1e-9;
        }

        private static List<XYZ> RectilinearVisiblePatch(XYZ a, XYZ b, List<BoundingBoxXYZ> obstaclesRaw, double clearFt)
        {
            var perms = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
            foreach (var p in perms)
            {
                var pts = BuildRectilinear(a, b, p[0], p[1], p[2]);
                CleanupDuplicates(pts); CleanupCollinear(pts);
                if (IsPolylineClear(pts, obstaclesRaw, clearFt)) return pts;
            }
            return null;
        }
        private static List<XYZ> BuildRectilinear(XYZ a, XYZ b, int ax1, int ax2, int ax3)
        {
            XYZ p1 = new XYZ(ax1 == 0 ? b.X : a.X, ax1 == 1 ? b.Y : a.Y, ax1 == 2 ? b.Z : a.Z);
            XYZ p2 = new XYZ(ax2 == 0 ? b.X : p1.X, ax2 == 1 ? b.Y : p1.Y, ax2 == 2 ? b.Z : p1.Z);
            var list = new List<XYZ> { a, p1, p2, b };
            CleanupDuplicates(list); CleanupCollinear(list);
            return list;
        }

        private static bool IsPolylineClear(List<XYZ> pts, List<BoundingBoxXYZ> obstaclesRaw, double clearFt)
        {
            for (int i = 0; i < pts.Count - 1; i++)
                if (SegmentHitsAny(pts[i], pts[i + 1], obstaclesRaw, clearFt)) return false;
            return true;
        }

        private static bool SegmentHitsAny(XYZ a, XYZ b, List<BoundingBoxXYZ> boxes, double cl)
        {
            foreach (var bb in boxes)
            {
                var min = new XYZ(bb.Min.X - cl, bb.Min.Y - cl, bb.Min.Z - cl);
                var max = new XYZ(bb.Max.X + cl, bb.Max.Y + cl, bb.Max.Z + cl);
                if (SegmentAabbIntersect(a, b, min, max)) return true;
            }
            return false;
        }

        private static bool SegmentAabbIntersect(XYZ p0, XYZ p1, XYZ bmin, XYZ bmax)
        {
            double t0 = 0, t1 = 1; double dx = p1.X - p0.X, dy = p1.Y - p0.Y, dz = p1.Z - p0.Z;
            bool Clip(double p, double q, ref double t0Ref, ref double t1Ref)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0; double r = q / p;
                if (p < 0) { if (r > t1Ref) return false; if (r > t0Ref) t0Ref = r; }
                else { if (r < t0Ref) return false; if (r < t1Ref) t1Ref = r; }
                return true;
            }
            if (!Clip(-dx, p0.X - bmin.X, ref t0, ref t1)) return false;
            if (!Clip(dx, bmax.X - p0.X, ref t0, ref t1)) return false;
            if (!Clip(-dy, p0.Y - bmin.Y, ref t0, ref t1)) return false;
            if (!Clip(dy, bmax.Y - p0.Y, ref t0, ref t1)) return false;
            if (!Clip(-dz, p0.Z - bmin.Z, ref t0, ref t1)) return false;
            if (!Clip(dz, bmax.Z - p0.Z, ref t0, ref t1)) return false;
            return t1 >= t0;
        }

        // ---------------- Aperçu ----------------
        private static List<ElementId> CreatePreviewModelCurves(Document doc, List<XYZ> pts, Color color, View view)
        {
            var ids = new List<ElementId>(); CleanupDuplicates(pts);
            var ogs = new OverrideGraphicSettings(); ogs.SetProjectionLineColor(color);

            for (int i = 0; i < pts.Count - 1; i++)
            {
                var a = pts[i]; var b = pts[i + 1]; if (a.DistanceTo(b) < 1e-7) continue;
                var line = Line.CreateBound(a, b);
                using (var st = new SubTransaction(doc))
                {
                    st.Start();
                    var sp = MakeSketchPlaneForLine(doc, a, b);
                    var mc = doc.Create.NewModelCurve(line, sp);
                    st.Commit();
                    ids.Add(mc.Id); view.SetElementOverrides(mc.Id, ogs);
                }
            }
            return ids;
        }
        private static SketchPlane MakeSketchPlaneForLine(Document doc, XYZ a, XYZ b)
        {
            var dir = (b - a).Normalize(); XYZ n = dir.CrossProduct(XYZ.BasisZ);
            if (n.GetLength() < 1e-9) n = dir.CrossProduct(XYZ.BasisX);
            n = n.Normalize(); var plane = Plane.CreateByNormalAndOrigin(n, a);
            return SketchPlane.Create(doc, plane);
        }
        private static int PickPreviewRouteIndex(UIDocument uidoc, List<List<ElementId>> sets)
        {
            var accepted = new HashSet<ElementId>(sets.SelectMany(s => s));
            var filter = new PreviewSelFilter(accepted);
            try
            {
                var r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, filter, "Clique la ligne colorée de l'itinéraire");
                var id = r.ElementId;
                for (int i = 0; i < sets.Count; i++) if (sets[i].Contains(id)) return i;
            }
            catch { }
            return -1;
        }
        private class PreviewSelFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            private readonly HashSet<ElementId> _ok; public PreviewSelFilter(HashSet<ElementId> ok) { _ok = ok; }
            public bool AllowElement(Element e) => _ok.Contains(e.Id); public bool AllowReference(Reference r, XYZ p) => true;
        }

        // ---------------- Création réseau ----------------
        private static List<Pipe> BuildPipePath(Document doc, List<XYZ> pts, PipeRefInfo info)
        {
            var pipes = new List<Pipe>();
            CleanupDuplicates(pts); CleanupCollinear(pts);

            Pipe prev = null; XYZ joint = null;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var a = pts[i]; var b = pts[i + 1]; if (a.DistanceTo(b) < 1e-7) continue;

                var pipe = Pipe.Create(doc, info.SystemTypeId, info.PipeTypeId, info.LevelId, a, b);
                if (pipe == null) continue;

                var diam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (diam != null && diam.StorageType == StorageType.Double) diam.Set(info.Diameter);

                pipes.Add(pipe);

                if (prev != null && joint != null)
                {
                    var cPrev = GetEndConnectorClosestToPoint(prev, joint);
                    var cCurr = GetEndConnectorClosestToPoint(pipe, joint);
                    if (cPrev != null && cCurr != null)
                    {
                        try { doc.Create.NewElbowFitting(cPrev, cCurr); }
                        catch { try { cPrev.ConnectTo(cCurr); } catch { } }
                    }
                }
                prev = pipe; joint = b;
            }
            return pipes;
        }

        private static Connector GetEndConnectorClosestToPoint(Pipe pipe, XYZ pt)
        {
            Connector best = null; double bestD = double.MaxValue;
            foreach (Connector c in pipe.ConnectorManager.Connectors)
            {
                if (c.Domain != Domain.DomainPiping) continue;
                if (c.ConnectorType != ConnectorType.End) continue;
                double d = c.Origin.DistanceTo(pt); if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null)
                foreach (Connector c in pipe.ConnectorManager.Connectors)
                {
                    if (c.Domain != Domain.DomainPiping) continue;
                    double d = c.Origin.DistanceTo(pt); if (d < bestD) { bestD = d; best = c; }
                }
            return best;
        }
        private static void TryElbowOrConnect(Document doc, Pipe pipeEnd, XYZ endPoint, Connector target)
        {
            if (pipeEnd == null || target == null) return;
            var own = GetEndConnectorClosestToPoint(pipeEnd, endPoint); if (own == null) return;
            try { doc.Create.NewElbowFitting(own, target); }
            catch { try { own.ConnectTo(target); } catch { } }
        }

        // ---------------- Utils géométrie ----------------
        private static void CleanupDuplicates(List<XYZ> pts)
        { for (int i = pts.Count - 2; i >= 0; i--) if (pts[i].DistanceTo(pts[i + 1]) <= 1e-9) pts.RemoveAt(i + 1); }
        private static void CleanupCollinear(List<XYZ> pts)
        {
            int i = 1; while (i < pts.Count - 1)
            {
                var a = pts[i - 1]; var b = pts[i]; var c = pts[i + 1];
                var cp = (b - a).CrossProduct(c - b);
                if (cp.GetLength() <= 1e-9) pts.RemoveAt(i); else i++;
            }
        }
        private static void RemoveShortSegments(List<XYZ> pts, double minLen)
        {
            int i = 0;
            while (i < pts.Count - 1)
            {
                if (pts[i].DistanceTo(pts[i + 1]) < minLen) { pts.RemoveAt(i + 1); if (i > 0) i--; }
                else i++;
            }
        }
        private static double PolyLength(List<XYZ> pts)
        { double L = 0; for (int i = 0; i < pts.Count - 1; i++) L += pts[i].DistanceTo(pts[i + 1]); return L; }
        private static double MmToFt(double mm) => mm / 304.8;
        private static double FtToMm(double ft) => ft * 304.8;
    }
}

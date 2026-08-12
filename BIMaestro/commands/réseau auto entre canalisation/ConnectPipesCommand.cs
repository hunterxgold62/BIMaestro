using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class ConnectPipesCommand : BaseTrackedCommand
    {
        // Marges XY (mm) pour l’exploration A*
        static readonly double[] XY_MARGINS_MM = { 1600, 3200, 5600 };
        private static bool _teePlacedThisRun = false;
        protected override string ButtonId => "ConnectPipesCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
            _teePlacedThisRun = false;

            try
            {
                // ---- 0) Sélection ----
                var (p1, p2) = PickExactlyTwoPipes(uidoc);
                if (p1 == null || p2 == null)
                {
                    TaskDialog.Show(UiLanguage.T("Connexion canalisations", "Connect Pipes"), UiLanguage.T("Sélectionne exactement deux canalisations.", "Select exactly two pipes."));
                    return Result.Cancelled;
                }

                // ---- 1) Paramètres dépendants du DN (basés sur p1) ----
                var ref1 = PipeRefInfo.FromPipe(doc, p1);
                var ref2 = PipeRefInfo.FromPipe(doc, p2);
                double dnMm = FtToMm(ref1.Diameter);

                double clearMm = Math.Max(25, dnMm * 0.5 + Clamp(dnMm * 0.05, 15, 50));
                double snapTolMm = Clamp(dnMm * 0.06, 20, 60);
                double minSegMm = Math.Max(120, dnMm * 1.5);
                double jogTolMm = Math.Max(80, dnMm * 0.8);
                double detourPadMm = Math.Max(80, dnMm * 0.25);
                double exemptMm = Math.Max(250, dnMm * 1.0);
                double gridFastMm = Clamp(dnMm * 0.9, 250, 450);
                double gridFineMm = Clamp(dnMm * 0.65, 180, 320);
                double zStepMm = Clamp(dnMm * 0.75, 200, 400);
                double corridorMm = Math.Max(2500, dnMm * 6);

                // ---- 2) Ancrages (Té si tronc sinon connecteur) ----
                var c1 = GetBestConnector(p1, GetPipeCenter(p2));
                var c2 = GetBestConnector(p2, GetPipeCenter(p1));
                if (c1 == null || c2 == null)
                {
                    TaskDialog.Show(UiLanguage.T("Connexion canalisations", "Connect Pipes"), UiLanguage.T("Connecteurs introuvables.", "No connectors were found."));
                    return Result.Cancelled;
                }

                bool startIsTee, endIsTee; XYZ startFoot, endFoot; Connector _;
                var start = GetRoutingAnchor(doc, p1, c2.Origin, ref1.Diameter, out startIsTee, out startFoot, out _);
                var end = GetRoutingAnchor(doc, p2, c1.Origin, ref2.Diameter, out endIsTee, out endFoot, out _);

                var anchorStart = (pipe: p1, isTee: startIsTee, foot: startFoot, conn: c1);
                var anchorEnd = (pipe: p2, isTee: endIsTee, foot: endFoot, conn: c2);

                // ---- 3) Obstacles ----
                double expandGlobalMm = Math.Max(1600, XY_MARGINS_MM.Max() + corridorMm + 3000);
                var bboxGlobal = MakeOutline(start, end, expandGlobalMm);
                var wallsRaw = CollectWallAabbsInOutline(doc, bboxGlobal);

                // ---- 4) Routes candidates ----
                var routes = new List<RouteCandidate>
                {
                    new RouteCandidate { Label = "Route rapide (X→Y→Z)", Points = ForceEndpoints(BuildOrthogonalRoute(start,end,true ).Points, start, end) },
                    new RouteCandidate { Label = "Route rapide (Y→X→Z)", Points = ForceEndpoints(BuildOrthogonalRoute(start,end,false).Points, start, end) }
                };

                int[] stepsFast = XYMarginsToSteps(gridFastMm);
                int[] stepsFine = XYMarginsToSteps(gridFineMm);

                var aFast = AStarMulti(start, end, wallsRaw, gridFastMm, zStepMm, clearMm, exemptMm, stepsFast, 1.0);
                if (aFast != null)
                {
                    aFast.Points = PostProcess(aFast.Points, wallsRaw, clearMm, snapTolMm, jogTolMm, minSegMm);
                    aFast.Points = ForceEndpoints(aFast.Points, start, end);
                    routes.Add(aFast);
                }

                var aFine = AStarMulti(start, end, wallsRaw, gridFineMm, zStepMm, clearMm, exemptMm, stepsFine, 1.06);
                if (aFine != null)
                {
                    aFine.Points = PostProcess(aFine.Points, wallsRaw, clearMm, snapTolMm, jogTolMm, minSegMm);
                    aFine.Points = ForceEndpoints(aFine.Points, start, end);
                    routes.Add(aFine);
                }

                var detours = BuildDetoursAroundBlockingWalls(start, end, wallsRaw, clearMm, detourPadMm);
                foreach (var d in detours)
                {
                    var fixedPts = ForceEndpoints(d.Points, start, end);
                    routes.Add(new RouteCandidate
                    {
                        Label = "Contournement direct",
                        Points = PostProcess(fixedPts, wallsRaw, clearMm, snapTolMm, jogTolMm, minSegMm)
                    });
                }

                if (routes.Count == 0)
                {
                    TaskDialog.Show(UiLanguage.T("Connexion canalisations", "Connect Pipes"), UiLanguage.T("Aucun itinéraire généré.", "No route was generated."));
                    return Result.Cancelled;
                }

                // ---- 5) Aperçu & choix ----
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
                    { tDel.Start(); foreach (var set in previews) if (set.Count > 0) doc.Delete(set); tDel.Commit(); }
                    return Result.Cancelled;
                }
                var route = routes[chosen];

                // ---- 6) Création réseau + raccords (1 transaction) ----
                using (var t = new Transaction(doc, "Connecter canalisations"))
                {
                    t.Start();

                    foreach (var set in previews) if (set.Count > 0) doc.Delete(set);

                    var pts = new List<XYZ>(route.Points);
                    pts[0] = start; pts[pts.Count - 1] = end;

                    // verrous géométriques : pas de pentes, pas de zigzag
                    pts = AxisAlignAndFlatten(pts);
                    RemoveShortSegments(pts, MmToFt(minSegMm));

                    var created = BuildPipePath(doc, pts, ref1);
                    doc.Regenerate();

                    // --- Un seul côté pose un Té s’il y a un fût ---
                    bool startWantsTee = anchorStart.isTee;
                    bool endWantsTee = anchorEnd.isTee;

                    if (startWantsTee && !endWantsTee)
                    {
                        FinishAtAnchor(doc, created.FirstOrDefault(), start, anchorStart,
                                       PipeRefInfo.FromPipe(doc, created.FirstOrDefault() ?? p1));
                        TryElbowTransitionOrLink(doc, created.LastOrDefault(), end, anchorEnd.pipe, anchorEnd.conn,
                                                 PipeRefInfo.FromPipe(doc, created.LastOrDefault() ?? p2));
                    }
                    else if (!startWantsTee && endWantsTee)
                    {
                        TryElbowTransitionOrLink(doc, created.FirstOrDefault(), start, anchorStart.pipe, anchorStart.conn,
                                                 PipeRefInfo.FromPipe(doc, created.FirstOrDefault() ?? p1));
                        FinishAtAnchor(doc, created.LastOrDefault(), end, anchorEnd,
                                       PipeRefInfo.FromPipe(doc, created.LastOrDefault() ?? p2));
                    }
                    else
                    {
                        // cas normal : aucun fût → 2 liaisons ; cas rare : 2 fûts → Té côté 'end'
                        TryElbowTransitionOrLink(doc, created.FirstOrDefault(), start, anchorStart.pipe, anchorStart.conn,
                                                 PipeRefInfo.FromPipe(doc, created.FirstOrDefault() ?? p1));
                        FinishAtAnchor(doc, created.LastOrDefault(), end, anchorEnd,
                                       PipeRefInfo.FromPipe(doc, created.LastOrDefault() ?? p2));
                    }

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

        // ======================== ANCRAGES & RACCORDEMENTS ========================

        private static XYZ GetRoutingAnchor(Document doc, Pipe p, XYZ towards, double dnFt,
            out bool anchorIsTee, out XYZ footForTee, out Connector connIfFree)
        {
            anchorIsTee = false; footForTee = XYZ.Zero; connIfFree = null;

            if (IsMainRun(p))
            {
                double minOff = MmToFt(Math.Max(80, 2.0 * FtToMm(dnFt)));
                if (TryGetSafeBreakPointOnPipe(p, towards, minOff, preferCenter: true, out footForTee))
                {
                    anchorIsTee = true;
                    return footForTee;
                }
            }

            connIfFree = GetBestConnector(p, towards);
            return connIfFree?.Origin ?? GetPipeCenter(p);
        }

        private static void FinishAtAnchor(Document doc, Pipe pathEndPipe, XYZ endPoint,
            (Pipe pipe, bool isTee, XYZ foot, Connector conn) anchor, PipeRefInfo infoForBranch)
        {
            if (pathEndPipe == null || anchor.pipe == null) return;

            if (anchor.isTee)
            {
                if (_teePlacedThisRun)
                {
                    // si un Té a déjà été posé dans cette commande,
                    // on finit par un raccord simple sur le connecteur existant
                    TryElbowTransitionOrLink(doc, pathEndPipe, endPoint, anchor.pipe, anchor.conn, infoForBranch);
                    return;
                }

                BackOffPipeEnd(pathEndPipe, endPoint, MmToFt(15));
                doc.Regenerate();
                var branchEnd = GetEndConnectorClosestToPoint(pathEndPipe, endPoint);
                var hintDir = GetOutgoingDirectionAtEnd(pathEndPipe, anchor.foot);

                if (CreateTeeAtPoint(doc, anchor.pipe, pathEndPipe, branchEnd, anchor.foot, infoForBranch, hintDir))
                    _teePlacedThisRun = true;   // <<< important
            }
            else
            {
                TryElbowTransitionOrLink(doc, pathEndPipe, endPoint, anchor.pipe, anchor.conn, infoForBranch);
            }
        }

        // Pose un Té en privilégiant la connexion directe de la branche ; garde-fou et déduplication
        private static bool CreateTeeAtPoint(
            Document doc,
            Pipe main,
            Pipe branchPipe,
            Connector branchEnd,
            XYZ footDesired,
            PipeRefInfo branchInfo,
            XYZ branchHintDir)

        {
            if (doc == null || main == null || branchPipe == null || branchEnd == null) return false;

            // GARDE-FOU : s'il y a déjà un Té à proximité, on s'y branche
            if (TryConnectToExistingTee(doc, main, branchEnd, footDesired, branchInfo, tolMm: 25.0))
                return true;

            // 1) déterminer un point de cassure sûr (au milieu si possible)
            if (!TryGetSafeBreakPointOnPipe(main, footDesired, MmToFt(0), preferCenter: true, out XYZ foot))
                return false;

            // 2) casser le fût
            ElementId partId;
            try { partId = PlumbingUtils.BreakCurve(doc, main.Id, foot); }
            catch
            {
                var lc0 = main.Location as LocationCurve;
                if (lc0?.Curve == null) return false;
                foot = lc0.Curve.Evaluate(0.5, true);
                partId = PlumbingUtils.BreakCurve(doc, main.Id, foot);
            }
            if (partId == ElementId.InvalidElementId) return false;
            doc.Regenerate();

            var mainA = (Pipe)doc.GetElement(main.Id);
            var mainB = (Pipe)doc.GetElement(partId);
            var cA = GetEndConnectorClosestToPoint(mainA, foot);
            var cB = GetEndConnectorClosestToPoint(mainB, foot);
            if (cA == null || cB == null) return false;

            // 3) tentatives de Té direct en utilisant le connecteur de la branche
            var orders = new List<(Connector, Connector)> { (cA, cB), (cB, cA) };

            foreach (var (x, y) in orders)
            {
                FamilyInstance tee = null;
                try
                {
                    doc.Create.NewTeeFitting(x, y, branchEnd);
                    doc.Regenerate();
                    tee = FindFittingCreatedAt(doc, foot, BuiltInCategory.OST_PipeFitting);
                }
                catch { tee = null; }

                if (tee != null)
                {
                    // Si la branche est déjà connectée au té : OK
                    if (IsConnectorConnectedToElement(branchEnd, tee.Id))
                    {
                        DeduplicateTeesAtPoint(doc, foot); // sécurité
                        _teePlacedThisRun = true;
                        return true;
                    }

                    // Sinon, on récupère le connecteur "branche" du té et on connecte explicitement
                    var dirMain = GetCurveDirection(mainA);
                    var cBranchOnTee = GetBranchConnectorFromFitting(tee, dirMain);
                    if (cBranchOnTee != null)
                    {
                        try
                        {
                            double dnMain = main.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                            if (NearlyEqual(branchInfo.Diameter, dnMain))
                                doc.Create.NewElbowFitting(branchEnd, cBranchOnTee);
                            else
                                doc.Create.NewTransitionFitting(cBranchOnTee, branchEnd);

                            doc.Regenerate();
                            if (IsConnectorConnectedToElement(branchEnd, tee.Id))
                            {
                                DeduplicateTeesAtPoint(doc, foot);
                                return true;
                            }
                        }
                        catch { /* on tentera autre chose */ }
                    }

                    // orientation/connexion pas bonne → suppression et essai ordre inverse
                    try { doc.Delete(tee.Id); } catch { }
                    doc.Regenerate();
                }
            }

            // 4) fallback : stub perpendiculaire + raccords (solution sûre)

            bool ok = CreateTeeWithOrientedStubFallback(doc, mainA, mainB, branchPipe, branchEnd, foot, branchInfo, branchHintDir);
            if (ok)
            {
                DeduplicateTeesAtPoint(doc, foot);
                _teePlacedThisRun = true;
            }
            return ok;
        }

        // Essaie de se connecter à un Té déjà présent près du point
        private static bool TryConnectToExistingTee(
            Document doc, Pipe main, Connector branchEnd, XYZ near, PipeRefInfo branchInfo, double tolMm = 3.0)
        {
            if (doc == null || main == null || branchEnd == null) return false;

            double tol = MmToFt(tolMm);
            var tees = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_PipeFitting).OfClass(typeof(FamilyInstance))
    .Cast<FamilyInstance>()
    .Where(fi =>
    {
        var bb = fi.get_BoundingBox(null);
        if (bb == null) return false;
        var c = (bb.Min + bb.Max) * 0.5;
        return c.DistanceTo(near) <= tol && IsTeeFitting(fi);
    })
    .ToList();


            if (tees.Count == 0) return false;

            var tee = tees.OrderBy(fi =>
            {
                var bb = fi.get_BoundingBox(null); var c = (bb.Min + bb.Max) * 0.5;
                return c.DistanceTo(near);
            }).First();

            var mainDir = GetCurveDirection(main);
            var cBranchOnTee = GetBranchConnectorFromFitting(tee, mainDir);
            if (cBranchOnTee == null) return false;

            try
            {
                double dnMain = main.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                if (NearlyEqual(branchInfo.Diameter, dnMain))
                    doc.Create.NewElbowFitting(branchEnd, cBranchOnTee);
                else
                    doc.Create.NewTransitionFitting(cBranchOnTee, branchEnd);

                doc.Regenerate();
                return true;
            }
            catch
            {
                try { MakeShortLinkAndElbows(doc, branchEnd, cBranchOnTee, branchInfo); doc.Regenerate(); return true; }
                catch { return false; }
            }
        }

        // Supprime les Tés en double autour d'un point (garde le plus connecté)
        private static void DeduplicateTeesAtPoint(Document doc, XYZ near, double tolMm = 25.0) // 25 mm au lieu de 3
        {
            double tol = MmToFt(tolMm);

            // Récupère tous les fittings proche du point
            var tees = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi =>
                {
                    var bb = fi.get_BoundingBox(null);
                    if (bb == null) return false;
                    var c = (bb.Min + bb.Max) * 0.5;
                    return c.DistanceTo(near) <= tol && IsTeeFitting(fi);
                })
                .ToList();

            if (tees.Count <= 1) return;

            // Garde celui qui a le plus de connecteurs Piping déjà connectés
            FamilyInstance keep = tees
                .OrderByDescending(fi =>
                {
                    try
                    {
                        return fi.MEPModel.ConnectorManager.Connectors
                            .Cast<Connector>()
                            .Count(c => c.Domain == Domain.DomainPiping && c.IsConnected);
                    }
                    catch { return 0; }
                })
                .First();

            foreach (var t in tees)
                if (t.Id != keep.Id)
                    try { doc.Delete(t.Id); } catch { }

            doc.Regenerate();
        }

        // Détection robuste d'un Té (3 connecteurs Piping, 2 opposés et 1 orthogonal)
        private static bool IsTeeFitting(FamilyInstance fi)
        {
            try
            {
                var conns = fi.MEPModel.ConnectorManager.Connectors
                    .Cast<Connector>()
                    .Where(c => c.Domain == Domain.DomainPiping)
                    .ToList();
                if (conns.Count != 3) return false;

                var axes = conns.Select(c => SafeAxisZ(c).Normalize()).ToList();

                // cherche un connecteur 'branche' presque orthogonal aux deux autres
                for (int i = 0; i < 3; i++)
                {
                    var a = axes[i];
                    var b = axes[(i + 1) % 3];
                    var c = axes[(i + 2) % 3];

                    bool ortho = Math.Abs(a.DotProduct(b)) < 0.1 && Math.Abs(a.DotProduct(c)) < 0.1;
                    bool opp = Math.Abs(b.DotProduct(c) + 1) < 0.1; // b ~ -c

                    if (ortho && opp) return true;
                }
                return false;
            }
            catch { return false; }
        }


        private static int CountPipingConnectors(FamilyInstance fi)
        {
            try
            {
                return fi?.MEPModel?.ConnectorManager?.Connectors
                           .Cast<Connector>()
                           .Count(c => c.Domain == Domain.DomainPiping) ?? 0;
            }
            catch { return 0; }
        }

        private static Connector GetBranchConnectorFromFitting(FamilyInstance teeFi, XYZ mainDir)
        {
            if (teeFi?.MEPModel?.ConnectorManager == null) return null;
            Connector best = null; double bestScore = -1.0;
            foreach (Connector c in teeFi.MEPModel.ConnectorManager.Connectors)
            {
                if (c.Domain != Domain.DomainPiping) continue;
                var z = SafeAxisZ(c);
                // branche = axe le plus orthogonal au fût
                double s = 1.0 - Math.Abs(z.DotProduct(mainDir.Normalize()));
                if (s > bestScore) { bestScore = s; best = c; }
            }
            return best;
        }

        private static bool IsConnectorConnectedToElement(Connector c, ElementId ownerId)
        {
            try
            {
                foreach (Connector r in c.AllRefs)
                    if (r.Owner != null && r.Owner.Id == ownerId)
                        return true;
            }
            catch { }
            return false;
        }

        private static bool CreateTeeWithOrientedStubFallback(
            Document doc,
            Pipe mainA, Pipe mainB,
            Pipe branchPipe, Connector branchEnd,
            XYZ foot,
            PipeRefInfo branchInfo,
            XYZ branchHintDir)
        {
            var main = mainA ?? mainB;
            if (main == null) return false;

            var mainInfo = PipeRefInfo.FromPipe(doc, main);
            double dnMain = mainInfo.Diameter;

            XYZ dirMain = GetCurveDirection(mainA ?? mainB);
            XYZ tgtDir = (branchHintDir != null && branchHintDir.GetLength() > 1e-9)
                            ? branchHintDir
                            : (branchEnd != null ? (branchEnd.Origin - foot) : GetCurveDirection(branchPipe));

            // composante perpendiculaire au fût
            XYZ stubDir = tgtDir - dirMain.Multiply(tgtDir.DotProduct(dirMain));
            if (stubDir.GetLength() < 1e-6)
                stubDir = dirMain.CrossProduct(Math.Abs(dirMain.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX);
            stubDir = stubDir.Normalize();
            if (stubDir.DotProduct(tgtDir) < 0) stubDir = stubDir.Negate();
            if (Math.Abs(dirMain.Z) < 0.15 && Math.Abs(stubDir.Z) < 0.25)
                stubDir = new XYZ(stubDir.X, stubDir.Y, 0).Normalize();

            double stubLen = MmToFt(90);
            XYZ stubEnd = foot + stubDir * stubLen;

            var stub = Pipe.Create(doc, mainInfo.SystemTypeId, mainInfo.PipeTypeId, mainInfo.LevelId, foot, stubEnd);
            var pDiam = stub.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (pDiam != null && pDiam.StorageType == StorageType.Double) pDiam.Set(dnMain);
            doc.Regenerate();

            var cMainA = GetEndConnectorClosestToPoint(mainA, foot);
            var cMainB = GetEndConnectorClosestToPoint(mainB, foot);
            var cNear = GetEndConnectorClosestToPoint(stub, foot);
            var cFar = GetEndConnectorClosestToPoint(stub, stubEnd);

            try { doc.Create.NewTeeFitting(cMainA, cMainB, cNear); }
            catch { return false; }
            doc.Regenerate();

            if (branchEnd != null)
            {
                if (NearlyEqual(branchInfo.Diameter, dnMain))
                {
                    try { doc.Create.NewElbowFitting(branchEnd, cFar); }
                    catch { try { branchEnd.ConnectTo(cFar); } catch { MakeShortLinkAndElbows(doc, branchEnd, cFar, branchInfo); } }
                }
                else
                {
                    try { doc.Create.NewTransitionFitting(cFar, branchEnd); }
                    catch { MakeShortLinkAndElbows(doc, branchEnd, cFar, branchInfo); }
                }
            }
            return true;
        }

        private static void TryElbowTransitionOrLink(Document doc, Pipe pathEndPipe, XYZ endPoint,
                                                     Pipe targetPipe, Connector targetConn, PipeRefInfo infoForBranch)
        {
            if (pathEndPipe == null || targetConn == null) return;

            var own = GetEndConnectorClosestToPoint(pathEndPipe, endPoint);
            if (own == null) return;

            double dnA = infoForBranch.Diameter;
            double dnB = GetPipeDiameter(targetPipe);

            try { doc.Create.NewElbowFitting(own, targetConn); return; } catch { }
            try { own.ConnectTo(targetConn); return; } catch { }

            if (!NearlyEqual(dnA, dnB))
            {
                try { doc.Create.NewTransitionFitting(own, targetConn); return; } catch { }
            }

            XYZ dir = GetPreferredDirection(targetConn, own);
            double L = MmToFt(120);
            var link = Pipe.Create(doc, infoForBranch.SystemTypeId, infoForBranch.PipeTypeId, infoForBranch.LevelId,
                                   own.Origin, own.Origin + dir * L);
            var d = link.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (d != null && d.StorageType == StorageType.Double) d.Set(infoForBranch.Diameter);
            doc.Regenerate();

            var c1 = GetEndConnectorClosestToPoint(link, own.Origin);
            var c2 = GetEndConnectorClosestToPoint(link, own.Origin + dir * L);

            try { doc.Create.NewElbowFitting(own, c1); } catch { try { own.ConnectTo(c1); } catch { } }

            if (!NearlyEqual(dnA, dnB))
            {
                try { doc.Create.NewTransitionFitting(c2, targetConn); return; }
                catch { try { c2.ConnectTo(targetConn); return; } catch { } }
            }
            else
            {
                try { doc.Create.NewElbowFitting(c2, targetConn); return; }
                catch { try { c2.ConnectTo(targetConn); return; } catch { } }
            }
        }

        private static void MakeShortLinkAndElbows(Document doc, Connector fromBranch, Connector toStub, PipeRefInfo branchInfo)
        {
            XYZ dir = (toStub.Origin - fromBranch.Origin);
            if (Math.Abs(dir.Z) < 1e-6) dir = new XYZ(dir.X, dir.Y, 0);
            if (dir.GetLength() < 1e-9) dir = SafeAxisZ(toStub);
            dir = dir.Normalize();

            double L = MmToFt(120);
            var link = Pipe.Create(doc, branchInfo.SystemTypeId, branchInfo.PipeTypeId, branchInfo.LevelId,
                                   fromBranch.Origin, fromBranch.Origin + dir * L);
            var d = link.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (d != null && d.StorageType == StorageType.Double) d.Set(branchInfo.Diameter);
            doc.Regenerate();

            var c1 = GetEndConnectorClosestToPoint(link, fromBranch.Origin);
            var c2 = GetEndConnectorClosestToPoint(link, fromBranch.Origin + dir * L);

            try { doc.Create.NewElbowFitting(fromBranch, c1); } catch { try { fromBranch.ConnectTo(c1); } catch { } }
            try { doc.Create.NewElbowFitting(c2, toStub); } catch { try { c2.ConnectTo(toStub); } catch { } }
        }

        private static void BackOffPipeEnd(Pipe p, XYZ toward, double backOffFt)
        {
            var lc = p?.Location as LocationCurve;
            if (lc?.Curve == null) return;
            var a = lc.Curve.GetEndPoint(0);
            var b = lc.Curve.GetEndPoint(1);
            bool endIsB = b.DistanceTo(toward) <= a.DistanceTo(toward);
            var from = endIsB ? a : b;
            var to = endIsB ? b : a;
            var dir = (to - from);
            double len = dir.GetLength();
            if (len < 1e-9 || backOffFt <= 0 || backOffFt >= len * 0.5) return;
            dir = dir / len;

            var newEnd = from + dir * (len - backOffFt);
            if (endIsB) lc.Curve = Line.CreateBound(a, newEnd);
            else lc.Curve = Line.CreateBound(newEnd, b);
        }

        private static XYZ SafeAxisZ(Connector c)
        {
            try { var cs = c.CoordinateSystem; if (cs != null) return cs.BasisZ.Normalize(); } catch { }
            return XYZ.BasisX;
        }

        private static bool IsMainRun(Pipe p)
        {
            int endsConnected = 0;
            foreach (Connector c in p.ConnectorManager.Connectors)
                if (c.ConnectorType == ConnectorType.End && c.IsConnected) endsConnected++;
            return endsConnected >= 2;
        }

        private static bool TryGetSafeBreakPointOnPipe(Pipe pipe, XYZ hint, double minOffset, bool preferCenter, out XYZ foot)
        {
            foot = XYZ.Zero;
            var lc = pipe?.Location as LocationCurve;
            if (lc?.Curve == null) return false;

            var curve = lc.Curve;
            var res = curve.Project(hint);
            double tN;

            if (res != null)
            {
                tN = curve.ComputeNormalizedParameter(res.Parameter);
                if (double.IsNaN(tN) || double.IsInfinity(tN)) tN = 0.5;
            }
            else tN = 0.5;

            double len = curve.Length;
            double minT = (len > 1e-9) ? Math.Min(0.49, minOffset / len) : 0.05;

            if (preferCenter && (tN < minT || tN > 1.0 - minT)) tN = 0.5;
            else { if (tN < minT) tN = minT; if (tN > 1.0 - minT) tN = 1.0 - minT; }

            double raw = curve.ComputeRawParameter(tN);
            foot = curve.Evaluate(raw, false);
            return true;
        }

        private static double GetPipeDiameter(Pipe p)
        {
            if (p == null) return 0.0;
            return p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0.0;
        }

        private static XYZ GetOutgoingDirectionAtEnd(Pipe pathEndPipe, XYZ foot)
        {
            var lc = pathEndPipe?.Location as LocationCurve;
            if (lc?.Curve == null) return XYZ.BasisX;
            XYZ a = lc.Curve.GetEndPoint(0);
            XYZ b = lc.Curve.GetEndPoint(1);
            double da = foot.DistanceTo(a), db = foot.DistanceTo(b);
            return (da <= db) ? (b - a).Normalize() : (a - b).Normalize();
        }

        private static XYZ GetCurveDirection(Pipe p)
        {
            var lc = p.Location as LocationCurve; if (lc?.Curve == null) return XYZ.BasisX;
            return (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize();
        }

        // ======================== SÉLECTION & INFOS PIPE ========================

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
        private static Connector GetBestConnector(Pipe pipe, XYZ towards)
        {
            var cm = pipe.ConnectorManager; if (cm == null) return null;
            Connector bestFree = null, bestAny = null; double dF = double.MaxValue, dA = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                if (c.Domain != Domain.DomainPiping) continue;
                if (c.ConnectorType != ConnectorType.End) continue;
                double d = c.Origin.DistanceTo(towards);
                if (!c.IsConnected && d < dF) { dF = d; bestFree = c; }
                if (d < dA) { dA = d; bestAny = c; }
            }
            return bestFree ?? bestAny;
        }

        // =========================== OBSTACLES & A* ===========================

        private static Outline MakeOutline(XYZ a, XYZ b, double inflateMm)
        {
            double inf = MmToFt(inflateMm);
            return new Outline(
                new XYZ(Math.Min(a.X, b.X) - inf, Math.Min(a.Y, b.Y) - inf, Math.Min(a.Z, b.Z) - inf),
                new XYZ(Math.Max(a.X, b.X) + inf, Math.Max(a.Y, b.Y) + inf, Math.Max(a.Z, b.Z) + inf));
        }

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

            var min = new XYZ(Math.Min(start.X, end.X) - xyMarginSteps * step, Math.Min(start.Y, end.Y) - xyMarginSteps * step, minZ);
            var max = new XYZ(Math.Max(start.X, end.X) + xyMarginSteps * step, Math.Max(start.Y, end.Y) + xyMarginSteps * step, maxZ);

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
                    return new RouteCandidate { Label = "Route évite-objets (A*)", Points = path };
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
                        open.Add(new Node(np, gTent, f));
                    }
                }
            }
            return null;

            static List<XYZ> Reconstruct(Dictionary<XYZ, XYZ> came, XYZ cur)
            {
                var list = new List<XYZ> { cur };
                while (came.TryGetValue(cur, out var prev)) { list.Add(prev); cur = prev; }
                list.Reverse(); return list;
            }
        }

        private static List<BoundingBoxXYZ> FilterAabbsToRange(List<BoundingBoxXYZ> boxes, XYZ min, XYZ max, double pad)
        {
            var outList = new List<BoundingBoxXYZ>();
            foreach (var b in boxes)
                if (!((b.Max.X + pad < min.X) || (b.Min.X - pad > max.X)
                   || (b.Max.Y + pad < min.Y) || (b.Min.Y - pad > max.Y)
                   || (b.Max.Z + pad < min.Z) || (b.Min.Z - pad > max.Z))) outList.Add(b);
            return outList;
        }

        private static bool IsFree(XYZ p, List<BoundingBoxXYZ> obs, double cl)
        {
            foreach (var b in obs)
                if (p.X >= b.Min.X - cl && p.X <= b.Max.X + cl &&
                    p.Y >= b.Min.Y - cl && p.Y <= b.Max.Y + cl &&
                    p.Z >= b.Min.Z - cl && p.Z <= b.Max.Z + cl) return false;
            return true;
        }

        // =========================== ROUTES UTILITAIRES ===========================

        private class RouteCandidate { public string Label; public List<XYZ> Points; }

        private static RouteCandidate BuildOrthogonalRoute(XYZ start, XYZ end, bool preferXY)
        {
            var pts = new List<XYZ> { start }; XYZ p = start;
            if (preferXY) { p = new XYZ(end.X, p.Y, p.Z); pts.Add(p); p = new XYZ(p.X, end.Y, p.Z); pts.Add(p); }
            else { p = new XYZ(p.X, end.Y, p.Z); pts.Add(p); p = new XYZ(end.X, p.Y, p.Z); pts.Add(p); }
            p = new XYZ(p.X, p.Y, end.Z); pts.Add(p);
            CleanupCollinear(pts);
            return new RouteCandidate { Label = preferXY ? "Route rapide (X→Y→Z)" : "Route rapide (Y→X→Z)", Points = pts };
        }

        private static List<RouteCandidate> BuildDetoursAroundBlockingWalls(XYZ start, XYZ end, List<BoundingBoxXYZ> wallsRaw, double clearMm, double padMm)
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

            void add(List<XYZ> opt) { CleanupDuplicates(opt); CleanupCollinear(opt); if (IsPolylineClear(opt, wallsRaw, MmToFt(clearMm))) list.Add(new RouteCandidate { Label = "Contournement direct", Points = opt }); }
            add(optBelow); add(optAbove); add(optLeft); add(optRight);
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

        // ======================== LISSAGE / SNAP / CLEAR ========================

        private static List<XYZ> PostProcess(List<XYZ> pts, List<BoundingBoxXYZ> wallsRaw, double clearMm, double snapTolMm, double jogTolMm, double minSegMm)
        {
            if (pts == null || pts.Count == 0) return pts;

            double clearFt = MmToFt(clearMm);
            var p = SmoothRectilinear(pts, wallsRaw, clearFt);
            p = AxisAlignAndFlatten(p); // pas de mini-pentes
            p = SnapPolylineToWallOffsets(p, wallsRaw, clearMm, snapTolMm);
            p = CollapseAdjacentCorners(p, wallsRaw, clearFt, jogTolMm);
            p = CollapseZigZagDoglegs(p, wallsRaw, clearFt, doglegTolMm: Math.Max(120, jogTolMm)); // supprime les “Z”

            CleanupDuplicates(p); CleanupCollinear(p);
            RemoveShortSegments(p, MmToFt(minSegMm));
            return p;
        }

        // Aligne chaque segment sur un seul axe et aplatit Z sur les horizontaux
        private static List<XYZ> AxisAlignAndFlatten(List<XYZ> pts, double flatEpsMm = 0.5)
        {
            if (pts == null || pts.Count < 2) return pts;

            double flatEps = MmToFt(flatEpsMm);
            var outPts = new List<XYZ> { pts[0] };

            for (int i = 0; i < pts.Count - 1; i++)
            {
                var a = outPts[outPts.Count - 1];
                var b = pts[i + 1];

                double dx = Math.Abs(b.X - a.X);
                double dy = Math.Abs(b.Y - a.Y);
                double dz = Math.Abs(b.Z - a.Z);

                XYZ nb;

                if (dz < flatEps && (dx > 1e-9 || dy > 1e-9))
                {
                    if (dx >= dy) nb = new XYZ(b.X, a.Y, a.Z);
                    else nb = new XYZ(a.X, b.Y, a.Z);
                }
                else
                {
                    if (dx >= dy && dx >= dz) nb = new XYZ(b.X, a.Y, a.Z);
                    else if (dy >= dx && dy >= dz) nb = new XYZ(a.X, b.Y, a.Z);
                    else nb = new XYZ(a.X, a.Y, b.Z);
                }

                outPts.Add(nb);
            }

            CleanupDuplicates(outPts);
            CleanupCollinear(outPts);
            return outPts;
        }

        // Supprime les “Z” courts (deux coudes rapprochés) en un seul coin
        private static List<XYZ> CollapseZigZagDoglegs(
            List<XYZ> pts,
            List<BoundingBoxXYZ> wallsRaw,
            double clearFt,
            double doglegTolMm = 120)
        {
            if (pts == null || pts.Count < 4) return pts;
            double tol = MmToFt(doglegTolMm);

            bool IsAxis(XYZ v) => Math.Abs(v.X) < 1e-9 || Math.Abs(v.Y) < 1e-9 || Math.Abs(v.Z) < 1e-9;
            bool Parallel(XYZ a, XYZ b) => a.CrossProduct(b).GetLength() <= 1e-9;
            bool Perp(XYZ a, XYZ b) => Math.Abs(a.Normalize().DotProduct(b.Normalize())) <= 1e-6;

            int i = 0;
            while (i < pts.Count - 3)
            {
                var a = pts[i];
                var b = pts[i + 1];
                var c = pts[i + 2];
                var d = pts[i + 3];

                var u = b - a; var v = c - b; var w = d - c;

                if (IsAxis(u) && IsAxis(v) && IsAxis(w) &&
                    Parallel(u, w) && Perp(u, v) && Perp(v, w) &&
                    b.DistanceTo(c) <= tol)
                {
                    double z = (a.Z + d.Z) * 0.5;
                    var p1 = new List<XYZ> { a, new XYZ(a.X, d.Y, z), d };
                    var p2 = new List<XYZ> { a, new XYZ(d.X, a.Y, z), d };

                    if (IsPolylineClear(p1, wallsRaw, clearFt)) { pts[i + 1] = p1[1]; pts.RemoveAt(i + 2); continue; }
                    if (IsPolylineClear(p2, wallsRaw, clearFt)) { pts[i + 1] = p2[1]; pts.RemoveAt(i + 2); continue; }
                }
                i++;
            }

            CleanupDuplicates(pts);
            CleanupCollinear(pts);
            return pts;
        }

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
                    if (Math.Abs(p.X - x1) <= tol) p = new XYZ(x1, p.Y, p.Z);
                    else if (Math.Abs(p.X - x2) <= tol) p = new XYZ(x2, p.Y, p.Z);

                    double y1 = w.Min.Y - clear, y2 = w.Max.Y + clear;
                    if (Math.Abs(p.Y - y1) <= tol) p = new XYZ(p.X, y1, p.Z);
                    else if (Math.Abs(p.Y - y2) <= tol) p = new XYZ(p.X, y2, p.Z);
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

            bool Clip(double p, double q, ref double tt0, ref double tt1)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0; double r = q / p;
                if (p < 0) { if (r > tt1) return false; if (r > tt0) tt0 = r; }
                else { if (r < tt0) return false; if (r < tt1) tt1 = r; }
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

        private static bool SegmentAabbIntersect2D(XYZ p0, XYZ p1, XYZ bmin, XYZ bmax)
        {
            double x0 = p0.X, y0 = p0.Y, x1 = p1.X, y1 = p1.Y;
            const int INSIDE = 0, LEFT = 1, RIGHT = 2, BOTTOM = 4, TOP = 8;
            int Code(double x, double y) { int c = INSIDE; if (x < bmin.X) c |= LEFT; else if (x > bmax.X) c |= RIGHT; if (y < bmin.Y) c |= BOTTOM; else if (y > bmax.Y) c |= TOP; return c; }
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

        // =========================== APERÇU & CRÉATION ===========================

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
                var r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, filter, UiLanguage.T("Clique la ligne colorée de l'itinéraire", "Click the route's coloured line"));
                var id = r.ElementId;
                for (int i = 0; i < sets.Count; i++) if (sets[i].Contains(id)) return i;
            }
            catch { }
            return -1;
        }

        private class PreviewSelFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            private readonly HashSet<ElementId> _ok; public PreviewSelFilter(HashSet<ElementId> ok) { _ok = ok; }
            public bool AllowElement(Element e) => _ok.Contains(e.Id);
            public bool AllowReference(Reference r, XYZ p) => true;
        }

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

        private static XYZ GetPreferredDirection(Connector baseConn, Connector toward)
        {
            try
            {
                var cs = baseConn.CoordinateSystem;
                if (cs != null)
                {
                    var v = cs.BasisZ;
                    if (v != null && v.GetLength() > 1e-9) return v.Normalize();
                }
            }
            catch { }
            var d = (toward.Origin - baseConn.Origin);
            if (Math.Abs(d.Z) < 1e-6) d = new XYZ(d.X, d.Y, 0);
            if (d.GetLength() < 1e-9) d = XYZ.BasisX;
            return d.Normalize();
        }

        // ============================== HELPERS ==============================

        private static List<XYZ> ForceEndpoints(List<XYZ> pts, XYZ start, XYZ end)
        {
            if (pts == null || pts.Count == 0) return new List<XYZ> { start, end };
            pts[0] = start; pts[pts.Count - 1] = end;
            CleanupDuplicates(pts); CleanupCollinear(pts);
            return pts;
        }

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
        private static int[] XYMarginsToSteps(double gridMm)
        {
            return XY_MARGINS_MM.Select(m => Math.Max(6, (int)Math.Round(m / gridMm))).ToArray();
        }
        // cherche un fitting créé autour d’un point (après Break + NewTeeFitting)
        private static FamilyInstance FindFittingCreatedAt(Document doc, XYZ near, BuiltInCategory cat, double tolMm = 2.0)
        {
            double tol = MmToFt(tolMm);
            return new FilteredElementCollector(doc)
                .OfCategory(cat).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .FirstOrDefault(fi =>
                {
                    var bb = fi.get_BoundingBox(null);
                    if (bb == null) return false;
                    var c = (bb.Min + bb.Max) * 0.5;
                    return c.DistanceTo(near) <= tol;
                });
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
        private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));
        private static bool NearlyEqual(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;
    }
}

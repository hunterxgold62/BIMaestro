using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.UI.Selection;
using Licensing; // BaseTrackedCommand

namespace Modification
{
    /// <summary>
    /// Retire les brides autour d’accessoires/équipements (piping & HVAC),
    /// traverse les petits fittings en ligne (réductions, manchons, unions)
    /// jusqu’au 1er MEPCurve (pipe/duct), étend/coupe proprement,
    /// connecte l’élément, puis supprime bride + chaîne skippée.
    /// Rollback intégral si échec.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RemoveFlangesReconnect : BaseTrackedCommand
    {
        protected override string ButtonId => "RemoveFlangesReconnect";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = data.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            UIApplication uiApp = data.Application;

            // Auto-Yes pour les boîtes de dialogue de déconnexion réseau
            EventHandler<DialogBoxShowingEventArgs> autoYes = (s, e) =>
            {
                if (e is TaskDialogShowingEventArgs td)
                    td.OverrideResult((int)TaskDialogResult.Yes);
            };
            uiApp.DialogBoxShowing += autoYes;

            try
            {
                // --- Sélection ---
                var ids = uiDoc.Selection.GetElementIds().ToList();
                if (ids.Count == 0)
                {
                    try
                    {
                        var picked = uiDoc.Selection.PickObjects(
                            ObjectType.Element, new SelectFilterForRemove(),
                            "Sélectionne les accessoires/équipements (piping/HVAC) à épurer (retrait de brides).");
                        ids = picked.Select(r => r.ElementId).ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                var targets = ids
                    .Select(id => doc.GetElement(id))
                    .OfType<FamilyInstance>()
                    .Where(HasPipeOrHvacConnectors)
                    .ToList();

                if (targets.Count == 0)
                {
                    TaskDialog.Show("Retrait de brides", "Aucun élément MEP (piping/HVAC) valide dans la sélection.");
                    return Result.Cancelled;
                }

                int removedFlanges = 0, reconnected = 0, skipped = 0, removedReducers = 0, removedInline = 0, movedReducers = 0;

                using (var t = new Transaction(doc, "Retirer brides + reconnecter (skip réductions/manchons)"))
                {
                    t.Start();
                    SuppressWarnings(t);

                    foreach (var fi in targets)
                        using (new ElementPinScope(fi))
                        {
                            foreach (var elemConn in GetMEPEndConnectors(fi).ToList())
                            {
                                // 1) Bride adjacente à ce connecteur ?
                                if (!TryGetNeighborFlange(elemConn, fi.Id, out FamilyInstance flange, out Connector flangeConnOnElemSide))
                                    continue;

                                var domain = DominantDomain(elemConn);
                                var flangeEndConns = GetFamilyEndConnectorsByDomain(flange, domain).ToList();
                                if (flangeEndConns.Count == 0) { skipped++; continue; }

                                if (flangeConnOnElemSide == null)
                                    flangeConnOnElemSide = flangeEndConns.FirstOrDefault(c => IsConnectedTo(c, elemConn));

                                var flangeOtherSide = flangeEndConns.FirstOrDefault(c => c.Id != (flangeConnOnElemSide?.Id ?? -1))
                                                   ?? flangeEndConns.FirstOrDefault();
                                if (flangeConnOnElemSide == null || flangeOtherSide == null) { skipped++; continue; }

                                // 2) Déterminer la cible côté opposé : traverser les “inline fittings” skippables jusqu'au 1er MEPCurve
                                if (!TryFindTerminalCurveFrom(flangeOtherSide, flange.Id, domain,
                                                              out MEPCurve terminalCurve,
                                                              out Connector terminalCurveConn,
                                                              out List<ElementId> skippables /*réductions/manchons*/, maxHops: 8))
                                {
                                    skipped++;
                                    continue;
                                }

                                // Sauvegardes pour rollback
                                var back_elem_flange = (a: elemConn, b: flangeConnOnElemSide);
                                var back_term_flange = (a: flangeOtherSide, b: terminalCurveConn);
                                var moved = new List<(ElementId id, XYZ delta)>();

                                bool success = false;

                                try
                                {
                                    // 3) Déconnecter bride des deux côtés (et skippables si connectés)
                                    SafeDisconnect(back_elem_flange.a, back_elem_flange.b);
                                    SafeDisconnect(back_term_flange.a, back_term_flange.b);
                                    foreach (var sid in skippables)
                                    {
                                        var sfe = doc.GetElement(sid) as FamilyInstance;
                                        if (sfe?.MEPModel?.ConnectorManager != null)
                                        {
                                            var pairs = GetAllDomainPairs(sfe, domain);
                                            foreach (var p in pairs) SafeDisconnect(p.a, p.b);
                                        }
                                    }
                                    doc.Regenerate();

                                    // 4) Amener l’extrémité du MEPCurve au connecteur de l’élément
                                    var elemPt = SafeOrigin(elemConn);
                                    var curveRefPt = SafeOrigin(terminalCurveConn);
                                    ExtendMEPCurveEndToPoint(terminalCurve, elemPt, curveRefPt);
                                    doc.Regenerate();

                                    // 5) Connecter élément ↔ curve
                                    var near = GetClosestMEPConnector(terminalCurve, domain, elemPt) ?? terminalCurveConn;
                                    success = ConnectWithAutoFlip(elemConn, near);

                                    if (!success)
                                    {
                                        // Micro-correctif : petit move vers l’élément pour décoincer des contraintes
                                        foreach (var sid in skippables)
                                        {
                                            var inst = doc.GetElement(sid) as FamilyInstance;
                                            if (inst == null) continue;
                                            var cm = inst.MEPModel?.ConnectorManager;
                                            if (cm == null) continue;

                                            var anyConn = cm.Connectors.Cast<Connector>().Select(SafeOrigin).FirstOrDefault();
                                            var vec = elemPt - anyConn;
                                            if (!IsZero(vec))
                                            {
                                                XYZ delta = vec.Normalize().Multiply(0.0001); // 0,1 mm
                                                ElementTransformUtils.MoveElement(doc, sid, delta);
                                                moved.Add((sid, delta));
                                            }
                                        }
                                        doc.Regenerate();

                                        near = GetClosestMEPConnector(terminalCurve, domain, elemPt) ?? terminalCurveConn;
                                        success = ConnectWithAutoFlip(elemConn, near);
                                    }

                                    if (!success)
                                    {
                                        // rollback
                                        foreach (var mv in Enumerable.Reverse(moved))
                                            try { ElementTransformUtils.MoveElement(doc, mv.id, -mv.delta); } catch { }

                                        TryConnectQuiet(back_elem_flange.a, back_elem_flange.b);
                                        TryConnectQuiet(back_term_flange.a, back_term_flange.b);
                                        skipped++;
                                        continue;
                                    }

                                    // 6) Connexion OK -> supprimer bride + skippables devenus inutiles
                                    doc.Delete(flange.Id);
                                    removedFlanges++;

                                    foreach (var sid in skippables)
                                    {
                                        var sfe = doc.GetElement(sid) as FamilyInstance;
                                        if (sfe == null) continue;

                                        if (!IsAnyConnectorConnected(sfe))
                                        {
                                            doc.Delete(sid);
                                            removedInline++;
                                        }
                                        else if (IsReducerFI(sfe, domain))
                                        {
                                            var ends = GetFamilyEndConnectorsByDomain(sfe, domain).ToList();
                                            var countConn = ends.Count(e => IsConnectorConnected(e));
                                            if (countConn <= 1)
                                            {
                                                doc.Delete(sfe.Id);
                                                removedReducers++;
                                            }
                                            else
                                            {
                                                movedReducers++;
                                            }
                                        }
                                    }

                                    reconnected++;
                                    doc.Regenerate();
                                }
                                catch
                                {
                                    // Rollback global
                                    TryConnectQuiet(back_elem_flange.a, back_elem_flange.b);
                                    TryConnectQuiet(back_term_flange.a, back_term_flange.b);
                                    skipped++;
                                }
                            }
                        }

                    doc.Regenerate();
                    t.Commit();
                }

                TaskDialog.Show("Retrait de brides",
                    $"Brides supprimées : {removedFlanges}\n" +
                    $"Réductions supprimées : {removedReducers}\n" +
                    $"Inline fittings supprimés : {removedInline}\n" +
                    $"Réductions déplacées/maintenues : {movedReducers}\n" +
                    $"Connexions rétablies : {reconnected}\n" +
                    $"Ignorées/échouées : {skipped}");

                return Result.Succeeded;
            }
            finally
            {
                uiApp.DialogBoxShowing -= autoYes;
            }
        }

        // ---------- Sélection ----------
        private class SelectFilterForRemove : ISelectionFilter
        {
            public bool AllowElement(Element e)
            {
                if (e == null || e.Category == null) return false;

                int cat = e.Category.Id.IntegerValue;
                bool catOk =
                    cat == (int)BuiltInCategory.OST_PipeAccessory ||
                    cat == (int)BuiltInCategory.OST_DuctAccessory ||
                    cat == (int)BuiltInCategory.OST_MechanicalEquipment;

                if (!catOk) return false;

                var fi = e as FamilyInstance;
                return fi?.MEPModel?.ConnectorManager != null && HasPipeOrHvacConnectors(fi);
            }

            public bool AllowReference(Reference r, XYZ p) => false;
        }

        // ---------- Helpers MEP ----------
        private static bool HasPipeOrHvacConnectors(FamilyInstance fi)
        {
            var cm = fi?.MEPModel?.ConnectorManager;
            if (cm == null) return false;
            foreach (Connector c in cm.Connectors)
                if (c.Domain == Domain.DomainPiping || c.Domain == Domain.DomainHvac)
                    return true;
            return false;
        }

        private static IEnumerable<Connector> GetMEPEndConnectors(FamilyInstance fi)
        {
            var cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) yield break;
            foreach (Connector c in cm.Connectors)
            {
                if ((c.Domain == Domain.DomainPiping || c.Domain == Domain.DomainHvac) &&
                    (c.ConnectorType == ConnectorType.End || c.ConnectorType == ConnectorType.Curve))
                {
                    yield return c;
                }
            }
        }

        private static IEnumerable<Connector> GetFamilyEndConnectorsByDomain(FamilyInstance fi, Domain domain)
        {
            var cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) yield break;
            foreach (Connector c in cm.Connectors)
            {
                if (c.Domain == domain &&
                    (c.ConnectorType == ConnectorType.End || c.ConnectorType == ConnectorType.Curve))
                {
                    yield return c;
                }
            }
        }

        private static Domain DominantDomain(Connector c) =>
            (c?.Domain == Domain.DomainHvac) ? Domain.DomainHvac : Domain.DomainPiping;

        private static bool IsConnectedTo(Connector a, Connector b)
        {
            if (a == null || b == null) return false;
            try
            {
                foreach (Connector r in a.AllRefs)
                    if (r.Owner?.Id == b.Owner?.Id && r.Id == b.Id) return true;
            }
            catch { }
            return false;
        }

        private static Connector GetFirstPhysicalOther(Connector c, ElementId selfId, Domain domain)
        {
            try
            {
                foreach (Connector rc in c.AllRefs)
                {
                    if (rc.Owner?.Id == selfId) continue;
                    if (rc.Domain != domain) continue;
                    if (!TryGetOrigin(rc, out _)) continue;
                    return rc;
                }
            }
            catch { }
            return null;
        }

        private static bool TryGetNeighborFlange(Connector c, ElementId selfId, out FamilyInstance flange, out Connector flangeConnOnElementSide)
        {
            flange = null;
            flangeConnOnElementSide = null;
            try
            {
                foreach (Connector rc in c.AllRefs)
                {
                    if (rc.Owner?.Id == selfId) continue;
                    if (!(rc.Owner is FamilyInstance fi)) continue;

                    if (IsFlange(fi))
                    {
                        if (!TryGetOrigin(rc, out _)) continue;
                        flange = fi;
                        flangeConnOnElementSide = rc;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IsFlange(FamilyInstance fi)
        {
            if (fi?.Category == null) return false;
            int cat = fi.Category.Id.IntegerValue;

            bool catOk =
                cat == (int)BuiltInCategory.OST_PipeAccessory ||
                cat == (int)BuiltInCategory.OST_PipeFitting ||
                cat == (int)BuiltInCategory.OST_DuctAccessory ||
                cat == (int)BuiltInCategory.OST_DuctFitting;

            if (!catOk) return false;

            string nm = (fi.Name ?? "").ToLowerInvariant();
            string typ = (fi.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "").ToLowerInvariant();

            return nm.Contains("bride") || nm.Contains("flange") || typ.Contains("bride") || typ.Contains("flange");
        }

        private static bool TryFindTerminalCurveFrom(
            Connector start,
            ElementId comingFrom,
            Domain domain,
            out MEPCurve terminalCurve,
            out Connector terminalCurveConnector,
            out List<ElementId> skippables,
            int maxHops = 8)
        {
            terminalCurve = null;
            terminalCurveConnector = null;
            skippables = new List<ElementId>();

            var visited = new HashSet<ElementId> { comingFrom };
            Connector current = start;
            int hop = 0;

            while (hop++ < maxHops && current != null)
            {
                Connector next = null;

                foreach (Connector rc in current.AllRefs)
                {
                    var owner = rc.Owner;
                    if (owner == null) continue;
                    if (visited.Contains(owner.Id)) continue;
                    if (rc.Domain != domain) continue;

                    if (owner is MEPCurve curve)
                    {
                        terminalCurve = curve;
                        terminalCurveConnector = rc;
                        return true;
                    }

                    if (owner is FamilyInstance fi)
                    {
                        if (IsInlineSkippable(fi, domain))
                        {
                            skippables.Add(fi.Id);
                            visited.Add(fi.Id);

                            var ends = GetFamilyEndConnectorsByDomain(fi, domain).ToList();
                            Connector other = (ends.Count == 2)
                                ? (IsConnectedTo(ends[0], rc) ? ends[1] : ends[0])
                                : ends.FirstOrDefault(e => e.Id != rc.Id);
                            next = other;
                            break;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }

                if (next == null) return false;
                current = next;
            }

            return false;
        }

        private static bool IsInlineSkippable(FamilyInstance fi, Domain domain)
        {
            if (fi?.Category == null) return false;
            int cat = fi.Category.Id.IntegerValue;
            bool isFitting = cat == (int)BuiltInCategory.OST_PipeFitting || cat == (int)BuiltInCategory.OST_DuctFitting;
            if (!isFitting) return false;

            if (IsReducerFI(fi, domain)) return true;

            string nm = (fi.Name ?? "").ToLowerInvariant();
            string typ = (fi.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "").ToLowerInvariant();
            if (nm.Contains("manchon") || typ.Contains("manchon") ||
                nm.Contains("union") || typ.Contains("union") ||
                nm.Contains("coupling") || typ.Contains("coupling"))
                return true;

            return false;
        }

        private static bool IsReducerFI(FamilyInstance fi, Domain domain)
        {
            var ends = GetFamilyEndConnectorsByDomain(fi, domain).ToList();
            if (ends.Count != 2) return false;
            double s0 = ConnectorSize(ends[0]);
            double s1 = ConnectorSize(ends[1]);
            return !ApproximatelyEqual(s0, s1);
        }

        // ---------- Ajustement & Connexions ----------
        private static void ExtendMEPCurveEndToPoint(MEPCurve curve, XYZ target, XYZ refPointNear)
        {
            if (curve?.Location is LocationCurve lc && lc.Curve is Line line)
            {
                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);
                bool change0 = p0.DistanceTo(refPointNear) <= p1.DistanceTo(refPointNear);
                if (change0)
                    lc.Curve = Line.CreateBound(target, p1);
                else
                    lc.Curve = Line.CreateBound(p0, target);
            }
        }

        private static Connector GetClosestMEPConnector(MEPCurve curve, Domain domain, XYZ toPoint)
        {
            var cm = curve?.ConnectorManager;
            if (cm == null) return null;
            Connector best = null; double bestD = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                if (c.Domain != domain) continue;
                if (!TryGetOrigin(c, out var o)) continue;
                double d = o.DistanceTo(toPoint);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private static IEnumerable<(Connector a, Connector b)> GetAllDomainPairs(FamilyInstance fi, Domain domain)
        {
            var ends = GetFamilyEndConnectorsByDomain(fi, domain).ToList();
            if (ends.Count < 2) yield break;
            for (int i = 0; i < ends.Count; ++i)
                for (int j = i + 1; j < ends.Count; ++j)
                    yield return (ends[i], ends[j]);
        }

        // --- Connexions utilitaires ---
        private static XYZ SafeOrigin(Connector c)
        {
            try { return c.Origin; } catch { return XYZ.Zero; }
        }

        private static bool TryGetOrigin(Connector c, out XYZ o)
        {
            try { o = c.Origin; return true; } catch { o = XYZ.Zero; return false; }
        }

        private static void SafeDisconnect(Connector a, Connector b)
        {
            if (a == null || b == null) return;
            try { a.DisconnectFrom(b); } catch { }
            try { b.DisconnectFrom(a); } catch { }
        }

        private static bool ConnectWithAutoFlip(Connector a, Connector b)
        {
            try { a.ConnectTo(b); return true; }
            catch
            {
                try { b.ConnectTo(a); return true; } catch { }
                return false;
            }
        }

        private static void TryConnectQuiet(Connector a, Connector b)
        {
            try { a.ConnectTo(b); } catch { }
        }

        private static bool IsAnyConnectorConnected(FamilyInstance fi)
        {
            var cm = fi?.MEPModel?.ConnectorManager;
            if (cm == null) return false;
            foreach (Connector c in cm.Connectors)
                if (IsConnectorConnected(c)) return true;
            return false;
        }

        private static bool IsConnectorConnected(Connector c)
        {
            try { return c.AllRefs.Cast<Connector>().Any(); } catch { return false; }
        }

        // --- Mesures & tolérances ---
        private static double ConnectorSize(Connector c)
        {
            try
            {
                if (c.Shape == ConnectorProfileType.Round)
                    return 2.0 * c.Radius; // diamètre
                else
                    return c.Width + c.Height; // métrique simple rect/ovale
            }
            catch { return 0.0; }
        }

        private static bool ApproximatelyEqual(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;

        private static bool IsZero(XYZ v) => v == null || v.IsAlmostEqualTo(XYZ.Zero);

        // --- Failures ---
        private static void SuppressWarnings(Transaction t)
        {
            var fho = t.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(new WarningSwallower());
            fho.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(fho);
        }

        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                var fails = a.GetFailureMessages();
                foreach (var f in fails)
                    if (f.GetSeverity() == FailureSeverity.Warning)
                        a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>Scope pin/unpin pour éviter des déplacements involontaires.</summary>
        private struct ElementPinScope : IDisposable
        {
            private readonly FamilyInstance _fi;
            private readonly bool _initial;
            public ElementPinScope(FamilyInstance fi)
            {
                _fi = fi;
                _initial = fi.Pinned;
                if (!_initial) try { fi.Pinned = true; } catch { }
            }
            public void Dispose()
            {
                try { _fi.Pinned = _initial; } catch { }
            }
        }
    }
}

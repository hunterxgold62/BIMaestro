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
using Licensing;

namespace Modification
{
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

                int removedFlanges = 0;
                int removedSkippables = 0;
                int keptReducers = 0;
                int movedKeptReducers = 0;
                int createdTransitions = 0;
                int reconnected = 0;
                int skipped = 0;
                int failures = 0;

                using (var t = new Transaction(doc, "Retirer brides + reconnecter (réutilise réduction si existante)"))
                {
                    t.Start();
                    SuppressWarnings(t);

                    foreach (var fi in targets)
                        using (new ElementPinScope(fi))
                        {
                            foreach (var elemConn in GetMEPEndConnectors(fi).ToList())
                            {
                                // 1) Bride adjacente ?
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

                                // 2) Trouver le MEPCurve terminal + chaîne skippables
                                if (!TryFindTerminalCurveFrom(
                                        start: flangeOtherSide,
                                        comingFrom: flange.Id,
                                        domain: domain,
                                        terminalCurve: out MEPCurve terminalCurve,
                                        terminalCurveConnector: out Connector terminalCurveConn,
                                        chain: out List<InlineChainItem> chain,
                                        linksToBreak: out List<(Connector a, Connector b)> linksToBreak,
                                        maxHops: 12))
                                {
                                    skipped++;
                                    continue;
                                }

                                // 3) SubTransaction safe
                                using (var st = new SubTransaction(doc))
                                {
                                    st.Start();
                                    try
                                    {
                                        // A) Déconnecter côté accessoire
                                        SafeDisconnect(elemConn, flangeConnOnElemSide);

                                        // B) Déconnecter la chaîne côté opposé
                                        foreach (var link in linksToBreak)
                                            SafeDisconnect(link.a, link.b);

                                        doc.Regenerate();

                                        // C) Choisir une réduction à conserver (la plus proche côté accessoire)
                                        InlineChainItem keptReducer = chain.FirstOrDefault(x => x.IsReducer);

                                        // D) Supprimer la bride
                                        doc.Delete(flange.Id);
                                        removedFlanges++;
                                        doc.Regenerate();

                                        // E) Supprimer skippables SAUF la réduction conservée
                                        foreach (var item in chain)
                                        {
                                            if (keptReducer != null && item.FiId == keptReducer.FiId) continue;
                                            try
                                            {
                                                if (doc.GetElement(item.FiId) != null)
                                                {
                                                    doc.Delete(item.FiId);
                                                    removedSkippables++;
                                                }
                                            }
                                            catch { }
                                        }

                                        doc.Regenerate();

                                        // F) Reconnexion
                                        XYZ elemPt = SafeOrigin(elemConn);

                                        bool ok;

                                        if (keptReducer != null && doc.GetElement(keptReducer.FiId) != null)
                                        {
                                            // --- Cas préféré : on réutilise la réduction existante ---
                                            keptReducers++;

                                            var reducerFi = (FamilyInstance)doc.GetElement(keptReducer.FiId);
                                            var reducerIn = GetConnectorById(reducerFi, domain, keptReducer.InConnId);
                                            var reducerOut = GetConnectorById(reducerFi, domain, keptReducer.OutConnId);

                                            if (reducerIn == null || reducerOut == null)
                                                throw new InvalidOperationException("Réduction conservée mais connecteurs introuvables.");

                                            // 1) Déplacer la réduction pour coller à l'accessoire
                                            //    (on la déplace uniquement, pas de stub)
                                            XYZ redInPt = SafeOrigin(reducerIn);
                                            XYZ delta = elemPt - redInPt;

                                            bool moved = TryMoveElement(doc, reducerFi.Id, delta);
                                            if (!moved)
                                            {
                                                // Si impossible de bouger la réduction (verrouillé/contraint), on fallback :
                                                // on la supprime et on tente une transition "neuve"
                                                try { doc.Delete(reducerFi.Id); } catch { }
                                                doc.Regenerate();

                                                ok = TryCreateTransitionWithTouch(doc, elemConn, terminalCurve, terminalCurveConn, domain, out bool madeTransition);
                                                if (madeTransition) createdTransitions++;

                                                if (!ok) throw new InvalidOperationException("Fallback transition échoué après impossibilité de déplacer la réduction.");
                                            }
                                            else
                                            {
                                                movedKeptReducers++;
                                                doc.Regenerate();

                                                // Re-prendre les connecteurs après move (origines mises à jour)
                                                reducerIn = GetConnectorById(reducerFi, domain, keptReducer.InConnId) ?? reducerIn;
                                                reducerOut = GetConnectorById(reducerFi, domain, keptReducer.OutConnId) ?? reducerOut;

                                                // 2) Connecter accessoire -> réduction (côté in)
                                                SafeDisconnect(elemConn, reducerIn);
                                                doc.Regenerate();

                                                ok = ConnectWithAutoFlip(elemConn, reducerIn);
                                                if (!ok) throw new InvalidOperationException("Connexion accessoire->réduction échouée.");

                                                doc.Regenerate();

                                                // 3) Amener le MEPCurve sur l'autre côté de la réduction puis connecter
                                                XYZ outPt = SafeOrigin(reducerOut);
                                                XYZ curveRefPt = SafeOrigin(terminalCurveConn);

                                                ExtendMEPCurveEndToPoint(terminalCurve, outPt, curveRefPt);
                                                doc.Regenerate();

                                                var nearCurve = GetClosestMEPConnector(terminalCurve, domain, outPt) ?? terminalCurveConn;

                                                SafeDisconnect(reducerOut, nearCurve);
                                                doc.Regenerate();

                                                ok = ConnectWithAutoFlip(reducerOut, nearCurve);
                                                if (!ok) throw new InvalidOperationException("Connexion réduction->tuyau échouée.");

                                                doc.Regenerate();
                                            }
                                        }
                                        else
                                        {
                                            // --- Fallback : pas de réduction existante => transition neuve + touch ---
                                            ok = TryCreateTransitionWithTouch(doc, elemConn, terminalCurve, terminalCurveConn, domain, out bool madeTransition);
                                            if (madeTransition) createdTransitions++;

                                            if (!ok)
                                                throw new InvalidOperationException("Connexion/transition fallback échouée.");
                                        }

                                        // Vérif “réelle”
                                        if (!IsConnectedToRealElement(elemConn))
                                            throw new InvalidOperationException("Connexion finale non réellement raccordée.");

                                        st.Commit();
                                        reconnected++;
                                    }
                                    catch
                                    {
                                        try { st.RollBack(); } catch { }
                                        failures++;
                                    }
                                }
                            }
                        }

                    doc.Regenerate();
                    t.Commit();
                }

                try { uiDoc.RefreshActiveView(); } catch { }

                TaskDialog.Show("Retrait de brides",
                    $"Brides supprimées : {removedFlanges}\n" +
                    $"Skippables supprimés : {removedSkippables}\n" +
                    $"Réductions conservées : {keptReducers}\n" +
                    $"Réductions déplacées : {movedKeptReducers}\n" +
                    $"Transitions neuves créées : {createdTransitions}\n" +
                    $"Connexions rétablies : {reconnected}\n" +
                    $"Ignorées : {skipped}\n" +
                    $"Échecs (rollback) : {failures}");

                return Result.Succeeded;
            }
            finally
            {
                uiApp.DialogBoxShowing -= autoYes;
            }
        }

        // ===================== Traversée chaîne =====================

        private class InlineChainItem
        {
            public ElementId FiId;
            public int InConnId;   // côté "vers accessoire"
            public int OutConnId;  // côté "vers tuyau"
            public bool IsReducer;
        }

        private static bool TryFindTerminalCurveFrom(
            Connector start,
            ElementId comingFrom,
            Domain domain,
            out MEPCurve terminalCurve,
            out Connector terminalCurveConnector,
            out List<InlineChainItem> chain,
            out List<(Connector a, Connector b)> linksToBreak,
            int maxHops = 8)
        {
            terminalCurve = null;
            terminalCurveConnector = null;
            chain = new List<InlineChainItem>();
            linksToBreak = new List<(Connector a, Connector b)>();

            var visited = new HashSet<ElementId> { comingFrom };
            Connector current = start;
            int hop = 0;

            while (hop++ < maxHops && current != null)
            {
                Connector chosenRef = null;

                try
                {
                    foreach (Connector rc in current.AllRefs)
                    {
                        var owner = rc.Owner;
                        if (owner == null) continue;
                        if (visited.Contains(owner.Id)) continue;
                        if (rc.Domain != domain) continue;
                        if (!TryGetOrigin(rc, out _)) continue;

                        chosenRef = rc;
                        break;
                    }
                }
                catch { return false; }

                if (chosenRef == null) return false;

                var nextOwner = chosenRef.Owner;

                // on casse toujours le lien current <-> chosenRef
                linksToBreak.Add((current, chosenRef));

                if (nextOwner is MEPCurve curve)
                {
                    terminalCurve = curve;
                    terminalCurveConnector = chosenRef;
                    return true;
                }

                if (nextOwner is FamilyInstance fi)
                {
                    if (!IsInlineSkippable(fi, domain))
                        return false;

                    var ends = GetFamilyEndConnectorsByDomain(fi, domain).ToList();
                    if (ends.Count != 2) return false;

                    visited.Add(fi.Id);

                    // déterminer in/out (in = celui connecté à current)
                    Connector inC = ends.FirstOrDefault(c => c.Id == chosenRef.Id) ?? chosenRef;
                    Connector outC = ends.FirstOrDefault(c => c.Id != inC.Id);

                    bool isReducer = IsReducerFI(fi, domain);

                    chain.Add(new InlineChainItem
                    {
                        FiId = fi.Id,
                        InConnId = inC.Id,
                        OutConnId = outC?.Id ?? -1,
                        IsReducer = isReducer
                    });

                    current = outC;
                    continue;
                }

                return false;
            }

            return false;
        }

        // ===================== Reconnexion fallback (transition + touch) =====================

        private static bool TryCreateTransitionWithTouch(
            Document doc,
            Connector elemConn,
            MEPCurve terminalCurve,
            Connector terminalCurveConn,
            Domain domain,
            out bool transitionCreated)
        {
            transitionCreated = false;

            // 1) Étendre/couper sur l’accessoire
            XYZ elemPt = SafeOrigin(elemConn);
            XYZ curveRefPt = SafeOrigin(terminalCurveConn);

            ExtendMEPCurveEndToPoint(terminalCurve, elemPt, curveRefPt);
            doc.Regenerate();

            var near = GetClosestMEPConnector(terminalCurve, domain, elemPt) ?? terminalCurveConn;

            SafeDisconnect(elemConn, near);
            doc.Regenerate();

            // 2) si tailles diffèrent => transition
            bool needTransition = !ApproximatelyEqual(ConnectorSize(elemConn), ConnectorSize(near));

            if (needTransition)
            {
                if (TryNewTransition(doc, elemConn, near))
                {
                    transitionCreated = true;
                    doc.Regenerate();
                    return true;
                }

                // “reset” comme toi : toucher un paramètre de l’accessoire
                TouchElement(doc, elemConn.Owner?.Id ?? ElementId.InvalidElementId);

                SafeDisconnect(elemConn, near);
                doc.Regenerate();

                if (TryNewTransition(doc, elemConn, near))
                {
                    transitionCreated = true;
                    doc.Regenerate();
                    return true;
                }

                // Dernière chance : toucher aussi le tuyau
                TouchElement(doc, terminalCurve.Id);

                SafeDisconnect(elemConn, near);
                doc.Regenerate();

                if (TryNewTransition(doc, elemConn, near))
                {
                    transitionCreated = true;
                    doc.Regenerate();
                    return true;
                }

                // dernier recours : connexion simple
                bool ok = ConnectWithAutoFlip(elemConn, near);
                doc.Regenerate();
                return ok;
            }
            else
            {
                bool ok = ConnectWithAutoFlip(elemConn, near);
                doc.Regenerate();
                return ok;
            }
        }

        private static bool TryNewTransition(Document doc, Connector a, Connector b)
        {
            try
            {
                var fit = doc.Create.NewTransitionFitting(a, b);
                return fit != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TouchElement(Document doc, ElementId id)
        {
            if (id == ElementId.InvalidElementId) return false;

            var e = doc.GetElement(id);
            if (e == null) return false;

            // On tente d’abord "Commentaires", sinon "Marque"
            Parameter p =
                e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) ??
                e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);

            if (p == null || p.IsReadOnly) return false;

            try
            {
                string oldVal = p.AsString() ?? "";
                p.Set(oldVal + " ");
                doc.Regenerate();
                p.Set(oldVal);
                doc.Regenerate();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ===================== Move safe (réduction) =====================

        private static bool TryMoveElement(Document doc, ElementId id, XYZ delta)
        {
            if (id == ElementId.InvalidElementId) return false;
            if (delta == null || delta.IsAlmostEqualTo(XYZ.Zero)) return true;

            try
            {
                ElementTransformUtils.MoveElement(doc, id, delta);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Connector GetConnectorById(FamilyInstance fi, Domain domain, int connectorId)
        {
            if (fi?.MEPModel?.ConnectorManager == null) return null;
            foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
            {
                if (c.Domain != domain) continue;
                if (c.Id == connectorId) return c;
            }
            return null;
        }

        // ===================== Sélection =====================

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

        // ===================== Helpers MEP =====================

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
                    yield return c;
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
                    yield return c;
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

        private static bool IsInlineSkippable(FamilyInstance fi, Domain domain)
        {
            if (fi?.Category == null) return false;
            int cat = fi.Category.Id.IntegerValue;

            bool isFitting = cat == (int)BuiltInCategory.OST_PipeFitting || cat == (int)BuiltInCategory.OST_DuctFitting;
            if (!isFitting) return false;

            if (IsReducerFI(fi, domain)) return true;

            string nm = (fi.Name ?? "").ToLowerInvariant();
            string typ = (fi.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "").ToLowerInvariant();

            return nm.Contains("manchon") || typ.Contains("manchon") ||
                   nm.Contains("union") || typ.Contains("union") ||
                   nm.Contains("coupling") || typ.Contains("coupling");
        }

        private static bool IsReducerFI(FamilyInstance fi, Domain domain)
        {
            var ends = GetFamilyEndConnectorsByDomain(fi, domain).ToList();
            if (ends.Count != 2) return false;
            double s0 = ConnectorSize(ends[0]);
            double s1 = ConnectorSize(ends[1]);
            return !ApproximatelyEqual(s0, s1);
        }

        // ===================== Ajustement & Connexions =====================

        private static void ExtendMEPCurveEndToPoint(MEPCurve curve, XYZ target, XYZ refPointNear)
        {
            if (curve?.Location is LocationCurve lc && lc.Curve is Line line)
            {
                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);
                bool change0 = p0.DistanceTo(refPointNear) <= p1.DistanceTo(refPointNear);
                lc.Curve = change0 ? Line.CreateBound(target, p1) : Line.CreateBound(p0, target);
            }
        }

        private static Connector GetClosestMEPConnector(MEPCurve curve, Domain domain, XYZ toPoint)
        {
            var cm = curve?.ConnectorManager;
            if (cm == null) return null;

            Connector best = null;
            double bestD = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                if (c.Domain != domain) continue;
                if (!TryGetOrigin(c, out var o)) continue;
                double d = o.DistanceTo(toPoint);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

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

        private static bool IsConnectedToRealElement(Connector c)
        {
            if (c == null) return false;
            try
            {
                var ownerId = c.Owner?.Id;
                if (ownerId == null) return false;

                foreach (Connector r in c.AllRefs)
                {
                    if (r?.Owner == null) continue;
                    if (r.Owner.Id == ownerId) continue;

                    int cat = r.Owner.Category?.Id.IntegerValue ?? int.MinValue;
                    if (cat == (int)BuiltInCategory.OST_PipingSystem || cat == (int)BuiltInCategory.OST_DuctSystem)
                        continue;

                    return true;
                }
            }
            catch { }
            return false;
        }

        // ===================== Mesures & tolérances =====================

        private static double ConnectorSize(Connector c)
        {
            try
            {
                if (c.Shape == ConnectorProfileType.Round) return 2.0 * c.Radius;
                return c.Width + c.Height;
            }
            catch { return 0.0; }
        }

        private static bool ApproximatelyEqual(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;

        // ===================== Failures =====================

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

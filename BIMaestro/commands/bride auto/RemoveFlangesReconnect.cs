using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.UI.Selection;
using Licensing; // BaseTrackedCommand

namespace Modification
{
    /// <summary>
    /// Retire les brides au droit des éléments sélectionnés ET reconnecte directement
    /// la canalisation/le fitting/l'équipement. Zéro trou : si la reconnexion échoue,
    /// on remet les connexions et on ne supprime pas la bride.
    /// Si le voisin est un PIPE, on prolonge/coupe l’extrémité jusqu’au connecteur.
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
                            "Sélectionne les accessoires/équipements sur lesquels retirer les brides.");
                        ids = picked.Select(r => r.ElementId).ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                var targets = ids.Select(id => doc.GetElement(id))
                                 .OfType<FamilyInstance>()
                                 .Where(HasPipingConnectors)
                                 .ToList();
                if (targets.Count == 0)
                {
                    TaskDialog.Show("Retrait de brides", "Aucun élément MEP (piping) valide dans la sélection.");
                    return Result.Cancelled;
                }

                int removed = 0, reconnected = 0, skipped = 0;

                using (var t = new Transaction(doc, "Retirer brides + reconnecter"))
                {
                    t.Start();
                    SuppressWarnings(t);

                    foreach (var fi in targets)
                        using (new PinScope(fi)) // ne bouge pas l'élément sélectionné
                        {
                            foreach (var elemConn in GetPipingEndConnectors(fi).ToList())
                            {
                                if (!TryGetNeighborFlange(elemConn, fi.Id, out FamilyInstance flange, out Connector flangeConnOnElemSide))
                                    continue;

                                var fConns = GetPipingEndConnectors(flange).ToList();
                                if (fConns.Count < 1) { skipped++; continue; }

                                if (flangeConnOnElemSide == null)
                                    flangeConnOnElemSide = fConns.FirstOrDefault(c => IsConnectedTo(c, elemConn));

                                var flangeOtherSide = fConns.FirstOrDefault(c => c.Id != flangeConnOnElemSide?.Id)
                                                   ?? fConns.FirstOrDefault();

                                if (flangeConnOnElemSide == null || flangeOtherSide == null) { skipped++; continue; }

                                // Voisin réel (pipe/fitting/équipement) relié à la bride côté opposé
                                var otherNeighbor = GetFirstPhysicalPipingOther(flangeOtherSide, flange.Id);
                                if (otherNeighbor == null)
                                    otherNeighbor = GetFirstPhysicalPipingOther(flangeConnOnElemSide, flange.Id);
                                if (otherNeighbor == null) { skipped++; continue; }

                                // Sauvegarde pour rollback
                                var back_elem_flange = (a: elemConn, b: flangeConnOnElemSide);
                                var back_other_flange = (a: flangeOtherSide, b: otherNeighbor);

                                bool success = false;
                                try
                                {
                                    // 1) Déconnecter les DEUX côtés de la bride
                                    SafeDisconnect(back_elem_flange.a, back_elem_flange.b);
                                    SafeDisconnect(back_other_flange.a, back_other_flange.b);
                                    doc.Regenerate();

                                    // 1bis) Si le voisin est un PIPE, on ajuste l’extrémité au point du connecteur élément
                                    if (IsPipe(otherNeighbor.Owner))
                                    {
                                        var pipe = otherNeighbor.Owner as Pipe;
                                        var elemPt = SafeOrigin(elemConn);
                                        var nearRefPt = SafeOrigin(flangeOtherSide); // côté de la bride où le pipe était connecté
                                        ExtendPipeEndToPoint(pipe, elemPt, nearRefPt);
                                        doc.Regenerate();

                                        // récupère le connecteur de pipe le plus proche du point cible (après extension)
                                        otherNeighbor = GetClosestPipeConnector(pipe, elemPt) ?? otherNeighbor;
                                    }

                                    // 2) Connecte directement l'élément au voisin
                                    if (ConnectWithAutoFlip(elemConn, otherNeighbor))
                                    {
                                        success = true;
                                    }
                                    else
                                    {
                                        // rollback
                                        TryConnectQuiet(back_elem_flange.a, back_elem_flange.b);
                                        TryConnectQuiet(back_other_flange.a, back_other_flange.b);
                                    }

                                    if (!success) { skipped++; continue; }

                                    // 3) Supprime la bride
                                    doc.Delete(flange.Id);
                                    removed++;
                                    reconnected++;
                                    doc.Regenerate();
                                }
                                catch
                                {
                                    // Rollback en cas d’exception
                                    TryConnectQuiet(back_elem_flange.a, back_elem_flange.b);
                                    TryConnectQuiet(back_other_flange.a, back_other_flange.b);
                                    skipped++;
                                }
                            }
                        }

                    doc.Regenerate();
                    t.Commit();
                }

                TaskDialog.Show("Retrait de brides",
                    $"Brides supprimées : {removed}\nConnexions rétablies : {reconnected}\nIgnorées/échouées : {skipped}");
                return Result.Succeeded;
            }
            finally
            {
                uiApp.DialogBoxShowing -= autoYes;
            }
        }

        // ---------- Filtres sélection ----------
        private class SelectFilterForRemove : ISelectionFilter
        {
            public bool AllowElement(Element e) =>
                e?.Category != null &&
                (e.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory ||
                 e.Category.Id.IntegerValue == (int)BuiltInCategory.OST_MechanicalEquipment) &&
                (e as FamilyInstance)?.MEPModel?.ConnectorManager != null;

            public bool AllowReference(Reference r, XYZ p) => false;
        }

        // ---------- Helpers MEP ----------
        private static bool HasPipingConnectors(FamilyInstance fi)
        {
            if (fi?.MEPModel?.ConnectorManager == null) return false;
            foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                if (c.Domain == Domain.DomainPiping) return true;
            return false;
        }

        private static IEnumerable<Connector> GetPipingEndConnectors(FamilyInstance fi)
        {
            var cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) yield break;
            foreach (Connector c in cm.Connectors)
                if (c.Domain == Domain.DomainPiping &&
                    (c.ConnectorType == ConnectorType.End || c.ConnectorType == ConnectorType.Curve))
                    yield return c;
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
                    var owner = rc.Owner as FamilyInstance;
                    if (owner == null) continue;

                    if (IsFlange(owner))
                    {
                        if (!TryGetOrigin(rc, out _)) continue; // seulement physical
                        flange = owner;
                        flangeConnOnElementSide = rc;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IsFlange(Element e)
        {
            if (e?.Category == null) return false;
            int cat = e.Category.Id.IntegerValue;
            bool catOk = (cat == (int)BuiltInCategory.OST_PipeAccessory) ||
                         (cat == (int)BuiltInCategory.OST_PipeFitting);
            if (!catOk) return false;

            string nm = (e.Name ?? "").ToLowerInvariant();
            string typ = (e.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "").ToLowerInvariant();

            return nm.Contains("bride") || nm.Contains("flange") || typ.Contains("bride") || typ.Contains("flange");
        }

        private static bool IsPipe(Element e)
            => e?.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_PipeCurves;

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

        private static Connector GetFirstPhysicalPipingOther(Connector c, ElementId selfId)
        {
            try
            {
                foreach (Connector rc in c.AllRefs)
                {
                    if (rc.Owner?.Id == selfId) continue;
                    if (rc.Domain != Domain.DomainPiping) continue;
                    try { var _ = rc.Origin; } catch { continue; } // Physical seulement
                    return rc;
                }
            }
            catch { }
            return null;
        }

        // --- Ajustement des pipes ---
        private static XYZ SafeOrigin(Connector c)
        {
            try { return c.Origin; } catch { return XYZ.Zero; }
        }

        private static bool TryGetOrigin(Connector c, out XYZ o)
        {
            try { o = c.Origin; return true; } catch { o = XYZ.Zero; return false; }
        }

        /// <summary>Étend ou coupe l’extrémité du pipe la plus proche de refPointNear pour qu’elle arrive à target.</summary>
        private static void ExtendPipeEndToPoint(Pipe pipe, XYZ target, XYZ refPointNear)
        {
            var lc = (pipe?.Location as LocationCurve);
            if (lc?.Curve is Line line)
            {
                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);
                // quelle extrémité était côté bride ?
                bool change0 = p0.DistanceTo(refPointNear) <= p1.DistanceTo(refPointNear);
                if (change0)
                    lc.Curve = Line.CreateBound(target, p1);
                else
                    lc.Curve = Line.CreateBound(p0, target);
            }
        }

        /// <summary>Renvoie le connecteur de pipe le plus proche d’un point donné.</summary>
        private static Connector GetClosestPipeConnector(Pipe pipe, XYZ toPoint)
        {
            var cm = (pipe as MEPCurve)?.ConnectorManager;
            if (cm == null) return null;
            Connector best = null; double bestD = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                if (c.Domain != Domain.DomainPiping) continue;
                if (!TryGetOrigin(c, out var o)) continue;
                double d = o.DistanceTo(toPoint);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        // --- Connexions utilitaires ---
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
    }
}

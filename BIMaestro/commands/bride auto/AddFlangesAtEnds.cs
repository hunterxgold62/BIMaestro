using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using Licensing;

namespace Modification
{
    // ----------------- Choix en mémoire (session) -----------------
    public static class FlangeChoiceCache
    {
        public static string FamilyName { get; set; } = null;
        public static string SymbolName { get; set; } = null;
        public static bool HasChoice =>
            !string.IsNullOrWhiteSpace(FamilyName) && !string.IsNullOrWhiteSpace(SymbolName);
        public static void Clear() { FamilyName = null; SymbolName = null; }
    }

    [Transaction(TransactionMode.Manual)]
    public class AddFlangesAtEnds : BaseTrackedCommand
    {
        private const bool ACCESSORY_SIDE_IS_IN = true;
        protected override string ButtonId => "AddFlangesAtEnds";


        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            UIDocument uiDoc = data.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            UIApplication uiApp = data.Application;

            // Auto-Yes aux TaskDialog (déconnexion réseau, etc.)
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
                            ObjectType.Element, new PipingAccessoryFilter(),
                            "Sélectionne des accessoires (vannes, filtres...) pour poser des brides.");
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
                    TaskDialog.Show("Brides", "Aucun accessoire MEP (piping) valide dans la sélection.");
                    return Result.Cancelled;
                }

                // --- Type de bride : choix en mémoire (fenêtre WPF) sinon fallback auto ---
                var flangeSymbol = FindFlangeSymbol(doc);
                if (flangeSymbol == null)
                {
                    TaskDialog.Show("Brides manquantes",
                        "Aucun type 'bride' admissible. Charge par ex. 'CML Bride à collerette tous PN2'.");
                    return Result.Cancelled;
                }

                // --- Vérifier que la bride a ≥ 2 connecteurs piping ---
                bool flangeHasTwoConnectors;
                using (var tg = new TransactionGroup(doc, "Pré-check bride"))
                {
                    tg.Start();
                    using (var t = new Transaction(doc, "Temp"))
                    {
                        t.Start();
                        if (!flangeSymbol.IsActive) flangeSymbol.Activate();
                        Level lvl = GuessAnyLevel(doc);
                        var temp = doc.Create.NewFamilyInstance(XYZ.Zero, flangeSymbol, lvl, StructuralType.NonStructural);
                        flangeHasTwoConnectors = CountPipingConnectors(temp) >= 2;
                        doc.Delete(temp.Id);
                        t.Commit();
                    }
                    tg.RollBack();
                }

                int placed = 0, skipped = 0;

                using (var t = new Transaction(doc, "Ajouter brides"))
                {
                    t.Start();
                    SuppressWarnings(t);

                    foreach (var acc in targets)
                    {
                        var accConns = GetPipingEndConnectors(acc).ToList();
                        if (accConns.Count == 0) { skipped++; continue; }

                        foreach (var accConn in accConns)
                        {
                            if (AlreadyHasFlangeAtConnector(accConn))
                                continue;

                            var neighbor = GetFirstConnectedOther(accConn, acc.Id);

                            try
                            {
                                if (neighbor != null && flangeHasTwoConnectors)
                                    InsertFlangeBetween(doc, flangeSymbol, accConn, neighbor);
                                else
                                    PlaceFlangeOnOneSide(doc, flangeSymbol, accConn);

                                placed++;
                            }
                            catch
                            {
                                skipped++;
                            }
                        }
                    }

                    t.Commit();
                }

                TaskDialog.Show("Brides",
                    $"Brides posées : {placed}\nIgnorées/échouées : {skipped}\n" +
                    $"Type : {flangeSymbol.FamilyName} : {flangeSymbol.Name}");
                return Result.Succeeded;
            }
            finally
            {
                uiApp.DialogBoxShowing -= autoYes;
            }
        }

        // ---------- Sélection & tests ----------
        private class PipingAccessoryFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) =>
                e?.Category != null &&
                (e.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory ||
                 e.Category.Id.IntegerValue == (int)BuiltInCategory.OST_MechanicalEquipment) &&
                HasPipingConnectors(e as FamilyInstance);

            public bool AllowReference(Reference r, XYZ p) => false;
        }

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

        private static int CountPipingConnectors(FamilyInstance fi)
        {
            int n = 0;
            var cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) return 0;
            foreach (Connector c in cm.Connectors)
                if (c.Domain == Domain.DomainPiping) n++;
            return n;
        }

        private static Connector GetFirstConnectedOther(Connector c, ElementId selfId)
        {
            try
            {
                foreach (Connector rc in c.AllRefs)
                    if (rc.Owner?.Id != selfId) return rc;
            }
            catch { }
            return null;
        }

        private static bool AlreadyHasFlangeAtConnector(Connector accConn)
        {
            try
            {
                foreach (Connector rc in accConn.AllRefs)
                {
                    var owner = rc.Owner;
                    if (owner?.Category == null) continue;
                    bool isPipeAcc = owner.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory;
                    bool isFitting = owner.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting;

                    string nm = (owner.Name ?? "").ToLower();
                    string typ = (owner.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "").ToLower();
                    if ((isPipeAcc || isFitting) && (nm.Contains("bride") || nm.Contains("flange") || typ.Contains("bride") || typ.Contains("flange")))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // ---------- Trouver le type de bride ----------
        private static FamilySymbol FindFlangeSymbol(Document doc)
        {
            // 1) Si l'utilisateur a choisi une famille/type dans la session, on l'utilise
            if (FlangeChoiceCache.HasChoice)
            {
                var chosen = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(fs =>
                        fs?.Category != null &&
                       (fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory ||
                        fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting) &&
                        string.Equals(fs.FamilyName, FlangeChoiceCache.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fs.Name, FlangeChoiceCache.SymbolName, StringComparison.OrdinalIgnoreCase));
                if (chosen != null) return chosen;
            }

            // 2) Recherche standard (exclusion de la famille problématique)
            var symbols = new FilteredElementCollector(doc)
    .OfClass(typeof(FamilySymbol))
    .OfCategory(BuiltInCategory.OST_PipeAccessory)   // <<< le filtre manquant
    .Cast<FamilySymbol>()
    .OrderBy(fs => fs.FamilyName).ThenBy(fs => fs.Name)
    .ToList();



            symbols = symbols.Where(fs =>
            {
                string both = ((fs.FamilyName ?? "") + " " + (fs.Name ?? "")).ToLower();
                return !both.Contains("cml_bride pleine tous pn");
            }).ToList();
            if (symbols.Count == 0) return null;

            var pn2 = symbols.FirstOrDefault(fs =>
                string.Equals(fs.FamilyName, "CML Bride à collerette tous PN2", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fs.Name, "CML Bride à collerette tous PN2", StringComparison.OrdinalIgnoreCase));
            if (pn2 != null) return pn2;

            var filtered = symbols.Where(fs =>
            {
                string hay = ((fs.FamilyName ?? "") + " " + (fs.Name ?? "")).ToLower();
                return hay.Contains("bride") || hay.Contains("flange");
            }).ToList();

            if (filtered.Count == 0) return null;

            return filtered
                .OrderByDescending(fs => fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory)
                .ThenBy(fs => fs.FamilyName).ThenBy(fs => fs.Name)
                .First();
        }

        // ---------- Placement / insertion ----------
        private static void InsertFlangeBetween(Document doc, FamilySymbol flangeSymbol, Connector accConn, Connector neighbor)
        {
            if (!flangeSymbol.IsActive) flangeSymbol.Activate();

            XYZ pA = accConn.Origin;
            XYZ dirA = (neighbor.Origin - accConn.Origin);
            if (dirA.IsAlmostEqualTo(XYZ.Zero)) dirA = SafeDirection(accConn);
            dirA = dirA.Normalize();

            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            accConn.DisconnectFrom(neighbor);

            const double placeOffset = 0.05; // ~15 mm
            var flange = doc.Create.NewFamilyInstance(pA + dirA * placeOffset, flangeSymbol, lvl, StructuralType.NonStructural);

            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count == 0) throw new InvalidOperationException("Bride sans connecteurs Piping.");
            var fToAcc_guess = ChooseFlangeConnForAccessory_Initial(fConns, dirA);

            AlignConnectorDirection(flange, fToAcc_guess, dirA);

            fConns = GetPipingEndConnectors(flange).ToList();
            var fToAcc = ChooseAccessoryFinal(fConns, dirA, pA);
            var fToNei = fConns.FirstOrDefault(c => c.Id != fToAcc.Id) ?? fToAcc;

            MoveBy(flange, pA - fToAcc.Origin);

            fToAcc.ConnectTo(accConn);
            fToNei.ConnectTo(neighbor);

            TrySetNominalDiameter(flange, accConn);
        }

        private static void PlaceFlangeOnOneSide(Document doc, FamilySymbol flangeSymbol, Connector accConn)
        {
            if (!flangeSymbol.IsActive) flangeSymbol.Activate();

            XYZ pA = accConn.Origin;
            XYZ dirA = SafeDirection(accConn);
            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            const double placeOffset = 0.02; // ~6 mm
            var flange = doc.Create.NewFamilyInstance(pA + dirA * placeOffset, flangeSymbol, lvl, StructuralType.NonStructural);

            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count == 0) throw new InvalidOperationException("Bride sans connecteurs Piping.");

            var fToAcc_guess = ChooseFlangeConnForAccessory_Initial(fConns, dirA);
            AlignConnectorDirection(flange, fToAcc_guess, dirA);

            fConns = GetPipingEndConnectors(flange).ToList();
            var fToAcc = ChooseAccessoryFinal(fConns, dirA, pA);

            MoveBy(flange, pA - fToAcc.Origin);
            fToAcc.ConnectTo(accConn);

            TrySetNominalDiameter(flange, accConn);
        }

        // ---------- Règles de choix connecteurs ----------
        private static Connector ChooseFlangeConnForAccessory_Initial(List<Connector> conns, XYZ dir)
        {
            if (ACCESSORY_SIDE_IS_IN)
            {
                var inConn = conns.FirstOrDefault(c => c.Direction == FlowDirectionType.In);
                if (inConn != null) return inConn;
                var outConn = conns.FirstOrDefault(c => c.Direction == FlowDirectionType.Out);
                if (outConn != null) return outConn;
            }
            else
            {
                var outConn = conns.FirstOrDefault(c => c.Direction == FlowDirectionType.Out);
                if (outConn != null) return outConn;
                var inConn = conns.FirstOrDefault(c => c.Direction == FlowDirectionType.In);
                if (inConn != null) return inConn;
            }
            return conns.OrderBy(c => GetBasisZ(c).DotProduct(dir)).First();
        }

        private static Connector ChooseAccessoryFinal(List<Connector> conns, XYZ dir, XYZ planePoint)
        {
            const double tol = 1e-3;
            var byDir = conns.OrderBy(c => GetBasisZ(c).DotProduct(dir)).ToList(); // min dot => plus opposé
            if (byDir.Count <= 1) return byDir[0];

            double s0 = GetBasisZ(byDir[0]).DotProduct(dir);
            double s1 = GetBasisZ(byDir[1]).DotProduct(dir);
            if (Math.Abs(s0 - s1) > tol) return byDir[0];

            return byDir.OrderBy(c => Math.Abs((c.Origin - planePoint).DotProduct(dir))).First();
        }

        // ---------- Utilitaires géométrie ----------
        private static XYZ SafeDirection(Connector c)
        {
            var cs = c.CoordinateSystem;
            if (cs != null)
            {
                var z = cs.BasisZ; if (!z.IsAlmostEqualTo(XYZ.Zero)) return z.Normalize();
                var x = cs.BasisX; if (!x.IsAlmostEqualTo(XYZ.Zero)) return x.Normalize();
                var y = cs.BasisY; if (!y.IsAlmostEqualTo(XYZ.Zero)) return y.Normalize();
            }
            try
            {
                foreach (Connector r in c.AllRefs)
                {
                    var v = r.Origin - c.Origin;
                    if (!v.IsAlmostEqualTo(XYZ.Zero)) return v.Normalize();
                }
            }
            catch { }
            return XYZ.BasisZ;
        }

        private static XYZ GetBasisZ(Connector c)
        {
            var cs = c.CoordinateSystem;
            return cs != null ? cs.BasisZ.Normalize() : XYZ.BasisZ;
        }

        private static void AlignConnectorDirection(FamilyInstance fi, Connector fiConn, XYZ targetDir)
        {
            XYZ from = GetBasisZ(fiConn);
            XYZ to = targetDir.Normalize();

            double dot = Math.Max(-1.0, Math.Min(1.0, from.DotProduct(to)));
            double angle = Math.Acos(dot);
            if (angle < 1e-6) return;

            XYZ axis = from.CrossProduct(to);
            if (axis.IsAlmostEqualTo(XYZ.Zero))
                axis = Math.Abs(from.DotProduct(XYZ.BasisX)) < 0.9 ? from.CrossProduct(XYZ.BasisX) : from.CrossProduct(XYZ.BasisY);

            axis = axis.Normalize();
            XYZ p = GetElementPivot(fi);
            var line = Line.CreateUnbound(p, axis);
            ElementTransformUtils.RotateElement(fi.Document, fi.Id, line, angle);
        }

        private static XYZ GetElementPivot(FamilyInstance fi)
        {
            var lp = fi.Location as LocationPoint;
            if (lp != null) return lp.Point;

            var bb = fi.get_BoundingBox(null);
            if (bb != null) return (bb.Min + bb.Max) * 0.5;

            return XYZ.Zero;
        }

        private static void MoveBy(FamilyInstance fi, XYZ delta)
        {
            if (delta.IsAlmostEqualTo(XYZ.Zero)) return;
            ElementTransformUtils.MoveElement(fi.Document, fi.Id, delta);
        }

        private static Level GetClosestLevel(Document doc, XYZ at)
        {
            var lvls = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            if (lvls.Count == 0) return null;
            return lvls.OrderBy(l => Math.Abs(l.Elevation - at.Z)).First();
        }

        private static Level GuessAnyLevel(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).FirstOrDefault();
        }

        private static void TrySetNominalDiameter(FamilyInstance flange, Connector sourceConn)
        {
            double dia = sourceConn.Radius * 2.0; // ft
            var candidates = new[] { "Nominal Diameter", "DN", "Diameter", "Diamètre nominal", "Diamètre", "RBS_PIPE_DIAMETER" };

            foreach (Parameter p in flange.Parameters)
            {
                string name = p.Definition?.Name ?? "";
                if (!candidates.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                if (p.StorageType == StorageType.Double)
                {
                    try { p.Set(dia); return; } catch { }
                }
            }
        }

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

    internal static class XyzExt
    {
        public static bool IsAlmostEqualTo(this XYZ a, XYZ b, double tol = 1e-9)
            => Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol && Math.Abs(a.Z - b.Z) < tol;
    }
}

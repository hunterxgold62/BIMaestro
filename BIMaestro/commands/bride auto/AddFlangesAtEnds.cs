// BIMaestro - Brides 2023/2025 - fichier unique

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.UI.Selection;
using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Modification
{
    // ----------------- Mémoire du choix (pour fenêtre XAML éventuelle) -----------------
    public static class FlangeChoiceCache
    {
        public static string FamilyName { get; set; }
        public static string SymbolName { get; set; }
        public static bool HasChoice =>
            !string.IsNullOrWhiteSpace(FamilyName) && !string.IsNullOrWhiteSpace(SymbolName);
        public static void Clear() { FamilyName = null; SymbolName = null; }
    }

    // ----------------- Utilitaires élémentaires -----------------
    internal sealed class PinScope : IDisposable
    {
        private readonly FamilyInstance _fi;
        private readonly bool _changed;
        private readonly bool _prev;

        public PinScope(Element e)
        {
            _fi = e as FamilyInstance;
            if (_fi == null) return;
            try
            {
                _prev = _fi.Pinned;
                if (!_fi.Pinned) { _fi.Pinned = true; _changed = true; }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_fi == null || !_changed) return;
            try { _fi.Pinned = _prev; } catch { }
        }
    }

    internal static class XyzExt
    {
        public static bool IsAlmostEqualTo(this XYZ a, XYZ b, double tol = 1e-9)
            => Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol && Math.Abs(a.Z - b.Z) < tol;
    }

    // ----------------- Filtre de sélection -----------------
    internal class PipingAccessoryFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            if (e == null || e.Category == null) return false;
            int cat = e.Category.Id.GetIdValue();
            bool okCat = cat == (int)BuiltInCategory.OST_PipeAccessory ||
                         cat == (int)BuiltInCategory.OST_MechanicalEquipment;
            if (!okCat) return false;
            var fi = e as FamilyInstance;
            if (fi == null) return false;
            var cm = fi.MEPModel != null ? fi.MEPModel.ConnectorManager : null;
            if (cm == null) return false;
            foreach (Connector c in cm.Connectors)
                if (c.Domain == Domain.DomainPiping) return true;
            return false;
        }
        public bool AllowReference(Reference r, XYZ p) => false;
    }

    // ----------------- Commande principale -----------------
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AddFlangesAtEnds : BaseTrackedCommand
    {
        protected override string ButtonId => "AddFlangesAtEnds";

        // ---- Unités & garde-fous
        const double MM = 1.0 / 304.8;
        const double SAFETY_MARGIN = 5 * MM;
        const double RADIAL_SCAN = 150 * MM;
        private static readonly bool ACCESSORY_SIDE_IS_IN = true;   // logique 2023 conservée
        private const double PLACE_OFFSET_BETWEEN = 0.05; // ~15 mm (2023)
        private const double PLACE_OFFSET_ONESIDE = 0.02; // ~6 mm  (2023)
        private const double EPS = 1e-9;

        // ---- Micro “handshake” (2025)
        private const double MICRO_GAP = 0.5 * MM;
        private const double MICRO_NUDGE = 0.1 * MM;
        private const double COLOC_TOL = 0.2 * MM;

        // ---- Tokens (lecture Description du connecteur – read-only)
        private static readonly string[] TOK_ACC = { "accessoire", "accessory", "acc" };
        private static readonly string[] TOK_PIPE = { "canalisations", "canalisation", "cana", "pipe", "piping" };

        // ---- Cache PN → FamilySymbol
        private static readonly Dictionary<string, ElementId> _pnTypeCache = new Dictionary<string, ElementId>();

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = data.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            UIApplication uiApp = data.Application;

            // Auto-Yes aux TaskDialog
            EventHandler<DialogBoxShowingEventArgs> autoYes = (s, e) =>
            {
                if (e is TaskDialogShowingEventArgs td) td.OverrideResult((int)TaskDialogResult.Yes);
            };
            uiApp.DialogBoxShowing += autoYes;

            try
            {
                int revitMajor = 0;
                int.TryParse(doc.Application.VersionNumber, out revitMajor);
                bool mode2025 = revitMajor >= 2025;

                // ---------- Sélection ----------
                var ids = uiDoc.Selection.GetElementIds().ToList();
                if (ids.Count == 0)
                {
                    try
                    {
                        var picked = uiDoc.Selection.PickObjects(
                            ObjectType.Element, new PipingAccessoryFilter(),
                            "Sélectionne des accessoires/équipements CVC pour poser des brides.");
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
                    TaskDialog.Show(UiLanguage.T("Brides", "Flanges"), UiLanguage.T("Aucun élément MEP (piping) valide dans la sélection.", "No valid MEP (piping) element was selected."));
                    return Result.Cancelled;
                }

                // ---------- Type de bride ----------
                var baseFlangeSymbol = FindFlangeSymbol(doc);
                if (baseFlangeSymbol == null)
                {
                    TaskDialog.Show(UiLanguage.T("Brides manquantes", "Missing Flanges"),
                        UiLanguage.T("Aucun type 'bride' admissible. Charge par ex. 'CML Bride à collerette tous PN2'.", "No eligible flange type was found. Load, for example, 'CML Bride à collerette tous PN2'."));
                    return Result.Cancelled;
                }

                var fpt = baseFlangeSymbol.Family.FamilyPlacementType;
                if (fpt != FamilyPlacementType.OneLevelBased &&
                    fpt != FamilyPlacementType.TwoLevelsBased)
                {
                    TaskDialog.Show(UiLanguage.T("Bride incompatible", "Incompatible Flange"),
                        UiLanguage.T($"La famille '{baseFlangeSymbol.FamilyName}' a un type de placement non supporté : {fpt}.\nUtilise une bride OneLevelBased/TwoLevelsBased (non hébergée).",
                            $"The family '{baseFlangeSymbol.FamilyName}' uses an unsupported placement type: {fpt}.\nUse a OneLevelBased/TwoLevelsBased flange (not hosted)."));
                    return Result.Failed;
                }

                // Vérifier 2 connecteurs piping (pré-check non destructif)
                bool flangeHasTwoConnectors;
                using (var tg = new TransactionGroup(doc, "Pré-check bride"))
                {
                    tg.Start();
                    using (var t = new Transaction(doc, "Temp"))
                    {
                        t.Start();
                        if (!baseFlangeSymbol.IsActive) baseFlangeSymbol.Activate();
                        Level lvl = GuessAnyLevel(doc);
                        var temp = doc.Create.NewFamilyInstance(XYZ.Zero, baseFlangeSymbol, lvl, StructuralType.NonStructural);
                        doc.Regenerate();
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
                        bool selectedIsMech = IsCat(acc, BuiltInCategory.OST_MechanicalEquipment);

                        using (new PinScope(acc))
                        {
                            var accConns = GetPipingEndConnectors(acc).ToList();
                            if (accConns.Count == 0) { skipped++; continue; }

                            var symbolForThisElement = ResolveFlangeTypeForElementPN(doc, baseFlangeSymbol, acc) ?? baseFlangeSymbol;
                            if (!symbolForThisElement.IsActive) symbolForThisElement.Activate();

                            foreach (var accConn in accConns)
                            {
                                try
                                {
                                    if (!TryGetOrigin(accConn, out _)) continue; // logique → ignore
                                    if (AlreadyHasFlangeAtConnector(accConn)) continue;

                                    // voisin pipe/fitting
                                    Connector neighbor = null;
                                    if (selectedIsMech)
                                    {
                                        if (!TryGetDirectPipeOrFittingNeighbor(accConn, acc.Id, out neighbor))
                                            continue; // côté équipement : on n'insère que si réseau
                                    }
                                    else
                                    {
                                        neighbor = GetFirstPhysicalPipingOther(accConn, acc.Id);
                                    }

                                    // ======= Garde-fous d’adjacence (maintenant **aussi** en 2023) =======
                                    if (ShouldSkipByAdjacencyGeneric(doc, acc, accConn, neighbor, symbolForThisElement))
                                    {
                                        skipped++;
                                        continue;
                                    }
                                    // ======================================================================

                                    if (mode2025 && flangeHasTwoConnectors)
                                    {
                                        InsertFlangeBetween_SimpleConnect(doc, symbolForThisElement, accConn, neighbor);
                                    }
                                    else
                                    {
                                        if (neighbor != null && flangeHasTwoConnectors)
                                            InsertFlangeBetween(doc, symbolForThisElement, accConn, neighbor, anchorToAccessory: false);
                                        else
                                            PlaceFlangeOnOneSide(doc, symbolForThisElement, accConn, anchorToAccessory: false);
                                    }

                                    placed++;
                                }
                                catch
                                {
                                    // Fallback minimal
                                    try
                                    {
                                        XYZ pA = SafeOriginOr(accConn, XYZ.Zero);
                                        XYZ dirA = SafeDirection(accConn);
                                        Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);
                                        var fi = doc.Create.NewFamilyInstance(pA, symbolForThisElement, lvl, StructuralType.NonStructural);
                                        doc.Regenerate();
                                        var fConn = GetPipingEndConnectors(fi).FirstOrDefault();
                                        if (fConn != null)
                                        {
                                            AlignConnectorDirection(fi, fConn, -dirA);
                                            TrySetNominalDiameter(fi, accConn);
                                            TrySetConnDescriptionParameters(fi, "Accessoire", "Canalisations");
                                            EnsureSenseByFlipParameter(fi, -dirA); // sécurité sens
                                            TryCopyPnToFlangeInstance(fi, accConn);
                                            doc.Regenerate();
                                        }
                                        placed++;
                                    }
                                    catch { skipped++; }
                                }
                            }
                        }
                    }

                    doc.Regenerate();
                    t.Commit();
                }

                TaskDialog.Show(UiLanguage.T("Brides", "Flanges"), UiLanguage.T($"Brides posées : {placed}\nIgnorées/échouées : {skipped}", $"Flanges placed: {placed}\nSkipped/failed: {skipped}"));
                return Result.Succeeded;
            }
            finally
            {
                uiApp.DialogBoxShowing -= autoYes;
            }
        }

        // ---------- Adjacent rules (communes 2023/2025) ----------
        // Interdiction bride si voisin immédiat = accessoire ou coude, quelle que soit la sélection
        private static bool ShouldSkipByAdjacencyGeneric(Document doc, FamilyInstance selected, Connector accConn, Connector neighborConn, FamilySymbol flangeSym)
        {
            var nOwner = neighborConn?.Owner;

            bool selIsAccessory = IsCat(selected, BuiltInCategory.OST_PipeAccessory);
            bool selIsMech = IsCat(selected, BuiltInCategory.OST_MechanicalEquipment);
            bool neighIsAccessory = nOwner != null && IsCat(nOwner, BuiltInCategory.OST_PipeAccessory);
            bool neighIsMech = nOwner != null && IsCat(nOwner, BuiltInCategory.OST_MechanicalEquipment);

            // 1) Accessoire ↔ Accessoire
            if (selIsAccessory && neighIsAccessory) return true;

            // 2) Equipement ↔ Accessoire (symétrique)
            if ((selIsMech && neighIsAccessory) || (selIsAccessory && neighIsMech)) return true;

            // 3) Coude juste à côté
            if (nOwner != null && IsLikelyElbow(nOwner)) return true;

            // 4) Proximité dans l'axe : accessoire/coude très proche → interdit
            XYZ pA = SafeOriginOr(accConn, XYZ.Zero);
            XYZ dirA;
            if (neighborConn != null && TryGetOrigin(neighborConn, out var pN)) dirA = (pN - pA);
            else dirA = SafeDirection(accConn);
            if (dirA.IsAlmostEqualTo(XYZ.Zero)) dirA = SafeDirection(accConn);
            dirA = dirA.Normalize();

            double thicknessFt = EstimateFlangeThicknessFt(flangeSym);
            var excludeIds = new HashSet<ElementId> { selected.Id };
            if (nOwner != null) excludeIds.Add(nOwner.Id);

            if (HasPipeAccessoryVeryCloseAccurate(doc, pA, dirA, thicknessFt + 50 * MM, 80 * MM, excludeIds)) return true;
            if (HasElbowVeryCloseAccurate(doc, pA, dirA, thicknessFt + 50 * MM, 80 * MM, excludeIds)) return true;

            return false;
        }

        // Recopie HYD_PN sur l'instance de la bride si le paramètre existe (en plus du mapping de type)
        private static void TryCopyPnToFlangeInstance(FamilyInstance flange, Element elementWithPN)
        {
            if (flange == null || elementWithPN == null) return;
            var src = GetHydPnParam(elementWithPN);               // instance ou type de l’élément source
            if (src == null) return;

            var dst = FindParamByName(flange, "HYD_PN");          // param de l'instance de bride
            if (dst == null || dst.IsReadOnly) return;

            CopyPnValue(dst, src);                                 // gère int/double/string
        }

        // 2025 : insertion robuste en priorisant la Description du connecteur ("Accessoire" / "Canalisations")
        private static void InsertFlangeBetween_SimpleConnect(Document doc, FamilySymbol flangeSymbol, Connector accConn, Connector neighbor)
        {
            if (flangeSymbol == null || accConn == null || neighbor == null) return;
            if (!TryGetOrigin(accConn, out var pA)) throw new InvalidOperationException("Connecteur accessoire logique.");
            if (!TryGetOrigin(neighbor, out var pN)) throw new InvalidOperationException("Connecteur voisin logique.");

            XYZ v = pN - pA;
            XYZ dir = v.GetLength() < 1e-6 ? SafeDirection(accConn) : v.Normalize();
            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            // Déconnecter avant insertion
            try { if (accConn.IsConnectedTo(neighbor)) accConn.DisconnectFrom(neighbor); } catch { }

            var flange = doc.Create.NewFamilyInstance(pA, flangeSymbol, lvl, StructuralType.NonStructural);
            doc.Regenerate();

            // --- Choix initial des connecteurs
            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count < 2) throw new InvalidOperationException("Bride sans connecteurs Piping.");

            var pair = ChooseFlangePairByDesc(fConns, dir);
            Connector fAcc = pair.cAcc;
            Connector fPipe = pair.cPipe;

            // Orientation : côté accessoire = -dir
            AlignConnectorDirection(flange, fAcc, -dir);
            doc.Regenerate();

            // Re-snap après rotation
            fConns = GetPipingEndConnectors(flange).ToList();
            pair = ChooseFlangePairByDesc(fConns, dir);
            fAcc = pair.cAcc;
            fPipe = pair.cPipe;

            // Position : aligne le connecteur accessoire sur pA
            MoveBy(flange, pA - SafeOriginOr(fAcc, pA));
            doc.Regenerate();

            // DN + paramètres + PN instance + sens
            TrySetNominalDiameter(flange, accConn);
            TrySetConnDescriptionParameters(flange, "Accessoire", "Canalisations");
            TryCopyPnToFlangeInstance(flange, accConn?.Owner);
            EnsureSenseByFlipParameter(flange, -dir);

            // Connexion accessoire
            if (!ConnectWithAutoFlip(fAcc, accConn))
                throw new InvalidOperationException("Connexion bride→accessoire impossible (2025).");
            doc.Regenerate();

            // Connexion canalisation
            fConns = GetPipingEndConnectors(flange).ToList();
            pair = ChooseFlangePairByDesc(fConns, dir);
            fAcc = pair.cAcc;
            fPipe = pair.cPipe;

            bool neighborIsReducer = IsLikelyReducer(neighbor.Owner);
            bool connectedPipe = ConnectWithAutoFlip(fPipe, neighbor);
            if (!connectedPipe)
            {
                // handshake micro-gap
                MoveBy(flange, dir * MICRO_GAP); doc.Regenerate();

                fConns = GetPipingEndConnectors(flange).ToList();
                pair = ChooseFlangePairByDesc(fConns, dir);
                fAcc = pair.cAcc;
                fPipe = pair.cPipe;

                if (!fAcc.IsConnectedTo(accConn))
                    if (!ConnectWithAutoFlip(fAcc, accConn))
                        throw new InvalidOperationException("Perte de connexion accessoire durant handshake.");

                if (!ConnectWithAutoFlip(fPipe, neighbor))
                    throw new InvalidOperationException("Connexion bride→voisin impossible (après micro-gap).");

                MoveBy(flange, -dir * MICRO_GAP); doc.Regenerate();
            }
            else if (neighborIsReducer)
            {
                // recalcul réduit "avalé"
                MoveBy(flange, dir * MICRO_GAP); doc.Regenerate();
                MoveBy(flange, -dir * MICRO_GAP); doc.Regenerate();
            }

            // léger nudge solveur
            MoveBy(flange, dir * MICRO_NUDGE); flange.Document.Regenerate();
            MoveBy(flange, -dir * MICRO_NUDGE); flange.Document.Regenerate();

            // Recaler précisément sur l’accessoire si besoin
            fConns = GetPipingEndConnectors(flange).ToList();
            pair = ChooseFlangePairByDesc(fConns, dir);
            fAcc = pair.cAcc;
            fPipe = pair.cPipe;

            XYZ miss = pA - SafeOriginOr(fAcc, pA);
            if (miss.GetLength() > COLOC_TOL)
            {
                MoveBy(flange, miss);
                doc.Regenerate();
            }
        }

        // Essaie d'abord "Description du connecteur" (Accessoire/Canalisation), sinon fallback alignement
        // Renvoie (côté Accessoire, côté Canalisation). Priorise la Description du connecteur.
        private static (Connector cAcc, Connector cPipe) ChooseFlangePairByDesc(List<Connector> conns, XYZ dir)
        {
            var acc = FindConnByDescTokens(conns, TOK_ACC);
            var pipe = FindConnByDescTokens(conns, TOK_PIPE);

            // Si une seule description trouvée, on déduit l’autre
            if (acc == null && pipe != null)
            {
                var chosenPipe = pipe;
                acc = conns.FirstOrDefault(c => !ReferenceEquals(c, chosenPipe));
            }
            if (pipe == null && acc != null)
            {
                var chosenAcc = acc;
                pipe = conns.FirstOrDefault(c => !ReferenceEquals(c, chosenAcc));
            }

            // Toujours ambigu/absent → fallback directionnel robuste
            if (acc == null || pipe == null || ReferenceEquals(acc, pipe))
            {
                acc = PickBestAligned(conns, -dir);
                var chosenAcc2 = acc;
                pipe = conns.FirstOrDefault(c => !ReferenceEquals(c, chosenAcc2)) ?? PickBestAligned(conns, dir);
            }
            return (acc, pipe);
        }


        private static void TryCopyPnToFlangeInstance(FamilyInstance flange, Connector srcConn)
        {
            if (srcConn == null) return;
            TryCopyPnToFlangeInstance(flange, srcConn.Owner);
        }

        // Assure le “bon sens” en tentant de basculer un booléen de flip si la famille en expose un
        // Assure le “bon sens” en basculant un booléen de flip (0/1) si la famille en expose un
        private static void EnsureSenseByFlipParameter(FamilyInstance flange, XYZ accDirMinus)
        {
            if (!TryFindFlipParameter(flange, out var flipParam)) return;

            var conns = GetPipingEndConnectors(flange).ToList();
            var descAcc = FindConnByDescTokens(conns, TOK_ACC);
            if (descAcc == null) return;

            var bestAcc = PickBestAligned(conns, accDirMinus);
            if (ReferenceEquals(descAcc, bestAcc)) return; // déjà bon

            int initialInt = flipParam.AsInteger();
            int flipped = initialInt == 0 ? 1 : 0;

            try { flipParam.Set(flipped); flange.Document.Regenerate(); }
            catch { return; }

            conns = GetPipingEndConnectors(flange).ToList();
            var descAcc2 = FindConnByDescTokens(conns, TOK_ACC);
            var bestAcc2 = PickBestAligned(conns, accDirMinus);

            if (!ReferenceEquals(descAcc2, bestAcc2))
            {
                try { flipParam.Set(initialInt); flange.Document.Regenerate(); } catch { }
            }
        }


        private static bool TryFindFlipParameter(FamilyInstance fi, out Parameter flip)
        {
            flip = null;
            string[] names =
            {
                "Flip", "Reverse", "Inverser", "Sens", "Orientation", "Swap",
                "HYD_Flip", "BRD_Flip", "BRIDE_Flip"
            };

            foreach (var n in names)
            {
                var p = fi.LookupParameter(n);
                if (p != null && p.StorageType == StorageType.Integer && !p.IsReadOnly)
                {
                    flip = p; return true;
                }
            }
            // tenter côté type
            var sym = fi.Symbol;
            if (sym != null)
            {
                foreach (var n in names)
                {
                    var p = sym.LookupParameter(n);
                    if (p != null && p.StorageType == StorageType.Integer && !p.IsReadOnly)
                    {
                        flip = p; return true;
                    }
                }
            }
            return false;
        }

        // ---------- Placement 2023 (logique conservée + sens/param) ----------
        private static void InsertFlangeBetween(Document doc, FamilySymbol flangeSymbol, Connector accConn, Connector neighbor, bool anchorToAccessory)
        {
            if (!flangeSymbol.IsActive) flangeSymbol.Activate();

            if (!TryGetOrigin(accConn, out var pA)) throw new InvalidOperationException("Connecteur accessoire logique.");
            if (!TryGetOrigin(neighbor, out var pN)) throw new InvalidOperationException("Connecteur voisin logique.");

            XYZ dirA = (pN - pA);
            if (dirA.GetLength() < EPS) dirA = SafeDirection(accConn);
            dirA = dirA.Normalize();

            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            try { if (accConn.IsConnectedTo(neighbor)) accConn.DisconnectFrom(neighbor); } catch { }

            var flange = doc.Create.NewFamilyInstance(pA + dirA * PLACE_OFFSET_BETWEEN, flangeSymbol, lvl, StructuralType.NonStructural);
            doc.Regenerate();

            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count < 1) throw new InvalidOperationException("Bride sans connecteurs Piping.");

            var fToAcc_guess = FindConnByDescTokens(fConns, TOK_ACC)
                               ?? ChooseFlangeConnForAccessory_Initial(fConns, dirA);

            AlignConnectorDirection(flange, fToAcc_guess, -dirA);
            doc.Regenerate();

            fConns = GetPipingEndConnectors(flange).ToList();
            var fToAcc = FindConnByDescTokens(fConns, TOK_ACC) ?? ChooseAccessoryFinal(fConns, dirA, pA);
            var fToPipe = FindConnByDescTokens(fConns, TOK_PIPE) ?? fConns.FirstOrDefault(c => !ReferenceEquals(c, fToAcc)) ?? fToAcc;

            double edge = anchorToAccessory ? 0.0 : ComputeEdgeOffsetFt(flange);
            ElementTransformUtils.MoveElement(doc, flange.Id, (pA - fToAcc.Origin) - dirA * edge);
            doc.Regenerate();

            TrySetNominalDiameter(flange, accConn);
            TrySetConnDescriptionParameters(flange, "Accessoire", "Canalisations");
            EnsureSenseByFlipParameter(flange, -dirA);
            TryCopyPnToFlangeInstance(flange, accConn);

            if (!ConnectWithAutoFlip(fToAcc, accConn))
                throw new InvalidOperationException("Connexion bride→accessoire impossible.");
            if (!ConnectWithAutoFlip(fToPipe, neighbor))
                throw new InvalidOperationException("Connexion bride→voisin impossible.");

            ReAnchorToAccessory(flange, accConn, ref fToAcc, -dirA, 0.0);
            doc.Regenerate();
        }

        private static void PlaceFlangeOnOneSide(Document doc, FamilySymbol flangeSymbol, Connector accConn, bool anchorToAccessory)
        {
            if (!flangeSymbol.IsActive) flangeSymbol.Activate();

            if (!TryGetOrigin(accConn, out var pA)) throw new InvalidOperationException("Connecteur accessoire logique.");
            XYZ dirA = SafeDirection(accConn);

            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            var flange = doc.Create.NewFamilyInstance(pA + dirA * PLACE_OFFSET_ONESIDE, flangeSymbol, lvl, StructuralType.NonStructural);
            doc.Regenerate();

            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count == 0) throw new InvalidOperationException("Bride sans connecteurs Piping.");

            var fToAcc_guess = FindConnByDescTokens(fConns, TOK_ACC)
                               ?? ChooseFlangeConnForAccessory_Initial(fConns, dirA);

            AlignConnectorDirection(flange, fToAcc_guess, -dirA);
            doc.Regenerate();

            fConns = GetPipingEndConnectors(flange).ToList();
            var fToAcc = FindConnByDescTokens(fConns, TOK_ACC) ?? ChooseAccessoryFinal(fConns, dirA, pA);
            var fToPipe = fConns.FirstOrDefault(c => !ReferenceEquals(c, fToAcc)) ?? PickBestAligned(fConns, dirA);

            double edge = anchorToAccessory ? 0.0 : ComputeEdgeOffsetFt(flange);
            ElementTransformUtils.MoveElement(doc, flange.Id, (pA - fToAcc.Origin) - dirA * edge);
            doc.Regenerate();

            TrySetNominalDiameter(flange, accConn);
            TrySetConnDescriptionParameters(flange, "Accessoire", "Canalisations");
            EnsureSenseByFlipParameter(flange, -dirA);
            TryCopyPnToFlangeInstance(flange, accConn);

            if (!ConnectWithAutoFlip(fToAcc, accConn))
                throw new InvalidOperationException("Connexion bride (one-side) impossible.");

            ReAnchorToAccessory(flange, accConn, ref fToAcc, -dirA, 0.0);
            doc.Regenerate();
        }

        // ---------- Proximité : Accessoires / Coudes ----------
        private static bool HasPipeAccessoryVeryCloseAccurate(
            Document doc, XYZ origin, XYZ dir, double maxDistFt, double radialFt, ISet<ElementId> exclude = null)
        {
            XYZ target = origin + dir * maxDistFt;
            XYZ min = new XYZ(Math.Min(origin.X, target.X), Math.Min(origin.Y, target.Y), Math.Min(origin.Z, target.Z));
            XYZ max = new XYZ(Math.Max(origin.X, target.X), Math.Max(origin.Y, target.Y), Math.Max(origin.Z, target.Z));
            min -= new XYZ(radialFt, radialFt, radialFt);
            max += new XYZ(radialFt, radialFt, radialFt);

            Outline o = new Outline(min, max);
            var bbFilter = new BoundingBoxIntersectsFilter(o);

            var candidates = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeAccessory)
                .WhereElementIsNotElementType()
                .WherePasses(bbFilter)
                .Cast<FamilyInstance>()
                .Where(fi => exclude == null || !exclude.Contains(fi.Id))
                .ToList();

            foreach (var fi in candidates)
            {
                var cm = fi.MEPModel?.ConnectorManager;
                if (cm == null) continue;

                foreach (Connector c in cm.Connectors)
                {
                    if (c.Domain != Domain.DomainPiping) continue;
                    if (!TryGetOrigin(c, out var co)) continue;

                    XYZ v = co - origin;
                    double t = v.DotProduct(dir);
                    if (t < 0 || t > maxDistFt) continue;
                    XYZ radial = v - dir * t;
                    if (radial.GetLength() <= radialFt)
                        return true;
                }
            }
            return false;
        }

        private static bool HasElbowVeryCloseAccurate(
            Document doc, XYZ origin, XYZ dir, double maxDistFt, double radialFt, ISet<ElementId> exclude = null)
        {
            XYZ target = origin + dir * maxDistFt;
            XYZ min = new XYZ(Math.Min(origin.X, target.X), Math.Min(origin.Y, target.Y), Math.Min(origin.Z, target.Z));
            XYZ max = new XYZ(Math.Max(origin.X, target.X), Math.Max(origin.Y, target.Y), Math.Max(origin.Z, target.Z));
            min -= new XYZ(radialFt, radialFt, radialFt);
            max += new XYZ(radialFt, radialFt, radialFt);

            Outline o = new Outline(min, max);
            var bbFilter = new BoundingBoxIntersectsFilter(o);

            var cands = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .WhereElementIsNotElementType()
                .WherePasses(bbFilter)
                .Cast<FamilyInstance>()
                .Where(fi => (exclude == null || !exclude.Contains(fi.Id)) && IsLikelyElbow(fi))
                .ToList();

            foreach (var fi in cands)
            {
                var cm = fi.MEPModel?.ConnectorManager;
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.Domain != Domain.DomainPiping) continue;
                    if (!TryGetOrigin(c, out var co)) continue;

                    XYZ v = co - origin;
                    double t = v.DotProduct(dir);
                    if (t < 0 || t > maxDistFt) continue;
                    XYZ radial = v - dir * t;
                    if (radial.GetLength() <= radialFt)
                        return true;
                }
            }
            return false;
        }

        // ---------- Règles de choix connecteurs ----------
        private static string ConnDesc(Connector c)
        {
            try { return (c.Description ?? "").Trim().ToLowerInvariant(); }
            catch { return ""; }
        }

        private static Connector FindConnByDescTokens(IEnumerable<Connector> conns, string[] tokens)
        {
            foreach (var t in tokens)
            {
                var hit = conns.FirstOrDefault(c => ConnDesc(c).Contains(t));
                if (hit != null) return hit;
            }
            return null;
        }

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

            // tie-break : plus proche du plan normal à dir
            return byDir.OrderBy(c => Math.Abs((c.Origin - planePoint).DotProduct(dir))).First();
        }

        // ---------- Géométrie & transform ----------
        private static bool TryGetOrigin(Connector c, out XYZ o)
        {
            try { o = c.Origin; return true; }
            catch { o = XYZ.Zero; return false; }
        }

        private static XYZ SafeOriginOr(Connector c, XYZ fallback)
            => TryGetOrigin(c, out var o) ? o : fallback;

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
                    if (TryGetOrigin(r, out var ro) && TryGetOrigin(c, out var co))
                    {
                        var v = ro - co;
                        if (!v.IsAlmostEqualTo(XYZ.Zero)) return v.Normalize();
                    }
                }
            }
            catch { }
            return XYZ.BasisZ;
        }

        private static XYZ GetBasisZ(Connector c)
        {
            try
            {
                var cs = c.CoordinateSystem;
                var z = cs != null ? cs.BasisZ : XYZ.BasisZ;
                if (z.IsAlmostEqualTo(XYZ.Zero)) return XYZ.BasisZ;
                return z.Normalize();
            }
            catch { return XYZ.BasisZ; }
        }

        private static Connector PickBestAligned(IEnumerable<Connector> conns, XYZ targetDir)
        {
            targetDir = targetDir.Normalize();
            return conns
                .OrderByDescending(c =>
                {
                    var d = GetBasisZ(c);
                    double dot = Math.Max(-1.0, Math.Min(1.0, d.DotProduct(targetDir)));
                    return dot;
                })
                .First();
        }

        private static XYZ GetElementPivot(FamilyInstance fi)
        {
            var lp = fi.Location as LocationPoint;
            if (lp != null) return lp.Point;
            var bb = fi.get_BoundingBox(null);
            if (bb != null) return (bb.Min + bb.Max) * 0.5;
            return XYZ.Zero;
        }

        private static void AlignConnectorDirection(FamilyInstance fi, Connector fiConn, XYZ targetDir)
        {
            if (fiConn == null) return;

            XYZ from = GetBasisZ(fiConn);
            XYZ to = targetDir.Normalize();
            double dot = Math.Max(-1.0, Math.Min(1.0, from.DotProduct(to)));
            double angle = Math.Acos(dot);
            if (double.IsNaN(angle) || angle < 1e-6) return;

            XYZ axis = from.CrossProduct(to);
            if (axis.IsAlmostEqualTo(XYZ.Zero))
                axis = Math.Abs(from.DotProduct(XYZ.BasisX)) < 0.9 ? from.CrossProduct(XYZ.BasisX) : from.CrossProduct(XYZ.BasisY);

            axis = axis.Normalize();
            var line = Line.CreateUnbound(GetElementPivot(fi), axis);
            ElementTransformUtils.RotateElement(fi.Document, fi.Id, line, angle);
        }

        private static double ComputeEdgeOffsetFt(FamilyInstance flange)
        {
            string[] names = { "Epaisseur", "Thickness", "Thk", "Gasket", "Bride_Epaisseur" };
            foreach (Parameter p in flange.Symbol.Parameters)
                if (p.StorageType == StorageType.Double &&
                    names.Any(n => (p.Definition?.Name ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                    return Math.Max(0.0, p.AsDouble() * 0.5);
            return 15 * MM;
        }

        private static double EstimateFlangeThicknessFt(FamilySymbol sym)
        {
            string[] names = { "Epaisseur", "Thickness", "Thk", "Bride_Epaisseur" };
            foreach (Parameter p in sym.Parameters)
                if (p.StorageType == StorageType.Double &&
                    names.Any(n => (p.Definition?.Name ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                    return Math.Max(1 * MM, p.AsDouble());
            return 30 * MM;
        }

        private static bool ConnectWithAutoFlip(Connector a, Connector b)
        {
            if (a == null || b == null) return false;
            if (!TryGetOrigin(a, out _)) return false;
            if (!TryGetOrigin(b, out _)) return false;

            try { a.ConnectTo(b); return true; }
            catch
            {
                try { foreach (Connector ra in a.AllRefs) if (ra.IsConnected) a.DisconnectFrom(ra); } catch { }
                try { foreach (Connector rb in b.AllRefs) if (rb.IsConnected) b.DisconnectFrom(rb); } catch { }
                try { a.ConnectTo(b); return true; } catch { return false; }
            }
        }

        private static void TrySetConnDescriptionParameters(FamilyInstance fi, string accText, string pipeText)
        {
            // on tente sur instance, puis sur type ; plusieurs alias supportés
            string[] accNames =
            {
                "Description du connecteur - Accessoire",
                "Description du connecteur (Accessoire)",
                "Description du connecteur A",
                "HYD_ConnDesc_A","Conn_Desc_A","Connector Description A"
            };
            string[] pipeNames =
            {
                "Description du connecteur - Canalisations",
                "Description du connecteur (Canalisations)",
                "Description du connecteur P",
                "HYD_ConnDesc_P","Conn_Desc_P","Connector Description P"
            };
            string[] singleNames = { "Description du connecteur", "Connector Description" };

            bool setAcc = TrySetTextParam(fi, accNames, accText);
            bool setPipe = TrySetTextParam(fi, pipeNames, pipeText);

            var sym = fi.Symbol;
            if (!setAcc && sym != null) setAcc = TrySetTextParam(sym, accNames, accText);
            if (!setPipe && sym != null) setPipe = TrySetTextParam(sym, pipeNames, pipeText);

            if (!setAcc && !setPipe)
            {
                TrySetTextParam(fi, singleNames, $"{accText} / {pipeText}");
                if (sym != null) TrySetTextParam(sym, singleNames, $"{accText} / {pipeText}");
            }
        }
        private static bool TrySetTextParam(Element e, IEnumerable<string> names, string value)
        {
            foreach (var n in names)
            {
                var p = e.LookupParameter(n);
                if (p != null && p.StorageType == StorageType.String && !p.IsReadOnly)
                { try { p.Set(value ?? ""); return true; } catch { } }
            }
            return false;
        }

        private static void ReAnchorToAccessory(FamilyInstance flange,
                                        Connector accessoryConnInProject,
                                        ref Connector flangeAccessoryConn,
                                        XYZ dirA,
                                        double extraGapFt = 0.0)
        {
            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count == 0) return;

            var accOri = SafeOriginOr(accessoryConnInProject, XYZ.Zero);
            flangeAccessoryConn = fConns
                .OrderBy(c => SafeOriginOr(c, XYZ.Zero).DistanceTo(accOri))
                .First();

            XYZ delta = accOri - SafeOriginOr(flangeAccessoryConn, accOri);
            if (!delta.IsAlmostEqualTo(XYZ.Zero))
                MoveBy(flange, delta);                   // ✅ au lieu de ElementTransformUtils.MoveElement(...)

            if (extraGapFt > 0)
                MoveBy(flange, (-dirA) * extraGapFt);    // ✅ idem
        }


        // ---------- Divers helpers ----------
        private static bool IsCat(Element e, BuiltInCategory bic)
            => e?.Category != null && e.Category.Id.GetIdValue() == (int)bic;

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

        private static Connector GetFirstPhysicalPipingOther(Connector c, ElementId selfId)
        {
            try
            {
                foreach (Connector rc in c.AllRefs)
                {
                    if (rc == null) continue;
                    if (rc.Owner?.Id == selfId) continue;
                    if (rc.Domain != Domain.DomainPiping) continue;
                    if (!TryGetOrigin(rc, out _)) continue;
                    return rc;
                }
            }
            catch { }
            return null;
        }

        private static bool TryGetDirectPipeOrFittingNeighbor(Connector c, ElementId selfId, out Connector neighborConn)
        {
            neighborConn = null;
            try
            {
                foreach (Connector rc in c.AllRefs)
                {
                    if (rc == null) continue;
                    if (rc.Owner?.Id == selfId) continue;
                    if (rc.Domain != Domain.DomainPiping) continue;
                    int cat = rc.Owner?.Category?.Id.GetIdValue() ?? 0;
                    bool isPipeOrFitting =
                        cat == (int)BuiltInCategory.OST_PipeCurves ||
                        cat == (int)BuiltInCategory.OST_PipeFitting;
                    if (isPipeOrFitting && TryGetOrigin(rc, out _))
                    { neighborConn = rc; return true; }
                }
            }
            catch { }
            return false;
        }

        private static bool AlreadyHasFlangeAtConnector(Connector accConn)
        {
            try
            {
                foreach (Connector rc in accConn.AllRefs)
                {
                    var owner = rc.Owner;
                    if (owner?.Category == null) continue;
                    bool isPipeAcc = IsCat(owner, BuiltInCategory.OST_PipeAccessory);
                    bool isFitting = IsCat(owner, BuiltInCategory.OST_PipeFitting);

                    string nm = (owner.Name ?? "").ToLowerInvariant();
                    string typ = (owner.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "").ToLowerInvariant();
                    if ((isPipeAcc || isFitting) && (nm.Contains("bride") || nm.Contains("flange") || typ.Contains("bride") || typ.Contains("flange")))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsLikelyReducer(Element e)
        {
            var fi = e as FamilyInstance;
            if (fi == null) return false;
            if (!IsCat(fi, BuiltInCategory.OST_PipeFitting)) return false;

            var conns = GetPipingEndConnectors(fi).ToList();
            if (conns.Count < 2) return false;

            double r0 = conns[0].Radius;
            double r1 = conns[1].Radius;
            return Math.Abs(r0 - r1) > 0.2 * MM;
        }

        private static bool IsLikelyElbow(Element e)
        {
            var fi = e as FamilyInstance;
            if (fi == null) return false;
            if (!IsCat(fi, BuiltInCategory.OST_PipeFitting)) return false;

            var conns = GetPipingEndConnectors(fi).ToList();
            if (conns.Count != 2) return false;

            var z0 = GetBasisZ(conns[0]);
            var z1 = GetBasisZ(conns[1]);
            double ad = Math.Abs(z0.DotProduct(z1)); // 1 → colinéaire, 0 → orthogonal
            return ad < 0.95;
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
            if (!TryGetOrigin(sourceConn, out _)) return;
            double dia = sourceConn.Radius * 2.0;
            var candidates = new[] { "Nominal Diameter", "DN", "Diameter", "Diamètre nominal", "Diamètre", "RBS_PIPE_DIAMETER" };
            foreach (Parameter p in flange.Parameters)
            {
                string name = p.Definition?.Name ?? "";
                if (!candidates.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (p.StorageType == StorageType.Double)
                { try { p.Set(dia); return; } catch { } }
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
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        // ---------- Vue 3D cache (si USE_RAYCAST = true) ----------
        static readonly Dictionary<int, ElementId> _view3dCache = new Dictionary<int, ElementId>();
        private static View3D GetCached3DView(Document doc)
        {
            int key = doc.GetHashCode();
            if (_view3dCache.TryGetValue(key, out var id))
            {
                var v = doc.GetElement(id) as View3D;
                if (v != null && !v.IsTemplate) return v;
            }
            var view = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);
            if (view != null) _view3dCache[key] = view.Id;
            return view;
        }

        // ---------- Recherche type de bride ----------
        private static FamilySymbol FindFlangeSymbol(Document doc)
        {
            if (FlangeChoiceCache.HasChoice)
            {
                var chosen = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(fs =>
                        fs?.Category != null &&
                       (fs.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_PipeAccessory ||
                        fs.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_PipeFitting) &&
                        string.Equals(fs.FamilyName, FlangeChoiceCache.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fs.Name, FlangeChoiceCache.SymbolName, StringComparison.OrdinalIgnoreCase));
                if (chosen != null) return chosen;
            }

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_PipeAccessory)
                .Cast<FamilySymbol>()
                .OrderBy(fs => fs.FamilyName).ThenBy(fs => fs.Name)
                .ToList();

            symbols = symbols.Where(fs =>
            {
                string both = ((fs.FamilyName ?? "") + " " + (fs.Name ?? "")).ToLowerInvariant();
                return !both.Contains("cml_bride pleine tous pn");
            }).ToList();
            if (symbols.Count == 0) return null;

            var pn2 = symbols.FirstOrDefault(fs =>
                string.Equals(fs.FamilyName, "CML Bride à collerette tous PN2", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fs.Name, "CML Bride à collerette tous PN2", StringComparison.OrdinalIgnoreCase));
            if (pn2 != null) return pn2;

            var filtered = symbols.Where(fs =>
            {
                string hay = ((fs.FamilyName ?? "") + " " + (fs.Name ?? "")).ToLowerInvariant();
                return hay.Contains("bride") || hay.Contains("flange");
            }).ToList();

            if (filtered.Count == 0) return null;

            return filtered
                .OrderByDescending(fs => fs.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_PipeAccessory)
                .ThenBy(fs => fs.FamilyName).ThenBy(fs => fs.Name)
                .First();
        }

        // ================= PN helpers + ResolveFlangeTypeForElementPN =================
        private static Parameter FindParamByName(Element e, string name)
        {
            if (e == null || string.IsNullOrWhiteSpace(name)) return null;
            foreach (Parameter p in e.Parameters)
            {
                var n = p.Definition?.Name;
                if (!string.IsNullOrEmpty(n) &&
                    string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }
        private static Parameter GetHydPnParam(Element e)
        {
            var p = FindParamByName(e, "HYD_PN");
            if (p != null) return p;
            var fi = e as FamilyInstance;
            if (fi?.Symbol != null) p = FindParamByName(fi.Symbol, "HYD_PN");
            return p;
        }
        private static Parameter GetHydPnParam(FamilySymbol fs) => FindParamByName(fs, "HYD_PN");

        private static string NormalizePnString(string s)
        {
            s = (s ?? "").Trim().ToUpperInvariant().Replace(" ", "");
            return s;
        }
        private static bool TryReadPnFromParam(Parameter p, out string pnCanonical, out string pnForName)
        {
            pnCanonical = null; pnForName = null;
            if (p == null) return false;

            switch (p.StorageType)
            {
                case StorageType.Integer:
                    pnForName = p.AsInteger().ToString(); pnCanonical = "I:" + pnForName; return true;
                case StorageType.Double:
                    pnForName = ((int)Math.Round(p.AsDouble())).ToString(); pnCanonical = "I:" + pnForName; return true;
                case StorageType.String:
                    {
                        string s = p.AsString() ?? "";
                        string digits = new string(s.Where(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out int vs))
                        { pnForName = vs.ToString(); pnCanonical = "I:" + pnForName; return true; }
                        pnForName = s; pnCanonical = "S:" + NormalizePnString(s); return true;
                    }
                default: return false;
            }
        }
        private static string CanonicalFromParam(Parameter p)
            => TryReadPnFromParam(p, out var c, out _) ? c : null;

        private static void CopyPnValue(Parameter targetTypeParam, Parameter srcParam)
        {
            if (targetTypeParam == null || srcParam == null) return;
            try
            {
                if (targetTypeParam.StorageType == StorageType.Integer)
                {
                    if (srcParam.StorageType == StorageType.Integer)
                        targetTypeParam.Set(srcParam.AsInteger());
                    else if (srcParam.StorageType == StorageType.Double)
                        targetTypeParam.Set((int)Math.Round(srcParam.AsDouble()));
                    else
                    {
                        string s = srcParam.AsString() ?? "";
                        string digits = new string(s.Where(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out int vi)) targetTypeParam.Set(vi);
                    }
                }
                else if (targetTypeParam.StorageType == StorageType.Double)
                {
                    if (srcParam.StorageType == StorageType.Integer)
                        targetTypeParam.Set((double)srcParam.AsInteger());
                    else if (srcParam.StorageType == StorageType.Double)
                        targetTypeParam.Set(srcParam.AsDouble());
                    else
                    {
                        string s = srcParam.AsString() ?? "";
                        string digits = new string(s.Where(char.IsDigit).ToArray());
                        if (double.TryParse(digits, out double vd)) targetTypeParam.Set(vd);
                    }
                }
                else if (targetTypeParam.StorageType == StorageType.String)
                {
                    targetTypeParam.Set(srcParam.AsString() ?? "");
                }
            }
            catch { }
        }
        private static bool PnEquals(Parameter typePn, Parameter srcPn)
        {
            string a = CanonicalFromParam(typePn);
            string b = CanonicalFromParam(srcPn);
            return !string.IsNullOrEmpty(a) && a == b;
        }
        private static string BuildPnTypeName(string pnForName)
        {
            string s = (pnForName ?? "").Trim().ToUpperInvariant();
            if (!s.StartsWith("PN")) s = "PN" + s;
            return $"Bride {s}";
        }

        private static void MoveBy(FamilyInstance fi, XYZ delta)
        {
            if (delta.IsAlmostEqualTo(XYZ.Zero)) return;
            ElementTransformUtils.MoveElement(fi.Document, fi.Id, delta);
        }
        private static FamilySymbol ResolveFlangeTypeForElementPN(Document doc, FamilySymbol baseSymbol, Element elementWithPN)
        {
            if (baseSymbol == null || elementWithPN == null) return baseSymbol;

            var srcPn = GetHydPnParam(elementWithPN);
            if (srcPn == null) return baseSymbol;
            if (!TryReadPnFromParam(srcPn, out var pnCanonical, out var pnForName))
                return baseSymbol;

            var baseTypePn = GetHydPnParam(baseSymbol);
            if (baseTypePn == null) return baseSymbol;

            string cacheKey = $"{baseSymbol.Family.Id.GetIdValue()}|{pnCanonical}";
            if (_pnTypeCache.TryGetValue(cacheKey, out var cachedId))
            {
                var cached = doc.GetElement(cachedId) as FamilySymbol;
                if (cached != null) return cached;
            }

            var family = baseSymbol.Family;
            foreach (ElementId sid in family.GetFamilySymbolIds())
            {
                var fs = doc.GetElement(sid) as FamilySymbol;
                if (fs == null) continue;
                var fsPn = GetHydPnParam(fs);
                if (fsPn == null) continue;
                if (PnEquals(fsPn, srcPn))
                { _pnTypeCache[cacheKey] = fs.Id; return fs; }
            }

            string wantedName = BuildPnTypeName(pnForName);
            var byName = family.GetFamilySymbolIds()
                               .Select(id => doc.GetElement(id) as FamilySymbol)
                               .FirstOrDefault(fs => fs != null &&
                                    string.Equals(fs.Name, wantedName, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                var byNamePn = GetHydPnParam(byName);
                if (byNamePn != null && !PnEquals(byNamePn, srcPn))
                    CopyPnValue(byNamePn, srcPn);
                _pnTypeCache[cacheKey] = byName.Id;
                return byName;
            }

            FamilySymbol newSym = null;
            try { newSym = (baseSymbol.Duplicate(wantedName) as ElementType) as FamilySymbol; }
            catch
            {
                newSym = family.GetFamilySymbolIds()
                               .Select(id => doc.GetElement(id) as FamilySymbol)
                               .FirstOrDefault(fs => fs != null &&
                                   string.Equals(fs.Name, wantedName, StringComparison.OrdinalIgnoreCase));
                if (newSym == null) return baseSymbol;
            }

            var newPn = GetHydPnParam(newSym);
            if (newPn != null) CopyPnValue(newPn, srcPn);

            _pnTypeCache[cacheKey] = newSym.Id;
            return newSym;
        }
        // =====================================================================
    }
}

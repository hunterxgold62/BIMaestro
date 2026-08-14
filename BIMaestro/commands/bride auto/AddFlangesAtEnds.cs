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
using System.Text;

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

        private const double COLOC_TOL = 0.2 * MM;

        // ---- Tokens (lecture Description du connecteur – read-only)
        private static readonly string[] TOK_ACC = { "accessoire", "accessory", "acc" };
        private static readonly string[] TOK_PIPE = { "canalisations", "canalisation", "cana", "pipe", "piping" };

        // ---- Cache PN → FamilySymbol
        private static readonly Dictionary<string, ElementId> _pnTypeCache = new Dictionary<string, ElementId>();

        private sealed class PlacementIssue
        {
            public ElementId ElementId { get; set; }
            public string ElementName { get; set; }
            public XYZ Location { get; set; }
            public string Reason { get; set; }
        }

        private sealed class ConnectorSnapshot
        {
            public XYZ Origin { get; set; }
            public XYZ Direction { get; set; }
        }

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

                int placed = 0;
                int alreadyPresent = 0;
                int connectorCount = 0;
                var issues = new List<PlacementIssue>();

                // Ne garder que les obstacles qui existaient avant la commande. Sans cela,
                // les brides créées au début d'une grosse sélection bloquaient les suivantes.
                var initialAccessoryIds = new HashSet<ElementId>(
                    new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PipeAccessory)
                        .WhereElementIsNotElementType()
                        .ToElementIds());
                var initialFittingIds = new HashSet<ElementId>(
                    new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PipeFitting)
                        .WhereElementIsNotElementType()
                        .ToElementIds());

                // Limiter l'auto-validation aux messages internes susceptibles d'apparaître
                // pendant les modifications. Le rapport final doit rester visible.
                uiApp.DialogBoxShowing += autoYes;
                using (var t = new Transaction(doc, "Ajouter brides"))
                {
                    t.Start();
                    SuppressWarnings(t);

                    foreach (var acc in targets)
                    {
                        bool selectedIsMech = IsCat(acc, BuiltInCategory.OST_MechanicalEquipment);

                        using (new PinScope(acc))
                        {
                            var connectorSnapshots = GetPipingEndConnectors(acc)
                                .Select(c => new ConnectorSnapshot
                                {
                                    Origin = SafeOriginOr(c, XYZ.Zero),
                                    Direction = SafeDirection(c)
                                })
                                .ToList();
                            if (connectorSnapshots.Count == 0)
                            {
                                AddIssue(issues, acc, XYZ.Zero, "Aucun connecteur physique de canalisation.");
                                continue;
                            }

                            var symbolForThisElement = ResolveFlangeTypeForElementPN(doc, baseFlangeSymbol, acc) ?? baseFlangeSymbol;
                            if (!symbolForThisElement.IsActive) symbolForThisElement.Activate();

                            foreach (var snapshot in connectorSnapshots)
                            {
                                connectorCount++;
                                XYZ connectorLocation = snapshot.Origin;
                                Connector accConn = FindMatchingConnector(acc, snapshot);

                                if (accConn == null)
                                {
                                    AddIssue(issues, acc, connectorLocation, "Le connecteur n'est plus disponible après une modification précédente.");
                                    continue;
                                }

                                if (!TryGetOrigin(accConn, out connectorLocation))
                                {
                                    AddIssue(issues, acc, connectorLocation, "Connecteur logique ou sans position exploitable.");
                                    continue;
                                }

                                if (AlreadyHasFlangeAtConnector(accConn))
                                {
                                    alreadyPresent++;
                                    continue;
                                }

                                // Voisin pipe/fitting. Un accessoire ouvert accepte une pose sur un seul côté ;
                                // un équipement doit rester raccordé à son réseau.
                                Connector neighbor = null;
                                if (selectedIsMech)
                                {
                                    if (!TryGetDirectPipeOrFittingNeighbor(accConn, acc.Id, out neighbor))
                                    {
                                        AddIssue(issues, acc, connectorLocation, "Aucune canalisation ou raccord directement connecté à l'équipement.");
                                        continue;
                                    }
                                }
                                else
                                {
                                    neighbor = GetFirstPhysicalPipingOther(accConn, acc.Id);
                                }

                                if (TryGetAdjacencyBlockReason(
                                    doc, acc, accConn, neighbor, symbolForThisElement,
                                    initialAccessoryIds, initialFittingIds, out string blockReason))
                                {
                                    AddIssue(issues, acc, connectorLocation, blockReason);
                                    continue;
                                }

                                using (var st = new SubTransaction(doc))
                                {
                                    st.Start();
                                    try
                                    {
                                        FamilyInstance flange;
                                        if (neighbor != null && flangeHasTwoConnectors)
                                        {
                                            flange = mode2025
                                                ? InsertFlangeBetween_SimpleConnect(doc, symbolForThisElement, accConn, neighbor)
                                                : InsertFlangeBetween(doc, symbolForThisElement, accConn, neighbor, anchorToAccessory: false);
                                        }
                                        else
                                        {
                                            flange = PlaceFlangeOnOneSide(doc, symbolForThisElement, accConn, anchorToAccessory: false);
                                        }

                                        ValidatePlacement(doc, flange, acc.Id, neighbor?.Owner?.Id);
                                        if (st.Commit() != TransactionStatus.Committed)
                                            throw new InvalidOperationException("La sous-transaction de pose n'a pas été validée par Revit.");

                                        placed++;
                                    }
                                    catch (Exception ex)
                                    {
                                        if (st.GetStatus() == TransactionStatus.Started)
                                        {
                                            try { st.RollBack(); } catch { }
                                        }

                                        AddIssue(issues, acc, connectorLocation, ClassifyPlacementFailure(ex));
                                    }
                                }
                            }
                        }
                    }

                    doc.Regenerate();
                    t.Commit();
                }
                uiApp.DialogBoxShowing -= autoYes;

                if (issues.Count > 0)
                {
                    var failedIds = issues.Select(i => i.ElementId).Where(id => id != null).Distinct().ToList();
                    if (failedIds.Count > 0)
                    {
                        try { uiDoc.Selection.SetElementIds(failedIds); } catch { }
                    }
                }

                ShowPlacementReport(connectorCount, placed, alreadyPresent, issues);
                return Result.Succeeded;
            }
            finally
            {
                uiApp.DialogBoxShowing -= autoYes;
            }
        }

        // ---------- Adjacent rules (communes 2023/2025) ----------
        // Interdiction bride si voisin immédiat = accessoire ou coude, quelle que soit la sélection
        private static bool TryGetAdjacencyBlockReason(
            Document doc,
            FamilyInstance selected,
            Connector accConn,
            Connector neighborConn,
            FamilySymbol flangeSym,
            ISet<ElementId> initialAccessoryIds,
            ISet<ElementId> initialFittingIds,
            out string reason)
        {
            reason = null;
            var nOwner = neighborConn?.Owner;

            bool selIsAccessory = IsCat(selected, BuiltInCategory.OST_PipeAccessory);
            bool selIsMech = IsCat(selected, BuiltInCategory.OST_MechanicalEquipment);
            bool neighIsAccessory = nOwner != null && IsCat(nOwner, BuiltInCategory.OST_PipeAccessory);
            bool neighIsMech = nOwner != null && IsCat(nOwner, BuiltInCategory.OST_MechanicalEquipment);

            // 1) Accessoire ↔ Accessoire
            if (selIsAccessory && neighIsAccessory)
            {
                reason = "Manque de place : un autre accessoire est raccordé directement.";
                return true;
            }

            // 2) Equipement ↔ Accessoire (symétrique)
            if ((selIsMech && neighIsAccessory) || (selIsAccessory && neighIsMech))
            {
                reason = "Manque de place : liaison directe entre un équipement et un accessoire.";
                return true;
            }

            // 3) Coude juste à côté
            if (nOwner != null && IsLikelyElbow(nOwner))
            {
                reason = "Manque de place : un coude est raccordé directement.";
                return true;
            }

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

            if (TryGetPipeTooShortReason(neighborConn, thicknessFt, out reason)) return true;

            if (HasPipeAccessoryVeryCloseAccurate(doc, pA, dirA, thicknessFt + 50 * MM, 80 * MM, excludeIds, initialAccessoryIds))
            {
                reason = "Manque de place : un accessoire se trouve dans la zone nécessaire à la bride.";
                return true;
            }
            if (HasElbowVeryCloseAccurate(doc, pA, dirA, thicknessFt + 50 * MM, 80 * MM, excludeIds, initialFittingIds))
            {
                reason = "Manque de place : un coude se trouve dans la zone nécessaire à la bride.";
                return true;
            }

            return false;
        }

        private static bool TryGetPipeTooShortReason(Connector neighborConn, double flangeThicknessFt, out string reason)
        {
            reason = null;
            var pipe = neighborConn?.Owner as Pipe;
            var curve = (pipe?.Location as LocationCurve)?.Curve;
            if (curve == null) return false;

            double requiredFt = flangeThicknessFt + SAFETY_MARGIN;
            if (curve.Length + EPS >= requiredFt) return false;

            reason = $"Manque de place : canalisation trop courte ({curve.Length / MM:0} mm disponibles, {requiredFt / MM:0} mm requis).";
            return true;
        }

        private static void AddIssue(ICollection<PlacementIssue> issues, FamilyInstance element, XYZ location, string reason)
        {
            issues.Add(new PlacementIssue
            {
                ElementId = element?.Id,
                ElementName = GetElementLabel(element),
                Location = location ?? XYZ.Zero,
                Reason = string.IsNullOrWhiteSpace(reason) ? "Cause technique non précisée par Revit." : reason.Trim()
            });
        }

        private static string GetElementLabel(FamilyInstance element)
        {
            if (element == null) return "Élément inconnu";
            string family = element.Symbol?.FamilyName ?? element.Name ?? "Élément MEP";
            string type = element.Symbol?.Name ?? "";
            string label = string.Equals(family, type, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(type)
                ? family
                : $"{family} – {type}";
            return $"{label} (ID {element.Id.GetIdValue()})";
        }

        private static string ClassifyPlacementFailure(Exception ex)
        {
            string message = (ex?.Message ?? "").Trim();
            if (message.Length > 220) message = message.Substring(0, 220) + "…";
            string lower = message.ToLowerInvariant();

            if (lower.Contains("connexion") || lower.Contains("connect"))
                return "Connexion Revit impossible : connecteurs incompatibles, non alignés ou espace insuffisant. " + message;
            if (lower.Contains("niveau") || lower.Contains("level"))
                return "Aucun niveau Revit exploitable pour placer la bride. " + message;
            if (lower.Contains("connecteur") || lower.Contains("connector"))
                return "Famille de bride incompatible ou connecteur inexploitable. " + message;
            if (lower.Contains("transaction") || lower.Contains("failure"))
                return "Revit a refusé la modification. " + message;

            return string.IsNullOrWhiteSpace(message)
                ? "Échec technique non détaillé par Revit."
                : $"Échec Revit ({ex.GetType().Name}) : {message}";
        }

        private static void ShowPlacementReport(int connectorCount, int placed, int alreadyPresent, IList<PlacementIssue> issues)
        {
            var byReason = issues
                .GroupBy(i => i.Reason)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .ToList();

            var summary = new StringBuilder();
            summary.AppendLine($"Extrémités analysées : {connectorCount}");
            summary.AppendLine($"Brides posées et raccordées : {placed}");
            summary.AppendLine($"Brides déjà présentes : {alreadyPresent}");
            summary.Append($"Brides non installées : {issues.Count}");
            if (issues.Count > 0)
                summary.AppendLine("\n\nLes éléments concernés sont sélectionnés dans Revit.");

            var details = new StringBuilder();
            if (byReason.Count > 0)
            {
                details.AppendLine("RÉPARTITION PAR CAUSE");
                foreach (var group in byReason)
                    details.AppendLine($"• {group.Count()} × {group.Key}");
                details.AppendLine();
                details.AppendLine("EMPLACEMENTS");
            }

            const int maxDetailedIssues = 100;
            foreach (var issue in issues.Take(maxDetailedIssues))
            {
                XYZ p = issue.Location ?? XYZ.Zero;
                details.AppendLine($"• {issue.ElementName}");
                details.AppendLine($"  X {p.X / MM:0} mm ; Y {p.Y / MM:0} mm ; Z {p.Z / MM:0} mm");
                details.AppendLine($"  Cause : {issue.Reason}");
            }
            if (issues.Count > maxDetailedIssues)
                details.AppendLine($"… {issues.Count - maxDetailedIssues} autre(s) échec(s) non affiché(s). Les éléments restent sélectionnés.");

            var dialog = new TaskDialog(UiLanguage.T("Rapport des brides", "Flange Report"))
            {
                MainInstruction = issues.Count == 0
                    ? UiLanguage.T("Pose terminée sans erreur", "Placement completed without errors")
                    : UiLanguage.T($"{issues.Count} bride(s) non installée(s)", $"{issues.Count} flange(s) not installed"),
                MainContent = summary.ToString().TrimEnd(),
                ExpandedContent = details.ToString().TrimEnd(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };
            dialog.Show();
        }

        private static void ValidatePlacement(Document doc, FamilyInstance flange, ElementId accessoryOwnerId, ElementId neighborOwnerId)
        {
            if (flange == null || !flange.IsValidObject)
                throw new InvalidOperationException("Aucune instance de bride valide n'a été créée.");

            doc.Regenerate();
            var flangeConnectors = GetPipingEndConnectors(flange).ToList();
            if (flangeConnectors.Count == 0)
                throw new InvalidOperationException("La bride créée ne possède aucun connecteur Piping physique.");

            if (!HasPhysicalConnectionToOwner(flangeConnectors, accessoryOwnerId))
                throw new InvalidOperationException("La bride n'est pas raccordée à l'accessoire sélectionné.");

            if (neighborOwnerId != null &&
                !HasPhysicalConnectionToOwner(flangeConnectors, neighborOwnerId) &&
                !HasPhysicalNetworkConnection(flangeConnectors, accessoryOwnerId, flange.Id))
            {
                throw new InvalidOperationException(
                    "La bride n'est raccordée ni au voisin d'origine ni à un voisin réseau de remplacement.");
            }
        }

        private static bool HasPhysicalConnectionToOwner(IEnumerable<Connector> connectors, ElementId ownerId)
        {
            if (ownerId == null) return false;
            foreach (var connector in connectors)
            {
                try
                {
                    foreach (Connector reference in connector.AllRefs)
                    {
                        if (reference?.Owner?.Id != ownerId || reference.Domain != Domain.DomainPiping) continue;
                        if (!connector.IsConnectedTo(reference)) continue;
                        if (!TryGetOrigin(connector, out XYZ a) || !TryGetOrigin(reference, out XYZ b)) continue;
                        if (a.DistanceTo(b) <= 1.0 * MM) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool HasPhysicalNetworkConnection(
            IEnumerable<Connector> connectors,
            ElementId accessoryOwnerId,
            ElementId flangeOwnerId)
        {
            foreach (var connector in connectors)
            {
                try
                {
                    foreach (Connector reference in connector.AllRefs)
                    {
                        ElementId ownerId = reference?.Owner?.Id;
                        if (ownerId == null || ownerId == accessoryOwnerId || ownerId == flangeOwnerId) continue;
                        if (reference.Domain != Domain.DomainPiping || !connector.IsConnectedTo(reference)) continue;
                        if (!TryGetOrigin(connector, out XYZ a) || !TryGetOrigin(reference, out XYZ b)) continue;
                        if (a.DistanceTo(b) <= 1.0 * MM) return true;
                    }
                }
                catch { }
            }
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
        private static FamilyInstance InsertFlangeBetween_SimpleConnect(Document doc, FamilySymbol flangeSymbol, Connector accConn, Connector neighbor)
        {
            if (flangeSymbol == null) throw new ArgumentNullException(nameof(flangeSymbol));
            if (accConn == null) throw new ArgumentNullException(nameof(accConn));
            if (neighbor == null) throw new ArgumentNullException(nameof(neighbor));
            if (!TryGetOrigin(accConn, out var pA)) throw new InvalidOperationException("Connecteur accessoire logique.");
            if (!TryGetOrigin(neighbor, out var pN)) throw new InvalidOperationException("Connecteur voisin logique.");

            XYZ v = pN - pA;
            XYZ dir = v.GetLength() < 1e-6 ? SafeDirection(accConn) : v.Normalize();
            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            // Déconnecter avant insertion. Un échec silencieux ici produisait ensuite
            // des brides superposées ou orphelines.
            DisconnectOriginalConnection(accConn, neighbor);

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

            // Les paramètres de diamètre/sens peuvent reconstruire la famille et invalider
            // les objets Connector précédemment lus : toujours les récupérer à nouveau.
            doc.Regenerate();
            fConns = GetPipingEndConnectors(flange).ToList();
            pair = ChooseFlangePairByDesc(fConns, dir);
            fAcc = pair.cAcc;
            fPipe = pair.cPipe;

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
            neighbor = AlignNeighborToFlangeIfPossible(doc, neighbor, fPipe);
            bool connectedPipe = ConnectWithAutoFlip(fPipe, neighbor);
            if (!connectedPipe)
                throw new InvalidOperationException("Connexion bride→voisin impossible.");
            else if (neighborIsReducer)
            {
                // Force seulement un recalcul ; déplacer une bride déjà connectée peut
                // déformer le réseau ou échouer lorsque l'accessoire est protégé.
                doc.Regenerate();
            }

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

            return flange;
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
            // Ne jamais basculer un paramètre de type : cela retournerait toutes les brides
            // existantes de ce type dans le projet, pas uniquement la nouvelle instance.
            return false;
        }

        // ---------- Placement 2023 (logique conservée + sens/param) ----------
        private static FamilyInstance InsertFlangeBetween(Document doc, FamilySymbol flangeSymbol, Connector accConn, Connector neighbor, bool anchorToAccessory)
        {
            if (!flangeSymbol.IsActive) flangeSymbol.Activate();

            if (!TryGetOrigin(accConn, out var pA)) throw new InvalidOperationException("Connecteur accessoire logique.");
            if (!TryGetOrigin(neighbor, out var pN)) throw new InvalidOperationException("Connecteur voisin logique.");

            XYZ dirA = (pN - pA);
            if (dirA.GetLength() < EPS) dirA = SafeDirection(accConn);
            dirA = dirA.Normalize();

            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            DisconnectOriginalConnection(accConn, neighbor);

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

            doc.Regenerate();
            fConns = GetPipingEndConnectors(flange).ToList();
            var finalPair = ChooseFlangePairByDesc(fConns, dirA);
            fToAcc = finalPair.cAcc;
            fToPipe = finalPair.cPipe;

            if (!ConnectWithAutoFlip(fToAcc, accConn))
                throw new InvalidOperationException("Connexion bride→accessoire impossible.");
            neighbor = AlignNeighborToFlangeIfPossible(doc, neighbor, fToPipe);
            if (!ConnectWithAutoFlip(fToPipe, neighbor))
                throw new InvalidOperationException("Connexion bride→voisin impossible.");

            ReAnchorToAccessory(flange, accConn, ref fToAcc, -dirA, 0.0);
            doc.Regenerate();
            return flange;
        }

        private static FamilyInstance PlaceFlangeOnOneSide(Document doc, FamilySymbol flangeSymbol, Connector accConn, bool anchorToAccessory)
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

            doc.Regenerate();
            fConns = GetPipingEndConnectors(flange).ToList();
            fToAcc = FindConnByDescTokens(fConns, TOK_ACC) ?? ChooseAccessoryFinal(fConns, dirA, pA);

            if (!ConnectWithAutoFlip(fToAcc, accConn))
                throw new InvalidOperationException("Connexion bride (one-side) impossible.");

            ReAnchorToAccessory(flange, accConn, ref fToAcc, -dirA, 0.0);
            doc.Regenerate();
            return flange;
        }

        // ---------- Proximité : Accessoires / Coudes ----------
        private static bool HasPipeAccessoryVeryCloseAccurate(
            Document doc, XYZ origin, XYZ dir, double maxDistFt, double radialFt,
            ISet<ElementId> exclude = null, ISet<ElementId> allowedIds = null)
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
                .Where(fi => (exclude == null || !exclude.Contains(fi.Id)) &&
                             (allowedIds == null || allowedIds.Contains(fi.Id)))
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
            Document doc, XYZ origin, XYZ dir, double maxDistFt, double radialFt,
            ISet<ElementId> exclude = null, ISet<ElementId> allowedIds = null)
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
                .Where(fi => (exclude == null || !exclude.Contains(fi.Id)) &&
                             (allowedIds == null || allowedIds.Contains(fi.Id)) &&
                             IsLikelyElbow(fi))
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

            try
            {
                if (a.IsConnectedTo(b)) return true;
                a.ConnectTo(b);
                return a.IsConnectedTo(b);
            }
            catch { return false; }
        }

        private static void DisconnectOriginalConnection(Connector accessory, Connector neighbor)
        {
            if (accessory == null || neighbor == null) return;
            try
            {
                if (!accessory.IsConnectedTo(neighbor)) return;
                accessory.DisconnectFrom(neighbor);
                if (accessory.IsConnectedTo(neighbor))
                    throw new InvalidOperationException("La connexion d'origine n'a pas pu être libérée.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Impossible de déconnecter proprement la liaison d'origine.", ex);
            }
        }

        private static Connector AlignNeighborToFlangeIfPossible(Document doc, Connector neighbor, Connector flangeConnector)
        {
            if (neighbor?.Owner is Pipe)
                return AlignPipeEndToFlangeIfPossible(doc, neighbor, flangeConnector);

            if (IsLikelyReducer(neighbor?.Owner))
                return MoveReducerToFlange(doc, neighbor, flangeConnector);

            return neighbor;
        }

        private static Connector MoveReducerToFlange(Document doc, Connector reducerConnector, Connector flangeConnector)
        {
            var reducer = reducerConnector?.Owner as FamilyInstance;
            if (reducer == null || flangeConnector == null) return reducerConnector;
            if (!TryGetOrigin(reducerConnector, out XYZ oldEnd) || !TryGetOrigin(flangeConnector, out XYZ targetEnd))
                return reducerConnector;

            XYZ delta = targetEnd - oldEnd;
            if (delta.GetLength() <= COLOC_TOL) return reducerConnector;

            // Après la déconnexion de la pompe, ces liaisons représentent le réseau aval
            // de la réduction. Elles devront toujours exister après son déplacement.
            var downstreamOwnerIds = GetConnectedOwnerIds(reducer)
                .Where(id => id != reducer.Id)
                .ToList();

            bool wasPinned = false;
            bool pinStateRead = false;
            try
            {
                wasPinned = reducer.Pinned;
                pinStateRead = true;
                if (wasPinned) reducer.Pinned = false;

                ElementTransformUtils.MoveElement(doc, reducer.Id, delta);
                doc.Regenerate();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "La réduction ne peut pas être décalée pour insérer la bride (contrainte, verrouillage ou manque de place).",
                    ex);
            }
            finally
            {
                if (pinStateRead && wasPinned)
                {
                    try { reducer.Pinned = true; } catch { }
                }
            }

            var refreshedConnectors = GetPipingEndConnectors(reducer).ToList();
            Connector refreshed = refreshedConnectors
                .Where(c => TryGetOrigin(c, out _))
                .OrderBy(c => SafeOriginOr(c, targetEnd).DistanceTo(targetEnd))
                .FirstOrDefault();

            if (refreshed == null || SafeOriginOr(refreshed, targetEnd).DistanceTo(targetEnd) > 1.0 * MM)
                throw new InvalidOperationException("La réduction n'a pas atteint le connecteur de la bride.");

            foreach (ElementId downstreamId in downstreamOwnerIds)
            {
                if (!HasPhysicalConnectionToOwner(refreshedConnectors, downstreamId))
                    throw new InvalidOperationException(
                        $"Le déplacement de la réduction couperait sa liaison aval avec l'élément {downstreamId.GetIdValue()}.");
            }

            return refreshed;
        }

        private static IEnumerable<ElementId> GetConnectedOwnerIds(FamilyInstance element)
        {
            var result = new HashSet<ElementId>();
            foreach (Connector connector in GetPipingEndConnectors(element))
            {
                try
                {
                    foreach (Connector reference in connector.AllRefs)
                    {
                        if (reference?.Owner == null || reference.Domain != Domain.DomainPiping) continue;
                        if (!connector.IsConnectedTo(reference)) continue;
                        result.Add(reference.Owner.Id);
                    }
                }
                catch { }
            }
            return result;
        }

        private static Connector AlignPipeEndToFlangeIfPossible(Document doc, Connector neighbor, Connector flangeConnector)
        {
            var pipe = neighbor?.Owner as Pipe;
            var locationCurve = pipe?.Location as LocationCurve;
            var line = locationCurve?.Curve as Line;
            if (pipe == null || line == null || flangeConnector == null) return neighbor;
            if (!TryGetOrigin(neighbor, out XYZ oldEnd) || !TryGetOrigin(flangeConnector, out XYZ targetEnd)) return neighbor;
            if (oldEnd.DistanceTo(targetEnd) <= COLOC_TOL) return neighbor;

            XYZ p0 = line.GetEndPoint(0);
            XYZ p1 = line.GetEndPoint(1);
            bool moveStart = p0.DistanceTo(oldEnd) <= p1.DistanceTo(oldEnd);
            XYZ fixedEnd = moveStart ? p1 : p0;

            // Ne modifier que les canalisations droites et lorsque la cible reste sur leur axe.
            XYZ axis = (p1 - p0).Normalize();
            XYZ fromAxis = targetEnd - p0;
            double radialDistance = (fromAxis - axis * fromAxis.DotProduct(axis)).GetLength();
            if (radialDistance > 1.0 * MM) return neighbor;

            double oldLength = fixedEnd.DistanceTo(oldEnd);
            XYZ towardFixedEnd = (fixedEnd - oldEnd).Normalize();
            double consumedLength = (targetEnd - oldEnd).DotProduct(towardFixedEnd);
            if (consumedLength < -1.0 * MM)
                throw new InvalidOperationException(
                    "Orientation incompatible : la bride allongerait la canalisation du mauvais côté.");
            if (consumedLength > oldLength - SAFETY_MARGIN)
                throw new InvalidOperationException(
                    $"Manque de place : la bride consommerait {consumedLength / MM:0} mm sur une canalisation de {oldLength / MM:0} mm.");

            double newLength = fixedEnd.DistanceTo(targetEnd);
            if (newLength < SAFETY_MARGIN)
                throw new InvalidOperationException(
                    $"Manque de place : la canalisation ne conserverait que {newLength / MM:0} mm après insertion.");

            locationCurve.Curve = moveStart
                ? Line.CreateBound(targetEnd, fixedEnd)
                : Line.CreateBound(fixedEnd, targetEnd);
            doc.Regenerate();

            Connector refreshed = null;
            try
            {
                refreshed = pipe.ConnectorManager.Connectors
                    .Cast<Connector>()
                    .Where(c => c.Domain == Domain.DomainPiping && TryGetOrigin(c, out _))
                    .OrderBy(c => SafeOriginOr(c, targetEnd).DistanceTo(targetEnd))
                    .FirstOrDefault();
            }
            catch { }

            if (refreshed == null || SafeOriginOr(refreshed, targetEnd).DistanceTo(targetEnd) > 1.0 * MM)
                throw new InvalidOperationException("L'extrémité de canalisation n'a pas pu être recalée sur la bride.");
            return refreshed;
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

        private static Connector FindMatchingConnector(FamilyInstance owner, ConnectorSnapshot snapshot)
        {
            if (owner == null || snapshot == null) return null;
            try
            {
                return GetPipingEndConnectors(owner)
                    .Where(c => TryGetOrigin(c, out _))
                    .OrderBy(c =>
                    {
                        double distance = SafeOriginOr(c, snapshot.Origin).DistanceTo(snapshot.Origin);
                        double alignment = GetBasisZ(c).DotProduct(snapshot.Direction);
                        return distance + (1.0 - alignment) * MM;
                    })
                    .FirstOrDefault();
            }
            catch { return null; }
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

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.UI.Selection;
using Licensing; // BaseTrackedCommand

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
    [Regeneration(RegenerationOption.Manual)]
    public class AddFlangesAtEnds : BaseTrackedCommand
    {
        protected override string ButtonId => "AddFlangesAtEnds";

        // ------- Constantes réglables (ft) -------
        const double MM = 1.0 / 304.8;
        const double SAFETY_MARGIN = 5 * MM;    // marge mini au-delà de l’épaisseur
        const double START_NUDGE = 2 * MM;    // évite de “voir” l’élément courant
        const double RADIAL_SCAN = 150 * MM;  // rayon recherche proximité

        // --- descriptions tolérantes (fr/en/abréviations) ---
        private static readonly string[] TOK_ACC = { "accessoire", "accessory", "acc" };
        private static readonly string[] TOK_PIPE = { "canalisation", "cana", "pipe", "piping" };

        // ------- PN / types brides (cache par famille + PN) -------
        // clé : $"{familyId.IntegerValue}|{pnCanonical}"
        private static readonly Dictionary<string, ElementId> _pnTypeCache = new Dictionary<string, ElementId>();

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = data.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            UIApplication uiApp = data.Application;

            // Auto-Yes aux TaskDialog
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
                            "Sélectionne des accessoires (vannes, filtres...) ou équipements CVC pour poser des brides.");
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
                    TaskDialog.Show("Brides", "Aucun élément MEP (piping) valide dans la sélection.");
                    return Result.Cancelled;
                }

                // --- Type de bride (base) ---
                var baseFlangeSymbol = FindFlangeSymbol(doc);
                if (baseFlangeSymbol == null)
                {
                    TaskDialog.Show("Brides manquantes",
                        "Aucun type 'bride' admissible. Charge par ex. 'CML Bride à collerette tous PN2'.");
                    return Result.Cancelled;
                }

                int placed = 0, skipped = 0;

                using (var t = new Transaction(doc, "Ajouter brides"))
                {
                    t.Start();
                    SuppressWarnings(t);

                    foreach (var acc in targets)
                    {
                        bool selectedIsMech = IsCat(acc, BuiltInCategory.OST_MechanicalEquipment);

                        using (new PinScope(acc)) // l’élément cliqué ne bouge pas
                        {
                            var accConns = GetPipingEndConnectors(acc).ToList();
                            if (accConns.Count == 0) { skipped++; continue; }

                            // --------- Récupère un type "baseFlangeSymbol" adapté PN à partir de l’élément ---------
                            var symbolForThisElement = ResolveFlangeTypeForElementPN(doc, baseFlangeSymbol, acc);
                            if (symbolForThisElement == null) symbolForThisElement = baseFlangeSymbol;

                            foreach (var accConn in accConns)
                            {
                                if (AlreadyHasFlangeAtConnector(accConn))
                                    continue;

                                // voisin générique (utile pour accessoires)
                                var neighbor = GetFirstPhysicalPipingOther(accConn, acc.Id);

                                if (selectedIsMech)
                                {
                                    // ÉQUIPEMENT CVC : on pose seulement si connecté directement à un Pipe OU un Fitting
                                    if (!TryGetDirectPipeOrFittingNeighbor(accConn, acc.Id, out Connector pipeOrFittingNeighbor))
                                        continue;
                                    neighbor = pipeOrFittingNeighbor;
                                }
                                else
                                {
                                    // ACCESSOIRE :
                                    // - si voisin direct est un EQUIPEMENT -> on n’insère pas
                                    if (neighbor != null && IsCat(neighbor.Owner, BuiltInCategory.OST_MechanicalEquipment))
                                        continue;

                                    // - NE JAMAIS poser entre deux accessoires (évite la bride au milieu d’une chaîne)
                                    if (neighbor != null && IsCat(neighbor.Owner, BuiltInCategory.OST_PipeAccessory))
                                        continue;
                                }

                                // ---------- Détection d'obstacle / proximité ----------
                                XYZ pA = SafeOriginOr(accConn, XYZ.Zero);
                                XYZ dirA;
                                if (!TryGetVector(accConn, neighbor, out dirA) || dirA.IsAlmostEqualTo(XYZ.Zero))
                                    dirA = SafeDirection(accConn);
                                else dirA = dirA.Normalize();

                                double thicknessFt = EstimateFlangeThicknessFt(symbolForThisElement);
                                double neededClearance = thicknessFt + SAFETY_MARGIN;

                                var excludeIds = new HashSet<ElementId> { acc.Id };
                                if (neighbor?.Owner != null) excludeIds.Add(neighbor.Owner.Id);

                                // On bloque toujours fittings/accessoires/équipements (les pipes seuls peuvent se recalculer)
                                var blockingCats = new[]
                                {
                                    BuiltInCategory.OST_PipeFitting,
                                    BuiltInCategory.OST_PipeAccessory,
                                    BuiltInCategory.OST_MechanicalEquipment
                                };

                                if (IsBlockedAheadRobust(doc, pA, dirA, neededClearance, excludeIds, blockingCats, out _))
                                {
                                    skipped++;
                                    continue;
                                }

                                // Proximité d’accessoire de cana non connecté mais dans l’axe
                                if (HasPipeAccessoryVeryCloseAccurate(doc, pA, dirA,
                                    neededClearance + 100 * MM, RADIAL_SCAN, excludeIds))
                                {
                                    skipped++;
                                    continue;
                                }

                                // ---------- Pose ----------
                                var sym = symbolForThisElement;
                                if (!sym.IsActive) sym.Activate();

                                try
                                {
                                    if (neighbor != null)
                                        InsertFlangeBetween(doc, sym, accConn, neighbor, anchorToAccessory: selectedIsMech);
                                    else
                                        PlaceFlangeOnOneSide(doc, sym, accConn, anchorToAccessory: selectedIsMech);

                                    placed++;
                                }
                                catch
                                {
                                    // Dernier recours : tentative posée non connectée
                                    try
                                    {
                                        Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);
                                        var fi = doc.Create.NewFamilyInstance(pA, sym, lvl, StructuralType.NonStructural);
                                        doc.Regenerate();
                                        AlignConnectorDirection(fi,
                                            GetPipingEndConnectors(fi).FirstOrDefault() ?? throw new Exception(),
                                            -dirA);
                                        doc.Regenerate();
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

                TaskDialog.Show("Brides",
                    $"Brides posées : {placed}\nIgnorées/échouées : {skipped}");
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

        private static bool IsCat(Element e, BuiltInCategory bic)
            => e?.Category != null && e.Category.Id.IntegerValue == (int)bic;

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

        private static Connector GetFirstPhysicalPipingOther(Connector c, ElementId selfId)
        {
            try
            {
                foreach (Connector rc in c.AllRefs)
                {
                    if (rc.Owner?.Id == selfId) continue;
                    if (rc.Domain != Domain.DomainPiping) continue;
                    if (!TryGetOrigin(rc, out _)) continue; // Physical only
                    return rc;
                }
            }
            catch { }
            return null;
        }

        // True si le connecteur a une ref physique vers Pipe ou Fitting
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

                    int cat = rc.Owner?.Category?.Id.IntegerValue ?? 0;
                    bool isPipeOrFitting =
                        cat == (int)BuiltInCategory.OST_PipeCurves ||
                        cat == (int)BuiltInCategory.OST_PipeFitting;

                    if (isPipeOrFitting && TryGetOrigin(rc, out _))
                    {
                        neighborConn = rc;
                        return true;
                    }
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

                    string nm = (owner.Name ?? "").ToLower();
                    string typ = (owner.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "").ToLower();
                    if ((isPipeAcc || isFitting) && (nm.Contains("bride") || nm.Contains("flange") || typ.Contains("bride") || typ.Contains("flange")))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // ---------- Trouver le type de bride de base ----------
        private static FamilySymbol FindFlangeSymbol(Document doc)
        {
            // 1) Choix session
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

            // 2) Fallback “bride”
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(fs => fs?.Category != null &&
                            (fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory ||
                             fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting))
                .ToList();

            symbols = symbols.Where(fs =>
            {
                string both = ((fs.FamilyName ?? "") + " " + (fs.Name ?? "")).ToLower();
                return !both.Contains("cml_bride pleine tous pn"); // blacklist
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

        // ==================== PN : helpers robustes ====================

        // ---- [1] utilitaires génériques de recherche de paramètre ----
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
            // instance d'abord
            var p = FindParamByName(e, "HYD_PN");
            if (p != null) return p;

            // puis le symbole (paramètre de TYPE)
            var fi = e as FamilyInstance;
            if (fi?.Symbol != null)
                p = FindParamByName(fi.Symbol, "HYD_PN");

            return p;
        }
        private static Parameter GetHydPnParam(FamilySymbol fs)
            => FindParamByName(fs, "HYD_PN");

        // ---- [2] normalisation + lecture PN depuis UN paramètre ----
        private static string NormalizePnString(string s)
        {
            s = (s ?? "").Trim().ToUpperInvariant();
            s = s.Replace(" ", "");
            return s;
        }
        private static bool TryReadPnFromParam(Parameter p, out string pnCanonical, out string pnForName)
        {
            pnCanonical = null; pnForName = null;
            if (p == null) return false;

            switch (p.StorageType)
            {
                case StorageType.Integer:
                    int vi = p.AsInteger();
                    pnCanonical = "I:" + vi;
                    pnForName = vi.ToString();
                    return true;

                case StorageType.Double:
                    int vd = (int)Math.Round(p.AsDouble());
                    pnCanonical = "I:" + vd;
                    pnForName = vd.ToString();
                    return true;

                case StorageType.String:
                    string s = p.AsString() ?? "";
                    string digits = new string(s.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out int vs))
                    {
                        pnCanonical = "I:" + vs;
                        pnForName = vs.ToString();
                        return true;
                    }
                    pnCanonical = "S:" + NormalizePnString(s);
                    pnForName = s;
                    return true;

                default:
                    return false;
            }
        }
        private static string CanonicalFromParam(Parameter p)
        {
            return TryReadPnFromParam(p, out var c, out _) ? c : null;
        }

        // ---- [3] copie de valeur PN d’un paramètre source vers un paramètre de TYPE cible (bride) ----
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
                        if (int.TryParse(digits, out int vi))
                            targetTypeParam.Set(vi);
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
                        if (double.TryParse(digits, out double vd))
                            targetTypeParam.Set(vd);
                    }
                }
                else if (targetTypeParam.StorageType == StorageType.String)
                {
                    targetTypeParam.Set(srcParam.AsString() ?? "");
                }
            }
            catch { /* param type verrouillé -> on ignore proprement */ }
        }

        // ---- [4] égalité PN (param type bride vs param accessoire/équipement) ----
        private static bool PnEquals(Parameter typePn, Parameter srcPn)
        {
            string a = CanonicalFromParam(typePn);
            string b = CanonicalFromParam(srcPn);
            return !string.IsNullOrEmpty(a) && a == b;
        }

        // ---- [5] nom de type normalisé ----
        private static string BuildPnTypeName(string pnForName)
        {
            string s = (pnForName ?? "").Trim();
            if (string.IsNullOrEmpty(s)) s = "PN?";
            s = s.ToUpperInvariant();
            if (!s.StartsWith("PN")) s = "PN" + s;
            return $"Bride {s}";
        }

        // ---- [6] résolution (ou création) du TYPE de bride en fonction du PN de l’élément sélectionné ----
        private static FamilySymbol ResolveFlangeTypeForElementPN(Document doc, FamilySymbol baseSymbol, Element elementWithPN)
        {
            if (baseSymbol == null || elementWithPN == null) return baseSymbol;

            // PN sur l’élément (instance OU type)
            var srcPn = GetHydPnParam(elementWithPN);
            if (srcPn == null) return baseSymbol;

            if (!TryReadPnFromParam(srcPn, out var pnCanonical, out var pnForName))
                return baseSymbol;

            // La famille de bride expose-t-elle HYD_PN (paramètre de TYPE) ?
            var baseTypePn = GetHydPnParam(baseSymbol);
            if (baseTypePn == null) return baseSymbol;

            // Cache par famille+PN
            string cacheKey = $"{baseSymbol.Family.Id.IntegerValue}|{pnCanonical}";
            if (_pnTypeCache.TryGetValue(cacheKey, out var cachedId))
            {
                var cached = doc.GetElement(cachedId) as FamilySymbol;
                if (cached != null) return cached;
            }

            // Chercher un type existant de la même famille avec PN identique
            var family = baseSymbol.Family;
            foreach (ElementId sid in family.GetFamilySymbolIds())
            {
                var fs = doc.GetElement(sid) as FamilySymbol;
                if (fs == null) continue;
                var fsPn = GetHydPnParam(fs);
                if (fsPn == null) continue;
                if (PnEquals(fsPn, srcPn))
                {
                    _pnTypeCache[cacheKey] = fs.Id;
                    return fs;
                }
            }

            // Créer le type
            // Chercher par NOM avant de créer
            string wantedName = BuildPnTypeName(pnForName); // "Bride PN40", etc.

            // s'il existe déjà un type avec ce nom, on l'utilise
            var byName = family.GetFamilySymbolIds()
                               .Select(id => doc.GetElement(id) as FamilySymbol)
                               .FirstOrDefault(fs => fs != null &&
                                    string.Equals(fs.Name, wantedName, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                // Aligne HYD_PN si le paramètre est présent et diffère (optionnel mais sûr)
                var byNamePn = GetHydPnParam(byName);
                if (byNamePn != null && !PnEquals(byNamePn, srcPn))
                    CopyPnValue(byNamePn, srcPn);

                _pnTypeCache[cacheKey] = byName.Id;
                return byName;
            }

            // sinon on le crée
            FamilySymbol newSym = null;
            try
            {
                newSym = (baseSymbol.Duplicate(wantedName) as ElementType) as FamilySymbol;
            }
            catch
            {
                // Le nom existe peut-être (créé par un autre utilisateur) → on le récupère et on l'utilise
                newSym = family.GetFamilySymbolIds()
                               .Select(id => doc.GetElement(id) as FamilySymbol)
                               .FirstOrDefault(fs => fs != null &&
                                   string.Equals(fs.Name, wantedName, StringComparison.OrdinalIgnoreCase));
                if (newSym == null) return baseSymbol;
            }

            // Renseigner HYD_PN sur le type créé/récupéré
            var newPn = GetHydPnParam(newSym);
            if (newPn != null) CopyPnValue(newPn, srcPn);

            _pnTypeCache[cacheKey] = newSym.Id;
            return newSym;
        }

        // ==================== Placement / insertion ====================
        private static void InsertFlangeBetween(
            Document doc, FamilySymbol flangeSymbol, Connector accConn, Connector neighbor, bool anchorToAccessory)
        {
            if (!flangeSymbol.IsActive) flangeSymbol.Activate();

            XYZ pA = SafeOriginOr(accConn, XYZ.Zero);
            XYZ dirA;
            if (!TryGetVector(accConn, neighbor, out dirA) || dirA.IsAlmostEqualTo(XYZ.Zero))
                dirA = SafeDirection(accConn);
            else dirA = dirA.Normalize();

            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            accConn.DisconnectFrom(neighbor);

            double placeOffset = anchorToAccessory ? 0.0 : 0.05; // ~15 mm sinon
            var flange = doc.Create.NewFamilyInstance(pA + dirA * placeOffset, flangeSymbol, lvl, StructuralType.NonStructural);
            doc.Regenerate();

            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count < 2) throw new InvalidOperationException("Bride sans 2 connecteurs Piping.");

            var fToAcc = FindConnByDescTokens(fConns, TOK_ACC);
            var fToPipe = FindConnByDescTokens(fConns, TOK_PIPE);
            if (fToAcc == null) fToAcc = ChooseByDirection(fConns, dirA, wantOpposite: true);
            if (fToPipe == null) fToPipe = fConns.First(c => c.Id != fToAcc.Id);

            AlignConnectorDirection(flange, fToAcc, -dirA);
            doc.Regenerate();

            fConns = GetPipingEndConnectors(flange).ToList();
            fToAcc = fConns.First(c => c.Id == fToAcc.Id);
            fToPipe = fConns.First(c => c.Id == fToPipe.Id);

            double edge = anchorToAccessory ? 0.0 : ComputeEdgeOffsetFt(flange);
            MoveBy(flange, (pA - SafeOriginOr(fToAcc, pA)) - dirA * edge);
            doc.Regenerate();

            bool ok1 = ConnectWithAutoFlip(fToAcc, accConn, fToPipe, accConn);
            bool ok2 = ConnectWithAutoFlip(fToPipe, neighbor, fToAcc, neighbor);
            if (!(ok1 && ok2)) throw new InvalidOperationException("Connexion bride impossible.");

            TrySetNominalDiameter(flange, accConn);

            double tinyGap = 0.0;
            ReAnchorToAccessory(flange, accConn, ref fToAcc, dirA, tinyGap);
            doc.Regenerate();
        }

        private static void PlaceFlangeOnOneSide(
            Document doc, FamilySymbol flangeSymbol, Connector accConn, bool anchorToAccessory)
        {
            if (!flangeSymbol.IsActive) flangeSymbol.Activate();

            XYZ pA = SafeOriginOr(accConn, XYZ.Zero);
            XYZ dirA = SafeDirection(accConn);
            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            double placeOffset = anchorToAccessory ? 0.0 : 0.02; // ~6 mm sinon
            var flange = doc.Create.NewFamilyInstance(pA + dirA * placeOffset, flangeSymbol, lvl, StructuralType.NonStructural);
            doc.Regenerate();

            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count == 0) throw new InvalidOperationException("Bride sans connecteurs Piping.");

            var fToAcc = FindConnByDescTokens(fConns, TOK_ACC);
            if (fToAcc == null) fToAcc = ChooseByDirection(fConns, dirA, wantOpposite: true);

            AlignConnectorDirection(flange, fToAcc, -dirA);
            doc.Regenerate();

            double edge = anchorToAccessory ? 0.0 : ComputeEdgeOffsetFt(flange);
            MoveBy(flange, (pA - SafeOriginOr(fToAcc, pA)) - dirA * edge);
            doc.Regenerate();

            var other = fConns.FirstOrDefault(c => c.Id != fToAcc.Id);
            bool ok = ConnectWithAutoFlip(fToAcc, accConn, other, accConn);
            if (!ok) throw new InvalidOperationException("Connexion bride impossible (one-side).");

            TrySetNominalDiameter(flange, accConn);

            double tinyGap = 0.0;
            ReAnchorToAccessory(flange, accConn, ref fToAcc, dirA, tinyGap);
            doc.Regenerate();
        }

        // ---------- Détection d'obstacles / proximité ----------
        private static bool IsBlockedAheadRobust(
            Document doc, XYZ origin, XYZ dir, double needDistFt,
            ISet<ElementId> exclude, IEnumerable<BuiltInCategory> blockingCats, out double hitDist)
        {
            hitDist = double.PositiveInfinity;
            var v3 = GetCached3DView(doc);

            // 1) Raycast précis si vue 3D dispo
            if (v3 != null)
            {
                IList<ElementFilter> catFilters =
                    blockingCats.Select(c => (ElementFilter)new ElementCategoryFilter(c)).ToList();

                var filter = new LogicalOrFilter(catFilters);

                var ri = new ReferenceIntersector(filter, FindReferenceTarget.Face, v3)
                { FindReferencesInRevitLinks = false };

                var start = origin + dir * START_NUDGE;

                IList<ReferenceWithContext> hits = null;
                try { hits = ri.Find(start, dir); } catch { hits = null; }

                if (hits != null && hits.Count > 0)
                {
                    foreach (var h in hits.OrderBy(h => h.Proximity))
                    {
                        var r = h.GetReference(); if (r == null) continue;
                        if (exclude != null && exclude.Contains(r.ElementId)) continue;

                        hitDist = h.Proximity;
                        return hitDist < needDistFt;
                    }
                }
                // sinon fallback AABB
            }

            // 2) Fallback AABB
            XYZ target = origin + dir * needDistFt;
            double pad = 20 * MM;
            XYZ min = new XYZ(Math.Min(origin.X, target.X), Math.Min(origin.Y, target.Y), Math.Min(origin.Z, target.Z)) - new XYZ(pad, pad, pad);
            XYZ max = new XYZ(Math.Max(origin.X, target.X), Math.Max(origin.Y, target.Y), Math.Max(origin.Z, target.Z)) + new XYZ(pad, pad, pad);

            var o = new Outline(min, max);
            var bbFilter = new BoundingBoxIntersectsFilter(o);

            IList<ElementFilter> bbCats =
                blockingCats.Select(c => (ElementFilter)new ElementCategoryFilter(c)).ToList();

            var blocked = new FilteredElementCollector(doc)
                .WherePasses(new LogicalOrFilter(bbCats))
                .WherePasses(bbFilter)
                .Where(e => exclude == null || !exclude.Contains(e.Id))
                .Any();

            return blocked;
        }

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

        // ---- Connecteurs "physiques" sûrs ----
        private static bool TryGetOrigin(Connector c, out XYZ o)
        {
            try { o = c.Origin; return true; } // throw si Logical
            catch { o = XYZ.Zero; return false; }
        }
        private static XYZ SafeOriginOr(Connector c, XYZ fallback)
            => TryGetOrigin(c, out var o) ? o : fallback;

        private static bool TryGetVector(Connector a, Connector b, out XYZ v)
        {
            v = XYZ.Zero;
            if (a == null || b == null) return false;
            if (!TryGetOrigin(a, out var oa)) return false;
            if (!TryGetOrigin(b, out var ob)) return false;
            v = ob - oa; return true;
        }

        private static Connector ChooseByDirection(IEnumerable<Connector> conns, XYZ toward, bool wantOpposite)
        {
            return wantOpposite
                ? conns.OrderBy(c => GetBasisZ(c).DotProduct(toward)).First()
                : conns.OrderByDescending(c => GetBasisZ(c).DotProduct(toward)).First();
        }

        private static double ComputeEdgeOffsetFt(FamilyInstance flange)
        {
            string[] names = { "Epaisseur", "Thickness", "Thk", "Gasket", "Bride_Epaisseur" };
            foreach (Parameter p in flange.Symbol.Parameters)
                if (p.StorageType == StorageType.Double &&
                    names.Any(n => (p.Definition?.Name ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                    return Math.Max(0.0, p.AsDouble() * 0.5);
            return 15 * MM; // défaut : demi-épaisseur ~15 mm
        }

        private static double EstimateFlangeThicknessFt(FamilySymbol sym)
        {
            string[] names = { "Epaisseur", "Thickness", "Thk", "Bride_Epaisseur" };
            foreach (Parameter p in sym.Parameters)
                if (p.StorageType == StorageType.Double &&
                    names.Any(n => (p.Definition?.Name ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                    return Math.Max(1 * MM, p.AsDouble());
            return 30 * MM; // défaut ~30 mm
        }

        private static bool ConnectWithAutoFlip(Connector a, Connector b, Connector altA = null, Connector altB = null)
        {
            try { a.ConnectTo(b); return true; }
            catch
            {
                if (altA != null && altB != null && altA.Id != a.Id && altB.Id != b.Id)
                {
                    try { altA.ConnectTo(altB); return true; } catch { }
                }
                return false;
            }
        }

        /// <summary>Après connexion, recale la bride pour que son connecteur "Accessoire"
        /// coïncide exactement avec le connecteur de l'accessoire (sans pousser l'équipement).</summary>
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
                MoveBy(flange, delta);

            if (extraGapFt > 0)
                MoveBy(flange, (-dirA) * extraGapFt);
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
                    if (TryGetVector(c, r, out var v) && !v.IsAlmostEqualTo(XYZ.Zero))
                        return v.Normalize();
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

        // ---------- Vue 3D cache pour raycast ----------
        static readonly Dictionary<int, ElementId> _view3dCache = new Dictionary<int, ElementId>();
        private static View3D GetCached3DView(Document doc)
        {
            int key = doc.Application.ActiveAddInId.GetHashCode() ^ doc.GetHashCode();
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
    }

    // ---------- Helpers / extensions ----------
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
                if (!_fi.Pinned)
                {
                    _fi.Pinned = true;
                    _changed = true;
                }
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
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        const double SAFETY_MARGIN = 5 * MM;      // marge mini au-delà de l’épaisseur
        const double START_NUDGE = 2 * MM;        // évite de “voir” l’élément courant
        const double RADIAL_SCAN = 150 * MM;      // rayon recherche proximité

        // --- descriptions tolérantes (fr/en/abréviations) ---
        private static readonly string[] TOK_ACC  = { "accessoire", "accessory", "acc" };
        private static readonly string[] TOK_PIPE = { "canalisation", "cana", "pipe", "piping" };

        // ------- PN / types brides (cache par famille + PN) -------
        private static readonly Dictionary<string, ElementId> _pnTypeCache = new Dictionary<string, ElementId>();

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = data.Application.ActiveUIDocument;
            Document doc     = uiDoc.Document;
            UIApplication uiApp = data.Application;

            // Auto-Yes aux TaskDialog
            EventHandler<DialogBoxShowingEventArgs> autoYes = (s, e) =>
            {
                if (e is TaskDialogShowingEventArgs td) td.OverrideResult((int)TaskDialogResult.Yes);
            };
            uiApp.DialogBoxShowing += autoYes;

            try
            {
                try // filet global : plus d’exception non interceptée
                {
                    // --- Sélection ---
                    var ids = uiDoc.Selection.GetElementIds().ToList();
                    if (ids.Count == 0)
                    {
                        try
                        {
                            var picked = uiDoc.Selection.PickObjects(
                                ObjectType.Element,
                                new PipingAccessoryFilter(),
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

                    // --- Type de bride (base) : ACCESSORY UNIQUEMENT ---
                    var baseFlangeSymbol = FindFlangeSymbol(doc);
                    if (baseFlangeSymbol == null)
                    {
                        TaskDialog.Show("Brides manquantes", "Aucun type 'bride' admissible (PipeAccessory). Charge par ex. 'CML Bride à collerette tous PN2'.");
                        return Result.Cancelled;
                    }

                    // Sanity : refuse PipeFitting (non point-placé en 2025)
                    if (baseFlangeSymbol.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting)
                    {
                        TaskDialog.Show("Brides", $"Le type sélectionné '{baseFlangeSymbol.FamilyName} :: {baseFlangeSymbol.Name}' est un Raccord (PipeFitting).\n" +
                                                  "En Revit 2025, ce type ne se place pas par point. Utilise une bride de catégorie 'Accessoire de canalisation'.");
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

                                // --------- Type adapté PN pour cet élément ---------
                                var symbolForThisElement = ResolveFlangeTypeForElementPN(doc, baseFlangeSymbol, acc) ?? baseFlangeSymbol;

                                // double check catégorie
                                if (symbolForThisElement.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting)
                                {
                                    // On skip au lieu de jeter — évite l'ArgumentException 2025
                                    skipped += accConns.Count;
                                    continue;
                                }

                                foreach (var accConn in accConns)
                                {
                                    try
                                    {
                                        if (AlreadyHasFlangeAtConnector(accConn))
                                            continue;

                                        // voisin générique (utile pour accessoires)
                                        var neighbor = GetFirstPhysicalPipingOther(accConn, acc.Id);

                                        if (selectedIsMech)
                                        {
                                            // ÉQUIPEMENT CVC : poser seulement si connecté direct à un Pipe OU un Fitting
                                            if (!TryGetDirectPipeOrFittingNeighbor(accConn, acc.Id, out Connector pipeOrFittingNeighbor))
                                                continue;
                                            neighbor = pipeOrFittingNeighbor;
                                        }
                                        else
                                        {
                                            // ACCESSOIRE :
                                            if (neighbor != null && IsCat(neighbor.Owner, BuiltInCategory.OST_MechanicalEquipment)) continue;
                                            if (neighbor != null && IsCat(neighbor.Owner, BuiltInCategory.OST_PipeAccessory)) continue;
                                        }

                                        // ---------- Détection d'obstacle / proximité ----------
                                        XYZ pA = SafeOriginOr(accConn, XYZ.Zero);
                                        if (IsInvalidPoint(pA)) { skipped++; continue; }

                                        XYZ dirA;
                                        if (!TryGetVector(accConn, neighbor, out dirA) || dirA.IsAlmostEqualTo(XYZ.Zero) || IsInvalidVector(dirA))
                                            dirA = SafeDirection(accConn);
                                        else
                                            dirA = dirA.Normalize();

                                        if (IsInvalidVector(dirA)) { skipped++; continue; }

                                        double thicknessFt = EstimateFlangeThicknessFt(symbolForThisElement);
                                        double neededClearance = thicknessFt + SAFETY_MARGIN;

                                        var excludeIds = new HashSet<ElementId> { acc.Id };
                                        if (neighbor?.Owner != null) excludeIds.Add(neighbor.Owner.Id);

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

                                        if (HasPipeAccessoryVeryCloseAccurate(doc, pA, dirA, neededClearance + 100 * MM, RADIAL_SCAN, excludeIds))
                                        {
                                            skipped++;
                                            continue;
                                        }

                                        // ---------- Pose ----------
                                        var sym = symbolForThisElement;
                                        if (!sym.IsActive) sym.Activate();

                                        bool done = false;

                                        // chemin principal (entre deux éléments)
                                        if (neighbor != null)
                                        {
                                            done = TryInsertFlangeBetween(doc, sym, accConn, neighbor, selectedIsMech);
                                        }
                                        else
                                        {
                                            // une seule extrémité
                                            done = TryPlaceFlangeOnOneSide(doc, sym, accConn, selectedIsMech);
                                        }

                                        if (done) placed++; else skipped++;
                                    }
                                    catch
                                    {
                                        // ne laisse rien sortir — robustesse
                                        skipped++;
                                    }
                                }
                            }
                        }

                        doc.Regenerate();
                        t.Commit();
                    }

                    TaskDialog.Show("Brides", $"Brides posées : {placed}\nIgnorées/échouées : {skipped}");
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Brides - Erreur",
                        "L’opération a été interrompue sans poser de bride.\n\n" +
                        "Détail : " + ex.Message);
                    return Result.Failed;
                }
            }
            finally
            {
                uiApp.DialogBoxShowing -= autoYes;
            }
        }

        // ---------- Sélection & tests ----------
        private static bool IsCat(Element e, BuiltInCategory bic) =>
            e?.Category != null && e.Category.Id.IntegerValue == (int)bic;

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
                    if (rc?.Owner?.Id == selfId) continue;
                    if (rc.Domain != Domain.DomainPiping) continue;
                    if (!TryGetOrigin(rc, out _)) continue; // Physical only
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

                    int cat = rc.Owner?.Category?.Id.IntegerValue ?? 0;
                    bool isPipeOrFitting = cat == (int)BuiltInCategory.OST_PipeCurves || cat == (int)BuiltInCategory.OST_PipeFitting;
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

                    if ((isPipeAcc || isFitting) &&
                        (nm.Contains("bride") || nm.Contains("flange") || typ.Contains("bride") || typ.Contains("flange")))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // ---------- Trouver le type de bride de base (ACCESSORY only) ----------
        private static FamilySymbol FindFlangeSymbol(Document doc)
        {
            // 1) Choix session
            if (FlangeChoiceCache.HasChoice)
            {
                var chosen = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(fs =>
                        fs?.Category != null &&
                        fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory &&
                        string.Equals(fs.FamilyName, FlangeChoiceCache.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fs.Name, FlangeChoiceCache.SymbolName, StringComparison.OrdinalIgnoreCase));
                if (chosen != null) return chosen;
            }

            // 2) Fallback “bride” : ACCESSOIRE UNIQUEMENT
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(fs => fs?.Category != null &&
                             fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory)
                .ToList();

            // blacklist
            symbols = symbols.Where(fs =>
            {
                string both = ((fs.FamilyName ?? "") + " " + (fs.Name ?? "")).ToLower();
                return !both.Contains("cml_bride pleine tous pn");
            }).ToList();

            if (symbols.Count == 0) return null;

            var pn2 = symbols.FirstOrDefault(fs =>
                string.Equals(fs.FamilyName, "CML Bride à collerette tous PN2", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fs.Name,       "CML Bride à collerette tous PN2", StringComparison.OrdinalIgnoreCase));
            if (pn2 != null) return pn2;

            var filtered = symbols.Where(fs =>
            {
                string hay = ((fs.FamilyName ?? "") + " " + (fs.Name ?? "")).ToLower();
                return hay.Contains("bride") || hay.Contains("flange");
            }).ToList();

            if (filtered.Count == 0) return null;

            return filtered
                .OrderBy(fs => fs.FamilyName).ThenBy(fs => fs.Name)
                .First();
        }

        // ==================== PN : helpers ====================
        private static Parameter FindParamByName(Element e, string name)
        {
            if (e == null || string.IsNullOrWhiteSpace(name)) return null;
            foreach (Parameter p in e.Parameters)
            {
                var n = p.Definition?.Name;
                if (!string.IsNullOrEmpty(n) && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
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
            s = (s ?? "").Trim().ToUpperInvariant();
            s = s.Replace(" ", "");
            return s;
        }

        private static bool TryReadPnFromParam(Parameter p, out string pnCanonical, out string pnForName)
        {
            pnCanonical = null;
            pnForName = null;
            if (p == null) return false;

            switch (p.StorageType)
            {
                case StorageType.Integer:
                    int vi = p.AsInteger();
                    pnCanonical = "I:" + vi;
                    pnForName   = vi.ToString();
                    return true;

                case StorageType.Double:
                    int vd = (int)Math.Round(p.AsDouble());
                    pnCanonical = "I:" + vd;
                    pnForName   = vd.ToString();
                    return true;

                case StorageType.String:
                    string s = p.AsString() ?? "";
                    string digits = new string(s.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out int vs))
                    {
                        pnCanonical = "I:" + vs;
                        pnForName   = vs.ToString();
                        return true;
                    }
                    pnCanonical = "S:" + NormalizePnString(s);
                    pnForName   = s;
                    return true;

                default:
                    return false;
            }
        }

        private static string CanonicalFromParam(Parameter p) =>
            TryReadPnFromParam(p, out var c, out _) ? c : null;

        private static void CopyPnValue(Parameter targetTypeParam, Parameter srcParam)
        {
            if (targetTypeParam == null || srcParam == null) return;
            try
            {
                if (targetTypeParam.StorageType == StorageType.Integer)
                {
                    if (srcParam.StorageType == StorageType.Integer) targetTypeParam.Set(srcParam.AsInteger());
                    else if (srcParam.StorageType == StorageType.Double) targetTypeParam.Set((int)Math.Round(srcParam.AsDouble()));
                    else
                    {
                        string s = srcParam.AsString() ?? "";
                        string digits = new string(s.Where(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out int vi)) targetTypeParam.Set(vi);
                    }
                }
                else if (targetTypeParam.StorageType == StorageType.Double)
                {
                    if (srcParam.StorageType == StorageType.Integer) targetTypeParam.Set((double)srcParam.AsInteger());
                    else if (srcParam.StorageType == StorageType.Double) targetTypeParam.Set(srcParam.AsDouble());
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
            catch { /* type verrouillé */ }
        }

        private static bool PnEquals(Parameter typePn, Parameter srcPn)
        {
            string a = CanonicalFromParam(typePn);
            string b = CanonicalFromParam(srcPn);
            return !string.IsNullOrEmpty(a) && a == b;
        }

        private static string BuildPnTypeName(string pnForName)
        {
            string s = (pnForName ?? "").Trim();
            if (string.IsNullOrEmpty(s)) s = "PN?";
            s = s.ToUpperInvariant();
            if (!s.StartsWith("PN")) s = "PN" + s;
            return $"Bride {s}";
        }

        private static FamilySymbol ResolveFlangeTypeForElementPN(Document doc, FamilySymbol baseSymbol, Element elementWithPN)
        {
            if (baseSymbol == null || elementWithPN == null) return baseSymbol;

            var srcPn = GetHydPnParam(elementWithPN);
            if (srcPn == null) return baseSymbol;
            if (!TryReadPnFromParam(srcPn, out var pnCanonical, out var pnForName)) return baseSymbol;

            var baseTypePn = GetHydPnParam(baseSymbol);
            if (baseTypePn == null) return baseSymbol;

            string cacheKey = $"{baseSymbol.Family.Id.IntegerValue}|{pnCanonical}";
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
                {
                    _pnTypeCache[cacheKey] = fs.Id;
                    return fs;
                }
            }

            string wantedName = BuildPnTypeName(pnForName);

            var byName = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(fs => fs != null && string.Equals(fs.Name, wantedName, StringComparison.OrdinalIgnoreCase));

            if (byName != null)
            {
                var byNamePn = GetHydPnParam(byName);
                if (byNamePn != null && !PnEquals(byNamePn, srcPn)) CopyPnValue(byNamePn, srcPn);
                _pnTypeCache[cacheKey] = byName.Id;
                return byName;
            }

            FamilySymbol newSym = null;
            try
            {
                newSym = (baseSymbol.Duplicate(wantedName) as ElementType) as FamilySymbol;
            }
            catch
            {
                newSym = family.GetFamilySymbolIds()
                               .Select(id => doc.GetElement(id) as FamilySymbol)
                               .FirstOrDefault(fs => fs != null && string.Equals(fs.Name, wantedName, StringComparison.OrdinalIgnoreCase));
                if (newSym == null) return baseSymbol;
            }

            var newPn = GetHydPnParam(newSym);
            if (newPn != null) CopyPnValue(newPn, srcPn);

            _pnTypeCache[cacheKey] = newSym.Id;
            return newSym;
        }

        // ==================== Placement / insertion (robustes) ====================
        private static bool TryInsertFlangeBetween(
            Document doc, FamilySymbol flangeSymbol, Connector accConn, Connector neighbor, bool anchorToAccessory)
        {
            try
            {
                var flange = CreateAndRoughAlign(doc, flangeSymbol, accConn, neighbor, anchorToAccessory, out var dirA, out var fToAcc, out var fToPipe);
                if (flange == null) return false;

                bool ok1 = ConnectWithAutoFlip(fToAcc, accConn, fToPipe, accConn);
                bool ok2 = neighbor != null && ConnectWithAutoFlip(fToPipe, neighbor, fToAcc, neighbor);
                if (!(ok1 && ok2)) return false;

                TrySetNominalDiameter(flange, accConn);
                ReAnchorToAccessory(flange, accConn, ref fToAcc, dirA, 0.0);
                doc.Regenerate();
                return true;
            }
            catch { return false; }
        }

        private static bool TryPlaceFlangeOnOneSide(
            Document doc, FamilySymbol flangeSymbol, Connector accConn, bool anchorToAccessory)
        {
            try
            {
                var flange = CreateAndRoughAlign(doc, flangeSymbol, accConn, null, anchorToAccessory, out var dirA, out var fToAcc, out var fToPipe);
                if (flange == null) return false;

                var other = fToPipe ?? GetPipingEndConnectors(flange).FirstOrDefault(c => c.Id != fToAcc.Id);
                bool ok = ConnectWithAutoFlip(fToAcc, accConn, other, accConn);
                if (!ok) return false;

                TrySetNominalDiameter(flange, accConn);
                ReAnchorToAccessory(flange, accConn, ref fToAcc, dirA, 0.0);
                doc.Regenerate();
                return true;
            }
            catch { return false; }
        }

        private static FamilyInstance CreateAndRoughAlign(
            Document doc, FamilySymbol flangeSymbol, Connector accConn, Connector neighbor,
            bool anchorToAccessory, out XYZ dirA, out Connector fToAcc, out Connector fToPipe)
        {
            fToAcc = null; fToPipe = null; dirA = XYZ.BasisZ;

            XYZ pA = SafeOriginOr(accConn, XYZ.Zero);
            if (IsInvalidPoint(pA)) return null;

            if (!TryGetVector(accConn, neighbor, out dirA) || dirA.IsAlmostEqualTo(XYZ.Zero) || IsInvalidVector(dirA))
                dirA = SafeDirection(accConn);
            else
                dirA = dirA.Normalize();

            if (IsInvalidVector(dirA)) return null;

            Level lvl = GetClosestLevel(doc, pA) ?? GuessAnyLevel(doc);

            // Déconnecte proprement uniquement si réellement connecté
            SafeDisconnect(accConn, neighbor);

            double placeOffset = anchorToAccessory ? 0.0 : (neighbor != null ? 0.05 : 0.02);
            var flange = CreateFamilyInstanceSafe(doc, pA + dirA * placeOffset, flangeSymbol, lvl);
            if (flange == null) return null;

            doc.Regenerate();

            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count == 0) return null;

            fToAcc  = FindConnByDescTokens(fConns, TOK_ACC) ?? ChooseByDirection(fConns, dirA, wantOpposite: true);
            fToPipe = FindConnByDescTokens(fConns, TOK_PIPE);
            if (fToPipe == null && fConns.Count >= 2)

            AlignConnectorDirection(flange, fToAcc, -dirA);
            doc.Regenerate();

            double edge = anchorToAccessory ? 0.0 : ComputeEdgeOffsetFt(flange);
            MoveBy(flange, (SafeOriginOr(accConn, pA) - SafeOriginOr(fToAcc, pA)) - dirA * edge);
            doc.Regenerate();

            return flange;
        }

        // ---------- Détection d'obstacles / proximité ----------
        private static bool IsBlockedAheadRobust(
            Document doc, XYZ origin, XYZ dir, double needDistFt,
            ISet<ElementId> exclude, IEnumerable<BuiltInCategory> blockingCats, out double hitDist)
        {
            hitDist = double.PositiveInfinity;

            var v3 = GetCached3DView(doc);

            // 1) Raycast précis si vue 3D dispo et non perspective
            if (v3 != null && !v3.IsPerspective)
            {
                try
                {
                    IList<ElementFilter> catFilters = blockingCats.Select(c => (ElementFilter)new ElementCategoryFilter(c)).ToList();
                    var filter = new LogicalOrFilter(catFilters);
                    ReferenceIntersector ri = null;

                    try
                    {
                        ri = new ReferenceIntersector(filter, FindReferenceTarget.Face, v3) { FindReferencesInRevitLinks = false };
                    }
                    catch { ri = null; }

                    if (ri != null)
                    {
                        var start = origin + dir * START_NUDGE;
                        IList<ReferenceWithContext> hits = null;
                        try { hits = ri.Find(start, dir); } catch { hits = null; }

                        if (hits != null && hits.Count > 0)
                        {
                            foreach (var h in hits.OrderBy(h => h.Proximity))
                            {
                                var r = h.GetReference();
                                if (r == null) continue;
                                if (exclude != null && exclude.Contains(r.ElementId)) continue;
                                hitDist = h.Proximity;
                                return hitDist < needDistFt;
                            }
                        }
                    }
                }
                catch { /* fallback AABB */ }
            }

            // 2) Fallback AABB
            XYZ target = origin + dir * needDistFt;
            double pad = 20 * MM;
            XYZ min = new XYZ(Math.Min(origin.X, target.X), Math.Min(origin.Y, target.Y), Math.Min(origin.Z, target.Z)) - new XYZ(pad, pad, pad);
            XYZ max = new XYZ(Math.Max(origin.X, target.X), Math.Max(origin.Y, target.Y), Math.Max(origin.Z, target.Z)) + new XYZ(pad, pad, pad);
            var o = new Outline(min, max);
            var bbFilter = new BoundingBoxIntersectsFilter(o);

            IList<ElementFilter> bbCats = blockingCats.Select(c => (ElementFilter)new ElementCategoryFilter(c)).ToList();

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
                    if (radial.GetLength() <= radialFt) return true;
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
            try { o = c.Origin; return !IsInvalidPoint(o); }
            catch { o = XYZ.Zero; return false; }
        }

        private static XYZ SafeOriginOr(Connector c, XYZ fallback) =>
            TryGetOrigin(c, out var o) ? o : fallback;

        private static bool TryGetVector(Connector a, Connector b, out XYZ v)
        {
            v = XYZ.Zero;
            if (a == null || b == null) return false;
            if (!TryGetOrigin(a, out var oa)) return false;
            if (!TryGetOrigin(b, out var ob)) return false;
            v = ob - oa;
            return !IsInvalidVector(v);
        }

        private static Connector ChooseByDirection(IEnumerable<Connector> conns, XYZ toward, bool wantOpposite)
        {
            var list = conns?.ToList() ?? new List<Connector>();
            if (list.Count == 0) return null;
            return wantOpposite
                ? list.OrderBy(c => GetBasisZ(c).DotProduct(toward)).First()
                : list.OrderByDescending(c => GetBasisZ(c).DotProduct(toward)).First();
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
            try
            {
                a?.ConnectTo(b);
                return true;
            }
            catch
            {
                if (altA != null && altB != null && altA.Id != a?.Id && altB.Id != b?.Id)
                {
                    try { altA.ConnectTo(altB); return true; } catch { }
                }
                return false;
            }
        }

        private static void ReAnchorToAccessory(FamilyInstance flange, Connector accessoryConnInProject, ref Connector flangeAccessoryConn, XYZ dirA, double extraGapFt = 0.0)
        {
            var fConns = GetPipingEndConnectors(flange).ToList();
            if (fConns.Count == 0) return;

            var accOri = SafeOriginOr(accessoryConnInProject, XYZ.Zero);
            flangeAccessoryConn = fConns
                .OrderBy(c => SafeOriginOr(c, XYZ.Zero).DistanceTo(accOri))
                .First();

            XYZ delta = accOri - SafeOriginOr(flangeAccessoryConn, accOri);
            if (!delta.IsAlmostEqualTo(XYZ.Zero)) MoveBy(flange, delta);
            if (extraGapFt > 0) MoveBy(flange, (-dirA) * extraGapFt);
        }

        // ---------- Utilitaires géométrie ----------
        private static XYZ SafeDirection(Connector c)
        {
            try
            {
                var cs = c.CoordinateSystem;
                if (cs != null)
                {
                    var z = cs.BasisZ; if (!z.IsAlmostEqualTo(XYZ.Zero) && !IsInvalidVector(z)) return z.Normalize();
                    var x = cs.BasisX; if (!x.IsAlmostEqualTo(XYZ.Zero) && !IsInvalidVector(x)) return x.Normalize();
                    var y = cs.BasisY; if (!y.IsAlmostEqualTo(XYZ.Zero) && !IsInvalidVector(y)) return y.Normalize();
                }
            }
            catch { }

            try
            {
                foreach (Connector r in c.AllRefs)
                {
                    if (TryGetVector(c, r, out var v) && !v.IsAlmostEqualTo(XYZ.Zero) && !IsInvalidVector(v))
                        return v.Normalize();
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
                return IsInvalidVector(z) ? XYZ.BasisZ : z.Normalize();
            }
            catch { return XYZ.BasisZ; }
        }

        private static void AlignConnectorDirection(FamilyInstance fi, Connector fiConn, XYZ targetDir)
        {
            if (fi == null || fiConn == null) return;

            XYZ from = GetBasisZ(fiConn);
            if (IsInvalidVector(from)) return;

            XYZ to = targetDir;
            if (IsInvalidVector(to)) return;
            to = to.Normalize();

            double dot = Math.Max(-1.0, Math.Min(1.0, from.DotProduct(to)));
            double angle = Math.Acos(dot);
            if (double.IsNaN(angle) || angle < 1e-6) return;

            XYZ axis = from.CrossProduct(to);
            if (IsInvalidVector(axis) || axis.IsAlmostEqualTo(XYZ.Zero))
                axis = Math.Abs(from.DotProduct(XYZ.BasisX)) < 0.9 ? from.CrossProduct(XYZ.BasisX) : from.CrossProduct(XYZ.BasisY);
            if (IsInvalidVector(axis) || axis.IsAlmostEqualTo(XYZ.Zero)) return;

            axis = axis.Normalize();
            XYZ p = GetElementPivot(fi);
            if (IsInvalidPoint(p)) p = SafeOriginOr(fiConn, XYZ.Zero);

            var line = Line.CreateUnbound(p, axis);
            try { ElementTransformUtils.RotateElement(fi.Document, fi.Id, line, angle); } catch { /* ignore */ }
        }

        private static XYZ GetElementPivot(FamilyInstance fi)
        {
            var lp = fi.Location as LocationPoint;
            if (lp != null && !IsInvalidPoint(lp.Point)) return lp.Point;
            var bb = fi.get_BoundingBox(null);
            if (bb != null) return (bb.Min + bb.Max) * 0.5;
            return XYZ.Zero;
        }

        private static void MoveBy(FamilyInstance fi, XYZ delta)
        {
            if (delta == null || IsInvalidVector(delta) || delta.IsAlmostEqualTo(XYZ.Zero)) return;
            try { ElementTransformUtils.MoveElement(fi.Document, fi.Id, delta); } catch { }
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
            if (sourceConn == null) return;
            double dia = sourceConn.Radius * 2.0; // ft

            var candidates = new[] { "Nominal Diameter", "DN", "Diameter", "Diamètre nominal", "Diamètre", "RBS_PIPE_DIAMETER" };
            foreach (Parameter p in flange.Parameters)
            {
                string name = p.Definition?.Name ?? "";
                if (!candidates.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

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
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        // ---------- Vue 3D cache pour raycast ----------
        static readonly Dictionary<int, ElementId> _view3dCache = new Dictionary<int, ElementId>();
        private static View3D GetCached3DView(Document doc)
        {
            int key = doc.GetHashCode();

            if (_view3dCache.TryGetValue(key, out var id))
            {
                var v = doc.GetElement(id) as View3D;
                if (v != null && !v.IsTemplate && !v.IsPerspective) return v;
            }

            var view = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective);

            if (view != null) _view3dCache[key] = view.Id;
            return view;
        }

        // ======== Création d'instance compatible 2023→2025 ========
        private static FamilyInstance CreateFamilyInstanceSafe(Document doc, XYZ p, FamilySymbol sym, Level lvl)
        {
            try
            {
                // Sur 2023/2024 (et parfois 2025 si catégorie accepte Level)
                return doc.Create.NewFamilyInstance(p, sym, lvl, StructuralType.NonStructural);
            }
            catch (ArgumentException)
            {
                // 2025 : surcharge LevelId
                try
                {
                    var createType = doc.Create.GetType(); // Autodesk.Revit.Creation.Document
                    var mi = createType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "NewFamilyInstance") return false;
                            var ps = m.GetParameters();
                            return ps.Length == 4
                                   && ps[0].ParameterType == typeof(XYZ)
                                   && ps[1].ParameterType == typeof(FamilySymbol)
                                   && ps[2].ParameterType == typeof(ElementId)
                                   && ps[3].ParameterType == typeof(StructuralType);
                        });

                    if (mi != null)
                    {
                        ElementId levelId = lvl != null ? lvl.Id : ElementId.InvalidElementId;
                        var created = mi.Invoke(doc.Create, new object[] { p, sym, levelId, StructuralType.NonStructural }) as FamilyInstance;
                        if (created != null) return created;
                    }
                }
                catch { }

                // Si on est ici : le type ne supporte pas le placement par point → retourne null (skip proprement)
                return null;
            }
        }

        private static void SafeDisconnect(Connector a, Connector b)
        {
            if (a == null || b == null) return;
            try
            {
                if (AreConnected(a, b))
                    a.DisconnectFrom(b);
            }
            catch { }
        }

        private static bool AreConnected(Connector a, Connector b)
        {
            try
            {
                foreach (Connector r in a.AllRefs)
                    if (r.Owner?.Id == b.Owner?.Id && r.Id == b.Id) return true;
            }
            catch { }
            return false;
        }

        private static bool IsInvalidPoint(XYZ p) =>
            p == null || double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsNaN(p.Z) ||
            double.IsInfinity(p.X) || double.IsInfinity(p.Y) || double.IsInfinity(p.Z);

        private static bool IsInvalidVector(XYZ v) =>
            v == null || IsInvalidPoint(new XYZ(v.X, v.Y, v.Z)) || v.GetLength() < 1e-12;
    }

    // === Extensions et helpers de niveau supérieur ===
    internal static class XyzExt
    {
        public static bool IsAlmostEqualTo(this XYZ a, XYZ b, double tol = 1e-9) =>
            Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol && Math.Abs(a.Z - b.Z) < tol;
    }

    internal class PipingAccessoryFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) =>
            e?.Category != null &&
            (e.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory
             || e.Category.Id.IntegerValue == (int)BuiltInCategory.OST_MechanicalEquipment)
            && (e as FamilyInstance) != null
            && HasPipingConnectors(e as FamilyInstance);

        public bool AllowReference(Reference r, XYZ p) => false;

        private static bool HasPipingConnectors(FamilyInstance fi)
        {
            if (fi?.MEPModel?.ConnectorManager == null) return false;
            foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                if (c.Domain == Domain.DomainPiping) return true;
            return false;
        }
    }

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
}

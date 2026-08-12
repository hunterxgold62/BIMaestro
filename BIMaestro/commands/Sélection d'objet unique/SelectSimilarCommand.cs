using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
// REMPLACÉ : using System.Text.Json;
using Newtonsoft.Json; // <-- Newtonsoft.Json
using Color = System.Drawing.Color;

namespace Visualisation
{
    public enum FilterOption { Category, Family, Type }

    // ====================== PREFERENCES & CHEMINS ======================
    internal class SelectSimilarPreferences
    {
        public bool Colorize { get; set; } = false;
        public FilterOption LastCriterion { get; set; } = FilterOption.Family;
        public bool LimitToActiveView { get; set; } = true;
        public bool IncludeAllTagsExplicit { get; set; } = false;

        private const string FileNamePrefs = "SelectSimilarPreferences.json";
        private const string FileNameLastSet = "SelectSimilarLastSet.json";
        private const string SubPath = "RevitLogs\\SauvegardePréférence";

        // OneDrive pro/perso si dispo, sinon Mes Documents
        private static string ResolvePreferredDocuments()
        {
            var odBiz = Environment.GetEnvironmentVariable("OneDriveCommercial");
            if (!string.IsNullOrWhiteSpace(odBiz)) return Path.Combine(odBiz, "Documents");
            var od = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrWhiteSpace(od)) return Path.Combine(od, "Documents");
            var odConsumer = Environment.GetEnvironmentVariable("OneDriveConsumer");
            if (!string.IsNullOrWhiteSpace(odConsumer)) return Path.Combine(odConsumer, "Documents");
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        public static string GetFolder()
        {
            var docsRoot = ResolvePreferredDocuments();
            var target = Path.Combine(docsRoot, SubPath);
            try { Directory.CreateDirectory(target); }
            catch
            {
                var fb = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                target = Path.Combine(fb, SubPath);
                Directory.CreateDirectory(target);
            }
            return target;
        }

        public static string PrefsPath => Path.Combine(GetFolder(), FileNamePrefs);
        public static string LastSetPath => Path.Combine(GetFolder(), FileNameLastSet);

        public static SelectSimilarPreferences Load()
        {
            try
            {
                if (File.Exists(PrefsPath))
                {
                    var json = File.ReadAllText(PrefsPath);
                    var prefs = JsonConvert.DeserializeObject<SelectSimilarPreferences>(json);
                    if (prefs != null) return prefs;
                }
            }
            catch { }
            return new SelectSimilarPreferences();
        }

        public static void Save(SelectSimilarPreferences prefs)
        {
            try
            {
                var json = JsonConvert.SerializeObject(prefs, Formatting.Indented);
                File.WriteAllText(PrefsPath, json);
            }
            catch { }
        }
    }

    // ====================== ETAT : DERNIERE SERIE COLOREE ======================
    internal class LastColoredSet
    {
        public string DocumentTitle { get; set; }
        public string ViewUniqueId { get; set; }
        public List<string> ElementUniqueIds { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public static LastColoredSet Load()
        {
            try
            {
                var path = SelectSimilarPreferences.LastSetPath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<LastColoredSet>(json);
                }
            }
            catch { }
            return null;
        }

        public static void Save(LastColoredSet set)
        {
            try
            {
                var json = JsonConvert.SerializeObject(set, Formatting.Indented);
                File.WriteAllText(SelectSimilarPreferences.LastSetPath, json);
            }
            catch { }
        }

        public static void ClearFile()
        {
            try
            {
                var path = SelectSimilarPreferences.LastSetPath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    // ====================== COMMANDE PRINCIPALE ======================
    [Transaction(TransactionMode.Manual)]
    public class SelectSimilarCommand : BaseTrackedCommand
    {
        private const string ANY_TAG_TOKEN = "__ANY_TAG__";
        protected override string ButtonId => "SelectSimilarCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            var doc = uiDoc.Document;
            var view = doc.ActiveView;

            // 0) Pré-sélection éventuelle
            var preSelIds = uiDoc.Selection.GetElementIds() ?? new List<ElementId>();
            bool hasPreselection = preSelIds.Count > 0;

            // 1) Références initiales (pré-sélection si dispo, sinon PickObjects)
            List<Element> referenceElements = new List<Element>();
            if (hasPreselection)
            {
                foreach (var id in preSelIds)
                {
                    var el = doc.GetElement(id);
                    if (el != null) referenceElements.Add(el);
                }
            }
            else
            {
                IList<Reference> initialRefs;
                try
                {
                    initialRefs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        "Cliquez les éléments de référence, puis Terminer pour ouvrir les options.");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                if (initialRefs == null || initialRefs.Count == 0)
                {
                    TaskDialog.Show(UiLanguage.T("Sélection similaire", "Similar Selection"), UiLanguage.T("Aucun élément de référence sélectionné.", "No reference element was selected."));
                    return Result.Cancelled;
                }

                foreach (var r in initialRefs)
                {
                    var el = doc.GetElement(r.ElementId);
                    if (el != null) referenceElements.Add(el);
                }
            }

            if (referenceElements.Count == 0)
            {
                TaskDialog.Show(UiLanguage.T("Sélection similaire", "Similar Selection"), UiLanguage.T("Impossible d’identifier des éléments de référence.", "Unable to identify reference elements."));
                return Result.Cancelled;
            }

            // 2) TaskDialog (identique)
            var prefs = SelectSimilarPreferences.Load();

            var dlg = new TaskDialog(UiLanguage.T("Sélection similaire", "Similar Selection"))
            {
                MainInstruction = hasPreselection
                    ? UiLanguage.T("Choisissez comment retrouver les éléments similaires à la sélection actuelle.", "Choose how to find elements similar to the current selection.")
                    : UiLanguage.T("Choisissez comment retrouver les éléments similaires aux éléments de référence.", "Choose how to find elements similar to the reference elements."),
                MainContent = UiLanguage.T(
                    "1. Choisissez le critère.\n2. Cliquez les éléments à garder dans la vue.\n3. Terminez pour appliquer la sélection.\n\nCatégorie : même catégorie (Murs, Sols, Portes, ...)\nFamille : même famille (Murs et Sols : nom du type)\nType : type exact.",
                    "1. Choose the criterion.\n2. Click the elements to keep in the view.\n3. Finish to apply the selection.\n\nCategory: same category (Walls, Floors, Doors, ...)\nFamily: same family (Walls and Floors: type name)\nType: exact type."),
                CommonButtons = TaskDialogCommonButtons.Close,
                VerificationText = UiLanguage.T("Colorier les éléments sélectionnés", "Color selected elements")
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, UiLanguage.T("Sélectionner par Catégorie", "Select by Category"));
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, UiLanguage.T("Sélectionner par Famille", "Select by Family"));
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, UiLanguage.T("Sélectionner par Type", "Select by Type"));
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, UiLanguage.T("Effacer mes dernières couleurs (mémorisées)", "Clear my last saved colors"));

            var dr = dlg.Show();
            prefs.Colorize = dlg.WasVerificationChecked();
            SelectSimilarPreferences.Save(prefs);

            if (dr == TaskDialogResult.CommandLink4)
                return ClearOverridesByLastSetFlow(doc);

            var opt = dr switch
            {
                TaskDialogResult.CommandLink1 => FilterOption.Category,
                TaskDialogResult.CommandLink2 => FilterOption.Family,
                TaskDialogResult.CommandLink3 => FilterOption.Type,
                _ => (FilterOption)(-1)
            };
            if (!Enum.IsDefined(typeof(FilterOption), opt)) return Result.Cancelled;
            prefs.LastCriterion = opt;
            SelectSimilarPreferences.Save(prefs);

            // 3) Préparer comparateurs (ajout de FLOORS en plus des WALLS)
            var wallCatId = Category.GetCategory(doc, BuiltInCategory.OST_Walls)?.Id;
            var floorCatId = Category.GetCategory(doc, BuiltInCategory.OST_Floors)?.Id;

            var catIds = new HashSet<ElementId>();
            var famNames = new HashSet<string>(StringComparer.Ordinal);
            var typeIds = new HashSet<ElementId>();
            bool includeAllTags = opt == FilterOption.Type ? false : prefs.IncludeAllTagsExplicit;

            BuildComparatorsFromElements(
                doc, referenceElements, opt, wallCatId, floorCatId,
                catIds, famNames, typeIds, ref includeAllTags);

            // 4) Sélection des éléments similaires
            var filter = new SimilarElementFilter(
                doc, opt, catIds, famNames, typeIds, includeAllTags, wallCatId, floorCatId);

            IList<Reference> selRefs;
            try
            {
                selRefs = uiDoc.Selection.PickObjects(
                    ObjectType.Element, filter,
                    "Sélectionnez les éléments similaires (Échap pour terminer).");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            var selIds = selRefs.Select(x => x.ElementId).Distinct().ToList();
            uiDoc.Selection.SetElementIds(selIds);

            // 5) Sans coloration => fin
            if (!prefs.Colorize)
            {
                return Result.Succeeded;
            }

            // 6) Appliquer une couleur stable par groupe, puis mémoriser la série.
            var coloredUniqueIds = new List<string>();
            using (var t = new Transaction(doc, "Surligner similaires"))
            {
                t.Start();

                var solidPatternId = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .First(fp => fp.GetFillPattern().IsSolidFill)
                    .Id;

                foreach (var id in selIds)
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;

                    var key = ComputeKey(doc, wallCatId, floorCatId, el);
                    var c = GetStableColorForKey(key);
                    var rc = new Autodesk.Revit.DB.Color(c.R, c.G, c.B);

                    var ogs = new OverrideGraphicSettings()
                        .SetSurfaceForegroundPatternId(solidPatternId)
                        .SetSurfaceForegroundPatternColor(rc)
                        .SetSurfaceBackgroundPatternId(solidPatternId)
                        .SetSurfaceBackgroundPatternColor(rc)
                        .SetCutForegroundPatternId(solidPatternId)
                        .SetCutForegroundPatternColor(rc)
                        .SetCutBackgroundPatternId(solidPatternId)
                        .SetCutBackgroundPatternColor(rc)
                        .SetSurfaceTransparency(0);

                    view.SetElementOverrides(id, ogs);

                    var uid = el.UniqueId;
                    if (!string.IsNullOrEmpty(uid))
                        coloredUniqueIds.Add(uid);
                }

                t.Commit();
            }

            var last = new LastColoredSet
            {
                DocumentTitle = doc.Title,
                ViewUniqueId = view.UniqueId,
                ElementUniqueIds = coloredUniqueIds.Distinct().ToList(),
                Timestamp = DateTime.Now
            };
            LastColoredSet.Save(last);



            return Result.Succeeded;
        }

        // ====== Effacer (dernière série mémorisée) ======
        private Result ClearOverridesByLastSetFlow(Document doc)
        {
            var last = LastColoredSet.Load();
            if (last == null || last.ElementUniqueIds == null || last.ElementUniqueIds.Count == 0)
            {
                TaskDialog.Show(UiLanguage.T("Nettoyage", "Cleanup"), UiLanguage.T("Aucune série colorée mémorisée à effacer.", "No saved color set is available to clear."));
                return Result.Cancelled;
            }

            var targetView = new FilteredElementCollector(doc)
                                .OfClass(typeof(View))
                                .Cast<View>()
                                .FirstOrDefault(v => v.UniqueId == last.ViewUniqueId);

            if (targetView == null)
            {
                TaskDialog.Show(UiLanguage.T("Nettoyage", "Cleanup"), UiLanguage.T("La vue mémorisée n’existe plus dans ce document. Rien n’a été effacé.", "The saved view no longer exists in this document. Nothing was cleared."));
                LastColoredSet.ClearFile();
                return Result.Cancelled;
            }

            int cleared = 0, missing = 0;
            using (var t = new Transaction(doc, "Effacer mes dernières couleurs"))
            {
                t.Start();
                var empty = new OverrideGraphicSettings();

                foreach (var uid in last.ElementUniqueIds.Distinct())
                {
                    var el = doc.GetElement(uid);
                    if (el == null) { missing++; continue; }
                    targetView.SetElementOverrides(el.Id, empty);
                    cleared++;
                }

                t.Commit();
            }

            LastColoredSet.ClearFile();

            TaskDialog.Show(UiLanguage.T("Nettoyage", "Cleanup"), UiLanguage.T(
                $"Dernière série effacée sur la vue : {targetView.Name}\nÉléments nettoyés : {cleared}" +
                (missing > 0 ? $"\nIntrouvables : {missing}" : string.Empty),
                $"Last color set cleared on view: {targetView.Name}\nElements cleared: {cleared}" +
                (missing > 0 ? $"\nNot found: {missing}" : string.Empty)));
            return Result.Succeeded;
        }

        // ---- Helpers
        private static string ComputeKey(Document doc, ElementId wallCatId, ElementId floorCatId, Element el)
        {
            if (el is IndependentTag) return "Étiquettes";

            // Murs & Sols (familles système) => clé = nom du Type
            if (el?.Category != null)
            {
                var catId = el.Category.Id;
                if ((wallCatId != null && catId == wallCatId) ||
                    (floorCatId != null && catId == floorCatId))
                {
                    var et = doc.GetElement(el.GetTypeId()) as ElementType;
                    return et != null ? et.Name : (catId == wallCatId ? "Mur" : "Sol");
                }
            }

            if (el is FamilyInstance fi) return fi.Symbol.FamilyName;

            var et2 = doc.GetElement(el.GetTypeId()) as ElementType;
            return !string.IsNullOrEmpty(et2?.FamilyName) ? et2.FamilyName : et2?.Name ?? el.Category?.Name ?? "Autres";
        }

        private static System.Drawing.Color GetStableColorForKey(string key)
        {
            var normalized = string.IsNullOrWhiteSpace(key) ? "Autres" : key.Trim().ToUpperInvariant();
            var hash = StableHash(normalized);
            var hue = (hash % 360) / 360.0;
            return ColorFromHSL(hue, 0.55, 0.78);
        }

        private static uint StableHash(string value)
        {
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                uint hash = offset;
                foreach (var ch in value ?? string.Empty)
                {
                    hash ^= ch;
                    hash *= prime;
                }
                return hash;
            }
        }

        private static System.Drawing.Color ColorFromHSL(double h, double s, double l)
        {
            double r, g, b;
            if (s == 0) { r = g = b = l; }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = Hue2RGB(p, q, h + 1.0 / 3);
                g = Hue2RGB(p, q, h);
                b = Hue2RGB(p, q, h - 1.0 / 3);
            }
            return System.Drawing.Color.FromArgb((int)(r * 255.0), (int)(g * 255.0), (int)(b * 255.0));
        }
        private static double Hue2RGB(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        // ---- Nouveau helper : construit les comparateurs à partir d'une liste d'éléments
        private static void BuildComparatorsFromElements(
            Document doc,
            IEnumerable<Element> initialElements,
            FilterOption opt,
            ElementId wallCatId,
            ElementId floorCatId,
            HashSet<ElementId> catIds,
            HashSet<string> famNames,
            HashSet<ElementId> typeIds,
            ref bool includeAllTags)
        {
            bool flag = includeAllTags;

            foreach (var el in initialElements)
            {
                if (el == null) continue;

                if (el is IndependentTag && opt != FilterOption.Type) flag = true;

                switch (opt)
                {
                    case FilterOption.Category:
                        if (el.Category != null) catIds.Add(el.Category.Id);
                        break;

                    case FilterOption.Family:
                        // Murs & Sols (famille système) : utiliser le nom du Type comme "famille"
                        if (el.Category != null)
                        {
                            var catId = el.Category.Id;
                            if ((wallCatId != null && catId == wallCatId) ||
                                (floorCatId != null && catId == floorCatId))
                            {
                                if (doc.GetElement(el.GetTypeId()) is ElementType wt)
                                    famNames.Add(wt.Name);
                                break;
                            }
                        }

                        if (el is IndependentTag)
                        {
                            famNames.Add(ANY_TAG_TOKEN);
                        }
                        else if (el is FamilyInstance fi)
                        {
                            famNames.Add(fi.Symbol.FamilyName);
                        }
                        else
                        {
                            if (doc.GetElement(el.GetTypeId()) is ElementType et)
                            {
                                if (!string.IsNullOrEmpty(et.FamilyName)) famNames.Add(et.FamilyName);
                                famNames.Add(et.Name);
                            }
                        }
                        break;

                    case FilterOption.Type:
                        typeIds.Add(el.GetTypeId());
                        break;
                }
            }

            includeAllTags = flag;
        }
    }

    // ====================== FILTRE DE SELECTION ======================
    public class SimilarElementFilter : ISelectionFilter
    {
        private readonly Document _doc;
        private readonly FilterOption _opt;
        private readonly HashSet<ElementId> _catIds;
        private readonly HashSet<string> _famNames;
        private readonly HashSet<ElementId> _typeIds;
        private readonly bool _includeAllTags;
        private readonly ElementId _wallCatId;
        private readonly ElementId _floorCatId;

        public SimilarElementFilter(
            Document doc, FilterOption opt,
            HashSet<ElementId> catIds, HashSet<string> famNames, HashSet<ElementId> typeIds,
            bool includeAllTags, ElementId wallCatId, ElementId floorCatId)
        {
            _doc = doc; _opt = opt;
            _catIds = catIds ?? new HashSet<ElementId>();
            _famNames = famNames ?? new HashSet<string>(StringComparer.Ordinal);
            _typeIds = typeIds ?? new HashSet<ElementId>();
            _includeAllTags = includeAllTags;
            _wallCatId = wallCatId;
            _floorCatId = floorCatId;
        }

        public bool AllowElement(Element elem)
        {
            if (_includeAllTags && _opt != FilterOption.Type && elem is IndependentTag) return true;

            switch (_opt)
            {
                case FilterOption.Category:
                    return elem.Category != null && _catIds.Contains(elem.Category.Id);

                case FilterOption.Family:
                    // Murs & Sols : comparer par nom de Type
                    if (elem.Category != null)
                    {
                        var catId = elem.Category.Id;
                        if ((_wallCatId != null && catId == _wallCatId) ||
                            (_floorCatId != null && catId == _floorCatId))
                        {
                            var t = _doc.GetElement(elem.GetTypeId()) as ElementType;
                            return t != null && _famNames.Contains(t.Name);
                        }
                    }

                    if (_famNames.Contains("__ANY_TAG__") && elem is IndependentTag) return true;

                    if (elem is FamilyInstance fi)
                        return _famNames.Contains(fi.Symbol.FamilyName);

                    var et = _doc.GetElement(elem.GetTypeId()) as ElementType;
                    if (et != null)
                        return (!string.IsNullOrEmpty(et.FamilyName) && _famNames.Contains(et.FamilyName))
                               || _famNames.Contains(et.Name);
                    return false;

                case FilterOption.Type:
                    return _typeIds.Contains(elem.GetTypeId());

                default:
                    return false;
            }
        }

        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}

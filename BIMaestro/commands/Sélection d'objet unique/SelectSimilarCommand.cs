using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Licensing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    // ====================== COULEURS PAR TYPE (PERSISTANTES) ======================
    internal class SelectSimilarColorMap
    {
        private const string FileNameColorMap = "SelectSimilarColorMap.json";

        public Dictionary<string, Dictionary<string, string>> Documents { get; set; }
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        public static string ColorMapPath => Path.Combine(SelectSimilarPreferences.GetFolder(), FileNameColorMap);

        public static SelectSimilarColorMap Load()
        {
            try
            {
                if (File.Exists(ColorMapPath))
                {
                    var json = File.ReadAllText(ColorMapPath);
                    var map = JsonConvert.DeserializeObject<SelectSimilarColorMap>(json);
                    if (map != null && map.Documents != null) return map;
                }
            }
            catch { }

            return new SelectSimilarColorMap();
        }

        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ColorMapPath, json);
            }
            catch { }
        }

        public bool TryGetColor(Document doc, string key, out Color color)
        {
            color = default;
            if (doc == null || string.IsNullOrWhiteSpace(key)) return false;

            var docKey = GetDocumentKey(doc);
            if (!Documents.TryGetValue(docKey, out var map)) return false;
            if (!map.TryGetValue(key, out var hex)) return false;

            return TryParseHex(hex, out color);
        }

        public void SetColor(Document doc, string key, Color color)
        {
            if (doc == null || string.IsNullOrWhiteSpace(key)) return;
            var docKey = GetDocumentKey(doc);
            if (!Documents.TryGetValue(docKey, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                Documents[docKey] = map;
            }

            map[key] = ToHex(color);
        }

        public Color CreateColor(string key)
        {
            double hue = ComputeHue(key);
            return ColorFromHSL(hue, 0.5, 0.8);
        }

        private Color ColorFromHSL(double hue, double v1, double v2)
        {
            throw new NotImplementedException();
        }

        private static string GetDocumentKey(Document doc)
        {
            if (!string.IsNullOrWhiteSpace(doc.PathName)) return doc.PathName;
            return doc.Title ?? "DocumentInconnu";
        }

        private static double ComputeHue(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0.0;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                int value = (bytes[0] << 8) + bytes[1];
                return value / 65535.0;
            }
        }

        private static string ToHex(Color color)
            => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static bool TryParseHex(string hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            try
            {
                color = ColorTranslator.FromHtml(hex);
                return true;
            }
            catch
            {
                return false;
            }
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
                    TaskDialog.Show("Sélection similaire", "Aucun élément de référence sélectionné.");
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
                TaskDialog.Show("Sélection similaire", "Impossible d’identifier des éléments de référence.");
                return Result.Cancelled;
            }

            // 2) TaskDialog (identique)
            var prefs = SelectSimilarPreferences.Load();

            var dlg = new TaskDialog("Sélection similaire")
            {
                MainInstruction = "Que voulez-vous faire ?",
                MainContent =
                    "• Catégorie : même catégorie (Murs, Sols, Portes, …)\n" +
                    "• Famille : même famille (Murs & Sols : clé = nom du Type)\n" +
                    "• Type : même type exact",
                CommonButtons = TaskDialogCommonButtons.Close,
                VerificationText = "Colorier les éléments (préférence)"
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Sélectionner par Catégorie");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Sélectionner par Famille");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Sélectionner par Type");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Effacer mes dernières couleurs (mémorisées)");

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

            // 6) Groupage + palette
            var groupKeys = selIds
                .Select(id => ComputeKey(doc, wallCatId, floorCatId, doc.GetElement(id)))
                .Distinct(StringComparer.Ordinal).ToList();

            var colorMap = SelectSimilarColorMap.Load();
            bool colorMapChanged = false;
            var palette = new Dictionary<string, System.Drawing.Color>(StringComparer.Ordinal);
            foreach (var key in groupKeys)
            {
                if (!colorMap.TryGetColor(doc, key, out var color))
                {
                    color = colorMap.CreateColor(key);
                    colorMap.SetColor(doc, key, color);
                    colorMapChanged = true;
                }
                palette[key] = color;
            }

            // 7) Appliquer & mémoriser la série
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
                    var c = palette[key];
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

            if (colorMapChanged)
            {
                colorMap.Save();
            }

            return Result.Succeeded;
        }

        // ====== Effacer (dernière série mémorisée) ======
        private Result ClearOverridesByLastSetFlow(Document doc)
        {
            var last = LastColoredSet.Load();
            if (last == null || last.ElementUniqueIds == null || last.ElementUniqueIds.Count == 0)
            {
                TaskDialog.Show("Nettoyage", "Aucune série colorée mémorisée à effacer.");
                return Result.Cancelled;
            }

            var targetView = new FilteredElementCollector(doc)
                                .OfClass(typeof(View))
                                .Cast<View>()
                                .FirstOrDefault(v => v.UniqueId == last.ViewUniqueId);

            if (targetView == null)
            {
                TaskDialog.Show("Nettoyage",
                    "La vue mémorisée n’existe plus dans ce document. Rien n’a été effacé.");
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

            TaskDialog.Show("Nettoyage",
                $"Dernière série effacée sur la vue : {targetView.Name}\n" +
                $"Éléments nettoyés : {cleared}" +
                (missing > 0 ? $"\nIntrouvables : {missing}" : string.Empty));
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

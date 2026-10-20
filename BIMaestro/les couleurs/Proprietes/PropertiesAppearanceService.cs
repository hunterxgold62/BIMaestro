using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed class PropertyTarget
    {
        internal string Key, Name, GroupKey, NativeName;
        internal bool ReadOnly;
        public override string ToString() => Name;
    }
    internal sealed class AppearanceContext
    {
        internal string CategoryKey, CategoryName, TypeKey, TypeName, Caption, Signature;
        internal List<PropertyTarget> Groups = new List<PropertyTarget>();
        internal List<PropertyTarget> Parameters = new List<PropertyTarget>();
        internal Dictionary<string, PropertyTarget> ParameterMap = new Dictionary<string, PropertyTarget>();
        internal Dictionary<string, string> GroupMap = new Dictionary<string, string>();

        internal static AppearanceContext Capture(UIApplication app)
        {
            var result = new AppearanceContext { Caption = "Ouvrez un projet et sélectionnez un élément, ou activez une vue.", Signature = "none" };
            var ui = app.ActiveUIDocument;
            if (ui == null) return result;
            var ids = ui.Selection.GetElementIds();
            if (ids.Count > 1) { result.Caption = "Sélection multiple : choisissez un seul élément pour personnaliser ses propriétés."; return result; }
            Element element = ids.Count == 1 ? ui.Document.GetElement(ids.First()) : ui.ActiveView;
            if (element == null) return result;
            result.CategoryKey = "cat:" + (element.Category?.Id.ToString() ?? ((int)BuiltInCategory.OST_Views).ToString());
            result.CategoryName = element.Category?.Name ?? "Vues";
            ElementType type = ui.Document.GetElement(element.GetTypeId()) as ElementType;
            string typeName = element is View v ? v.ViewType.ToString() : type == null ? null : type.FamilyName + " / " + type.Name;
            result.TypeKey = typeName == null ? null : result.CategoryKey + "/type:" + typeName;
            result.TypeName = typeName;
            result.Caption = result.CategoryName + " · " + element.Name;
            result.Signature = ui.Document.GetHashCode() + "/" + element.Id + "/" + result.TypeKey;
            var targets = new List<PropertyTarget>();
            foreach (Parameter p in element.Parameters)
            {
                try
                {
                    string group = p.Definition.GetGroupTypeId().TypeId;
                    string label = LabelUtils.GetLabelForGroup(p.Definition.GetGroupTypeId());
                    if (string.IsNullOrWhiteSpace(label)) label = "Autres";
                    if (!result.Groups.Any(g => g.Key == group)) result.Groups.Add(new PropertyTarget { Key = group, Name = label, NativeName = AppearancePreferences.Normalize(label) });
                    string identity = p.IsShared ? "shared:" + p.GUID : p.Id.ToString().StartsWith("-", StringComparison.Ordinal) ? "builtin:" + p.Id : "name:" + p.Definition.Name;
                    targets.Add(new PropertyTarget { Key = group + "/" + identity, GroupKey = group, Name = label + " · " + p.Definition.Name, NativeName = AppearancePreferences.Normalize(p.Definition.Name), ReadOnly = p.IsReadOnly });
                }
                catch { /* A missing label must not interrupt native selection. */ }
            }
            foreach (var g in result.Groups.GroupBy(g => g.NativeName).Where(g => g.Count() == 1)) result.GroupMap[g.Key] = g.First().Key;
            // Ambiguous same-name parameters are intentionally left native.
            foreach (var g in targets.GroupBy(p => p.GroupKey + "|" + p.NativeName).Where(g => g.Count() == 1))
            { result.ParameterMap[g.Key] = g.First(); result.Parameters.Add(g.First()); }
            result.Groups = result.Groups.OrderBy(g => g.Name).ToList();
            result.Parameters = result.Parameters.OrderBy(p => p.Name).ToList();
            return result;
        }
    }

    internal static class PropertiesAppearanceService
    {
        private static AppearancePreferences _saved;
        private static AppearancePreferences _preview;
        private static PropertiesPaletteRenderer _renderer;
        private static DateTime _next;
        private static string _signature;
        private static DateTime _nextFileCheck;
        private static DateTime _fileStamp;
        internal static string Status { get; private set; } = "Personnalisation désactivée.";

        internal static bool IsDark
        {
            get
            {
                try { var t = typeof(UIApplication).Assembly.GetType("Autodesk.Revit.UI.UIThemeManager"); return t?.GetProperty("CurrentTheme", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null)?.ToString() == "Dark"; }
                catch { return false; }
            }
        }
        internal static AppearancePreferences Saved => _saved ?? (_saved = AppearanceStore.Load());
        internal static void ReloadSaved()
        {
            var value = AppearanceStore.Load();
            _saved = value; _fileStamp = System.IO.File.Exists(AppearanceStore.FilePath) ? System.IO.File.GetLastWriteTimeUtc(AppearanceStore.FilePath) : DateTime.MinValue;
            Invalidate();
        }
        internal static void SetPreview(IEnumerable<AppearanceProfile> profiles, bool enabled)
        {
            var changes = profiles.ToArray();
            var draft = new AppearancePreferences { Enabled = enabled, Profiles = Saved.Profiles.Where(p => !changes.Any(c => c.Key == p.Key)).Select(p => p.Copy()).ToList() };
            draft.Profiles.AddRange(changes.Select(p => p.Copy())); _preview = draft; Invalidate();
        }
        internal static void Commit(IEnumerable<AppearanceProfile> profiles, bool enabled) { _saved = AppearanceStore.SaveMany(profiles, enabled); _preview = null; Invalidate(); }
        internal static void CancelPreview() { _preview = null; Invalidate(); }
        private static void Invalidate() { _signature = null; _next = DateTime.MinValue; _renderer?.Dispose(); _renderer = null; }
        internal static void Shutdown() { PropertiesAppearanceCommand.Shutdown(); _preview = null; _renderer?.Dispose(); _renderer = null; }

        internal static void ProcessIdling(UIApplication app)
        {
            try
            {
                if (DateTime.UtcNow < _next) return;
                _next = DateTime.UtcNow.AddMilliseconds(450);
                if (_preview == null && DateTime.UtcNow >= _nextFileCheck)
                {
                    _nextFileCheck = DateTime.UtcNow.AddSeconds(2);
                    var stamp = System.IO.File.Exists(AppearanceStore.FilePath) ? System.IO.File.GetLastWriteTimeUtc(AppearanceStore.FilePath) : DateTime.MinValue;
                    if (stamp != _fileStamp) ReloadSaved();
                }
                var preferences = _preview ?? Saved;
                if (!preferences.Enabled || !preferences.Profiles.Any(p => p.Rules.Count > 0))
                { if (_renderer != null) { _renderer.Dispose(); _renderer = null; } Status = "Apparence Revit d’origine."; return; }
                var context = AppearanceContext.Capture(app);
                if (context.CategoryKey == null) { _renderer?.Dispose(); _renderer = null; Status = context.Caption; return; }
                var grid = NativePropertiesGrid.Find(app);
                if (grid == null) { _renderer?.Dispose(); _renderer = null; Status = "Affichez la palette Propriétés pour voir vos couleurs."; return; }
                string signature = context.Signature + "/" + IsDark;
                
                var cells = new List<PropertiesPaletteRenderer.CellPlan>();
                string group = null;
                for (uint row = 1; row <= grid.RowCount; row++)
                {
                    string raw = grid.Value(row, 1); if (string.IsNullOrWhiteSpace(raw)) continue;
                    bool header = !string.IsNullOrWhiteSpace(grid.Value(row, 3));
                    if (header) context.GroupMap.TryGetValue(AppearancePreferences.Normalize(raw), out group);
                    if (group == null) continue;
                    PropertyTarget parameter = null;
                    if (!header) context.ParameterMap.TryGetValue(group + "|" + AppearancePreferences.Normalize(raw), out parameter);
                    if (!header && (parameter == null || parameter.ReadOnly)) continue;
                    var colors = preferences.Resolve(context.CategoryKey, context.TypeKey, group, parameter?.Key, IsDark);
                    if (colors == null) continue;
                    cells.Add(new PropertiesPaletteRenderer.CellPlan { Row = row, Label = raw, Header = header, Colors = colors, Dark = IsDark });
                }
                if (_renderer == null || _renderer.Window != grid.Window || _signature != signature)
                { _renderer?.Dispose(); _renderer = new PropertiesPaletteRenderer(grid); _signature = signature; }
                _renderer.Update(grid, cells);
                Status = cells.Count == 0 ? "Aucune règle applicable à cette sélection. Les champs grisés gardent leur style Revit." : cells.Count + " ligne(s) personnalisée(s) · champs grisés et saisie conservés.";
                if (_renderer.Error != null) Status = _renderer.Error;
            }
            catch (Exception ex)
            {
                _renderer?.Dispose(); _renderer = null;
                Status = "Palette conservée dans son état Revit : " + ex.Message;
                _next = DateTime.UtcNow.AddSeconds(5);
            }
        }
    }
}

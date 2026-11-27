using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed class PropertyTarget
    {
        internal string Key, Name, GroupKey, NativeName, DisplayValue;
        // Displayed value for each selected element, only for parameters used by a condition.
        internal List<string> Values;
        internal bool ReadOnly;
        internal long? NativeParameterId;
        public override string ToString() => Name;
    }
    internal sealed class AppearanceContext
    {
        internal string CategoryKey, CategoryName, TypeKey, TypeName, Caption, Signature;
        internal List<PropertyPreviewRow> PreviewRows = new List<PropertyPreviewRow>();
        internal string PreviewHint;
        internal List<PropertyTarget> Groups = new List<PropertyTarget>();
        internal List<PropertyTarget> Parameters = new List<PropertyTarget>();
        internal Dictionary<string, PropertyTarget> ParameterMap = new Dictionary<string, PropertyTarget>();
        internal Dictionary<string, string> GroupMap = new Dictionary<string, string>();
        internal IList<string> ValuesOf(string parameterKey) => Parameters.FirstOrDefault(p => p.Key == parameterKey)?.Values;

        internal static AppearanceContext ForEditor(UIApplication app)
        {
            var context = Capture(app, true);
            if (context.CategoryKey == null) return context;
            try
            {
                var grid = NativePropertiesGrid.Find(app);
                if (grid != null)
                {
                    var original=PropertiesPaletteLayout.OriginalPreview(grid,context);
                    if(original!=null){context.PreviewRows=original;context.PreviewHint=UiLanguage.Get("PropertiesAppearanceService.DragAParameterOntoACustomGroupHeader");return context;}
                    string group = null;
                    for (uint row = 1; row <= grid.RowCount; row++)
                    {
                        string label = grid.Value(row, 1);
                        if (string.IsNullOrWhiteSpace(label)) continue;
                        bool header = grid.IsGroupHeader(row);
                        if (header && !context.GroupMap.TryGetValue(AppearancePreferences.Normalize(label), out group)) group = "native:" + AppearancePreferences.Normalize(label);
                        PropertyTarget parameter = null;
                        if (!header && group != null) context.ParameterMap.TryGetValue(group + "|" + AppearancePreferences.Normalize(label), out parameter);
                        context.PreviewRows.Add(new PropertyPreviewRow { GroupKey = group, ParameterKey = parameter?.Key,
                            Header = header, Label = label, Value = header ? "" : parameter?.DisplayValue ?? grid.Value(row, 2),
                            ReadOnly = header ? group == null || group.StartsWith("native:", StringComparison.Ordinal) : parameter == null || parameter.ReadOnly || !grid.CanStyle(row) });
                    }
                    if (context.PreviewRows.Count > 0) { context.PreviewHint = UiLanguage.Get("PropertiesAppearanceService.ClickToColorDragTheHandleToReorder");
                        context.PreviewRows = PropertiesPaletteLayout.OriginalOrder(context.PreviewRows, context.Signature); return context; }
                }
            }
            catch { /* The editor stays usable when a native binary is not qualified. */ }
            context.PreviewHint = UiLanguage.Get("PropertiesAppearanceService.ApproximatePreviewValuesWillBeVisibleWithThe");
            foreach (var group in context.Groups)
            {
                context.PreviewRows.Add(new PropertyPreviewRow { Header = true, GroupKey = group.Key, Label = group.Name });
                foreach (var parameter in context.Parameters.Where(p => p.GroupKey == group.Key))
                    context.PreviewRows.Add(new PropertyPreviewRow { GroupKey = group.Key, ParameterKey = parameter.Key,
                        Label = parameter.Name.Substring(parameter.Name.IndexOf(" · ", StringComparison.Ordinal) + 3),
                        Value = parameter.DisplayValue ?? "—", ReadOnly = parameter.ReadOnly });
            }
            return context;
        }

        private static string DisplayValue(Parameter p)
        {
            try
            {
                if (p.Id.ToString() == ((int)BuiltInParameter.VIEW_SCALE).ToString()) return "1 : " + p.AsInteger();
                string formatted = p.AsValueString();
                if (!string.IsNullOrWhiteSpace(formatted)) return formatted;
                if (p.StorageType == StorageType.String) return p.AsString() ?? "";
                if (p.StorageType == StorageType.ElementId)
                {
                    var id = p.AsElementId();
                    return id == ElementId.InvalidElementId ? "Aucun(e)" : p.Element.Document.GetElement(id)?.Name ?? id.ToString();
                }
                if (p.StorageType == StorageType.Integer)
                    return p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo ? (p.AsInteger() == 0 ? "Non" : "Oui") : p.AsInteger().ToString();
                if (p.StorageType == StorageType.Double) return p.AsDouble().ToString("G4", System.Globalization.CultureInfo.CurrentCulture);
            }
            catch { }
            return "";
        }

        internal static AppearanceContext Capture(UIApplication app, bool displayValues = false, ICollection<string> valueKeys = null)
        {
            var result = new AppearanceContext { Caption = UiLanguage.Get("PropertiesAppearanceService.OpenAProjectAndSelectAnElementOr"), Signature = "none" };
            var ui = app.ActiveUIDocument;
            if (ui == null) return result;
            var ids = ui.Selection.GetElementIds();
            var selected=ids.Select(ui.Document.GetElement).Where(e=>e!=null).ToArray();
            if(ids.Count>1 && (selected.Length!=ids.Count || selected.Any(e=>e.Category?.Id!=selected[0].Category?.Id)))
            {result.Caption=UiLanguage.Get("PropertiesAppearanceService.SelectionSpansDifferentCategoriesRevitAppearanceKept");return result;}
            Element element = ids.Count > 0 ? selected.FirstOrDefault() : ui.ActiveView;
            if (element == null) return result;
            result.CategoryKey = "cat:" + (element.Category?.Id.ToString() ?? ((int)BuiltInCategory.OST_Views).ToString());
            result.CategoryName = element.Category?.Name ?? "Vues";
            ElementType type = ui.Document.GetElement(element.GetTypeId()) as ElementType;
            bool sameType=selected.All(e=>e.GetTypeId()==element.GetTypeId());
            string typeName = !sameType?null:element is View v ? v.ViewType.ToString() : type == null ? null : type.FamilyName + " / " + type.Name;
            result.TypeKey = typeName == null ? null : result.CategoryKey + "/type:" + typeName;
            result.TypeName = element is View ? type?.Name ?? typeName : typeName;
            result.Caption = result.CategoryName + " · " + (ids.Count>1?ids.Count+UiLanguage.Get("PropertiesAppearanceService.Elements"):element.Name);
            result.Signature = ui.Document.GetHashCode() + "/" + (ids.Count>1?string.Join(",",ids.Select(id=>id.ToString()).OrderBy(id=>id,StringComparer.Ordinal)):element.Id.ToString()) + "/" + result.TypeKey;
            var common=selected.Skip(1).Select(e=>e.Parameters.Cast<Parameter>().GroupBy(p=>p.Id.ToString()).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single())).ToArray();
            var targets = new List<PropertyTarget>();
            foreach (Parameter p in element.Parameters)
            {
                try
                {
                    string nativeId=p.Id.ToString();
                    if(common.Any(map=>!map.ContainsKey(nativeId)))continue;
                    string group = p.Definition.GetGroupTypeId().TypeId;
                    string label = LabelUtils.GetLabelForGroup(p.Definition.GetGroupTypeId());
                    if (string.IsNullOrWhiteSpace(label)) label = "Autres";
                    if (!result.Groups.Any(g => g.Key == group)) result.Groups.Add(new PropertyTarget { Key = group, Name = label, NativeName = AppearancePreferences.Normalize(label) });
                    string identity = p.IsShared ? "shared:" + p.GUID : p.Id.ToString().StartsWith("-", StringComparison.Ordinal) ? "builtin:" + p.Id : "name:" + p.Definition.Name;
                    string key = group + "/" + identity;
                    // Above 500 elements the per-element reading would slow Revit down.
                    List<string> values = (displayValues || valueKeys?.Contains(key) == true) && selected.Length <= 500
                        ? new[] { DisplayValue(p) }.Concat(common.Select(map => DisplayValue(map[nativeId]))).ToList() : null;
                    string value=displayValues?values?[0]??DisplayValue(p):null;
                    if(displayValues&&common.Any(map=>DisplayValue(map[nativeId])!=value))value="<multiples>";
                    targets.Add(new PropertyTarget { Key = key, Values = values, GroupKey = group, Name = label + " · " + p.Definition.Name, NativeName = AppearancePreferences.Normalize(p.Definition.Name), NativeParameterId = long.Parse(nativeId, System.Globalization.CultureInfo.InvariantCulture), DisplayValue = value, ReadOnly = p.IsReadOnly||common.Any(map=>map[nativeId].IsReadOnly) });
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
        internal static long ModelRevision {get;private set;}
        private static Autodesk.Revit.ApplicationServices.Application _observedApplication;
        private static void ModelChanged(object sender,Autodesk.Revit.DB.Events.DocumentChangedEventArgs args){ModelRevision++;}
#if BIMAESTRO_PROPERTIES_TESTS
        // The isolated group prototype owns its permutation after normal ordering.
        // Keep the two native layout writers from moving the same rows independently.
        internal static Func<NativePropertiesGrid, bool> CustomLayoutOwnsGrid;
        internal static Func<NativePropertiesGrid, uint, PaletteColors> CustomRowColors;
        internal static long LastColoredPaint=>_renderer?.LastColoredPaint??0;
        internal static bool HasCurrentColorPlan=>_renderer?.HasCurrentColorPlan==true;
#endif
        private static readonly PalettePresentationGate Presentation=new PalettePresentationGate();
        private static ExternalEvent _presentationEvent;
        private sealed class PresentationHandler:IExternalEventHandler
        {
            public string GetName()=>"Actualisation atomique de la palette";
            public void Execute(UIApplication app){_next=DateTime.MinValue;ProcessIdling(app);}
        }
        // Lets the open editor follow the Revit selection.
        internal static event Action ContextChanged;
        internal static void NotifyContextChanging()
        {
            _next=DateTime.MinValue;
            try{ContextChanged?.Invoke();}catch{}
            // Revit can reuse the same native HWND for a different selection.
            // Never let WM_PAINT reapply a closure captured for the previous owner.
            if(_renderer!=null)_renderer.BeforePaint=null;
            _signature=null;
            var preferences=_preview??_saved;
            if(preferences==null||!preferences.Enabled)return;
            try{if(!TextInputFocus.IsActive()){Presentation.Hold(_cachedGrid);_presentationEvent?.Raise();}}
            catch{Presentation.Release();}
        }
        private static AppearancePreferences _saved;
        private static AppearancePreferences _preview;
        private static PropertiesPaletteRenderer _renderer;
        private static NativePropertiesGrid _cachedGrid;
        private static DateTime _next;
        private static string _signature;
        private static DateTime _nextFileCheck;
        private static DateTime _fileStamp;
        private static Exception _nativeRefreshError;
        internal static string Status { get; private set; } = UiLanguage.Get("PropertiesAppearanceService.CustomizationDisabled");

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
        internal static void ResetAll() { _saved = AppearanceStore.ResetAll(); _preview = null; Invalidate(); }
        internal static void CancelPreview() { _preview = null; Invalidate(); }
        // Parameters whose value must be read on every selected element (conditional looks).
        private static HashSet<string> ConditionKeys(AppearancePreferences preferences) =>
            new HashSet<string>(preferences.Profiles.SelectMany(p => p.Rules).Where(r => r.When != null && r.Kind == "parameter").Select(r => r.TargetKey));
        private static void Invalidate() { _signature = null; _next = DateTime.MinValue; _renderer?.Dispose(); _renderer = null; }
        internal static void Shutdown() { if(_observedApplication!=null){_observedApplication.DocumentChanged-=ModelChanged;_observedApplication=null;} Presentation.Dispose();_presentationEvent?.Dispose();_presentationEvent=null; PropertiesAppearanceCommand.Shutdown(); _preview = null; _renderer?.Dispose(); _renderer = null; }

        // Only native grid access and plain captured metadata are allowed here.
        // This also runs from WM_PAINT, outside Revit's document API context.
        private static int RefreshNativePlan(NativePropertiesGrid grid, AppearanceContext context,
            AppearancePreferences preferences, string version, bool dark, PropertiesPaletteRenderer renderer, bool redraw)
        {
            if(TextInputFocus.IsActive()||PropertiesPaletteLayout.IsApplying||!grid.RefreshIfCurrent()) return 0;
            bool hasColors = preferences.Enabled && preferences.Profiles.Any(p => p.Rules.Count > 0);
            bool hasOrder = preferences.Enabled && preferences.Profiles.Any(p => !p.Layout.IsEmpty);
            bool customLayout = false;
#if BIMAESTRO_PROPERTIES_TESTS
            customLayout = CustomLayoutOwnsGrid?.Invoke(grid) == true;
#endif
            if (!customLayout && (hasOrder || PropertiesPaletteLayout.HasApplied))
                PropertiesPaletteLayout.Apply(grid, context, hasOrder ? preferences : null, version,
                    () => renderer.Update(grid, new List<PropertiesPaletteRenderer.CellPlan>(), false));
            var cells = new List<PropertiesPaletteRenderer.CellPlan>();
            string group = null;
            int index = 0; // position of the editable row inside its group, for alternate rows
            if (hasColors)
                for (uint row = 1; row <= grid.RowCount; row++)
                {
                    string raw = grid.Value(row, 1); if (string.IsNullOrWhiteSpace(raw)) continue;
                    bool header = grid.IsGroupHeader(row);
#if BIMAESTRO_PROPERTIES_TESTS
                    var customColors = CustomRowColors?.Invoke(grid,row);
                    if(customColors!=null && (header || grid.CanStyle(row)))
                    { cells.Add(new PropertiesPaletteRenderer.CellPlan { Row=row,Label=raw,Header=header,Colors=customColors,Dark=dark });continue; }
#endif
                    if (header) {context.GroupMap.TryGetValue(AppearancePreferences.Normalize(raw), out group);group=PropertiesPaletteLayout.CustomGroupAt(grid,row)??group;index=0;}
                    if (group == null) continue;
                    PropertyTarget parameter = null;
                    if (!header) context.ParameterMap.TryGetValue(group + "|" + AppearancePreferences.Normalize(raw), out parameter);
                    if(!header)parameter=PropertiesPaletteLayout.ParameterAt(grid,context,row)??parameter;
                    if (!header && (parameter == null || parameter.ReadOnly)) continue;
                    var colors = preferences.Resolve(context.CategoryKey, context.TypeKey, group, parameter?.Key, dark, context.ValuesOf);
                    int position = header ? -1 : index++;
                    if (colors != null) cells.Add(new PropertiesPaletteRenderer.CellPlan { Row = row, Label = raw, Header = header, Colors = colors, Dark = dark, Index = position });
                }
            renderer.Update(grid, cells, redraw);
            return cells.Count;
        }

        internal static void ProcessIdling(UIApplication app)
        {
            if (TextInputFocus.IsActive()) return;
            long measured = 0;
            try
            {
                if (DateTime.UtcNow < _next) return;
                if(_observedApplication==null&&app!=null){_observedApplication=app.Application;_observedApplication.DocumentChanged+=ModelChanged;}
                if(_presentationEvent==null)_presentationEvent=ExternalEvent.Create(new PresentationHandler());
                _next = DateTime.UtcNow.AddMilliseconds(450);
                measured = PropertiesAppearanceMetrics.Begin();
                if (_nativeRefreshError != null)
                {
                    var error = _nativeRefreshError; _nativeRefreshError = null;
                    throw new InvalidOperationException("Actualisation native interrompue.", error);
                }
                if (_preview == null && DateTime.UtcNow >= _nextFileCheck)
                {
                    _nextFileCheck = DateTime.UtcNow.AddSeconds(2);
                    var stamp = System.IO.File.Exists(AppearanceStore.FilePath) ? System.IO.File.GetLastWriteTimeUtc(AppearanceStore.FilePath) : DateTime.MinValue;
                    if (stamp != _fileStamp) ReloadSaved();
                }
                var preferences = _preview ?? Saved;
                bool hasColors = preferences.Enabled && preferences.Profiles.Any(p => p.Rules.Count > 0);
                bool hasOrder = preferences.Enabled && preferences.Profiles.Any(p => !p.Layout.IsEmpty);
                if (!hasColors && !hasOrder && !PropertiesPaletteLayout.HasApplied)
                { if (_renderer != null) { _renderer.Dispose(); _renderer = null; } Status = UiLanguage.Get("PropertiesAppearanceService.OriginalRevitAppearance"); return; }
                if (app?.ActiveUIDocument == null) { _renderer?.Dispose(); _renderer = null; _cachedGrid = null; PropertiesPaletteLayout.Forget(); Status = UiLanguage.Get("PropertiesAppearanceService.OpenAProjectToDisplayYourProperties"); return; }
                var grid = _cachedGrid;
                long findTime = PropertiesAppearanceMetrics.Begin();
                try
                {
                    bool reuse = false;
                    try { reuse = grid != null && grid.RefreshIfCurrent(); } catch { }
                    if (!reuse) grid = NativePropertiesGrid.Find(app);
                    _cachedGrid = grid;
                }
                finally { PropertiesAppearanceMetrics.End("FindGrid", findTime); }
                if (grid == null) { _renderer?.Dispose(); _renderer = null; Status = UiLanguage.Get("PropertiesAppearanceService.DisplayThePropertiesPaletteToSeeYourColors"); return; }
                long contextTime = PropertiesAppearanceMetrics.Begin();
                AppearanceContext context;
                try { context = AppearanceContext.Capture(app, false, ConditionKeys(preferences)); }
                finally { PropertiesAppearanceMetrics.End("Context", contextTime); }
                if (context.CategoryKey == null)
                {
                    _renderer?.Dispose(); _renderer = null;
                    // A multiple/unsupported selection can reach the API before the
                    // native palette rebuild. Preserve the last verified baseline
                    // without moving this grid; the next valid capture replaces it.
                    Status = context.Caption;
                    return;
                }
                bool dark = IsDark;
                string version = app.Application.VersionNumber;
                string signature = context.Signature + "/" + dark;
                if (_renderer == null || _renderer.Window != grid.Window || _signature != signature)
                { _renderer?.Dispose(); _renderer = new PropertiesPaletteRenderer(grid); _signature = signature; }
                var renderer = _renderer;
                int cellCount = RefreshNativePlan(grid, context, preferences, version, dark, renderer, true);
                renderer.BeforePaint = () =>
                {
                    long refreshTime = PropertiesAppearanceMetrics.Begin();
                    try { RefreshNativePlan(grid, context, preferences, version, dark, renderer, false); }
                    catch (Exception ex)
                    {
                        renderer.BeforePaint = null;
                        renderer.Update(grid, new List<PropertiesPaletteRenderer.CellPlan>(), false);
                        _nativeRefreshError = ex; _next = DateTime.MinValue;
                    }
                    finally { PropertiesAppearanceMetrics.End("BeforePaint", refreshTime); }
                };
                Status = cellCount == 0 ? UiLanguage.Get("PropertiesAppearanceService.NoRuleAppliesToThisSelectionGrayedOut") : cellCount + UiLanguage.Get("PropertiesAppearanceService.CustomizedRowSGrayedOutFieldsAndInput");
                if (!hasColors) { Status = PropertiesPaletteLayout.Status ?? UiLanguage.Get("PropertiesAppearanceService.OriginalRevitAppearance"); return; }
                if (_renderer.Error != null) Status = _renderer.Error;
                else if (PropertiesPaletteLayout.Status != null) Status += " " + PropertiesPaletteLayout.Status;
            }
            catch (Exception ex)
            {
                var recoveryGrid=_cachedGrid;
                _renderer?.Dispose(); _renderer = null; _cachedGrid = null;
                if (PropertiesPaletteLayout.RequiresNativeRebuild && app?.ActiveUIDocument != null)
                {
                    bool idle=false;
                    try{idle=recoveryGrid!=null&&recoveryGrid.IsCurrent()&&!recoveryGrid.IsEditing;}catch{}
                    if(!idle){Status=UiLanguage.Get("PropertiesAppearanceService.RefreshPostponedToPreserveTheCurrentInputChange")+ex.Message;_next=DateTime.UtcNow.AddSeconds(5);return;}
                    // Idling is on Revit's UI thread: discard uncertain row indices
                    // before returning control to the user. Never write model values.
                    var selection = app.ActiveUIDocument.Selection;
                    var ids = selection.GetElementIds().ToArray();
                    selection.SetElementIds(new ElementId[0]);
                    selection.SetElementIds(ids);
                    PropertiesPaletteLayout.Forget();
                }
                Status = UiLanguage.Get("PropertiesAppearanceService.PaletteKeptInItsRevitState") + ex.Message;
                _next = DateTime.UtcNow.AddSeconds(5);
            }
            finally { Presentation.Release();PropertiesAppearanceMetrics.End("Refresh", measured); }
        }
    }
}

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Couleur
{
    public sealed class RevitRibbonTabDescriptor
    {
        public RevitRibbonTabDescriptor(string title, IEnumerable<string> panelTitles)
        {
            Title = title;
            PanelTitles = panelTitles
                .Where(panelTitle => !string.IsNullOrWhiteSpace(panelTitle))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }

        public string Title { get; }

        public IReadOnlyList<string> PanelTitles { get; }
    }

    public static class RevitRibbonCatalog
    {
        public static IReadOnlyList<RevitRibbonTabDescriptor> Discover()
        {
            object ribbon = GetRibbon();
            if (ribbon == null)
                return Array.Empty<RevitRibbonTabDescriptor>();

            var result = new List<RevitRibbonTabDescriptor>();
            foreach (object tab in GetEnumerableProperty(ribbon, "Tabs"))
            {
                string tabTitle = GetTextProperty(tab, "Title", "Text", "Name");
                if (string.IsNullOrWhiteSpace(tabTitle) ||
                    string.Equals(tabTitle, "BIMaestro", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var panels = new List<string>();
                foreach (object panel in GetEnumerableProperty(tab, "Panels"))
                {
                    object source = GetProperty(panel, "Source");
                    string panelTitle =
                        GetTextProperty(source, "Title", "Text", "Name") ??
                        GetTextProperty(panel, "Title", "Text", "Name");

                    if (!string.IsNullOrWhiteSpace(panelTitle))
                        panels.Add(panelTitle.Trim());
                }

                if (panels.Count > 0)
                    result.Add(new RevitRibbonTabDescriptor(tabTitle.Trim(), panels));
            }

            return result
                .GroupBy(tab => tab.Title, StringComparer.OrdinalIgnoreCase)
                .Select(group => new RevitRibbonTabDescriptor(
                    group.Key,
                    group.SelectMany(tab => tab.PanelTitles)))
                .OrderBy(tab => tab.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
                .AsReadOnly();
        }

        public static string GetActiveTabTitle()
        {
            object ribbon = GetRibbon();
            if (ribbon == null)
                return null;

            object activeTab = GetProperty(ribbon, "ActiveTab");
            if (activeTab == null)
            {
                activeTab = GetEnumerableProperty(ribbon, "Tabs")
                    .FirstOrDefault(tab =>
                        GetBooleanProperty(tab, "IsActive") ||
                        GetBooleanProperty(tab, "IsSelected"));
            }

            return GetTextProperty(activeTab, "Title", "Text", "Name");
        }

        internal static IReadOnlyList<object> GetPanelsForTab(
            string tabTitle)
        {
            if (string.IsNullOrWhiteSpace(tabTitle))
                return Array.Empty<object>();

            object ribbon = GetRibbon();
            if (ribbon == null)
                return Array.Empty<object>();

            object tab = GetEnumerableProperty(ribbon, "Tabs")
                .FirstOrDefault(candidate =>
                    string.Equals(
                        GetTextProperty(
                            candidate,
                            "Title",
                            "Text",
                            "Name"),
                        tabTitle,
                        StringComparison.OrdinalIgnoreCase));
            return tab == null
                ? Array.Empty<object>()
                : GetEnumerableProperty(tab, "Panels").ToList();
        }

        internal static string GetPanelTitle(object panel)
        {
            object source = GetProperty(panel, "Source");
            return GetTextProperty(source, "Title", "Text", "Name") ??
                   GetTextProperty(panel, "Title", "Text", "Name");
        }

        private static object GetRibbon()
        {
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type componentManager =
                        assembly.GetType("Autodesk.Windows.ComponentManager", false);
                    if (componentManager == null)
                        continue;

                    object ribbon = componentManager
                        .GetProperty(
                            "Ribbon",
                            BindingFlags.Public | BindingFlags.Static)
                        ?.GetValue(null);

                    if (ribbon != null)
                        return ribbon;
                }
            }
            catch
            {
                // Le catalogue visuel reste simplement indisponible.
            }

            return null;
        }

        private static IEnumerable<object> GetEnumerableProperty(
            object source,
            string propertyName)
        {
            object value = GetProperty(source, propertyName);
            if (!(value is IEnumerable enumerable))
                return Enumerable.Empty<object>();

            return enumerable.Cast<object>().Where(item => item != null);
        }

        private static object GetProperty(object source, string propertyName)
        {
            try
            {
                return source?.GetType()
                    .GetProperty(
                        propertyName,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    ?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private static string GetTextProperty(object source, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                string value = GetProperty(source, propertyName)?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static bool GetBooleanProperty(object source, string propertyName)
        {
            object value = GetProperty(source, propertyName);
            return value is bool boolean && boolean;
        }
    }

    public sealed class RevitRibbonPanelPreference
    {
        public RevitRibbonPanelPreference(
            string tabTitle,
            string panelTitle,
            bool isCustomized,
            bool isFullPanel,
            RibbonPanelColorScheme scheme)
        {
            TabTitle = tabTitle;
            PanelTitle = panelTitle;
            IsCustomized = isCustomized;
            IsFullPanel = isFullPanel;
            Scheme = scheme;
        }

        public string TabTitle { get; }

        public string PanelTitle { get; }

        public bool IsCustomized { get; }

        public bool IsFullPanel { get; }

        public RibbonPanelColorScheme Scheme { get; }
    }

    public static class RevitRibbonColorPreferences
    {
        private static readonly object SyncRoot =
            RibbonColorPreferences.PreferenceSyncRoot;
        private static NativeRibbonSettings _cachedSettings;

        private static string LegacyPreferenceFilePath { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                "SauvegardePréférence",
                "couleursRubanRevit.json");

        public static string PreferenceFilePath =>
            RibbonColorPreferences.PreferenceFilePath;

        public static bool IsGlobalColoringEnabled
        {
            get
            {
                lock (SyncRoot)
                    return LoadWithoutLock().IsEnabled;
            }
        }

        public static Dictionary<string, RevitRibbonPanelPreference>
            GetCustomizedPanelsForTab(string tabTitle)
        {
            lock (SyncRoot)
            {
                NativeRibbonSettings settings = LoadWithoutLock();
                if (!settings.IsEnabled || string.IsNullOrWhiteSpace(tabTitle))
                {
                    return new Dictionary<string, RevitRibbonPanelPreference>(
                        StringComparer.OrdinalIgnoreCase);
                }

                return settings.Panels.Values
                    .Where(preference =>
                        preference.IsCustomized &&
                        string.Equals(
                            preference.TabTitle,
                            tabTitle,
                            StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        preference => preference.PanelTitle,
                        preference => Clone(preference),
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        public static RevitRibbonPanelPreference GetPanelPreference(
            string tabTitle,
            string panelTitle)
        {
            NativeRibbonSettings settings = Load();
            return settings.Panels.TryGetValue(
                CreateKey(tabTitle, panelTitle),
                out RevitRibbonPanelPreference preference)
                ? Clone(preference)
                : new RevitRibbonPanelPreference(
                    tabTitle,
                    panelTitle,
                    false,
                    false,
                    CreateDefaultScheme());
        }

        public static void Save(
            bool isEnabled,
            IEnumerable<RevitRibbonPanelPreference> preferences)
        {
            if (preferences == null)
                throw new ArgumentNullException(nameof(preferences));

            lock (SyncRoot)
            {
                NativeRibbonSettings current = LoadWithoutLock();
                var merged = new Dictionary<string, RevitRibbonPanelPreference>(
                    current.Panels,
                    StringComparer.OrdinalIgnoreCase);

                foreach (RevitRibbonPanelPreference preference in preferences)
                {
                    if (preference == null ||
                        string.IsNullOrWhiteSpace(preference.TabTitle) ||
                        string.IsNullOrWhiteSpace(preference.PanelTitle))
                    {
                        continue;
                    }

                    merged[CreateKey(preference.TabTitle, preference.PanelTitle)] =
                        Clone(preference);
                }

                var serialized = new SavedNativeRibbonSettings
                {
                    Actif = isEnabled,
                    Panneaux = merged.Values
                        .OrderBy(item => item.TabTitle, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(item => item.PanelTitle, StringComparer.CurrentCultureIgnoreCase)
                        .Select(item => new SavedNativePanel
                        {
                            Onglet = item.TabTitle,
                            Panneau = item.PanelTitle,
                            Personnalise = item.IsCustomized,
                            PanneauComplet = item.IsFullPanel,
                            Fond = ToHex(item.Scheme.BackgroundColor),
                            Fin = ToHex(item.Scheme.BackgroundEndColor),
                            Texte = ToHex(item.Scheme.TextColor),
                            Degrade = item.Scheme.IsGradient,
                            Direction = item.Scheme.GradientDirection.ToString(),
                            Motif = item.Scheme.BackgroundPattern.ToString(),
                            DebutMotif = item.Scheme.PatternStart,
                            FinMotif = item.Scheme.PatternEnd
                        })
                        .ToList()
                };

                JObject root = RibbonColorPreferences.LoadPreferenceRoot();
                root["EncorePlus"] = JObject.FromObject(serialized);
                RibbonColorPreferences.SavePreferenceRoot(root);

                _cachedSettings = new NativeRibbonSettings(isEnabled, merged);
            }
        }

        private static NativeRibbonSettings Load()
        {
            lock (SyncRoot)
                return Clone(LoadWithoutLock());
        }

        private static NativeRibbonSettings LoadWithoutLock()
        {
            if (_cachedSettings != null)
                return _cachedSettings;

            var panels =
                new Dictionary<string, RevitRibbonPanelPreference>(
                    StringComparer.OrdinalIgnoreCase);
            bool isEnabled = false;

            try
            {
                SavedNativeRibbonSettings saved = null;
                JObject root = RibbonColorPreferences.LoadPreferenceRoot();
                if (root["EncorePlus"] != null)
                {
                    saved = root["EncorePlus"]
                        .ToObject<SavedNativeRibbonSettings>();
                }
                else if (File.Exists(LegacyPreferenceFilePath))
                {
                    saved =
                        JsonConvert.DeserializeObject<SavedNativeRibbonSettings>(
                            File.ReadAllText(
                                LegacyPreferenceFilePath,
                                Encoding.UTF8));
                }

                isEnabled = saved?.Actif ?? false;
                foreach (SavedNativePanel panel in
                         saved?.Panneaux ?? Enumerable.Empty<SavedNativePanel>())
                {
                    if (string.IsNullOrWhiteSpace(panel.Onglet) ||
                        string.IsNullOrWhiteSpace(panel.Panneau))
                    {
                        continue;
                    }

                    RibbonPanelColorScheme defaults = CreateDefaultScheme();
                    Color background =
                        TryParseColor(panel.Fond, out Color parsedBackground)
                            ? parsedBackground
                            : defaults.BackgroundColor;
                    Color end =
                        TryParseColor(panel.Fin, out Color parsedEnd)
                            ? parsedEnd
                            : background;
                    Color text =
                        TryParseColor(panel.Texte, out Color parsedText)
                            ? parsedText
                            : defaults.TextColor;

                    Enum.TryParse(
                        panel.Direction,
                        true,
                        out RibbonGradientDirection direction);
                    Enum.TryParse(
                        panel.Motif,
                        true,
                        out RibbonBackgroundPattern pattern);

                    var scheme = new RibbonPanelColorScheme(
                        background,
                        end,
                        text,
                        panel.Degrade,
                        direction,
                        pattern,
                        panel.DebutMotif,
                        panel.FinMotif <= panel.DebutMotif
                            ? 1
                            : panel.FinMotif);

                    var preference = new RevitRibbonPanelPreference(
                        panel.Onglet,
                        panel.Panneau,
                        panel.Personnalise ?? panel.Actif ?? false,
                        panel.PanneauComplet,
                        scheme);
                    panels[CreateKey(panel.Onglet, panel.Panneau)] = preference;
                }
            }
            catch
            {
                isEnabled = false;
                panels.Clear();
            }

            _cachedSettings = new NativeRibbonSettings(isEnabled, panels);
            return _cachedSettings;
        }

        private static string CreateKey(string tabTitle, string panelTitle)
        {
            return $"{tabTitle?.Trim()}\u001F{panelTitle?.Trim()}";
        }

        public static RibbonPanelColorScheme CreateDefaultScheme()
        {
            return new RibbonPanelColorScheme(
                Color.FromRgb(245, 247, 250),
                Color.FromRgb(245, 247, 250),
                Color.FromRgb(31, 41, 55));
        }

        private static NativeRibbonSettings Clone(NativeRibbonSettings source)
        {
            return new NativeRibbonSettings(
                source.IsEnabled,
                source.Panels.ToDictionary(
                    item => item.Key,
                    item => Clone(item.Value),
                    StringComparer.OrdinalIgnoreCase));
        }

        private static RevitRibbonPanelPreference Clone(
            RevitRibbonPanelPreference source)
        {
            return new RevitRibbonPanelPreference(
                source.TabTitle,
                source.PanelTitle,
                source.IsCustomized,
                source.IsFullPanel,
                Clone(source.Scheme));
        }

        private static RibbonPanelColorScheme Clone(RibbonPanelColorScheme source)
        {
            return new RibbonPanelColorScheme(
                source.BackgroundColor,
                source.BackgroundEndColor,
                source.TextColor,
                source.IsGradient,
                source.GradientDirection,
                source.BackgroundPattern,
                source.PatternStart,
                source.PatternEnd);
        }

        private static bool TryParseColor(string value, out Color color)
        {
            color = Colors.Transparent;
            try
            {
                object converted = ColorConverter.ConvertFromString(value);
                if (converted is Color parsed)
                {
                    color = parsed;
                    return true;
                }
            }
            catch
            {
                // Une couleur invalide est remplacée par la valeur par défaut.
            }

            return false;
        }

        private static string ToHex(Color color)
        {
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private sealed class NativeRibbonSettings
        {
            public NativeRibbonSettings(
                bool isEnabled,
                Dictionary<string, RevitRibbonPanelPreference> panels)
            {
                IsEnabled = isEnabled;
                Panels = panels;
            }

            public bool IsEnabled { get; }

            public Dictionary<string, RevitRibbonPanelPreference> Panels { get; }
        }

        private sealed class SavedNativeRibbonSettings
        {
            public bool Actif { get; set; }

            public List<SavedNativePanel> Panneaux { get; set; }
        }

        private sealed class SavedNativePanel
        {
            public string Onglet { get; set; }

            public string Panneau { get; set; }

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public bool? Actif { get; set; }

            public bool? Personnalise { get; set; }

            public bool PanneauComplet { get; set; }

            public string Fond { get; set; }

            public string Fin { get; set; }

            public string Texte { get; set; }

            public bool Degrade { get; set; }

            public string Direction { get; set; }

            public string Motif { get; set; }

            public double DebutMotif { get; set; }

            public double FinMotif { get; set; }
        }
    }

    public static class RevitRibbonGlobalColoring
    {
        private static readonly List<Border> ColoredBorders = new List<Border>();
        private static readonly List<TextBlock> ColoredTexts = new List<TextBlock>();
        private static readonly Dictionary<object, NativePanelBrushState>
            NativePanelBrushes =
                new Dictionary<object, NativePanelBrushState>();

        public static void Apply(IntPtr mainWindowHandle)
        {
            Reset();
            if (!ColoringStateManager.IsColoringActive ||
                !RevitRibbonColorPreferences.IsGlobalColoringEnabled)
            {
                return;
            }

            string activeTabTitle = RevitRibbonCatalog.GetActiveTabTitle();
            if (string.IsNullOrWhiteSpace(activeTabTitle) ||
                string.Equals(
                    activeTabTitle,
                    "BIMaestro",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dictionary<string, RevitRibbonPanelPreference> preferences =
                RevitRibbonColorPreferences.GetCustomizedPanelsForTab(
                    activeTabTitle);
            if (preferences.Count == 0)
                return;

            ApplyNativePanelBackgrounds(activeTabTitle, preferences);

            Window window = HwndSource
                .FromHwnd(mainWindowHandle)
                ?.RootVisual as Window;
            if (window == null)
                return;

            foreach (FrameworkElement titleBar in
                     FindVisualByTypeName(window, "PanelTitleBar"))
            {
                string panelTitle = GetTitle(titleBar);
                if (string.IsNullOrWhiteSpace(panelTitle) ||
                    !preferences.TryGetValue(
                        panelTitle,
                        out RevitRibbonPanelPreference preference))
                {
                    continue;
                }

                RibbonPanelColorScheme scheme = preference.Scheme;
                DependencyObject panelControl =
                    FindAncestorByTypeName(titleBar, "RibbonPanelControl");
                DependencyObject textTarget =
                    preference.IsFullPanel
                        ? panelControl ?? titleBar
                        : titleBar;
                List<Border> borders = GetPanelBackgroundBorders(
                    titleBar,
                    panelControl,
                    panelTitle,
                    preference.IsFullPanel);

                Brush background = scheme.CreateBackgroundBrush();
                foreach (Border border in borders.Distinct())
                {
                    border.Background = background;
                    border.BorderBrush = CreateBorderBrush(
                        background,
                        scheme.BackgroundColor);
                    border.BorderThickness = new Thickness(1);
                    ColoredBorders.Add(border);
                }

                foreach (TextBlock textBlock in
                         FindChildrenByType<TextBlock>(textTarget))
                {
                    textBlock.Foreground = new SolidColorBrush(scheme.TextColor);
                    ColoredTexts.Add(textBlock);
                }
            }
        }

        public static void Reset()
        {
            foreach (KeyValuePair<object, NativePanelBrushState> entry in
                     NativePanelBrushes.ToList())
            {
                if (entry.Value.Notifier != null &&
                    entry.Value.PropertyChangedHandler != null)
                {
                    entry.Value.Notifier.PropertyChanged -=
                        entry.Value.PropertyChangedHandler;
                }

                SetPropertyValue(
                    entry.Key,
                    "CustomPanelTitleBarBackground",
                    entry.Value.TitleBarBackground);
                SetPropertyValue(
                    entry.Key,
                    "CustomPanelBackground",
                    entry.Value.PanelBackground);
                SetPropertyValue(
                    entry.Key,
                    "CustomSlideOutPanelBackground",
                    entry.Value.SlideOutBackground);
            }

            foreach (Border border in ColoredBorders.Distinct().ToList())
            {
                border.ClearValue(Border.BackgroundProperty);
                border.ClearValue(Border.BorderBrushProperty);
                border.ClearValue(Border.BorderThicknessProperty);
            }

            foreach (TextBlock textBlock in ColoredTexts.Distinct().ToList())
                textBlock.ClearValue(TextBlock.ForegroundProperty);

            NativePanelBrushes.Clear();
            ColoredBorders.Clear();
            ColoredTexts.Clear();
        }

        private static void ApplyNativePanelBackgrounds(
            string activeTabTitle,
            Dictionary<string, RevitRibbonPanelPreference> preferences)
        {
            foreach (object panel in
                     RevitRibbonCatalog.GetPanelsForTab(activeTabTitle))
            {
                string panelTitle =
                    RevitRibbonCatalog.GetPanelTitle(panel);
                if (string.IsNullOrWhiteSpace(panelTitle) ||
                    !preferences.TryGetValue(
                        panelTitle,
                        out RevitRibbonPanelPreference preference))
                {
                    continue;
                }

                if (!NativePanelBrushes.TryGetValue(
                        panel,
                        out NativePanelBrushState originalState))
                {
                    originalState = new NativePanelBrushState(
                            GetPropertyValue(
                                panel,
                                "CustomPanelTitleBarBackground"),
                            GetPropertyValue(
                                panel,
                                "CustomPanelBackground"),
                            GetPropertyValue(
                                panel,
                                "CustomSlideOutPanelBackground"));
                    NativePanelBrushes[panel] = originalState;
                }

                Brush background =
                    preference.Scheme.CreateBackgroundBrush();
                ApplyNativePanelState(
                    panel,
                    preference,
                    background,
                    originalState);

                if (panel is INotifyPropertyChanged notifier &&
                    originalState.PropertyChangedHandler == null)
                {
                    PropertyChangedEventHandler handler = (_, args) =>
                    {
                        if (string.IsNullOrWhiteSpace(args.PropertyName) ||
                            string.Equals(
                                args.PropertyName,
                                "IsCollapsed",
                                StringComparison.Ordinal))
                        {
                            ApplyNativePanelState(
                                panel,
                                preference,
                                background,
                                originalState);
                        }
                    };
                    originalState.Notifier = notifier;
                    originalState.PropertyChangedHandler = handler;
                    notifier.PropertyChanged += handler;
                }
            }
        }

        private static void ApplyNativePanelState(
            object panel,
            RevitRibbonPanelPreference preference,
            Brush background,
            NativePanelBrushState originalState)
        {
            SetPropertyValue(
                panel,
                "CustomPanelTitleBarBackground",
                background);

            bool isCollapsed =
                GetPropertyValue(panel, "IsCollapsed") is bool collapsed &&
                collapsed;
            SetPropertyValue(
                panel,
                "CustomPanelBackground",
                preference.IsFullPanel || isCollapsed
                    ? background
                    : originalState.PanelBackground);
            SetPropertyValue(
                panel,
                "CustomSlideOutPanelBackground",
                preference.IsFullPanel
                    ? background
                    : originalState.SlideOutBackground);
        }

        private static object GetPropertyValue(
            object source,
            string propertyName)
        {
            try
            {
                return source?.GetType()
                    .GetProperty(
                        propertyName,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    ?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private static void SetPropertyValue(
            object source,
            string propertyName,
            object value)
        {
            try
            {
                PropertyInfo property = source?.GetType()
                    .GetProperty(
                        propertyName,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);
                if (property?.CanWrite == true)
                    property.SetValue(source, value);
            }
            catch
            {
                // Les propriétés Autodesk peuvent varier selon la version.
            }
        }

        private static List<Border> GetPanelBackgroundBorders(
            FrameworkElement titleBar,
            DependencyObject panelControl,
            string panelTitle,
            bool isFullPanel)
        {
            List<Border> titleBorders = FindChildrenByType<Border>(titleBar);
            if (titleBar is Border titleBorder)
                titleBorders.Insert(0, titleBorder);

            if (!isFullPanel || panelControl == null)
                return titleBorders.Take(1).ToList();

            List<Border> panelBorders = FindChildrenByType<Border>(panelControl)
                .Where(border =>
                {
                    string cookie = GetCookie(border);
                    return !string.IsNullOrWhiteSpace(cookie) &&
                           cookie.IndexOf(
                               panelTitle,
                               StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .ToList();

            return panelBorders.Count > 0
                ? panelBorders
                : titleBorders.Take(1).ToList();
        }

        private static string GetCookie(FrameworkElement element)
        {
            try
            {
                object dataContext = element?.DataContext;
                return dataContext?.GetType()
                    .GetProperty(
                        "Cookie",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    ?.GetValue(dataContext)
                    ?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string GetTitle(FrameworkElement titleBar)
        {
            try
            {
                return titleBar.GetType()
                    .GetProperty(
                        "Title",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    ?.GetValue(titleBar)
                    ?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static DependencyObject FindAncestorByTypeName(
            DependencyObject source,
            string typeName)
        {
            DependencyObject current = source;
            while (current != null)
            {
                if (current.GetType().Name.Equals(
                        typeName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static List<FrameworkElement> FindVisualByTypeName(
            DependencyObject parent,
            string typeName)
        {
            var result = new List<FrameworkElement>();
            if (parent == null)
                return result;

            for (int index = 0;
                 index < VisualTreeHelper.GetChildrenCount(parent);
                 index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement element &&
                    element.GetType().Name.Equals(
                        typeName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(element);
                }

                result.AddRange(FindVisualByTypeName(child, typeName));
            }

            return result;
        }

        private static List<T> FindChildrenByType<T>(DependencyObject parent)
            where T : DependencyObject
        {
            var result = new List<T>();
            if (parent == null)
                return result;

            for (int index = 0;
                 index < VisualTreeHelper.GetChildrenCount(parent);
                 index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T typedChild)
                    result.Add(typedChild);

                result.AddRange(FindChildrenByType<T>(child));
            }

            return result;
        }

        private sealed class NativePanelBrushState
        {
            public NativePanelBrushState(
                object titleBarBackground,
                object panelBackground,
                object slideOutBackground)
            {
                TitleBarBackground = titleBarBackground;
                PanelBackground = panelBackground;
                SlideOutBackground = slideOutBackground;
            }

            public object TitleBarBackground { get; }

            public object PanelBackground { get; }

            public object SlideOutBackground { get; }

            public INotifyPropertyChanged Notifier { get; set; }

            public PropertyChangedEventHandler PropertyChangedHandler
            {
                get;
                set;
            }
        }

        private static SolidColorBrush CreateBorderBrush(
            Brush background,
            Color fallback)
        {
            Color color = fallback;
            if (background is SolidColorBrush solid)
            {
                color = solid.Color;
            }
            else if (background is LinearGradientBrush gradient &&
                     gradient.GradientStops.Count > 0)
            {
                color = gradient.GradientStops
                    .OrderBy(stop => Math.Abs(stop.Offset - 0.5))
                    .First()
                    .Color;
            }

            return new SolidColorBrush(
                Color.FromArgb(
                    color.A,
                    (byte)(color.R * 0.72),
                    (byte)(color.G * 0.72),
                    (byte)(color.B * 0.72)));
        }
    }
}

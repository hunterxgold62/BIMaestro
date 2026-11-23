using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace BIMaestro.Localization
{
    public enum UiLanguageChoice
    {
        Automatic,
        French,
        English
    }

    public sealed class UiLanguageOption
    {
        public UiLanguageOption(UiLanguageChoice value, string label)
        {
            Value = value;
            Label = label;
        }

        public UiLanguageChoice Value { get; }
        public string Label { get; }
    }

    public static class UiLanguage
    {
        private const string SettingsFileName = "ui-language.json";
        private static bool _windowHookInstalled;
        private static bool _automaticIsEnglish = !string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "fr",
            StringComparison.OrdinalIgnoreCase);

        public static UiLanguageChoice Choice { get; private set; } = LoadChoice();

        public static bool IsEnglish
        {
            get
            {
                if (Choice == UiLanguageChoice.English) return true;
                if (Choice == UiLanguageChoice.French) return false;
                return _automaticIsEnglish;
            }
        }

        /// <summary>
        /// Code de la langue effectivement utilisée par l'interface BIMaestro.
        /// </summary>
        public static string CurrentLanguageCode => IsEnglish ? "en" : "fr";

        public static IReadOnlyList<UiLanguageOption> Options => new[]
        {
            new UiLanguageOption(UiLanguageChoice.Automatic, Get("UiLanguage.AutomaticRevitLanguage")),
            new UiLanguageOption(UiLanguageChoice.French, "Français"),
            new UiLanguageOption(UiLanguageChoice.English, "English")
        };

        public static void Initialize(string revitLanguage = null)
        {
            if (!string.IsNullOrWhiteSpace(revitLanguage))
                _automaticIsEnglish = !string.Equals(revitLanguage, "French", StringComparison.OrdinalIgnoreCase);
            if (_windowHookInstalled) return;
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded),
                handledEventsToo: true);
            EventManager.RegisterClassHandler(
                typeof(FrameworkElement),
                FrameworkElement.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(OnContextMenuOpening),
                handledEventsToo: true);
            _windowHookInstalled = true;
        }

        public static bool SetChoice(UiLanguageChoice choice)
        {
            UiLanguageChoice previousChoice = Choice;
            Choice = choice;
            if (SaveChoice(choice)) return true;
            Choice = previousChoice;
            return false;
        }

        public static string Get(string key)
        {
            return UiTextCatalog.Get(key, CurrentLanguageCode);
        }

        public static string Format(string key, params object[] arguments)
        {
            return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
        }

        public static string T(string french, string english)
        {
            return IsEnglish ? english : french;
        }

        public static string T(string text)
        {
            if (!IsEnglish || string.IsNullOrWhiteSpace(text)) return text;
            return UiTextCatalog.TranslateWpfText(text, CurrentLanguageCode);
        }

        public static void LocalizeWindow(Window window)
        {
            if (window == null || !IsEnglish || !IsBIMaestroWindow(window)) return;

            try
            {
                LocalizeProperty(window, Window.TitleProperty, window.Title);
            }
            catch (Exception ex)
            {
                LogLocalizationError(window, ex);
            }

            LocalizeElement(window);
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // This is a process-wide WPF class handler. Never inspect or modify a
            // Revit/third-party window, and never let localization escape the
            // Loaded event: an unhandled exception here terminates Revit.
            if (sender is not Window window || !IsBIMaestroWindow(window)) return;

            try
            {
                LocalizeWindow(window);
                window.Dispatcher.BeginInvoke(
                    new Action(() => LocalizeWindow(window)),
                    DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                LogLocalizationError(window, ex);
            }
        }

        private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!IsEnglish || !(sender is FrameworkElement element)) return;

            try
            {
                if (element.ContextMenu != null)
                    LocalizeElement(element.ContextMenu);
            }
            catch (Exception ex)
            {
                LogLocalizationError(element, ex);
            }
        }

        private static void LocalizeElement(DependencyObject element)
        {
            if (element == null) return;

            var pending = new Stack<DependencyObject>();
            var visited = new HashSet<DependencyObject>();
            pending.Push(element);

            while (pending.Count > 0)
            {
                DependencyObject current = pending.Pop();
                if (current == null || !visited.Add(current)) continue;

                TryLocalizeCurrentElement(current);

                int count;
                try
                {
                    count = VisualTreeHelper.GetChildrenCount(current);
                }
                catch (Exception ex)
                {
                    LogLocalizationError(current, ex);
                    count = 0;
                }

                for (int i = count - 1; i >= 0; i--)
                {
                    try
                    {
                        DependencyObject child = VisualTreeHelper.GetChild(current, i);
                        if (child != null) pending.Push(child);
                    }
                    catch (Exception ex)
                    {
                        LogLocalizationError(current, ex);
                    }
                }

                try
                {
                    foreach (object child in LogicalTreeHelper.GetChildren(current))
                    {
                        if (child is DependencyObject dependencyChild)
                            pending.Push(dependencyChild);
                    }
                }
                catch (Exception ex)
                {
                    LogLocalizationError(current, ex);
                }

                if (current is Popup popup && popup.Child != null)
                    pending.Push(popup.Child);
            }
        }

        private static void TryLocalizeCurrentElement(DependencyObject element)
        {
            try
            {
                if (element is TextBlock textBlock)
                {
                    if (textBlock.Inlines.Count == 0)
                        LocalizeProperty(textBlock, TextBlock.TextProperty, textBlock.Text);
                    else
                    {
                        // Snapshot the collection before changing Run.Text. Some
                        // custom controls invalidate their inline enumerator when
                        // a Run changes during Loaded.
                        var runs = new List<Run>();
                        foreach (Inline inline in textBlock.Inlines)
                            if (inline is Run run) runs.Add(run);

                        foreach (Run run in runs)
                            LocalizeProperty(run, Run.TextProperty, run.Text);
                    }
                }

                // ComboBox item text is often also used as a legacy business value.
                // Translate it explicitly when that window has been migrated to stable values.
                if (element is ContentControl contentControl &&
                    contentControl is not ComboBoxItem &&
                    contentControl.Content is string content)
                    LocalizeProperty(contentControl, ContentControl.ContentProperty, content);

                if (element is HeaderedContentControl headered && headered.Header is string header)
                    LocalizeProperty(headered, HeaderedContentControl.HeaderProperty, header);

                if (element is HeaderedItemsControl headeredItems && headeredItems.Header is string itemHeader)
                    LocalizeProperty(headeredItems, HeaderedItemsControl.HeaderProperty, itemHeader);

                if (element is DataGrid dataGrid)
                {
                    foreach (DataGridColumn column in dataGrid.Columns)
                    {
                        if (column.Header is string columnHeader)
                            LocalizeProperty(column, DataGridColumn.HeaderProperty, columnHeader);
                    }
                }

                if (element is FrameworkElement frameworkElement && frameworkElement.ToolTip is string toolTip)
                    LocalizeProperty(frameworkElement, FrameworkElement.ToolTipProperty, toolTip);
            }
            catch (Exception ex)
            {
                // A single custom control must not prevent the rest of the
                // window from being translated or bring down the Revit process.
                LogLocalizationError(element, ex);
            }
        }

        private static void LocalizeProperty(DependencyObject target, DependencyProperty property, string text)
        {
            string translation = T(text);
            if (!string.Equals(text, translation, StringComparison.Ordinal))
                target.SetCurrentValue(property, translation);
        }

        private static bool IsBIMaestroWindow(Window window)
        {
            return window?.GetType().Assembly == typeof(UiLanguage).Assembly;
        }

        private static void LogLocalizationError(object source, Exception ex)
        {
            string sourceType = source?.GetType().FullName ?? "unknown";
            Debug.WriteLine($"[UiLanguage] Localization skipped for '{sourceType}': {ex}");
        }

        private static UiLanguageChoice LoadChoice()
        {
            try
            {
                string path = GetSettingsPath();
                if (!File.Exists(path)) return UiLanguageChoice.Automatic;
                var settings = JsonConvert.DeserializeObject<LanguageSettings>(File.ReadAllText(path));
                return settings?.Language ?? UiLanguageChoice.Automatic;
            }
            catch
            {
                return UiLanguageChoice.Automatic;
            }
        }

        private static bool SaveChoice(UiLanguageChoice choice)
        {
            try
            {
                string path = GetSettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(
                    new LanguageSettings { Language = choice }, Formatting.Indented));
                return true;
            }
            catch
            {
                // A language preference must never prevent BIMaestro from starting.
                return false;
            }
        }

        private static string GetSettingsPath()
        {
            string directory = global::BIMaestro.Storage.PreferenceStorage.Folder("Option");
            return Path.Combine(directory, SettingsFileName);
        }

        private sealed class LanguageSettings
        {
            public UiLanguageChoice Language { get; set; }
        }
    }

    internal static class UiTextCatalog
    {
        private const string WpfKeyPrefix = "Wpf.";
        private static readonly Lazy<Dictionary<string, string>> French = new Lazy<Dictionary<string, string>>(() => Load("fr"));
        private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> Catalogs =
            new ConcurrentDictionary<string, Lazy<Dictionary<string, string>>>(StringComparer.Ordinal);
        private static readonly Lazy<Dictionary<string, string>> WpfKeys = new Lazy<Dictionary<string, string>>(IndexWpfKeys);

        internal static string Get(string key, string language)
        {
            if (language != "fr")
            {
                var catalog = Catalogs.GetOrAdd(language, code => new Lazy<Dictionary<string, string>>(() => Load(code))).Value;
                if (catalog.TryGetValue(key, out var translation))
                    return translation;
            }

            return French.Value.TryGetValue(key, out var french) ? french : key;
        }

        internal static string TranslateWpfText(string text, string language)
        {
            return WpfKeys.Value.TryGetValue(text, out var key) ? Get(key, language) : text;
        }

        private static Dictionary<string, string> IndexWpfKeys()
        {
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in French.Value)
            {
                if (entry.Key.StartsWith(WpfKeyPrefix, StringComparison.Ordinal) && !keys.ContainsKey(entry.Value))
                    keys.Add(entry.Value, entry.Key);
            }
            return keys;
        }

        private static Dictionary<string, string> Load(string language)
        {
            string resource = "BIMaestro.Localization.Strings." + language + ".json";
            using (var stream = typeof(UiTextCatalog).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null)
                    return new Dictionary<string, string>(StringComparer.Ordinal);

                using (var reader = new StreamReader(stream))
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
            }
        }
    }
}

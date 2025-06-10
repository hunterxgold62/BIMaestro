using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using Color = System.Windows.Media.Color;

namespace MyRevitPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleCombinedColoringCommand : IExternalCommand
    {
        private const int DoubleClickThresholdMs = 300;
        private static bool _waitingForDoubleClick = false;
        private static Timer _singleClickTimer = null;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ColoringStateManager.LoadState();

                if (!_waitingForDoubleClick)
                {
                    _waitingForDoubleClick = true;
                    _singleClickTimer = new Timer(SingleClickAction, commandData, DoubleClickThresholdMs, Timeout.Infinite);
                }
                else
                {
                    _waitingForDoubleClick = false;
                    _singleClickTimer?.Dispose();
                    _singleClickTimer = null;
                    DoDoubleClick(commandData);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private void SingleClickAction(object state)
        {
            _waitingForDoubleClick = false;
            if (state is ExternalCommandData cdata)
                DoSingleClick(cdata);
        }

        private void DoSingleClick(ExternalCommandData commandData)
        {
            try
            {
                ColoringStateManager.ToggleColoring();
                IntPtr h = commandData.Application.MainWindowHandle;
                CombinedColoringApplication.ResetColorings(h);
                PartialColoringHelper.ResetPartialColoring(h);

                if (ColoringStateManager.IsColoringActive)
                {
                    CombinedColoringApplication.ApplyTabItemColoring(h);
                    if (ColoringStateManager.IsFullMode)
                        CombinedColoringApplication.ApplyPapanoelColoring(h);
                    else
                        PartialColoringHelper.ApplyPartialColoring(h);
                }
            }
            catch { }
        }

        private void DoDoubleClick(ExternalCommandData commandData)
        {
            try
            {
                IntPtr h = commandData.Application.MainWindowHandle;
                ColoringStateManager.SwitchMode();
                CombinedColoringApplication.ResetColorings(h);
                PartialColoringHelper.ResetPartialColoring(h);

                if (ColoringStateManager.IsColoringActive)
                {
                    CombinedColoringApplication.ApplyTabItemColoring(h);
                    if (ColoringStateManager.IsFullMode)
                        CombinedColoringApplication.ApplyPapanoelColoring(h);
                    else
                        PartialColoringHelper.ApplyPartialColoring(h);
                }
            }
            catch { }
        }
    }

    // -----------------------------------------------------------------------
    //   1) ColoringStateManager : on/off + full/partial
    // -----------------------------------------------------------------------
    public static class ColoringStateManager
    {
        private static readonly string persistenceFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "RevitLogs", "SauvegardePréférence", "resetColor.txt");

        public static bool IsColoringActive { get; private set; }
        public static bool IsFullMode { get; private set; }

        public static void LoadState()
        {
            try
            {
                EnsureDirectoryExists();
                if (File.Exists(persistenceFilePath))
                {
                    var parts = File.ReadAllText(persistenceFilePath).Trim().Split('-');
                    if (parts.Length == 2)
                    {
                        IsColoringActive = parts[0].Equals("Active", StringComparison.OrdinalIgnoreCase);
                        IsFullMode = parts[1].Equals("Full", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        IsColoringActive = true;
                        IsFullMode = true;
                        SaveState();
                    }
                }
                else
                {
                    IsColoringActive = true;
                    IsFullMode = true;
                    SaveState();
                }
            }
            catch
            {
                TaskDialog.Show("Erreur de Chargement", "Impossible de charger l'état, valeurs par défaut appliquées.");
                IsColoringActive = true;
                IsFullMode = true;
            }
        }

        public static void SaveState()
        {
            try
            {
                EnsureDirectoryExists();
                string a = IsColoringActive ? "Active" : "Inactive";
                string m = IsFullMode ? "Full" : "Partial";
                File.WriteAllText(persistenceFilePath, $"{a}-{m}");
            }
            catch
            {
                TaskDialog.Show("Erreur de Sauvegarde", "Impossible de sauvegarder l'état.");
            }
        }

        public static void ToggleColoring()
        {
            IsColoringActive = !IsColoringActive;
            SaveState();
        }

        public static void SwitchMode()
        {
            IsFullMode = !IsFullMode;
            SaveState();
        }

        private static void EnsureDirectoryExists()
        {
            var dir = Path.GetDirectoryName(persistenceFilePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }

    // -----------------------------------------------------------------------
    //   2) Coloration Complète : TabItems + BIMaestro (watcher) + "Papanoel"
    // -----------------------------------------------------------------------
    public static class CombinedColoringApplication
    {
        private static Dictionary<string, SolidColorBrush> _projectTabColors = new Dictionary<string, SolidColorBrush>();
        private static readonly Random _random = new Random();

        // Pour BIMaestro
        private static FrameworkElement _bimButton;
        private static List<Border> _bimBorders;
        private static DispatcherTimer _bimWatcher;

        private static readonly SolidColorBrush _pastelBrush = new SolidColorBrush(Color.FromRgb(242, 255, 242));
        private static readonly SolidColorBrush _whiteBrush = Brushes.White;
        private static readonly SolidColorBrush _hoverBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));

        private static readonly Dictionary<string, SolidColorBrush> predefinedKeywordColors =
            new Dictionary<string, SolidColorBrush>
            {
                { "Outils de Visualisation",   new SolidColorBrush(Color.FromRgb(255, 230, 230)) },
                { "Modification",             new SolidColorBrush(Color.FromRgb(230, 255, 230)) },
                { "Outils IA",                new SolidColorBrush(Color.FromRgb(230, 230, 255)) },
                { "Couleur du projet",        new SolidColorBrush(Color.FromRgb(230, 230, 230)) },
                { "Panneaux réservés au test",new SolidColorBrush(Color.FromRgb(255, 255, 230)) },
                { "Analyse",                  new SolidColorBrush(Color.FromRgb(230, 255, 255)) },
                { "Spécifique aux familles",  new SolidColorBrush(Color.FromRgb(255, 230, 255)) }
            };

        private static readonly List<string> _targetKeywords = new List<string>
        {
            "Outils de Visualisation", "Modification", "Outils IA",
            "Couleur du projet", "Panneaux réservés au test",
            "Spécifique aux familles", "Analyse"
        };

        public static void ResetRandomColors() => _projectTabColors.Clear();

        public static void ApplyColorings(IntPtr mainWindowHandle)
        {
            if (!ColoringStateManager.IsColoringActive) return;
            ApplyTabItemColoring(mainWindowHandle);
            ApplyPapanoelColoring(mainWindowHandle);
        }

        public static void ResetColorings(IntPtr mainWindowHandle)
        {
            ResetTabItemColoring(mainWindowHandle);
            ResetPapanoelColoring(mainWindowHandle);
            ResetBIMaestroTab();  // stop watcher + clear
        }

        public static void ApplyTabItemColoring(IntPtr mainWindowHandle)
        {
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;

            // 1) projets flashy
            foreach (var tab in FindChildrenByType<TabItem>(wnd))
            {
                var tip = tab.ToolTip as string;
                if (string.IsNullOrEmpty(tip)) continue;
                var proj = ExtractProjectName(tip);
                if (string.IsNullOrEmpty(proj)) continue;

                var brush = GetFlashyProjectColor(proj, out var borderBrush);
                tab.Background = brush;
                tab.BorderBrush = borderBrush;
                ColorTextBlocks(tab, Brushes.Black);
            }

            // 2) BIMaestro : repérage + démarrage du watcher
            if (_bimWatcher == null)
            {
                var buttons = FindVisualByTypeName(wnd, "RibbonTabButton");
                _bimButton = buttons.FirstOrDefault(b => AutomationProperties.GetName(b) == "BIMaestro");
                if (_bimButton != null)
                {
                    _bimBorders = FindChildrenByType<Border>(_bimButton);
                    // d’emblée, fond pastel
                    foreach (var b in _bimBorders)
                        b.Background = _pastelBrush;

                    // timer WPF sur le même thread UI
                    _bimWatcher = new DispatcherTimer(DispatcherPriority.Render)
                    {
                        Interval = TimeSpan.FromMilliseconds(100)
                    };
                    _bimWatcher.Tick += (_, __) => UpdateBIMaestroBackground();
                    _bimWatcher.Start();
                }
            }
        }

        private static void UpdateBIMaestroBackground()
        {
            if (_bimButton == null || !ColoringStateManager.IsColoringActive) return;

            bool isSelected = false;
            // Revit RibbonTabButton a souvent une propriété IsSelected ou IsChecked
            var pi = _bimButton.GetType().GetProperty("IsSelected")
                  ?? _bimButton.GetType().GetProperty("IsChecked");
            if (pi != null && (bool?)pi.GetValue(_bimButton) == true)
                isSelected = true;

            Brush target;
            if (isSelected)
                target = _whiteBrush;
            else if (_bimButton.IsMouseOver)
                target = _hoverBrush;
            else
                target = _pastelBrush;

            foreach (var b in _bimBorders)
                b.Background = target;
        }

        private static void ResetTabItemColoring(IntPtr mainWindowHandle)
        {
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;
            foreach (var t in FindChildrenByType<TabItem>(wnd))
            {
                t.ClearValue(TabItem.BackgroundProperty);
                t.ClearValue(TabItem.BorderBrushProperty);
                ClearTextBlocks(t);
            }
        }

        private static void ResetBIMaestroTab()
        {
            if (_bimWatcher != null)
            {
                _bimWatcher.Stop();
                _bimWatcher = null;
            }
            if (_bimBorders != null)
            {
                foreach (var b in _bimBorders)
                    b.ClearValue(Border.BackgroundProperty);
            }
            _bimButton = null;
            _bimBorders = null;
        }

        public static void ApplyPapanoelColoring(IntPtr mainWindowHandle)
        {
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;
            foreach (var border in FindChildrenByType<Border>(wnd))
            {
                var dc = border.DataContext;
                if (dc == null) continue;
                var prop = dc.GetType().GetProperty("Cookie");
                var val = prop?.GetValue(dc)?.ToString();
                if (string.IsNullOrEmpty(val)) continue;

                foreach (var kw in _targetKeywords)
                {
                    if (val.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        predefinedKeywordColors.TryGetValue(kw, out var brush))
                    {
                        border.Background = brush;
                        border.BorderBrush = DarkenColor(brush.Color, 0.7);
                        border.BorderThickness = new Thickness(1);
                        ColorTextBlocks(border, Brushes.Black);
                        break;
                    }
                }
            }
        }

        private static void ResetPapanoelColoring(IntPtr mainWindowHandle)
        {
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;
            foreach (var b in FindChildrenByType<Border>(wnd))
            {
                b.ClearValue(Border.BackgroundProperty);
                b.ClearValue(Border.BorderBrushProperty);
                b.ClearValue(Border.BorderThicknessProperty);
                ClearTextBlocks(b);
            }
        }

        // —— utilitaires ——
        private static Window GetMainWindow(IntPtr handle)
        {
            var src = HwndSource.FromHwnd(handle);
            return src?.RootVisual as Window;
        }

        private static SolidColorBrush GetFlashyProjectColor(string name, out SolidColorBrush borderBrush)
        {
            if (_projectTabColors.TryGetValue(name, out var existing))
            {
                borderBrush = DarkenColor(existing.Color, 0.7);
                return existing;
            }
            var c = Color.FromRgb((byte)_random.Next(180, 256),
                                      (byte)_random.Next(180, 256),
                                      (byte)_random.Next(180, 256));
            var brush = new SolidColorBrush(c);
            _projectTabColors[name] = brush;
            borderBrush = DarkenColor(c, 0.7);
            return brush;
        }

        private static string ExtractProjectName(string tt)
        {
            var idx = tt.IndexOf(" - ");
            return idx > 0 ? tt.Substring(0, idx).Trim() : tt;
        }

        private static List<T> FindChildrenByType<T>(DependencyObject parent) where T : DependencyObject
        {
            var list = new List<T>();
            if (parent == null) return list;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var c = VisualTreeHelper.GetChild(parent, i);
                if (c is T t) list.Add(t);
                list.AddRange(FindChildrenByType<T>(c));
            }
            return list;
        }

        private static List<FrameworkElement> FindVisualByTypeName(DependencyObject parent, string typeName)
        {
            var res = new List<FrameworkElement>();
            if (parent == null) return res;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var c = VisualTreeHelper.GetChild(parent, i);
                if (c is FrameworkElement fe && fe.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    res.Add(fe);
                res.AddRange(FindVisualByTypeName(c, typeName));
            }
            return res;
        }

        private static void ColorTextBlocks(DependencyObject parent, Brush color)
        {
            foreach (var tb in FindChildrenByType<TextBlock>(parent))
                tb.Foreground = color;
        }

        private static void ClearTextBlocks(DependencyObject parent)
        {
            foreach (var tb in FindChildrenByType<TextBlock>(parent))
                tb.ClearValue(TextBlock.ForegroundProperty);
        }

        private static SolidColorBrush DarkenColor(Color c, double f) =>
            new SolidColorBrush(Color.FromRgb(
                (byte)(c.R * f),
                (byte)(c.G * f),
                (byte)(c.B * f)));
    }

    // -----------------------------------------------------------------------
    //   3) Coloration Partielle : DockablePane “PanelTitleBar”
    // -----------------------------------------------------------------------
    public static class PartialColoringHelper
    {
        private static readonly Dictionary<string, SolidColorBrush> partialTitles =
            new Dictionary<string, SolidColorBrush>
            {
                { "Outils de Visualisation",   new SolidColorBrush(Color.FromRgb(255, 230, 230)) },
                { "Modification",             new SolidColorBrush(Color.FromRgb(230, 255, 230)) },
                { "Outils IA",                new SolidColorBrush(Color.FromRgb(230, 230, 255)) },
                { "Analyse",                  new SolidColorBrush(Color.FromRgb(230, 255, 255)) },
                { "Spécifique aux familles",  new SolidColorBrush(Color.FromRgb(255, 230, 255)) },
                { "Panneaux réservés au test",new SolidColorBrush(Color.FromRgb(255, 255, 230)) },
                { "Couleur du projet",        new SolidColorBrush(Color.FromRgb(230, 230, 230)) }
            };

        public static void ApplyPartialColoring(IntPtr mainWindowHandle)
        {
            if (!ColoringStateManager.IsColoringActive || ColoringStateManager.IsFullMode) return;
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;

            var panels = FindVisualByTypeName(wnd, "PanelTitleBar");
            foreach (var ptb in panels)
            {
                var prop = ptb.GetType().GetProperty("Title");
                if (prop == null) continue;
                var title = prop.GetValue(ptb)?.ToString();
                if (title != null && partialTitles.TryGetValue(title, out var brush))
                {
                    var b = FindChildrenByType<Border>(ptb).FirstOrDefault() as Border ?? (ptb as Border);
                    if (b != null)
                    {
                        b.Background = brush;
                        b.BorderBrush = DarkenColor(brush.Color, 0.7);
                        b.BorderThickness = new Thickness(1);
                        ColorTextBlocks(b, Brushes.Black);
                    }
                }
            }
        }

        public static void ResetPartialColoring(IntPtr mainWindowHandle)
        {
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;
            var panels = FindVisualByTypeName(wnd, "PanelTitleBar");
            foreach (var ptb in panels)
            {
                var prop = ptb.GetType().GetProperty("Title");
                if (prop == null) continue;
                var title = prop.GetValue(ptb)?.ToString();
                if (title != null && partialTitles.ContainsKey(title))
                {
                    var b = FindChildrenByType<Border>(ptb).FirstOrDefault() as Border ?? (ptb as Border);
                    if (b != null)
                    {
                        b.ClearValue(Border.BackgroundProperty);
                        b.ClearValue(Border.BorderBrushProperty);
                        b.ClearValue(Border.BorderThicknessProperty);
                        ClearTextBlocks(b);
                    }
                }
            }
        }

        // utilitaires...
        private static Window GetMainWindow(IntPtr handle)
        {
            var src = HwndSource.FromHwnd(handle);
            return src?.RootVisual as Window;
        }

        private static List<FrameworkElement> FindVisualByTypeName(DependencyObject parent, string typeName)
        {
            var res = new List<FrameworkElement>();
            if (parent == null) return res;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var c = VisualTreeHelper.GetChild(parent, i);
                if (c is FrameworkElement fe && fe.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    res.Add(fe);
                res.AddRange(FindVisualByTypeName(c, typeName));
            }
            return res;
        }

        private static List<T> FindChildrenByType<T>(DependencyObject p) where T : DependencyObject
        {
            var list = new List<T>();
            if (p == null) return list;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(p); i++)
            {
                var c = VisualTreeHelper.GetChild(p, i);
                if (c is T t) list.Add(t);
                list.AddRange(FindChildrenByType<T>(c));
            }
            return list;
        }

        private static void ColorTextBlocks(DependencyObject p, Brush color)
        {
            foreach (var tb in FindChildrenByType<TextBlock>(p))
                tb.Foreground = color;
        }

        private static void ClearTextBlocks(DependencyObject p)
        {
            foreach (var tb in FindChildrenByType<TextBlock>(p))
                tb.ClearValue(TextBlock.ForegroundProperty);
        }

        private static SolidColorBrush DarkenColor(Color c, double f) =>
            new SolidColorBrush(Color.FromRgb(
                (byte)(c.R * f),
                (byte)(c.G * f),
                (byte)(c.B * f)));
    }
}

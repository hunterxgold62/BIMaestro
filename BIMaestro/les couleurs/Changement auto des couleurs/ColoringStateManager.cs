using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace Couleur
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleCombinedColoringCommand : BaseTrackedCommand
    {
        private const int DoubleClickThresholdMs = 300;
        private static bool _waitingForDoubleClick = false;
        private static Timer _singleClickTimer = null;
        protected override string ButtonId => "ToggleCombinedColoringCommand";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
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
        private const bool DefaultColoringActive = true;
        private const bool DefaultFullMode = false;

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
                        IsColoringActive = DefaultColoringActive;
                        IsFullMode = DefaultFullMode;
                        SaveState();
                    }
                }
                else
                {
                    IsColoringActive = DefaultColoringActive;
                    IsFullMode = DefaultFullMode;
                    SaveState();
                }
            }
            catch
            {
                TaskDialog.Show("Erreur de Chargement", "Impossible de charger l'état, valeurs par défaut appliquées.");
                IsColoringActive = DefaultColoringActive;
                IsFullMode = DefaultFullMode;
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

        public static void ResetRandomColors() => _projectTabColors.Clear();

        public static void ApplyColorings(IntPtr mainWindowHandle)
        {
            if (!ColoringStateManager.IsColoringActive) return;
            ApplyTabItemColoring(mainWindowHandle);
            ApplyPapanoelColoring(mainWindowHandle);
        }

        public static void ResetColorings(IntPtr mainWindowHandle)
        {
            RevitRibbonGlobalColoring.Reset();
            ProjectBrowserColoring.Reset();
            ResetTabItemColoring(mainWindowHandle);
            ResetPapanoelColoring(mainWindowHandle);
            ResetBIMaestroTab();  // stop watcher + clear
        }

        public static void ApplyTabItemColoring(IntPtr mainWindowHandle)
        {
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;

            // Si le watcher actuel n'est plus lié à la fenêtre active,
            // on le réinitialise pour retrouver le bouton BIMaestro.
            if (_bimWatcher != null && (_bimButton == null || Window.GetWindow(_bimButton) != wnd))
            {
                ResetBIMaestroTab();
            }
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
                    ColorTextBlocks(_bimButton, Brushes.Black);


                    // timer WPF sur le même thread UI
                    _bimWatcher = new DispatcherTimer(DispatcherPriority.Render)
                    {
                        Interval = TimeSpan.FromMilliseconds(100)
                    };
                    _bimWatcher.Tick += (_, __) => UpdateBIMaestroBackground();
                    _bimWatcher.Start();
                }
            }

            ProjectBrowserColoring.Apply(mainWindowHandle);
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
            ColorTextBlocks(_bimButton, Brushes.Black);

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
            if (_bimButton != null)
                ClearTextBlocks(_bimButton);
            _bimButton = null;
            _bimBorders = null;
        }

        public static void ApplyPapanoelColoring(IntPtr mainWindowHandle)
        {
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;
            Dictionary<string, RibbonPanelColorScheme> panelColors =
                RibbonColorPreferences.CreateSchemes();

            var targets = new List<(Border Border, RibbonPanelColorScheme Scheme)>();
            foreach (var border in FindChildrenByType<Border>(wnd))
            {
                var dc = border.DataContext;
                if (dc == null) continue;
                var prop = dc.GetType().GetProperty("Cookie");
                var val = prop?.GetValue(dc)?.ToString();
                if (string.IsNullOrEmpty(val)) continue;

                foreach (var panelColor in panelColors)
                {
                    if (val.IndexOf(panelColor.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targets.Add((border, panelColor.Value));
                        break;
                    }
                }
            }

            double ribbonLeft = double.MaxValue;
            double ribbonRight = double.MinValue;
            foreach (var target in targets)
            {
                if (!target.Scheme.IsContinuousAcrossRibbon)
                    continue;

                if (TryGetHorizontalRange(target.Border, wnd, out double left, out double right))
                {
                    ribbonLeft = Math.Min(ribbonLeft, left);
                    ribbonRight = Math.Max(ribbonRight, right);
                }
            }

            bool hasContinuousRange =
                ribbonLeft < ribbonRight && ribbonLeft != double.MaxValue;

            foreach (var target in targets)
            {
                Brush background = target.Scheme.CreateBackgroundBrush();
                if (hasContinuousRange &&
                    target.Scheme.IsContinuousAcrossRibbon &&
                    TryGetHorizontalRange(target.Border, wnd, out double left, out double right))
                {
                    double totalWidth = ribbonRight - ribbonLeft;
                    background = target.Scheme.CreateBackgroundBrush(
                        left - ribbonLeft,
                        right - ribbonLeft,
                        totalWidth);
                }

                target.Border.Background = background;
                target.Border.BorderBrush =
                    DarkenBackgroundBrush(background, target.Scheme.BackgroundColor);
                target.Border.BorderThickness = new Thickness(1);
                if (hasContinuousRange &&
                    target.Scheme.BackgroundPattern ==
                    RibbonBackgroundPattern.FrenchFlagContinuous)
                {
                    ColorContinuousFlagTextBlocks(
                        target.Border,
                        wnd,
                        ribbonLeft,
                        ribbonRight);
                }
                else
                {
                    ColorTextBlocks(
                        target.Border,
                        new SolidColorBrush(target.Scheme.TextColor));
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
            var parts = tt.Split(new[] { " - " }, StringSplitOptions.None);

            // Revit 2025 fournit un second segment correspondant à la version de maquette
            // (ex. "V3", "V4"), ce qui permet de distinguer des fichiers dupliqués.
            // En 2023/2024, ce second segment correspond souvent au nom de vue, provoquant
            // une couleur différente par onglet. On n'inclut donc le deuxième segment que
            // lorsqu'il ressemble clairement à un identifiant de version.
            if (parts.Length >= 2)
            {
                var second = parts[1].Trim();
                if (LooksLikeVersionSegment(second))
                {
                    return string.Join(" - ", parts.Take(2)).Trim();
                }

                return parts[0].Trim();
            }

            return tt.Trim();
        }

        private static bool LooksLikeVersionSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment)) return false;

           
            if (segment.IndexOf(' ') >= 0) return false;

            return Regex.IsMatch(segment, @"V\d+", RegexOptions.IgnoreCase);
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

        private static bool TryGetHorizontalRange(
            FrameworkElement element,
            Visual ancestor,
            out double left,
            out double right)
        {
            left = 0;
            right = 0;

            try
            {
                if (element == null || ancestor == null || element.ActualWidth <= 0)
                    return false;

                var transform = element.TransformToAncestor(ancestor);
                left = transform.Transform(new System.Windows.Point(0, 0)).X;
                right = left + element.ActualWidth;
                return right > left;
            }
            catch
            {
                return false;
            }
        }

        private static void ColorTextBlocks(DependencyObject parent, Brush color)
        {
            foreach (var tb in FindChildrenByType<TextBlock>(parent))
                tb.Foreground = color;
        }

        private static void ColorContinuousFlagTextBlocks(
            DependencyObject parent,
            Visual ancestor,
            double ribbonLeft,
            double ribbonRight)
        {
            double totalWidth = ribbonRight - ribbonLeft;
            if (totalWidth <= 0)
                return;

            foreach (var textBlock in FindChildrenByType<TextBlock>(parent))
            {
                if (!TryGetHorizontalRange(textBlock, ancestor, out double left, out double right))
                    continue;

                double center = ((left + right) / 2 - ribbonLeft) / totalWidth;
                textBlock.Foreground =
                    center < 1.0 / 3.0 || center >= 2.0 / 3.0
                        ? Brushes.White
                        : Brushes.Black;
            }
        }

        private static void ClearTextBlocks(DependencyObject parent)
        {
            foreach (var tb in FindChildrenByType<TextBlock>(parent))
                tb.ClearValue(TextBlock.ForegroundProperty);
        }

        private static SolidColorBrush DarkenColor(Color c, double f) =>
            new SolidColorBrush(Color.FromArgb(
                c.A,
                (byte)(c.R * f),
                (byte)(c.G * f),
                (byte)(c.B * f)));

        private static SolidColorBrush DarkenBackgroundBrush(Brush brush, Color fallback)
        {
            Color color = fallback;
            if (brush is SolidColorBrush solid)
            {
                color = solid.Color;
            }
            else if (brush is LinearGradientBrush gradient &&
                     gradient.GradientStops.Count > 0)
            {
                color = gradient.GradientStops
                    .OrderBy(stop => Math.Abs(stop.Offset - 0.5))
                    .First()
                    .Color;
            }

            return DarkenColor(color, 0.7);
        }
    }

    // -----------------------------------------------------------------------
    //   3) Coloration Partielle : DockablePane “PanelTitleBar”
    // -----------------------------------------------------------------------
    public static class PartialColoringHelper
    {
        public static void ApplyPartialColoring(IntPtr mainWindowHandle)
        {
            if (!ColoringStateManager.IsColoringActive || ColoringStateManager.IsFullMode) return;
            var wnd = GetMainWindow(mainWindowHandle);
            if (wnd == null) return;
            Dictionary<string, RibbonPanelColorScheme> panelColors =
                RibbonColorPreferences.CreateSchemes();

            var panels = FindVisualByTypeName(wnd, "PanelTitleBar");
            var targets =
                new List<(FrameworkElement Panel, Border Border, RibbonPanelColorScheme Scheme)>();
            foreach (var ptb in panels)
            {
                var prop = ptb.GetType().GetProperty("Title");
                if (prop == null) continue;
                var title = prop.GetValue(ptb)?.ToString();
                if (title != null &&
                    panelColors.TryGetValue(title, out RibbonPanelColorScheme scheme))
                {
                    var b = FindChildrenByType<Border>(ptb).FirstOrDefault() as Border ?? (ptb as Border);
                    if (b != null)
                        targets.Add((ptb, b, scheme));
                }
            }

            double ribbonLeft = double.MaxValue;
            double ribbonRight = double.MinValue;
            foreach (var target in targets)
            {
                if (!target.Scheme.IsContinuousAcrossRibbon)
                    continue;

                if (TryGetHorizontalRange(target.Panel, wnd, out double left, out double right))
                {
                    ribbonLeft = Math.Min(ribbonLeft, left);
                    ribbonRight = Math.Max(ribbonRight, right);
                }
            }

            bool hasContinuousRange =
                ribbonLeft < ribbonRight && ribbonLeft != double.MaxValue;

            foreach (var target in targets)
            {
                Brush background = target.Scheme.CreateBackgroundBrush();
                if (hasContinuousRange &&
                    target.Scheme.IsContinuousAcrossRibbon &&
                    TryGetHorizontalRange(target.Panel, wnd, out double left, out double right))
                {
                    double totalWidth = ribbonRight - ribbonLeft;
                    background = target.Scheme.CreateBackgroundBrush(
                        left - ribbonLeft,
                        right - ribbonLeft,
                        totalWidth);
                }

                target.Border.Background = background;
                target.Border.BorderBrush =
                    DarkenBackgroundBrush(background, target.Scheme.BackgroundColor);
                target.Border.BorderThickness = new Thickness(1);
                if (hasContinuousRange &&
                    target.Scheme.BackgroundPattern ==
                    RibbonBackgroundPattern.FrenchFlagContinuous)
                {
                    ColorContinuousFlagTextBlocks(
                        target.Border,
                        wnd,
                        ribbonLeft,
                        ribbonRight);
                }
                else
                {
                    ColorTextBlocks(
                        target.Border,
                        new SolidColorBrush(target.Scheme.TextColor));
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
                if (title != null && RibbonColorPreferences.IsKnownPanel(title))
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

        private static bool TryGetHorizontalRange(
            FrameworkElement element,
            Visual ancestor,
            out double left,
            out double right)
        {
            left = 0;
            right = 0;

            try
            {
                if (element == null || ancestor == null || element.ActualWidth <= 0)
                    return false;

                var transform = element.TransformToAncestor(ancestor);
                left = transform.Transform(new System.Windows.Point(0, 0)).X;
                right = left + element.ActualWidth;
                return right > left;
            }
            catch
            {
                return false;
            }
        }

        private static void ColorTextBlocks(DependencyObject p, Brush color)
        {
            foreach (var tb in FindChildrenByType<TextBlock>(p))
                tb.Foreground = color;
        }

        private static void ColorContinuousFlagTextBlocks(
            DependencyObject parent,
            Visual ancestor,
            double ribbonLeft,
            double ribbonRight)
        {
            double totalWidth = ribbonRight - ribbonLeft;
            if (totalWidth <= 0)
                return;

            foreach (var textBlock in FindChildrenByType<TextBlock>(parent))
            {
                if (!TryGetHorizontalRange(textBlock, ancestor, out double left, out double right))
                    continue;

                double center = ((left + right) / 2 - ribbonLeft) / totalWidth;
                textBlock.Foreground =
                    center < 1.0 / 3.0 || center >= 2.0 / 3.0
                        ? Brushes.White
                        : Brushes.Black;
            }
        }

        private static void ClearTextBlocks(DependencyObject p)
        {
            foreach (var tb in FindChildrenByType<TextBlock>(p))
                tb.ClearValue(TextBlock.ForegroundProperty);
        }

        private static SolidColorBrush DarkenColor(Color c, double f) =>
            new SolidColorBrush(Color.FromArgb(
                c.A,
                (byte)(c.R * f),
                (byte)(c.G * f),
                (byte)(c.B * f)));

        private static SolidColorBrush DarkenBackgroundBrush(Brush brush, Color fallback)
        {
            Color color = fallback;
            if (brush is SolidColorBrush solid)
            {
                color = solid.Color;
            }
            else if (brush is LinearGradientBrush gradient &&
                     gradient.GradientStops.Count > 0)
            {
                color = gradient.GradientStops
                    .OrderBy(stop => Math.Abs(stop.Offset - 0.5))
                    .First()
                    .Color;
            }

            return DarkenColor(color, 0.7);
        }
    }

    /// <summary>
    /// Premier essai de personnalisation de l'Arborescence du projet.
    /// La palette est un contrôle interne de Revit : la détection reste donc
    /// volontairement souple et toutes les modifications sont réversibles.
    /// </summary>
    public static class ProjectBrowserColoring
    {
        private static readonly Dictionary<
            DependencyObject,
            Dictionary<DependencyProperty, object>> OriginalValues =
            new Dictionary<
                DependencyObject,
                Dictionary<DependencyProperty, object>>();

        private static FrameworkElement _projectBrowserRoot;
        private static object _chromiumBrowser;
        private static bool _browserInjectionSucceeded;
        private static DispatcherTimer _refreshTimer;
        private static int _diagnosticState;
        private static string _revitVersion = "inconnue";
        private static bool _legacyNativeProjectBrowser;
        private static ProjectBrowserColorSettings _settings =
            ProjectBrowserColorPreferences.GetDefaults();

        private static SolidColorBrush BackgroundBrush =>
            new SolidColorBrush(_settings.BackgroundColor);

        private static SolidColorBrush TextBrush =>
            new SolidColorBrush(_settings.TextColor);

        private static SolidColorBrush AccentBrush =>
            new SolidColorBrush(_settings.AccentColor);

        public static string DiagnosticFilePath =>
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                "SauvegardePréférence",
                $"diagnostic-arborescence-{_revitVersion}.txt");

        public static void ConfigureRevitVersion(string version)
        {
            string safeVersion = new string(
                (version ?? string.Empty)
                    .Where(char.IsLetterOrDigit)
                    .ToArray());
            _revitVersion = string.IsNullOrWhiteSpace(safeVersion)
                ? "inconnue"
                : safeVersion;
            _diagnosticState = 0;
        }

        public static void Apply(IntPtr mainWindowHandle)
        {
            _settings = ProjectBrowserColorPreferences.Load();
            if (!_settings.IsEnabled)
            {
                Reset();
                return;
            }

            Window window = HwndSource
                .FromHwnd(mainWindowHandle)
                ?.RootVisual as Window;
            if (window == null)
                return;

            FrameworkElement root = FindProjectBrowserRoot(window);
            if (root == null)
            {
                WriteDiagnostic(window, null);
                return;
            }

            _projectBrowserRoot = root;
            ApplyColors(root);
            WriteDiagnostic(window, root);
            if (_legacyNativeProjectBrowser)
                return;

            EnsureRefreshTimer(root);
        }

        public static void Reset()
        {
            if (_chromiumBrowser != null)
            {
                ExecuteBrowserScript(
                    _chromiumBrowser,
                    "if(window.__bimaestroProjectBrowserTheme){" +
                    "const t=window.__bimaestroProjectBrowserTheme;" +
                    "if(t.clearFocus)t.clearFocus();" +
                    "if(t.observer)t.observer.disconnect();" +
                    "document.querySelectorAll('[data-bimaestro-badge]')" +
                    ".forEach(el=>el.remove());" +
                    "document.querySelectorAll(" +
                    "'[data-bimaestro-arc-star]," +
                    "[data-bimaestro-arc-lock]," +
                    "[data-bimaestro-arc-group]," +
                    "[data-bimaestro-focus-overlay]," +
                    "[data-bimaestro-bubble-surface]')" +
                    ".forEach(el=>{" +
                    "el.removeAttribute('data-bimaestro-arc-star');" +
                    "el.removeAttribute('data-bimaestro-arc-lock');" +
                    "el.removeAttribute('data-bimaestro-arc-group');" +
                    "if(el.hasAttribute('data-bimaestro-focus-overlay'))" +
                    "{el.remove();return;}" +
                    "el.removeAttribute(" +
                    "'data-bimaestro-bubble-surface');});" +
                    "const badgeStyle=document.getElementById(" +
                    "'bimaestro-project-browser-badges');" +
                    "if(badgeStyle)badgeStyle.remove();" +
                    "t.originals.forEach((css,el)=>{" +
                    "if(!el)return;" +
                    "if(css===null)el.removeAttribute('style');" +
                    "else el.setAttribute('style',css);});" +
                    "delete window.__bimaestroProjectBrowserTheme;}");
            }

            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer = null;
            }

            foreach (KeyValuePair<
                         DependencyObject,
                         Dictionary<DependencyProperty, object>> target
                     in OriginalValues.ToList())
            {
                foreach (KeyValuePair<DependencyProperty, object> property
                         in target.Value)
                {
                    try
                    {
                        if (property.Value == DependencyProperty.UnsetValue)
                            target.Key.ClearValue(property.Key);
                        else
                            target.Key.SetValue(property.Key, property.Value);
                    }
                    catch
                    {
                        // Un contrôle virtualisé peut avoir disparu entre-temps.
                    }
                }
            }

            OriginalValues.Clear();
            _projectBrowserRoot = null;
            _chromiumBrowser = null;
            _browserInjectionSucceeded = false;
            _diagnosticState = 0;
            _legacyNativeProjectBrowser = false;
        }

        public static void FocusSelectedSheetContent(
            Document document,
            IEnumerable<ElementId> selectedIds)
        {
            if (_chromiumBrowser == null)
                return;

            if (!ProjectBrowserColorPreferences
                    .Load()
                    .IsSheetViewSearchEnabled)
            {
                ClearAutomaticFocus();
                return;
            }

            if (document == null || selectedIds == null)
            {
                ScheduleAutomaticFocusClear();
                return;
            }

            View targetView = null;
            foreach (ElementId selectedId in selectedIds)
            {
                Element selectedElement = document.GetElement(selectedId);
                if (selectedElement is Viewport viewport)
                {
                    targetView =
                        document.GetElement(viewport.ViewId) as View;
                }
                else if (selectedElement is ScheduleSheetInstance schedule)
                {
                    targetView =
                        document.GetElement(schedule.ScheduleId) as View;
                }

                if (targetView != null)
                    break;
            }

            if (targetView == null ||
                string.IsNullOrWhiteSpace(targetView.Name))
            {
                ScheduleAutomaticFocusClear();
                return;
            }

            string viewNameJson =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    targetView.Name);
            ExecuteBrowserScript(
                _chromiumBrowser,
                "if(window.__bimaestroProjectBrowserTheme)" +
                "window.__bimaestroProjectBrowserTheme" +
                $".focusView({viewNameJson});");
        }

        public static void CompleteAutomaticFocusNavigation()
        {
            ClearAutomaticFocus();
        }

        private static void ScheduleAutomaticFocusClear()
        {
            ExecuteBrowserScript(
                _chromiumBrowser,
                "if(window.__bimaestroProjectBrowserTheme)" +
                "window.__bimaestroProjectBrowserTheme" +
                ".scheduleClearFocus(300);");
        }

        private static void ClearAutomaticFocus()
        {
            ExecuteBrowserScript(
                _chromiumBrowser,
                "if(window.__bimaestroProjectBrowserTheme)" +
                "window.__bimaestroProjectBrowserTheme.clearFocus();");
        }

        private static void EnsureRefreshTimer(FrameworkElement root)
        {
            if (_refreshTimer != null &&
                _refreshTimer.Dispatcher == root.Dispatcher)
            {
                return;
            }

            _refreshTimer?.Stop();
            _refreshTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                root.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _refreshTimer.Tick += (_, __) =>
            {
                if (!ColoringStateManager.IsColoringActive ||
                    _projectBrowserRoot == null ||
                    !IsAttachedToWindow(_projectBrowserRoot))
                {
                    return;
                }

                ApplyColors(_projectBrowserRoot);
            };
            _refreshTimer.Start();
        }

        private static void ApplyColors(FrameworkElement root)
        {
            List<FrameworkElement> descendants =
                FindChildrenByType<FrameworkElement>(root);
            IEnumerable<FrameworkElement> browserCandidates =
                descendants
                    .Where(IsBrowserCandidate)
                    .OrderBy(GetBrowserCandidatePriority);
            foreach (FrameworkElement browser in browserCandidates)
            {
                if (!ExecuteBrowserScript(
                        browser,
                        CreateBrowserThemeScript(_settings)))
                {
                    continue;
                }

                _chromiumBrowser = browser;
                _browserInjectionSucceeded = true;
                break;
            }

            _legacyNativeProjectBrowser =
                _revitVersion == "2023" &&
                !_browserInjectionSucceeded &&
                !browserCandidates.Any();
            if (_legacyNativeProjectBrowser)
                return;

            double minimumWidth = Math.Max(80, root.ActualWidth * 0.60);
            double minimumHeight = Math.Max(80, root.ActualHeight * 0.25);

            ApplyBackgroundIfSupported(root);
            foreach (FrameworkElement element in
                     descendants)
            {
                string typeName = element.GetType().Name;
                bool isTreeSurface =
                    typeName.IndexOf(
                        "Tree",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf(
                        "Browser",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                bool isLargeSurface =
                    element.ActualWidth >= minimumWidth &&
                    element.ActualHeight >= minimumHeight;

                if (isTreeSurface || isLargeSurface)
                    ApplyBackgroundIfSupported(element);

                if (element is TextBlock textBlock)
                {
                    Brush foreground = IsArcLabel(textBlock.Text)
                        ? AccentBrush
                        : TextBrush;
                    SetTrackedValue(
                        textBlock,
                        TextBlock.ForegroundProperty,
                        foreground);
                }
                else if (element is HeaderedItemsControl itemsControl &&
                         itemsControl.Header is string header)
                {
                    SetTrackedValue(
                        itemsControl,
                        System.Windows.Controls.Control.ForegroundProperty,
                        IsArcLabel(header) ? AccentBrush : TextBrush);
                }
                else if (element is ContentControl contentControl &&
                         contentControl.Content is string content)
                {
                    SetTrackedValue(
                        contentControl,
                        System.Windows.Controls.Control.ForegroundProperty,
                        IsArcLabel(content) ? AccentBrush : TextBrush);
                }
            }
        }

        private static string CreateBrowserThemeScript(
            ProjectBrowserColorSettings settings)
        {
            string script = @"
(()=>{
  const key='__bimaestroProjectBrowserTheme';
  const version=5;
  const theme={
    background:'__BIMAESTRO_BROWSER_BACKGROUND__',
    text:'__BIMAESTRO_BROWSER_TEXT__',
    accent:'__BIMAESTRO_BROWSER_ACCENT__',
    focusBackground:'__BIMAESTRO_BROWSER_FOCUS__',
    mode:'__BIMAESTRO_BROWSER_MODE__'
  };
  if(window[key]&&window[key].version!==version){
    const previous=window[key];
    if(previous.observer)previous.observer.disconnect();
    if(previous.clearFocus)previous.clearFocus();
    document.querySelectorAll(
      '[data-bimaestro-arc-star],'+
      '[data-bimaestro-arc-lock],'+
      '[data-bimaestro-arc-group],'+
      '[data-bimaestro-focus-overlay],'+
      '[data-bimaestro-bubble-surface]')
      .forEach(el=>{
        if(el.hasAttribute('data-bimaestro-focus-overlay')){
          el.remove();
          return;
        }
        el.removeAttribute('data-bimaestro-arc-star');
        el.removeAttribute('data-bimaestro-arc-lock');
        el.removeAttribute('data-bimaestro-arc-group');
        el.removeAttribute('data-bimaestro-bubble-surface');
      });
    if(previous.originals)
      previous.originals.forEach((css,el)=>{
        if(!el)return;
        if(css===null)el.removeAttribute('style');
        else el.setAttribute('style',css);
      });
    const previousStyle=document.getElementById(
      'bimaestro-project-browser-badges');
    if(previousStyle)previousStyle.remove();
    delete window[key];
  }
  if(!window[key]){
    const originals=new Map();
    const remember=el=>{
      if(!originals.has(el))
        originals.set(
          el,
          el.hasAttribute('style')?el.getAttribute('style'):null);
    };
    let badgeStyle=document.getElementById(
      'bimaestro-project-browser-badges');
    if(!badgeStyle){
      badgeStyle=document.createElement('style');
      badgeStyle.id='bimaestro-project-browser-badges';
      badgeStyle.textContent=`
        @keyframes bimaestroBubbleDrift{
          0%{
            background-position:
              12px 18px,
              74px 42px,
              28px 96px,
              118px 126px;
          }
          50%{
            background-position:
              42px -62px,
              22px -46px,
              76px 26px,
              68px 54px;
          }
          100%{
            background-position:
              72px -142px,
              -30px -134px,
              124px -44px,
              18px -18px;
          }
        }
        @keyframes bimaestroWaveDrift{
          0%{background-position:0% 25%,100% 75%,20% 100%;}
          50%{background-position:100% 55%,0% 35%,80% 0%;}
          100%{background-position:0% 25%,100% 75%,20% 100%;}
        }
        @keyframes bimaestroFireflyDrift{
          0%{
            background-position:10px 18px,62px 94px,124px 42px;
          }
          50%{
            background-position:54px -48px,18px 28px,82px -22px;
          }
          100%{
            background-position:98px -114px,-26px -38px,40px -86px;
          }
        }
        @keyframes bimaestroAuroraDrift{
          0%{background-position:0% 50%,100% 50%;}
          50%{background-position:100% 45%,0% 55%;}
          100%{background-position:0% 50%,100% 50%;}
        }
        [data-bimaestro-bubble-surface]{
          background-color:${theme.background} !important;
          background-attachment:fixed;
        }
        [data-bimaestro-bubble-surface='solid']{
          background-image:none !important;
          animation:none !important;
        }
        [data-bimaestro-bubble-surface='bubbles']{
          background-color:${theme.background} !important;
          background-image:
            radial-gradient(
              circle,
              rgba(255,180,207,.52) 0 7px,
              rgba(255,180,207,.18) 8px 10px,
              transparent 11px),
            radial-gradient(
              circle,
              rgba(178,220,255,.48) 0 10px,
              rgba(178,220,255,.16) 11px 14px,
              transparent 15px),
            radial-gradient(
              circle,
              rgba(191,237,211,.46) 0 5px,
              rgba(191,237,211,.15) 6px 8px,
              transparent 9px),
            radial-gradient(
              circle,
              rgba(225,199,255,.40) 0 13px,
              rgba(225,199,255,.13) 14px 17px,
              transparent 18px);
          background-repeat:repeat;
          background-size:
            156px 156px,
            224px 224px,
            188px 188px,
            286px 286px;
          background-attachment:fixed;
          animation:bimaestroBubbleDrift 22s linear infinite;
        }
        [data-bimaestro-bubble-surface='waves']{
          background-image:
            radial-gradient(
              ellipse at 15% 115%,
              rgba(255,188,211,.42) 0 24%,
              transparent 25%),
            radial-gradient(
              ellipse at 85% -15%,
              rgba(177,220,255,.40) 0 27%,
              transparent 28%),
            radial-gradient(
              ellipse at 50% 120%,
              rgba(191,237,211,.32) 0 20%,
              transparent 21%);
          background-size:190% 145%,210% 155%,230% 170%;
          background-repeat:repeat;
          animation:bimaestroWaveDrift 20s ease-in-out infinite;
        }
        [data-bimaestro-bubble-surface='fireflies']{
          background-image:
            radial-gradient(
              circle,
              rgba(255,207,107,.68) 0 2px,
              rgba(255,207,107,.18) 3px 6px,
              transparent 7px),
            radial-gradient(
              circle,
              rgba(159,224,255,.62) 0 2px,
              rgba(159,224,255,.15) 3px 5px,
              transparent 6px),
            radial-gradient(
              circle,
              rgba(205,177,255,.58) 0 1px,
              rgba(205,177,255,.15) 2px 4px,
              transparent 5px);
          background-size:138px 138px,194px 194px,166px 166px;
          background-repeat:repeat;
          animation:bimaestroFireflyDrift 18s linear infinite;
        }
        [data-bimaestro-bubble-surface='aurora']{
          background-image:
            linear-gradient(
              120deg,
              rgba(255,192,216,.34),
              rgba(187,225,255,.32),
              rgba(197,240,218,.30),
              rgba(226,203,255,.32)),
            linear-gradient(
              60deg,
              transparent 20%,
              rgba(255,255,255,.36) 50%,
              transparent 80%);
          background-size:300% 300%,220% 220%;
          background-repeat:repeat;
          animation:bimaestroAuroraDrift 16s ease-in-out infinite;
        }
        @media (prefers-reduced-motion:reduce){
          [data-bimaestro-bubble-surface='bubbles'],
          [data-bimaestro-bubble-surface='waves'],
          [data-bimaestro-bubble-surface='fireflies'],
          [data-bimaestro-bubble-surface='aurora']{
            animation:none;
          }
        }`;
      (document.head||document.documentElement).appendChild(badgeStyle);
    }
    const visibleLabels=()=>Array.from(document.querySelectorAll('*'))
      .filter(el=>{
        if(el.closest('[data-bimaestro-badge]'))return false;
        const value=(el.textContent||'').trim();
        if(!value||el.children.length!==0)return false;
        const r=el.getBoundingClientRect();
        return r.width>0&&r.height>0&&r.bottom>=0&&
          r.top<=window.innerHeight;
      });
    const meaningfulLabels=()=>visibleLabels().filter(label=>{
      const value=(label.textContent||'').trim();
      const style=getComputedStyle(label);
      const rect=label.getBoundingClientRect();
      return value.length>1&&
        !/^[+\-–—…]+$/.test(value)&&
        style.display!=='none'&&
        style.visibility!=='hidden'&&
        Number(style.opacity)!==0&&
        rect.height<=32;
    });
    const markArcGroups=()=>{
      document.querySelectorAll(
        '[data-bimaestro-arc-group]')
        .forEach(label=>{
          label.removeAttribute('data-bimaestro-arc-group');
        });
      const labels=meaningfulLabels().sort((a,b)=>{
        const ar=a.getBoundingClientRect();
        const br=b.getBoundingClientRect();
        return ar.top-br.top||ar.left-br.left;
      });
      const arcLabels=labels.filter(
        label=>(label.textContent||'').trim().toUpperCase()==='ARC');
      arcLabels.forEach(arc=>{
        const arcRect=arc.getBoundingClientRect();
        const descendants=[];
        const arcIndex=labels.indexOf(arc);
        for(let index=arcIndex+1;index<labels.length;index++){
          const label=labels[index];
          const rect=label.getBoundingClientRect();
          if(rect.top<=arcRect.top+2)continue;
          if(rect.left<=arcRect.left+3)break;
          if(rect.left>arcRect.left+6)descendants.push(label);
        }
        const uniqueDescendants=Array.from(new Set(descendants))
          .filter(label=>{
            if(label===arc)return false;
            const rect=label.getBoundingClientRect();
            return rect.top>=arcRect.bottom-2&&
              rect.left>arcRect.left+6;
          });
        if(uniqueDescendants.length===0)return;
        arc.setAttribute('data-bimaestro-arc-group','1');
      });
    };
    const paint=()=>{
      [document.documentElement,document.body].forEach(el=>{
        if(!el)return;
        remember(el);
        el.setAttribute(
          'data-bimaestro-bubble-surface',
          theme.mode);
        el.style.setProperty(
          'background-color',
          theme.background,
          'important');
        el.style.setProperty('color',theme.text,'important');
      });
      markArcGroups();
      document.querySelectorAll('*').forEach(el=>{
        if(el.closest('[data-bimaestro-badge]'))return;
        const r=el.getBoundingClientRect();
        if(r.width>=window.innerWidth*.60&&
           r.height>=window.innerHeight*.25){
          remember(el);
          el.setAttribute(
            'data-bimaestro-bubble-surface',
            theme.mode);
          el.style.setProperty(
            'background-color',
            theme.background,
            'important');
        }
        const value=(el.textContent||'').trim();
        if(value&&el.children.length===0){
          remember(el);
          el.style.setProperty(
            'color',
            el.hasAttribute('data-bimaestro-arc-group')?
              theme.accent:theme.text,
            'important');
        }
      });
    };
    const setInputValue=(input,value)=>{
      const descriptor=Object.getOwnPropertyDescriptor(
        HTMLInputElement.prototype,
        'value');
      if(descriptor&&descriptor.set)
        descriptor.set.call(input,value);
      else input.value=value;
      input.dispatchEvent(new Event('input',{bubbles:true}));
      input.dispatchEvent(new Event('change',{bubbles:true}));
    };
    let activeHighlight=null;
    let focusSequence=0;
    let automaticSearchValue=null;
    let clearFocusTimer=null;
    const findSearchInput=()=>Array.from(
      document.querySelectorAll('input')).find(input=>{
        const placeholder=(input.placeholder||'').toLowerCase();
        return placeholder.includes('recherch')||
          placeholder.includes('search');
      });
    const cancelScheduledClear=()=>{
      if(clearFocusTimer===null)return;
      clearTimeout(clearFocusTimer);
      clearFocusTimer=null;
    };
    const clearFocus=()=>{
      cancelScheduledClear();
      focusSequence++;
      if(activeHighlight){
        const previousHighlight=activeHighlight;
        activeHighlight=null;
        previousHighlight.remove();
      }
      const search=findSearchInput();
      if(search&&automaticSearchValue!==null&&
         search.value===automaticSearchValue)
        setInputValue(search,'');
      automaticSearchValue=null;
      paint();
    };
    const scheduleClearFocus=delay=>{
      cancelScheduledClear();
      const safeDelay=Math.max(100,Number(delay)||300);
      clearFocusTimer=setTimeout(()=>{
        clearFocusTimer=null;
        clearFocus();
      },safeDelay);
    };
    const highlightLabel=label=>{
      if(!label)return false;
      if(activeHighlight){
        const previousHighlight=activeHighlight;
        activeHighlight=null;
        previousHighlight.remove();
      }
      label.scrollIntoView({
        behavior:'auto',
        block:'center',
        inline:'nearest'
      });
      const rect=label.getBoundingClientRect();
      if(rect.width<=0||rect.height<=0)return false;
      const overlay=document.createElement('div');
      overlay.setAttribute(
        'data-bimaestro-focus-overlay',
        '1');
      overlay.style.position='fixed';
      overlay.style.left='2px';
      overlay.style.width='calc(100vw - 18px)';
      overlay.style.top=`${Math.max(0,rect.top-3)}px`;
      overlay.style.height=`${Math.max(22,rect.height+6)}px`;
      overlay.style.boxSizing='border-box';
      overlay.style.border=`2px solid ${theme.accent}`;
      overlay.style.borderRadius='4px';
      overlay.style.background=theme.focusBackground;
      overlay.style.pointerEvents='none';
      overlay.style.zIndex='2147483646';
      (document.body||document.documentElement)
        .appendChild(overlay);
      activeHighlight=overlay;
      setTimeout(()=>{
        if(activeHighlight!==overlay)return;
        activeHighlight=null;
        overlay.remove();
      },2600);
      return true;
    };
    const findLabel=name=>visibleLabels().find(
      label=>(label.textContent||'').trim()===name);
    const focusView=name=>{
      if(!name)return;
      cancelScheduledClear();
      const sequence=++focusSequence;
      if(highlightLabel(findLabel(name)))return;
      const search=findSearchInput();
      if(!search)return;
      automaticSearchValue=name;
      setInputValue(search,name);
      setTimeout(()=>{
        if(sequence!==focusSequence)return;
        highlightLabel(findLabel(name));
      },300);
    };
    let paintScheduled=false;
    const observer=new MutationObserver(()=>{
      if(paintScheduled)return;
      paintScheduled=true;
      requestAnimationFrame(()=>{
        paintScheduled=false;
        paint();
      });
    });
    observer.observe(
      document.documentElement,
      {childList:true,subtree:true});
    window[key]={
      version,
      originals,
      observer,
      paint,
      focusView,
      clearFocus,
      scheduleClearFocus
    };
  }
  window[key].paint();
})()";
            return script
                .Replace(
                    "__BIMAESTRO_BROWSER_BACKGROUND__",
                    ToCssColor(settings.BackgroundColor))
                .Replace(
                    "__BIMAESTRO_BROWSER_TEXT__",
                    ToCssColor(settings.TextColor))
                .Replace(
                    "__BIMAESTRO_BROWSER_ACCENT__",
                    ToCssColor(settings.AccentColor))
                .Replace(
                    "__BIMAESTRO_BROWSER_FOCUS__",
                    ToCssFocusBackground(settings.AccentColor))
                .Replace(
                    "__BIMAESTRO_BROWSER_MODE__",
                    GetBrowserBackgroundMode(
                        settings.BackgroundMode));
        }

        private static string ToCssColor(Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private static string ToCssFocusBackground(Color color)
        {
            return $"rgba({color.R},{color.G},{color.B},0.22)";
        }

        private static string GetBrowserBackgroundMode(string mode)
        {
            if (string.Equals(
                    mode,
                    "Bulles pastel",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "bubbles";
            }

            if (string.Equals(
                    mode,
                    "Vagues pastel",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "waves";
            }

            if (string.Equals(
                    mode,
                    "Lucioles pastel",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "fireflies";
            }

            if (string.Equals(
                    mode,
                    "Aurore pastel",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "aurora";
            }

            return "solid";
        }

        private static bool ExecuteBrowserScript(
            object browser,
            string script)
        {
            if (browser == null || string.IsNullOrWhiteSpace(script))
                return false;

            return ExecuteBrowserScript(
                browser,
                script,
                new HashSet<object>(),
                0);
        }

        private static bool ExecuteBrowserScript(
            object browser,
            string script,
            HashSet<object> visited,
            int depth)
        {
            if (browser == null ||
                depth > 3 ||
                !visited.Add(browser))
            {
                return false;
            }

            try
            {
                Type extensionsType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(assembly =>
                        assembly.GetType(
                            "CefSharp.WebBrowserExtensions",
                            false))
                    .FirstOrDefault(type => type != null);
                if (extensionsType != null)
                {
                    System.Reflection.MethodInfo method = extensionsType
                        .GetMethods(
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Static)
                        .FirstOrDefault(candidate =>
                        {
                            if (!candidate.Name.Equals(
                                    "ExecuteScriptAsync",
                                    StringComparison.Ordinal))
                            {
                                return false;
                            }

                            System.Reflection.ParameterInfo[] parameters =
                                candidate.GetParameters();
                            return parameters.Length == 2 &&
                                   parameters[1].ParameterType ==
                                   typeof(string) &&
                                   parameters[0].ParameterType
                                       .IsInstanceOfType(browser);
                        });
                    if (method != null)
                    {
                        method.Invoke(null, new[] { browser, script });
                        return true;
                    }
                }
            }
            catch
            {
                // Le contrôle peut utiliser WebView2 ou une autre version de Cef.
            }

            try
            {
                System.Reflection.MethodInfo directMethod = browser
                    .GetType()
                    .GetMethods(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(candidate =>
                    {
                        bool supportedName =
                            candidate.Name.Equals(
                                "ExecuteScriptAsync",
                                StringComparison.Ordinal) ||
                            candidate.Name.Equals(
                                "EvaluateScriptAsync",
                                StringComparison.Ordinal);
                        if (!supportedName)
                            return false;

                        System.Reflection.ParameterInfo[] parameters =
                            candidate.GetParameters();
                        return parameters.Length >= 1 &&
                               parameters[0].ParameterType ==
                               typeof(string) &&
                               parameters
                                   .Skip(1)
                                   .All(parameter => parameter.IsOptional);
                    });
                if (directMethod != null)
                {
                    System.Reflection.ParameterInfo[] parameters =
                        directMethod.GetParameters();
                    object[] arguments = parameters
                        .Select((parameter, index) =>
                            index == 0
                                ? (object)script
                                : Type.Missing)
                        .ToArray();
                    directMethod.Invoke(browser, arguments);
                    return true;
                }
            }
            catch
            {
                // On essaie ensuite les contrôles internes du conteneur.
            }

            string[] nestedPropertyNames =
            {
                "CoreWebView2",
                "Browser",
                "WebBrowser",
                "WebView",
                "Content",
                "Child"
            };
            foreach (string propertyName in nestedPropertyNames)
            {
                try
                {
                    System.Reflection.PropertyInfo property = browser
                        .GetType()
                        .GetProperty(
                            propertyName,
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                    if (property == null ||
                        property.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    object nestedBrowser =
                        property.GetValue(browser, null);
                    if (nestedBrowser != null &&
                        ExecuteBrowserScript(
                            nestedBrowser,
                            script,
                            visited,
                            depth + 1))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Certaines propriétés internes lèvent avant initialisation.
                }
            }

            return false;
        }

        private static bool IsBrowserCandidate(
            FrameworkElement element)
        {
            string typeName = element?.GetType().Name ?? string.Empty;
            return typeName.IndexOf(
                       "Browser",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf(
                       "WebView",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetBrowserCandidatePriority(
            FrameworkElement element)
        {
            string typeName = element?.GetType().Name ?? string.Empty;
            if (typeName.IndexOf(
                    "ChromiumWebBrowser",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }

            if (typeName.IndexOf(
                    "WebView2",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }

            if (typeName.IndexOf(
                    "WebBrowserControl",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 2;
            }

            return 3;
        }

        private static void ApplyBackgroundIfSupported(
            FrameworkElement element)
        {
            if (element is Border border)
            {
                SetTrackedValue(
                    border,
                    Border.BackgroundProperty,
                    BackgroundBrush);
            }
            else if (element is System.Windows.Controls.Panel panel)
            {
                SetTrackedValue(
                    panel,
                    System.Windows.Controls.Panel.BackgroundProperty,
                    BackgroundBrush);
            }
            else if (element is System.Windows.Controls.Control control)
            {
                SetTrackedValue(
                    control,
                    System.Windows.Controls.Control.BackgroundProperty,
                    BackgroundBrush);
                SetTrackedValue(
                    control,
                    System.Windows.Controls.Control.ForegroundProperty,
                    TextBrush);
            }
        }

        private static FrameworkElement FindProjectBrowserRoot(Window window)
        {
            var visualRoots = new List<FrameworkElement>
            {
                window
            };

            foreach (PresentationSource source in
                     PresentationSource.CurrentSources)
            {
                if (source?.RootVisual is FrameworkElement root &&
                    !visualRoots.Contains(root))
                {
                    visualRoots.Add(root);
                }
            }

            // Le titre de la palette appartient au HwndSource principal,
            // tandis que son contenu Chromium possède son propre HwndSource.
            // On privilégie donc le vrai ProjectBrowserFrame avant le cadre
            // WPF extérieur, même si ce dernier est rencontré en premier.
            foreach (FrameworkElement visualRoot in visualRoots)
            {
                IEnumerable<FrameworkElement> elements =
                    new[] { visualRoot }
                        .Concat(
                            FindChildrenByType<FrameworkElement>(
                                visualRoot));
                FrameworkElement projectBrowserFrame =
                    elements.FirstOrDefault(element =>
                        element.GetType().Name.IndexOf(
                            "ProjectBrowser",
                            StringComparison.OrdinalIgnoreCase) >= 0 &&
                        IsUsablePaneRoot(element, window));
                if (projectBrowserFrame != null)
                    return projectBrowserFrame;
            }

            foreach (FrameworkElement visualRoot in visualRoots)
            {
                FrameworkElement result =
                    FindProjectBrowserRootInSource(visualRoot, window);
                if (result != null)
                    return result;
            }

            return null;
        }

        private static FrameworkElement FindProjectBrowserRootInSource(
            FrameworkElement visualRoot,
            Window mainWindow)
        {
            IEnumerable<FrameworkElement> elements =
                new[] { visualRoot }
                    .Concat(
                        FindChildrenByType<FrameworkElement>(visualRoot));

            foreach (FrameworkElement element in elements)
            {
                if (!IsProjectBrowserMarker(element))
                    continue;

                if (IsUsablePaneRoot(element, mainWindow))
                    return element;

                DependencyObject current = element;
                while (current != null)
                {
                    current = GetParent(current);
                    if (current == null)
                        break;

                    if (current is FrameworkElement candidate &&
                        IsUsablePaneRoot(candidate, mainWindow))
                    {
                        return candidate;
                    }

                    if (current == visualRoot)
                        break;
                }
            }

            return null;
        }

        private static bool IsProjectBrowserMarker(FrameworkElement element)
        {
            if (element == null)
                return false;

            if (element.GetType().Name.IndexOf(
                    "ProjectBrowser",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (element is TextBlock textBlock &&
                IsProjectBrowserCaption(textBlock.Text))
            {
                return true;
            }

            string automationName = AutomationProperties.GetName(element);
            if (IsProjectBrowserCaption(automationName))
                return true;

            try
            {
                object title = element.GetType()
                    .GetProperty("Title")
                    ?.GetValue(element);
                return IsProjectBrowserCaption(title?.ToString());
            }
            catch
            {
                return false;
            }
        }

        private static bool IsProjectBrowserCaption(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.IndexOf(
                       "Arborescence du projet",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf(
                       "Project Browser",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsUsablePaneRoot(
            FrameworkElement element,
            Window window)
        {
            if (element == null ||
                element.ActualWidth < 150 ||
                element.ActualHeight < 180)
            {
                return false;
            }

            return element.ActualWidth <
                   Math.Max(800, window.ActualWidth * 0.75);
        }

        private static bool IsArcLabel(string value)
        {
            return string.Equals(
                value?.Trim(),
                "ARC",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAttachedToWindow(FrameworkElement element)
        {
            try
            {
                return element.IsLoaded &&
                       PresentationSource.FromVisual(element) != null;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteDiagnostic(
            Window mainWindow,
            FrameworkElement result)
        {
            int newState = result == null ? 1 : 2;
            if (_diagnosticState >= newState)
                return;

            try
            {
                var lines = new List<string>
                {
                    $"Date UTC : {DateTime.UtcNow:O}",
                    $"Version Revit : {_revitVersion}",
                    $"Résultat : {(result == null ? "introuvable" : "trouvé")}"
                };

                int sourceIndex = 0;
                foreach (PresentationSource source in
                         PresentationSource.CurrentSources)
                {
                    sourceIndex++;
                    if (!(source?.RootVisual is FrameworkElement root))
                    {
                        lines.Add($"Source {sourceIndex} : sans racine WPF");
                        continue;
                    }

                    List<FrameworkElement> descendants =
                        FindChildrenByType<FrameworkElement>(root);
                    string interestingTypes = string.Join(
                        ", ",
                        descendants
                            .Select(element => element.GetType().Name)
                            .Where(name =>
                                name.IndexOf(
                                    "Browser",
                                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf(
                                    "Tree",
                                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf(
                                    "Dock",
                                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf(
                                    "Pane",
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                            .Distinct()
                            .Take(20));

                    lines.Add(
                        $"Source {sourceIndex} : {root.GetType().Name}, " +
                        $"{root.ActualWidth:0} x {root.ActualHeight:0}, " +
                        $"{descendants.Count} éléments");
                    if (!string.IsNullOrWhiteSpace(interestingTypes))
                        lines.Add($"  Types utiles : {interestingTypes}");
                }

                if (result != null)
                {
                    List<TextBlock> texts =
                        FindChildrenByType<TextBlock>(result);
                    lines.Add(
                        $"Contrôle : {result.GetType().FullName}, " +
                        $"{result.ActualWidth:0} x {result.ActualHeight:0}");
                    lines.Add($"TextBlock visibles : {texts.Count}");
                    lines.Add(
                        $"Libellés ARC visibles : " +
                        $"{texts.Count(text => IsArcLabel(text.Text))}");
                    lines.Add(
                        $"Chromium trouvé : " +
                        $"{(_chromiumBrowser == null ? "non" : "oui")}");
                    string activeBrowserType =
                        _chromiumBrowser?.GetType().FullName ?? "aucun";
                    lines.Add(
                        $"Moteur utilisé : {activeBrowserType}");
                    string browserCandidateTypes = string.Join(
                        ", ",
                        FindChildrenByType<FrameworkElement>(result)
                            .Where(IsBrowserCandidate)
                            .Select(element =>
                                element.GetType().FullName)
                            .Distinct());
                    lines.Add(
                        $"Moteurs candidats : {browserCandidateTypes}");
                    lines.Add(
                        $"Injection CSS demandée : " +
                        $"{(_browserInjectionSucceeded ? "oui" : "non")}");
                }

                string directory = Path.GetDirectoryName(DiagnosticFilePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllLines(DiagnosticFilePath, lines);
                _diagnosticState = newState;
            }
            catch
            {
                // Le diagnostic ne doit jamais affecter le démarrage de Revit.
            }
        }

        private static DependencyObject GetParent(DependencyObject element)
        {
            try
            {
                return VisualTreeHelper.GetParent(element) ??
                       LogicalTreeHelper.GetParent(element);
            }
            catch
            {
                return null;
            }
        }

        private static void SetTrackedValue(
            DependencyObject target,
            DependencyProperty property,
            object value)
        {
            if (!OriginalValues.TryGetValue(
                    target,
                    out Dictionary<DependencyProperty, object> properties))
            {
                properties = new Dictionary<DependencyProperty, object>();
                OriginalValues[target] = properties;
            }

            if (!properties.ContainsKey(property))
                properties[property] = target.ReadLocalValue(property);

            try
            {
                target.SetValue(property, value);
            }
            catch
            {
                // Certaines surfaces Revit exposent la propriété en lecture seule.
            }
        }

        private static List<T> FindChildrenByType<T>(
            DependencyObject parent)
            where T : DependencyObject
        {
            var result = new List<T>();
            if (parent == null)
                return result;

            int childCount;
            try
            {
                childCount = VisualTreeHelper.GetChildrenCount(parent);
            }
            catch
            {
                return result;
            }

            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child;
                try
                {
                    child = VisualTreeHelper.GetChild(parent, index);
                }
                catch
                {
                    continue;
                }

                if (child is T typedChild)
                    result.Add(typedChild);

                result.AddRange(FindChildrenByType<T>(child));
            }

            return result;
        }
    }
}

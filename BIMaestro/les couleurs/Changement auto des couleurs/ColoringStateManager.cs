using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

                if (RibbonColorPreferences.TryGetPanelScheme(
                    val,
                    panelColors,
                    out RibbonPanelColorScheme matchedScheme))
                {
                    targets.Add((border, matchedScheme));
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
                if (RibbonColorPreferences.TryGetPanelScheme(
                    title,
                    panelColors,
                    out RibbonPanelColorScheme scheme))
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
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect WorkArea;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(
            IntPtr windowHandle,
            out NativeRect rectangle);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(
            NativePoint point,
            uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(
            IntPtr monitorHandle,
            ref NativeMonitorInfo monitorInfo);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(
            IntPtr windowHandle,
            int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(
            IntPtr windowHandle,
            int index,
            IntPtr newValue);

        private const int ExtendedWindowStyleIndex = -20;
        private const long TransparentWindowStyle = 0x00000020L;
        private const long ToolWindowStyle = 0x00000080L;
        private const long NoActivateWindowStyle = 0x08000000L;
        private const uint NearestMonitor = 0x00000002;
        private const uint NoSizeWindowPosition = 0x0001;
        private const uint NoZOrderWindowPosition = 0x0004;
        private const uint NoActivateWindowPosition = 0x0010;
        private const uint ShowWindowPosition = 0x0040;

        private static readonly Dictionary<
            DependencyObject,
            Dictionary<DependencyProperty, object>> OriginalValues =
            new Dictionary<
                DependencyObject,
                Dictionary<DependencyProperty, object>>();

        private static FrameworkElement _projectBrowserRoot;
        private static object _chromiumBrowser;
        private static object _viewHoverPreviewBrowser;
        private static bool _browserInjectionSucceeded;
        private static DispatcherTimer _refreshTimer;
        private static DispatcherTimer _viewHoverPollTimer;
        private static bool _viewHoverPollPending;
        private static System.Windows.Controls.Primitives.Popup
            _viewHoverPopup;
        private static System.Windows.Controls.Image _viewHoverImage;
        private static Border _viewHoverBorder;
        private static TextBlock _viewHoverCaption;
        private static string _visibleViewHoverKey = string.Empty;
        private static int _viewHoverEmptyPollCount;
        private static string _revitVersion = "inconnue";
        private static bool _legacyNativeProjectBrowser;
        private static ProjectBrowserColorSettings _settings =
            ProjectBrowserColorPreferences.GetDefaults();
        private static string _activeViewName = string.Empty;
        private static string _activeViewElementId = string.Empty;
        private static long _activeViewGeneration;
        private static IReadOnlyList<string> _activeViewFolderPath =
            Array.Empty<string>();
        private static Document _viewTypeMapDocument;
        private static string _viewTypeMapActiveViewId = string.Empty;
        private static string _viewTypeMapJson = "[]";
        private sealed class BrowserViewTypeInfo
        {
            [Newtonsoft.Json.JsonProperty("id")]
            public string Id { get; set; }

            [Newtonsoft.Json.JsonProperty("name")]
            public string Name { get; set; }

            [Newtonsoft.Json.JsonProperty("kind")]
            public string Kind { get; set; }
        }

        private sealed class ViewHoverPreviewInfo
        {
            public string ViewId { get; set; }
            public string ViewName { get; set; }
            public string DataUri { get; set; }
            public bool IsStale { get; set; }
            public DateTime CapturedAtLocal { get; set; }
        }
        private static readonly Dictionary<string, ViewHoverPreviewInfo>
            ViewHoverPreviews =
                new Dictionary<string, ViewHoverPreviewInfo>(
                    StringComparer.OrdinalIgnoreCase);

        private static SolidColorBrush BackgroundBrush =>
            new SolidColorBrush(_settings.BackgroundColor);

        private static SolidColorBrush TextBrush =>
            new SolidColorBrush(_settings.TextColor);

        public static void ConfigureRevitVersion(string version)
        {
            string safeVersion = new string(
                (version ?? string.Empty)
                    .Where(char.IsLetterOrDigit)
                    .ToArray());
            _revitVersion = string.IsNullOrWhiteSpace(safeVersion)
                ? "inconnue"
                : safeVersion;
        }

        public static void Apply(IntPtr mainWindowHandle)
        {
            _settings = ProjectBrowserColorPreferences.Load();
            if (!_settings.IsEnabled)
                Reset();

            Window window = HwndSource
                .FromHwnd(mainWindowHandle)
                ?.RootVisual as Window;
            if (window == null)
                return;

            FrameworkElement root = FindProjectBrowserRoot(window);
            if (root == null)
                return;

            _projectBrowserRoot = root;
            ApplyColors(root);
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
                    "if(t.dispose)t.dispose();" +
                    "else{" +
                    "if(t.observer)t.observer.disconnect();" +
                    "if(t.clearFocus)t.clearFocus();}" +
                    "document.querySelectorAll('[data-bimaestro-badge]')" +
                    ".forEach(el=>el.remove());" +
                    "document.querySelectorAll(" +
                    "'[data-bimaestro-arc-star]," +
                    "[data-bimaestro-arc-lock]," +
                    "[data-bimaestro-arc-group]," +
                    "[data-bimaestro-active-parent]," +
                    "[data-bimaestro-view-kind]," +
                    "[data-bimaestro-view-text]," +
                    "[data-bimaestro-focus-overlay]," +
                    "[data-bimaestro-focus-target]," +
                    "[data-bimaestro-bubble-surface]')" +
                    ".forEach(el=>{" +
                    "el.removeAttribute('data-bimaestro-arc-star');" +
                    "el.removeAttribute('data-bimaestro-arc-lock');" +
                    "el.removeAttribute('data-bimaestro-arc-group');" +
                    "el.removeAttribute('data-bimaestro-active-parent');" +
                    "el.removeAttribute('data-bimaestro-view-kind');" +
                    "el.removeAttribute('data-bimaestro-view-parent');" +
                    "el.removeAttribute('data-bimaestro-selected');" +
                    "el.removeAttribute('data-bimaestro-color-target');" +
                    "if(el.hasAttribute('data-bimaestro-view-text'))" +
                    "el.style.removeProperty('color');" +
                    "el.removeAttribute('data-bimaestro-view-text');" +
                    "el.style.removeProperty('--bimaestro-view-color');" +
                    "el.removeAttribute('data-bimaestro-focus-target');" +
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
                ExecuteBrowserScript(
                    _chromiumBrowser,
                    "if(window.__bimaestroViewHoverPreview){" +
                    "window.__bimaestroViewHoverPreview.dispose();" +
                    "delete window.__bimaestroViewHoverPreview;}");
            }

            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer = null;
            }

            if (_viewHoverPollTimer != null)
            {
                _viewHoverPollTimer.Stop();
                _viewHoverPollTimer = null;
            }
            HideViewHoverPopup();
            _viewHoverPollPending = false;

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
            _viewHoverPreviewBrowser = null;
            _browserInjectionSucceeded = false;
            _legacyNativeProjectBrowser = false;
        }

        public static void SetViewHoverPreview(
            string viewId,
            string viewName,
            string dataUri,
            DateTime? capturedAtLocal = null,
            bool isStale = false)
        {
            if (string.IsNullOrWhiteSpace(dataUri)) return;

            string safeId = viewId ?? string.Empty;
            string safeName = viewName ?? string.Empty;
            string key = !string.IsNullOrWhiteSpace(safeId)
                ? "id:" + safeId
                : "name:" + safeName;
            ViewHoverPreviews[key] = new ViewHoverPreviewInfo
            {
                ViewId = safeId,
                ViewName = safeName,
                DataUri = dataUri,
                IsStale = isStale,
                CapturedAtLocal = capturedAtLocal ?? DateTime.Now
            };
            ViewHoverPreviewInfo updatedPreview = ViewHoverPreviews[key];

            if (string.Equals(
                    _visibleViewHoverKey,
                    key,
                    StringComparison.OrdinalIgnoreCase) &&
                _viewHoverPopup?.IsOpen == true)
            {
                if (_viewHoverImage != null)
                {
                    _viewHoverImage.Source = CreateBitmapFromDataUri(
                        updatedPreview.DataUri);
                }
                ApplyViewHoverBorder(updatedPreview);
                UpdateViewHoverCaption(updatedPreview);
            }

            if (_chromiumBrowser != null)
            {
                EnsureViewHoverPreviewScript(_chromiumBrowser);
                PushViewHoverPreview(
                    _chromiumBrowser,
                    updatedPreview);
            }
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
            string viewIdJson =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    GetElementIdText(targetView.Id));
            ExecuteBrowserScript(
                _chromiumBrowser,
                "if(window.__bimaestroProjectBrowserTheme)" +
                "window.__bimaestroProjectBrowserTheme" +
                $".focusView({viewNameJson},{viewIdJson});");
        }

        public static void TrackActiveView(
            Document document,
            View activeView)
        {
            _activeViewGeneration++;
            _activeViewName = activeView?.Name ?? string.Empty;
            _activeViewElementId = activeView == null
                ? string.Empty
                : GetElementIdText(activeView.Id);
            _activeViewFolderPath = GetActiveViewFolderPath(
                document,
                activeView);
            RefreshViewTypeMap(document, activeView);
            PushActiveViewMarker();
            PushViewTypeMap();
        }

        private static void RefreshViewTypeMap(
            Document document,
            View activeView)
        {
            string activeViewId = activeView == null
                ? string.Empty
                : GetElementIdText(activeView.Id);
            if (ReferenceEquals(_viewTypeMapDocument, document) &&
                string.Equals(
                    _viewTypeMapActiveViewId,
                    activeViewId,
                    StringComparison.Ordinal))
            {
                return;
            }

            var viewTypes = new List<BrowserViewTypeInfo>();
            if (document != null)
            {
                try
                {
                    foreach (View view in new FilteredElementCollector(document)
                                 .OfClass(typeof(View))
                                 .Cast<View>())
                    {
                        string kind = GetBrowserViewKind(view);
                        if (string.IsNullOrEmpty(kind))
                            continue;

                        string id = GetElementIdText(view.Id);
                        if (!string.IsNullOrEmpty(id) ||
                            !string.IsNullOrWhiteSpace(view.Name))
                        {
                            viewTypes.Add(new BrowserViewTypeInfo
                            {
                                Id = id,
                                Name = view.Name ?? string.Empty,
                                Kind = kind
                            });
                        }
                    }
                }
                catch
                {
                    // Le document peut être en cours de fermeture. La dernière
                    // table valide reste alors affichée jusqu'à la vue suivante.
                    return;
                }
            }

            _viewTypeMapDocument = document;
            _viewTypeMapActiveViewId = activeViewId;
            _viewTypeMapJson =
                Newtonsoft.Json.JsonConvert.SerializeObject(viewTypes);
        }

        private static string GetBrowserViewKind(View view)
        {
            if (view == null || view is ViewSheet)
                return string.Empty;

            string viewType = view.ViewType.ToString();
            switch (viewType)
            {
                case "FloorPlan":
                case "CeilingPlan":
                case "EngineeringPlan":
                case "AreaPlan":
                    return "plan";
                case "Section":
                case "Detail":
                    return "section";
                case "ThreeD":
                case "Walkthrough":
                case "Rendering":
                    return "three-d";
                case "Elevation":
                    return "elevation";
                case "Schedule":
                case "ColumnSchedule":
                case "PanelSchedule":
                case "Report":
                    return "schedule";
                case "ProjectBrowser":
                case "SystemBrowser":
                case "Internal":
                case "DrawingSheet":
                case "Undefined":
                    return string.Empty;
                default:
                    return "other";
            }
        }

        public static void SetViewHoverPreviewStale(
            string viewId,
            string viewName,
            bool isStale)
        {
            string safeId = viewId ?? string.Empty;
            string safeName = viewName ?? string.Empty;
            string key = !string.IsNullOrWhiteSpace(safeId)
                ? "id:" + safeId
                : "name:" + safeName;
            if (!ViewHoverPreviews.TryGetValue(
                    key,
                    out ViewHoverPreviewInfo preview) ||
                preview == null)
            {
                return;
            }

            preview.IsStale = isStale;
            if (!string.IsNullOrWhiteSpace(safeName))
                preview.ViewName = safeName;
            if (_chromiumBrowser != null)
            {
                EnsureViewHoverPreviewScript(_chromiumBrowser);
                PushViewHoverPreview(_chromiumBrowser, preview);
            }
            if (string.Equals(
                    _visibleViewHoverKey,
                    key,
                    StringComparison.OrdinalIgnoreCase) &&
                _viewHoverPopup?.IsOpen == true)
            {
                ApplyViewHoverBorder(preview);
                UpdateViewHoverCaption(preview);
            }
        }

        public static void CompleteAutomaticFocusNavigation()
        {
            ClearAutomaticFocus();
        }

        private static IReadOnlyList<string> GetActiveViewFolderPath(
            Document document,
            View activeView)
        {
            if (document == null ||
                activeView == null ||
                activeView is ViewSheet ||
                activeView.IsTemplate)
            {
                return Array.Empty<string>();
            }

            List<FolderItemInfo> folderItems = null;
            try
            {
                BrowserOrganization organization =
                    BrowserOrganization
                        .GetCurrentBrowserOrganizationForViews(document);
                if (organization == null ||
                    !organization.AreFiltersSatisfied(activeView.Id))
                {
                    return Array.Empty<string>();
                }

                folderItems = organization
                    .GetFolderItems(activeView.Id)
                    ?.Where(item => item != null)
                    .ToList();
                if (folderItems == null)
                    return Array.Empty<string>();

                return folderItems
                    .Select(item => item.Name?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList()
                    .AsReadOnly();
            }
            catch
            {
                return Array.Empty<string>();
            }
            finally
            {
                if (folderItems != null)
                {
                    foreach (FolderItemInfo item in folderItems)
                    {
                        try
                        {
                            item.Dispose();
                        }
                        catch
                        {
                            // L'information peut déjà avoir été libérée
                            // par une autre version de l'API Revit.
                        }
                    }
                }
            }
        }

        private static string GetElementIdText(ElementId elementId)
        {
            if (elementId == null)
                return string.Empty;

            foreach (string propertyName in new[]
                     {
                         "Value",
                         "IntegerValue"
                     })
            {
                try
                {
                    System.Reflection.PropertyInfo valueProperty =
                        elementId.GetType().GetProperty(propertyName);
                    object value = valueProperty?.GetValue(elementId);
                    if (value != null)
                    {
                        return Convert.ToString(
                            value,
                            System.Globalization
                                .CultureInfo.InvariantCulture);
                    }
                }
                catch
                {
                    // La propriété dépend de la génération de l'API Revit.
                }
            }

            return elementId.ToString() ?? string.Empty;
        }

        private static void PushActiveViewMarker()
        {
            if (_chromiumBrowser == null)
                return;

            ProjectBrowserColorSettings settings =
                ProjectBrowserColorPreferences.Load();
            if (!settings.IsActiveViewParentHighlightEnabled)
            {
                ExecuteBrowserScript(
                    _chromiumBrowser,
                    "if(window.__bimaestroProjectBrowserTheme)" +
                    "window.__bimaestroProjectBrowserTheme" +
                    ".clearActiveView();");
                return;
            }

            string viewNameJson =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    _activeViewName);
            string pathJson =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    _activeViewFolderPath);
            string viewIdJson =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    _activeViewElementId);
            string generationJson =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    _activeViewGeneration);
            ExecuteBrowserScript(
                _chromiumBrowser,
                "if(window.__bimaestroProjectBrowserTheme)" +
                "window.__bimaestroProjectBrowserTheme" +
                $".setActiveView(" +
                $"{viewNameJson},{pathJson},{viewIdJson}," +
                $"{generationJson});");
        }

        private static void PushViewTypeMap()
        {
            if (_chromiumBrowser == null)
                return;

            ExecuteBrowserScript(
                _chromiumBrowser,
                "if(window.__bimaestroProjectBrowserTheme)" +
                "window.__bimaestroProjectBrowserTheme" +
                $".setViewTypes({_viewTypeMapJson});");
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
            EnsureViewHoverPollTimer(root);
        }

        private static void EnsureViewHoverPollTimer(FrameworkElement root)
        {
            if (_viewHoverPollTimer != null &&
                _viewHoverPollTimer.Dispatcher == root.Dispatcher)
            {
                return;
            }

            _viewHoverPollTimer?.Stop();
            _viewHoverPollTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                root.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _viewHoverPollTimer.Tick += (_, __) =>
                PollViewHoverPreview();
            _viewHoverPollTimer.Start();
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
                EnsureViewHoverPreviewScript(browser);
                if (!ReferenceEquals(_viewHoverPreviewBrowser, browser))
                {
                    _viewHoverPreviewBrowser = browser;
                    PushViewHoverPreviews(browser);
                }
                PushActiveViewMarker();
                PushViewTypeMap();
                break;
            }

            if (!_settings.IsEnabled)
                return;

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
                    SetTrackedValue(
                        textBlock,
                        TextBlock.ForegroundProperty,
                        TextBrush);
                }
                else if (element is HeaderedItemsControl itemsControl &&
                         itemsControl.Header is string header)
                {
                    SetTrackedValue(
                        itemsControl,
                        System.Windows.Controls.Control.ForegroundProperty,
                        TextBrush);
                }
                else if (element is ContentControl contentControl &&
                         contentControl.Content is string content)
                {
                    SetTrackedValue(
                        contentControl,
                        System.Windows.Controls.Control.ForegroundProperty,
                        TextBrush);
                }
            }
        }

        private static string CreateBrowserThemeScript(
            ProjectBrowserColorSettings settings)
        {
            string categoryRulesJson =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    settings.CategoryColorRules
                        .Where(rule =>
                            !string.IsNullOrWhiteSpace(rule.CategoryName))
                        .Select(rule => new
                        {
                            name = rule.CategoryName.Trim(),
                            color = ToCssColor(rule.Color)
                        }));
            string script = @"
(()=>{
  const key='__bimaestroProjectBrowserTheme';
  const version=24;
  const theme={
    appearanceEnabled:__BIMAESTRO_BROWSER_APPEARANCE_ENABLED__,
    activeParentEnabled:__BIMAESTRO_ACTIVE_PARENT_ENABLED__,
    viewTypeEnabled:__BIMAESTRO_VIEW_TYPE_ENABLED__,
    viewParentEnabled:__BIMAESTRO_VIEW_PARENT_ENABLED__,
    categoryEnabled:__BIMAESTRO_CATEGORY_ENABLED__,
    categoryRules:__BIMAESTRO_CATEGORY_RULES__,
    viewColorTarget:'__BIMAESTRO_VIEW_COLOR_TARGET__',
    background:'__BIMAESTRO_BROWSER_BACKGROUND__',
    text:'__BIMAESTRO_BROWSER_TEXT__',
    accent:'__BIMAESTRO_BROWSER_ACCENT__',
    activeParent:'__BIMAESTRO_ACTIVE_PARENT_COLOR__',
    focusBackground:'__BIMAESTRO_BROWSER_FOCUS__',
    mode:'__BIMAESTRO_BROWSER_MODE__',
    viewColors:{
      plan:'__BIMAESTRO_PLAN_VIEW_COLOR__',
      section:'__BIMAESTRO_SECTION_VIEW_COLOR__',
      'three-d':'__BIMAESTRO_3D_VIEW_COLOR__',
      elevation:'__BIMAESTRO_ELEVATION_VIEW_COLOR__',
      schedule:'__BIMAESTRO_SCHEDULE_VIEW_COLOR__',
      other:'__BIMAESTRO_OTHER_VIEW_COLOR__'
    }
  };
  if(window[key]&&window[key].version!==version){
    const previous=window[key];
    if(previous.dispose)previous.dispose();
    else{
      if(previous.observer)previous.observer.disconnect();
      if(previous.clearFocus)previous.clearFocus();
    }
    document.querySelectorAll(
      '[data-bimaestro-arc-star],'+
      '[data-bimaestro-arc-lock],'+
      '[data-bimaestro-arc-group],'+
      '[data-bimaestro-active-parent],'+
      '[data-bimaestro-view-kind],'+
      '[data-bimaestro-view-text],'+
      '[data-bimaestro-focus-overlay],'+
      '[data-bimaestro-focus-target],'+
      '[data-bimaestro-bubble-surface]')
      .forEach(el=>{
        if(el.hasAttribute('data-bimaestro-focus-overlay')){
          el.remove();
          return;
        }
        el.removeAttribute('data-bimaestro-arc-star');
        el.removeAttribute('data-bimaestro-arc-lock');
        el.removeAttribute('data-bimaestro-arc-group');
        el.removeAttribute('data-bimaestro-active-parent');
        el.removeAttribute('data-bimaestro-view-kind');
        el.removeAttribute('data-bimaestro-view-parent');
        el.removeAttribute('data-bimaestro-selected');
        el.removeAttribute('data-bimaestro-color-target');
        if(el.hasAttribute('data-bimaestro-view-text'))
          el.style.removeProperty('color');
        el.removeAttribute('data-bimaestro-view-text');
        el.style.removeProperty('--bimaestro-view-color');
        el.removeAttribute('data-bimaestro-focus-target');
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
    const state={disposed:false};
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
          100%{
            background-position:
              168px -138px,
              -150px -182px,
              216px -92px,
              -168px -160px;
          }
        }
        @keyframes bimaestroWaveDrift{
          0%{background-position:0 22px,96px 112px;}
          100%{background-position:240px 22px,-224px 112px;}
        }
        @keyframes bimaestroRibbonDrift{
          0%{background-position:0 0;}
          100%{background-position:480px 0;}
        }
        @keyframes bimaestroTopographyDrift{
          0%{background-position:0 0;}
          100%{background-position:360px 240px;}
        }
        @keyframes bimaestroArchitectGridDrift{
          0%{background-position:0 0,0 0,0 0,0 0;}
          100%{background-position:100px 100px,100px 100px,100px 100px,100px 100px;}
        }
        @keyframes bimaestroNorthernLightsDrift{
          0%{background-position:0% 45%,100% 55%,45% 100%;}
          50%{background-position:100% 58%,0% 42%,58% 0%;}
          100%{background-position:0% 45%,100% 55%,45% 100%;}
        }
        @keyframes bimaestroConstellationDrift{
          0%{background-position:0 0;}
          100%{background-position:320px -220px;}
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
            url('data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 width=%22240%22 height=%2260%22 viewBox=%220 0 240 60%22%3E%3Cpath d=%22M0 30 C15 8 45 8 60 30 S105 52 120 30 S165 8 180 30 S225 52 240 30%22 fill=%22none%22 stroke=%22%23A6D3F1%22 stroke-opacity=%22.46%22 stroke-width=%223%22 stroke-linecap=%22round%22/%3E%3C/svg%3E'),
            url('data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 width=%22320%22 height=%2280%22 viewBox=%220 0 320 80%22%3E%3Cpath d=%22M0 40 C20 12 60 12 80 40 S140 68 160 40 S220 12 240 40 S300 68 320 40%22 fill=%22none%22 stroke=%22%23F4B1CD%22 stroke-opacity=%22.38%22 stroke-width=%223%22 stroke-linecap=%22round%22/%3E%3C/svg%3E');
          background-size:240px 60px,320px 80px;
          background-repeat:repeat;
          background-attachment:fixed;
          animation:bimaestroWaveDrift 16s linear infinite;
        }
        [data-bimaestro-bubble-surface='ribbons']{
          background-image:
            url('data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 width=%22480%22 height=%22180%22 viewBox=%220 0 480 180%22%3E%3Cpath d=%22M0 72 C120 -8 360 152 480 72%22 fill=%22none%22 stroke=%22%23A6D3F1%22 stroke-opacity=%22.24%22 stroke-width=%2224%22 stroke-linecap=%22round%22/%3E%3Cpath d=%22M0 118 C120 38 360 198 480 118%22 fill=%22none%22 stroke=%22%23F4B1CD%22 stroke-opacity=%22.20%22 stroke-width=%2218%22 stroke-linecap=%22round%22/%3E%3Cpath d=%22M0 28 C120 -52 360 108 480 28%22 fill=%22none%22 stroke=%22%23BFEED3%22 stroke-opacity=%22.18%22 stroke-width=%2212%22 stroke-linecap=%22round%22/%3E%3C/svg%3E');
          background-size:480px 180px;
          background-repeat:repeat;
          background-attachment:fixed;
          animation:bimaestroRibbonDrift 24s linear infinite;
        }
        [data-bimaestro-bubble-surface='topography']{
          background-image:
            url('data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 width=%22360%22 height=%22240%22 viewBox=%220 0 360 240%22%3E%3Cg fill=%22none%22 stroke=%22%2388B8C9%22 stroke-opacity=%22.30%22 stroke-width=%221.4%22%3E%3Cpath d=%22M-20 66 C38 16 94 22 132 55 S211 100 258 58 S332 18 382 52%22/%3E%3Cpath d=%22M-24 91 C31 42 88 45 126 77 S211 123 266 83 S335 46 386 76%22/%3E%3Cpath d=%22M-18 121 C36 75 83 72 119 103 S203 153 268 113 S337 77 382 102%22/%3E%3Cpath d=%22M-25 153 C24 112 76 103 114 132 S204 181 272 144 S339 110 385 132%22/%3E%3Cpath d=%22M-18 188 C29 150 75 140 119 166 S211 210 278 176 S340 145 382 166%22/%3E%3C/g%3E%3C/svg%3E');
          background-size:360px 240px;
          background-repeat:repeat;
          background-attachment:fixed;
          animation:bimaestroTopographyDrift 34s linear infinite;
        }
        [data-bimaestro-bubble-surface='architect-grid']{
          background-image:
            linear-gradient(rgba(115,151,170,.13) 1px,transparent 1px),
            linear-gradient(90deg,rgba(115,151,170,.13) 1px,transparent 1px),
            linear-gradient(rgba(85,126,148,.22) 1px,transparent 1px),
            linear-gradient(90deg,rgba(85,126,148,.22) 1px,transparent 1px);
          background-size:20px 20px,20px 20px,100px 100px,100px 100px;
          background-repeat:repeat;
          background-attachment:fixed;
          animation:bimaestroArchitectGridDrift 30s linear infinite;
        }
        [data-bimaestro-bubble-surface='northern-lights']{
          background-image:
            radial-gradient(ellipse at 18% 45%,rgba(95,225,190,.30),transparent 54%),
            radial-gradient(ellipse at 78% 52%,rgba(116,180,255,.28),transparent 58%),
            radial-gradient(ellipse at 52% 82%,rgba(208,150,255,.24),transparent 56%);
          background-size:180% 180%,220% 220%,190% 210%;
          background-repeat:no-repeat;
          animation:bimaestroNorthernLightsDrift 20s ease-in-out infinite;
        }
        [data-bimaestro-bubble-surface='constellation']{
          background-image:
            url('data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 width=%22320%22 height=%22220%22 viewBox=%220 0 320 220%22%3E%3Cg fill=%22none%22 stroke=%22%2390B7D4%22 stroke-opacity=%22.25%22 stroke-width=%221%22%3E%3Cpath d=%22M22 42 L82 76 L138 34 L204 92 L286 54 M82 76 L112 154 L204 92 L264 172 M112 154 L48 190%22/%3E%3C/g%3E%3Cg fill=%22%23A9CCE3%22 fill-opacity=%22.62%22%3E%3Ccircle cx=%2222%22 cy=%2242%22 r=%222.5%22/%3E%3Ccircle cx=%2282%22 cy=%2276%22 r=%223%22/%3E%3Ccircle cx=%22138%22 cy=%2234%22 r=%222%22/%3E%3Ccircle cx=%22204%22 cy=%2292%22 r=%223.2%22/%3E%3Ccircle cx=%22286%22 cy=%2254%22 r=%222.2%22/%3E%3Ccircle cx=%22112%22 cy=%22154%22 r=%222.6%22/%3E%3Ccircle cx=%22264%22 cy=%22172%22 r=%222.8%22/%3E%3Ccircle cx=%2248%22 cy=%22190%22 r=%222%22/%3E%3C/g%3E%3C/svg%3E');
          background-size:320px 220px;
          background-repeat:repeat;
          background-attachment:fixed;
          animation:bimaestroConstellationDrift 38s linear infinite;
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
        [data-bimaestro-active-parent]{
          color:${theme.activeParent} !important;
          font-weight:700 !important;
        }
        [data-bimaestro-active-parent]::after{
          content:' ★';
          color:${theme.activeParent};
          font-size:.82em;
          font-weight:700;
          margin-left:4px;
          text-shadow:0 0 1px rgba(255,255,255,.9);
        }
        [data-bimaestro-view-kind][data-bimaestro-color-target='background']{
          background-color:var(--bimaestro-view-color);
          box-shadow:inset 3px 0 0 rgba(20,30,45,.20);
          border-radius:3px;
          box-sizing:border-box;
        }
        [data-bimaestro-view-kind][data-bimaestro-color-target='text']{
          color:var(--bimaestro-view-color) !important;
          font-weight:600;
        }
        [data-bimaestro-view-parent]{
          font-weight:600;
        }
        [data-bimaestro-view-kind][data-bimaestro-selected]{
          color:${theme.text} !important;
          background-color:rgba(25,174,232,.78) !important;
          background-image:linear-gradient(
            rgba(25,174,232,.78),
            rgba(25,174,232,.78)) !important;
          box-shadow:
            inset 4px 0 0 rgba(0,89,170,.92),
            inset 0 0 0 1px rgba(0,122,204,1) !important;
        }
        @media (prefers-reduced-motion:reduce){
          [data-bimaestro-bubble-surface='bubbles'],
          [data-bimaestro-bubble-surface='waves'],
          [data-bimaestro-bubble-surface='ribbons'],
          [data-bimaestro-bubble-surface='topography'],
          [data-bimaestro-bubble-surface='architect-grid'],
          [data-bimaestro-bubble-surface='northern-lights'],
          [data-bimaestro-bubble-surface='constellation'],
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
    let viewTypesById=new Map();
    let viewTypesByName=new Map();
    let clickedRowId='';
    let clickedRowName='';
    const paint=()=>{
      if(state.disposed)return;
      if(theme.appearanceEnabled){
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
              theme.text,
              'important');
          }
        });
      }
      paintViewTypes();
      refreshActiveHighlight();
      renderActiveParent();
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
    const restoreHighlight=highlight=>{
      if(!highlight)return;
      if(highlight.overlay)
        highlight.overlay.remove();
      if(!highlight.host)return;
      highlight.host.removeAttribute(
        'data-bimaestro-focus-target');
      if(highlight.label&&highlight.label!==highlight.host)
        highlight.label.removeAttribute(
          'data-bimaestro-focus-target');
    };
    const removeActiveHighlight=()=>{
      if(!activeHighlight)return;
      const previousHighlight=activeHighlight;
      activeHighlight=null;
      restoreHighlight(previousHighlight);
    };
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
      removeActiveHighlight();
      const search=findSearchInput();
      if(search&&automaticSearchValue!==null&&
         search.value===automaticSearchValue)
        setInputValue(search,'');
      automaticSearchValue=null;
      if(!state.disposed)paint();
    };
    const scheduleClearFocus=delay=>{
      cancelScheduledClear();
      const safeDelay=Math.max(100,Number(delay)||300);
      clearFocusTimer=setTimeout(()=>{
        clearFocusTimer=null;
        clearFocus();
      },safeDelay);
    };
    const positionHighlight=highlight=>{
      if(!highlight||!highlight.overlay)return false;
      if(!highlight.host||
         !highlight.host.isConnected){
        highlight.overlay.style.display='none';
        return false;
      }
      const rect=highlight.host.getBoundingClientRect();
      if(rect.width<=0||
         rect.height<=0||
         rect.bottom<=0||
         rect.top>=window.innerHeight){
        highlight.overlay.style.display='none';
        return false;
      }
      const overlay=highlight.overlay;
      overlay.style.display='block';
      overlay.style.left='2px';
      overlay.style.width='calc(100vw - 18px)';
      overlay.style.top=`${Math.max(0,rect.top-2)}px`;
      overlay.style.height=
        `${Math.max(22,rect.height+4)}px`;
      return true;
    };
    const highlightLabel=row=>{
      if(!row)return false;
      removeActiveHighlight();
      const label=row.label||row.element||row;
      let host=row.host||label;
      host.scrollIntoView({
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
      overlay.style.boxSizing='border-box';
      overlay.style.border=`2px solid ${theme.accent}`;
      overlay.style.borderRadius='4px';
      overlay.style.background=theme.focusBackground;
      overlay.style.pointerEvents='none';
      overlay.style.zIndex='2147483646';
      (document.body||document.documentElement)
        .appendChild(overlay);
      const currentHighlight={
        overlay,
        host,
        label,
        elementId:row.id||''
      };
      activeHighlight=currentHighlight;
      positionHighlight(currentHighlight);
      setTimeout(()=>{
        if(activeHighlight!==currentHighlight)return;
        activeHighlight=null;
        restoreHighlight(currentHighlight);
      },6000);
      return true;
    };
    const normalizeLabel=value=>(value||'')
      .replace(/[\u200B-\u200D\uFEFF]/g,'')
      .replace(/\s+/g,' ')
      .trim()
      .toLocaleLowerCase();
    const isSelectedElement=element=>{
      if(!element)return false;
      if(element.getAttribute&&(
         element.getAttribute('aria-selected')==='true'||
         element.getAttribute('aria-current')==='true'))
        return true;
      const className=typeof element.className==='string'
        ?element.className
        :(element.className&&element.className.baseVal)||'';
      return /selected/i.test(className)&&
             !/selectable/i.test(className);
    };
    const getProjectBrowserRows=()=>{
      const wraps=Array.from(document.querySelectorAll(
        '[class*=""vTree_treeItemWrap""]'));
      const rows=wraps
        .map(wrap=>{
          const host=wrap.parentElement||wrap;
          const treeItem=wrap.querySelector(
            '[role=""treeitem""]');
          const input=wrap.querySelector('input');
          const rawName=input?input.value:wrap.textContent;
          const name=normalizeLabel(rawName);
          if(!name)return null;
          const style=getComputedStyle(wrap);
          const rect=wrap.getBoundingClientRect();
          const hostRect=host.getBoundingClientRect();
          if(style.display==='none'||
             style.visibility==='hidden'||
             Number(style.opacity)===0||
             rect.width<=0||
             rect.height<=0)
            return null;
          const labelCandidates=Array.from(
            wrap.querySelectorAll('*'))
            .filter(element=>
              element.children.length===0&&
              normalizeLabel(element.textContent)===name)
            .sort((a,b)=>{
              const ar=a.getBoundingClientRect();
              const br=b.getBoundingClientRect();
              return ar.width*ar.height-br.width*br.height;
            });
          const selected=[host,wrap,treeItem]
            .concat(Array.from(wrap.querySelectorAll('*')))
            .some(isSelectedElement);
          return {
            wrap,
            host,
            label:labelCandidates[0]||input||wrap,
            id:treeItem&&treeItem.id
              ?String(treeItem.id)
              :'',
            name,
            top:hostRect.top,
            left:rect.left,
            selected,
            branch:null,
            ancestors:[]
          };
        })
        .filter(Boolean)
        .sort((a,b)=>a.top-b.top||a.left-b.left);
      if(!rows.length)return rows;
      const rootLeft=Math.min(...rows.map(row=>row.left));
      let currentBranch=null;
      const stack=[];
      rows.forEach(row=>{
        if(row.left<=rootLeft+3){
          if(/^(vues|views)(\s*\([^)]*\))?$/.test(row.name))
            currentBranch='views';
          else if(
            /^(feuilles|sheets)(\s*\([^)]*\))?$/.test(
              row.name))
            currentBranch='sheets';
          else currentBranch=null;
        }
        row.branch=currentBranch;
        while(stack.length&&
              row.left<=stack[stack.length-1].left+3)
          stack.pop();
        row.ancestors=stack.slice();
        stack.push(row);
      });
      return rows;
    };
    const clearViewTypeMarkers=()=>{
      document.querySelectorAll('[data-bimaestro-view-text]')
        .forEach(element=>{
          element.removeAttribute('data-bimaestro-view-text');
          if(theme.appearanceEnabled)
            element.style.setProperty('color',theme.text,'important');
          else
            element.style.removeProperty('color');
        });
      document.querySelectorAll('[data-bimaestro-view-kind]')
        .forEach(element=>{
          element.removeAttribute('data-bimaestro-view-kind');
          element.removeAttribute('data-bimaestro-view-parent');
          element.removeAttribute('data-bimaestro-selected');
          element.removeAttribute('data-bimaestro-color-target');
          element.style.removeProperty('--bimaestro-view-color');
        });
    };
    const resolveViewKind=row=>{
      if(!row)return null;
      let kind=row.id?viewTypesById.get(row.id):null;
      if(!kind)kind=viewTypesByName.get(row.name);
      if(!kind){
        for(const [name,candidateKind] of viewTypesByName){
          if(row.name.endsWith(`: ${name}`)||
             row.name.endsWith(`:${name}`)){
            kind=candidateKind;
            break;
          }
        }
      }
      return kind||null;
    };
    const markViewTypeRow=(row,kind,isParent,customColor)=>{
      const color=customColor||
        (kind?theme.viewColors[kind]:null);
      if(!row||!kind||!color)return;
      row.wrap.setAttribute('data-bimaestro-view-kind',kind);
      row.wrap.setAttribute(
        'data-bimaestro-color-target',
        theme.viewColorTarget);
      if(isParent)
        row.wrap.setAttribute('data-bimaestro-view-parent','1');
      if(row.selected||
         (clickedRowId&&row.id===clickedRowId)||
         (clickedRowName&&row.name===clickedRowName))
        row.wrap.setAttribute('data-bimaestro-selected','1');
      row.wrap.style.setProperty('--bimaestro-view-color',color);
      if(theme.viewColorTarget==='text'&&row.label){
        row.label.setAttribute('data-bimaestro-view-text','1');
        const selected=row.wrap.hasAttribute('data-bimaestro-selected');
        row.label.style.setProperty(
          'color',
          selected?theme.text:color,
          'important');
      }
    };
    const getCategoryRule=row=>{
      if(!theme.categoryEnabled||
         !Array.isArray(theme.categoryRules))return null;
      const candidates=[row]
        .concat((row.ancestors||[]).slice().reverse());
      for(const candidate of candidates){
        const rule=theme.categoryRules.find(item=>
          item&&normalizeLabel(item.name)===candidate.name);
        if(rule&&rule.color)return rule;
      }
      return null;
    };
    const paintViewTypes=()=>{
      clearViewTypeMarkers();
      const hasViewTypes=theme.viewTypeEnabled&&
        (viewTypesById.size||viewTypesByName.size);
      const hasCategories=theme.categoryEnabled&&
        Array.isArray(theme.categoryRules)&&
        theme.categoryRules.length;
      if(!hasViewTypes&&!hasCategories)return;
      const rows=getProjectBrowserRows();
      const coloredRows=[];
      rows.filter(row=>row.branch!=='sheets')
        .forEach(row=>{
          const categoryRule=getCategoryRule(row);
          if(categoryRule){
            markViewTypeRow(
              row,
              'category',
              false,
              categoryRule.color);
            return;
          }
          if(!hasViewTypes)return;
          const kind=resolveViewKind(row);
          if(!kind||!theme.viewColors[kind])return;
          coloredRows.push({row,kind});
          markViewTypeRow(row,kind,false);
        });
      if(!hasViewTypes||!theme.viewParentEnabled)return;
      const parentKinds=new Map();
      coloredRows.forEach(item=>{
        const ancestors=item.row.ancestors||[];
        const parent=ancestors.length
          ?ancestors[ancestors.length-1]
          :null;
        if(!parent||parent.branch==='sheets')return;
        if(!parentKinds.has(parent))parentKinds.set(parent,new Set());
        parentKinds.get(parent).add(item.kind);
      });
      parentKinds.forEach((kinds,parent)=>{
        if(kinds.size===1)
          markViewTypeRow(parent,Array.from(kinds)[0],true);
      });
    };
    const setViewTypes=entries=>{
      viewTypesById=new Map();
      viewTypesByName=new Map();
      if(Array.isArray(entries))
        entries.forEach(entry=>{
          if(!entry||typeof entry!=='object')return;
          const id=String(entry.id||'');
          const name=normalizeLabel(entry.name);
          const kind=String(entry.kind||'');
          if(!theme.viewColors[kind])return;
          if(id)viewTypesById.set(id,kind);
          if(name)viewTypesByName.set(name,kind);
        });
      paintViewTypes();
    };
    const findTreeScroller=rows=>{
      const seed=rows&&rows.length
        ?rows[0].host
        :document.querySelector(
           '[class*=""vTree_treeItemWrap""]');
      let candidate=seed?seed.parentElement:null;
      while(candidate&&candidate!==document.body){
        const style=getComputedStyle(candidate);
        const canScroll=
          candidate.scrollHeight>
            candidate.clientHeight+4&&
          /(auto|scroll)/i.test(
            style.overflowY||style.overflow);
        if(canScroll)return candidate;
        candidate=candidate.parentElement;
      }
      return null;
    };
    const findLabel=(name,elementId)=>{
      const target=normalizeLabel(name);
      const id=String(elementId||'');
      const rows=getProjectBrowserRows();
      if(id){
        const exactId=rows.find(
          row=>row.id===id&&row.branch==='views');
        if(exactId)return exactId;
      }
      if(!target)return null;
      const matchesName=row=>
        row.name===target||
        row.name.endsWith(`: ${target}`)||
        row.name.endsWith(`:${target}`);
      return rows.find(
        row=>row.branch==='views'&&matchesName(row))||
        rows.find(
          row=>row.branch!=='sheets'&&matchesName(row))||
        null;
    };
    const refreshActiveHighlight=()=>{
      if(!activeHighlight||
         !activeHighlight.elementId)
        return;
      const row=getProjectBrowserRows().find(
        candidate=>
          candidate.id===activeHighlight.elementId);
      if(!row){
        activeHighlight.host=null;
        activeHighlight.label=null;
        if(activeHighlight.overlay)
          activeHighlight.overlay.style.display='none';
        return;
      }
      activeHighlight.host=row.host;
      activeHighlight.label=row.label;
      positionHighlight(activeHighlight);
    };
    const highlightWhenReady=(
      name,
      elementId,
      sequence,
      attempt)=>{
      if(sequence!==focusSequence)return;
      refreshActiveHighlight();
      const label=findLabel(name,elementId);
      const needsRefresh=
        label&&(
          !activeHighlight||
          activeHighlight.elementId!==label.id||
          activeHighlight.host!==label.host||
          !activeHighlight.host||
          !activeHighlight.host.isConnected);
      if(needsRefresh)highlightLabel(label);
      if(label){
        if(attempt>=12)return;
        setTimeout(
          ()=>highlightWhenReady(
            name,
            elementId,
            sequence,
            attempt+1),
          100);
        return;
      }
      if(attempt>=80)return;
      const rows=getProjectBrowserRows();
      const scroller=findTreeScroller(rows);
      if(scroller){
        const page=Math.max(80,scroller.clientHeight-28);
        if(attempt===0)
          scroller.scrollTop=0;
        else if(
          scroller.scrollTop+scroller.clientHeight<
            scroller.scrollHeight-2)
          scroller.scrollTop=Math.min(
            scroller.scrollHeight-scroller.clientHeight,
            scroller.scrollTop+page);
        else if(attempt>8)return;
      }
      setTimeout(
        ()=>highlightWhenReady(
          name,
          elementId,
          sequence,
          attempt+1),
        60);
    };
    const focusView=(name,elementId)=>{
      if(!name)return;
      cancelScheduledClear();
      const sequence=++focusSequence;
      if(highlightLabel(findLabel(name,elementId))){
        setTimeout(
          ()=>highlightWhenReady(
            name,
            elementId,
            sequence,
            0),
          100);
        return;
      }
      const search=findSearchInput();
      if(!search)return;
      automaticSearchValue=name;
      setInputValue(search,name);
      setTimeout(
        ()=>highlightWhenReady(
          name,
          elementId,
          sequence,
          0),
        100);
    };
    let activeViewName='';
    let activeViewElementId='';
    let activeViewGeneration=0;
    let activeViewPath=[];
    let activeParentNodeId='';
    let activeParentMarker=null;
    let activeParentClickTimer=null;
    let activeParentScrollTimer=null;
    let activeParentRetryTimers=[];
    const restoreStyleProperty=(element,name,snapshot)=>{
      if(!element||!snapshot)return;
      if(snapshot.value)
        element.style.setProperty(
          name,
          snapshot.value,
          snapshot.priority);
      else element.style.removeProperty(name);
    };
    const clearActiveParentMarker=()=>{
      if(!activeParentMarker)return;
      const marker=activeParentMarker;
      activeParentMarker=null;
      if(!marker.element)return;
      marker.element.removeAttribute(
        'data-bimaestro-active-parent');
      restoreStyleProperty(
        marker.element,
        'color',
        marker.color);
      restoreStyleProperty(
        marker.element,
        'font-weight',
        marker.fontWeight);
    };
    const markActiveParent=element=>{
      if(!element){
        clearActiveParentMarker();
        return;
      }
      if(activeParentMarker&&
         activeParentMarker.element===element){
        element.setAttribute(
          'data-bimaestro-active-parent',
          '1');
        element.style.setProperty(
          'color',
          theme.activeParent,
          'important');
        element.style.setProperty(
          'font-weight',
          '700',
          'important');
        return;
      }
      clearActiveParentMarker();
      activeParentMarker={
        element,
        color:{
          value:element.style.getPropertyValue('color'),
          priority:element.style.getPropertyPriority('color')
        },
        fontWeight:{
          value:element.style.getPropertyValue('font-weight'),
          priority:element.style.getPropertyPriority('font-weight')
        }
      };
      element.setAttribute(
        'data-bimaestro-active-parent',
        '1');
      element.style.setProperty(
        'color',
        theme.activeParent,
        'important');
      element.style.setProperty(
        'font-weight',
        '700',
        'important');
    };
    const getVisibleViewRows=()=>getProjectBrowserRows()
      .filter(row=>row.branch!=='sheets');
    const findVisibleActiveParent=()=>{
      if(!activeViewPath.length)return null;
      const rows=getVisibleViewRows();
      if(activeParentNodeId){
        const cachedParent=rows.find(
          row=>row.id===activeParentNodeId);
        if(cachedParent)return cachedParent;
        activeParentNodeId='';
      }
      const normalizedPath=activeViewPath.map(normalizeLabel);
      const normalizedPathSet=new Set(normalizedPath);
      if(activeViewElementId){
        const activeRow=rows.find(
          row=>row.id===activeViewElementId);
        if(activeRow){
          const exactAncestors=activeRow.ancestors
            .filter(row=>
              normalizedPathSet.has(row.name)&&
              row.branch!=='sheets');
          if(exactAncestors.length)
            return exactAncestors[
              exactAncestors.length-1];
        }
      }
      let best=null;
      for(let level=0;level<normalizedPath.length;level++){
        const target=normalizedPath[level];
        if(!target)continue;
        rows
          .filter(row=>row.name===target)
          .forEach(row=>{
            let expectedLevel=level-1;
            let matchedAncestors=0;
            for(let index=row.ancestors.length-1;
                index>=0&&expectedLevel>=0;
                index--){
              if(row.ancestors[index].name===
                 normalizedPath[expectedLevel]){
                matchedAncestors++;
                expectedLevel--;
              }
            }
            const isVerifiedRoot=
              level===0&&row.branch==='views';
            if(!isVerifiedRoot&&matchedAncestors===0)
              return;
            const score=
              matchedAncestors*10000+
              level*1000+
              Math.min(99,row.left);
            if(!best||score>best.score)
              best={row,score};
          });
      }
      return best?best.row:null;
    };
    const renderActiveParent=()=>{
      if(state.disposed||
         !theme.activeParentEnabled||
         !activeViewPath.length){
        clearActiveParentMarker();
        return;
      }
      const parentRow=findVisibleActiveParent();
      if(parentRow){
        activeParentNodeId=parentRow.id||'';
        markActiveParent(parentRow.label);
      }else{
        markActiveParent(null);
      }
    };
    const clearActiveView=()=>{
      activeViewName='';
      activeViewElementId='';
      activeViewGeneration=0;
      activeViewPath=[];
      activeParentNodeId='';
      activeParentRetryTimers.forEach(clearTimeout);
      activeParentRetryTimers=[];
      clearActiveParentMarker();
    };
    const scheduleActiveParentRetries=delays=>{
      activeParentRetryTimers.forEach(clearTimeout);
      activeParentRetryTimers=(delays||[]).map(delay=>
        setTimeout(
          ()=>renderActiveParent(),
          Math.max(0,Number(delay)||0)));
    };
    const setActiveView=(
      name,
      path,
      elementId,
      generation)=>{
      const incomingGeneration=
        Math.max(0,Number(generation)||0);
      if(incomingGeneration<activeViewGeneration)
        return;
      activeViewGeneration=incomingGeneration;
      activeViewName=name||'';
      activeViewElementId=String(elementId||'');
      activeParentNodeId='';
      clearActiveParentMarker();
      activeViewPath=Array.isArray(path)
        ?path.filter(value=>
           typeof value==='string'&&value.trim())
        :[];
      renderActiveParent();
      scheduleActiveParentRetries([70,220,550]);
    };
    const onBrowserClick=event=>{
      const target=event&&event.target;
      const clickedWrap=target&&target.closest
        ?target.closest('[class*=""vTree_treeItemWrap""]')
        :null;
      const clickedRow=clickedWrap
        ?getProjectBrowserRows().find(row=>row.wrap===clickedWrap)
        :null;
      clickedRowId=clickedRow?clickedRow.id:'';
      clickedRowName=clickedRow?clickedRow.name:'';
      if(activeParentClickTimer!==null)
        clearTimeout(activeParentClickTimer);
      activeParentClickTimer=setTimeout(()=>{
        activeParentClickTimer=null;
        paint();
      },90);
      scheduleActiveParentRetries([180,450]);
    };
    const onBrowserScroll=()=>{
      if(activeParentScrollTimer!==null)return;
      activeParentScrollTimer=requestAnimationFrame(()=>{
        activeParentScrollTimer=null;
        refreshActiveHighlight();
        renderActiveParent();
      });
    };
    document.addEventListener('click',onBrowserClick,true);
    document.addEventListener('scroll',onBrowserScroll,true);
    let paintScheduled=false;
    const observer=new MutationObserver(()=>{
      if(state.disposed||paintScheduled)return;
      paintScheduled=true;
      requestAnimationFrame(()=>{
        paintScheduled=false;
        if(state.disposed)return;
        paint();
      });
    });
    observer.observe(
      document.documentElement,
      {
        childList:true,
        characterData:true,
        attributes:true,
        attributeFilter:[
          'aria-selected',
          'aria-current',
          'class'
        ],
        subtree:true
      });
    const dispose=()=>{
      state.disposed=true;
      observer.disconnect();
      cancelScheduledClear();
      if(activeParentClickTimer!==null){
        clearTimeout(activeParentClickTimer);
        activeParentClickTimer=null;
      }
      if(activeParentScrollTimer!==null){
        cancelAnimationFrame(activeParentScrollTimer);
        activeParentScrollTimer=null;
      }
      activeParentRetryTimers.forEach(clearTimeout);
      activeParentRetryTimers=[];
      clearViewTypeMarkers();
      document.removeEventListener(
        'click',
        onBrowserClick,
        true);
      document.removeEventListener(
        'scroll',
        onBrowserScroll,
        true);
      clearActiveParentMarker();
      focusSequence++;
      removeActiveHighlight();
      const search=findSearchInput();
      if(search&&automaticSearchValue!==null&&
         search.value===automaticSearchValue)
        setInputValue(search,'');
      automaticSearchValue=null;
    };
    window[key]={
      version,
      originals,
      observer,
      paint,
      focusView,
      clearFocus,
      scheduleClearFocus,
      setActiveView,
      clearActiveView,
      setViewTypes,
      dispose
    };
  }
  window[key].paint();
})()";
            return script
                .Replace(
                    "__BIMAESTRO_BROWSER_APPEARANCE_ENABLED__",
                    settings.IsEnabled ? "true" : "false")
                .Replace(
                    "__BIMAESTRO_ACTIVE_PARENT_ENABLED__",
                    settings.IsActiveViewParentHighlightEnabled
                        ? "true"
                        : "false")
                .Replace(
                    "__BIMAESTRO_VIEW_TYPE_ENABLED__",
                    settings.IsViewTypeColoringEnabled
                        ? "true"
                        : "false")
                .Replace(
                    "__BIMAESTRO_VIEW_PARENT_ENABLED__",
                    settings.IsViewTypeParentColoringEnabled
                        ? "true"
                        : "false")
                .Replace(
                    "__BIMAESTRO_CATEGORY_ENABLED__",
                    settings.IsCategoryColoringEnabled
                        ? "true"
                        : "false")
                .Replace(
                    "__BIMAESTRO_CATEGORY_RULES__",
                    categoryRulesJson)
                .Replace(
                    "__BIMAESTRO_VIEW_COLOR_TARGET__",
                    string.Equals(
                        settings.ViewColorTarget,
                        "Texte",
                        StringComparison.OrdinalIgnoreCase)
                        ? "text"
                        : "background")
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
                    "__BIMAESTRO_ACTIVE_PARENT_COLOR__",
                    ToCssColor(settings.ActiveViewParentColor))
                .Replace(
                    "__BIMAESTRO_BROWSER_FOCUS__",
                    ToCssFocusBackground(settings.AccentColor))
                .Replace(
                    "__BIMAESTRO_BROWSER_MODE__",
                    GetBrowserBackgroundMode(
                        settings.BackgroundMode))
                .Replace(
                    "__BIMAESTRO_PLAN_VIEW_COLOR__",
                    ToCssColor(settings.PlanViewColor))
                .Replace(
                    "__BIMAESTRO_SECTION_VIEW_COLOR__",
                    ToCssColor(settings.SectionViewColor))
                .Replace(
                    "__BIMAESTRO_3D_VIEW_COLOR__",
                    ToCssColor(settings.ThreeDViewColor))
                .Replace(
                    "__BIMAESTRO_ELEVATION_VIEW_COLOR__",
                    ToCssColor(settings.ElevationViewColor))
                .Replace(
                    "__BIMAESTRO_SCHEDULE_VIEW_COLOR__",
                    ToCssColor(settings.ScheduleViewColor))
                .Replace(
                    "__BIMAESTRO_OTHER_VIEW_COLOR__",
                    ToCssColor(settings.OtherViewColor));
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
                    "Rubans fluides",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "ribbons";
            }

            if (string.Equals(
                    mode,
                    "Courbes topographiques",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "topography";
            }

            if (string.Equals(
                    mode,
                    "Grille d'architecte",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "architect-grid";
            }

            if (string.Equals(
                    mode,
                    "Aurore boréale",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "northern-lights";
            }

            if (string.Equals(
                    mode,
                    "Constellation douce",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "constellation";
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
                    "Dégradé pastel animé",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    mode,
                    "Aurore pastel",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "aurora";
            }

            return "solid";
        }

        private static void EnsureViewHoverPreviewScript(object browser)
        {
            ExecuteBrowserScript(browser, @"
(() => {
  const key='__bimaestroViewHoverPreview';
  if(window[key])return;
  const state={byId:new Map(),byName:new Map(),disposed:false};
  const normalize=value=>(value||'')
    .replace(/[\u200B-\u200D\uFEFF]/g,'')
    .replace(/\s+/g,' ')
    .trim()
    .toLocaleLowerCase();
  let currentKey='';
  let currentRowTop=0;
  let pendingKey='';
  let pendingRowTop=0;
  let pendingTimer=0;
  const hide=()=>{
    if(pendingTimer){
      clearTimeout(pendingTimer);
      pendingTimer=0;
    }
    pendingKey='';
    pendingRowTop=0;
    currentKey='';
    currentRowTop=0;
  };
  const closestTreeRow=element=>{
    if(!element||element.nodeType!==1)return null;
    if(element.matches&&element.matches('[role=""treeitem""]'))
      return element;
    if(!element.closest)return null;
    return element.closest(
      '[role=""treeitem""],' +
      '[class*=""vTree_treeItemWrap""],' +
      '[class*=""treeItemWrap""],' +
      '[class*=""tree-item""]');
  };
  const collectRows=event=>{
    const rows=[];
    const add=element=>{
      const row=closestTreeRow(element);
      if(row&&!rows.includes(row))rows.push(row);
    };
    if(event&&typeof event.composedPath==='function')
      event.composedPath().forEach(add);
    if(event)add(event.target);
    if(document.elementFromPoint&&event)
      add(document.elementFromPoint(event.clientX,event.clientY));
    return rows;
  };
  const namesForRow=row=>{
    const values=[];
    const add=value=>{
      const normalized=normalize(value);
      if(normalized&&!values.includes(normalized))
        values.push(normalized);
    };
    add(row.getAttribute&&row.getAttribute('aria-label'));
    add(row.getAttribute&&row.getAttribute('title'));
    if(row.value!==undefined)add(row.value);
    row.querySelectorAll&&row.querySelectorAll(
      'input,[aria-label],[title]').forEach(element=>{
        if(element.value!==undefined)add(element.value);
        add(element.getAttribute('aria-label'));
        add(element.getAttribute('title'));
      });
    add(row.textContent);
    return values;
  };
  const findNameInfo=name=>{
    if(!name)return null;
    if(state.byName.has(name))
      return {key:state.byName.get(name)};
    let best=null;
    state.byName.forEach((previewKey,knownName)=>{
      if(best||!knownName)return;
      if(name.endsWith(`: ${knownName}`)||
         name.endsWith(` ${knownName}`))
        best={key:previewKey};
    });
    return best;
  };
  const findInfo=event=>{
    const rows=collectRows(event);
    for(const row of rows){
      const idCandidates=[
        row.id,
        row.getAttribute&&row.getAttribute('data-id'),
        row.getAttribute&&row.getAttribute('data-element-id'),
        row.getAttribute&&row.getAttribute('aria-rowindex')
      ].filter(Boolean).map(String);
      for(const id of idCandidates){
        if(state.byId.has(id))
          return {key:state.byId.get(id),row};
        const numeric=id.match(/\d+/g);
        if(numeric){
          for(const token of numeric){
            if(state.byId.has(token))
              return {key:state.byId.get(token),row};
          }
        }
      }
      for(const name of namesForRow(row)){
        const info=findNameInfo(name);
        if(info){info.row=row;return info;}
      }
    }
    return null;
  };
  const onMove=event=>{
    const info=findInfo(event);
    if(!info){hide();return;}
    const rect=info.row&&info.row.getBoundingClientRect
      ?info.row.getBoundingClientRect()
      :null;
    const rowTop=rect&&Number.isFinite(rect.top)?rect.top:0;
    if(currentKey===info.key){
      currentRowTop=rowTop;
      return;
    }
    if(pendingKey===info.key){
      pendingRowTop=rowTop;
      return;
    }
    if(pendingTimer)clearTimeout(pendingTimer);
    pendingKey=info.key;
    pendingRowTop=rowTop;
    pendingTimer=setTimeout(()=>{
      pendingTimer=0;
      if(state.disposed||pendingKey!==info.key)return;
      currentKey=info.key;
      currentRowTop=pendingRowTop;
    },1000);
  };
  const onLeave=()=>hide();
  document.addEventListener('pointermove',onMove,true);
  document.addEventListener('mousemove',onMove,true);
  document.addEventListener('mouseleave',onLeave,true);
  state.setPreview=(id,name)=>{
    const safeId=String(id||'');
    const safeName=normalize(name);
    const previewKey=safeId
      ?`id:${safeId}`
      :`name:${safeName}`;
    if(!safeId&&!safeName)return;
    if(safeId)state.byId.set(safeId,previewKey);
    if(safeName)state.byName.set(safeName,previewKey);
  };
  state.getActiveKey=()=>currentKey
    ?`${currentKey}\t${currentRowTop}`
    :'';
  state.dispose=()=>{
    state.disposed=true;
    hide();
    document.removeEventListener('pointermove',onMove,true);
    document.removeEventListener('mousemove',onMove,true);
    document.removeEventListener('mouseleave',onLeave,true);
    state.byId.clear();
    state.byName.clear();
  };
  window[key]=state;
})() ");
        }

        private static void PushViewHoverPreviews(object browser)
        {
            foreach (ViewHoverPreviewInfo preview
                     in ViewHoverPreviews.Values.ToList())
            {
                PushViewHoverPreview(browser, preview);
            }
        }

        private static void PushViewHoverPreview(
            object browser,
            ViewHoverPreviewInfo preview)
        {
            if (browser == null || preview == null ||
                string.IsNullOrWhiteSpace(preview.DataUri))
            {
                return;
            }

            string idJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                preview.ViewId ?? string.Empty);
            string nameJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                preview.ViewName ?? string.Empty);
            ExecuteBrowserScript(
                browser,
                "if(window.__bimaestroViewHoverPreview)" +
                "window.__bimaestroViewHoverPreview.setPreview(" +
                idJson + "," + nameJson + ");");
        }

        private static void PollViewHoverPreview()
        {
            if (_viewHoverPollPending ||
                _chromiumBrowser == null ||
                _projectBrowserRoot == null ||
                !IsAttachedToWindow(_projectBrowserRoot))
            {
                if (_chromiumBrowser == null)
                    HideViewHoverPopup();
                return;
            }

            _viewHoverPollPending = true;
            if (!EvaluateBrowserScriptAsync(
                    _chromiumBrowser,
                    "window.__bimaestroViewHoverPreview" +
                    "?window.__bimaestroViewHoverPreview.getActiveKey()" +
                    ":'';",
                    value =>
                    {
                        FrameworkElement root = _projectBrowserRoot;
                        Dispatcher dispatcher = root?.Dispatcher;
                        if (dispatcher == null)
                        {
                            _viewHoverPollPending = false;
                            return;
                        }

                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            _viewHoverPollPending = false;
                            ApplyViewHoverResult(value);
                        }));
                    }))
            {
                _viewHoverPollPending = false;
                HideViewHoverPopup();
            }
        }

        private static void ApplyViewHoverResult(object rawValue)
        {
            string result = NormalizeBrowserEvaluationResult(rawValue);
            string[] parts = result.Split(new[] { '\t' }, 2);
            string key = parts.Length > 0 ? parts[0] : string.Empty;

            if (string.IsNullOrWhiteSpace(key) ||
                !ViewHoverPreviews.TryGetValue(
                    key,
                    out ViewHoverPreviewInfo preview) ||
                preview == null)
            {
                _viewHoverEmptyPollCount++;
                if (_viewHoverEmptyPollCount >= 3)
                    HideViewHoverPopup();
                return;
            }

            _viewHoverEmptyPollCount = 0;
            if (string.Equals(
                    _visibleViewHoverKey,
                    key,
                    StringComparison.OrdinalIgnoreCase) &&
                _viewHoverPopup?.IsOpen == true)
            {
                return;
            }

            ShowViewHoverPopup(key, preview);
        }

        private static void ShowViewHoverPopup(
            string key,
            ViewHoverPreviewInfo preview)
        {
            FrameworkElement root = _projectBrowserRoot;
            if (root == null || string.IsNullOrWhiteSpace(preview?.DataUri))
            {
                HideViewHoverPopup();
                return;
            }

            try
            {
                EnsureViewHoverPopup();
                _viewHoverImage.Source = CreateBitmapFromDataUri(
                    preview.DataUri);
                if (_viewHoverImage.Source == null)
                {
                    HideViewHoverPopup();
                    return;
                }
                ApplyViewHoverBorder(preview);
                UpdateViewHoverCaption(preview);

                if (!TryGetProjectBrowserBounds(
                        root,
                        out NativeRect browserBounds,
                        out NativeRect workArea,
                        out NativePoint cursor,
                        out double scaleX,
                        out double scaleY))
                {
                    HideViewHoverPopup();
                    return;
                }

                const double popupWidth = 316;
                const double popupHeight = 260;
                const double gap = 12;
                int popupWidthPixels = (int)Math.Ceiling(
                    popupWidth * scaleX);
                int popupHeightPixels = (int)Math.Ceiling(
                    popupHeight * scaleY);
                int gapPixels = (int)Math.Ceiling(gap * scaleX);
                int marginPixels = (int)Math.Ceiling(6 * scaleX);

                int xPixels = browserBounds.Right + gapPixels;
                if (xPixels + popupWidthPixels >
                    workArea.Right - marginPixels)
                {
                    xPixels = browserBounds.Left -
                              popupWidthPixels - gapPixels;
                }

                xPixels = Math.Max(
                    workArea.Left + marginPixels,
                    Math.Min(
                        xPixels,
                        workArea.Right - popupWidthPixels - marginPixels));
                int yPixels = Math.Max(
                    workArea.Top + marginPixels,
                    Math.Min(
                        cursor.Y - popupHeightPixels / 2,
                        workArea.Bottom - popupHeightPixels - marginPixels));

                _viewHoverPopup.HorizontalOffset = xPixels / scaleX;
                _viewHoverPopup.VerticalOffset = yPixels / scaleY;
                _visibleViewHoverKey = key;
                _viewHoverPopup.IsOpen = true;
                PositionViewHoverPopupNative(xPixels, yPixels);

                root.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() =>
                        PositionViewHoverPopupNative(xPixels, yPixels)));
            }
            catch
            {
                HideViewHoverPopup();
            }
        }

        private static bool TryGetProjectBrowserBounds(
            FrameworkElement browserRoot,
            out NativeRect browserBounds,
            out NativeRect workArea,
            out NativePoint cursor,
            out double scaleX,
            out double scaleY)
        {
            browserBounds = default;
            workArea = default;
            scaleX = 1;
            scaleY = 1;
            if (!GetCursorPos(out cursor))
                return false;

            if (browserRoot == null ||
                browserRoot.ActualWidth < 80 ||
                browserRoot.ActualHeight < 80 ||
                !IsAttachedToWindow(browserRoot))
            {
                return false;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(browserRoot);
            scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
            scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
            System.Windows.Point topLeft = browserRoot.PointToScreen(
                new System.Windows.Point(0, 0));
            browserBounds.Left = (int)Math.Round(topLeft.X);
            browserBounds.Top = (int)Math.Round(topLeft.Y);
            browserBounds.Right = browserBounds.Left +
                (int)Math.Round(browserRoot.ActualWidth * scaleX);
            browserBounds.Bottom = browserBounds.Top +
                (int)Math.Round(browserRoot.ActualHeight * scaleY);

            IntPtr monitor = MonitorFromPoint(cursor, NearestMonitor);
            var monitorInfo = new NativeMonitorInfo
            {
                Size = Marshal.SizeOf(typeof(NativeMonitorInfo))
            };
            if (monitor == IntPtr.Zero ||
                !GetMonitorInfo(monitor, ref monitorInfo))
            {
                return false;
            }

            workArea = monitorInfo.WorkArea;
            return browserBounds.Right > browserBounds.Left &&
                   workArea.Right > workArea.Left;
        }

        private static void EnsureViewHoverPopup()
        {
            if (_viewHoverPopup != null) return;

            _viewHoverImage = new System.Windows.Controls.Image
            {
                Width = 300,
                MaxHeight = 220,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            _viewHoverCaption = new TextBlock
            {
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(90, 90, 90)),
                FontSize = 11,
                TextAlignment = TextAlignment.Center
            };
            var content = new StackPanel();
            content.Children.Add(_viewHoverImage);
            content.Children.Add(_viewHoverCaption);
            _viewHoverBorder = new Border
            {
                Width = 316,
                MaxHeight = 260,
                Padding = new Thickness(7),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(
                    Color.FromRgb(190, 190, 190)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = content,
                IsHitTestVisible = false
            };
            _viewHoverPopup = new System.Windows.Controls.Primitives.Popup
            {
                Placement = System.Windows.Controls.Primitives
                    .PlacementMode.AbsolutePoint,
                AllowsTransparency = true,
                StaysOpen = true,
                IsHitTestVisible = false,
                Child = _viewHoverBorder
            };
            _viewHoverPopup.Opened += (_, __) =>
                MakeViewHoverPopupNonInteractive();
        }

        private static void ApplyViewHoverBorder(
            ViewHoverPreviewInfo preview)
        {
            if (_viewHoverBorder == null) return;
            bool isStale = preview?.IsStale == true;
            _viewHoverBorder.BorderBrush = isStale
                ? new SolidColorBrush(Color.FromRgb(245, 132, 31))
                : new SolidColorBrush(Color.FromRgb(190, 190, 190));
            _viewHoverBorder.BorderThickness = isStale
                ? new Thickness(3)
                : new Thickness(1);
        }

        private static void UpdateViewHoverCaption(
            ViewHoverPreviewInfo preview)
        {
            if (_viewHoverCaption == null || preview == null) return;
            string timestamp = preview.CapturedAtLocal > DateTime.MinValue
                ? preview.CapturedAtLocal.ToString(
                    "dd/MM/yyyy - HH'h'mm",
                    System.Globalization.CultureInfo.InvariantCulture)
                : "Date inconnue";
            _viewHoverCaption.Text = timestamp + " - " +
                (preview.IsStale ? "à actualiser" : "à jour");
            _viewHoverCaption.Foreground = preview.IsStale
                ? new SolidColorBrush(Color.FromRgb(220, 105, 15))
                : new SolidColorBrush(Color.FromRgb(90, 90, 90));
        }

        private static void MakeViewHoverPopupNonInteractive()
        {
            try
            {
                if (!(_viewHoverPopup?.Child is Visual child)) return;
                HwndSource source = PresentationSource.FromVisual(child)
                    as HwndSource;
                if (source == null || source.Handle == IntPtr.Zero) return;

                IntPtr current = GetWindowLongPtr(
                    source.Handle,
                    ExtendedWindowStyleIndex);
                long updated = current.ToInt64() |
                               TransparentWindowStyle |
                               ToolWindowStyle |
                               NoActivateWindowStyle;
                SetWindowLongPtr(
                    source.Handle,
                    ExtendedWindowStyleIndex,
                    new IntPtr(updated));
            }
            catch
            {
                // Le popup reste utilisable même si Windows refuse le style.
            }
        }

        private static void PositionViewHoverPopupNative(int x, int y)
        {
            try
            {
                if (!(_viewHoverPopup?.Child is Visual child)) return;
                HwndSource source = PresentationSource.FromVisual(child)
                    as HwndSource;
                if (source == null || source.Handle == IntPtr.Zero) return;

                SetWindowPos(
                    source.Handle,
                    IntPtr.Zero,
                    x,
                    y,
                    0,
                    0,
                    NoSizeWindowPosition |
                    NoZOrderWindowPosition |
                    NoActivateWindowPosition |
                    ShowWindowPosition);
            }
            catch
            {
                // Le positionnement WPF reste le repli si Windows le refuse.
            }
        }

        private static System.Windows.Media.Imaging.BitmapImage
            CreateBitmapFromDataUri(string dataUri)
        {
            try
            {
                int comma = dataUri?.IndexOf(',') ?? -1;
                if (comma < 0 || comma + 1 >= dataUri.Length)
                    return null;

                byte[] bytes = Convert.FromBase64String(
                    dataUri.Substring(comma + 1));
                using (var stream = new MemoryStream(bytes))
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging
                        .BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void HideViewHoverPopup()
        {
            _visibleViewHoverKey = string.Empty;
            _viewHoverEmptyPollCount = 0;
            if (_viewHoverPopup != null)
                _viewHoverPopup.IsOpen = false;
        }

        private static string NormalizeBrowserEvaluationResult(object value)
        {
            string text = Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture) ??
                string.Empty;
            text = text.Trim();
            if (text.Length >= 2 && text[0] == '"' &&
                text[text.Length - 1] == '"')
            {
                try
                {
                    return Newtonsoft.Json.JsonConvert
                        .DeserializeObject<string>(text) ?? string.Empty;
                }
                catch { }
            }

            return text;
        }

        private static bool EvaluateBrowserScriptAsync(
            object browser,
            string script,
            Action<object> completed)
        {
            return EvaluateBrowserScriptAsync(
                browser,
                script,
                completed,
                new HashSet<object>(),
                0);
        }

        private static bool EvaluateBrowserScriptAsync(
            object browser,
            string script,
            Action<object> completed,
            HashSet<object> visited,
            int depth)
        {
            if (browser == null || depth > 3 || !visited.Add(browser))
                return false;

            try
            {
                Type extensionsType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(assembly =>
                        assembly.GetType(
                            "CefSharp.WebBrowserExtensions",
                            false))
                    .FirstOrDefault(type => type != null);
                System.Reflection.MethodInfo extensionMethod =
                    extensionsType?
                        .GetMethods(
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Static)
                        .Where(candidate => candidate.Name.Equals(
                            "EvaluateScriptAsync",
                            StringComparison.Ordinal))
                        .FirstOrDefault(candidate =>
                        {
                            System.Reflection.ParameterInfo[] parameters =
                                candidate.GetParameters();
                            return parameters.Length >= 2 &&
                                   parameters[0].ParameterType
                                       .IsInstanceOfType(browser) &&
                                   parameters[1].ParameterType ==
                                       typeof(string) &&
                                   parameters.Skip(2).All(parameter =>
                                       parameter.IsOptional);
                        });
                if (extensionMethod != null)
                {
                    object[] arguments = extensionMethod.GetParameters()
                        .Select((parameter, index) =>
                            index == 0
                                ? browser
                                : index == 1
                                    ? (object)script
                                    : Type.Missing)
                        .ToArray();
                    object invocation = extensionMethod.Invoke(
                        null,
                        arguments);
                    if (AttachBrowserEvaluationTask(invocation, completed))
                        return true;
                }
            }
            catch
            {
                // Le navigateur peut ne pas être CefSharp.
            }

            try
            {
                System.Reflection.MethodInfo method = browser.GetType()
                    .GetMethods(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance)
                    .Where(candidate =>
                        candidate.Name.Equals(
                            "EvaluateScriptAsync",
                            StringComparison.Ordinal) ||
                        candidate.Name.Equals(
                            "ExecuteScriptAsync",
                            StringComparison.Ordinal) ||
                        candidate.Name.Equals(
                            "ExecuteJavascript",
                            StringComparison.Ordinal))
                    .OrderBy(candidate =>
                        candidate.Name.Equals(
                            "EvaluateScriptAsync",
                            StringComparison.Ordinal)
                            ? 0
                            : 1)
                    .FirstOrDefault(candidate =>
                    {
                        System.Reflection.ParameterInfo[] parameters =
                            candidate.GetParameters();
                        return parameters.Length >= 1 &&
                               parameters[0].ParameterType == typeof(string) &&
                               parameters.Skip(1).All(parameter =>
                                   parameter.IsOptional);
                    });
                if (method != null)
                {
                    object[] arguments = method.GetParameters()
                        .Select((parameter, index) =>
                            index == 0 ? (object)script : Type.Missing)
                        .ToArray();
                    object invocation = method.Invoke(browser, arguments);
                    if (AttachBrowserEvaluationTask(
                            invocation,
                            completed))
                        return true;
                }
            }
            catch
            {
                // On essaie les contrôles internes du conteneur.
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

                    object nested = property.GetValue(browser, null);
                    if (nested != null &&
                        EvaluateBrowserScriptAsync(
                            nested,
                            script,
                            completed,
                            visited,
                            depth + 1))
                    {
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private static bool AttachBrowserEvaluationTask(
            object invocation,
            Action<object> completed)
        {
            if (!(invocation is System.Threading.Tasks.Task task))
                return false;

            task.ContinueWith(done =>
            {
                object result = null;
                if (!done.IsCanceled && !done.IsFaulted)
                {
                    try
                    {
                        result = done.GetType()
                            .GetProperty("Result")
                            ?.GetValue(done, null);
                        if (result != null)
                        {
                            System.Reflection.PropertyInfo successProperty =
                                result.GetType().GetProperty("Success");
                            if (successProperty != null &&
                                successProperty.GetValue(
                                    result,
                                    null) is bool success &&
                                !success)
                            {
                                result = null;
                            }
                            else
                            {
                                System.Reflection.PropertyInfo valueProperty =
                                    result.GetType().GetProperty("Result");
                                if (valueProperty != null)
                                    result = valueProperty.GetValue(
                                        result,
                                        null);
                            }
                        }
                    }
                    catch { result = null; }
                }

                completed?.Invoke(result);
            });
            return true;
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
                                StringComparison.Ordinal) ||
                            candidate.Name.Equals(
                                "ExecuteJavascript",
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

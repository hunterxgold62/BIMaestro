using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace BIMaestro.Tutorials
{
    // The family exercise finishes in Revit, after the browser has been closed.
    // Keep its little Pikachu card independent of the browser, and locate the
    // native ribbon/menu surfaces anew as Revit opens and closes each popup.
    internal sealed class FamilyRibbonTutorialGuide
    {
        private const int FirstStep = 18;
        private const int KeyboardShortcutStep = 22;
        private readonly IntPtr _revitHandle;
        private readonly DemoStep[] _steps;
        private readonly Action _closed;
        private readonly Window _cardWindow;
        private readonly Window _outlineWindow;
        private readonly TextBlock _title;
        private readonly TextBlock _text;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly DispatcherTimer _timer;
        private int _index;
        private bool _completed;
        private bool _closing;
        private readonly Dictionary<string, Rect?> _automationBounds = new Dictionary<string, Rect?>();
        private readonly Dictionary<string, DateTime> _nextAutomationLookupUtc = new Dictionary<string, DateTime>();
        private readonly HashSet<string> _automationPending = new HashSet<string>();
        private bool _cardMovedByUser;
        private bool _keyboardDialogSeen;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        private const int ExStyle = -20;
        private const long Transparent = 0x20;
        private const long ToolWindow = 0x80;
        private const long NoActivate = 0x08000000;
        private static readonly IntPtr TopMost = new IntPtr(-1);
        private const uint NoActivatePosition = 0x0010;

        internal FamilyRibbonTutorialGuide(IntPtr revitHandle, DemoStep[] steps, int firstIndex, Action closed)
        {
            _revitHandle = revitHandle;
            _steps = steps;
            _index = firstIndex;
            _closed = closed;

            _cardWindow = new Window
            {
                Title = "BIMaestro · Pikachu · Rosace",
                Width = 390,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = Brushes.White,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            var content = new Border
            {
                Padding = new Thickness(14),
                BorderBrush = Brushes.DarkOrange,
                BorderThickness = new Thickness(2),
                Background = Brushes.White
            };
            var stack = new StackPanel();
            content.Child = stack;
            _cardWindow.Content = content;
            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            var sprite = new Image
            {
                Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 9, 0)
            };
            RenderOptions.SetBitmapScalingMode(sprite, BitmapScalingMode.NearestNeighbor);
            heading.Children.Add(sprite);
            _title = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Width = 316,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            heading.Children.Add(_title);
            stack.Children.Add(heading);
            heading.Cursor = System.Windows.Input.Cursors.SizeAll;
            heading.MouseLeftButtonDown += (_, args) =>
            {
                if (args.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;
                _cardMovedByUser = true;
                try { _cardWindow.DragMove(); } catch { }
            };
            _text = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(_text);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            stack.Children.Add(buttons);
            _previous = AddButton(buttons, "Précédent", Previous);
            _next = AddButton(buttons, "Suivant", Next);
            AddButton(buttons, "Quitter", Close);
            _cardWindow.Closed += (_, __) => Close();

            _outlineWindow = new Window
            {
                Width = 20,
                Height = 20,
                Left = -100,
                Top = -100,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Content = new Border
                {
                    BorderBrush = Brushes.DarkOrange,
                    BorderThickness = new Thickness(4),
                    CornerRadius = new CornerRadius(5),
                    Background = Brushes.Transparent
                }
            };
            _outlineWindow.SourceInitialized += (_, __) =>
            {
                IntPtr hwnd = new WindowInteropHelper(_outlineWindow).Handle;
                long style = GetWindowLongPtr(hwnd, ExStyle).ToInt64();
                SetWindowLongPtr(hwnd, ExStyle,
                    new IntPtr(style | Transparent | ToolWindow | NoActivate));
            };

            _timer = new DispatcherTimer(DispatcherPriority.Background, _cardWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(650)
            };
            _timer.Tick += OnTick;
        }

        internal void Show()
        {
            if (_closing) return;
            // Keep the explanation away from the dialog's usual OK/Attribuer
            // area. The heading can be dragged if a custom Revit layout needs it.
            _cardWindow.Left = SystemParameters.WorkArea.Left + 24;
            _cardWindow.Top = SystemParameters.WorkArea.Bottom - 260;
            _cardWindow.Show();
            _cardWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_cardMovedByUser)
                    _cardWindow.Top = Math.Max(SystemParameters.WorkArea.Top + 16,
                        SystemParameters.WorkArea.Bottom - _cardWindow.ActualHeight - 24);
            }), DispatcherPriority.Loaded);
            ShowStep();
            _timer.Start();
        }

        internal void CompleteAction(string action)
        {
            if (_closing || _index < FirstStep || _index >= _steps.Length) return;
            if (_steps[_index].CompletionEvent == "radial-tutorial-chaise-used" && action == "radial-opened")
            {
                _text.Text = "✓ La rosace est ouverte. Clic droit au centre > « Charger une collection » > « Favoris », puis choisis ta chaise étoilée. Pikachu attend son placement.";
                return;
            }
            if (string.Equals(_steps[_index].CompletionEvent, action, StringComparison.Ordinal))
                CompleteStep();
        }

        private static Button AddButton(Panel panel, string label, Action click)
        {
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(5, 0, 0, 0)
            };
            button.Click += (_, __) => click();
            panel.Children.Add(button);
            return button;
        }

        private void Previous()
        {
            if (_index <= FirstStep) return;
            _index--;
            ShowStep();
        }

        private void Next()
        {
            if (!_completed) return;
            if (_index == _steps.Length - 1) { Close(); return; }
            _index++;
            ShowStep();
        }

        private void CompleteStep()
        {
            if (_index == _steps.Length - 1)
            {
                if (_completed) return;
                _completed = true;
                _text.Text = "✓ Pikachu a vérifié le lancement de ta chaise favorite depuis la rosace. Clique dans la vue pour la poser, puis Échap. Ton catalogue personnel et tes favoris sont conservés.";
                _next.IsEnabled = true;
            }
            else
                Next();
        }

        private void ShowStep()
        {
            if (_closing || _index < FirstStep || _index >= _steps.Length) return;
            if (_index == KeyboardShortcutStep)
                _keyboardDialogSeen = false;
            DemoStep step = _steps[_index];
            _title.Text = $"{_index + 1}/{_steps.Length} · {step.Title}";
            _text.Text = step.Text;
            _previous.IsEnabled = _index > FirstStep;
            _next.Content = _index == _steps.Length - 1 ? "Terminer" : "Suivant";
            // Revit's modal shortcut dialog disables this separate guide card.
            // All dialog instructions therefore live in one step, which resumes
            // after the dialog closes. Suivant remains available as a fallback
            // once Revit returns control to the card.
            _completed = _index != _steps.Length - 1;
            _next.IsEnabled = _completed;
            if (_index == _steps.Length - 1 &&
                Famille.FamilyBrowserCommand.uiapp?.ActiveUIDocument == null)
            {
                _text.Text += " Ouvre une maquette avant cet essai ; sinon, quitte le guide et relance-le plus tard.";
            }
            UpdateTarget();
        }

        private void OnTick(object sender, EventArgs args)
        {
            if (_closing) return;
            try
            {
                if (_index == 18 && FindTarget("RevitRosace").HasValue)
                    CompleteStep();
                else if (_index == 20 && IsTabActive("Vue", "View"))
                    CompleteStep();
                else if (_index == 21)
                {
                    // A quick click can open the modal dialog between two ticks,
                    // before the menu item was ever observed on screen.
                    bool dialogOpen = FindAutomationTarget("KeyboardDialog").HasValue;
                    if (dialogOpen || FindTarget("RevitKeyboardShortcuts").HasValue)
                    {
                        CompleteStep();
                        if (dialogOpen && _index == KeyboardShortcutStep)
                            _keyboardDialogSeen = true;
                    }
                }
                else if (_index == KeyboardShortcutStep)
                {
                    bool dialogOpen = FindAutomationTarget("KeyboardDialog").HasValue;
                    if (dialogOpen)
                        _keyboardDialogSeen = true;
                    else if (_keyboardDialogSeen)
                        CompleteStep();
                }
                UpdateTarget();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("BIMaestro family ribbon guide: " + ex.Message);
                HideOutline();
            }
        }

        private static bool IsTabActive(params string[] names)
        {
            string active = Couleur.RevitRibbonCatalog.GetActiveTabTitle();
            return names.Any(name => string.Equals(active, name, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateTarget()
        {
            if (_closing || _index >= _steps.Length) return;
            string target = _steps[_index].Target;
            if (target == "RevitFamilySplit" && !IsTabActive("BIMaestro"))
                target = "RevitBimTab";
            if (_index == KeyboardShortcutStep && _keyboardDialogSeen)
                target = "KeyboardShortcutSearch";
            Rect? bounds = FindTarget(target);
            if (bounds.HasValue)
            {
                PlaceCardAwayFrom(bounds.Value);
                ShowOutline(bounds.Value);
            }
            else HideOutline();
        }

        private void PlaceCardAwayFrom(Rect target)
        {
            if (_cardMovedByUser || !_cardWindow.IsLoaded) return;
            try
            {
                Point origin = _cardWindow.PointToScreen(new Point(0, 0));
                Point end = _cardWindow.PointToScreen(new Point(_cardWindow.ActualWidth,
                    _cardWindow.ActualHeight));
                Rect card = new Rect(origin, end);
                if (!card.IntersectsWith(target)) return;
                _cardWindow.Left = target.Left > card.Width + 48
                    ? SystemParameters.WorkArea.Left + 24
                    : SystemParameters.WorkArea.Right - _cardWindow.ActualWidth - 24;
                if (target.Height > SystemParameters.WorkArea.Height * 0.65)
                    _cardWindow.Top = SystemParameters.WorkArea.Top + 170;
            }
            catch { }
        }

        private Rect? FindTarget(string target)
        {
            if (target == "RevitUseShortcut") return null;
            if (target == "KeyboardShortcutSearch" || target == "KeyboardShortcutAssign" ||
                target == "KeyboardShortcutConfirm")
                return FindAutomationTarget(target) ?? FindAutomationTarget("KeyboardDialog");
            Rect? visual = FindVisualTarget(target);
            return visual ?? FindAutomationTarget(target);
        }

        private Rect? FindVisualTarget(string target)
        {
            FrameworkElement best = null;
            int bestScore = 0;
            foreach (PresentationSource presentation in PresentationSource.CurrentSources)
            {
                if (!(presentation is HwndSource source) || source.RootVisual == null) continue;
                IntPtr handle = source.Handle;
                if (handle == new WindowInteropHelper(_cardWindow).Handle ||
                    handle == new WindowInteropHelper(_outlineWindow).Handle) continue;
                var pending = new Stack<DependencyObject>();
                pending.Push(source.RootVisual);
                int seen = 0;
                while (pending.Count > 0 && seen++ < 25000)
                {
                    DependencyObject node = pending.Pop();
                    if (node is FrameworkElement element && element.IsVisible &&
                        element.ActualWidth >= 8 && element.ActualHeight >= 8)
                    {
                        int score = ScoreVisual(element, target);
                        if (score > 0)
                        {
                            FrameworkElement surface = ClickSurface(element, target);
                            if (score > bestScore && surface.ActualWidth < 600 && surface.ActualHeight < 240)
                            { best = surface; bestScore = score; }
                        }
                    }
                    int count;
                    try { count = VisualTreeHelper.GetChildrenCount(node); }
                    catch { continue; }
                    for (int i = 0; i < count; i++)
                        pending.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
            if (best == null) return null;
            try
            {
                Point start = best.PointToScreen(new Point(0, 0));
                Point end = best.PointToScreen(new Point(best.ActualWidth, best.ActualHeight));
                return new Rect(start, end);
            }
            catch { return null; }
        }

        private static int ScoreVisual(FrameworkElement element, string target)
        {
            string type = element.GetType().Name;
            string name = AutomationProperties.GetName(element) ?? string.Empty;
            string content = (element as ContentControl)?.Content as string ??
                (element as TextBlock)?.Text ?? string.Empty;
            string modelId = GetModelId(element);
            bool tab = type.IndexOf("RibbonTabButton", StringComparison.OrdinalIgnoreCase) >= 0;
            if (target == "RevitBimTab")
                return tab && (Is(name, "BIMaestro") || Is(content, "BIMaestro")) ? 100 : 0;
            if (target == "RevitViewTab")
                return tab && (Is(name, "Vue", "View") || Is(content, "Vue", "View")) ? 100 : 0;
            if (target == "RevitFamilySplit")
                return modelId.IndexOf("FamilyBrowser", StringComparison.OrdinalIgnoreCase) >= 0 ? 110 :
                    type.IndexOf("RibbonSplitButton", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (Is(name, "Famille", "Family") || Is(content, "Famille", "Family")) ? 80 : 0;
            if (target == "RevitRosace")
                return modelId.IndexOf("Rosace", StringComparison.OrdinalIgnoreCase) >= 0 ? 120 :
                    (Is(name, ".") || Is(content, ".")) &&
                    (type.IndexOf("Ribbon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     type.IndexOf("MenuItem", StringComparison.OrdinalIgnoreCase) >= 0) ? 70 : 0;
            if (target == "RevitUserInterface")
                return Contains(name, "Interface utilisateur", "User Interface") ||
                    Contains(content, "Interface utilisateur", "User Interface") ? 90 : 0;
            if (target == "RevitKeyboardShortcuts")
                return Contains(name, "Raccourcis clavier", "Keyboard Shortcuts") ||
                    Contains(content, "Raccourcis clavier", "Keyboard Shortcuts") ? 90 : 0;
            return 0;
        }

        private static string GetModelId(FrameworkElement element)
        {
            foreach (object model in new[] { element.DataContext, element.Tag,
                (element as ContentControl)?.Content, (object)element })
            {
                if (model == null || !(model.GetType().Namespace ?? string.Empty)
                    .StartsWith("Autodesk.Windows", StringComparison.Ordinal)) continue;
                try
                {
                    string id = model.GetType().GetProperty("Id")?.GetValue(model, null) as string;
                    if (!string.IsNullOrEmpty(id)) return id;
                }
                catch { }
            }
            return string.Empty;
        }

        private static FrameworkElement ClickSurface(FrameworkElement element, string target)
        {
            FrameworkElement result = element;
            DependencyObject node = element;
            FrameworkElement firstButton = null;
            for (int i = 0; i < 8 && node != null; i++, node = VisualTreeHelper.GetParent(node))
            {
                if (!(node is FrameworkElement candidate) || !candidate.IsVisible ||
                    candidate.ActualWidth > 600 || candidate.ActualHeight > 240) continue;
                string type = candidate.GetType().Name;
                if (target == "RevitFamilySplit" &&
                    type.IndexOf("RibbonSplitButton", StringComparison.OrdinalIgnoreCase) >= 0)
                    return candidate;
                if (type.IndexOf("RibbonTabButton", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("RibbonButton", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("MenuItem", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.EndsWith("Button", StringComparison.OrdinalIgnoreCase))
                    firstButton ??= candidate;
                result = candidate;
            }
            return firstButton ?? result;
        }

        private Rect? FindAutomationTarget(string target)
        {
            if ((!_nextAutomationLookupUtc.TryGetValue(target, out DateTime next) ||
                 DateTime.UtcNow >= next) && _automationPending.Add(target))
            {
                _nextAutomationLookupUtc[target] = DateTime.UtcNow.AddSeconds(2);
                Task.Run(() => QueryAutomationTarget(target)).ContinueWith(task =>
                {
                    if (_cardWindow.Dispatcher.HasShutdownStarted) return;
                    _cardWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _automationPending.Remove(target);
                        if (_closing) return;
                        _automationBounds[target] = task.Status == TaskStatus.RanToCompletion
                            ? task.Result : null;
                        UpdateTarget();
                    }), DispatcherPriority.Background);
                });
            }
            return _automationBounds.TryGetValue(target, out Rect? bounds) ? bounds : null;
        }

        private Rect? QueryAutomationTarget(string target)
        {
            if (target == "KeyboardDialog") return SafeBounds(FindKeyboardDialog());
            if (target == "KeyboardShortcutSearch" || target == "KeyboardShortcutAssign" ||
                target == "KeyboardShortcutConfirm")
            {
                AutomationElement dialog = FindKeyboardDialog();
                return dialog == null ? null : FindKeyboardField(dialog, target);
            }
            string[] names = target switch
            {
                "RevitBimTab" => new[] { "BIMaestro" },
                "RevitViewTab" => new[] { "Vue", "View" },
                "RevitFamilySplit" => new[] { "Famille", "Family" },
                "RevitRosace" => new[] { "." },
                "RevitUserInterface" => new[] { "Interface utilisateur", "User Interface" },
                "RevitKeyboardShortcuts" => new[] { "Raccourcis clavier", "Keyboard Shortcuts" },
                _ => Array.Empty<string>()
            };
            foreach (string name in names)
            {
                AutomationElement element = FindAutomationByName(name);
                if (element != null) return SafeBounds(element);
            }
            return null;
        }

        private AutomationElement FindAutomationByName(string name)
        {
            try
            {
                var condition = new PropertyCondition(AutomationElement.NameProperty, name,
                    PropertyConditionFlags.IgnoreCase);
                AutomationElement main = AutomationElement.FromHandle(_revitHandle);
                AutomationElement found = main?.FindFirst(TreeScope.Descendants, condition);
                if (IsUsable(found)) return found;
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty,
                        Process.GetCurrentProcess().Id));
                foreach (AutomationElement window in windows)
                {
                    if (window.Current.NativeWindowHandle == _revitHandle.ToInt64()) continue;
                    found = window.FindFirst(TreeScope.Descendants, condition);
                    if (IsUsable(found)) return found;
                }
            }
            catch (Exception ex) { Trace.WriteLine("BIMaestro keyboard guide UIA: " + ex.Message); }
            return null;
        }

        private AutomationElement FindKeyboardDialog()
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty,
                        Process.GetCurrentProcess().Id));
                foreach (AutomationElement window in windows)
                {
                    string title = window.Current.Name ?? string.Empty;
                    if (Contains(title, "Raccourcis clavier", "Keyboard Shortcuts") && IsUsable(window))
                        return window;
                }
            }
            catch { }
            return null;
        }

        private static Rect? FindKeyboardField(AutomationElement dialog, string target)
        {
            string[] names = target == "KeyboardShortcutSearch"
                ? new[] { "Rechercher", "Recherche", "Search" }
                : target == "KeyboardShortcutAssign"
                ? new[] { "Appuyer sur de nouvelles touches", "Press new keys", "Nouvelles touches" }
                : new[] { "Attribuer", "Assign" };
            foreach (string name in names)
            {
                try
                {
                    var condition = new PropertyCondition(AutomationElement.NameProperty, name,
                        PropertyConditionFlags.IgnoreCase);
                    AutomationElement field = dialog.FindFirst(TreeScope.Descendants, condition);
                    if (IsUsable(field)) return SafeBounds(field);
                }
                catch { }
            }
            if (target != "KeyboardShortcutConfirm")
            {
                try
                {
                    var edits = dialog.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    var visible = edits.Cast<AutomationElement>()
                        .Where(IsUsable)
                        .OrderBy(element => element.Current.BoundingRectangle.Top)
                        .ToList();
                    if (visible.Count > 0)
                        return SafeBounds(target == "KeyboardShortcutSearch"
                            ? visible.First() : visible.Last());
                }
                catch { }
            }
            return null;
        }

        private static bool IsUsable(AutomationElement element)
        {
            if (element == null) return false;
            try
            {
                Rect bounds = element.Current.BoundingRectangle;
                return !element.Current.IsOffscreen && bounds.Width >= 8 && bounds.Height >= 8;
            }
            catch { return false; }
        }

        private static Rect? SafeBounds(AutomationElement element)
        {
            if (!IsUsable(element)) return null;
            try { return element.Current.BoundingRectangle; }
            catch { return null; }
        }

        private static bool Is(string value, params string[] options) =>
            options.Any(option => string.Equals(value.Trim(), option, StringComparison.OrdinalIgnoreCase));

        private static bool Contains(string value, params string[] options) =>
            options.Any(option => value.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0);

        private void ShowOutline(Rect bounds)
        {
            if (bounds.Width < 8 || bounds.Height < 8) { HideOutline(); return; }
            if (!_outlineWindow.IsVisible) _outlineWindow.Show();
            IntPtr hwnd = new WindowInteropHelper(_outlineWindow).Handle;
            SetWindowPos(hwnd, TopMost, (int)Math.Floor(bounds.Left) - 4,
                (int)Math.Floor(bounds.Top) - 4,
                (int)Math.Ceiling(bounds.Width) + 8,
                (int)Math.Ceiling(bounds.Height) + 8, NoActivatePosition);
        }

        private void HideOutline()
        {
            if (_outlineWindow.IsVisible) _outlineWindow.Hide();
        }

        internal void Close()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _outlineWindow.Close();
            _cardWindow.Close();
            _closed?.Invoke();
        }
    }
}

using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Licensing;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Couleur
{
    // First-run invitation and the hand-off from the ribbon to the color window.
    internal static class AppearanceOnboarding
    {
        private static readonly string ChoiceFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "AppearanceGuideFirstLaunch.txt");
        private static readonly DateTime ReadyAfterUtc = DateTime.UtcNow.AddSeconds(12);
        private static bool _askedThisSession;
        private static DateTime? _projectReadyUtc;
        private static IntroWindow _intro;
        private static string _introTourId;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        internal static void ProcessIdling(UIApplication app)
        {
            if (_askedThisSession || DateTime.UtcNow < ReadyAfterUtc || app?.MainWindowHandle == IntPtr.Zero)
                return;
            if (File.Exists(ChoiceFile)) { _askedThisSession = true; return; }
            if (app.ActiveUIDocument?.Document?.IsValidObject != true || app.ActiveUIDocument.ActiveView == null)
            {
                _projectReadyUtc = null;
                return;
            }
            if (Path.GetFileName(app.ActiveUIDocument.Document.PathName)
                .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
            {
                _projectReadyUtc = null;
                return;
            }
            if (!_projectReadyUtc.HasValue) _projectReadyUtc = DateTime.UtcNow;
            if (DateTime.UtcNow - _projectReadyUtc.Value < TimeSpan.FromSeconds(8)) return;
            if (GetForegroundWindow() != app.MainWindowHandle) return;
            // Let BIMaestro's existing welcome dialog finish before asking.
            if (Application.Current?.Windows.Cast<Window>().Any(window => window.IsVisible) == true) return;
            _askedThisSession = true;
            try
            {
                var prompt = new TaskDialog("BIMaestro")
                {
                    MainInstruction = "Veux-tu être guidé dans la personnalisation des couleurs ?",
                    MainContent = "Pikachu te montrera le bouton Couleurs, puis les réglages dans la fenêtre Apparence BIMaestro. Tu pourras quitter le guide à tout moment.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                TaskDialogResult answer = prompt.Show();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile));
                    File.WriteAllText(ChoiceFile, answer == TaskDialogResult.Yes ? "Accepted" : "Declined");
                }
                catch (IOException ex)
                {
                    System.Diagnostics.Trace.WriteLine("BIMaestro appearance guide choice: " + ex.Message);
                }
                if (answer == TaskDialogResult.Yes) StartIntro(app.MainWindowHandle);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("BIMaestro appearance onboarding: " + ex);
            }
        }

        internal static void StartIntro(IntPtr owner, string tourId = "colors")
        {
            _intro?.Close();
            _introTourId = tourId;
            _intro = new IntroWindow(owner, tourId);
            IntroWindow current = _intro;
            current.Closed += (_, __) =>
            {
                if (ReferenceEquals(_intro, current)) { _intro = null; _introTourId = null; }
            };
            _intro.Show();
        }

        internal static bool ConsumeColorClick() => ConsumeTourClick("colors");

        internal static bool ConsumeTourClick(string tourId)
        {
            if (_intro == null || _introTourId != tourId) return false;
            IntroWindow active = _intro;
            _intro = null;
            _introTourId = null;
            active.Close();
            return true;
        }

        internal static void Shutdown()
        {
            _intro?.Close();
            _intro = null;
            _introTourId = null;
        }

        private sealed class IntroWindow : Window
        {
            private readonly IntPtr _owner;
            private readonly string _tourId;
            private readonly string _buttonId;
            private readonly string _buttonLabel;
            private readonly string _menuId;
            private readonly DispatcherTimer _timer;
            private readonly TextBlock _hint;
            private AdornerLayer _layer;
            private RibbonPointer _pointer;
            private FrameworkElement _target;

            internal IntroWindow(IntPtr owner, string tourId)
            {
                _owner = owner;
                _tourId = tourId;
                _buttonId = tourId == "reservation" ? "ResérvationAuto" :
                    tourId == "history" ? "Qui a fait ça ?" :
                    tourId == "pipe-calculation" ? "PipeLengthByDiameterV2" :
                    tourId == "organizer" ? "ElementRenamerButton" :
                    tourId == "view-template" ? "ViewTemplateTransfer" :
                    tourId == "family-browser" ? "FamilyBrowser" :
                    tourId == "excel" ? "GestionExcelCmd" : "Couleur de projet";
                _buttonLabel = tourId == "reservation" ? "Auto Réservation" :
                    tourId == "history" ? "Qui a fait ça ?" :
                    tourId == "pipe-calculation" ? "Calcul des canalisations" :
                    tourId == "organizer" ? "Organisateur" :
                    tourId == "view-template" ? "Gabarit de vue" :
                    tourId == "family-browser" ? "Navigateur de familles" :
                    tourId == "excel" ? "Gestion Excel" : "Couleurs";
                _menuId = tourId == "colors" ? "Changement de couleur" :
                    tourId == "organizer" ? "OrganisateurSplit" : null;
                Title = "BIMaestro — Guide " + _buttonLabel;
                Width = 330; Height = 230;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = SystemParameters.WorkArea.Right - Width - 30;
                Top = SystemParameters.WorkArea.Bottom - Height - 30;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                Background = Brushes.White;
                new WindowInteropHelper(this).Owner = owner;
                var layout = new StackPanel { Margin = new Thickness(18) };
                Content = layout;
                var sprite = new Image { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Left,
                    Source = RibbonPanelColorScheme.CreateCompanionImage() };
                RenderOptions.SetBitmapScalingMode(sprite, BitmapScalingMode.NearestNeighbor);
                layout.Children.Add(sprite);
                layout.Children.Add(new TextBlock { Text = "Parcours : " + _buttonLabel, FontSize = 17,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) });
                _hint = new TextBlock { Text = "Clique d’abord sur l’onglet BIMaestro. Pikachu te montrera ensuite " + _buttonLabel + ".",
                    TextWrapping = TextWrapping.Wrap, MinHeight = 58 };
                layout.Children.Add(_hint);
                var quit = new Button { Content = "Quitter le guide", HorizontalAlignment = HorizontalAlignment.Right,
                    Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 8, 0, 0) };
                quit.Click += (_, __) => Close();
                layout.Children.Add(quit);
                _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(700) };
                _timer.Tick += LocateColorButton;
                _timer.Start();
                Closed += (_, __) => { _timer.Stop(); _timer.Tick -= LocateColorButton; RemovePointer(); };
            }

            private void LocateColorButton(object sender, EventArgs args)
            {
                try
                {
                    DependencyObject root = HwndSource.FromHwnd(_owner)?.RootVisual;
                    if (root == null) return;
                    string commandId = AppUI.GetRibbonButtonById(_buttonId)?.PushButton
                        ?.GetType().GetMethod("getId", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.Invoke(AppUI.GetRibbonButtonById(_buttonId).PushButton, null) as string;
                    bool bimTabSelected = string.Equals(RevitRibbonCatalog.GetActiveTabTitle(),
                        "BIMaestro", StringComparison.OrdinalIgnoreCase);
                    FrameworkElement target = bimTabSelected
                        ? FindButton(root, commandId, _menuId)
                        : FindBIMaestroTab(root);
                    if (!ReferenceEquals(target, _target))
                    {
                        RemovePointer();
                        AdornerLayer layer = target == null ? null : AdornerLayer.GetAdornerLayer(target);
                        if (layer != null)
                        {
                            _target = target; _layer = layer;
                            _pointer = new RibbonPointer(target);
                            layer.Add(_pointer);
                        }
                    }
                    _hint.Text = !bimTabSelected
                        ? "Clique sur l’onglet BIMaestro indiqué par Pikachu. Il te montrera ensuite " + _buttonLabel + "."
                        : target == null
                            ? "Dans BIMaestro, cherche « " + _buttonLabel + " ». Le guide continuera après le clic."
                            : "Pikachu indique « " + _buttonLabel + " ». Clique dessus pour continuer le parcours.";
                }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("BIMaestro guide ribbon: " + ex.Message); }
            }

            private static FrameworkElement FindBIMaestroTab(DependencyObject root)
            {
                var pending = new Stack<DependencyObject>(); pending.Push(root);
                int visited = 0;
                while (pending.Count > 0 && visited++ < 25000)
                {
                    DependencyObject node = pending.Pop();
                    if (node is FrameworkElement element && element.IsVisible &&
                        element.GetType().Name.IndexOf("RibbonTabButton", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        string.Equals(AutomationProperties.GetName(element), "BIMaestro", StringComparison.OrdinalIgnoreCase))
                        return element;
                    int count = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < count; i++) pending.Push(VisualTreeHelper.GetChild(node, i));
                }
                return null;
            }

            private static FrameworkElement FindButton(DependencyObject root, string commandId, string menuId)
            {
                var pending = new Stack<DependencyObject>(); pending.Push(root);
                FrameworkElement splitExact = null;
                FrameworkElement splitContaining = null;
                FrameworkElement command = null;
                int visited = 0;
                while (pending.Count > 0 && visited++ < 25000)
                {
                    DependencyObject node = pending.Pop();
                    if (node is FrameworkElement element && element.IsVisible &&
                        element.ActualWidth >= 16 && element.ActualHeight >= 16)
                    {
                        foreach (object model in new[] { element.DataContext, (object)element,
                            element.Tag, (element as ContentControl)?.Content })
                        {
                            if (model == null || !(model.GetType().Namespace ?? "")
                                .StartsWith("Autodesk.Windows", StringComparison.Ordinal)) continue;
                            string id = model.GetType().GetProperty("Id")?.GetValue(model, null) as string;
                            FrameworkElement surface = FindRibbonClickSurface(element, menuId != null);
                            if (!string.IsNullOrEmpty(commandId) && id == commandId &&
                                (command == null || surface.ActualWidth * surface.ActualHeight > command.ActualWidth * command.ActualHeight))
                                command = surface;
                            if (menuId != null && id?.IndexOf(menuId, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                (splitExact == null || surface.ActualWidth * surface.ActualHeight > splitExact.ActualWidth * splitExact.ActualHeight))
                                splitExact = surface;
                            if (ContainsColorCommand(model, commandId, 0) &&
                                (splitContaining == null || surface.ActualWidth * surface.ActualHeight > splitContaining.ActualWidth * splitContaining.ActualHeight))
                                splitContaining = surface;
                        }
                    }
                    int count = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < count; i++) pending.Push(VisualTreeHelper.GetChild(node, i));
                }
                return menuId != null ? splitExact ?? splitContaining ?? command : command ?? splitContaining;
            }

            private static FrameworkElement FindRibbonClickSurface(FrameworkElement element, bool preferSplit)
            {
                FrameworkElement button = null;
                FrameworkElement split = null;
                FrameworkElement fallback = element;
                DependencyObject node = element;
                for (int depth = 0; node != null && depth < 9; depth++, node = VisualTreeHelper.GetParent(node))
                {
                    if (!(node is FrameworkElement candidate) || !candidate.IsVisible) continue;
                    string typeName = candidate.GetType().Name;
                    if (candidate.ActualWidth <= 240 && candidate.ActualHeight <= 160 &&
                        candidate.ActualWidth * candidate.ActualHeight > fallback.ActualWidth * fallback.ActualHeight &&
                        (typeName.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         ReferenceEquals(candidate.DataContext, element.DataContext)))
                        fallback = candidate;
                    if (typeName.IndexOf("RibbonSplitButton", StringComparison.OrdinalIgnoreCase) >= 0)
                        split = candidate;
                    else if (typeName.Equals("RibbonButton", StringComparison.OrdinalIgnoreCase) ||
                             typeName.Equals("RibbonToggleButton", StringComparison.OrdinalIgnoreCase))
                        button = candidate;
                }
                return preferSplit ? split ?? button ?? fallback : button ?? split ?? fallback;
            }

            private static bool ContainsColorCommand(object model, string id, int depth)
            {
                if (depth >= 4 || string.IsNullOrEmpty(id)) return false;
                if (!(model.GetType().GetProperty("Items")?.GetValue(model, null) is IEnumerable items)) return false;
                foreach (object child in items)
                {
                    if (child == null) continue;
                    if (child.GetType().GetProperty("Id")?.GetValue(child, null) as string == id ||
                        ContainsColorCommand(child, id, depth + 1)) return true;
                }
                return false;
            }

            private void RemovePointer()
            {
                if (_pointer != null) _layer?.Remove(_pointer);
                _pointer = null; _target = null; _layer = null;
            }
        }

        private sealed class RibbonPointer : Adorner
        {
            private readonly DrawingImage _sprite = RibbonPanelColorScheme.CreateCompanionImage();
            internal RibbonPointer(UIElement target) : base(target)
            { IsHitTestVisible = false; Focusable = false; }
            protected override void OnRender(DrawingContext drawing)
            {
                Rect bounds = new Rect(AdornedElement.RenderSize);
                if (bounds.Width < 1 || bounds.Height < 1) return;
                drawing.DrawRoundedRectangle(null, new Pen(Brushes.DarkOrange, 3),
                    new Rect(-2, -2, bounds.Width + 4, bounds.Height + 4), 5, 5);
                drawing.DrawImage(_sprite, new Rect(Math.Max(0, bounds.Width - 28), bounds.Height + 3, 28, 28));
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class RestartAppearanceGuideCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "RestartAppearanceGuide";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            AppearanceOnboarding.StartIntro(data.Application.MainWindowHandle);
            return Result.Succeeded;
        }
    }
}

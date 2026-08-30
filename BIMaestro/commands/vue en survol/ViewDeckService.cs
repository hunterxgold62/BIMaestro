using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitView = Autodesk.Revit.DB.View;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;

namespace BIMaestro.ViewHover
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ToggleViewDeckCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ViewDeckToggle";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (ViewDeckService.Toggle(data.Application, out string error)) return Result.Succeeded;
            TaskDialog.Show("BIMaestro - Onglets", error);
            return Result.Cancelled;
        }
    }

    /// <summary>
    /// Opt-in prototype: open views of the active document only. Native tabs,
    /// close buttons and document navigation remain untouched underneath.
    /// All Revit reads/exports run in Idling; clicks use an ExternalEvent.
    /// </summary>
    internal static class ViewDeckService
    {
        private sealed class Card
        {
            internal string ViewUniqueId;
            internal Border Border;
            internal Image Image;
            internal TextBlock Placeholder;
            internal long ImageStamp = -1;
        }

        private static readonly List<Card> Cards = new List<Card>();
        private static readonly Brush Accent = FrozenBrush(38, 112, 184);
        private static readonly Brush InactiveBorder = FrozenBrush(184, 194, 207);
        private static readonly Brush ActiveBackground = FrozenBrush(224, 239, 253);
        private static ViewDeckHost _host;
        private static StackPanel _cardsPanel;
        private static TextBlock _documentTitle;
        private static Document _document;
        private static string _structure;
        private static string _lastActiveView;
        private static bool _enabled;
        private static DateTime _nextRefreshUtc;
        private static DateTime _nextCaptureUtc;
        private static NavigationHandler _navigation;
        private static ExternalEvent _navigationEvent;

        internal static bool Toggle(UIApplication app, out string error)
        {
            error = null;
            if (_enabled)
            {
                Disable();
                return true;
            }
            if (app?.ActiveUIDocument == null)
            {
                error = UiLanguage.T("Ouvrez un document Revit avant d'activer les miniatures.",
                    "Open a Revit document before enabling thumbnails.");
                return false;
            }
            try
            {
                if (_navigationEvent == null)
                {
                    _navigation = new NavigationHandler();
                    _navigationEvent = ExternalEvent.Create(_navigation);
                }
                if (!EnsureHost(app))
                {
                    error = UiLanguage.T(
                        "La barre des onglets n'a pas été trouvée. Repassez en vues à onglets (TW), puis réessayez. L'interface n'a pas été modifiée.",
                        "The view tab bar was not found. Switch to tabbed views (TW), then retry. The interface was not changed.");
                    return false;
                }
                _enabled = true;
                _nextRefreshUtc = DateTime.MinValue;
                _nextCaptureUtc = DateTime.UtcNow.AddSeconds(2);
                Refresh(app, false);
                UpdateButton();
                return true;
            }
            catch (Exception ex)
            {
                Disable();
                error = UiLanguage.T("Impossible d'activer les miniatures : ", "Unable to enable thumbnails: ") + ex.Message;
                return false;
            }
        }

        internal static void ProcessIdling(UIApplication app)
        {
            if (!_enabled || DateTime.UtcNow < _nextRefreshUtc) return;
            _nextRefreshUtc = DateTime.UtcNow.AddSeconds(1);
            try { Refresh(app, true); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("BIMaestro ViewDeck: " + ex);
                Disable();
                TaskDialog.Show("BIMaestro - Onglets", UiLanguage.T(
                    "La bande de miniatures a été désactivée après une erreur. Les onglets natifs restent disponibles.",
                    "The thumbnail strip was disabled after an error. Native tabs remain available.") + "\n\n" + ex.Message);
            }
        }

        private static void Refresh(UIApplication app, bool allowCapture)
        {
            UIDocument uiDocument = app?.ActiveUIDocument;
            if (uiDocument == null)
            {
                Detach();
                return;
            }
            if (!EnsureHost(app)) return; // WT/TW can rebuild the visual tree between idle ticks.
            Document document = uiDocument.Document;
            var views = uiDocument.GetOpenUIViews()
                .Select(window => document.GetElement(window.ViewId) as RevitView)
                .Where(view => view != null && !view.IsTemplate)
                .ToList();
            string structure = string.Join("|", views.Select(view => view.UniqueId + ":" + view.Name));
            if (!document.Equals(_document) || structure != _structure)
            {
                _document = document;
                _structure = structure;
                _lastActiveView = null;
                Cards.Clear();
                _cardsPanel.Children.Clear();
                foreach (RevitView view in views) AddCard(document, view);
            }
            _documentTitle.Text = document.Title;

            string activeId = uiDocument.ActiveView?.UniqueId;
            foreach (Card card in Cards)
            {
                bool active = card.ViewUniqueId == activeId;
                card.Border.BorderBrush = active ? Accent : InactiveBorder;
                card.Border.Background = active ? ActiveBackground : Brushes.White;
                if (active && activeId != _lastActiveView) card.Border.BringIntoView();
                RevitView view = views.First(item => item.UniqueId == card.ViewUniqueId);
                UpdateImage(card, ViewHoverPreviewService.GetDeckPreviewPath(document, view));
                if (card.Image.Source == null)
                    card.Placeholder.Text = ViewHoverPreviewService.IsDeckPreviewUnavailable(document, view)
                        ? UiLanguage.T("Aperçu indisponible", "Preview unavailable")
                        : UiLanguage.T("Aperçu en attente", "Preview pending");
            }
            _lastActiveView = activeId;

            if (!allowCapture || DateTime.UtcNow < _nextCaptureUtc) return;
            _nextCaptureUtc = DateTime.UtcNow.AddSeconds(2);
            // Export only one missing preview, without switching the active view.
            foreach (RevitView view in views)
                if (ViewHoverPreviewService.TryCreateMissingDeckPreview(document, view)) break;
        }

        private static bool EnsureHost(UIApplication app)
        {
            if (_host != null && _host.IsAttached && _host.Grid.IsVisible &&
                PresentationSource.FromVisual(_host.Grid) != null) return true;
            Detach();
            DependencyObject root = HwndSource.FromHwnd(app.MainWindowHandle)?.RootVisual;
            if (root == null) return false;
            foreach (FrameworkElement pane in FindTabPanels(root))
            {
                DependencyObject parent = VisualTreeHelper.GetParent(pane);
                for (int depth = 0; parent != null && depth < 12; depth++)
                {
                    if (parent is Grid grid)
                    {
                        _host = ViewDeckHost.Attach(grid, BuildStrip());
                        if (_host != null) return true;
                        break;
                    }
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
            return false;
        }

        private static IEnumerable<FrameworkElement> FindTabPanels(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            int visited = 0;
            while (pending.Count > 0 && visited++ < 25000)
            {
                DependencyObject current = pending.Pop();
                if (current is FrameworkElement element && element.IsVisible &&
                    element.GetType().FullName == "Xceed.Wpf.AvalonDock.Controls.DocumentPaneTabPanel")
                    yield return element;
                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int index = count - 1; index >= 0; index--)
                    pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }

        private static FrameworkElement BuildStrip()
        {
            _documentTitle = new TextBlock
            {
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = FrozenBrush(50, 63, 80),
                Margin = new Thickness(7, 2, 7, 1), TextTrimming = TextTrimming.CharacterEllipsis
            };
            _cardsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var scroll = new ScrollViewer
            {
                Content = _cardsPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false,
                Focusable = false
            };
            scroll.PreviewMouseWheel += (_, args) =>
            {
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - args.Delta);
                args.Handled = true;
            };
            var layout = new DockPanel();
            DockPanel.SetDock(_documentTitle, Dock.Top);
            layout.Children.Add(_documentTitle);
            layout.Children.Add(scroll);
            return new Border
            {
                Height = 126, Background = FrozenBrush(240, 243, 247),
                BorderBrush = InactiveBorder, BorderThickness = new Thickness(0, 0, 0, 1), Child = layout
            };
        }

        private static void AddCard(Document document, RevitView view)
        {
            var card = new Card { ViewUniqueId = view.UniqueId };
            var title = new TextBlock
            {
                Text = view.Name, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 1, 4, 3)
            };
            card.Image = new Image { Stretch = Stretch.Uniform, Visibility = System.Windows.Visibility.Collapsed };
            card.Placeholder = new TextBlock
            {
                Text = view is ViewSchedule ? UiLanguage.T("Nomenclature", "Schedule") :
                    UiLanguage.T("Aperçu en attente", "Preview pending"),
                Foreground = Brushes.DimGray, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var preview = new Grid { Height = 61, Background = Brushes.White };
            preview.Children.Add(card.Placeholder);
            preview.Children.Add(card.Image);
            var content = new StackPanel();
            content.Children.Add(title);
            content.Children.Add(preview);
            card.Border = new Border
            {
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2), Child = content
            };
            var button = new Button
            {
                Width = 146, Height = 88, Padding = new Thickness(0), Margin = new Thickness(3, 1, 3, 1),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                ToolTip = view.Name, Content = card.Border, Focusable = false
            };
            button.Click += (_, __) =>
            {
                if (!_enabled) return;
                _navigation.Document = document;
                _navigation.ViewUniqueId = card.ViewUniqueId;
                try
                {
                    ExternalEventRequest request = _navigationEvent.Raise();
                    if (request != ExternalEventRequest.Accepted && request != ExternalEventRequest.Pending)
                        _navigation.Clear();
                }
                catch { _navigation.Clear(); }
            };
            Cards.Add(card);
            _cardsPanel.Children.Add(button);
        }

        private static void UpdateImage(Card card, string path)
        {
            long stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
            if (stamp == card.ImageStamp) return;
            card.ImageStamp = stamp;
            card.Image.Source = null;
            card.Image.Visibility = System.Windows.Visibility.Collapsed;
            card.Placeholder.Visibility = System.Windows.Visibility.Visible;
            if (stamp == 0) return;
            try
            {
                // OnLoad releases the file handle so the existing cache can refresh it.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 280;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    card.Image.Source = image;
                }
                card.Image.Visibility = System.Windows.Visibility.Visible;
                card.Placeholder.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch (IOException) { card.ImageStamp = -1; }
            catch (NotSupportedException)
            {
                card.Placeholder.Text = UiLanguage.T("Aperçu indisponible", "Preview unavailable");
            }
        }

        private static Brush FrozenBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static void Detach()
        {
            _host?.Dispose();
            _host = null;
            _cardsPanel = null;
            _documentTitle = null;
            _document = null;
            _structure = null;
            _lastActiveView = null;
            Cards.Clear();
        }

        internal static void UpdateButton()
        {
            AppUI.UpdatePushButtonPresentation("ViewDeckToggle", _enabled ? "Onglets : ON" : "Onglets : OFF",
                UiLanguage.T("Affiche ou masque les miniatures des vues ouvertes du document actif. Cliquez sur une miniature pour activer la vue.",
                    "Show or hide thumbnails of the active document's open views. Click a thumbnail to activate its view."));
        }

        private static void Disable()
        {
            _enabled = false;
            _navigation?.Clear();
            Detach();
            UpdateButton();
        }

        internal static void Shutdown()
        {
            Disable();
            _navigationEvent?.Dispose();
            _navigationEvent = null;
            _navigation = null;
        }

        private sealed class NavigationHandler : IExternalEventHandler
        {
            internal Document Document;
            internal string ViewUniqueId;
            internal void Clear() { Document = null; ViewUniqueId = null; }

            public void Execute(UIApplication app)
            {
                Document requestedDocument = Document;
                string uniqueId = ViewUniqueId;
                Clear();
                if (!_enabled || requestedDocument == null || string.IsNullOrEmpty(uniqueId)) return;
                UIDocument uiDocument = app.ActiveUIDocument;
                // Ignore a stale click after a document switch/close or an OFF toggle.
                if (uiDocument == null || !requestedDocument.IsValidObject ||
                    !uiDocument.Document.Equals(requestedDocument)) return;
                try
                {
                    var view = requestedDocument.GetElement(uniqueId) as RevitView;
                    if (view == null || view.IsTemplate || uiDocument.ActiveView?.Id == view.Id ||
                        !uiDocument.GetOpenUIViews().Any(window => window.ViewId == view.Id)) return;
                    uiDocument.RequestViewChange(view);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("BIMaestro - Onglets", UiLanguage.T(
                        "Impossible d'activer cette vue pour le moment : ", "Unable to activate this view right now: ") + ex.Message);
                }
            }

            public string GetName() => "BIMaestro - ViewDeck navigation";
        }
    }
}

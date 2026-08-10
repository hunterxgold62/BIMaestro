using System;
using System.Collections.Generic;
using BIMaestro.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace BIMaestro.UI
{
    public partial class RadialMenuWindow : Window
    {
        // ======== Events externes ========
        public event Action<bool, int, RadialItem> Completed;
        public event Action<RadialItem> ReloadRequested; // clic droit → "Recharger"

        // ======== Données / pagination ========
        private List<RadialItem> _items; // multiples de 8 (complétés dynamiquement)
        private int _pageIndex = 0;
        private const int PAGE_SIZE = 8;
        private readonly RadialItem[] _currentPageItems = new RadialItem[PAGE_SIZE];

        // ======== Position écran (px natifs) ========
        private readonly int _screenXpx;
        private readonly int _screenYpx;

        // ======== Géométrie roue ========
        private const int SEGMENTS = 8;
        private const double OUTER_R = 220;
        private const double INNER_R = 80;
        private const double IMG_SIZE = 64;
        private const double GAP_DEG = 4; // angle mort visuel ET (ci-dessous) en hit-test

        // ======== Couleurs / styles ========
        private readonly Color _sectorFill = Color.FromArgb(200, 245, 245, 245);
        private readonly Color _sectorFillHover = Color.FromArgb(230, 255, 255, 255);
        private readonly Color _sectorStroke = Color.FromArgb(160, 150, 150, 160);
        private readonly Color _accent = (Color)ColorConverter.ConvertFromString("#3A86FF");

        // ======== Eléments visuels ========
        private readonly List<Path> _sectors = new();
        private readonly List<Border> _iconBorders = new();
        private Path _hoverOutlinePath;
        private Ellipse _centerDisk;
        private Image _centerPreview;
        private Polygon _leftArrow;
        private Polygon _rightArrow;
        private TextBlock _centerLabel;

        // ======== Collections ========
        private Func<IReadOnlyList<(string Id, string Name)>> _collectionOptionsProvider;
        private Action<string> _collectionSelectionCallback;
        private Action _collectionClearCallback;
        private bool _collectionModeActive;
        private string _activeCollectionId;
        private Func<int, int, string> _pageLabelFactory;

        // ======== Cache images ========
        private static readonly Dictionary<string, BitmapImage> s_ImageCache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private const int CACHE_MAX = 256;

        // ======== Sécurité fermeture / état ========
        private DateTime _canCloseAfter = DateTime.MaxValue;
        private bool _closing;

        // ======== Molette : anti-saut multi-pages ========
        private DateTime _lastWheelSwitch = DateTime.MinValue;
        private static readonly TimeSpan _wheelCooldown = TimeSpan.FromMilliseconds(120);
        public bool InvertWheel { get; set; } = false; // option : inverser sens molette

        // ======== Hystérésis de survol (anti-papillonnage) ========
        private int _lastHover = -1;
        private double _lastAngleDeg = double.NaN;
        private const double HOVER_HYSTERESIS_DEG = 6; // ne pas changer de secteur pour < 6°

        // ======== Auto-fermeture douce ========
        public bool AutoCloseEnabled { get; set; } = true;
        public TimeSpan AutoCloseDelay { get; set; } = TimeSpan.FromSeconds(5);
        private DispatcherTimer _idleTimer;

        public RadialMenuWindow(List<RadialItem> items, int screenXpx, int screenYpx)
        {
            InitializeComponent();

            _items = items ?? new List<RadialItem>();
            NormalizeItems();

            _screenXpx = screenXpx;
            _screenYpx = screenYpx;

            double diameter = OUTER_R * 2 + 8;
            this.Width = diameter;
            this.Height = diameter;

            _pageLabelFactory = DefaultPageLabelFactory;

            Loaded += Window_Loaded;
            Unloaded += Window_Unloaded;

            KeyDown += Window_KeyDown;         // ESC + raccourcis 1..8
            Deactivated += Window_Deactivated; // Alt-Tab etc.
        }

        // ===================== Cycle de vie =====================


        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Positionne la roue au-dessus de la souris (corrigé DPI)
            var dpi = VisualTreeHelper.GetDpi(this);
            double xDip = _screenXpx / dpi.DpiScaleX;
            double yDip = _screenYpx / dpi.DpiScaleY;
            this.Left = xDip - (this.Width / 2.0);
            this.Top = yDip - (this.Height / 2.0);

            BuildWheel();
            LoadPage(0);
            UpdatePageLabel();
            this.Focus();

            // ouvre "protégé" 250ms (évite fermeture immédiate par clic d'activation)
            _canCloseAfter = DateTime.UtcNow.AddMilliseconds(250);

            // capture globale & fermeture clic-extérieur
            Mouse.Capture(RootCanvas, CaptureMode.SubTree);
            Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(RootCanvas, OnClickOutsideCaptured);
            try { Application.Current.Deactivated += App_Deactivated; } catch { }

            // fade-in
            RootCanvas.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new QuadraticEase() };
            RootCanvas.BeginAnimation(UIElement.OpacityProperty, fade);

            // molette + mouvement pour hover continu
            this.PreviewMouseWheel += OnWheelScroll;
            RootCanvas.MouseMove += OnRootMouseMove;

            // Auto-close timer + capteurs d'activité
            _idleTimer = new DispatcherTimer { Interval = AutoCloseDelay };
            _idleTimer.Tick += (_, __) => BeginSoftClose();
            this.PreviewMouseMove += AnyUserActivity;
            this.PreviewMouseDown += AnyUserActivity;
            this.PreviewMouseWheel += AnyUserActivity;
            this.PreviewKeyDown += AnyUserActivity;

            if (AutoCloseEnabled) ResetIdleTimer();
        }

        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // libère la capture
                if (Mouse.Captured == RootCanvas) Mouse.Capture(null);
                Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(RootCanvas, OnClickOutsideCaptured);
                try { Application.Current.Deactivated -= App_Deactivated; } catch { }

                // désabonnements
                this.PreviewMouseWheel -= OnWheelScroll;
                RootCanvas.MouseMove -= OnRootMouseMove;

                if (_idleTimer != null)
                {
                    _idleTimer.Stop();
                    _idleTimer.Tick -= (_, __) => BeginSoftClose(); // (no-op safe)
                }
                this.PreviewMouseMove -= AnyUserActivity;
                this.PreviewMouseDown -= AnyUserActivity;
                this.PreviewMouseWheel -= AnyUserActivity;
                this.PreviewKeyDown -= AnyUserActivity;
            }
            catch { }
        }

        // ===================== Gestion fermeture/activation =====================

        private void OnClickOutsideCaptured(object sender, MouseButtonEventArgs e)
        {
            if (_closing) return;
            if (DateTime.UtcNow < _canCloseAfter) return;
            e.Handled = true;
            SafeComplete(false, -1, null);
        }

        private void App_Deactivated(object? sender, EventArgs e)
        {
            if (_closing) return;
            if (DateTime.UtcNow < _canCloseAfter) return;
            SafeComplete(false, -1, null);
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (_closing) return;
            if (DateTime.UtcNow < _canCloseAfter) return;
            SafeComplete(false, -1, null);
        }

        // ===================== Clavier =====================

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (_closing) return;
            ResetIdleTimer();

            if (e.Key == Key.Escape)
            {
                SafeComplete(false, -1, null);
                return;
            }

            // Raccourcis 1..8 / NumPad 1..8
            int idx = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D8) idx = (int)(e.Key - Key.D1);
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad8) idx = (int)(e.Key - Key.NumPad1);

            if (idx >= 0 && idx < SEGMENTS)
            {
                var item = _currentPageItems[idx];
                if (item != null && item.HasAction)
                    SafeComplete(true, _pageIndex * PAGE_SIZE + idx, item);
            }
        }

        // ===================== Construction roue =====================

        private void BuildWheel()
        {
            RootCanvas.Children.Clear();
            _sectors.Clear();
            _iconBorders.Clear();

            double cx = this.Width / 2.0;
            double cy = this.Height / 2.0;

            // Anneau arrière (légère vignette + ombre)
            var ring = new Ellipse
            {
                Width = OUTER_R * 2,
                Height = OUTER_R * 2,
                Fill = new RadialGradientBrush(Color.FromArgb(40, 255, 255, 255), Color.FromArgb(100, 235, 235, 235))
                { RadiusX = 1.0, RadiusY = 1.0, Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5) }
            };
            ring.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.25 };
            Canvas.SetLeft(ring, cx - OUTER_R);
            Canvas.SetTop(ring, cy - OUTER_R);
            RootCanvas.Children.Add(ring);

            // Disque centre
            _centerDisk = new Ellipse
            {
                Width = INNER_R * 2,
                Height = INNER_R * 2,
                Fill = new RadialGradientBrush(Color.FromArgb(170, 255, 255, 255), Color.FromArgb(120, 240, 240, 240)),
                Stroke = new SolidColorBrush(Color.FromArgb(120, 180, 180, 190)),
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(_centerDisk, cx - INNER_R);
            Canvas.SetTop(_centerDisk, cy - INNER_R);
            RootCanvas.Children.Add(_centerDisk);
            _centerDisk.MouseRightButtonUp += OnCenterRightButtonUp;

            // Aperçu centre
            _centerPreview = new Image { Width = INNER_R * 0.9, Height = INNER_R * 0.9, Stretch = Stretch.Uniform };
            Canvas.SetLeft(_centerPreview, cx - _centerPreview.Width / 2.0);
            Canvas.SetTop(_centerPreview, cy - _centerPreview.Height / 2.0);
            RootCanvas.Children.Add(_centerPreview);
            _centerPreview.MouseRightButtonUp += OnCenterRightButtonUp;

            // Label centre
            _centerLabel = new TextBlock
            {
                Text = "Top-8",
                Foreground = new SolidColorBrush(Color.FromArgb(230, 60, 60, 80)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Width = INNER_R * 1.6,
                Opacity = 0.95
            };
            Canvas.SetLeft(_centerLabel, cx - _centerLabel.Width / 2.0);
            double labelTop = cy - (_centerPreview.Height / 2.0) - 16;
            Canvas.SetTop(_centerLabel, labelTop);
            RootCanvas.Children.Add(_centerLabel);
            _centerLabel.MouseRightButtonUp += OnCenterRightButtonUp;

            // Flèche gauche/droite (pagination)
            _leftArrow = MakeArrowPolygon(isRight: false, size: 18);
            PositionArrow(_leftArrow, cx - INNER_R * 0.65, cy);
            _leftArrow.MouseLeftButtonUp += (s, e) => { if (_closing) return; ResetIdleTimer(); e.Handled = true; PrevPage(); };
            RootCanvas.Children.Add(_leftArrow);

            _rightArrow = MakeArrowPolygon(isRight: true, size: 18);
            PositionArrow(_rightArrow, cx + INNER_R * 0.65, cy);
            _rightArrow.MouseLeftButtonUp += (s, e) => { if (_closing) return; ResetIdleTimer(); e.Handled = true; NextPage(); };
            RootCanvas.Children.Add(_rightArrow);

            // Contour hover (copie du secteur survolé)
            _hoverOutlinePath = new Path
            {
                Stroke = new SolidColorBrush(_accent),
                StrokeThickness = 2.3,
                Visibility = Visibility.Collapsed
            };
            RootCanvas.Children.Add(_hoverOutlinePath);

            // Secteurs + icônes
            double sweep = 360.0 / SEGMENTS;
            for (int i = 0; i < SEGMENTS; i++)
            {
                int idx = i;
                double start = idx * sweep + (GAP_DEG / 2.0);
                double actualSweep = sweep - GAP_DEG;

                var sector = CreateSector(new Point(cx, cy), INNER_R, OUTER_R, start, actualSweep);
                sector.Fill = new SolidColorBrush(_sectorFill);
                sector.Stroke = new SolidColorBrush(_sectorStroke);
                sector.StrokeThickness = 1.0;
                sector.Tag = idx;

                // Survol → hystérésis (anti-papillon)
                sector.MouseEnter += (_, __) => { if (!_closing) SetHoverWithHysteresis(idx); };
                // Clic gauche → valider
                sector.MouseLeftButtonUp += (s, e) =>
                {
                    if (_closing) return;
                    ResetIdleTimer();
                    e.Handled = true;
                    var item = _currentPageItems[idx];
                    if (item != null && item.HasAction)
                        SafeComplete(true, _pageIndex * PAGE_SIZE + idx, item);
                };

                // Menu contextuel (clic droit)
                sector.ContextMenu = BuildContextMenu(idx);

                _sectors.Add(sector);
                RootCanvas.Children.Add(sector);

                // Icône
                var icon = new Image { Width = IMG_SIZE, Height = IMG_SIZE, Stretch = Stretch.Uniform };
                var border = new Border
                {
                    Width = IMG_SIZE,
                    Height = IMG_SIZE,
                    Background = Brushes.Transparent,
                    Child = icon,
                    Tag = idx,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1.0, 1.0)
                };

                border.MouseEnter += (_, __) => { if (!_closing) SetHoverWithHysteresis(idx); };
                border.MouseLeftButtonUp += (s, e) =>
                {
                    if (_closing) return;
                    ResetIdleTimer();
                    e.Handled = true;
                    var item = _currentPageItems[idx];
                    if (item != null && item.HasAction)
                        SafeComplete(true, _pageIndex * PAGE_SIZE + idx, item);
                };
                border.ContextMenu = BuildContextMenu(idx);

                // Position icône au milieu du secteur
                double midDeg = start + actualSweep / 2.0;
                double midRad = midDeg * Math.PI / 180.0;
                double r = (INNER_R + OUTER_R) / 2.0;
                double ix = cx + r * Math.Cos(midRad) - IMG_SIZE / 2.0;
                double iy = cy + r * Math.Sin(midRad) - IMG_SIZE / 2.0;

                Canvas.SetLeft(border, ix);
                Canvas.SetTop(border, iy);
                _iconBorders.Add(border);
                RootCanvas.Children.Add(border);
            }

            // clic ailleurs dans la roue => fermeture (après délai d’armement)
            RootCanvas.MouseLeftButtonUp += (s, e) =>
            {
                if (_closing) return;
                if (!e.Handled && DateTime.UtcNow >= _canCloseAfter)
                    SafeComplete(false, -1, null);
            };
        }

        // ===================== Menus / Pages =====================

        private ContextMenu BuildContextMenu(int idx)
        {
            if (ReloadRequested == null) return null;

            var cm = new ContextMenu();
            var mi = new MenuItem { Header = "Recharger la dernière version" };
            mi.Click += (s, e) =>
            {
                var item = _currentPageItems[idx];
                if (item == null || !item.HasFamily) return;

                try { ReloadRequested?.Invoke(item); } catch { }
                SafeComplete(false, -1, null); // on ferme après action
            };
            cm.Items.Add(mi);
            return cm;
        }

        private int PageCount
        {
            get
            {
                int count = _items?.Count ?? 0;
                int pages = (int)Math.Ceiling(count / (double)PAGE_SIZE);
                return Math.Max(1, pages);
            }
        }

        private void PrevPage()
        {
            _pageIndex = (_pageIndex - 1 + PageCount) % PageCount;
            LoadPage(_pageIndex);
            UpdatePageLabel();
            UpdateHoverFromMouse();
        }

        private void NextPage()
        {
            _pageIndex = (_pageIndex + 1) % PageCount;
            LoadPage(_pageIndex);
            UpdatePageLabel();
            UpdateHoverFromMouse();
        }

        private void LoadPage(int index)
        {
            _pageIndex = index;
            Array.Clear(_currentPageItems, 0, _currentPageItems.Length);

            int offset = index * PAGE_SIZE;
            for (int i = 0; i < PAGE_SIZE; i++)
                _currentPageItems[i] = (offset + i < _items.Count) ? _items[offset + i] : null;

            _hoverOutlinePath.Visibility = Visibility.Collapsed;
            _centerPreview.Source = null;
            _lastHover = -1; // reset hystérésis

            for (int i = 0; i < _iconBorders.Count; i++)
            {
                var img = (Image)_iconBorders[i].Child;
                var item = _currentPageItems[i];
                var src = LoadImage(item?.ImagePath);
                img.Source = src;
                _iconBorders[i].Opacity = (item == null || !item.HasAction) ? 0.2 : 1.0;

                var tr = (ScaleTransform)_iconBorders[i].RenderTransform;
                tr.ScaleX = tr.ScaleY = 1.0;
            }
        }

        private void UpdatePageLabel()
        {
            if (_centerLabel == null) return;

            var factory = _pageLabelFactory ?? DefaultPageLabelFactory;
            string text = factory(_pageIndex, PageCount) ?? string.Empty;
            _centerLabel.Text = text;

            _centerLabel.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new QuadraticEase() };
            _centerLabel.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private string DefaultPageLabelFactory(int index, int pageCount)
        {
            return index switch
            {
                0 => "Top-8",
                1 => UiLanguage.T("Récents (1/2)", "Recent (1/2)"),
                2 => UiLanguage.T("Récents (2/2)", "Recent (2/2)"),
                _ => UiLanguage.T($"Page {index + 1}/{pageCount}", $"Page {index + 1}/{pageCount}"),
            };
        }

        public void SetPageLabelFactory(Func<int, int, string> factory)
        {
            _pageLabelFactory = factory ?? DefaultPageLabelFactory;
            UpdatePageLabel();
        }

        public void UpdateCollectionState(bool isActive, string collectionName, string collectionId)
        {
            _collectionModeActive = isActive;
            _activeCollectionId = collectionId;
            if (_centerLabel != null)
            {
                if (isActive && !string.IsNullOrWhiteSpace(collectionName))
                    _centerLabel.ToolTip = UiLanguage.T($"Collection : {collectionName}", $"Collection: {collectionName}");
                else
                    _centerLabel.ToolTip = null;
            }
            UpdatePageLabel();
        }

        public void ConfigureCollectionActions(
            Func<IReadOnlyList<(string Id, string Name)>> optionsProvider,
            Action<string> onSelection,
            Action onClear)
        {
            _collectionOptionsProvider = optionsProvider;
            _collectionSelectionCallback = onSelection;
            _collectionClearCallback = onClear;
        }

        public void ReplaceItems(List<RadialItem> items)
        {
            _items = items ?? new List<RadialItem>();
            NormalizeItems();

            _pageIndex = 0;
            LoadPage(0);
            UpdatePageLabel();
            UpdateHoverFromMouse();
        }

        private void NormalizeItems()
        {
            if (_items == null)
            {
                _items = new List<RadialItem>();
            }

            if (_items.Count == 0)
            {
                for (int i = 0; i < PAGE_SIZE; i++)
                    _items.Add(new RadialItem());
                return;
            }

            int remainder = _items.Count % PAGE_SIZE;
            if (remainder == 0) return;

            int needed = PAGE_SIZE - remainder;
            for (int i = 0; i < needed; i++)
                _items.Add(new RadialItem());
        }

        private void OnCenterRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_closing) return;
            ResetIdleTimer();
            e.Handled = true;

            var menu = BuildCenterContextMenu();
            if (menu == null || menu.Items.Count == 0) return;

            menu.PlacementTarget = _centerDisk;
            menu.IsOpen = true;
        }

        private ContextMenu BuildCenterContextMenu()
        {
            var provider = _collectionOptionsProvider;
            if (provider == null && !_collectionModeActive) return null;

            var cm = new ContextMenu();

            if (provider != null)
            {
                var loadItem = new MenuItem { Header = UiLanguage.T("Charger une collection", "Load a Collection") };
                var options = provider() ?? Array.Empty<(string Id, string Name)>();
                foreach (var option in options)
                {
                    if (string.IsNullOrWhiteSpace(option.Id)) continue;
                    var sub = new MenuItem { Header = option.Name ?? option.Id, Tag = option.Id };
                    sub.IsCheckable = true;
                    if (!string.IsNullOrEmpty(_activeCollectionId) &&
                        string.Equals(option.Id, _activeCollectionId, StringComparison.OrdinalIgnoreCase))
                        sub.IsChecked = true;
                    sub.Click += (s, e) =>
                    {
                        try
                        {
                            _collectionSelectionCallback?.Invoke((string)((MenuItem)s).Tag);
                        }
                        catch { }
                    };
                    loadItem.Items.Add(sub);
                }

                if (loadItem.Items.Count == 0)
                {
                    loadItem.IsEnabled = false;
                    loadItem.Items.Add(new MenuItem { Header = UiLanguage.T("Aucune collection disponible", "No Collection Available"), IsEnabled = false });
                }

                cm.Items.Add(loadItem);
            }

            if (_collectionModeActive && _collectionClearCallback != null)
            {
                var cancelItem = new MenuItem { Header = UiLanguage.T("Annuler la collection", "Clear Collection") };
                cancelItem.Click += (s, e) =>
                {
                    try { _collectionClearCallback?.Invoke(); } catch { }
                };
                cm.Items.Add(cancelItem);
            }

            return cm;
        }

        // ===================== Survol / sélection =====================

        private void SetHover(int i)
        {
            if (i < 0 || i >= _sectors.Count) return;

            for (int k = 0; k < _sectors.Count; k++)
                _sectors[k].Fill = new SolidColorBrush(k == i ? _sectorFillHover : _sectorFill);

            for (int k = 0; k < _iconBorders.Count; k++)
            {
                var tr = (ScaleTransform)_iconBorders[k].RenderTransform;
                double target = (k == i) ? 1.18 : 1.0;
                var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(90))
                { EasingFunction = new QuadraticEase() };
                tr.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                tr.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }

            _hoverOutlinePath.Data = _sectors[i].Data?.CloneCurrentValue();
            _hoverOutlinePath.Visibility = Visibility.Visible;

            var item = _currentPageItems[i];
            _centerPreview.Source = LoadImage(item?.ImagePath);
        }

        private void SetHoverWithHysteresis(int idx) // version MouseEnter (sans point)
        {
            var p = Mouse.GetPosition(RootCanvas);
            SetHoverWithHysteresis(idx, p);
        }

        private void SetHoverWithHysteresis(int idx, Point p) // version MouseMove
        {
            if (idx < 0 || idx >= SEGMENTS)
            {
                // garde l'aperçu sticky ; ne cache que le contour si on sort
                _hoverOutlinePath.Visibility = Visibility.Collapsed;
                return;
            }

            double cx = this.Width / 2.0, cy = this.Height / 2.0;
            double aDeg = Math.Atan2(p.Y - cy, p.X - cx) * 180.0 / Math.PI; if (aDeg < 0) aDeg += 360.0;

            if (_lastHover == -1)
            {
                _lastHover = idx; _lastAngleDeg = aDeg; SetHover(idx); return;
            }

            double delta = Math.Abs(aDeg - _lastAngleDeg);
            if (delta > 180) delta = 360 - delta;

            if (idx != _lastHover && delta < HOVER_HYSTERESIS_DEG)
                return; // déplacement trop faible → ignore

            _lastHover = idx; _lastAngleDeg = aDeg; SetHover(idx);
        }

        private void OnRootMouseMove(object sender, MouseEventArgs e)
        {
            if (_closing) return;
            // recalcul continu (gère les "gaps" et hors anneau)
            UpdateHoverFromMouse();
        }

        private void UpdateHoverFromMouse()
        {
            try
            {
                Point p = Mouse.GetPosition(RootCanvas);
                int idx = GetSectorIndexFromPoint(p);
                SetHoverWithHysteresis(idx, p);
            }
            catch { /* no-op */ }
        }

        private int GetSectorIndexFromPoint(Point p)
        {
            // centre = milieu de la fenêtre (identique à BuildWheel)
            double cx = this.Width / 2.0;
            double cy = this.Height / 2.0;

            var v = new Vector(p.X - cx, p.Y - cy);
            double r = v.Length;
            if (r < INNER_R || r > OUTER_R) return -1; // hors anneau

            double a = Math.Atan2(v.Y, v.X); // [-π ; +π]
            if (a < 0) a += 2 * Math.PI;     // [0 ; 2π)

            double sweep = 2 * Math.PI / SEGMENTS;
            int raw = (int)Math.Floor(a / sweep);

            // applique le "gap" en radians → zone morte aux séparations
            double gap = (GAP_DEG * Math.PI / 180.0);
            double centerOfSector = raw * sweep + sweep / 2.0;
            double dist = Math.Abs(a - centerOfSector);
            if (dist > Math.PI) dist = 2 * Math.PI - dist;
            if (dist > (sweep / 2.0 - gap / 2.0)) return -1; // on est dans le gap

            return raw;
        }

        // ===================== Molette (pagination) =====================

        private void OnWheelScroll(object sender, MouseWheelEventArgs e)
        {
            if (_closing) return;
            ResetIdleTimer();

            var now = DateTime.UtcNow;
            if (now - _lastWheelSwitch < _wheelCooldown)
            {
                e.Handled = true;
                return; // throttle
            }
            _lastWheelSwitch = now;

            bool up = e.Delta > 0;
            if (InvertWheel) up = !up;

            if (up) PrevPage();
            else NextPage();

            UpdateHoverFromMouse(); // feedback immédiat
            e.Handled = true;
        }

        // ===================== Fermeture & idle =====================

        private void AnyUserActivity(object sender, EventArgs e) => ResetIdleTimer();

        private void ResetIdleTimer()
        {
            if (!AutoCloseEnabled || _closing) return;
            _idleTimer.Stop();
            _idleTimer.Interval = AutoCloseDelay;
            _idleTimer.Start();
        }

        private void BeginSoftClose()
        {
            if (_closing || DateTime.UtcNow < _canCloseAfter) { ResetIdleTimer(); return; }

            _closing = true;
            _idleTimer.Stop();

            var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new QuadraticEase() };
            anim.Completed += (s, e) => SafeComplete(false, -1, null);
            RootCanvas.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void SafeComplete(bool accepted, int globalIndex, RadialItem item)
        {
            if (_closing) return;
            _closing = true;

            try { Completed?.Invoke(accepted, globalIndex, item); } catch { }

            try { if (Mouse.Captured == RootCanvas) Mouse.Capture(null); } catch { }
            try { Close(); } catch { }

            // seconde chance asynchrone (si fermeture bloquée par un handler externe)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { if (IsVisible) Close(); } catch { }
            }));
        }

        // ===================== Outils géométrie / UI =====================

        private static Path CreateSector(Point center, double innerR, double outerR, double startDeg, double sweepDeg)
        {
            double start = startDeg * Math.PI / 180.0;
            double end = (startDeg + sweepDeg) * Math.PI / 180.0;

            Point p1 = new(center.X + outerR * Math.Cos(start), center.Y + outerR * Math.Sin(start));
            Point p2 = new(center.X + outerR * Math.Cos(end), center.Y + outerR * Math.Sin(end));
            Point p3 = new(center.X + innerR * Math.Cos(end), center.Y + innerR * Math.Sin(end));
            Point p4 = new(center.X + innerR * Math.Cos(start), center.Y + innerR * Math.Sin(start));

            bool large = sweepDeg > 180.0;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(p1, true, true);
                ctx.ArcTo(p2, new Size(outerR, outerR), 0, large, SweepDirection.Clockwise, true, true);
                ctx.LineTo(p3, true, true);
                ctx.ArcTo(p4, new Size(innerR, innerR), 0, large, SweepDirection.Counterclockwise, true, true);
            }
            try { geo.Freeze(); } catch { }
            return new Path { Data = geo };
        }

        private static Polygon MakeArrowPolygon(bool isRight, double size)
        {
            var poly = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(210, 250, 250, 250)),
                Stroke = new SolidColorBrush(Color.FromArgb(220, 160, 160, 170)),
                StrokeThickness = 1.1,
                Points = new PointCollection(new[]
                {
                    new Point(isRight ? -size/2 :  size/2, -size/1.6),
                    new Point(isRight ? -size/2 :  size/2,  size/1.6),
                    new Point(isRight ?  size/2 : -size/2,  0)
                })
            };
            poly.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.25 };
            poly.MouseEnter += (s, e) =>
            {
                poly.Fill = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
                poly.RenderTransformOrigin = new Point(0.5, 0.5);
                var tr = new ScaleTransform(1, 1);
                poly.RenderTransform = tr;
                var anim = new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(100))
                { EasingFunction = new QuadraticEase() };
                tr.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                tr.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            };
            poly.MouseLeave += (s, e) =>
            {
                poly.Fill = new SolidColorBrush(Color.FromArgb(210, 250, 250, 250));
                poly.RenderTransform = new ScaleTransform(1, 1);
            };
            return poly;
        }

        private static void PositionArrow(Shape arrow, double centerX, double centerY)
        {
            Canvas.SetLeft(arrow, centerX - arrow.StrokeThickness);
            Canvas.SetTop(arrow, centerY - arrow.StrokeThickness);
        }

        private static BitmapImage LoadImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (s_ImageCache.TryGetValue(path, out var cached)) return cached;

            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.DecodePixelWidth = 256;
                bi.EndInit();
                bi.Freeze();

                if (s_ImageCache.Count >= CACHE_MAX) s_ImageCache.Clear();
                s_ImageCache[path] = bi;
                return bi;
            }
            catch { return null; }
        }
    }
}

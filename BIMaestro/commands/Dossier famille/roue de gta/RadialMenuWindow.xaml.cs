using System;
using System.Collections.Generic;
using System.Linq;
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
        public event Action<bool, int, RadialItem> Completed;
        public event Action<RadialItem> ReloadRequested; // <<< nouveau

        private readonly List<RadialItem> _items; // 24 attendus
        private int _pageIndex = 0;
        private const int PAGE_SIZE = 8;
        private readonly RadialItem[] _currentPageItems = new RadialItem[PAGE_SIZE];

        private readonly int _screenXpx;
        private readonly int _screenYpx;

        private const int SEGMENTS = 8;
        private const double OUTER_R = 220;
        private const double INNER_R = 80;
        private const double IMG_SIZE = 64;
        private const double GAP_DEG = 4;

        private readonly Color _sectorFill = Color.FromArgb(200, 245, 245, 245);
        private readonly Color _sectorFillHover = Color.FromArgb(230, 255, 255, 255);
        private readonly Color _sectorStroke = Color.FromArgb(160, 150, 150, 160);
        private readonly Color _accent = (Color)ColorConverter.ConvertFromString("#3A86FF");

        private readonly List<Path> _sectors = new();
        private readonly List<Border> _iconBorders = new();
        private Path _hoverOutlinePath;
        private Ellipse _centerDisk;
        private Image _centerPreview;
        private Polygon _leftArrow;
        private Polygon _rightArrow;
        private TextBlock _centerLabel;

        private static readonly Dictionary<string, BitmapImage> s_ImageCache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private const int CACHE_MAX = 256;

        private DateTime _canCloseAfter = DateTime.MaxValue;
        private bool _closing;

        public RadialMenuWindow(List<RadialItem> items, int screenXpx, int screenYpx)
        {
            InitializeComponent();
            _items = items ?? new List<RadialItem>();
            while (_items.Count < 24) _items.Add(new RadialItem());

            _screenXpx = screenXpx;
            _screenYpx = screenYpx;

            double diameter = OUTER_R * 2 + 8;
            this.Width = diameter;
            this.Height = diameter;

            Loaded += Window_Loaded;
            Unloaded += Window_Unloaded;

            KeyDown += Window_KeyDown;
            Deactivated += Window_Deactivated;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double xDip = _screenXpx / dpi.DpiScaleX;
            double yDip = _screenYpx / dpi.DpiScaleY;

            this.Left = xDip - (this.Width / 2.0);
            this.Top = yDip - (this.Height / 2.0);

            BuildWheel();
            LoadPage(0);
            UpdatePageLabel();
            this.Focus();

            _canCloseAfter = DateTime.UtcNow.AddMilliseconds(250);

            Mouse.Capture(RootCanvas, CaptureMode.SubTree);
            Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(RootCanvas, OnClickOutsideCaptured);
            try { Application.Current.Deactivated += App_Deactivated; } catch { }

            RootCanvas.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            RootCanvas.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Mouse.Captured == RootCanvas) Mouse.Capture(null);
                Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(RootCanvas, OnClickOutsideCaptured);
                try { Application.Current.Deactivated -= App_Deactivated; } catch { }
            }
            catch { }
        }

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

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (_closing) return;
            if (e.Key == Key.Escape) SafeComplete(false, -1, null);
        }

        private void BuildWheel()
        {
            RootCanvas.Children.Clear();
            _sectors.Clear();
            _iconBorders.Clear();

            double cx = this.Width / 2.0;
            double cy = this.Height / 2.0;

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

            _centerDisk = new Ellipse
            {
                Width = INNER_R * 2,
                Height = INNER_R * 2,
                Fill = new RadialGradientBrush(Color.FromArgb(170, 255, 255, 255), Color.FromArgb(120, 240, 240, 240)),
                Stroke = new SolidColorBrush(Color.FromArgb(120, 180, 180, 190)),
                StrokeThickness = 1.2
            };
            _centerDisk.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.20 };
            Canvas.SetLeft(_centerDisk, cx - INNER_R);
            Canvas.SetTop(_centerDisk, cy - INNER_R);
            RootCanvas.Children.Add(_centerDisk);

            _centerPreview = new Image { Width = INNER_R * 0.9, Height = INNER_R * 0.9, Stretch = Stretch.Uniform };
            Canvas.SetLeft(_centerPreview, cx - _centerPreview.Width / 2.0);
            Canvas.SetTop(_centerPreview, cy - _centerPreview.Height / 2.0);
            RootCanvas.Children.Add(_centerPreview);

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

            _leftArrow = MakeArrowPolygon(isRight: false, size: 18);
            PositionArrow(_leftArrow, cx - INNER_R * 0.65, cy);
            _leftArrow.MouseLeftButtonUp += (s, e) => { if (_closing) return; e.Handled = true; PrevPage(); };
            RootCanvas.Children.Add(_leftArrow);

            _rightArrow = MakeArrowPolygon(isRight: true, size: 18);
            PositionArrow(_rightArrow, cx + INNER_R * 0.65, cy);
            _rightArrow.MouseLeftButtonUp += (s, e) => { if (_closing) return; e.Handled = true; NextPage(); };
            RootCanvas.Children.Add(_rightArrow);

            _hoverOutlinePath = new Path
            {
                Stroke = new SolidColorBrush(_accent),
                StrokeThickness = 2.3,
                Visibility = Visibility.Collapsed
            };
            RootCanvas.Children.Add(_hoverOutlinePath);

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

                // clic gauche = placer
                sector.MouseEnter += (_, __) => { if (!_closing) SetHover(idx); };
                sector.MouseLeftButtonUp += (s, e) =>
                {
                    if (_closing) return;
                    e.Handled = true;
                    var item = _currentPageItems[idx];
                    if (item != null && !string.IsNullOrEmpty(item.FamilyPath))
                        SafeComplete(true, _pageIndex * PAGE_SIZE + idx, item);
                };

                // >>> clic droit = context menu "Recharger dernière version"
                sector.ContextMenu = BuildContextMenu(idx);

                _sectors.Add(sector);
                RootCanvas.Children.Add(sector);

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

                border.MouseEnter += (_, __) => { if (!_closing) SetHover(idx); };
                border.MouseLeftButtonUp += (s, e) =>
                {
                    if (_closing) return;
                    e.Handled = true;
                    var item = _currentPageItems[idx];
                    if (item != null && !string.IsNullOrEmpty(item.FamilyPath))
                        SafeComplete(true, _pageIndex * PAGE_SIZE + idx, item);
                };

                // >>> clic droit sur l’icône = même menu
                border.ContextMenu = BuildContextMenu(idx);

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

            RootCanvas.MouseLeftButtonUp += (s, e) =>
            {
                if (_closing) return;
                if (!e.Handled && DateTime.UtcNow >= _canCloseAfter)
                    SafeComplete(false, -1, null);
            };
        }

        // Construit un menu contextuel “Recharger la dernière version…”
        private ContextMenu BuildContextMenu(int idx)
        {
            var cm = new ContextMenu();
            var mi = new MenuItem { Header = "Recharger la dernière version" };
            mi.Click += (s, e) =>
            {
                var item = _currentPageItems[idx];
                if (item == null || string.IsNullOrWhiteSpace(item.FamilyPath)) return;

                try { ReloadRequested?.Invoke(item); } catch { }
                SafeComplete(false, -1, null); // on ferme la rosace après l’action
            };
            cm.Items.Add(mi);
            return cm;
        }

        private int PageCount => 3;

        private void PrevPage() { _pageIndex = (_pageIndex - 1 + PageCount) % PageCount; LoadPage(_pageIndex); UpdatePageLabel(); }
        private void NextPage() { _pageIndex = (_pageIndex + 1) % PageCount; LoadPage(_pageIndex); UpdatePageLabel(); }

        private void LoadPage(int index)
        {
            _pageIndex = index;
            Array.Clear(_currentPageItems, 0, _currentPageItems.Length);

            int offset = index * PAGE_SIZE;
            for (int i = 0; i < PAGE_SIZE; i++)
                _currentPageItems[i] = (offset + i < _items.Count) ? _items[offset + i] : null;

            _hoverOutlinePath.Visibility = Visibility.Collapsed;
            _centerPreview.Source = null;

            for (int i = 0; i < _iconBorders.Count; i++)
            {
                var img = (Image)_iconBorders[i].Child;
                var item = _currentPageItems[i];
                var src = LoadImage(item?.ImagePath);
                img.Source = src;
                _iconBorders[i].Opacity = (item == null || string.IsNullOrEmpty(item.FamilyPath)) ? 0.2 : 1.0;

                var tr = (ScaleTransform)_iconBorders[i].RenderTransform;
                tr.ScaleX = tr.ScaleY = 1.0;
            }
        }

        private void UpdatePageLabel()
        {
            if (_centerLabel == null) return;
            _centerLabel.Text = _pageIndex switch
            {
                0 => "Top-8",
                1 => "Récents (1/2)",
                2 => "Récents (2/2)",
                _ => ""
            };

            _centerLabel.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            _centerLabel.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void SetHover(int i)
        {
            if (i < 0 || i >= _sectors.Count) return;

            for (int k = 0; k < _sectors.Count; k++)
                _sectors[k].Fill = new SolidColorBrush(k == i ? _sectorFillHover : _sectorFill);

            for (int k = 0; k < _iconBorders.Count; k++)
            {
                var tr = (ScaleTransform)_iconBorders[k].RenderTransform;
                double target = (k == i) ? 1.18 : 1.0;
                var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(90)) { EasingFunction = new QuadraticEase() };
                tr.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                tr.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }

            _hoverOutlinePath.Data = _sectors[i].Data?.CloneCurrentValue();
            _hoverOutlinePath.Visibility = Visibility.Visible;

            var item = _currentPageItems[i];
            _centerPreview.Source = LoadImage(item?.ImagePath);
        }

        private void SafeComplete(bool accepted, int globalIndex, RadialItem item)
        {
            if (_closing) return;
            _closing = true;

            try { Completed?.Invoke(accepted, globalIndex, item); } catch { }

            try { if (Mouse.Captured == RootCanvas) Mouse.Capture(null); } catch { }
            try { Close(); } catch { }

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { if (IsVisible) Close(); } catch { }
            }));
        }

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
            geo.Freeze();
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
                var anim = new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(100)) { EasingFunction = new QuadraticEase() };
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

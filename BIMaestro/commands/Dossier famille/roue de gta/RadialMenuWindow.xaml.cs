using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        public event Action<bool, int> Completed; // (accepted, index 0..7)

        private readonly List<string> _allImages;
        private int _pageIndex = 0;
        private const int PAGE_SIZE = 8;
        private readonly string[] _currentPageImages = new string[PAGE_SIZE];

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

        public int SelectedIndex { get; private set; } = -1;
        public bool Accepted { get; private set; } = false;
        private bool _closingPosted = false;

        private readonly List<Path> _sectors = new();
        private readonly List<Border> _iconBorders = new();
        private Path _hoverOutlinePath;
        private Ellipse _centerDisk;
        private Image _centerPreview;
        private Polygon _leftArrow;
        private Polygon _rightArrow;
        private TextBlock _centerLabel;    // << libellé “top-8 / cmd récent”

        private static readonly Dictionary<string, BitmapImage> s_ImageCache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private const int CACHE_MAX = 256;

        public RadialMenuWindow(List<string> allImages, int screenXpx, int screenYpx)
        {
            InitializeComponent();
            _allImages = (allImages ?? new List<string>()).ToList();
            Shuffle(_allImages);
            _screenXpx = screenXpx;
            _screenYpx = screenYpx;

            double diameter = OUTER_R * 2 + 8;
            this.Width = diameter;
            this.Height = diameter;

            Loaded += Window_Loaded;
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
            UpdatePageLabel(); // => "top-8"
            this.Focus();

            RootCanvas.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            RootCanvas.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void Window_Deactivated(object? sender, EventArgs e) => SafeComplete(false, -1);
        private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) SafeComplete(false, -1); }

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

            // --- libellé centré AU-DESSUS de l'image centrale (ajustement dynamique) ---
            _centerLabel = new TextBlock
            {
                Text = "top-8",
                Foreground = new SolidColorBrush(Color.FromArgb(230, 60, 60, 80)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Width = INNER_R * 1.6,
                Opacity = 0.95
            };
            Canvas.SetLeft(_centerLabel, cx - _centerLabel.Width / 2.0);

            // Place le texte juste au-dessus de l'aperçu central (plus bas qu'avant)
            double labelTop = cy - (_centerPreview.Height / 2.0) - 16; // 16 px d’espace
            Canvas.SetTop(_centerLabel, labelTop);
            RootCanvas.Children.Add(_centerLabel);

            _leftArrow = MakeArrowPolygon(isRight: false, size: 18);
            PositionArrow(_leftArrow, cx - INNER_R * 0.65, cy);
            _leftArrow.MouseLeftButtonUp += (s, e) => { e.Handled = true; PrevPage(); };
            RootCanvas.Children.Add(_leftArrow);

            _rightArrow = MakeArrowPolygon(isRight: true, size: 18);
            PositionArrow(_rightArrow, cx + INNER_R * 0.65, cy);
            _rightArrow.MouseLeftButtonUp += (s, e) => { e.Handled = true; NextPage(); };
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

                sector.MouseEnter += (_, __) => SetHover(idx);
                sector.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (!string.IsNullOrEmpty(_currentPageImages[idx]))
                        SafeComplete(true, idx);
                };

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

                border.MouseEnter += (_, __) => SetHover(idx);
                border.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (!string.IsNullOrEmpty(_currentPageImages[idx]))
                        SafeComplete(true, idx);
                };

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

            RootCanvas.MouseLeftButtonUp += (s, e) => { if (!e.Handled) SafeComplete(false, -1); };
        }

        // --- pagination ---
        private int PageCount => Math.Max(1, (_allImages.Count + PAGE_SIZE - 1) / PAGE_SIZE);

        private void PrevPage()
        {
            if (_allImages.Count == 0) return;
            _pageIndex = (_pageIndex - 1 + PageCount) % PageCount;
            LoadPage(_pageIndex);
            UpdatePageLabel();
        }
        private void NextPage()
        {
            if (_allImages.Count == 0) return;
            _pageIndex = (_pageIndex + 1) % PageCount;
            LoadPage(_pageIndex);
            UpdatePageLabel();
        }

        private void LoadPage(int index)
        {
            _pageIndex = index;
            Array.Clear(_currentPageImages, 0, _currentPageImages.Length);
            int start = index * PAGE_SIZE;
            for (int i = 0; i < PAGE_SIZE; i++)
            {
                int g = start + i;
                _currentPageImages[i] = (g < _allImages.Count) ? _allImages[g] : null;
            }

            _hoverOutlinePath.Visibility = Visibility.Collapsed;
            _centerPreview.Source = null;

            for (int i = 0; i < _iconBorders.Count; i++)
            {
                var img = (Image)_iconBorders[i].Child;
                var src = LoadImage(_currentPageImages[i]);
                img.Source = src;
                _iconBorders[i].Opacity = (src == null) ? 0.3 : 1.0;

                var tr = (ScaleTransform)_iconBorders[i].RenderTransform;
                tr.ScaleX = tr.ScaleY = 1.0;
            }
        }

        // --- libellé central : "top-8" ou "cmd récent" ---
        private void UpdatePageLabel()
        {
            if (_centerLabel == null) return;

            _centerLabel.Text = (_pageIndex == 0) ? "top-8" : "cmd récent";

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

            _centerPreview.Source = LoadImage(_currentPageImages[i]);
        }

        private void SafeComplete(bool accepted, int index)
        {
            if (_closingPosted) return;
            _closingPosted = true;
            Accepted = accepted;
            SelectedIndex = index;

            try { Completed?.Invoke(Accepted, SelectedIndex); } catch { }

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { Close(); } catch { }
            }));
        }

        // --- helpers ---
        private static Path CreateSector(Point center, double innerR, double outerR, double startDeg, double sweepDeg)
        {
            double start = startDeg * Math.PI / 180.0;
            double end = (startDeg + sweepDeg) * Math.PI / 180.0;

            Point p1 = new(center.X + outerR * Math.Cos(start), center.Y + outerR * Math.Sin(start));
            Point p2 = new(center.X + outerR * Math.Cos(end), center.Y + outerR * Math.Sin(end));
            Point p3 = new(center.X + innerR * Math.Cos(end), center.Y + innerR * Math.Sin(end));
            Point p4 = new(center.X + innerR * Math.Cos(start), center.Y + innerR * Math.Sin(start));

            bool isLargeArc = sweepDeg > 180.0;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(p1, true, true);
                ctx.ArcTo(p2, new Size(outerR, outerR), 0, isLargeArc, SweepDirection.Clockwise, true, true);
                ctx.LineTo(p3, true, true);
                ctx.ArcTo(p4, new Size(innerR, innerR), 0, isLargeArc, SweepDirection.Counterclockwise, true, true);
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
                    new Point(isRight ? -size/2 : size/2, -size/1.6),
                    new Point(isRight ? -size/2 : size/2,  size/1.6),
                    new Point(isRight ?  size/2 : -size/2, 0)
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

        private static void Shuffle<T>(IList<T> list)
        {
            var rng = new Random();
            for (int n = list.Count - 1; n > 0; n--)
            {
                int k = rng.Next(n + 1);
                (list[n], list[k]) = (list[k], list[n]);
            }
        }

        private static BitmapImage LoadImage(string pathOrResource)
        {
            if (string.IsNullOrWhiteSpace(pathOrResource)) return null;

            if (s_ImageCache.TryGetValue(pathOrResource, out var cached))
                return cached;

            try
            {
                BitmapImage bmp = null;

                if (File.Exists(pathOrResource))
                {
                    using (var fs = new FileStream(pathOrResource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = fs;
                        bmp.EndInit();
                        bmp.Freeze();
                    }
                }
                else
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(pathOrResource))
                    {
                        if (s == null) return null;
                        bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = s;
                        bmp.EndInit();
                        bmp.Freeze();
                    }
                }

                if (bmp != null)
                {
                    if (s_ImageCache.Count >= CACHE_MAX) s_ImageCache.Clear();
                    s_ImageCache[pathOrResource] = bmp;
                }
                return bmp;
            }
            catch { return null; }
        }
    }
}

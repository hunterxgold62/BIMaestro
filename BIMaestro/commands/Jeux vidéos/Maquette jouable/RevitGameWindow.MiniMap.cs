using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX.Elements2D;

namespace BIMaestro.VideoGames
{
    public partial class RevitGameWindow
    {
        private bool _miniMapVisible = true;
        private double _miniMapLastUpdate = double.MinValue;
        private double _miniMapAltitude = double.NaN;
        private DrawingGroup? _miniMapPlan;
        private MemoryStream? _miniMapStream;
        private const double MiniMapSize = 280;
        private const double MiniMapPlanWidth = MiniMapSize - 24;
        private static readonly Rect MiniMapPlanBounds = new Rect(12, 28, MiniMapPlanWidth, MiniMapSize - 42);
        // Fixed local range: 30 metres across, with model X right and Y up.
        private const double MiniMapRange = 98.4252;
        private Point _miniMapCenter;

        private void MiniMapToggle_Click(object sender, RoutedEventArgs e)
        {
            _miniMapVisible = !_miniMapVisible;
            MiniMapImage2D.Visibility = _miniMapVisible ? Visibility.Visible : Visibility.Collapsed;
            _miniMapLastUpdate = double.MinValue;
            if (_miniMapVisible) UpdateMiniMap(_renderFootPosition);
        }

        private Point MiniMapPoint(double x, double y) => new Point(
            MiniMapSize / 2 + (x - _miniMapCenter.X) * MiniMapPlanWidth / MiniMapRange,
            MiniMapSize / 2 - (y - _miniMapCenter.Y) * MiniMapPlanWidth / MiniMapRange);

        private void UpdateMiniMap(Point3D position)
        {
            if (!_miniMapVisible || !_readyToPlay || _isClosing) return;
            double now = _frameClock.Elapsed.TotalSeconds;
            if (now - _miniMapLastUpdate < 0.2) return;
            _miniMapLastUpdate = now;

            // Cache the plan; only the marker is redrawn while moving nearby.
            if (_miniMapPlan == null || Math.Abs(position.Z - _miniMapAltitude) > 1.64 ||
                Math.Abs(position.X - _miniMapCenter.X) > MiniMapRange * 0.25 ||
                Math.Abs(position.Y - _miniMapCenter.Y) > MiniMapRange * 0.25)
            {
                _miniMapAltitude = position.Z;
                _miniMapCenter = new Point(position.X, position.Y);
                _miniMapPlan = new DrawingGroup();
                using (DrawingContext dc = _miniMapPlan.Open())
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromRgb(113, 150, 139)), 0.7);
                    dc.PushClip(new RectangleGeometry(MiniMapPlanBounds));
                    foreach (GameElementData element in _scene.Elements)
                    {
                        if (!element.HasBounds) continue;
                        Rect3D b = element.Bounds;
                        // Slice near eye height, excluding other floors and ceilings.
                        double slice = position.Z + 3.28;
                        if (b.Z > slice || b.Z + b.SizeZ < slice) continue;
                        Point a = MiniMapPoint(b.X, b.Y + b.SizeY);
                        Point c = MiniMapPoint(b.X + b.SizeX, b.Y);
                        var rect = new Rect(a, c);
                        if (!rect.IntersectsWith(MiniMapPlanBounds)) continue;
                        dc.DrawRectangle(null, pen, rect);
                    }
                    dc.Pop();
                }
                _miniMapPlan.Freeze();
            }

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 15, 27, 23)),
                    new Pen(Brushes.SeaGreen, 1), new Rect(0, 0, MiniMapSize - 1, MiniMapSize - 1), 10, 10);
                dc.DrawDrawing(_miniMapPlan);
                var label = new FormattedText("PLAN · Y ↑       30 m · M masquer",
                    System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 10, Brushes.White, 1);
                dc.DrawText(label, new Point(12, 9));
                Point p = MiniMapPoint(position.X, position.Y);
                double dx = Math.Cos(_yaw), dy = -Math.Sin(_yaw);
                var arrow = new StreamGeometry();
                using (StreamGeometryContext g = arrow.Open())
                {
                    g.BeginFigure(new Point(p.X + dx * 12, p.Y + dy * 12), true, true);
                    g.LineTo(new Point(p.X - dx * 6 - dy * 6, p.Y - dy * 6 + dx * 6), true, false);
                    g.LineTo(new Point(p.X - dx * 6 + dy * 6, p.Y - dy * 6 - dx * 6), true, false);
                }
                dc.DrawGeometry(Brushes.Orange, new Pen(Brushes.White, 1), arrow);
            }
            var bitmap = new RenderTargetBitmap((int)MiniMapSize, (int)MiniMapSize, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            MiniMapImage2D.ImageStream = stream;
            _miniMapStream?.Dispose();
            _miniMapStream = stream;
            Canvas2D.SetLeft(MiniMapImage2D, 12);
            Canvas2D.SetTop(MiniMapImage2D, 12);
        }
    }
}

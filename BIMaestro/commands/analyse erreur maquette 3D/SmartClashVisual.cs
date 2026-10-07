using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BIMaestro.Localization;

namespace Analyse
{
    // These immutable snapshots contain no Revit objects. A mesh is shared by every clash involving that object.
    public readonly struct SmartVisualPoint
    {
        public readonly double X, Y, Z;
        public SmartVisualPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static SmartVisualPoint Lerp(SmartVisualPoint a, SmartVisualPoint b, double t) =>
            new SmartVisualPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        public double Axis(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;
    }
    public readonly struct SmartVisualTriangle
    {
        public readonly SmartVisualPoint A, B, C;
        public SmartVisualTriangle(SmartVisualPoint a, SmartVisualPoint b, SmartVisualPoint c) { A = a; B = b; C = c; }
    }
    public sealed class SmartVisualMesh
    {
        internal readonly SmartVisualTriangle[] Triangles;
        public int TriangleCount => Triangles.Length;
        public string Notice { get; }
        internal SmartVisualMesh(IEnumerable<SmartVisualTriangle> triangles, string notice = null)
        { Triangles = triangles.ToArray(); Notice = notice; }
    }
    public sealed class SmartVisualScene
    {
        public SmartVisualMesh Source { get; }
        public SmartVisualMesh Obstacle { get; }
        public SmartVisualPoint Center { get; }
        public double Radius { get; }
        public bool Approximate { get; }
        public bool HasGeometry => Source?.TriangleCount > 0 || Obstacle?.TriangleCount > 0;
        public string Notice { get; }
        private readonly Lazy<ImageSource> _thumbnail;
        public ImageSource Thumbnail => _thumbnail.Value;
        internal SmartVisualScene(SmartVisualMesh source, SmartVisualMesh obstacle, SmartVisualPoint center, double radius,
            bool approximate, string notice)
        { Source = source; Obstacle = obstacle; Center = center; Radius = radius; Approximate = approximate; Notice = notice;
            _thumbnail = new Lazy<ImageSource>(() => HasGeometry ? SmartVisualProjection.Thumbnail(this) : null); }

        internal static IEnumerable<SmartVisualTriangle> Clip(SmartVisualMesh mesh, SmartVisualPoint center, double radius)
        {
            if (mesh == null) yield break;
            foreach (var t in mesh.Triangles)
            {
                var polygon = new List<SmartVisualPoint> { t.A, t.B, t.C };
                for (int axis = 0; axis < 3 && polygon.Count > 0; axis++)
                    for (int side = 0; side < 2 && polygon.Count > 0; side++)
                    {
                        double plane = center.Axis(axis) + (side == 0 ? -radius : radius);
                        var output = new List<SmartVisualPoint>(); var previous = polygon[polygon.Count - 1];
                        double pd = side == 0 ? previous.Axis(axis) - plane : plane - previous.Axis(axis);
                        foreach (var point in polygon)
                        {
                            double d = side == 0 ? point.Axis(axis) - plane : plane - point.Axis(axis);
                            if ((pd >= 0) != (d >= 0)) output.Add(SmartVisualPoint.Lerp(previous, point, pd / (pd - d)));
                            if (d >= 0) output.Add(point);
                            previous = point; pd = d;
                        }
                        polygon = output;
                    }
                for (int i = 1; i + 1 < polygon.Count; i++) yield return new SmartVisualTriangle(polygon[0], polygon[i], polygon[i + 1]);
            }
        }
        internal Point3D Normalize(SmartVisualPoint p) => new Point3D((p.X - Center.X) / Radius, (p.Y - Center.Y) / Radius, (p.Z - Center.Z) / Radius);
    }

    // Small vector drawings in the list; the detailed viewer uses a depth-buffered WPF 3D scene.
    internal static class SmartVisualProjection
    {
        private sealed class Face
        { public Point A, B, C; public double Depth, Light; public bool Source; }
        private sealed class Frame
        {
            internal readonly Face[] Faces;
            private readonly double _x, _y, _scale;
            internal Frame(SmartVisualScene scene)
            {
                Faces = SmartVisualProjection.Faces(scene).OrderBy(f => f.Depth).ToArray();
                var points = Faces.SelectMany(f => new[] { f.A, f.B, f.C }).Concat(new[] { new Point(0, 0) }).ToArray();
                double left = points.Min(p => p.X), right = points.Max(p => p.X), top = points.Min(p => p.Y), bottom = points.Max(p => p.Y);
                _x = (left + right) / 2; _y = (top + bottom) / 2;
                _scale = Math.Min(140 / Math.Max(right - left, .000001), 90 / Math.Max(bottom - top, .000001));
            }
            internal Point Map(Point p) => new Point(80 + (p.X - _x) * _scale, 55 + (p.Y - _y) * _scale);
        }
        private static IEnumerable<Face> Faces(SmartVisualScene scene)
        {
            foreach (var part in new[] { (scene.Source, true), (scene.Obstacle, false) })
                foreach (var t in SmartVisualScene.Clip(part.Item1, scene.Center, scene.Radius))
                {
                    var a = scene.Normalize(t.A); var b = scene.Normalize(t.B); var c = scene.Normalize(t.C);
                    var n = Vector3D.CrossProduct(b - a, c - a); if (n.LengthSquared < 1e-16) continue;
                    n.Normalize();
                    yield return new Face { A = Project(a), B = Project(b), C = Project(c), Source = part.Item2,
                        Depth = a.X + a.Y + a.Z + b.X + b.Y + b.Z + c.X + c.Y + c.Z,
                        Light = .68 + .32 * Math.Abs(Vector3D.DotProduct(n, new Vector3D(.408, .408, .816))) };
                }
        }
        private static Point Project(Point3D p) => new Point((p.X - p.Y) * .7071, (p.X + p.Y) * .4082 - p.Z * .8165);
        private static Color Shade(bool source, double light) => Color.FromRgb((byte)((source ? 225 : 36) * light),
            (byte)((source ? 89 : 109) * light), (byte)((source ? 36 : 196) * light));
        internal static ImageSource Thumbnail(SmartVisualScene scene)
        {
            var frame = new Frame(scene);
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 251)), null, new Rect(0, 0, 160, 110));
                foreach (var f in frame.Faces)
                {
                    var g = new StreamGeometry(); using (var path = g.Open())
                    { path.BeginFigure(frame.Map(f.A), true, true); path.LineTo(frame.Map(f.B), true, false); path.LineTo(frame.Map(f.C), true, false); }
                    g.Freeze(); dc.DrawGeometry(new SolidColorBrush(Shade(f.Source, f.Light)), null, g);
                }
                var pen = new Pen(scene.Approximate ? Brushes.DarkGoldenrod : Brushes.Crimson, 2);
                var marker = frame.Map(new Point(0, 0));
                dc.DrawEllipse(null, pen, marker, 5, 5);
                dc.DrawLine(pen, new Point(marker.X - 8, marker.Y), new Point(marker.X + 8, marker.Y));
                dc.DrawLine(pen, new Point(marker.X, marker.Y - 8), new Point(marker.X, marker.Y + 8));
            }
            drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
        }
        internal static string Svg(SmartVisualScene scene)
        {
            var frame = new Frame(scene);
            var b = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 160 110' role='img' aria-label='Vue locale des objets du conflit'><rect width='160' height='110' fill='#f6f8fb'/>");
            foreach (var f in frame.Faces)
            {
                var color = Shade(f.Source, f.Light);
                b.Append("<polygon points='").Append(Coordinates(frame.Map(f.A))).Append(' ').Append(Coordinates(frame.Map(f.B))).Append(' ')
                    .Append(Coordinates(frame.Map(f.C))).Append("' fill='#").Append(color.R.ToString("X2")).Append(color.G.ToString("X2"))
                    .Append(color.B.ToString("X2")).Append("'/>");
            }
            var marker = frame.Map(new Point(0, 0));
            b.Append("<circle cx='").Append(marker.X.ToString("F2", CultureInfo.InvariantCulture)).Append("' cy='")
                .Append(marker.Y.ToString("F2", CultureInfo.InvariantCulture)).Append("' r='5' fill='none' stroke='").Append(scene.Approximate ? "#b8860b" : "#dc143c")
                .Append("' stroke-width='2'/></svg>"); return b.ToString();
        }
        private static string Coordinates(Point p) => p.X.ToString("F2", CultureInfo.InvariantCulture) + "," + p.Y.ToString("F2", CultureInfo.InvariantCulture);
    }

    public sealed class SmartClashViewer : Border
    {
        private readonly Viewport3D _viewport = new Viewport3D { ClipToBounds = true };
        private readonly PerspectiveCamera _camera = new PerspectiveCamera { FieldOfView = 38, NearPlaneDistance = .01, FarPlaneDistance = 100 };
        private readonly ModelVisual3D _source = new ModelVisual3D(), _obstacle = new ModelVisual3D();
        private double _yaw = Math.PI / 4, _pitch = .5, _distance = 5;
        private Point _last;
        private bool _drag;
        internal PerspectiveCamera Camera => _camera;
        internal int VertexCount { get; private set; }
        public SmartClashViewer(SmartVisualScene scene)
        {
            Height = 285; CornerRadius = new CornerRadius(8); Margin = new Thickness(0, 10, 0, 12);
            SetResourceReference(BackgroundProperty, "Surface");
            var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
            var canvas = new Grid { Background = Brushes.Transparent, ToolTip = UiLanguage.T("Glissez pour tourner · Molette pour zoomer · Repère : centre de la zone détectée") };
            _viewport.Camera = _camera; canvas.Children.Add(_viewport);
            var lights = new Model3DGroup(); lights.Children.Add(new AmbientLight(Color.FromRgb(120, 120, 120)));
            lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -2, -3)));
            _viewport.Children.Add(new ModelVisual3D { Content = lights });
            var sourceModel = Model(scene, scene.Source, Color.FromRgb(225, 89, 36));
            var obstacleModel = Model(scene, scene.Obstacle, Color.FromRgb(36, 109, 196));
            _source.Content = sourceModel; _obstacle.Content = obstacleModel;
            _viewport.Children.Add(_source); _viewport.Children.Add(_obstacle);
            var marker = new TextBlock { Text = "⊕", FontSize = 28, Foreground = scene.Approximate ? Brushes.DarkGoldenrod : Brushes.Crimson,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                ToolTip = UiLanguage.T("Centre de la zone détectée") };
            canvas.Children.Add(marker); Grid.SetRow(canvas, 1); root.Children.Add(canvas);
            var controls = new WrapPanel { Margin = new Thickness(8) };
            controls.Children.Add(Toggle("Objet orange", sourceModel != null, value => _source.Content = value ? sourceModel : null));
            controls.Children.Add(Toggle("Obstacle bleu", obstacleModel != null, value => _obstacle.Content = value ? obstacleModel : null));
            var reset = new Button { Content = UiLanguage.T("Recentrer"), MinWidth = 0, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            reset.SetResourceReference(StyleProperty, "SecondaryButton");
            reset.Click += (s, e) => { _yaw = Math.PI / 4; _pitch = .5; _distance = 5; UpdateCamera(); };
            controls.Children.Add(reset); root.Children.Add(controls); Child = root;
            canvas.MouseLeftButtonDown += (s, e) => { _last = e.GetPosition(canvas); _drag = canvas.CaptureMouse(); e.Handled = true; };
            canvas.MouseMove += (s, e) => { if (!_drag) return; var next = e.GetPosition(canvas); Orbit(next.X - _last.X, next.Y - _last.Y); _last = next; };
            canvas.MouseLeftButtonUp += (s, e) => { _drag = false; canvas.ReleaseMouseCapture(); e.Handled = true; };
            canvas.LostMouseCapture += (s, e) => _drag = false;
            canvas.MouseWheel += (s, e) => { Zoom(e.Delta); e.Handled = true; };
            Unloaded += (s, e) => { _drag = false; canvas.ReleaseMouseCapture(); };
            UpdateCamera();
        }
        private GeometryModel3D Model(SmartVisualScene scene, SmartVisualMesh data, Color color)
        {
            if (data == null || data.TriangleCount == 0) return null;
            var mesh = new MeshGeometry3D();
            foreach (var t in SmartVisualScene.Clip(data, scene.Center, scene.Radius))
            {
                var a = scene.Normalize(t.A); var b = scene.Normalize(t.B); var c = scene.Normalize(t.C);
                if (Vector3D.CrossProduct(b - a, c - a).LengthSquared < 1e-16) continue;
                int index = mesh.Positions.Count; mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
                mesh.TriangleIndices.Add(index); mesh.TriangleIndices.Add(index + 1); mesh.TriangleIndices.Add(index + 2);
            }
            VertexCount += mesh.Positions.Count; mesh.Freeze();
            var material = new DiffuseMaterial(new SolidColorBrush(color)); material.Freeze();
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material }; model.Freeze(); return model;
        }
        private static CheckBox Toggle(string label, bool enabled, Action<bool> changed)
        {
            var check = new CheckBox { Content = UiLanguage.T(label), IsChecked = enabled, IsEnabled = enabled, Margin = new Thickness(0, 5, 12, 0) };
            check.Checked += (s, e) => changed(true); check.Unchecked += (s, e) => changed(false); return check;
        }
        internal void Orbit(double x, double y) { _yaw -= x * .012; _pitch = Math.Max(-1.45, Math.Min(1.45, _pitch + y * .012)); UpdateCamera(); }
        internal void Zoom(int delta) { _distance = Math.Max(2.7, Math.Min(12, _distance * Math.Pow(.85, delta / 120.0))); UpdateCamera(); }
        private void UpdateCamera()
        {
            _camera.UpDirection = new Vector3D(0, 0, 1);
            _camera.Position = new Point3D(_distance * Math.Cos(_pitch) * Math.Cos(_yaw), _distance * Math.Cos(_pitch) * Math.Sin(_yaw), _distance * Math.Sin(_pitch));
            _camera.LookDirection = new Vector3D(-_camera.Position.X, -_camera.Position.Y, -_camera.Position.Z);
        }
    }
}

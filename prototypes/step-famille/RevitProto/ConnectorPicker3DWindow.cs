using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BIMaestro.ProtoStep
{
    // Fenêtre de choix des raccordements : l'appareil en 3D simplifiée, une pastille par raccordement possible.
    // Clic sur une pastille : rouge (ignoré) <-> vert (connecteur créé). La liste à droite sert à nommer le réseau.
    internal sealed class ConnectorPicker3DWindow : Window
    {
        // Données d'entrée, déjà en mètres, repère Revit (Z vers le haut)
        internal sealed class MeshPart { public Color Color; public List<Point3D> Tris = new List<Point3D>(); }

        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(30, 170, 80));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(215, 55, 50));
        private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(250, 200, 30));

        private readonly List<ConnectorRow> rows;
        private readonly Dictionary<GeometryModel3D, ConnectorRow> markerRow = new Dictionary<GeometryModel3D, ConnectorRow>();
        private readonly Dictionary<ConnectorRow, GeometryModel3D> rowMarker = new Dictionary<ConnectorRow, GeometryModel3D>();
        private readonly Viewport3D view = new Viewport3D();
        private readonly PerspectiveCamera cam = new PerspectiveCamera { FieldOfView = 40 };
        private readonly DataGrid grid;
        private Point3D target; private double dist, yaw = -0.8, pitch = 0.5;
        private Point last; private bool rotating, panning, moved;
        private ConnectorRow highlighted; private bool fromView;

        internal ConnectorPicker3DWindow(List<MeshPart> model, List<ConnectorRow> rows, string title)
        {
            this.rows = rows;
            Title = "Raccordements — " + title; Width = 1300; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel { Margin = new Thickness(10) };
            var help = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
                Text = "Cliquez une pastille pour la passer du rouge (ignorée) au vert (connecteur créé), ou l'inverse.   "
                     + "Glisser clic gauche : tourner · molette : zoom · glisser clic droit : déplacer.   "
                     + "Dans la liste, donnez le réseau des pastilles vertes ; un clic sur une ligne la fait clignoter en jaune.",
            };
            DockPanel.SetDock(help, Dock.Top); root.Children.Add(help);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "Créer la famille", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Annuler", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
            ok.Click += (_, __) => DialogResult = true;
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

            // liste à droite
            grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, Width = 520, Margin = new Thickness(8, 0, 0, 0), SelectionMode = DataGridSelectionMode.Single };
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Créer", Binding = new Binding("Keep") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Réseau", ItemsSource = ConnectorReviewWindow.Labels, SelectedItemBinding = new Binding("Label"), Width = 150 });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Type", ItemsSource = ConnectorRow.Kinds, SelectedItemBinding = new Binding("Kind"), Width = 100 });
            grid.Columns.Add(new DataGridTextColumn { Header = "DN", Binding = new Binding("Diameter"), Width = 50 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Nom", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.SelectionChanged += (_, __) => { if (!fromView) Highlight(grid.SelectedItem as ConnectorRow); };
            grid.CurrentCellChanged += (_, __) => Dispatcher.BeginInvoke(new Action(Repaint));
            DockPanel.SetDock(grid, Dock.Right); root.Children.Add(grid);

            // vue 3D
            var border = new Border { Background = new SolidColorBrush(Color.FromRgb(245, 246, 248)), Child = view, ClipToBounds = true };
            root.Children.Add(border);
            Content = root;

            view.Camera = cam;
            var scene = new Model3DGroup();
            scene.Children.Add(new AmbientLight(Color.FromRgb(110, 110, 110)));
            scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.4, -0.6, -1)));
            scene.Children.Add(new DirectionalLight(Color.FromRgb(120, 120, 120), new Vector3D(0.5, 0.7, 0.3)));
            foreach (var part in model.Where(p => p.Tris.Count >= 3))
            {
                var mesh = new MeshGeometry3D();
                foreach (var p in part.Tris) mesh.Positions.Add(ToView(p));
                var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(255, part.Color.R, part.Color.G, part.Color.B)));
                scene.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
            }
            var all = model.SelectMany(m => m.Tris).ToList();
            if (all.Count == 0) all = rows.Select(r => new Point3D(r.P[0] / 1000, r.P[1] / 1000, r.P[2] / 1000)).ToList();
            var min = new Point3D(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z));
            var max = new Point3D(all.Max(p => p.X), all.Max(p => p.Y), all.Max(p => p.Z));
            double size = (max - min).Length;
            foreach (var r in rows)
            {
                var m = Disc(new Point3D(r.P[0] / 1000, r.P[1] / 1000, r.P[2] / 1000), new Vector3D(r.Dir[0], r.Dir[1], r.Dir[2]),
                             Math.Max(r.Diameter / 2000.0 + 0.03, size * 0.012), Math.Max(0.04, size * 0.006));
                markerRow[m] = r; rowMarker[r] = m; scene.Children.Add(m);
            }
            view.Children.Add(new ModelVisual3D { Content = scene });
            target = ToView(new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2));
            dist = size * 1.4; UpdateCamera(); Repaint();

            view.MouseDown += OnDown; view.MouseMove += OnMove; view.MouseUp += OnUp; view.MouseWheel += (_, e) => { dist *= e.Delta > 0 ? 0.85 : 1.18; UpdateCamera(); };
            border.MouseDown += OnDown; border.MouseMove += OnMove; border.MouseUp += OnUp;
        }

        internal List<ConnectorRow> Accepted => rows.Where(r => r.Keep).ToList();

        // Revit (X, Y, Z haut) -> WPF (X, Y haut, Z vers l'observateur)
        private static Point3D ToView(Point3D p) => new Point3D(p.X, p.Z, -p.Y);
        private static Vector3D ToView(Vector3D v) => new Vector3D(v.X, v.Z, -v.Y);

        private GeometryModel3D Disc(Point3D c, Vector3D n, double r, double thick)
        {
            var o = ToView(c); var d = ToView(n); d.Normalize();
            var u = Vector3D.CrossProduct(d, Math.Abs(d.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0)); u.Normalize();
            var w = Vector3D.CrossProduct(d, u);
            var mesh = new MeshGeometry3D(); const int seg = 28;
            var b = o; var t = o + d * thick;
            for (int i = 0; i < seg; i++)
            {
                double a0 = 2 * Math.PI * i / seg, a1 = 2 * Math.PI * (i + 1) / seg;
                var p0 = u * Math.Cos(a0) * r + w * Math.Sin(a0) * r; var p1 = u * Math.Cos(a1) * r + w * Math.Sin(a1) * r;
                void Tri(Point3D x, Point3D y, Point3D z) { mesh.Positions.Add(x); mesh.Positions.Add(y); mesh.Positions.Add(z); }
                Tri(t, t + p0, t + p1); Tri(b, b + p1, b + p0); Tri(b + p0, b + p1, t + p1); Tri(b + p0, t + p1, t + p0);
            }
            var mat = new DiffuseMaterial(Red);
            return new GeometryModel3D(mesh, mat) { BackMaterial = mat };
        }

        private void Repaint()
        {
            foreach (var kv in rowMarker)
            {
                var brush = kv.Key == highlighted ? Yellow : kv.Key.Keep ? Green : Red;
                var mat = new DiffuseMaterial(brush); kv.Value.Material = mat; kv.Value.BackMaterial = mat;
            }
        }

        private void Highlight(ConnectorRow r) { highlighted = r; Repaint(); }

        private void UpdateCamera()
        {
            pitch = Math.Max(-1.45, Math.Min(1.45, pitch));
            var dir = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw));
            cam.Position = target + dir * dist; cam.LookDirection = -dir * dist; cam.UpDirection = new Vector3D(0, 1, 0);
            cam.NearPlaneDistance = dist * 0.01; cam.FarPlaneDistance = dist * 100;
        }

        private void OnDown(object s, MouseButtonEventArgs e)
        {
            last = e.GetPosition(view); moved = false;
            if (e.ChangedButton == MouseButton.Left) rotating = true;
            if (e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Middle) panning = true;
            view.CaptureMouse();
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            if (!rotating && !panning) return;
            var p = e.GetPosition(view); var dx = p.X - last.X; var dy = p.Y - last.Y; last = p;
            if (Math.Abs(dx) + Math.Abs(dy) > 1) moved = true;
            if (rotating) { yaw -= dx * 0.01; pitch += dy * 0.01; }
            else
            {
                var look = cam.LookDirection; look.Normalize();
                var right = Vector3D.CrossProduct(look, cam.UpDirection); right.Normalize();
                var up = Vector3D.CrossProduct(right, look);
                double k = dist * 0.0015; target += -right * dx * k + up * dy * k;
            }
            UpdateCamera();
        }

        private void OnUp(object s, MouseButtonEventArgs e)
        {
            bool click = rotating && !moved && e.ChangedButton == MouseButton.Left;
            rotating = panning = false; view.ReleaseMouseCapture();
            if (!click) return;
            // clic sans glisser : bascule la pastille touchée
            GeometryModel3D hit = null;
            VisualTreeHelper.HitTest(view, null, r =>
            {
                if (r is RayMeshGeometry3DHitTestResult m && m.ModelHit is GeometryModel3D g && markerRow.ContainsKey(g)) { hit = g; return HitTestResultBehavior.Stop; }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(e.GetPosition(view)));
            if (hit == null) return;
            var row = markerRow[hit]; row.Keep = !row.Keep;
            highlighted = null; fromView = true; grid.Items.Refresh(); grid.SelectedItem = row; grid.ScrollIntoView(row); fromView = false;
            Repaint();
        }
    }
}

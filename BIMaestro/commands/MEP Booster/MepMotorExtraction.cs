using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Licensing;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using Point = System.Windows.Point;
using Line = System.Windows.Shapes.Line;
using Transform = Autodesk.Revit.DB.Transform;

namespace BIMaestro.MepBooster
{
    [Transaction(TransactionMode.Manual)]
    public sealed class MepMotorExtractionCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "MepMotorExtraction";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui == null || ui.Document.IsFamilyDocument) return Result.Cancelled;
            if (!(ui.ActiveView is View3D view) || view.IsPerspective)
            {
                TaskDialog.Show("Extraction moteur", "Ouvrez une vue 3D orthographique et cadrez la pompe avec son environnement.");
                return Result.Cancelled;
            }
            try
            {
                var selected = ui.Selection.GetElementIds().Select(ui.Document.GetElement).OfType<FamilyInstance>().ToList();
                var pump = selected.Count == 1 && PumpFilter.Accept(selected[0]) ? selected[0]
                    : ui.Document.GetElement(ui.Selection.PickObject(ObjectType.Element, new PumpFilter(),
                        "Sélectionnez la pompe (équipement mécanique).")) as FamilyInstance;
                var projection = BoosterProjection.Read(ui);
                if (pump == null || projection == null) return Result.Cancelled;
                var dialog = new MotorExtractionWindow(ui.Document, pump, projection, data.Application.MainWindowHandle);
                dialog.ShowDialog();
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
        private sealed class PumpFilter : ISelectionFilter
        {
            internal static bool Accept(Element e) => e is FamilyInstance && BoosterIds.Value(e.Category?.Id) == (long)BuiltInCategory.OST_MechanicalEquipment;
            public bool AllowElement(Element e) => Accept(e);
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }

    // A box in a rigid local frame. Extending its interval along a local axis gives
    // the exact continuous swept volume for an axis-aligned translation (no sampling gaps).
    internal sealed class MotorEnvelope
    {
        internal Transform Frame;
        internal XYZ Center, Size;
        internal int Axis, Sign;
        internal double Travel, Margin;
        internal XYZ Direction => new[] { Frame.BasisX, Frame.BasisY, Frame.BasisZ }[Axis] * Sign;
        internal XYZ[] Corners(double distance, bool clearance = false)
        {
            double m = clearance ? Margin : 0;
            var half = Size / 2 + new XYZ(m, m, m);
            return Enumerable.Range(0, 8).Select(i => Frame.OfPoint(Center + new XYZ(
                (i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z)) + Direction * distance).ToArray();
        }
        internal Solid Sweep(double distance)
        {
            var half = Size / 2 + new XYZ(Margin, Margin, Margin);
            var lo = Center - half;
            var hi = Center + half;
            var shift = new[] { 0.0, 0.0, 0.0 }; shift[Axis] = Sign * distance;
            lo += new XYZ(Math.Min(0, shift[0]), Math.Min(0, shift[1]), Math.Min(0, shift[2]));
            hi += new XYZ(Math.Max(0, shift[0]), Math.Max(0, shift[1]), Math.Max(0, shift[2]));
            var points = new[] { new XYZ(lo.X, lo.Y, lo.Z), new XYZ(hi.X, lo.Y, lo.Z),
                new XYZ(hi.X, hi.Y, lo.Z), new XYZ(lo.X, hi.Y, lo.Z) }.Select(Frame.OfPoint).ToArray();
            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++) loop.Append(Autodesk.Revit.DB.Line.CreateBound(points[i], points[(i + 1) % 4]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, Frame.BasisZ, hi.Z - lo.Z);
        }
        internal static Outline Bounds(IEnumerable<XYZ> points)
        {
            var p = points.ToArray();
            return new Outline(new XYZ(p.Min(v => v.X), p.Min(v => v.Y), p.Min(v => v.Z)),
                new XYZ(p.Max(v => v.X), p.Max(v => v.Y), p.Max(v => v.Z)));
        }
    }

    internal sealed class MotorObstacle
    {
        internal string Label;
        internal double First;
        internal XYZ[] Box;
    }
    internal sealed class MotorExtractionReport
    {
        internal readonly List<MotorObstacle> Obstacles = new List<MotorObstacle>();
        internal readonly List<string> Unknown = new List<string>();
        internal double? First => Obstacles.Count == 0 ? (double?)null : Obstacles.Min(o => o.First);
    }
    internal static class MotorExtractionAnalysis
    {
        internal static MotorExtractionReport Run(Document doc, FamilyInstance pump, MotorEnvelope envelope)
        {
            var report = new MotorExtractionReport();
            var ignored = new HashSet<long>();
            void Ignore(FamilyInstance instance)
            {
                if (!ignored.Add(BoosterIds.Value(instance.Id))) return;
                foreach (var id in instance.GetSubComponentIds())
                    if (doc.GetElement(id) is FamilyInstance child) Ignore(child);
            }
            Ignore(pump);
            using (var swept = envelope.Sweep(envelope.Travel))
            using (var bounds = MotorEnvelope.Bounds(envelope.Corners(0, true).Concat(envelope.Corners(envelope.Travel, true))))
            {
                Scan(doc, Transform.Identity, "", true);
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    var linked = link.GetLinkDocument();
                    if (linked == null) { report.Unknown.Add("Lien non chargé : " + link.Name); continue; }
                    Scan(linked, link.GetTotalTransform(), link.Name + " / ", false);
                }
                void Scan(Document source, Transform transform, string prefix, bool host)
                {
                    // Transform all eight broad-phase corners into the linked model's frame.
                    var world = new[] { bounds.MinimumPoint, bounds.MaximumPoint };
                    var corners = Enumerable.Range(0, 8).Select(i => transform.Inverse.OfPoint(new XYZ(
                        world[(i & 1)].X, world[(i >> 1) & 1].Y, world[(i >> 2) & 1].Z)));
                    using (var localBounds = MotorEnvelope.Bounds(corners))
                    using (var filter = new BoundingBoxIntersectsFilter(localBounds))
                    using (var collector = new FilteredElementCollector(source).WhereElementIsNotElementType().WherePasses(filter))
                    foreach (var element in collector)
                    {
                        if (element is RevitLinkInstance)
                        {
                            if (!host) report.Unknown.Add(prefix + element.Name + " : lien imbriqué non analysé");
                            continue;
                        }
                        if (element.Category == null || element.Category.CategoryType != CategoryType.Model
                            || (host && ignored.Contains(BoosterIds.Value(element.Id)))) continue;
                        string label = prefix + element.Name + " [" + BoosterIds.Value(element.Id) + "]";
                        var solids = new List<Solid>();
                        bool incomplete = false;
                        try
                        {
                            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine })
                            using (var geometry = element.get_Geometry(options))
                            {
                                if (geometry != null) Read(geometry);
                                void Read(GeometryElement shape)
                                {
                                    foreach (var item in shape)
                                    {
                                        if (item is GeometryInstance nested)
                                        { using (var expanded = nested.GetInstanceGeometry()) Read(expanded); }
                                        else if (item is Solid solid && solid.Volume > 1e-12)
                                            solids.Add(SolidUtils.CreateTransformed(solid, transform));
                                        else if (item is Mesh) incomplete = true;
                                    }
                                }
                            }
                            if (solids.Count == 0 || incomplete) report.Unknown.Add(label + " : géométrie incomplète");
                            bool Intersects(Solid volume)
                            {
                                foreach (var solid in solids)
                                using (var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(volume, solid, BooleanOperationsType.Intersect))
                                    if (intersection.Volume > 1e-10) return true;
                                return false;
                            }
                            if (!Intersects(swept)) continue;
                            double high = MotorSweepMath.FirstContact(envelope.Travel, 1.0 / 304.8,
                                distance => { using (var test = envelope.Sweep(distance)) return Intersects(test); }).Value;
                            var box = element.get_BoundingBox(null);
                            XYZ[] boxPoints = box == null ? null : Enumerable.Range(0, 8).Select(i => transform.OfPoint(box.Transform.OfPoint(new XYZ(
                                (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                                (i & 4) == 0 ? box.Min.Z : box.Max.Z)))).ToArray();
                            report.Obstacles.Add(new MotorObstacle { Label = label, First = high, Box = boxPoints });
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException || ex is InvalidOperationException)
                        { report.Unknown.Add(label + " : contrôle géométrique impossible"); }
                        finally { foreach (var solid in solids) solid.Dispose(); }
                    }
                }
            }
            report.Obstacles.Sort((a, b) => a.First.CompareTo(b.First));
            return report;
        }
    }

    internal sealed class MotorGhost : Window
    {
        private readonly Canvas _canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        internal MotorGhost(IntPtr owner)
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; ShowActivated = false; ShowInTaskbar = false; Focusable = false;
            Content = _canvas; new WindowInteropHelper(this).Owner = owner;
            SourceInitialized += (_, __) => BoosterNative.Style(this, true);
        }
        internal void Draw(BoosterProjection projection, MotorEnvelope e, double distance, MotorExtractionReport report)
        {
            new WindowInteropHelper(this).EnsureHandle();
            var start = BoosterNative.Dip(this, projection.Left, projection.Top);
            var end = BoosterNative.Dip(this, projection.Right, projection.Bottom);
            Left = start.X; Top = start.Y; Width = end.X - start.X; Height = end.Y - start.Y;
            _canvas.Children.Clear();
            Point Map(XYZ p) { var q = projection.Pixel(p); var dip = BoosterNative.Dip(this, q.X, q.Y); return new Point(dip.X - Left, dip.Y - Top); }
            void Edge(XYZ a, XYZ b, Brush brush, bool dashed)
            {
                var p = Map(a); var q = Map(b);
                var line = new Line { X1 = p.X, Y1 = p.Y, X2 = q.X, Y2 = q.Y, Stroke = brush, StrokeThickness = 2 };
                if (dashed) line.StrokeDashArray = new DoubleCollection { 3, 3 };
                _canvas.Children.Add(line);
            }
            void Box(XYZ[] points, Brush brush, bool dashed)
            {
                if (points == null) return;
                for (int i = 0; i < 8; i++) foreach (int bit in new[] { 1, 2, 4 })
                    if ((i & bit) == 0) Edge(points[i], points[i | bit], brush, dashed);
            }
            Box(e.Corners(0), Brushes.SlateGray, true);
            Box(e.Corners(e.Travel, true), Brushes.DarkOrange, true);
            bool blocked = report?.First is double hit && distance >= hit;
            Brush color = blocked ? Brushes.OrangeRed : report == null || report.Unknown.Count > 0 ? Brushes.DarkOrange : Brushes.DeepSkyBlue;
            var moving = e.Corners(distance);
            foreach (var face in new[] { new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 },
                new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 } })
                _canvas.Children.Add(new Polygon { Points = new PointCollection(face.Select(i => Map(moving[i]))), Fill = color, Opacity = 0.08 });
            Box(moving, color, false);
            var center = e.Frame.OfPoint(e.Center);
            Edge(center, center + e.Direction * e.Travel, Brushes.DarkOrange, true);
            var tip = Map(center + e.Direction * e.Travel); var tail = Map(center);
            var direction = tip - tail;
            if (direction.Length > 1)
            {
                direction.Normalize(); var normal = new Vector(-direction.Y, direction.X);
                _canvas.Children.Add(new Polyline { Points = new PointCollection { tip - direction * 12 + normal * 6, tip, tip - direction * 12 - normal * 6 },
                    Stroke = Brushes.DarkOrange, StrokeThickness = 3 });
            }
            if (report != null) foreach (var obstacle in report.Obstacles.Take(30)) Box(obstacle.Box, Brushes.OrangeRed, true);
            Show();
        }
    }

    internal sealed class MotorExtractionWindow : Window
    {
        private readonly Document _doc;
        private readonly FamilyInstance _pump;
        private readonly BoosterProjection _projection;
        private readonly Transform _frame;
        private readonly MotorGhost _ghost;
        private readonly Dictionary<string, TextBox> _fields = new Dictionary<string, TextBox>();
        private readonly ComboBox _direction = new ComboBox { ItemsSource = new[] { "+X local", "−X local", "+Y local", "−Y local", "+Z local", "−Z local" }, SelectedIndex = 0 };
        private readonly Slider _position = new Slider { Minimum = 0, Maximum = 100, Value = 0 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        private readonly TextBlock _distance = new TextBlock();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        private MotorEnvelope _envelope;
        private MotorExtractionReport _report;
        internal MotorExtractionWindow(Document doc, FamilyInstance pump, BoosterProjection projection, IntPtr owner)
        {
            _doc = doc; _pump = pump; _projection = projection;
            Title = "MEP · Extraction moteur — prototype"; Width = 430; Height = 790; MinHeight = 500;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false;
            new WindowInteropHelper(this).Owner = owner; BoosterTheme.Attach(this);
            Background = (Brush)FindResource("Surface");
            _ghost = new MotorGhost(owner);
            var t = pump.GetTransform();
            _frame = Transform.Identity; _frame.Origin = t.Origin;
            _frame.BasisX = t.BasisX.Normalize(); _frame.BasisY = t.BasisY.Normalize();
            _frame.BasisZ = t.BasisZ.Normalize();
            var box = pump.get_BoundingBox(null) ?? throw new InvalidOperationException("La pompe n’a pas de volume géométrique.");
            var corners = Enumerable.Range(0, 8).Select(i => _frame.Inverse.OfPoint(box.Transform.OfPoint(new XYZ(
                (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                (i & 4) == 0 ? box.Min.Z : box.Max.Z)))).ToArray();
            var min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z));
            var max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z));
            var size = max - min; var center = (max + min) / 2;
            var panel = new StackPanel { Margin = new Thickness(18) };
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text = "Extraction rectiligne du moteur", FontSize = 19, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = pump.Name + " · " + BoosterIds.Value(pump.Id), Margin = new Thickness(0, 6, 0, 8) });
            panel.Children.Add(new TextBlock { Text = "Réglez le pavé sur le moteur. Le volume initial couvre la pompe entière : ce n’est pas une reconnaissance du moteur. Dimensions et décalages en mm, selon les axes locaux de la pompe.", TextWrapping = TextWrapping.Wrap });
            Field("X", "Longueur X", Math.Max(10, size.X * 304.8));
            Field("Y", "Largeur Y", Math.Max(10, size.Y * 304.8));
            Field("Z", "Hauteur Z", Math.Max(10, size.Z * 304.8));
            Field("CX", "Centre X / origine famille", center.X * 304.8);
            Field("CY", "Centre Y / origine famille", center.Y * 304.8);
            Field("CZ", "Centre Z / origine famille", center.Z * 304.8);
            panel.Children.Add(new TextBlock { Text = "Direction d’extraction", Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(_direction);
            Field("Travel", "Distance d’extraction", 600);
            Field("Margin", "Marge autour du moteur", 10);
            var check = new Button { Content = "Analyser tout le trajet", Margin = new Thickness(0, 12, 0, 8), Style = (Style)FindResource("PrimaryButton") };
            panel.Children.Add(check);
            var animate = new Button { Content = "▶ Lire / arrêter l’extraction", Style = (Style)FindResource("SecondaryButton") };
            panel.Children.Add(animate); panel.Children.Add(_position); panel.Children.Add(_distance); panel.Children.Add(_status);
            panel.Children.Add(new TextBlock { Text = "Bleu : position avant le premier obstacle détecté · Rouge : obstacle rencontré sur le trajet · Orange : non analysé ou incomplet.\n\nLa pompe sélectionnée et ses sous-composants sont exclus. Le test ne vérifie pas le démontage à l’intérieur de la pompe. Liens Revit directs chargés inclus ; liens imbriqués, fichiers CAO et maillages non certifiés. Contrôle du volume saisi, hors outillage, levage et prescriptions fabricant.", TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            var close = new Button { Content = "Fermer", Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
            panel.Children.Add(close); close.Click += (_, __) => Close();
            foreach (var input in _fields.Values) input.TextChanged += (_, __) => Invalidate();
            _direction.SelectionChanged += (_, __) => Invalidate();
            _position.ValueChanged += (_, __) => Draw();
            check.Click += (_, __) =>
            {
                _timer.Stop();
                try
                {
                    _envelope = Read(); _report = null;
                    using (new WaitCursor()) _report = MotorExtractionAnalysis.Run(_doc, _pump, _envelope);
                    _position.Value = 0;
                    _status.Text = (_report.First.HasValue
                        ? "Obstacle rencontré vers " + (_report.First.Value * 304.8).ToString("0") + " mm (précision de recherche ≈ 1 mm).\n"
                        : "Aucun obstacle détecté sur " + (_envelope.Travel * 304.8).ToString("0") + " mm.\n")
                        + string.Join("\n", _report.Obstacles.Take(8).Select(o => "• " + o.Label + " — " + (o.First * 304.8).ToString("0") + " mm"))
                        + (_report.Unknown.Count == 0 ? "" : "\nContrôle incomplet : " + _report.Unknown.Count + " élément(s) ou lien(s).\n" + string.Join("\n", _report.Unknown.Take(5)));
                    Draw();
                }
                catch (Exception ex) { _report = null; _status.Text = "Analyse impossible : " + ex.Message; Draw(); }
            };
            animate.Click += (_, __) =>
            {
                if (_timer.IsEnabled) _timer.Stop();
                else if (_envelope != null) { if (_position.Value >= 100) _position.Value = 0; _timer.Start(); }
            };
            _timer.Tick += (_, __) => { _position.Value = Math.Min(100, _position.Value + 1); if (_position.Value >= 100) _timer.Stop(); };
            Loaded += (_, __) => Invalidate();
            Closed += (_, __) => { _timer.Stop(); _ghost.Close(); };
            void Field(string key, string label, double value)
            {
                var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                var input = new TextBox { Text = value.ToString("0.#", CultureInfo.CurrentCulture), Padding = new Thickness(5) };
                Grid.SetColumn(input, 1); row.Children.Add(input); panel.Children.Add(row); _fields.Add(key, input);
            }
        }
        private MotorEnvelope Read()
        {
            double Number(string key, bool positive = false)
            {
                if (!double.TryParse(_fields[key].Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    || double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > 1000000 || (positive && n < 2))
                    throw new InvalidOperationException("Valeur invalide : " + key + ". Dimensions et course : au moins 2 mm.");
                return n / 304.8;
            }
            var margin = Number("Margin"); if (margin < 0) throw new InvalidOperationException("La marge doit être positive ou nulle.");
            return new MotorEnvelope { Frame = _frame, Center = new XYZ(Number("CX"), Number("CY"), Number("CZ")),
                Size = new XYZ(Number("X", true), Number("Y", true), Number("Z", true)),
                Axis = _direction.SelectedIndex / 2, Sign = _direction.SelectedIndex % 2 == 0 ? 1 : -1,
                Travel = Number("Travel", true), Margin = margin };
        }
        private void Invalidate()
        {
            _timer.Stop(); _report = null; _position.Value = 0;
            try { _envelope = Read(); _status.Text = "Volume modifié : lancez l’analyse du trajet."; Draw(); }
            catch (Exception ex) { _envelope = null; _ghost.Hide(); _status.Text = ex.Message; }
        }
        private void Draw()
        {
            if (_envelope == null) return;
            double distance = _envelope.Travel * _position.Value / 100;
            _distance.Text = "Position du moteur : " + (distance * 304.8).ToString("0") + " mm / " + (_envelope.Travel * 304.8).ToString("0") + " mm";
            _ghost.Draw(_projection, _envelope, distance, _report);
        }
        private sealed class WaitCursor : IDisposable
        {
            private readonly System.Windows.Input.Cursor _previous = System.Windows.Input.Mouse.OverrideCursor;
            internal WaitCursor() { System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait; }
            public void Dispose() { System.Windows.Input.Mouse.OverrideCursor = _previous; }
        }
    }
}

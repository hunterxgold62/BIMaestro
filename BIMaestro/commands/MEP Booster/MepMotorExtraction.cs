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
using BIMaestro.Localization;

namespace BIMaestro.MepBooster
{
    [Transaction(TransactionMode.Manual)]
    public sealed class MepMotorExtractionCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "MepMotorExtraction";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
            => RunWorkflow(data, ref message);
        internal static Result RunWorkflow(ExternalCommandData data, ref string message)
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
                        UiLanguage.T("Sélectionnez la pompe (équipement mécanique).", "Select the pump (mechanical equipment)."))) as FamilyInstance;
                var projection = BoosterProjection.Read(ui);
                if (pump == null || projection == null) return Result.Cancelled;
                var state = new MotorExtractionState();
                while (true)
                {
                    var dialog = new MotorExtractionWindow(ui, pump, BoosterProjection.Read(ui) ?? projection, data.Application.MainWindowHandle, state);
                    dialog.ShowDialog();
                    if (dialog.PickRequest == null) break;
                    try
                    {
                        if (dialog.PickRequest == "motor")
                            state.MotorPoint = ui.Selection.PickObject(ObjectType.PointOnElement, UiLanguage.T("Cliquez le moteur pour positionner le centre XY du volume.", "Click the motor to position the XY center of the volume.")).GlobalPoint;
                        else
                        {
                            var reference = ui.Selection.PickObject(ObjectType.PointOnElement, new DoorFilter(ui.Document), UiLanguage.T("Cliquez la dernière porte de sortie (maquette ou lien Revit).", "Click the final exit door (model or Revit link)."));
                            var element = ui.Document.GetElement(reference.ElementId);
                            var link = element as RevitLinkInstance;
                            var door = (link == null ? element : link.GetLinkDocument().GetElement(reference.LinkedElementId)) as FamilyInstance;
                            if (door == null) continue;
                            var transform = link?.GetTotalTransform() ?? Transform.Identity;
                            var box = door.get_BoundingBox(null);
                            var location = (door.Location as LocationPoint)?.Point ?? (box.Min + box.Max) / 2;
                            state.DoorPoint = transform.OfPoint(location);
                            state.DoorNormal = transform.OfVector(door.GetTransform().BasisY).Normalize();
                            state.DoorWidth = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()
                                ?? door.Symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0;
                            state.DoorHeight = door.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble()
                                ?? door.Symbol.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0;
                            state.DoorSill = transform.OfPoint(location).Z;
                            state.DoorId = door.Id; state.DoorLinkId = link?.Id ?? ElementId.InvalidElementId;
                            state.DoorName = door.Name + " [" + BoosterIds.Value(door.Id) + "]";
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                }
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
        private sealed class DoorFilter : ISelectionFilter
        {
            private readonly Document _doc;
            internal DoorFilter(Document doc) { _doc = doc; }
            public bool AllowElement(Element e) => e is RevitLinkInstance || BoosterIds.Value(e.Category?.Id) == (long)BuiltInCategory.OST_Doors;
            public bool AllowReference(Reference r, XYZ p)
            {
                var e = _doc.GetElement(r.ElementId);
                if (e is RevitLinkInstance link) e = link.GetLinkDocument()?.GetElement(r.LinkedElementId);
                return BoosterIds.Value(e?.Category?.Id) == (long)BuiltInCategory.OST_Doors;
            }
        }
    }

    internal sealed class MotorExtractionState
    {
        internal readonly Dictionary<string, string> Fields = new Dictionary<string, string>();
        internal int Direction;
        internal XYZ DoorPoint, DoorNormal, MotorPoint;
        internal ElementId DoorId = ElementId.InvalidElementId, DoorLinkId = ElementId.InvalidElementId;
        internal string DoorName;
        internal double DoorWidth, DoorHeight, DoorSill;
        internal bool AllowIntermediateDoors = true;
    }

    // A box in a rigid local frame. Extending its interval along a local axis gives
    // the exact continuous swept volume for an axis-aligned translation (no sampling gaps).
    internal sealed class MotorEnvelope
    {
        internal Transform Frame;
        internal XYZ Center, Size;
        internal int Axis, Sign;
        internal double Travel, Margin;
        internal XYZ TranslationDirection;
        internal XYZ Direction => TranslationDirection ?? new[] { Frame.BasisX, Frame.BasisY, Frame.BasisZ }[Axis] * Sign;
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
        // A translated convex box sweeps its initial box plus prisms extruded
        // from the faces pointing into the movement. This is continuous and exact
        // for an arbitrary translation, without an oversized diagonal bounding box.
        internal List<Solid> SweepParts(double distance)
        {
            var parts = new List<Solid>();
            try
            {
                parts.Add(Sweep(0));
                if (distance < 1e-8) return parts;
                var points = Corners(0, true);
                var faces = new[] { new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 },
                    new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 } };
                var normals = new[] { -Frame.BasisX, Frame.BasisX, -Frame.BasisY, Frame.BasisY, -Frame.BasisZ, Frame.BasisZ };
                for (int f = 0; f < faces.Length; f++)
                {
                    if (normals[f].DotProduct(Direction) <= 1e-8) continue;
                    var loop = new CurveLoop();
                    for (int i = 0; i < 4; i++) loop.Append(Autodesk.Revit.DB.Line.CreateBound(points[faces[f][i]], points[faces[f][(i + 1) % 4]]));
                    parts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, Direction, distance));
                }
                return parts;
            }
            catch { foreach (var part in parts) part.Dispose(); throw; }
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
        internal static MotorExtractionReport Run(Document doc, FamilyInstance pump, MotorEnvelope envelope, ISet<long> extraIgnored = null)
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
            if (extraIgnored != null) ignored.UnionWith(extraIgnored);
            var swept = envelope.SweepParts(envelope.Travel);
            try
            {
            using (var bounds = MotorEnvelope.Bounds(envelope.Corners(0, true).Concat(envelope.Corners(envelope.Travel, true))))
            {
                Scan(doc, Transform.Identity, "", true);
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    var linked = link.GetLinkDocument();
                    if (linked == null) { report.Unknown.Add(UiLanguage.T("Lien non chargé : ", "Link not loaded: ") + link.Name); continue; }
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
                            if (!host) report.Unknown.Add(prefix + element.Name + UiLanguage.T(" : lien imbriqué non analysé", ": nested link not analyzed"));
                            continue;
                        }
                        if (element is View || element is SpatialElement || element.Category == null || element.Category.CategoryType != CategoryType.Model
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
                            if (solids.Count == 0 || incomplete) report.Unknown.Add(label + UiLanguage.T(" : géométrie incomplète", ": incomplete geometry"));
                            bool Intersects(IEnumerable<Solid> volumes)
                            {
                                foreach (var volume in volumes)
                                foreach (var solid in solids)
                                using (var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(volume, solid, BooleanOperationsType.Intersect))
                                    if (intersection.Volume > 1e-10) return true;
                                return false;
                            }
                            if (!Intersects(swept)) continue;
                            double high = MotorSweepMath.FirstContact(envelope.Travel, 1.0 / 304.8,
                                distance =>
                                {
                                    var tests = envelope.SweepParts(distance);
                                    try { return Intersects(tests); }
                                    finally { foreach (var test in tests) test.Dispose(); }
                                }).Value;
                            var box = element.get_BoundingBox(null);
                            XYZ[] boxPoints = box == null ? null : Enumerable.Range(0, 8).Select(i => transform.OfPoint(box.Transform.OfPoint(new XYZ(
                                (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                                (i & 4) == 0 ? box.Min.Z : box.Max.Z)))).ToArray();
                            report.Obstacles.Add(new MotorObstacle { Label = label, First = high, Box = boxPoints });
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException || ex is InvalidOperationException)
                        { report.Unknown.Add(label + UiLanguage.T(" : contrôle géométrique impossible", ": geometric check failed")); }
                        finally { foreach (var solid in solids) solid.Dispose(); }
                    }
                }
            }
            }
            finally { foreach (var part in swept) part.Dispose(); }
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
        internal void DrawPath(BoosterProjection projection, IList<MotorRoutePose> route)
        {
            var line = new Polyline { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 4, 2 } };
            foreach (var pose in route)
            {
                var pixel = projection.Pixel(pose.Center); var dip = BoosterNative.Dip(this, pixel.X, pixel.Y);
                line.Points.Add(new Point(dip.X - Left, dip.Y - Top));
            }
            _canvas.Children.Add(line);
            if (line.Points.Count > 0)
            {
                var label = new TextBlock { Text = UiLanguage.T("Sortie · porte supposée ouverte", "Exit · door assumed open"), Background = Brushes.White, Foreground = Brushes.Black, Padding = new Thickness(5) };
                var end = line.Points.Last(); Canvas.SetLeft(label, end.X + 8); Canvas.SetTop(label, end.Y + 8); _canvas.Children.Add(label);
            }
        }
    }

    internal sealed class MotorExtractionWindow : Window
    {
        private readonly Document _doc;
        private readonly FamilyInstance _pump;
        private readonly BoosterProjection _projection;
        private readonly Transform _frame;
        private readonly MotorGhost _ghost;
        private readonly MotorExtractionState _state;
        internal string PickRequest { get; private set; }
        private readonly Dictionary<string, TextBox> _fields = new Dictionary<string, TextBox>();
        private readonly ComboBox _direction = new ComboBox { ItemsSource = new[] { "+X local", "−X local", "+Y local", "−Y local", "+Z local", "−Z local" }, SelectedIndex = 0 };
        private readonly Slider _position = new Slider { Minimum = 0, Maximum = 100, Value = 0 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        private readonly TextBlock _distance = new TextBlock();
        private readonly TextBlock _details = new TextBlock { TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,6,0,6) };
        private readonly Expander _scope = new Expander { Header=UiLanguage.T("Hypothèses et détails du contrôle", "Check assumptions and details"), IsExpanded=false };
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        private MotorEnvelope _envelope;
        private MotorExtractionReport _report;
        private List<MotorRoutePose> _route;
        private StackPanel _settings;
        private readonly Button _cancel = new Button { Content=UiLanguage.T("Annuler la recherche", "Cancel the search"), Visibility=System.Windows.Visibility.Collapsed };
        private bool _running, _cancelRequested;
        private string _lastError;
        internal MotorExtractionWindow(UIDocument ui, FamilyInstance pump, BoosterProjection projection, IntPtr owner, MotorExtractionState state)
        {
            _doc = ui.Document; _pump = pump; _projection = projection; _state = state;
            _direction.SelectedIndex = state.Direction;
            Title = UiLanguage.T("MEP · Chemin vers la sortie · ", "MEP · Path to Exit · ")+MaintenanceBuildIdentity.Display; Width = 490; Height = 850; MinHeight = 500;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false;
            new WindowInteropHelper(this).Owner = owner; BoosterTheme.Attach(this);
            Background = (Brush)FindResource("Surface");
            _ghost = new MotorGhost(owner);
            var t = pump.GetTransform();
            _frame = Transform.Identity; _frame.Origin = t.Origin;
            _frame.BasisX = t.BasisX.Normalize(); _frame.BasisY = t.BasisY.Normalize();
            _frame.BasisZ = t.BasisZ.Normalize();
            var profile = MotorExtractionProfile.Read(pump);
            var size = profile.Size; var center = profile.Center;
            var panel = new StackPanel { Margin = new Thickness(18) };
            _settings=panel;
            var layout = new DockPanel();
            var resultCard = new Border { Padding = new Thickness(14), Background = Brushes.WhiteSmoke, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(0,0,0,1) };
            var resultPanel = new StackPanel(); resultCard.Child=resultPanel;
            resultPanel.Children.Add(_status); resultPanel.Children.Add(_distance); resultPanel.Children.Add(_position);
            _scope.Content=_details; resultPanel.Children.Add(_scope);
            resultPanel.Children.Add(_cancel); _cancel.Click += (_,__) => _cancelRequested=true;
            DockPanel.SetDock(resultCard,Dock.Top); layout.Children.Add(resultCard);
            layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content=layout;
            panel.Children.Add(new TextBlock { Text = UiLanguage.T("Du moteur à la porte de sortie", "From the motor to the exit door"), FontSize = 19, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = pump.Name + " · " + BoosterIds.Value(pump.Id), Margin = new Thickness(0, 6, 0, 8) });
            panel.Children.Add(new TextBlock { Text = profile.Description + UiLanguage.T(" Toutes les valeurs sont en mm.", " All values are in mm."), TextWrapping = TextWrapping.Wrap });
            Field("X", UiLanguage.T("Dimension X du moteur", "Motor X dimension"), Math.Max(10, size.X * 304.8));
            Field("Y", UiLanguage.T("Dimension Y du moteur", "Motor Y dimension"), Math.Max(10, size.Y * 304.8));
            Field("Z", UiLanguage.T("Hauteur Z du moteur", "Motor Z height"), Math.Max(10, size.Z * 304.8));
            Field("CX", UiLanguage.T("Centre X / origine famille", "Center X / family origin"), center.X * 304.8);
            Field("CY", UiLanguage.T("Centre Y / origine famille", "Center Y / family origin"), center.Y * 304.8);
            Field("CZ", UiLanguage.T("Centre Z / origine famille", "Center Z / family origin"), center.Z * 304.8);
            if (state.MotorPoint != null)
            {
                var current = _frame.OfPoint(new XYZ(ParseSaved("CX", center.X * 304.8), ParseSaved("CY", center.Y * 304.8), ParseSaved("CZ", center.Z * 304.8)) / 304.8);
                var local = _frame.Inverse.OfPoint(new XYZ(state.MotorPoint.X, state.MotorPoint.Y, current.Z));
                _fields["CX"].Text = (local.X * 304.8).ToString("0.#"); _fields["CY"].Text = (local.Y * 304.8).ToString("0.#");
                state.MotorPoint = null;
            }
            var pointMotor = new Button { Content = UiLanguage.T("Pointer le centre du moteur (XY)", "Pick the motor center (XY)"), Margin = new Thickness(0, 8, 0, 4), Style = (Style)FindResource("SecondaryButton") };
            panel.Children.Add(pointMotor); pointMotor.Click += (_, __) => { PickRequest = "motor"; Close(); };
            var pickDoor = new Button { Content = state.DoorPoint == null ? UiLanguage.T("Choisir la porte de sortie…", "Choose the exit door…") : UiLanguage.T("Changer la porte de sortie…", "Change the exit door…"), Style = (Style)FindResource("SecondaryButton") };
            panel.Children.Add(pickDoor); pickDoor.Click += (_, __) => { PickRequest = "door"; Close(); };
            panel.Children.Add(new TextBlock { Text = state.DoorName == null ? UiLanguage.T("Sélectionnez la dernière porte : le chemin sera recherché automatiquement.", "Select the final door: the path will be searched automatically.") : UiLanguage.T("Sortie : ", "Exit: ") + state.DoorName,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
            var openDoors = new CheckBox { Content = UiLanguage.T("Portes intermédiaires supposées ouvertes", "Intermediate doors assumed open"), IsChecked = state.AllowIntermediateDoors,
                Margin = new Thickness(0, 6, 0, 6), ToolTip = UiLanguage.T("Le trajet peut traverser d’autres portes. Le passage est contrôlé selon les dimensions disponibles ; l’ouverture effective des vantaux reste à confirmer sur site.", "The route may pass through other doors. The clearance is checked using the available dimensions; actual leaf opening must be confirmed on site.") };
            panel.Children.Add(openDoors);
            openDoors.Checked += (_, __) => { state.AllowIntermediateDoors = true; Invalidate(); };
            openDoors.Unchecked += (_, __) => { state.AllowIntermediateDoors = false; Invalidate(); };
            Field("Step", UiLanguage.T("Pas de recherche (50 à 500)", "Search step (50 to 500)"), 200);
            Field("Buffer", UiLanguage.T("Recherche autour du trajet", "Search around the route"), 4000);
            var find = new Button { Content = UiLanguage.T("Trouver le chemin jusqu’à la sortie", "Find the path to the exit"), Margin = new Thickness(0, 10, 0, 6), Style = (Style)FindResource("PrimaryButton"), IsEnabled = state.DoorPoint != null };
            panel.Children.Add(find); find.Click += (_, __) => FindRoute();
            panel.Children.Add(new TextBlock { Text = UiLanguage.T("Direction d’extraction", "Extraction direction"), Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(_direction);
            Field("Travel", UiLanguage.T("Distance d’extraction", "Extraction distance"), 600);
            Field("Margin", UiLanguage.T("Marge autour du moteur", "Clearance around the motor"), 10);
            var check = new Button { Content = UiLanguage.T("Tester seulement la dépose rectiligne", "Test straight-line removal only"), Margin = new Thickness(0, 12, 0, 8), Style = (Style)FindResource("SecondaryButton") };
            panel.Children.Add(check);
            var animate = new Button { Content = UiLanguage.T("▶ Lire / arrêter l’extraction", "▶ Play / stop extraction"), Style = (Style)FindResource("SecondaryButton") };
            resultPanel.Children.Insert(0,animate);
            panel.Children.Add(new TextBlock { Text = UiLanguage.T("Le chemin vérifie les translations et les virages du volume, avec 16 orientations horizontales. Hauteur constante, sans basculement ni levage. La pompe est exclue ; la porte de sortie et, selon l’option, les portes intermédiaires sont supposées ouvertes. Leurs passages restent contrôlés avec les données disponibles. Le résultat dépend du gabarit réglé et ne valide pas une procédure de manutention.", "The path checks translations and turns of the volume, with 16 horizontal orientations. Constant height, no tilting or lifting. The pump is excluded; the exit door and, depending on the option, intermediate doors are assumed open. Their openings are still checked with the available data. The result depends on the configured template and does not validate a handling procedure."), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
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
                    _envelope = Read(); _report = null; _route = null;
                    using (new WaitCursor()) _report = MotorExtractionAnalysis.Run(_doc, _pump, _envelope);
                    _position.Value = 0;
                    _status.Text = (_report.First.HasValue
                        ? UiLanguage.T("Obstacle rencontré vers ", "Obstacle hit at about ") + (_report.First.Value * 304.8).ToString("0") + UiLanguage.T(" mm (précision de recherche ≈ 1 mm).\n", " mm (search accuracy ≈ 1 mm).\n")
                        : UiLanguage.T("Aucun obstacle détecté sur ", "No obstacle detected over ") + (_envelope.Travel * 304.8).ToString("0") + UiLanguage.T(" mm.\n", " mm.\n"))
                        + string.Join("\n", _report.Obstacles.Take(8).Select(o => "• " + o.Label + " — " + (o.First * 304.8).ToString("0") + " mm"))
                        + (_report.Unknown.Count == 0 ? "" : UiLanguage.T("\nContrôle incomplet : ", "\nIncomplete check: ") + _report.Unknown.Count + UiLanguage.T(" élément(s) ou lien(s).\n", " element(s) or link(s).\n") + string.Join("\n", _report.Unknown.Take(5)));
                    Draw();
                }
                catch (Exception ex) { _report = null; _status.Text = UiLanguage.T("Analyse impossible : ", "Analysis failed: ") + ex.Message; Draw(); }
            };
            animate.Click += (_, __) =>
            {
                if (_timer.IsEnabled) _timer.Stop();
                else if (_report == null) _status.Text = UiLanguage.T("Lancez une recherche de chemin ou le test de dépose avant la lecture.", "Run a path search or the removal test before playback.");
                else if (_envelope != null) { if (_position.Value >= 100) _position.Value = 0; _timer.Start(); }
            };
            _timer.Tick += (_, __) => { _position.Value = Math.Min(100, _position.Value + 1); if (_position.Value >= 100) _timer.Stop(); };
            Loaded += (_, __) => Invalidate();
            Closing += (_,e) => {if(_running){_cancelRequested=true;e.Cancel=true;}};
            Closed += (_, __) =>
            {
                _timer.Stop(); _ghost.Close();
                foreach (var item in _fields) state.Fields[item.Key] = item.Value.Text;
                state.Direction = _direction.SelectedIndex;
            };
            void Field(string key, string label, double value)
            {
                var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                // Override the shared fixed-height template: it clipped the text
                // at the DPI/font settings used in Revit.
                var input = new TextBox { Text = state.Fields.TryGetValue(key, out string saved) ? saved : value.ToString("0.#", CultureInfo.CurrentCulture),
                    Style = null, Height = double.NaN, MinHeight = 38, FontSize = 15, Padding = new Thickness(8, 5, 8, 5),
                    VerticalContentAlignment = VerticalAlignment.Center, Foreground = Brushes.Black, Background = Brushes.White,
                    BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Margin = new Thickness(0) };
                Grid.SetColumn(input, 1); row.Children.Add(input); panel.Children.Add(row); _fields.Add(key, input);
            }
            double ParseSaved(string key, double fallbackValue)
                => double.TryParse(_fields[key].Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : fallbackValue;
        }
        private MotorEnvelope Read()
        {
            double Number(string key, bool positive = false)
            {
                if (!double.TryParse(_fields[key].Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    || double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > 1000000 || (positive && n < 2))
                    throw new InvalidOperationException(UiLanguage.T("Valeur invalide : ", "Invalid value: ") + key + UiLanguage.T(". Dimensions et course : au moins 2 mm.", ". Dimensions and travel: at least 2 mm."));
                return n / 304.8;
            }
            var margin = Number("Margin"); if (margin < 0) throw new InvalidOperationException(UiLanguage.T("La marge doit être positive ou nulle.", "The margin must be zero or positive."));
            return new MotorEnvelope { Frame = _frame, Center = new XYZ(Number("CX"), Number("CY"), Number("CZ")),
                Size = new XYZ(Number("X", true), Number("Y", true), Number("Z", true)),
                Axis = _direction.SelectedIndex / 2, Sign = _direction.SelectedIndex % 2 == 0 ? 1 : -1,
                Travel = Number("Travel", true), Margin = margin };
        }
        private void Invalidate()
        {
            _timer.Stop(); _report = null; _route = null; _position.Value = 0;
            try { _envelope = Read(); _status.Text = UiLanguage.T("Volume modifié : lancez l’analyse du trajet.", "Volume changed: run the route analysis."); Draw(); }
            catch (Exception ex) { _envelope = null; _ghost.Hide(); _status.Text = ex.Message; }
        }
        private void Draw()
        {
            if (_envelope == null) return;
            if (_route != null && _route.Count > 1)
            {
                double radius = _envelope.Size.GetLength() / 2;
                double Weight(int i) => (_route[i].Center - _route[i - 1].Center).GetLength()
                    + radius * Math.Abs(MotorRouteGeometry.AngleDelta(_route[i - 1].Yaw, _route[i].Yaw));
                double total = Enumerable.Range(1, _route.Count - 1).Sum(Weight);
                double remaining = total * _position.Value / 100;
                int segment = 1;
                while (segment < _route.Count - 1 && remaining > Weight(segment)) { remaining -= Weight(segment); segment++; }
                double fraction = Math.Min(1, remaining / Math.Max(1e-9, Weight(segment)));
                var a = _route[segment - 1]; var b = _route[segment];
                var pose = new MotorRoutePose(a.Center + (b.Center - a.Center) * fraction,
                    a.Yaw + MotorRouteGeometry.AngleDelta(a.Yaw, b.Yaw) * fraction);
                _ghost.Draw(_projection, MotorRouteGeometry.At(_envelope, pose), 0, _report);
                _ghost.DrawPath(_projection, _route);
                _distance.Text = UiLanguage.T("Chemin vers la sortie : ", "Path to exit: ") + _position.Value.ToString("0") + UiLanguage.T(" % · rotation ", " % · rotation ") + (pose.Yaw * 180 / Math.PI).ToString("0") + "°";
                return;
            }
            double distance = _envelope.Travel * _position.Value / 100;
            _distance.Text = UiLanguage.T("Position du moteur : ", "Motor position: ") + (distance * 304.8).ToString("0") + " mm / " + (_envelope.Travel * 304.8).ToString("0") + " mm";
            _ghost.Draw(_projection, _envelope, distance, _report);
        }
        private void FindRoute()
        {
            if (_state.DoorPoint == null) return;
            _timer.Stop(); _route = null; _report = null;
            _running=true; _cancelRequested=false; _lastError=null; _settings.IsEnabled=false; _cancel.Visibility=System.Windows.Visibility.Visible;
            var elapsed=System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _envelope = Read();
                double Input(string key) => double.Parse(_fields[key].Text.Replace(',', '.'), CultureInfo.InvariantCulture);
                double stepMm = Input("Step"), bufferMm = Input("Buffer");
                if (double.IsNaN(stepMm) || double.IsInfinity(stepMm) || stepMm < 50 || stepMm > 500
                    || double.IsNaN(bufferMm) || double.IsInfinity(bufferMm) || bufferMm < 500 || bufferMm > 15000)
                    throw new InvalidOperationException(UiLanguage.T("Pas : 50 à 500 mm. Périmètre autour du trajet : 500 à 15 000 mm.", "Step: 50 to 500 mm. Area around the route: 500 to 15,000 mm."));
                double step = stepMm / 304.8, padding = bufferMm / 304.8;
                var start = _envelope.Frame.OfPoint(_envelope.Center);
                var door = new XYZ(_state.DoorPoint.X, _state.DoorPoint.Y, start.Z);
                var normal = new XYZ(_state.DoorNormal.X, _state.DoorNormal.Y, 0).Normalize();
                if ((door - start).DotProduct(normal) < 0) normal = -normal;
                double standOff = _envelope.Size.GetLength() / 2 + _envelope.Margin + 300 / 304.8;
                var before = door - normal * standOff;
                var after = door + normal * standOff;
                _status.Text = UiLanguage.T("Recherche automatique du passage et des virages… (jusqu’à 40 s de recherche après lecture de la géométrie)", "Automatic search for the passage and turns… (up to 40 s of search after reading the geometry)");
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
                using (new WaitCursor())
                using (var geometry = new MotorRouteGeometry(_doc, _pump, _envelope, after, padding, _state.DoorId, _state.DoorLinkId, before, Progress, _state.AllowIntermediateDoors))
                {
                    var portals = geometry.Portals.ToList();
                    double horizontalRadius = _envelope.Corners(0, true).Max(p => Math.Sqrt((p.X - start.X) * (p.X - start.X) + (p.Y - start.Y) * (p.Y - start.Y)));
                    var gates = portals.Select(p => new MotorRouteGate((p.Origin.X - start.X) / step, (p.Origin.Y - start.Y) / step,
                        p.Normal.X, p.Normal.Y, (horizontalRadius + Math.Max(300 / 304.8, p.Depth / 2 + 50 / 304.8)) / step, p.Target)).ToList();
                    if (!gates.Any(g => g.Target))
                        gates.Add(new MotorRouteGate((door.X - start.X) / step, (door.Y - start.Y) / step, normal.X, normal.Y, standOff / step, true));
                    // Keep the regular orientations and also align with each actual
                    // opening, so a rotated narrow door is not lost to a 22.5° step.
                    var angles = Enumerable.Range(0, 16).Select(i => i * Math.PI / 8).ToList();
                    double initialAngle = Math.Atan2(_envelope.Frame.BasisX.Y, _envelope.Frame.BasisX.X);
                    foreach (var p in portals)
                    for (int quarter = 0; quarter < 4; quarter++)
                    {
                        double yaw = (Math.Atan2(p.WidthAxis.Y, p.WidthAxis.X) - initialAngle + quarter * Math.PI / 2) % (2 * Math.PI);
                        if (yaw < 0) yaw += 2 * Math.PI;
                        if (!angles.Any(a => Math.Abs(MotorRouteGeometry.AngleDelta(a, yaw)) < 1e-7)) angles.Add(yaw);
                    }
                    angles.Sort();
                    var cache = new Dictionary<(MotorRoutePoint, MotorRoutePoint), bool>();
                    MotorRoutePose Pose(MotorRoutePoint point) => new MotorRoutePose(start + new XYZ(point.X * step, point.Y * step, 0), angles[point.Heading]);
                    bool Clear(MotorRoutePoint a, MotorRoutePoint b)
                    {
                        if (cache.TryGetValue((a, b), out bool free)) return free;
                        free = geometry.ClearForSearch(Pose(a), Pose(b)); cache[(a, b)] = free; cache[(b, a)] = free; return free;
                    }
                    var result = MotorRouteSearch.FindWithPortals(gates, (int)Math.Floor(padding / step),
                        Math.Max(0.2, _envelope.Size.GetLength() / 2 * Math.PI / 8 / step), Clear,
                        progress: n=>Progress(UiLanguage.T("Recherche du chemin : ", "Path search: ")+n+UiLanguage.T(" positions/orientations explorées…", " positions/orientations explored…")), headingAngles: angles);
                    _report = new MotorExtractionReport(); _report.Unknown.AddRange(geometry.Unknown.Distinct());
                    if (result.Points != null)
                    {
                        _route = result.Points.Select(Pose).ToList();
                        _route=Compact(_route);
                        for(int i=1;i<_route.Count;i++)
                        {
                            Progress(UiLanguage.T("Vérification des solides Revit : mouvement ", "Checking Revit solids: movement ")+i+" / "+(_route.Count-1)+"…");
                            if(!geometry.Clear(_route[i-1],_route[i]))
                                throw new InvalidOperationException(UiLanguage.T("Le chemin candidat échoue au contrôle précis : ", "The candidate path fails the precise check: ")+geometry.LastObstacle+UiLanguage.T(". Aucun trajet validé. Réduisez le pas de recherche.", ". No route validated. Reduce the search step."));
                        }
                        double length = Enumerable.Range(1, _route.Count - 1).Sum(i => (_route[i].Center - _route[i - 1].Center).GetLength());
                        int turns = Enumerable.Range(1, _route.Count - 1).Count(i => Math.Abs(MotorRouteGeometry.AngleDelta(_route[i - 1].Yaw, _route[i].Yaw)) > 1e-6);
                        _status.Text = UiLanguage.T("Chemin vérifié en ", "Path verified in ")+elapsed.Elapsed.TotalSeconds.ToString("0.0")+UiLanguage.T(" s : ", " s: ") + (length * 0.3048).ToString("0.0") + " m · " + turns + UiLanguage.T(" rotation(s).\n", " turn(s).\n")
                            + UiLanguage.T("Passage testé jusqu’au-delà de la porte. Utilisez le curseur ou la lecture pour voir le moteur avancer et tourner.\n", "Passage tested beyond the door. Use the slider or playback to see the motor move and turn.\n");
                    }
                    else _status.Text = result.Message + "\n" + (result.Visited <= 1 && geometry.LastObstacle != null ? UiLanguage.T("Obstacle : ", "Obstacle: ") + geometry.LastObstacle + "\n" : "");
                    _status.Text += result.Visited + UiLanguage.T(" positions/orientations explorées.", " positions/orientations explored.")
                        + (_report.Unknown.Count==0 ? "" : UiLanguage.T("\nContrôle incomplet : ", "\nIncomplete check: ")+_report.Unknown.Count+UiLanguage.T(" anomalie(s), voir les détails.", " issue(s), see details."));
                    _details.Text=string.Join("\n", geometry.Assumptions)
                        + (_state.DoorWidth <= 0 || _state.DoorHeight <= 0 ? UiLanguage.T("\nDimensions nominales de la porte non disponibles : contrôle limité à la géométrie des murs.", "\nNominal door dimensions unavailable: check limited to wall geometry.") : UiLanguage.T("\nLargeur et hauteur nominales de la porte contrôlées, sans déduction des jeux du dormant.", "\nNominal door width and height checked, without deducting frame clearances."))
                        + (_report.Unknown.Count == 0 ? "" : UiLanguage.T("\nContrôle incomplet : ", "\nIncomplete check: ") + _report.Unknown.Count + UiLanguage.T(" anomalie(s).\n", " issue(s).\n") + string.Join("\n", _report.Unknown.Take(4)));
                    _position.Value = 0;
                    if (_route == null) _report = null;
                    Draw();
                    if(_route!=null) _timer.Start();
                }
            }
            catch(OperationCanceledException) {_route=null;_report=null;_status.Text=UiLanguage.T("Recherche annulée : aucun trajet validé.", "Search cancelled: no route validated.");Draw();}
            catch (Exception ex) { _lastError=ex.ToString(); _route = null; _report = null; _status.Text = UiLanguage.T("Recherche impossible : ", "Search failed: ") + ex.Message; Draw(); }
            finally {_running=false;_settings.IsEnabled=true;_cancel.Visibility=System.Windows.Visibility.Collapsed;}
        }
        private void Progress(string message)
        {
            _status.Text=message; _details.Text="";
            var frame=new DispatcherFrame();
            Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);
            if(_cancelRequested) throw new OperationCanceledException();
        }
        private static List<MotorRoutePose> Compact(IList<MotorRoutePose> route)
        {
            var result=new List<MotorRoutePose>();
            foreach(var pose in route)
            {
                if(result.Count>1)
                {
                    var a=result[result.Count-2];var b=result[result.Count-1];
                    var first=b.Center-a.Center;var next=pose.Center-b.Center;
                    if(first.GetLength()>1e-8&&next.GetLength()>1e-8 && Math.Abs(MotorRouteGeometry.AngleDelta(a.Yaw,b.Yaw))<1e-8
                        &&Math.Abs(MotorRouteGeometry.AngleDelta(b.Yaw,pose.Yaw))<1e-8
                        &&first.Normalize().DotProduct(next.Normalize())>1-1e-8) result.RemoveAt(result.Count-1);
                }
                result.Add(pose);
            }
            return result;
        }
        private sealed class WaitCursor : IDisposable
        {
            private readonly System.Windows.Input.Cursor _previous = System.Windows.Input.Mouse.OverrideCursor;
            internal WaitCursor() { System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait; }
            public void Dispose() { System.Windows.Input.Mouse.OverrideCursor = _previous; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Point = System.Windows.Point;
using Line = System.Windows.Shapes.Line;
using Color = System.Windows.Media.Color;
using Transform = Autodesk.Revit.DB.Transform;

namespace BIMaestro.MepBooster
{
    internal static class BoosterPreferences
    {
        internal static string AnglePath => System.IO.Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "Settings", "mep-booster-angle.txt");
        internal static double LoadAngle(string path)
        {
            try
            {
                if (System.IO.File.Exists(path) && BoosterPalette.ParseAngle(System.IO.File.ReadAllText(path), out double value)) return value;
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
            return 22.5;
        }
        internal static void SaveAngle(string path, double value)
        {
            string text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (!BoosterPalette.ParseAngle(text, out _)) throw new ArgumentOutOfRangeException(nameof(value));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, text);
        }
    }
    internal static class BoosterTheme
    {
        internal static void Attach(System.Windows.Window window) => window.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("/" + typeof(BoosterTheme).Assembly.GetName().Name
                + ";component/Themes/BIMaestroTheme.xaml", UriKind.Relative) });
    }
    internal static class BoosterNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct PixelPoint { public int X, Y; }
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out PixelPoint point);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
        internal static uint InputStamp()
        {
            var input = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            return GetLastInputInfo(ref input) ? input.Time : 0;
        }
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        internal static bool IsRevitForeground(IntPtr owner)
        {
            if (owner == IntPtr.Zero) return false;
            GetWindowThreadProcessId(owner, out uint ownerProcess);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
            return ownerProcess != 0 && ownerProcess == foregroundProcess;
        }
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int hook, MouseHook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string module);
        private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
        // Observes mouse-down/wheel only while ON; never consumes or changes Revit input.
        internal sealed class MouseObserver : IDisposable
        {
            private readonly MouseHook _callback;
            private IntPtr _hook;
            internal MouseObserver(Action<int, int, int> onInput)
            {
                _callback = (code, message, data) =>
                {
                    if (code >= 0)
                    {
                        int kind = message.ToInt32();
                        if (kind == 0x201 || kind == 0x204 || kind == 0x207 || kind == 0x20A || kind == 0x20E)
                        {
                            try
                            {
                                var point = Marshal.PtrToStructure<PixelPoint>(data);
                                onInput(kind, point.X, point.Y);
                            }
                            catch { /* Always forward the input, even during shutdown. */ }
                        }
                    }
                    return CallNextHookEx(_hook, code, message, data);
                };
                _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
            }
            public void Dispose()
            {
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                GC.KeepAlive(_callback);
            }
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        internal static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        internal static void Style(System.Windows.Window window, bool transparent)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x00000080 | (transparent ? 0x20 : 0));
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivate);
        }
        private static IntPtr NoActivate(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x21) return IntPtr.Zero; // WM_MOUSEACTIVATE
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE, still deliver the click to the button.
        }
        internal static Point Dip(System.Windows.Window window, double x, double y)
        {
            return DeviceToDip(window).Transform(new Point(x, y));
        }
        internal static Matrix DeviceToDip(System.Windows.Window window)
        {
            // A hidden window has a native handle but is not necessarily attached to
            // a PresentationSource yet (notably on the first preview in Revit 2025).
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var source = HwndSource.FromHwnd(handle);
            return source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        }
    }

    internal sealed class BoosterProjection
    {
        internal int Left, Top, Right, Bottom;
        internal XYZ Center, Up, RightAxis;
        internal double Width, Height;
        internal static BoosterProjection Read(UIDocument doc)
        {
            var view = doc.ActiveView;
            if (view is View3D three && three.IsPerspective) return null;
            if (!(view is View3D) && !(view is ViewPlan) && !(view is ViewSection)) return null;
            var uiView = doc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
            if (uiView == null) return null;
            var rect = uiView.GetWindowRectangle();
            var corners = uiView.GetZoomCorners();
            XYZ span = corners[1] - corners[0];
            double width = Math.Abs(span.DotProduct(view.RightDirection));
            double height = Math.Abs(span.DotProduct(view.UpDirection));
            if (width < 1e-9 || height < 1e-9 || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return null;
            return new BoosterProjection { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom,
                Center = (corners[0] + corners[1]) / 2, RightAxis = view.RightDirection, Up = view.UpDirection,
                Width = width, Height = height };
        }
        internal bool Same(BoosterProjection other) => other != null && Left == other.Left && Top == other.Top
            && Right == other.Right && Bottom == other.Bottom && Center.IsAlmostEqualTo(other.Center)
            && Up.IsAlmostEqualTo(other.Up) && RightAxis.IsAlmostEqualTo(other.RightAxis)
            && Math.Abs(Width - other.Width) < 1e-8 && Math.Abs(Height - other.Height) < 1e-8;
        internal Point Pixel(XYZ point) => new Point(
            Left + (0.5 + (point - Center).DotProduct(RightAxis) / Width) * (Right - Left),
            Top + (0.5 - (point - Center).DotProduct(Up) / Height) * (Bottom - Top));
        internal bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    }

    internal sealed class BoosterPreview : System.Windows.Window
    {
        private readonly Canvas _canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        internal BoosterPreview(IntPtr owner)
        {
            BoosterTheme.Attach(this);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowActivated = false; ShowInTaskbar = false; Focusable = false;
            Content = _canvas;
            new WindowInteropHelper(this).Owner = owner;
            SourceInitialized += (_, __) => BoosterNative.Style(this, true);
        }

        internal void Draw(BoosterProjection projection, IList<BoosterPart> parts, double angle, bool flip,
            Func<BoosterPart, Transform> plannedRotation = null)
        {
            new WindowInteropHelper(this).EnsureHandle();
            var origin = BoosterNative.Dip(this, projection.Left, projection.Top);
            var end = BoosterNative.Dip(this, projection.Right, projection.Bottom);
            Left = origin.X; Top = origin.Y; Width = end.X - origin.X; Height = end.Y - origin.Y;
            _canvas.Children.Clear();
            var deviceToDip = BoosterNative.DeviceToDip(this);
            Brush contourBrush = (Brush)FindResource("Focus"), axisBrush = (Brush)FindResource("Warm.Border");
            Point Map(XYZ p)
            {
                var pixel = projection.Pixel(p);
                var dip = deviceToDip.Transform(pixel);
                return new Point(dip.X - Left, dip.Y - Top);
            }
            foreach (var part in parts)
            {
                var rotation = plannedRotation?.Invoke(part) ?? part.Rotation(angle, flip);
                var contours = new StreamGeometry();
                using (var drawing = contours.Open())
                {
                    foreach (var edge in part.Edges)
                    {
                        if (edge.Length < 2) continue;
                        drawing.BeginFigure(Map(rotation.OfPoint(edge[0])), false, false);
                        for (int i = 1; i < edge.Length; i++) drawing.LineTo(Map(rotation.OfPoint(edge[i])), true, false);
                    }
                }
                contours.Freeze();
                _canvas.Children.Add(new System.Windows.Shapes.Path { Data = contours, Stroke = contourBrush, StrokeThickness = 1.5 });
                XYZ axis = flip ? part.FlipAxis : part.Axis;
                XYZ pivot = flip ? part.FlipCenter : part.Center;
                double radius = Math.Max(part.Length * 0.65, projection.Width * 0.018);
                var a = Map(pivot - axis * radius);
                var b = Map(pivot + axis * radius);
                AddLine(a, b, axisBrush, 2);
                var label = new TextBlock { Text = (plannedRotation != null ? "Orientation" : flip ? "Inversion" : angle.ToString("+0;-0;0") + "°") + "  ·  aperçu",
                    Foreground = (Brush)FindResource("Text.Primary"), Background = (Brush)FindResource("Surface"),
                    Padding = new Thickness(5, 2, 5, 2), FontSize = 12 };
                var center = Map(pivot);
                Canvas.SetLeft(label, center.X + 14); Canvas.SetTop(label, center.Y + 12);
                _canvas.Children.Add(label);
                if (plannedRotation != null) continue;
                XYZ radial = flip ? part.Axis : part.FlipAxis;
                radial = (radial - axis * radial.DotProduct(axis)).Normalize();
                var arrowOutline = (Brush)FindResource("Text.Primary");
                var arrowBrush = (Brush)FindResource("Surface");
                var arc = new Polyline { Stroke = arrowBrush, StrokeThickness = 3 };
                for (int i = 0; i <= 24; i++)
                {
                    var turn = Transform.CreateRotation(axis, angle * Math.PI / 180 * i / 24);
                    arc.Points.Add(Map(pivot + turn.OfVector(radial) * radius));
                }
                _canvas.Children.Add(new Polyline { Points = arc.Points.Clone(), Stroke = arrowOutline, StrokeThickness = 5 });
                _canvas.Children.Add(arc);
                var tip = arc.Points[24];
                var tangent = tip - arc.Points[22];
                if (tangent.Length > 0.5)
                {
                    tangent.Normalize();
                    var normal = new Vector(-tangent.Y, tangent.X);
                    AddLine(tip, tip - tangent * 10 + normal * 5, arrowOutline, 5);
                    AddLine(tip, tip - tangent * 10 - normal * 5, arrowOutline, 5);
                    AddLine(tip, tip - tangent * 10 + normal * 5, arrowBrush, 3);
                    AddLine(tip, tip - tangent * 10 - normal * 5, arrowBrush, 3);
                }
            }
            Show();
        }
        private void AddLine(Point a, Point b, Brush brush, double thickness) => _canvas.Children.Add(
            new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = thickness });
    }

    internal sealed class BoosterPalette : System.Windows.Window
    {
        internal event Action<double?, bool> Preview;
        internal event Action<double, bool> Apply;
        internal event Action Dismissed;
        internal event Action CopyOrientation;
        private readonly Canvas _canvas = new Canvas { Width = 292, Height = 388 };
        private readonly System.Windows.Controls.Button _copy;
        internal bool EditingAngle { get; private set; }
        private double _customAngle = BoosterPreferences.LoadAngle(BoosterPreferences.AnglePath);
        private readonly System.Windows.Controls.Button _custom;
        private readonly Border _card;
        private readonly TextBlock _status;
        private readonly List<System.Windows.Controls.Button> _actions = new List<System.Windows.Controls.Button>();
        private bool _expanded;
        private bool _canFlip;
        private Func<double, bool, bool> _supports;
        private int _count;

        internal BoosterPalette(IntPtr owner)
        {
            BoosterTheme.Attach(this);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowActivated = false;
            ShowInTaskbar = false; Focusable = false; SizeToContent = SizeToContent.WidthAndHeight;
            Topmost = true; WindowStartupLocation = WindowStartupLocation.Manual;
            new WindowInteropHelper(this).Owner = owner;
            SourceInitialized += (_, __) => BoosterNative.Style(this, false);
            _card = new Border { CornerRadius = new CornerRadius(16), Background = (Brush)FindResource("Surface"),
                BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1), Padding = new Thickness(8) };
            Content = _card;
            var orbit = new System.Windows.Shapes.Ellipse { Width = 226, Height = 224, Stroke = (Brush)FindResource("Focus"),
                StrokeThickness = 1, Opacity = 0.45, IsHitTestVisible = false };
            Canvas.SetLeft(orbit, 33); Canvas.SetTop(orbit, 48); _canvas.Children.Add(orbit);
            _status = new TextBlock { Width = 94, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(5),
                Foreground = (Brush)FindResource("Text.Primary"), FontSize = 12, IsHitTestVisible = false };
            var center = new Border { Width = 100, MinHeight = 92, CornerRadius = new CornerRadius(22),
                Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1), Child = _status, IsHitTestVisible = false };
            _status.VerticalAlignment = VerticalAlignment.Center;
            Canvas.SetLeft(center, 96); Canvas.SetTop(center, 113); _canvas.Children.Add(center);
            int row = 0;
            foreach (int angle in new[] { 30, 45, 60, 90, 180 })
            {
                double inset = new[] { 42.0, 14, 0, 14, 42 }[row];
                AddAction("↶ −" + angle + "°", -angle, false, inset, 56 + row * 42);
                AddAction("+" + angle + "° ↷", angle, false, 214 - inset, 56 + row * 42);
                row++;
            }
            AddAction("Inverser", 180, true, 107, 20);
            var close = MakeButton("×", 28);
            Canvas.SetLeft(close, 259); Canvas.SetTop(close, 0);
            close.Click += (_, __) => Dismissed?.Invoke(); _canvas.Children.Add(close);
            var brand = new TextBlock { Text = "BIMaestro", Background = (Brush)FindResource("Surface"), Padding = new Thickness(3, 1, 3, 1), Foreground = (Brush)FindResource("Brand"), FontSize = 11, FontWeight = FontWeights.SemiBold };
            Canvas.SetLeft(brand, 8); Canvas.SetTop(brand, 7); _canvas.Children.Add(brand);
            var hint = new TextBlock { Text = "Survol : aperçu   ·   Clic : appliquer", Background = (Brush)FindResource("Surface"), Padding = new Thickness(3), Foreground = (Brush)FindResource("Text.Secondary"), FontSize = 11 };
            Canvas.SetLeft(hint, 53); Canvas.SetTop(hint, 360); _canvas.Children.Add(hint);
            _custom = MakeButton("Perso : +22,5°", 158);
            _custom.MouseEnter += (_, __) => { SetCenter(_customAngle.ToString("+0.##;-0.##") + "°", _count + " pièce(s)\nAperçu"); Preview?.Invoke(_customAngle, false); };
            _custom.MouseLeave += (_, __) => { ResetCenter(); Preview?.Invoke(null, false); };
            _custom.Click += (_, __) => Apply?.Invoke(_customAngle, false);
            Canvas.SetLeft(_custom, 46); Canvas.SetTop(_custom, 276); _canvas.Children.Add(_custom);
            var configure = MakeButton("…", 38);
            configure.ToolTip = "Définir un angle personnalisé positif ou négatif";
            configure.Click += (_, __) => EditAngle();
            Canvas.SetLeft(configure, 210); Canvas.SetTop(configure, 276); _canvas.Children.Add(configure);
            _copy = MakeButton("Copier l’orientation…", 202);
            _copy.ToolTip = "Reproduire cet accessoire sur les cibles de la même famille que vous allez sélectionner.";
            _copy.Click += (_, __) => CopyOrientation?.Invoke();
            Canvas.SetLeft(_copy, 45); Canvas.SetTop(_copy, 318); _canvas.Children.Add(_copy);
            UpdateCustom();
            MouseLeave += (_, __) => Preview?.Invoke(null, false);
        }
        internal void SetCopyAvailability(bool available, int selectionCount = 1)
        {
            available = available && selectionCount == 1;
            _copy.IsEnabled = available;
            _copy.Visibility = available ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            ToolTipService.SetShowOnDisabled(_copy, true);
            _copy.ToolTip = available ? "Cet accessoire est la référence. Sélectionnez ensuite les cibles : orientation et sens seront reproduits."
                : "Copie réservée aux accessoires droits de la même famille, non hébergés et non en miroir.";
        }
        internal static bool ParseAngle(string text, out double angle) =>
            double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out angle)
            && !double.IsNaN(angle) && !double.IsInfinity(angle) && Math.Abs(angle) > 0 && Math.Abs(angle) <= 180;
        private void UpdateCustom()
        {
            _custom.Content = "Perso : " + _customAngle.ToString("+0.##;-0.##") + "°";
            _custom.IsEnabled = _supports?.Invoke(_customAngle, false) ?? true;
            _custom.ToolTip = _custom.IsEnabled ? "Survol : aperçu · clic : appliquer. Le bouton … modifie l’angle."
                : "Cet angle déplacerait une extrémité raccordée. Le bouton … permet de changer l’angle.";
            ToolTipService.SetShowOnDisabled(_custom, true);
        }
        private void EditAngle()
        {
            EditingAngle = true;
            Preview?.Invoke(null, false);
            try
            {
                var dialog = new System.Windows.Window { Owner = this, Title = "Angle personnalisé", Width = 340, Height = 235,
                    ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ShowInTaskbar = false, Background = (Brush)FindResource("Surface") };
                BoosterTheme.Attach(dialog);
                var panel = new StackPanel { Margin = new Thickness(18) };
                panel.Children.Add(new TextBlock { Text = "Angle en degrés (−180 à +180, sauf 0)", Margin = new Thickness(0, 0, 0, 10) });
                var input = new System.Windows.Controls.TextBox { Text = _customAngle.ToString(), Height = 30, FontSize = 15 };
                panel.Children.Add(input);
                var error = new TextBlock { Text = "Exemple : 22,5 ou −22,5", Margin = new Thickness(0, 8, 0, 10) };
                panel.Children.Add(error);
                var save = new System.Windows.Controls.Button { Content = "Enregistrer l’angle", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
                save.Click += (_, __) =>
                {
                    if (!ParseAngle(input.Text.Replace('−', '-'), out double value)) { error.Text = "Saisissez un nombre non nul entre −180 et 180."; return; }
                    try { BoosterPreferences.SaveAngle(BoosterPreferences.AnglePath, value); }
                    catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
                    { error.Text = "Enregistrement impossible : vérifiez vos droits d’accès."; return; }
                    _customAngle = value; dialog.DialogResult = true;
                };
                panel.Children.Add(save); dialog.Content = panel;
                dialog.Loaded += (_, __) => { input.Focus(); input.SelectAll(); };
                dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; dialog.Close(); } };
                dialog.ShowDialog();
                UpdateCustom();
            }
            finally { EditingAngle = false; }
        }
        private System.Windows.Controls.Button MakeButton(string text, double width)
        {
            var style = new Style(typeof(System.Windows.Controls.Button), (Style)FindResource("SecondaryButton"));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, FindResource("Surface")));
            style.Triggers.Add(hover);
            var button = new System.Windows.Controls.Button { Content = text, Width = width, MinWidth = 0,
                Height = 35, Padding = new Thickness(2), Focusable = false, FontSize = 12,
                FontWeight = FontWeights.SemiBold, Style = style };
            button.Resources["Hover.Fill"] = FindResource("Brand");
            return button;
        }
        private void SetCenter(string title, string detail)
        {
            _status.Inlines.Clear();
            _status.Inlines.Add(new System.Windows.Documents.Run(title) { FontSize = 21, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Brand") });
            _status.Inlines.Add(new System.Windows.Documents.LineBreak());
            _status.Inlines.Add(new System.Windows.Documents.Run(detail) { FontSize = 11 });
        }
        private void ResetCenter() => SetCenter(_count.ToString(), (_count == 1 ? "pièce sélectionnée" : "pièces sélectionnées")
            + "\n" + (_count > 20 ? "Aperçu : axes et flèches" : "Survolez un angle"));
        internal void RefreshAfterRotation(int count, bool canFlip, Func<double, bool, bool> supports)
        {
            _canFlip = canFlip; _supports = supports;
            _expanded = false;
            Expand(null, count);
        }
        private void AddAction(string text, double angle, bool flip, double x, double y)
        {
            var button = MakeButton(text, 78);
            button.Tag = flip;
            button.CommandParameter = angle;
            button.ToolTip = flip ? "Retourner la pièce en conservant les points raccordés. L’aperçu montre l’axe d’inversion propre au droit, au coude ou au té."
                : "Rotation autour de l’axe jaune. La flèche et l’aperçu indiquent le sens réel dans la vue.";
            Canvas.SetLeft(button, x); Canvas.SetTop(button, y);
            button.MouseEnter += (_, __) => { SetCenter(flip ? "Inverser" : angle.ToString("+0;-0;0") + "°", _count + " pièce(s)\nAperçu"); Preview?.Invoke(angle, flip); };
            button.MouseLeave += (_, __) => { ResetCenter(); Preview?.Invoke(null, false); };
            button.Click += (_, __) => Apply?.Invoke(angle, flip);
            _actions.Add(button); _canvas.Children.Add(button);
        }
        internal System.Windows.Controls.Button CreatePill(int count, string unavailable = null)
        {
            var pill = MakeButton(string.Empty, 164);
            pill.Height = 44;
            pill.Style = (Style)FindResource("PrimaryButton");
            var content = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
            var icon = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M 19,9 A 8,8 0 1 0 19,15 M 19,3 L 19,9 L 13,9"),
                Stroke = (Brush)FindResource("Focus"), StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            };
            content.Children.Add(icon);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = "MEP Booster", FontSize = 12, FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = unavailable == null ? "Survol : ouvrir" : "Voir les détails",
                FontSize = 10, FontWeight = FontWeights.Normal, Opacity = 0.8 });
            content.Children.Add(label);
            content.Children.Add(new Border
            {
                Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(10),
                MinWidth = 24, Height = 24, Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = unavailable == null ? (count > 99 ? "99+" : count.ToString()) : "i",
                    Foreground = (Brush)FindResource("Brand"), FontSize = 11, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            pill.Content = content;
            pill.ToolTip = unavailable ?? count + (count == 1 ? " pièce sélectionnée" : " pièces sélectionnées")
                + " · Survolez ou cliquez pour ouvrir les rotations. Échap pour masquer.";
            System.Windows.Automation.AutomationProperties.SetName(pill,
                unavailable == null ? "MEP Booster, " + count + " pièces sélectionnées, ouvrir les rotations" : "MEP Booster : " + unavailable);
            ToolTipService.SetInitialShowDelay(pill, 350);
            return pill;
        }
        internal void ShowPill(BoosterProjection projection, int count, bool canFlip, string unavailable = null, Func<double, bool, bool> supports = null)
        {
            _expanded = false; _canFlip = canFlip; _supports = supports;
            var pill = CreatePill(count, unavailable);
            if (unavailable == null)
            {
                pill.MouseEnter += (_, __) => Expand(projection, count);
                pill.Click += (_, __) => Expand(projection, count);
            }
            _card.Padding = new Thickness(2);
            _card.Background = (Brush)FindResource("Surface");
            _card.BorderBrush = (Brush)FindResource("Border");
            _card.CornerRadius = new CornerRadius(15);
            _card.Child = pill;
            new WindowInteropHelper(this).EnsureHandle();
            BoosterNative.GetCursorPos(out var cursor);
            var location = BoosterNative.Dip(this, cursor.X + 24, cursor.Y + 18);
            var minimum = BoosterNative.Dip(this, projection.Left, projection.Top);
            var maximum = BoosterNative.Dip(this, projection.Right, projection.Bottom);
            // Reserve the expanded footprint so the menu opens without moving its target.
            Left = Math.Max(minimum.X, Math.Min(location.X, maximum.X - 310));
            Top = Math.Max(minimum.Y, Math.Min(location.Y, maximum.Y - 406));
            Show();
        }
        private void Expand(BoosterProjection projection, int count)
        {
            if (_expanded) return;
            _expanded = true;
            _count = count;
            UpdateCustom();
            _card.Padding = new Thickness(8);
            _card.CornerRadius = new CornerRadius(16);
            // Almost transparent, but not alpha zero: keep one continuous mouse
            // surface between buttons so Revit does not receive clicks through it.
            var glass = ((SolidColorBrush)FindResource("Surface")).Clone();
            glass.Opacity = 0.08; glass.Freeze();
            _card.Background = glass;
            _card.BorderBrush = Brushes.Transparent;
            ResetCenter();
            foreach (var button in _actions)
            {
                button.IsEnabled = (!(bool)button.Tag || _canFlip) && (_supports?.Invoke((double)button.CommandParameter, (bool)button.Tag) ?? true);
                button.Opacity = button.IsEnabled ? 1 : 0.6;
                ToolTipService.SetShowOnDisabled(button, true);
                if (!button.IsEnabled) button.ToolTip = "Cette action déplacerait une extrémité raccordée ou concerne une pièce hébergée. Libérez la branche concernée avant de la tourner.";
            }
            _card.Child = _canvas;
        }
        internal bool ContainsCursor()
        {
            if (!IsVisible) return false;
            BoosterNative.GetCursorPos(out var cursor);
            return ContainsPixel(cursor.X, cursor.Y);
        }
        internal bool ContainsPixel(int x, int y)
        {
            if (!IsVisible) return false;
            var p = PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        }
    }
}

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace BIMaestro.ViewHover
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ToggleTabCompanionCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "TabCompanionToggle";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            TabCompanionService.Toggle();
            return Result.Succeeded;
        }
    }

    internal static class TabCompanionService
    {
        private static bool _enabled;
        private static WindowCompanion _windowCompanion;
        private static readonly Dictionary<Panel, Companion> Companions = new Dictionary<Panel, Companion>();

        internal static void Toggle()
        {
            StopWindowTest();
            _enabled = !_enabled;
            if (!_enabled) Clear();
            UpdateButton();
        }

        internal static void UpdateButton() => AppUI.UpdatePushButtonPresentation(
            "TabCompanionToggle", _enabled ? "Compagnon : ON" : "Compagnon : OFF",
            "Un Pikachu pixel suit la souris sur la barre des onglets. Cliquez pour activer ou désactiver.");

        internal static void ToggleWindowTest(IntPtr owner)
        {
            if (_windowCompanion != null) StopWindowTest();
            else
            {
                Clear();
                _windowCompanion = new WindowCompanion(owner);
            }
            UpdateWindowButton();
        }

        private static void StopWindowTest()
        {
            _windowCompanion?.Dispose();
            _windowCompanion = null;
            UpdateWindowButton();
        }

        internal static void UpdateWindowButton() => AppUI.UpdatePushButtonPresentation(
            "WindowCompanionToggle", _windowCompanion != null ? "Compagnon fenêtre : ON" : "Compagnon fenêtre : OFF",
            "Test : Pikachu suit la souris dans la fenêtre principale Revit, y compris au-dessus de la maquette. Désactivez pour retrouver le mode onglets.");

        internal static void Refresh(IEnumerable<TabItem> tabs)
        {
            if (!_enabled || _windowCompanion != null) return;
            // Use only the immediate native header panel, never the document canvas.
            var panels = new HashSet<Panel>(tabs.Select(tab => VisualTreeHelper.GetParent(tab) as Panel)
                .Where(panel => panel != null && panel.IsVisible));
            foreach (Panel old in Companions.Keys.Where(panel => !panels.Contains(panel)).ToList())
            {
                Companions[old].Dispose();
                Companions.Remove(old);
            }
            foreach (Panel panel in panels)
            {
                if (Companions.ContainsKey(panel)) continue;
                AdornerLayer layer = AdornerLayer.GetAdornerLayer(panel);
                if (layer != null) Companions.Add(panel, new Companion(panel, layer));
            }
        }

        internal static void Clear()
        {
            StopWindowTest();
            foreach (Companion companion in Companions.Values) companion.Dispose();
            Companions.Clear();
        }

        // A tiny owned, click-through native window also covers Revit's HWND view canvas.
        // A WPF adorner alone cannot reliably render over that native surface.
        private sealed class WindowCompanion : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct NativePoint { internal int X, Y; }
            [StructLayout(LayoutKind.Sequential)]
            private struct NativeRect { internal int Left, Top, Right, Bottom; }
            [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
            [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
            [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
            [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
            [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
            [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

            private readonly IntPtr _owner;
            private readonly Window _window;
            private readonly System.Windows.Controls.Image _sprite;
            private readonly DispatcherTimer _timer;
            private IntPtr _handle;
            private bool _positioned;
            private bool _right = true;
            private double _x, _y, _phase;
            private DateTime _lastTick = DateTime.UtcNow;

            internal WindowCompanion(IntPtr owner)
            {
                _owner = owner;
                _sprite = new System.Windows.Controls.Image
                {
                    Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                    Width = 28, Height = 28, Stretch = Stretch.Uniform,
                    RenderTransformOrigin = new Point(0.5, 0.5), IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(_sprite, BitmapScalingMode.NearestNeighbor);
                _window = new Window
                {
                    Width = 36, Height = 36, WindowStyle = WindowStyle.None,
                    AllowsTransparency = true, Background = Brushes.Transparent,
                    ShowInTaskbar = false, ShowActivated = false, Focusable = false,
                    IsHitTestVisible = false, ResizeMode = ResizeMode.NoResize,
                    Content = _sprite
                };
                var helper = new WindowInteropHelper(_window) { Owner = owner };
                _handle = helper.EnsureHandle();
                // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW.
                SetWindowLong(_handle, -20, GetWindowLong(_handle, -20) | 0x20 | 0x08000000 | 0x80);
                _timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(33) };
                _timer.Tick += Tick;
                _timer.Start();
            }

            private void Tick(object sender, EventArgs args)
            {
                DateTime now = DateTime.UtcNow;
                double dt = Math.Min(0.1, (now - _lastTick).TotalSeconds);
                _lastTick = now;
                var origin = new NativePoint();
                if (GetForegroundWindow() != _owner || IsIconic(_owner) ||
                    !GetCursorPos(out NativePoint cursor) || !GetClientRect(_owner, out NativeRect rect) ||
                    !ClientToScreen(_owner, ref origin) ||
                    cursor.X < origin.X || cursor.X >= origin.X + rect.Right ||
                    cursor.Y < origin.Y || cursor.Y >= origin.Y + rect.Bottom)
                {
                    if (_window.IsVisible) _window.Hide();
                    _positioned = false;
                    return;
                }
                var source = HwndSource.FromHwnd(_handle);
                var scale = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                int width = (int)Math.Ceiling(36 * scale.M11);
                int height = (int)Math.Ceiling(36 * scale.M22);
                double gap = 24 * scale.M11;
                if (!_positioned)
                {
                    _x = cursor.X - gap - width / 2;
                    _y = cursor.Y + 12 * scale.M22;
                    _positioned = true;
                }
                if (Math.Abs(cursor.X - (_x + width / 2)) > gap + 10 * scale.M11)
                    _right = cursor.X > _x + width / 2;
                double targetX = cursor.X + (_right ? -gap - width / 2 : gap - width / 2);
                double targetY = cursor.Y + 12 * scale.M22;
                bool moving = Math.Abs(targetX - _x) + Math.Abs(targetY - _y) > 3;
                double follow = 1 - Math.Exp(-9 * dt);
                _x += (targetX - _x) * follow;
                _y += (targetY - _y) * follow;
                _phase += dt * 18;
                double hop = moving ? Math.Abs(Math.Sin(_phase)) * 2 * scale.M22 : 0;
                _x = Math.Max(origin.X, Math.Min(origin.X + rect.Right - width, _x));
                _y = Math.Max(origin.Y, Math.Min(origin.Y + rect.Bottom - height, _y));
                _sprite.RenderTransform = new ScaleTransform(_right ? 1 : -1, 1);
                if (!_window.IsVisible) _window.Show();
                SetWindowPos(_handle, IntPtr.Zero, (int)_x, (int)(_y - hop), 0, 0,
                    0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE; physical screen coordinates.
            }

            public void Dispose()
            {
                _timer.Stop();
                _timer.Tick -= Tick;
                _window.Close();
            }
        }

        private sealed class Companion : Adorner, IDisposable
        {
            private readonly Panel _panel;
            private readonly AdornerLayer _layer;
            private readonly DispatcherTimer _timer;
            private readonly DrawingImage _image = Couleur.RibbonPanelColorScheme.CreateCompanionImage();
            private bool _shown;
            private bool _right = true;
            private double _x;
            private double _phase;
            private double _hop;
            private DateTime _lastTick;

            internal Companion(Panel panel, AdornerLayer layer) : base(panel)
            {
                _panel = panel;
                _layer = layer;
                IsHitTestVisible = false;
                Focusable = false;
                RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
                _timer = new DispatcherTimer(DispatcherPriority.Background, panel.Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(33) };
                _timer.Tick += Tick;
                panel.MouseEnter += Enter;
                panel.MouseLeave += Leave;
                panel.Unloaded += OnPanelUnloaded;
                layer.Add(this);
                if (panel.IsMouseOver) Enter(panel, null);
            }

            private void Enter(object sender, MouseEventArgs args)
            {
                _x = Mouse.GetPosition(_panel).X;
                _shown = true;
                _lastTick = DateTime.UtcNow;
                _timer.Start();
                InvalidateVisual();
            }

            private void Leave(object sender, MouseEventArgs args)
            {
                _timer.Stop();
                _shown = false;
                InvalidateVisual();
            }

            private void OnPanelUnloaded(object sender, RoutedEventArgs args) => Leave(sender, null);

            private void Tick(object sender, EventArgs args)
            {
                if (!_panel.IsVisible || !_panel.IsMouseOver) { Leave(sender, null); return; }
                double dt = Math.Min(0.1, (DateTime.UtcNow - _lastTick).TotalSeconds);
                _lastTick = DateTime.UtcNow;
                double size = Math.Min(22, _panel.ActualHeight - 2);
                if (size <= 0 || _panel.ActualWidth < size) return;
                double mouseX = Mouse.GetPosition(_panel).X;
                // Leave a small gap beside the pointer and clamp to the header strip.
                double target = Math.Max(size / 2, Math.Min(_panel.ActualWidth - size / 2,
                    mouseX + (_right ? -18 : 18)));
                double distance = target - _x;
                if (Math.Abs(distance) > 2)
                {
                    // Face the pointer rather than oscillating around the trailing gap.
                    if (Math.Abs(mouseX - _x) > 24) _right = mouseX > _x;
                    _x += distance * (1 - Math.Exp(-9 * dt));
                    _phase += dt * 18;
                    _hop = Math.Abs(Math.Sin(_phase)) * 1.5;
                }
                else _hop = 0;
                _x = Math.Max(size / 2, Math.Min(_panel.ActualWidth - size / 2, _x));
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawing)
            {
                if (!_shown) return;
                double size = Math.Min(22, _panel.ActualHeight - 2);
                if (size <= 0) return;
                drawing.PushClip(new RectangleGeometry(new Rect(_panel.RenderSize)));
                drawing.PushTransform(new TranslateTransform(_x, _panel.ActualHeight - size - 1 - _hop));
                drawing.PushTransform(new ScaleTransform(_right ? 1 : -1, 1));
                drawing.DrawImage(_image, new Rect(-size / 2, 0, size, size));
                drawing.Pop();
                drawing.Pop();
                drawing.Pop();
            }

            public void Dispose()
            {
                _timer.Stop();
                _timer.Tick -= Tick;
                _panel.MouseEnter -= Enter;
                _panel.MouseLeave -= Leave;
                _panel.Unloaded -= OnPanelUnloaded;
                _layer.Remove(this);
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ToggleWindowCompanionCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "WindowCompanionToggle";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            TabCompanionService.ToggleWindowTest(data.Application.MainWindowHandle);
            return Result.Succeeded;
        }
    }
}

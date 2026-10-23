using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace BIMaestro.PropertiesAppearance
{
    // The native grid owns all values/editors. This layer only changes its drawing,
    // using real row identities and native cell rectangles, never a fixed stripe height.
    internal sealed class PropertiesPaletteRenderer : IDisposable
    {
        internal sealed class CellPlan { internal uint Row; internal string Label; internal bool Header, Dark; internal PaletteColors Colors; }
        private const uint HookId = 0xB1A975;
        private readonly SubclassProc _callback;
        private NativePropertiesGrid _grid;
        private List<CellPlan> _cells = new List<CellPlan>();
        private string _key;
        private bool _disposed;
        private bool _refreshing;
        private IntPtr _dc, _bitmap, _old, _bits;
        private byte[] _pixels;
        private int _width, _height;
        internal IntPtr Window { get; }
        internal string Error { get; private set; }
        internal Action BeforePaint { get; set; }
#if BIMAESTRO_PROPERTIES_TESTS
        internal long LastColoredPaint { get; private set; }
        internal bool HasCurrentColorPlan => !_disposed && Error==null && _cells.Count>0 && PlanIsCurrent();
#endif
        private delegate IntPtr SubclassProc(IntPtr h, uint m, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int X, Y; public uint Used, Important; }
        [StructLayout(LayoutKind.Sequential)] private struct Info { public Header Header; public uint Color; }
        [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id);
        [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out NativePropertiesGrid.Rect r);
        [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr h, out NativePropertiesGrid.Rect r, bool erase);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr region, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref Info i, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);

        internal PropertiesPaletteRenderer(NativePropertiesGrid grid)
        {
            _grid = grid; Window = grid.Window; _callback = Paint;
            if (!SetWindowSubclass(Window, _callback, new UIntPtr(HookId), UIntPtr.Zero)) throw new InvalidOperationException("Habillage de palette indisponible.");
        }
        internal void Update(NativePropertiesGrid grid, List<CellPlan> cells, bool redraw = true)
        {
            _grid = grid;
            string key = string.Join("\n", cells.Select(c => c.Row + "|" + c.Label + "|" + c.Dark + "|" + c.Colors.Text + "|" + c.Colors.Background));
            _cells = cells;
            if (_key == key) return;
            _key = key; if (redraw) RedrawWindow(Window, IntPtr.Zero, IntPtr.Zero, 0x5);
        }
        internal void RefreshBeforePaint()
        {
            if (_disposed || _refreshing || BeforePaint == null) return;
            _refreshing = true;
            try { BeforePaint(); }
            finally { _refreshing = false; }
        }
        private bool PlanIsCurrent()
        {
            // Native rows can disappear between Idling and WM_PAINT. Validate the
            // whole range before calling any native cell accessor with a saved index.
            return _grid.RefreshIfCurrent() &&
                _cells.All(c => c.Row >= 1 && c.Row <= _grid.RowCount) &&
                _cells.All(c => _grid.Value(c.Row, 1) == c.Label && _grid.IsGroupHeader(c.Row) == c.Header);
        }
        private IntPtr Paint(IntPtr h, uint message, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data)
        {
            // Native value edits rebuild the palette independently of Idling.
            // Reconcile cached native layout metadata before Revit paints that
            // rebuild, without touching the document API from a window callback.
            if (message == 0xF) RefreshBeforePaint();
            NativePropertiesGrid.Rect dirty = new NativePropertiesGrid.Rect();
            bool paint = message == 0xF && GetUpdateRect(h, out dirty, false);
            IntPtr result = DefSubclassProc(h, message, w, l);
            if (message == 0x82) { Dispose(); return result; }
            if (!paint || _disposed || _cells.Count == 0 || Error != null) return result;
            long measured = PropertiesAppearanceMetrics.Begin();
            try
            {
                // A rebuild must never reuse a rule for another row that happens to
                // occupy the same index. Idling will rebuild the semantic plan.
                if (!PlanIsCurrent()) return result;
                NativePropertiesGrid.Rect bounds; GetClientRect(h, out bounds);
                int width = bounds.Right, height = bounds.Bottom;
                if (width < 10 || height < 10 || (long)width * height > 16000000) return result;
                IntPtr dc = GetDC(h); if (dc == IntPtr.Zero) return result;
                try
                {
                    EnsureBitmap(dc, width, height);
                    BitBlt(_dc, 0, 0, width, height, dc, 0, 0, 0x00CC0020); GdiFlush();
                    Marshal.Copy(_bits, _pixels, 0, _pixels.Length);
                    bool editing = _grid.IsEditing;
                    foreach (var cell in _cells)
                    {
                        if (!cell.Header && !_grid.CanStyle(cell.Row)) continue;
                        for (uint col = 1; col <= 3; col++)
                        {
                            if (cell.Header && col == 2) continue;
                            if (editing && col == 2) continue;
                            Transform(_grid.Bounds(cell.Row, col), dirty, _grid.TextColor(cell.Row, col), cell);
                        }
                    }
                    Marshal.Copy(_pixels, 0, _bits, _pixels.Length);
                    int left = Math.Max(0, dirty.Left), top = Math.Max(0, dirty.Top);
                    int right = Math.Min(width, dirty.Right), bottom = Math.Min(height, dirty.Bottom);
                    if (right > left && bottom > top && BitBlt(dc, left, top, right - left, bottom - top, _dc, left, top, 0x00CC0020))
                    {
#if BIMAESTRO_PROPERTIES_TESTS
                        LastColoredPaint=System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                    }
                }
                finally { ReleaseDC(h, dc); }
            }
            catch (Exception ex) { Error = "Habillage suspendu : " + ex.Message; RemoveWindowSubclass(h, _callback, new UIntPtr(HookId)); RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, 0x105); }
            finally { PropertiesAppearanceMetrics.End("Paint", measured); }
            return result;
        }
        private void Transform(NativePropertiesGrid.Rect cell, NativePropertiesGrid.Rect dirty, uint nativeText, CellPlan plan)
        {
            int l = Math.Max(0, cell.Left), t = Math.Max(0, cell.Top), r = Math.Min(_width, cell.Right), b = Math.Min(_height, cell.Bottom);
            if (r - l < 3 || b - t < 3) return;
            // Estimate the native background from a small interior sample, preserving
            // glyph coverage instead of replacing every dark pixel with a solid color.
            var histogram = new Dictionary<int, int>();
            for (int sy = 1; sy <= 4; sy++) for (int sx = 1; sx <= 5; sx++)
            {
                int x = l + (r - l - 1) * sx / 6, y = t + (b - t - 1) * sy / 5, i = (y * _width + x) * 4;
                int rgb = (_pixels[i + 2] << 16) | (_pixels[i + 1] << 8) | _pixels[i];
                histogram[rgb] = histogram.TryGetValue(rgb, out int n) ? n + 1 : 1;
            }
            int bg = histogram.OrderByDescending(x => x.Value).First().Key;
            int cr = (bg >> 16) & 255, cg = (bg >> 8) & 255, cb = bg & 255;
            if (Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb)) > 75) return;
            double br = (bg >> 16) & 255, bgc = (bg >> 8) & 255, bb = bg & 255;
            double fr = nativeText & 255, fg = (nativeText >> 8) & 255, fb = (nativeText >> 16) & 255;
            double dr = fr - br, dg = fg - bgc, db = fb - bb, length = dr * dr + dg * dg + db * db;
            if (length < 100) return;
            Color desiredBg = AppearancePreferences.Parse(plan.Colors.Background) ?? Color.FromRgb((byte)br, (byte)bgc, (byte)bb);
            var effective = new PaletteColors { Text = plan.Colors.Text, Background = desiredBg.ToString() };
            Color desiredFg = AppearancePreferences.ReadableText(effective, plan.Dark);
            for (int y = Math.Max(t, dirty.Top); y < Math.Min(b, dirty.Bottom); y++)
                for (int x = Math.Max(l, dirty.Left); x < Math.Min(r, dirty.Right); x++)
                {
                    int i = (y * _width + x) * 4;
                    double sr = _pixels[i + 2], sg = _pixels[i + 1], sb = _pixels[i];
                    // ClearType gives R/G/B different coverage at glyph edges. A
                    // single RGB projection rejects these pixels as unrelated colors,
                    // leaving holes and native-colored fringes in recolored letters.
                    // Accept each channel's native foreground/background interval,
                    // then collapse its subpixel coverage to a neutral glyph mask.
                    if (!InChannelRange(sr, br, fr) || !InChannelRange(sg, bgc, fg) || !InChannelRange(sb, bb, fb)) continue;
                    double coverage = 0, weight = 0;
                    AddCoverage(sr, br, dr, ref coverage, ref weight);
                    AddCoverage(sg, bgc, dg, ref coverage, ref weight);
                    AddCoverage(sb, bb, db, ref coverage, ref weight);
                    double a = weight == 0 ? 0 : coverage / weight;
                    _pixels[i + 2] = (byte)(desiredBg.R + a * (desiredFg.R - desiredBg.R));
                    _pixels[i + 1] = (byte)(desiredBg.G + a * (desiredFg.G - desiredBg.G));
                    _pixels[i] = (byte)(desiredBg.B + a * (desiredFg.B - desiredBg.B));
                }
        }
        private static bool InChannelRange(double pixel, double background, double foreground) =>
            pixel >= Math.Min(background, foreground) - 24 && pixel <= Math.Max(background, foreground) + 24;
        private static void AddCoverage(double pixel, double background, double delta, ref double coverage, ref double weight)
        {
            if (Math.Abs(delta) < 10) return;
            double channelWeight = Math.Abs(delta);
            coverage += Math.Max(0, Math.Min(1, (pixel - background) / delta)) * channelWeight;
            weight += channelWeight;
        }
        private void EnsureBitmap(IntPtr dc, int width, int height)
        {
            if (_dc != IntPtr.Zero && width == _width && height == _height) return;
            FreeBitmap(); _width = width; _height = height;
            _pixels = new byte[width * height * 4]; _dc = CreateCompatibleDC(dc);
            var info = new Info { Header = new Header { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 } };
            _bitmap = CreateDIBSection(dc, ref info, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || _dc == IntPtr.Zero) { FreeBitmap(); throw new InvalidOperationException("Mémoire de dessin indisponible."); }
            _old = SelectObject(_dc, _bitmap);
        }
        private void FreeBitmap()
        {
            if (_dc != IntPtr.Zero) { if (_old != IntPtr.Zero) SelectObject(_dc, _old); if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap); DeleteDC(_dc); }
            _dc = _bitmap = _bits = _old = IntPtr.Zero; _pixels = null;
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            BeforePaint = null;
            RemoveWindowSubclass(Window, _callback, new UIntPtr(HookId)); FreeBitmap();
            RedrawWindow(Window, IntPtr.Zero, IntPtr.Zero, 0x105);
        }
    }
}

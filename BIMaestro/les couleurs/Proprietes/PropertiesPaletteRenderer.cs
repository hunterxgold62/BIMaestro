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
        internal sealed class CellPlan { internal uint Row; internal string Label; internal bool Header, Dark; internal PaletteColors Colors; internal int Index; }
        private const uint HookId = 0xB1A975;
        private readonly SubclassProc _callback;
        private NativePropertiesGrid _grid;
        private List<CellPlan> _cells = new List<CellPlan>();
        private string _key;
        private readonly Dictionary<string, int> _inkEnds = new Dictionary<string, int>();
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
            string key = string.Join("\n", cells.Select(c => c.Row + "|" + c.Label + "|" + c.Dark + "|" + c.Colors.Text + "|" + c.Colors.Background + "|" + c.Colors.Style + "|" + c.Colors.Icon + "|" + c.Colors.Badge + "|" + c.Index));
            _cells = cells;
            if (_key == key) return;
            _key = key; _inkEnds.Clear(); if (redraw) RedrawWindow(Window, IntPtr.Zero, IntPtr.Zero, 0x5);
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
                        var set = AppearanceStyles.Parse(cell.Colors.Style);
                        var accent = AppearanceStyles.Accent(cell.Colors);
                        bool zebra = set.Zebra && !cell.Header && cell.Index % 2 == 1;
                        var name = _grid.Bounds(cell.Row, 1);
                        // Measure where the label ends before the name cell is recolored.
                        bool needsInk = set.Marker == StyleSet.MarkDot || cell.Colors.Icon != null;
                        int inkEnd = needsInk ? MeasureInk(name, dirty, cell) : -1;
                        for (uint col = 1; col <= 3; col++)
                        {
                            if (cell.Header && col == 2) continue;
                            if (editing && col == 2) continue;
                            Transform(_grid.Bounds(cell.Row, col), dirty, _grid.TextColor(cell.Row, col), cell, set, col, zebra, accent);
                        }
                        if (set.Marker == StyleSet.MarkStripe && accent.HasValue) Stripe(name, dirty, accent.Value);
                        if (set.Marker == StyleSet.MarkOutline && accent.HasValue) Outline(cell.Row, name, dirty, accent.Value);
                        // Part of a multiple selection matches the condition: show how many.
                        if (cell.Colors.Badge != null && accent.HasValue && !editing) Badge(_grid.Bounds(cell.Row, 2), dirty, accent.Value, cell.Colors.Badge);
                        if (needsInk && inkEnd != NoInk)
                        {
                            if (cell.Colors.Icon != null) inkEnd = Icon(name, dirty, cell.Colors.Icon, inkEnd);
                            if (set.Marker == StyleSet.MarkDot && accent.HasValue) DrawDot(name, dirty, accent.Value, inkEnd, cell.Colors.Badge != null);
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
        private const int NoInk = int.MinValue;
        private static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
        private bool NativeColors(int l, int t, int r, int b, uint nativeText, out Color native, out Color ink)
        {
            native = ink = Colors.Transparent;
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
            if (Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb)) > 75) return false;
            native = Color.FromRgb((byte)cr, (byte)cg, (byte)cb);
            ink = Color.FromRgb((byte)(nativeText & 255), (byte)((nativeText >> 8) & 255), (byte)((nativeText >> 16) & 255));
            int dr = ink.R - cr, dg = ink.G - cg, db = ink.B - cb;
            return dr * dr + dg * dg + db * db >= 100;
        }
        private void Transform(NativePropertiesGrid.Rect cell, NativePropertiesGrid.Rect dirty, uint nativeText, CellPlan plan, StyleSet set, uint col, bool zebra, Color? accent)
        {
            int l = Math.Max(0, cell.Left), t = Math.Max(0, cell.Top), r = Math.Min(_width, cell.Right), b = Math.Min(_height, cell.Bottom);
            if (r - l < 3 || b - t < 3) return;
            bool fill = set.Background == StyleSet.BgFill, gradient = set.Background == StyleSet.BgGradient && col == 1;
            if (!fill && !gradient && !zebra && !set.Text) return;
            if (!NativeColors(l, t, r, b, nativeText, out var native, out var nativeInk)) return;
            double br = native.R, bgc = native.G, bb = native.B, fr = nativeInk.R, fg = nativeInk.G, fb = nativeInk.B;
            double dr = fr - br, dg = fg - bgc, db = fb - bb;
            Color chosenBg = AppearancePreferences.Parse(plan.Colors.Background) ?? native;
            Color baseBg = fill ? chosenBg : native;
            // Letters keep Revit's ink unless the text layer is on (or a full fill needs readable ink).
            Color desiredFg = set.Text || (fill && AppearancePreferences.Parse(plan.Colors.Background).HasValue)
                ? AppearancePreferences.ReadableText(new PaletteColors { Text = set.Text ? plan.Colors.Text : null, Background = baseBg.ToString() }, plan.Dark)
                : nativeInk;
            // Alternate rows: a very light veil of the accent (or a neutral grey).
            Color veil = accent ?? Color.FromRgb(128, 128, 128);
            double veilAmount = plan.Dark ? .10 : .06;
            for (int y = Math.Max(t, dirty.Top); y < Math.Min(b, dirty.Bottom); y++)
                for (int x = Math.Max(l, dirty.Left); x < Math.Min(r, dirty.Right); x++)
                {
                    int i = (y * _width + x) * 4;
                    Color cellBg = baseBg;
                    // The gradient fades the chosen background into the native one toward the value column.
                    if (gradient) { double p = (double)(x - l) / Math.Max(1, r - l - 1); cellBg = AppearanceStyles.Mix(chosenBg, native, p * p * (3 - 2 * p)); }
                    if (zebra) cellBg = AppearanceStyles.Mix(cellBg, veil, veilAmount);
                    double sr = _pixels[i + 2], sg = _pixels[i + 1], sb = _pixels[i];
                    // ClearType gives R/G/B different coverage at glyph edges: accept each
                    // channel's native interval, then collapse it to a neutral glyph mask.
                    if (!InChannelRange(sr, br, fr) || !InChannelRange(sg, bgc, fg) || !InChannelRange(sb, bb, fb)) continue;
                    double coverage = 0, weight = 0;
                    AddCoverage(sr, br, dr, ref coverage, ref weight);
                    AddCoverage(sg, bgc, dg, ref coverage, ref weight);
                    AddCoverage(sb, bb, db, ref coverage, ref weight);
                    double a = weight == 0 ? 0 : coverage / weight;
                    _pixels[i + 2] = (byte)(cellBg.R + a * (desiredFg.R - cellBg.R));
                    _pixels[i + 1] = (byte)(cellBg.G + a * (desiredFg.G - cellBg.G));
                    _pixels[i] = (byte)(cellBg.B + a * (desiredFg.B - cellBg.B));
                }
        }
        // Where the label text ends in the name cell. Pixels outside the dirty area may still
        // hold a previous icon or dot, so measure only on a full repaint of the cell and reuse
        // that measure (per row, label and width, so scrolling keeps it) for partial repaints.
        private int MeasureInk(NativePropertiesGrid.Rect cell, NativePropertiesGrid.Rect dirty, CellPlan plan)
        {
            int l = Math.Max(0, cell.Left), t = Math.Max(0, cell.Top), r = Math.Min(_width, cell.Right), b = Math.Min(_height, cell.Bottom);
            if (r - l < 3 || b - t < 3) return NoInk;
            string key = plan.Row + "|" + plan.Label + "|" + l + "|" + r;
            bool full = dirty.Left <= l && dirty.Right >= r && dirty.Top <= t && dirty.Bottom >= b;
            if (!full) return _inkEnds.TryGetValue(key, out int cached) ? cached : NoInk;
            if (!NativeColors(l, t, r, b, _grid.TextColor(plan.Row, 1), out var native, out _)) return NoInk;
            int lastInk = -1;
            for (int y = t; y < b; y++)
                for (int x = r - 1; x > lastInk && x >= l; x--)
                {
                    int i = (y * _width + x) * 4;
                    if (Math.Abs(_pixels[i + 2] - native.R) + Math.Abs(_pixels[i + 1] - native.G) + Math.Abs(_pixels[i] - native.B) > 90) { lastInk = x; break; }
                }
            if (_inkEnds.Count > 4000) _inkEnds.Clear();
            _inkEnds[key] = lastInk;
            return lastInk;
        }
        // A 3 px rounded bar on the left edge of the name cell; the rest stays native.
        private void Stripe(NativePropertiesGrid.Rect cell, NativePropertiesGrid.Rect dirty, Color accent)
        {
            int l = Math.Max(0, cell.Left), r = Math.Min(_width, cell.Right);
            int width = Math.Min(3, r - l), top = Math.Max(0, cell.Top) + 2, bottom = Math.Min(_height, cell.Bottom) - 2;
            for (int y = Math.Max(top, dirty.Top); y < Math.Min(bottom, dirty.Bottom); y++)
                for (int x = Math.Max(l, dirty.Left); x < Math.Min(l + width, dirty.Right); x++)
                {
                    // Soften the two ends so the bar reads as a rounded pill.
                    double a = y == top || y == bottom - 1 ? .45 : 1;
                    Blend((y * _width + x) * 4, accent, a);
                }
        }
        // A thin rounded frame around the whole row (name and value).
        private void Outline(uint row, NativePropertiesGrid.Rect name, NativePropertiesGrid.Rect dirty, Color accent)
        {
            int right = name.Right;
            for (uint col = 2; col <= 3; col++) { var c = _grid.Bounds(row, col); if (c.Right > right && c.Bottom > c.Top) right = c.Right; }
            double l = Math.Max(0, name.Left) + 1.5, t = Math.Max(0, name.Top) + 1.5, r = Math.Min(_width, right) - 2.5, b = Math.Min(_height, name.Bottom) - 2.5;
            if (r - l < 8 || b - t < 6) return;
            double radius = Math.Min(5, (b - t) / 2), half = .75;
            double cx = (l + r) / 2, cy = (t + b) / 2, hx = (r - l) / 2, hy = (b - t) / 2;
            for (int y = Clamp((int)t - 1, dirty.Top, dirty.Bottom); y < Clamp((int)b + 2, dirty.Top, dirty.Bottom); y++)
                for (int x = Clamp((int)l - 1, dirty.Left, dirty.Right); x < Clamp((int)r + 2, dirty.Left, dirty.Right); x++)
                {
                    if (x < 0 || y < 0 || x >= _width || y >= _height) continue;
                    // Signed distance to a rounded rectangle, then a 1.5 px anti-aliased line on it.
                    double qx = Math.Abs(x + .5 - cx) - hx + radius, qy = Math.Abs(y + .5 - cy) - hy + radius;
                    double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
                    double d = outside + Math.Min(Math.Max(qx, qy), 0) - radius;
                    double a = Math.Max(0, Math.Min(1, half + .5 - Math.Abs(d)));
                    if (a > 0) Blend((y * _width + x) * 4, accent, a);
                }
        }
        // The group icon, drawn just after the label. Returns where the next marker may start.
        private int Icon(NativePropertiesGrid.Rect cell, NativePropertiesGrid.Rect dirty, string id, int inkEnd)
        {
            int l = Math.Max(0, cell.Left), t = Math.Max(0, cell.Top), r = Math.Min(_width, cell.Right), b = Math.Min(_height, cell.Bottom);
            int size = Math.Max(10, Math.Min(16, b - t - 6));
            int x0 = inkEnd < 0 ? l + 4 : inkEnd + 7, y0 = t + (b - t - size) / 2;
            if (x0 + size > r - 3) return inkEnd;
            var pixels = PaletteIcons.Pixels(id, size); if (pixels == null) return inkEnd;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int px = x0 + x, py = y0 + y;
                    if (px < Math.Max(l, dirty.Left) || px >= Math.Min(r, dirty.Right) || py < Math.Max(t, dirty.Top) || py >= Math.Min(b, dirty.Bottom)) continue;
                    int s = (y * size + x) * 4, i = (py * _width + px) * 4;
                    double a = pixels[s + 3] / 255.0; if (a <= 0) continue;
                    // Premultiplied BGRA source over the palette pixels.
                    _pixels[i] = (byte)Math.Min(255, pixels[s] + _pixels[i] * (1 - a));
                    _pixels[i + 1] = (byte)Math.Min(255, pixels[s + 1] + _pixels[i + 1] * (1 - a));
                    _pixels[i + 2] = (byte)Math.Min(255, pixels[s + 2] + _pixels[i + 2] * (1 - a));
                }
            return x0 + size - 1;
        }
        // A small anti-aliased dot placed just after the label text (and icon).
        private void DrawDot(NativePropertiesGrid.Rect cell, NativePropertiesGrid.Rect dirty, Color accent, int lastInk, bool partial = false)
        {
            int l = Math.Max(0, cell.Left), t = Math.Max(0, cell.Top), r = Math.Min(_width, cell.Right), b = Math.Min(_height, cell.Bottom);
            double radius = Math.Max(2.5, Math.Min(4.5, (b - t) * .18)), cy = (t + b - 1) / 2.0;
            double cx = (lastInk < 0 ? l + 4 : lastInk + 8) + radius;
            if (cx + radius > r - 3) return;
            for (int y = (int)Math.Floor(cy - radius - 1); y <= (int)Math.Ceiling(cy + radius + 1); y++)
                for (int x = (int)Math.Floor(cx - radius - 1); x <= (int)Math.Ceiling(cx + radius + 1); x++)
                {
                    if (y < Math.Max(t, dirty.Top) || y >= Math.Min(b, dirty.Bottom) || x < Math.Max(l, dirty.Left) || x >= Math.Min(r, dirty.Right)) continue;
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    double a = Math.Max(0, Math.Min(1, radius + .5 - d));
                    // Half dot (filled on the left, ring on the right) when only part of the selection matches.
                    if (partial && x + .5 > cx) a = Math.Min(a, Math.Max(0, Math.Min(1, 1.2 - Math.Abs(d - radius + .6))));
                    if (a > 0) Blend((y * _width + x) * 4, accent, a);
                }
        }
        // A small rounded badge such as "2/5" at the right end of the value cell.
        private void Badge(NativePropertiesGrid.Rect cell, NativePropertiesGrid.Rect dirty, Color accent, string text)
        {
            int l = Math.Max(0, cell.Left), t = Math.Max(0, cell.Top), r = Math.Min(_width, cell.Right), b = Math.Min(_height, cell.Bottom);
            if (r - l < 20 || b - t < 8) return;
            int height = Math.Max(12, Math.Min(18, b - t - 4));
            var image = PaletteIcons.Badge(text, accent, height, out int width);
            if (image == null) return;
            int x0 = r - width - 4, y0 = t + (b - t - height) / 2;
            if (x0 < l + 2) return;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int px = x0 + x, py = y0 + y;
                    if (px < Math.Max(l, dirty.Left) || px >= Math.Min(r, dirty.Right) || py < Math.Max(t, dirty.Top) || py >= Math.Min(b, dirty.Bottom)) continue;
                    int s = (y * width + x) * 4, i = (py * _width + px) * 4;
                    double a = image[s + 3] / 255.0; if (a <= 0) continue;
                    _pixels[i] = (byte)Math.Min(255, image[s] + _pixels[i] * (1 - a));
                    _pixels[i + 1] = (byte)Math.Min(255, image[s + 1] + _pixels[i + 1] * (1 - a));
                    _pixels[i + 2] = (byte)Math.Min(255, image[s + 2] + _pixels[i + 2] * (1 - a));
                }
        }
        private void Blend(int i, Color c, double a)
        {
            _pixels[i + 2] = (byte)(_pixels[i + 2] + a * (c.R - _pixels[i + 2]));
            _pixels[i + 1] = (byte)(_pixels[i + 1] + a * (c.G - _pixels[i + 1]));
            _pixels[i] = (byte)(_pixels[i] + a * (c.B - _pixels[i]));
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

    // Built-in project browser icons, scaled once per size with high quality for the palette.
    internal static class PaletteIcons
    {
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();
        private static readonly Dictionary<string, Tuple<byte[], int>> Badges = new Dictionary<string, Tuple<byte[], int>>();
        // White bold text on a rounded pill of the accent color.
        internal static byte[] Badge(string text, Color accent, int height, out int width)
        {
            string key = text + "|" + accent + "|" + height;
            if (!Badges.TryGetValue(key, out var made))
            {
                made = null;
                try
                {
                    var typeface = new Typeface(new FontFamily("Segoe UI"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);
                    var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, typeface, height * .68, Brushes.White, 1.0);
                    int w = (int)Math.Ceiling(formatted.Width) + height;
                    var visual = new DrawingVisual();
                    using (var context = visual.RenderOpen())
                    {
                        context.DrawRoundedRectangle(new SolidColorBrush(accent), null, new System.Windows.Rect(0, 0, w, height), height / 2.0, height / 2.0);
                        context.DrawText(formatted, new System.Windows.Point((w - formatted.Width) / 2, (height - formatted.Height) / 2));
                    }
                    var target = new System.Windows.Media.Imaging.RenderTargetBitmap(w, height, 96, 96, PixelFormats.Pbgra32);
                    target.Render(visual);
                    var bytes = new byte[w * height * 4]; target.CopyPixels(bytes, w * 4, 0);
                    made = Tuple.Create(bytes, w);
                }
                catch { made = null; }
                if (Badges.Count > 200) Badges.Clear();
                Badges[key] = made;
            }
            width = made?.Item2 ?? 0; return made?.Item1;
        }
        internal static System.Windows.Media.Imaging.BitmapSource Source(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Any(c => !(char.IsLetterOrDigit(c) || c == (char)45))) return null;
            using (var stream = typeof(PaletteIcons).Assembly.GetManifestResourceStream("BIMaestro.Resources.BrowserIcons." + id + ".png"))
            {
                if (stream == null) return null;
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                return image;
            }
        }
        internal static byte[] Pixels(string id, int size)
        {
            string key = id + "|" + size;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            byte[] result = null;
            try
            {
                var source = Source(id);
                if (source != null)
                {
                    var visual = new DrawingVisual();
                    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                    using (var context = visual.RenderOpen()) context.DrawImage(source, new System.Windows.Rect(0, 0, size, size));
                    var target = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                    target.Render(visual);
                    result = new byte[size * size * 4]; target.CopyPixels(result, size * 4, 0);
                }
            }
            catch { result = null; }
            Cache[key] = result; return result;
        }
    }
}

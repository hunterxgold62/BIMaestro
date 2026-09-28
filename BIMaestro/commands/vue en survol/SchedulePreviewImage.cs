using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BIMaestro.ViewHover
{
    // Plain drawing data: no Revit objects outlive the Idling callback.
    internal sealed class SchedulePreviewImage
    {
        internal sealed class Cell
        {
            internal Rect Bounds;
            internal string Text = string.Empty;
            internal string Font = "Arial";
            internal bool Bold, Italic, Underline;
            internal TextAlignment Alignment;
            internal Brush Foreground = Brushes.Black;
            internal Brush Background = Brushes.White;
        }

        internal readonly List<Cell> Cells = new List<Cell>();
        internal double Width, Height;
        internal string Footer;

        internal void Save(string path)
        {
            const double margin = 12;
            int width = (int)Math.Ceiling(Math.Max(240, Width + 2 * margin));
            int height = (int)Math.Ceiling(Math.Max(80, Height + 2 * margin +
                (string.IsNullOrEmpty(Footer) ? 0 : 28)));
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                var grid = new Pen(Brushes.LightGray, 0.7);
                foreach (Cell cell in Cells)
                {
                    Rect bounds = cell.Bounds;
                    bounds.Offset(margin, margin);
                    drawing.DrawRectangle(cell.Background, grid, bounds);
                    if (bounds.Width <= 8 || bounds.Height <= 4) continue;
                    var typeface = new Typeface(new FontFamily(cell.Font),
                        cell.Italic ? FontStyles.Italic : FontStyles.Normal,
                        cell.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                    var text = new FormattedText(cell.Text ?? string.Empty,
                        CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        typeface, 13, cell.Foreground, 1.0)
                    {
                        MaxTextWidth = bounds.Width - 8,
                        MaxTextHeight = bounds.Height - 4,
                        TextAlignment = cell.Alignment,
                        Trimming = TextTrimming.CharacterEllipsis
                    };
                    if (cell.Underline) text.SetTextDecorations(TextDecorations.Underline);
                    drawing.PushClip(new RectangleGeometry(bounds));
                    drawing.DrawText(text, new Point(bounds.X + 4,
                        bounds.Y + Math.Max(2, (bounds.Height - text.Height) / 2)));
                    drawing.Pop();
                }
                if (!string.IsNullOrEmpty(Footer))
                {
                    var text = new FormattedText(Footer, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
                        Brushes.DimGray, 1.0) { MaxTextWidth = width - 2 * margin };
                    drawing.DrawText(text, new Point(margin, margin + Height + 8));
                }
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                encoder.Save(stream);
        }
    }
}

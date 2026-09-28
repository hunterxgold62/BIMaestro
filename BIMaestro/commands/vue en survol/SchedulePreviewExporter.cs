using Autodesk.Revit.DB;
using BIMaestro.Localization;
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace BIMaestro.ViewHover
{
    internal static class SchedulePreviewExporter
    {
        // Bound API reads and bitmap size even for very large schedules.
        private const int MaximumRows = 28;
        private const int MaximumColumns = 12;
        private const double MaximumWidth = 1000;

        internal static bool Export(ViewSchedule schedule, string targetPath)
        {
            var image = new SchedulePreviewImage();
            bool partial = false;
            int remainingRows = MaximumRows;
            using (TableData table = schedule.GetTableData())
            {
                foreach (SectionType kind in new[] { SectionType.Header, SectionType.Body })
                {
                    using (TableSectionData section = table.GetSectionData(kind))
                    {
                        if (section.HideSection || section.NumberOfRows == 0 ||
                            section.NumberOfColumns == 0) continue;
                        if (remainingRows == 0) { partial = true; continue; }
                        int rows = Math.Min(section.NumberOfRows, remainingRows);
                        int columns = Math.Min(section.NumberOfColumns, MaximumColumns);
                        partial |= rows < section.NumberOfRows || columns < section.NumberOfColumns;
                        var xs = new double[columns + 1];
                        for (int c = 0; c < columns; c++)
                            xs[c + 1] = xs[c] + Math.Max(60, Math.Min(300,
                                section.GetColumnWidthInPixels(section.FirstColumnNumber + c)));
                        double scale = Math.Min(1, MaximumWidth / xs[columns]);
                        for (int c = 0; c <= columns; c++) xs[c] *= scale;
                        var ys = new double[rows + 1];
                        ys[0] = image.Height;
                        for (int r = 0; r < rows; r++)
                            ys[r + 1] = ys[r] + Math.Max(26, Math.Min(48,
                                section.GetRowHeightInPixels(section.FirstRowNumber + r)));

                        for (int r = 0; r < rows; r++)
                        for (int c = 0; c < columns; c++)
                        {
                            int row = section.FirstRowNumber + r;
                            int column = section.FirstColumnNumber + c;
                            int bottom = r, right = c;
                            using (TableMergedCell merged = section.GetMergedCell(row, column))
                            {
                                if (merged != null)
                                {
                                    if (merged.Top != row || merged.Left != column) continue;
                                    bottom = Math.Min(rows - 1, merged.Bottom - section.FirstRowNumber);
                                    right = Math.Min(columns - 1, merged.Right - section.FirstColumnNumber);
                                }
                            }
                            var cell = new SchedulePreviewImage.Cell
                            {
                                Bounds = new Rect(xs[c], ys[r], xs[right + 1] - xs[c],
                                    ys[bottom + 1] - ys[r]),
                                // Read from the view, not the section: calculated/formatted
                                // body values must match the displayed schedule.
                                Text = schedule.GetCellText(kind, row, column)
                            };
                            ReadStyle(section, row, column, cell);
                            image.Cells.Add(cell);
                        }
                        image.Width = Math.Max(image.Width, xs[columns]);
                        image.Height = ys[rows];
                        remainingRows -= rows;
                    }
                }
            }
            if (image.Cells.Count == 0) return false;
            if (partial)
                image.Footer = UiLanguage.T("Aperçu partiel — ouvrez la nomenclature pour tout voir.",
                    "Partial preview — open the schedule to see all rows and columns.");

            string generated = Path.Combine(Path.GetDirectoryName(targetPath),
                "schedule-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                image.Save(generated);
                ViewDeckCachedImage.PublishGeneratedImage(generated, targetPath);
                return File.Exists(targetPath);
            }
            finally
            {
                if (File.Exists(generated)) File.Delete(generated);
            }
        }

        private static void ReadStyle(TableSectionData section, int row, int column,
            SchedulePreviewImage.Cell cell)
        {
            // Certain specialized schedules expose text but not cell styles.
            // Keep their content usable with the default style.
            try
            {
                using (TableCellStyle style = section.GetTableCellStyle(row, column))
                {
                    if (!string.IsNullOrWhiteSpace(style.FontName)) cell.Font = style.FontName;
                    cell.Bold = style.IsFontBold;
                    cell.Italic = style.IsFontItalic;
                    cell.Underline = style.IsFontUnderline;
                    cell.Foreground = ToBrush(style.TextColor, Brushes.Black);
                    cell.Background = ToBrush(style.BackgroundColor, Brushes.White);
                    cell.Alignment = style.FontHorizontalAlignment == HorizontalAlignmentStyle.Center
                        ? TextAlignment.Center
                        : style.FontHorizontalAlignment == HorizontalAlignmentStyle.Right
                            ? TextAlignment.Right : TextAlignment.Left;
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
        }

        private static Brush ToBrush(Autodesk.Revit.DB.Color color, Brush fallback)
        {
            if (color == null || !color.IsValid) return fallback;
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue));
        }
    }
}

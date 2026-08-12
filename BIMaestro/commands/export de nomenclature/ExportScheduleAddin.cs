// Visualisation/ExportScheduleCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace Visualisation
{
    // Ceinture + bretelles : évite le renommage de la classe (optionnel selon ton obfuscateur)
    [Obfuscation(Exclude = true, ApplyToMembers = false, StripAfterObfuscation = false)]
    [Transaction(TransactionMode.Manual)]
    public class ExportScheduleCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ExportScheduleCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = data.Application.ActiveUIDocument;
                Document doc = uidoc.Document;

                // 1) Vérifier qu'on est sur une nomenclature
                if (doc.ActiveView is not ViewSchedule schedule)
                {
                    TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T("Activez une vue de nomenclature avant de lancer l'export.", "Activate a schedule view before starting the export."));
                    return Result.Failed;
                }

                // 2) Dossiers de sortie
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs", "Export Nomenclatures");
                string excelDir = Path.Combine(baseDir, "Excel");
                string pdfDir = Path.Combine(baseDir, "PDF");
                Directory.CreateDirectory(excelDir);
                Directory.CreateDirectory(pdfDir);

                // 3) Nom de fichier : projet_nomenclature.ext
                string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
                if (string.IsNullOrWhiteSpace(projectName))
                {
                    projectName = doc.Title;
                }
                string scheduleName = schedule.Name;
                string fileBaseName = SanitizeFileNamePart($"{projectName}_{scheduleName}");

                // 4) Choix du format
                TaskDialog dlg = new TaskDialog(UiLanguage.T("Type d'export", "Export Type"))
                {
                    MainInstruction = UiLanguage.T("Choisissez le format d'export de la nomenclature :", "Choose the schedule export format:"),
                    MainContent = UiLanguage.T($"Projet : {projectName}\nNomenclature : {scheduleName}", $"Project: {projectName}\nSchedule: {scheduleName}"),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Excel");
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "PDF");

                var res = dlg.Show();
                if (res == TaskDialogResult.CommandLink1)
                {
                    string xlsxPath = Path.Combine(excelDir, fileBaseName + ".xlsx");
                    ExportScheduleToExcel(schedule, xlsxPath);
                    AskAndOpen(xlsxPath, "Excel");
                }
                else if (res == TaskDialogResult.CommandLink2)
                {
                    string pdfPath = Path.Combine(pdfDir, fileBaseName + ".pdf");
                    ExportScheduleToPdfViaExcel(schedule, pdfPath);
                    AskAndOpen(pdfPath, "PDF");
                }
                else
                {
                    return Result.Cancelled;
                }

                return Result.Succeeded;
            }
            catch (COMException comEx)
            {
                TaskDialog.Show(UiLanguage.T("Excel/COM", "Excel/COM"), UiLanguage.T($"Erreur COM: {comEx.Message}", $"COM error: {comEx.Message}"));
                message = comEx.Message;
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.T("Erreur", "Error"), ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ----------------------- EXPORT EXCEL -----------------------
        private void ExportScheduleToExcel(ViewSchedule schedule, string path)
        {
            var exportContent = BuildScheduleExportContent(schedule);

            Excel.Application app = null;
            Excel.Workbook wb = null;
            Excel.Worksheet ws = null;
            Excel.Range fullRange = null, headerRange = null, titleRange = null;

            try
            {
                app = new Excel.Application { Visible = false, DisplayAlerts = false };
                wb = app.Workbooks.Add();
                ws = (Excel.Worksheet)wb.ActiveSheet;

                int firstRow = 1;
                fullRange = WriteScheduleToWorksheet(ws, exportContent, firstRow, out headerRange, out titleRange);

                // Zone d'impression
                ws.PageSetup.PrintArea = fullRange.Address[false, false];

                // Sauvegarde
                wb.SaveAs(path, Excel.XlFileFormat.xlOpenXMLWorkbook);
            }
            finally
            {
                if (wb != null) wb.Close(false);
                if (app != null) app.Quit();

                ComUtils.Release(titleRange);
                ComUtils.Release(headerRange);
                ComUtils.Release(fullRange);
                ComUtils.Release(ws);
                ComUtils.Release(wb);
                ComUtils.Release(app);

                // Assure la libération des COM
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        // ----------------------- EXPORT PDF (via Excel) -----------------------
        private void ExportScheduleToPdfViaExcel(ViewSchedule schedule, string pdfPath)
        {
            var exportContent = BuildScheduleExportContent(schedule);

            Excel.Application app = null;
            Excel.Workbook wb = null;
            Excel.Worksheet ws = null;
            Excel.Range fullRange = null, headerRange = null, titleRange = null;

            try
            {
                app = new Excel.Application { Visible = false, DisplayAlerts = false };
                wb = app.Workbooks.Add();
                ws = (Excel.Worksheet)wb.ActiveSheet;

                int firstRow = 1;
                fullRange = WriteScheduleToWorksheet(ws, exportContent, firstRow, out headerRange, out titleRange);

                // --- Mise en page sûre pour Interop ---
                var ps = ws.PageSetup;

                // Tenter d'assurer une imprimante valide (non bloquant)
                try
                {
                    string ap = app.ActivePrinter; // lecture (peut throw sur certaines machines)
                    // app.ActivePrinter = "Microsoft Print to PDF"; // optionnel : forcer si tu veux
                }
                catch { /* no-op */ }

                try
                {
                    // IMPORTANT : désactiver le Zoom avant FitToPages*
                    ps.Zoom = false;

                    // Fit sur 1 page en largeur; hauteur libre
                    ps.FitToPagesWide = 1;
                    ps.FitToPagesTall = 0;

                    ps.Orientation = Excel.XlPageOrientation.xlLandscape;
                    ps.PrintArea = fullRange.Address[false, false];
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Fallback (si l'environnement bloque FitToPages* à cause d'absence d'imprimante / GPO)
                    try
                    {
                        ps.FitToPagesWide = 0;
                        ps.FitToPagesTall = 0;
                        ps.Zoom = 100;
                        ps.Orientation = Excel.XlPageOrientation.xlLandscape;
                        ps.PrintArea = fullRange.Address[false, false];
                    }
                    catch { /* ignore */ }
                }

                // Export PDF
                wb.ExportAsFixedFormat(
                    Excel.XlFixedFormatType.xlTypePDF,
                    pdfPath,
                    Excel.XlFixedFormatQuality.xlQualityStandard,
                    IncludeDocProperties: true,
                    IgnorePrintAreas: false,
                    OpenAfterPublish: false);
            }
            finally
            {
                if (wb != null) wb.Close(false);
                if (app != null) app.Quit();

                ComUtils.Release(titleRange);
                ComUtils.Release(headerRange);
                ComUtils.Release(fullRange);
                ComUtils.Release(ws);
                ComUtils.Release(wb);
                ComUtils.Release(app);

                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private void AskAndOpen(string filePath, string formatLabel)
        {
            TaskDialog td = new TaskDialog(UiLanguage.T("Export terminé", "Export Completed"))
            {
                MainInstruction = UiLanguage.T($"La nomenclature a bien été exportée en {formatLabel}.", $"The schedule was successfully exported as {formatLabel}."),
                MainContent = UiLanguage.T($"Chemin :{Environment.NewLine}{filePath}", $"Path:{Environment.NewLine}{filePath}"),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes
            };
            td.MainInstruction += UiLanguage.T("\n\nOuvrir le fichier ?", "\n\nOpen the file?");
            if (td.Show() == TaskDialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
        }

        private static ScheduleExportContent BuildScheduleExportContent(ViewSchedule schedule)
        {
            var data = schedule.GetTableData();
            var header = data.GetSectionData(SectionType.Header);
            var body = data.GetSectionData(SectionType.Body);

            var rows = new List<string[]>();
            var headerRows = ReadSectionRows(schedule, SectionType.Header, header);
            var bodyRows = ReadSectionRows(schedule, SectionType.Body, body);

            EnsureScheduleTitle(schedule, headerRows);

            rows.AddRange(headerRows);
            rows.AddRange(bodyRows);

            int columnCount = GetMaxColumnCount(rows);
            if (columnCount <= 0)
            {
                rows.Add(new[] { schedule.Name ?? "Nomenclature" });
                columnCount = 1;
            }

            int headerRowCount = Math.Min(headerRows.Count, rows.Count);
            bool shouldMergeTitle = ShouldMergeHeaderTitle(headerRows, columnCount);
            object[,] values = new object[rows.Count, columnCount];

            for (int r = 0; r < rows.Count; r++)
            {
                string[] row = rows[r];
                for (int c = 0; c < columnCount; c++)
                {
                    values[r, c] = c < row.Length ? row[c] : string.Empty;
                }
            }

            return new ScheduleExportContent(values, rows.Count, columnCount, headerRowCount, shouldMergeTitle);
        }

        private static Excel.Range WriteScheduleToWorksheet(
            Excel.Worksheet ws,
            ScheduleExportContent content,
            int firstRow,
            out Excel.Range headerRange,
            out Excel.Range titleRange)
        {
            headerRange = null;
            titleRange = null;

            Excel.Range fullRange = ws.Range[
                ws.Cells[firstRow, 1],
                ws.Cells[firstRow + content.RowCount - 1, content.ColumnCount]];
            fullRange.Value2 = content.RowCount == 1 && content.ColumnCount == 1
                ? content.Values[0, 0]
                : content.Values;
            fullRange.Columns.AutoFit();
            fullRange.Rows.AutoFit();

            fullRange.Borders.LineStyle = Excel.XlLineStyle.xlContinuous;
            fullRange.Borders.Weight = Excel.XlBorderWeight.xlThin;

            if (content.HeaderRowCount > 0)
            {
                headerRange = ws.Range[
                    ws.Cells[firstRow, 1],
                    ws.Cells[firstRow + content.HeaderRowCount - 1, content.ColumnCount]];
                headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                    System.Drawing.Color.FromArgb(198, 217, 241));
                headerRange.Font.Bold = true;

                if (content.ShouldMergeTitleRow)
                {
                    titleRange = ws.Range[
                        ws.Cells[firstRow, 1],
                        ws.Cells[firstRow, content.ColumnCount]];
                    if (content.ColumnCount > 1)
                    {
                        titleRange.Merge();
                    }
                    titleRange.HorizontalAlignment = Excel.XlHAlign.xlHAlignCenter;
                    titleRange.VerticalAlignment = Excel.XlVAlign.xlVAlignCenter;
                }
            }

            int firstBodyRow = firstRow + content.HeaderRowCount;
            for (int row = firstBodyRow + 1; row <= firstRow + content.RowCount - 1; row += 2)
            {
                var rowRange = ws.Range[ws.Cells[row, 1], ws.Cells[row, content.ColumnCount]];
                rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                    System.Drawing.Color.FromArgb(242, 242, 242));
                ComUtils.Release(rowRange);
            }

            return fullRange;
        }

        private static List<string[]> ReadSectionRows(ViewSchedule schedule, SectionType sectionType, TableSectionData sectionData)
        {
            if (sectionData == null || !sectionData.IsValidObject ||
                sectionData.NumberOfRows <= 0 || sectionData.NumberOfColumns <= 0)
            {
                return new List<string[]>();
            }

            var absoluteRows = BuildNumberRange(sectionData.FirstRowNumber, sectionData.LastRowNumber);
            var absoluteColumns = BuildNumberRange(sectionData.FirstColumnNumber, sectionData.LastColumnNumber);
            var relativeRows = BuildNumberRange(0, sectionData.NumberOfRows - 1);
            var relativeColumns = BuildNumberRange(0, sectionData.NumberOfColumns - 1);

            var candidates = new[]
            {
                ReadSectionRows(schedule, sectionType, sectionData, relativeRows, relativeColumns),
                ReadSectionRows(schedule, sectionType, sectionData, absoluteRows, absoluteColumns),
                ReadSectionRows(schedule, sectionType, sectionData, relativeRows, absoluteColumns),
                ReadSectionRows(schedule, sectionType, sectionData, absoluteRows, relativeColumns)
            };

            return candidates
                .OrderByDescending(CountFilledCells)
                .ThenByDescending(r => r.Count)
                .FirstOrDefault() ?? new List<string[]>();
        }

        private static List<string[]> ReadSectionRows(
            ViewSchedule schedule,
            SectionType sectionType,
            TableSectionData sectionData,
            IReadOnlyList<int> rowNumbers,
            IReadOnlyList<int> columnNumbers)
        {
            var rows = new List<string[]>();
            if (rowNumbers.Count == 0 || columnNumbers.Count == 0)
            {
                return rows;
            }

            foreach (int row in rowNumbers)
            {
                var cells = new List<string>();
                foreach (int column in columnNumbers)
                {
                    cells.Add(ReadCellText(schedule, sectionType, sectionData, row, column));
                }

                rows.Add(cells.ToArray());
            }

            return rows;
        }

        private static string ReadCellText(ViewSchedule schedule, SectionType sectionType, TableSectionData sectionData, int row, int column)
        {
            string text = TryReadViewCellText(schedule, sectionType, row, column);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            text = TryReadSectionCellText(sectionData, row, column);
            return text ?? string.Empty;
        }

        private static string TryReadViewCellText(ViewSchedule schedule, SectionType sectionType, int row, int column)
        {
            try
            {
                return schedule.GetCellText(sectionType, row, column) ?? string.Empty;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                return string.Empty;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                return string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static string TryReadSectionCellText(TableSectionData sectionData, int row, int column)
        {
            try
            {
                if (!sectionData.IsValidRowNumber(row) || !sectionData.IsValidColumnNumber(column))
                {
                    return string.Empty;
                }

                return sectionData.GetCellText(row, column) ?? string.Empty;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                return string.Empty;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                return string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static List<int> BuildNumberRange(int first, int last)
        {
            var result = new List<int>();
            if (last < first)
            {
                return result;
            }

            for (int i = first; i <= last; i++)
            {
                result.Add(i);
            }

            return result;
        }

        private static int CountFilledCells(List<string[]> rows)
        {
            int count = 0;
            foreach (string[] row in rows)
            {
                if (row == null)
                {
                    continue;
                }

                foreach (string cell in row)
                {
                    if (!string.IsNullOrWhiteSpace(cell))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void EnsureScheduleTitle(ViewSchedule schedule, List<string[]> headerRows)
        {
            string title = schedule?.Name ?? "Nomenclature";
            if (headerRows.Count == 0)
            {
                headerRows.Add(new[] { title });
                return;
            }

            string[] firstRow = headerRows[0];
            bool firstRowIsEmpty = firstRow == null || firstRow.All(string.IsNullOrWhiteSpace);
            if (firstRowIsEmpty)
            {
                int columnCount = Math.Max(1, firstRow?.Length ?? 1);
                var replacement = new string[columnCount];
                replacement[0] = title;
                for (int i = 1; i < columnCount; i++)
                {
                    replacement[i] = string.Empty;
                }

                headerRows[0] = replacement;
            }
            else if (IsScheduleTitleVisible(schedule) && !LooksLikeTitleRow(firstRow))
            {
                int columnCount = Math.Max(1, firstRow.Length);
                var insertedTitle = new string[columnCount];
                insertedTitle[0] = title;
                for (int i = 1; i < columnCount; i++)
                {
                    insertedTitle[i] = string.Empty;
                }

                headerRows.Insert(0, insertedTitle);
            }
        }

        private static bool IsScheduleTitleVisible(ViewSchedule schedule)
        {
            try
            {
                var definition = schedule?.Definition;
                var prop = definition?.GetType().GetProperty("ShowTitle");
                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    return (bool)prop.GetValue(definition, null);
                }
            }
            catch { }

            return true;
        }

        private static bool LooksLikeTitleRow(string[] row)
        {
            var values = row
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();

            if (values.Count == 0)
            {
                return false;
            }

            if (values.Count == 1)
            {
                return true;
            }

            return values.All(v => string.Equals(v, values[0], StringComparison.Ordinal));
        }

        private static int GetMaxColumnCount(List<string[]> rows)
        {
            int max = 0;
            foreach (string[] row in rows)
            {
                if (row != null && row.Length > max)
                {
                    max = row.Length;
                }
            }

            return max;
        }

        private static bool ShouldMergeHeaderTitle(List<string[]> headerRows, int columnCount)
        {
            if (headerRows.Count == 0 || columnCount <= 1)
            {
                return false;
            }

            string[] firstHeaderRow = headerRows[0];
            if (firstHeaderRow.Length == 0 || string.IsNullOrWhiteSpace(firstHeaderRow[0]))
            {
                return false;
            }

            for (int c = 1; c < columnCount; c++)
            {
                if (c < firstHeaderRow.Length && !string.IsNullOrWhiteSpace(firstHeaderRow[c]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string SanitizeFileNamePart(string value)
        {
            string safeValue = string.IsNullOrWhiteSpace(value) ? "Nomenclature" : value.Trim();
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                safeValue = safeValue.Replace(invalidChar, '_');
            }

            return safeValue;
        }

        private sealed class ScheduleExportContent
        {
            public ScheduleExportContent(
                object[,] values,
                int rowCount,
                int columnCount,
                int headerRowCount,
                bool shouldMergeTitleRow)
            {
                Values = values;
                RowCount = rowCount;
                ColumnCount = columnCount;
                HeaderRowCount = headerRowCount;
                ShouldMergeTitleRow = shouldMergeTitleRow;
            }

            public object[,] Values { get; }
            public int RowCount { get; }
            public int ColumnCount { get; }
            public int HeaderRowCount { get; }
            public bool ShouldMergeTitleRow { get; }
        }
    }
}

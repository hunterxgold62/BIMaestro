// Visualisation/ExportScheduleCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
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
                    TaskDialog.Show("Erreur", "Activez une vue de nomenclature avant de lancer l'export.");
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
                string scheduleName = schedule.Name;
                string fileBaseName = $"{projectName}_{scheduleName}";

                // 4) Choix du format
                TaskDialog dlg = new TaskDialog("Type d'export")
                {
                    MainInstruction = "Choisissez le format d'export de la nomenclature :",
                    MainContent = $"Projet : {projectName}\nNomenclature : {scheduleName}",
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
                TaskDialog.Show("Excel/COM", $"Erreur COM: {comEx.Message}");
                message = comEx.Message;
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Erreur", ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ----------------------- EXPORT EXCEL -----------------------
        private void ExportScheduleToExcel(ViewSchedule schedule, string path)
        {
            // Données Revit
            var data = schedule.GetTableData();
            var header = data.GetSectionData(SectionType.Header);
            var body = data.GetSectionData(SectionType.Body);

            Excel.Application app = null;
            Excel.Workbook wb = null;
            Excel.Worksheet ws = null;
            Excel.Range used = null, fullRange = null, headerRange = null;

            try
            {
                app = new Excel.Application { Visible = false, DisplayAlerts = false };
                wb = app.Workbooks.Add();
                ws = (Excel.Worksheet)wb.ActiveSheet;

                int firstRow = 1;
                int nHeaderCols = header.NumberOfColumns;
                int nBodyCols = body.NumberOfColumns;
                int nCols = Math.Max(nHeaderCols, nBodyCols);

                // En-têtes (1ère ligne du header Revit)
                for (int c = 0; c < nHeaderCols; c++)
                    ws.Cells[firstRow, c + 1] = schedule.GetCellText(SectionType.Header, 0, c);

                // Corps
                for (int r = 0; r < body.NumberOfRows; r++)
                    for (int c = 0; c < nBodyCols; c++)
                        ws.Cells[firstRow + 1 + r, c + 1] = schedule.GetCellText(SectionType.Body, r, c);

                used = ws.UsedRange;
                used.Columns.AutoFit();
                used.Rows.AutoFit();

                int totalRows = used.Rows.Count;
                int totalCols = Math.Max(used.Columns.Count, nCols);

                fullRange = ws.Range[ws.Cells[firstRow, 1], ws.Cells[firstRow + totalRows - 1, totalCols]];

                // Bordures
                fullRange.Borders.LineStyle = Excel.XlLineStyle.xlContinuous;
                fullRange.Borders.Weight = Excel.XlBorderWeight.xlThin;

                // Header pastel
                headerRange = ws.Range[ws.Cells[firstRow, 1], ws.Cells[firstRow, totalCols]];
                headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                    System.Drawing.Color.FromArgb(198, 217, 241));
                headerRange.Font.Bold = true;

                // Zebra pastel
                for (int r = 2; r <= totalRows; r += 2)
                {
                    var rowRange = ws.Range[ws.Cells[firstRow + r - 1, 1], ws.Cells[firstRow + r - 1, totalCols]];
                    rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                        System.Drawing.Color.FromArgb(242, 242, 242));
                    ComUtils.Release(rowRange);
                }

                // Zone d'impression
                ws.PageSetup.PrintArea = fullRange.Address[false, false];

                // Sauvegarde
                wb.SaveAs(path, Excel.XlFileFormat.xlOpenXMLWorkbook);
            }
            finally
            {
                if (wb != null) wb.Close(false);
                if (app != null) app.Quit();

                ComUtils.Release(headerRange);
                ComUtils.Release(fullRange);
                ComUtils.Release(used);
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
            var data = schedule.GetTableData();
            var header = data.GetSectionData(SectionType.Header);
            var body = data.GetSectionData(SectionType.Body);

            Excel.Application app = null;
            Excel.Workbook wb = null;
            Excel.Worksheet ws = null;
            Excel.Range used = null, fullRange = null, headerRange = null;

            try
            {
                app = new Excel.Application { Visible = false, DisplayAlerts = false };
                wb = app.Workbooks.Add();
                ws = (Excel.Worksheet)wb.ActiveSheet;

                int firstRow = 1;
                int nHeaderCols = header.NumberOfColumns;
                int nBodyCols = body.NumberOfColumns;
                int nCols = Math.Max(nHeaderCols, nBodyCols);

                for (int c = 0; c < nHeaderCols; c++)
                    ws.Cells[firstRow, c + 1] = schedule.GetCellText(SectionType.Header, 0, c);

                for (int r = 0; r < body.NumberOfRows; r++)
                    for (int c = 0; c < nBodyCols; c++)
                        ws.Cells[firstRow + 1 + r, c + 1] = schedule.GetCellText(SectionType.Body, r, c);

                used = ws.UsedRange;
                used.Columns.AutoFit();
                used.Rows.AutoFit();

                int totalRows = used.Rows.Count;
                int totalCols = Math.Max(used.Columns.Count, nCols);

                fullRange = ws.Range[ws.Cells[firstRow, 1], ws.Cells[firstRow + totalRows - 1, totalCols]];

                fullRange.Borders.LineStyle = Excel.XlLineStyle.xlContinuous;
                fullRange.Borders.Weight = Excel.XlBorderWeight.xlThin;

                headerRange = ws.Range[ws.Cells[firstRow, 1], ws.Cells[firstRow, totalCols]];
                headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                    System.Drawing.Color.FromArgb(198, 217, 241));
                headerRange.Font.Bold = true;

                for (int r = 2; r <= totalRows; r += 2)
                {
                    var rowRange = ws.Range[ws.Cells[firstRow + r - 1, 1], ws.Cells[firstRow + r - 1, totalCols]];
                    rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                        System.Drawing.Color.FromArgb(242, 242, 242));
                    ComUtils.Release(rowRange);
                }

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

                ComUtils.Release(headerRange);
                ComUtils.Release(fullRange);
                ComUtils.Release(used);
                ComUtils.Release(ws);
                ComUtils.Release(wb);
                ComUtils.Release(app);

                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private void AskAndOpen(string filePath, string formatLabel)
        {
            TaskDialog td = new TaskDialog("Export terminé")
            {
                MainInstruction = $"La nomenclature a bien été exportée en {formatLabel}.",
                MainContent = $"Chemin :{Environment.NewLine}{filePath}",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes
            };
            td.MainInstruction += "\n\nOuvrir le fichier ?";
            if (td.Show() == TaskDialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
        }
    }
}

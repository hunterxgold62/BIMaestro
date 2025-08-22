// ExportScheduleCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;    
using Excel = Microsoft.Office.Interop.Excel;



namespace Visualisation
{
    [Transaction(TransactionMode.Manual)]
    public class ExportScheduleCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ExportScheduleCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 1) Vérifier qu'on est sur une nomenclature
            ViewSchedule schedule = doc.ActiveView as ViewSchedule;
            if (schedule == null)
            {
                TaskDialog.Show("Erreur", "Activez une vue de nomenclature avant de lancer l'export.");
                return Result.Failed;
            }

            // 2) Préparer les dossiers racines et sous-dossiers fixes
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                "Export Nomenclatures");
            string excelDir = Path.Combine(baseDir, "Excel");
            string pdfDir = Path.Combine(baseDir, "PDF");
            Directory.CreateDirectory(excelDir);
            Directory.CreateDirectory(pdfDir);

            // 3) Construire le nom de fichier : projet_nomenclature.ext
            string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
            string scheduleName = schedule.Name;
            string fileBaseName = $"{projectName}_{scheduleName}";

            // 4) Dialogue de choix de format
            TaskDialog dlg = new TaskDialog("Type d'export")
            {
                MainInstruction = "Choisissez le format d'export de la nomenclature :",
                MainContent = $"Projet : {projectName}\nNomenclature : {scheduleName}",
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Excel");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "PDF");

            TaskDialogResult res = dlg.Show();
            if (res == TaskDialogResult.CommandLink1)
            {
                // --- Export Excel ---
                string xlsxPath = Path.Combine(excelDir, fileBaseName + ".xlsx");
                ExportScheduleToExcel(schedule, xlsxPath);
                AskAndOpen(xlsxPath, "Excel");
            }
            else if (res == TaskDialogResult.CommandLink2)
            {
                // --- Export PDF via Excel interop ---
                string pdfPath = Path.Combine(pdfDir, fileBaseName + ".pdf");
                ExportScheduleToPdfViaExcel(schedule, pdfPath);
                AskAndOpen(pdfPath, "PDF");
            }
            else
            {
                // Fermer/clôturer → annulation
                return Result.Cancelled;
            }

            return Result.Succeeded;
        }

        private void ExportScheduleToExcel(ViewSchedule schedule, string path)
        {
            // Récupération des données
            var data = schedule.GetTableData();
            var header = data.GetSectionData(SectionType.Header);
            var body = data.GetSectionData(SectionType.Body);

            // Démarrer Excel
            var app = new Excel.Application();
            var workbook = app.Workbooks.Add();
            var sheet = (Excel._Worksheet)workbook.ActiveSheet;

            int firstRow = 1;
            // En-têtes
            for (int c = 0; c < header.NumberOfColumns; c++)
                sheet.Cells[firstRow, c + 1] = schedule.GetCellText(SectionType.Header, 0, c);
            // Corps
            for (int r = 0; r < body.NumberOfRows; r++)
                for (int c = 0; c < body.NumberOfColumns; c++)
                    sheet.Cells[firstRow + 1 + r, c + 1] = schedule.GetCellText(SectionType.Body, r, c);

            // AutoFit & styling pastel
            Excel.Range used = sheet.UsedRange;
            used.Columns.AutoFit();
            used.Rows.AutoFit();

            int totalRows = used.Rows.Count;
            int totalCols = used.Columns.Count;
            var fullRange = sheet.Range[
                sheet.Cells[firstRow, 1],
                sheet.Cells[firstRow + totalRows - 1, totalCols]];

            // Bordures fines
            fullRange.Borders.LineStyle = Excel.XlLineStyle.xlContinuous;
            fullRange.Borders.Weight = Excel.XlBorderWeight.xlThin;

            // En-tête pastel
            var headerRange = sheet.Range[
                sheet.Cells[firstRow, 1],
                sheet.Cells[firstRow, totalCols]];
            headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                System.Drawing.Color.FromArgb(198, 217, 241));
            headerRange.Font.Bold = true;

            // Zebra shading pastel
            for (int r = 2; r <= totalRows; r += 2)
            {
                var rowRange = sheet.Range[
                    sheet.Cells[firstRow + r - 1, 1],
                    sheet.Cells[firstRow + r - 1, totalCols]];
                rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                    System.Drawing.Color.FromArgb(242, 242, 242));
            }

            // Zone d'impression
            sheet.PageSetup.PrintArea = fullRange.Address;

            // Sauvegarder et fermer
            workbook.SaveAs(path, Excel.XlFileFormat.xlOpenXMLWorkbook);
            workbook.Close(false);
            app.Quit();

            // Libération COM
            ReleaseCom(fullRange);
            ReleaseCom(headerRange);
            ReleaseCom(used);
            ReleaseCom(sheet);
            ReleaseCom(workbook);
            ReleaseCom(app);
        }

        private void ExportScheduleToPdfViaExcel(ViewSchedule schedule, string pdfPath)
        {
            // On reproduit la même logique & style
            var data = schedule.GetTableData();
            var header = data.GetSectionData(SectionType.Header);
            var body = data.GetSectionData(SectionType.Body);

            var app = new Excel.Application();
            var workbook = app.Workbooks.Add();
            var sheet = (Excel._Worksheet)workbook.ActiveSheet;

            int firstRow = 1;
            for (int c = 0; c < header.NumberOfColumns; c++)
                sheet.Cells[firstRow, c + 1] = schedule.GetCellText(SectionType.Header, 0, c);
            for (int r = 0; r < body.NumberOfRows; r++)
                for (int c = 0; c < body.NumberOfColumns; c++)
                    sheet.Cells[firstRow + 1 + r, c + 1] = schedule.GetCellText(SectionType.Body, r, c);

            Excel.Range used = sheet.UsedRange;
            used.Columns.AutoFit();
            used.Rows.AutoFit();

            int totalRows = used.Rows.Count;
            int totalCols = used.Columns.Count;
            var fullRange = sheet.Range[
                sheet.Cells[firstRow, 1],
                sheet.Cells[firstRow + totalRows - 1, totalCols]];

            fullRange.Borders.LineStyle = Excel.XlLineStyle.xlContinuous;
            fullRange.Borders.Weight = Excel.XlBorderWeight.xlThin;

            var headerRange = sheet.Range[
                sheet.Cells[firstRow, 1],
                sheet.Cells[firstRow, totalCols]];
            headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                System.Drawing.Color.FromArgb(198, 217, 241));
            headerRange.Font.Bold = true;

            for (int r = 2; r <= totalRows; r += 2)
            {
                var rowRange = sheet.Range[
                    sheet.Cells[firstRow + r - 1, 1],
                    sheet.Cells[firstRow + r - 1, totalCols]];
                rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                    System.Drawing.Color.FromArgb(242, 242, 242));
            }

            sheet.PageSetup.PrintArea = fullRange.Address;
            sheet.PageSetup.FitToPagesWide = 1;
            sheet.PageSetup.FitToPagesTall = false;

            workbook.ExportAsFixedFormat(
                Excel.XlFixedFormatType.xlTypePDF,
                pdfPath);

            workbook.Close(false);
            app.Quit();

            ReleaseCom(fullRange);
            ReleaseCom(headerRange);
            ReleaseCom(used);
            ReleaseCom(sheet);
            ReleaseCom(workbook);
            ReleaseCom(app);
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

        /// <summary>
        /// Libère un objet COM (Excel interop) pour éviter les processus fantômes.
        /// </summary>
        private void ReleaseCom(object obj)
        {
            if (obj == null) return;
            try
            {
                Marshal.ReleaseComObject(obj);
            }
            catch
            {
                // On ignore volontairement les erreurs de release
            }
        }

    }
}
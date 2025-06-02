// ExportImportScheduleCommand.cs
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;     // Pour Marshal.ReleaseComObject
using Excel = Microsoft.Office.Interop.Excel;
using Microsoft.Win32;                    // Pour OpenFileDialog

namespace ExportScheduleAddin
{
    [Transaction(TransactionMode.Manual)]
    public class ExportImportScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 1) Vérifier qu'on est sur une nomenclature
            var schedule = doc.ActiveView as ViewSchedule;
            if (schedule == null)
            {
                TaskDialog.Show("Erreur", "Activez une vue de nomenclature avant de lancer la commande.");
                return Result.Failed;
            }

            // 2) Préparer dossiers
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                              "RevitLogs", "Export Nomenclatures");
            string excelDir = Path.Combine(baseDir, "Excel");
            string pdfDir = Path.Combine(baseDir, "PDF");
            Directory.CreateDirectory(excelDir);
            Directory.CreateDirectory(pdfDir);

            // 3) Nom de base
            string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
            string scheduleName = schedule.Name;
            string fileBase = $"{projectName}_{scheduleName}";

            // 4) Choix de l’action
            var dlg = new TaskDialog("Action sur la nomenclature")
            {
                MainInstruction = "Choisissez une action :",
                MainContent = $"Projet : {projectName}\nNomenclature : {scheduleName}",
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Exporter en Excel");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Exporter en PDF");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Importer depuis Excel");

            var res = dlg.Show();
            if (res == TaskDialogResult.CommandLink1)
            {
                string xlsx = Path.Combine(excelDir, fileBase + ".xlsx");
                ExportScheduleToExcel(schedule, xlsx);
                AskAndOpen(xlsx, "Excel");
            }
            else if (res == TaskDialogResult.CommandLink2)
            {
                string pdf = Path.Combine(pdfDir, fileBase + ".pdf");
                ExportScheduleToPdfViaExcel(schedule, pdf);
                AskAndOpen(pdf, "PDF");
            }
            else if (res == TaskDialogResult.CommandLink3)
            {
                ImportFromExcel(doc);
            }
            else
            {
                return Result.Cancelled;
            }

            return Result.Succeeded;
        }

        private void ExportScheduleToExcel(ViewSchedule schedule, string path)
        {
            var data = schedule.GetTableData();
            var header = data.GetSectionData(SectionType.Header);
            var body = data.GetSectionData(SectionType.Body);

            // Trouver l'index de la colonne "ID" dans l'en-tête
            int idColIdx = -1;
            for (int c = 0; c < header.NumberOfColumns; c++)
            {
                string hdr = schedule.GetCellText(SectionType.Header, 0, c).Trim();
                if (hdr.Equals("ID", StringComparison.OrdinalIgnoreCase))
                {
                    idColIdx = c;
                    break;
                }
            }
            if (idColIdx < 0)
            {
                TaskDialog.Show("Erreur",
                    "La nomenclature doit contenir la colonne 'ID'. Ajoutez-la avant d'exporter.");
                return;
            }

            // Lancer Excel
            var app = new Excel.Application();
            var workbook = app.Workbooks.Add();
            var sheet = (Excel._Worksheet)workbook.ActiveSheet;

            int firstRow = 1;
            int nCols = header.NumberOfColumns;
            int nRows = body.NumberOfRows;

            // En-têtes : colonne cachée ElementId + toutes les autres
            sheet.Cells[firstRow, 1] = "ElementId";
            for (int c = 0; c < nCols; c++)
            {
                sheet.Cells[firstRow, c + 2] = schedule.GetCellText(SectionType.Header, 0, c);
            }

            // Corps : on lit d'abord la colonne ID, puis le reste
            for (int r = 0; r < nRows; r++)
            {
                string idText = schedule.GetCellText(SectionType.Body, r, idColIdx).Trim();
                sheet.Cells[firstRow + 1 + r, 1] = idText;
                for (int c = 0; c < nCols; c++)
                {
                    sheet.Cells[firstRow + 1 + r, c + 2] =
                        schedule.GetCellText(SectionType.Body, r, c);
                }
            }

            // Masquer la colonne ID
            sheet.Columns[1].Hidden = true;

            // Mise en forme via méthode partagée
            ApplyExcelStyling(sheet, firstRow);

            // Sauvegarder & fermer
            workbook.SaveAs(path, Excel.XlFileFormat.xlOpenXMLWorkbook);
            workbook.Close(false);
            app.Quit();
            ReleaseCom(sheet);
            ReleaseCom(workbook);
            ReleaseCom(app);
        }

        private void ExportScheduleToPdfViaExcel(ViewSchedule schedule, string pdfPath)
        {
            var data = schedule.GetTableData();
            var header = data.GetSectionData(SectionType.Header);
            var body = data.GetSectionData(SectionType.Body);

            // Même logique pour trouver idColIdx
            int idColIdx = -1;
            for (int c = 0; c < header.NumberOfColumns; c++)
            {
                if (schedule.GetCellText(SectionType.Header, 0, c)
                            .Trim()
                            .Equals("ID", StringComparison.OrdinalIgnoreCase))
                {
                    idColIdx = c;
                    break;
                }
            }
            if (idColIdx < 0)
            {
                TaskDialog.Show("Erreur",
                    "La nomenclature doit contenir la colonne 'ID' avant export PDF.");
                return;
            }

            // Lancer Excel
            var app = new Excel.Application();
            var workbook = app.Workbooks.Add();
            var sheet = (Excel._Worksheet)workbook.ActiveSheet;

            int firstRow = 1;
            int nCols = header.NumberOfColumns;
            int nRows = body.NumberOfRows;

            sheet.Cells[firstRow, 1] = "ElementId";
            for (int c = 0; c < nCols; c++)
            {
                sheet.Cells[firstRow, c + 2] = schedule.GetCellText(SectionType.Header, 0, c);
            }

            for (int r = 0; r < nRows; r++)
            {
                string idText = schedule.GetCellText(SectionType.Body, r, idColIdx).Trim();
                sheet.Cells[firstRow + 1 + r, 1] = idText;
                for (int c = 0; c < nCols; c++)
                {
                    sheet.Cells[firstRow + 1 + r, c + 2] =
                        schedule.GetCellText(SectionType.Body, r, c);
                }
            }

            sheet.Columns[1].Hidden = true;
            ApplyExcelStyling(sheet, firstRow);

            // Ajustements PDF
            sheet.PageSetup.FitToPagesWide = 1;
            sheet.PageSetup.FitToPagesTall = false;
            workbook.ExportAsFixedFormat(
                Excel.XlFixedFormatType.xlTypePDF,
                pdfPath);

            workbook.Close(false);
            app.Quit();
            ReleaseCom(sheet);
            ReleaseCom(workbook);
            ReleaseCom(app);
        }

        private void ImportFromExcel(Document doc)
        {
            // Ouvrir boîte de sélection
            var ofd = new OpenFileDialog
            {
                Filter = "Fichiers Excel (*.xlsx)|*.xlsx",
                Title = "Sélectionnez le fichier d'import"
            };
            if (ofd.ShowDialog() != true) return;
            string path = ofd.FileName;

            // Lancer Excel
            var app = new Excel.Application();
            var workbook = app.Workbooks.Open(path);
            var sheet = (Excel._Worksheet)workbook.Sheets[1];
            var used = sheet.UsedRange;

            int rows = used.Rows.Count;
            int cols = used.Columns.Count;

            // Repérer la colonne cachée ElementId (col 1)
            int colId = 1;

            using (var t = new Transaction(doc, "Import paramètres depuis Excel"))
            {
                t.Start();
                // Chaque colonne >1 est un paramètre à mettre à jour
                for (int c = 2; c <= cols; c++)
                {
                    string paramName = (sheet.Cells[1, c] as Excel.Range).Text.ToString().Trim();
                    if (string.IsNullOrEmpty(paramName)) continue;

                    for (int r = 2; r <= rows; r++)
                    {
                        string idText = (sheet.Cells[r, colId] as Excel.Range).Text.ToString().Trim();
                        if (!int.TryParse(idText, out int idNum)) continue;

                        var el = doc.GetElement(new ElementId(idNum));
                        if (el == null) continue;

                        var p = el.LookupParameter(paramName);
                        if (p == null || p.IsReadOnly) continue;

                        string value = (sheet.Cells[r, c] as Excel.Range).Text.ToString();
                        p.Set(value);
                    }
                }
                t.Commit();
            }

            // Nettoyage
            workbook.Close(false);
            app.Quit();
            ReleaseCom(used);
            ReleaseCom(sheet);
            ReleaseCom(workbook);
            ReleaseCom(app);

            TaskDialog.Show("Import terminé", "Les paramètres ont été mis à jour avec succès.");
        }

        private void AskAndOpen(string filePath, string formatLabel)
        {
            var td = new TaskDialog("Export terminé")
            {
                MainInstruction = $"La nomenclature a été exportée en {formatLabel}.",
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

        private void ApplyExcelStyling(Excel._Worksheet sheet, int firstRow)
        {
            var used = sheet.UsedRange;
            used.Columns.AutoFit();
            used.Rows.AutoFit();

            int totalRows = used.Rows.Count;
            int totalCols = used.Columns.Count;
            var fullRange = sheet.Range[
                sheet.Cells[firstRow, 1],
                sheet.Cells[firstRow + totalRows - 1, totalCols]
            ];
            fullRange.Borders.LineStyle = Excel.XlLineStyle.xlContinuous;
            fullRange.Borders.Weight = Excel.XlBorderWeight.xlThin;

            var headerRange = sheet.Range[
                sheet.Cells[firstRow, 1],
                sheet.Cells[firstRow, totalCols]
            ];
            headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                System.Drawing.Color.FromArgb(198, 217, 241));
            headerRange.Font.Bold = true;

            for (int rr = 2; rr <= totalRows; rr += 2)
            {
                var rowRange = sheet.Range[
                    sheet.Cells[firstRow + rr - 1, 1],
                    sheet.Cells[firstRow + rr - 1, totalCols]
                ];
                rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                    System.Drawing.Color.FromArgb(242, 242, 242));
            }
        }

        private void ReleaseCom(object obj)
        {
            if (obj == null) return;
            try { Marshal.ReleaseComObject(obj); }
            catch { /* ignore */ }
        }
    }
}

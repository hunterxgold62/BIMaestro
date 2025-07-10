using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using OfficeOpenXml.Table;
using WF = System.Windows.Forms;
using Color = System.Drawing.Color;

namespace MyRevitAddin
{
    [Transaction(TransactionMode.Manual)]
    public class ExcelScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData cmdData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uiDoc = cmdData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Choix Export ou Import
            var dlg = new TaskDialog("Excel Nomenclature")
            {
                MainInstruction = "Que voulez-vous faire ?",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Exporter une nomenclature");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Importer depuis Excel");
            var res = dlg.Show();

            if (res == TaskDialogResult.CommandLink1)
                ExportSchedule(doc);
            else if (res == TaskDialogResult.CommandLink2)
                ImportSchedule(cmdData.Application, doc);

            return Result.Succeeded;
        }

        private void ExportSchedule(Document doc)
        {
            // 1) Liste des vues nomenclatures
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .ToList();

            // 2) Sélection de la nomenclature
            var combo = new WF.ComboBox { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 300 };
            schedules.ForEach(vs => combo.Items.Add(vs.Name));
            combo.SelectedIndex = 0;
            using (var form = new WF.Form { Text = "Sélectionnez une nomenclature", Width = 350, Height = 120 })
            {
                form.Controls.Add(combo);
                var ok = new WF.Button { Text = "OK", DialogResult = WF.DialogResult.OK, Dock = WF.DockStyle.Bottom };
                form.Controls.Add(ok);
                form.AcceptButton = ok;
                if (form.ShowDialog() != WF.DialogResult.OK)
                    return;
            }

            var vsel = schedules.First(x => x.Name == combo.SelectedItem.ToString());
            var def = vsel.Definition;

            // 3) Ajout du champ ElementId si nécessaire
            var schedFields = def.GetSchedulableFields();
            var idField = schedFields
                .FirstOrDefault(sf => sf.ParameterId.IntegerValue == (int)BuiltInParameter.ID_PARAM);
            bool hasId = def.GetFieldOrder()
                .Any(fid => def.GetField(fid).ParameterId.IntegerValue == (int)BuiltInParameter.ID_PARAM);
            if (idField != null && !hasId)
            {
                using (var trx = new Transaction(doc, "Ajout Champ ElementId"))
                {
                    trx.Start();
                    def.AddField(idField);
                    trx.Commit();
                }
                doc.Regenerate();
                vsel = doc.GetElement(vsel.Id) as ViewSchedule;
                def = vsel.Definition;
            }

            // 4) Collecte des éléments de la nomenclature
            var items = new FilteredElementCollector(doc, vsel.Id)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            // 5) Ordre des champs
            var fieldOrder = def.GetFieldOrder().ToList();

            // 6) Sélection du fichier
            var sfd = new WF.SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = vsel.Name + ".xlsx"
            };
            if (sfd.ShowDialog() != WF.DialogResult.OK)
                return;

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var fileInfo = new FileInfo(sfd.FileName);
            if (fileInfo.Exists) fileInfo.Delete();

            // 7) Export via EPPlus
            using (var pkg = new ExcelPackage(fileInfo))
            {
                var ws = pkg.Workbook.Worksheets.Add(vsel.Name);

                // Police et taille
                ws.Cells.Style.Font.Name = "Calibri";
                ws.Cells.Style.Font.Size = 11;

                // En-têtes
                ws.Cells[1, 1].Value = "Element Id";
                ws.Cells[1, 1].Style.Font.Bold = true;
                ws.Cells[1, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[1, 1].Style.Fill.BackgroundColor.SetColor(Color.LightGray);
                ws.Cells[1, 1].Style.Font.Color.SetColor(Color.Black);

                for (int c = 0; c < fieldOrder.Count; c++)
                {
                    var heading = def.GetField(fieldOrder[c]).ColumnHeading;
                    var cell = ws.Cells[1, c + 2];
                    cell.Value = heading;
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    bool modifiable = items.Any() &&
                                      items.First().LookupParameter(heading) is Parameter p && !p.IsReadOnly;
                    cell.Style.Fill.BackgroundColor.SetColor(modifiable ? Color.LightGreen : Color.LightCoral);
                    cell.Style.Font.Color.SetColor(Color.Black);
                }

                // Gel des volets
                ws.View.FreezePanes(2, 1);

                // Données + zébrure
                for (int r = 0; r < items.Count; r++)
                {
                    var el = items[r];
                    int row = r + 2;
                    var stripe = (r % 2 == 0) ? Color.White : Color.FromArgb(242, 242, 242);

                    ws.Cells[row, 1].Value = el.Id.IntegerValue;
                    ws.Cells[row, 1, row, fieldOrder.Count + 1]
                      .Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[row, 1, row, fieldOrder.Count + 1]
                      .Style.Fill.BackgroundColor.SetColor(stripe);

                    for (int c = 0; c < fieldOrder.Count; c++)
                    {
                        var name = def.GetField(fieldOrder[c]).ColumnHeading;
                        var param = el.LookupParameter(name);
                        object val = null;
                        if (param != null)
                        {
                            switch (param.StorageType)
                            {
                                case StorageType.Double: val = param.AsDouble(); break;
                                case StorageType.Integer: val = param.AsInteger(); break;
                                case StorageType.String: val = param.AsString(); break;
                                case StorageType.ElementId: val = param.AsElementId().IntegerValue; break;
                            }
                        }
                        ws.Cells[row, c + 2].Value = val;
                    }
                }

                // Masquer ID
                ws.Column(1).Hidden = true;

                // Tableau structuré
                int totalCols = 1 + fieldOrder.Count;
                var range = ws.Cells[1, 1, items.Count + 1, totalCols];
                string tblName = Regex.Replace(vsel.Name, @"\W+", "_");
                if (!char.IsLetter(tblName, 0)) tblName = "tbl" + tblName;
                var tblEp = ws.Tables.Add(range, tblName);
                tblEp.ShowHeader = true;
                tblEp.TableStyle = TableStyles.Light1;

                // Ajustement colonnes
                ws.Cells[ws.Dimension.Address].AutoFitColumns();

                pkg.Save();
            }

            TaskDialog.Show("Terminé", $"Export créé : {sfd.FileName}");
        }

        private void ImportSchedule(UIApplication uiApp, Document doc)
        {
            var ofd = new WF.OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx" };
            if (ofd.ShowDialog() != WF.DialogResult.OK) return;

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using (var pkg = new ExcelPackage(new FileInfo(ofd.FileName)))
            {
                var ws = pkg.Workbook.Worksheets.First();
                int rows = ws.Dimension.End.Row;
                int cols = ws.Dimension.End.Column;
                var hdrs = Enumerable.Range(1, cols)
                                     .Select(c => ws.Cells[1, c].Text)
                                     .ToArray();

                using (var trx = new Transaction(doc, "Import Excel → Revit"))
                {
                    trx.Start();
                    for (int r = 2; r <= rows; r++)
                    {
                        if (!int.TryParse(ws.Cells[r, 1].Text, out int id)) continue;
                        var e = doc.GetElement(new ElementId(id));
                        if (e == null) continue;

                        for (int c = 2; c <= cols; c++)
                        {
                            var param = e.LookupParameter(hdrs[c - 1]);
                            if (param == null || param.IsReadOnly) continue;
                            var txt = ws.Cells[r, c].Text;
                            switch (param.StorageType)
                            {
                                case StorageType.Double:
                                    if (double.TryParse(txt, out double d)) param.Set(d);
                                    break;
                                case StorageType.Integer:
                                    if (int.TryParse(txt, out int i)) param.Set(i);
                                    break;
                                case StorageType.String:
                                    param.Set(txt);
                                    break;
                                case StorageType.ElementId:
                                    if (int.TryParse(txt, out int eid)) param.Set(new ElementId(eid));
                                    break;
                            }
                        }
                    }
                    trx.Commit();
                }
            }

            TaskDialog.Show("Import terminé", "Les données ont été importées dans Revit.");
        }
    }
}

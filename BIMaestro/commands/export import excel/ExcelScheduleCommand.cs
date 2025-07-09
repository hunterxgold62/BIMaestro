using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
            var uiDoc = cmdData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            var dlg = new TaskDialog("Excel Nomenclature")
            {
                MainInstruction = "Que voulez-vous faire ?",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Exporter une nomenclature");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Importer depuis Excel");

            if (dlg.Show() == TaskDialogResult.CommandLink1)
                ExportSchedule(doc);
            else
                ImportSchedule(cmdData.Application, doc);

            return Result.Succeeded;
        }

        private void ExportSchedule(Document doc)
        {
            // 1) Sélection de la vue Schedule
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .ToList();

            if (!schedules.Any())
            {
                TaskDialog.Show("Erreur", "Aucune nomenclature trouvée.");
                return;
            }

            var combo = new WF.ComboBox
            {
                DropDownStyle = WF.ComboBoxStyle.DropDownList,
                Width = 300
            };
            schedules.ForEach(vs => combo.Items.Add(vs.Name));
            combo.SelectedIndex = 0;

            using (var form = new WF.Form { Text = "Sélectionnez une nomenclature", Width = 350, Height = 120 })
            {
                form.Controls.Add(combo);
                var ok = new WF.Button
                {
                    Text = "OK",
                    DialogResult = WF.DialogResult.OK,
                    Dock = WF.DockStyle.Bottom
                };
                form.Controls.Add(ok);
                form.AcceptButton = ok;
                if (form.ShowDialog() != WF.DialogResult.OK)
                    return;
            }

            var vsel = schedules.First(vs => vs.Name == combo.SelectedItem.ToString());
            var def = vsel.Definition;

            // 2) Collecte + filtre natifs
            var collector = new FilteredElementCollector(doc, vsel.Id)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            // On garde une copie pour l'ordre original
            var originalList = collector.ToList();
            var originalIndex = originalList
                .Select((e, i) => new { id = e.Id.IntegerValue, idx = i })
                .ToDictionary(x => x.id, x => x.idx);

            // 3) Tri multi-niveau selon Tri/Regroupement
            var elems = originalList;
            IOrderedEnumerable<Element> ordered = null;
            int sortCount = def.GetSortGroupFieldCount();

            for (int i = 0; i < sortCount; i++)
            {
                var sgf = def.GetSortGroupField(i);
                var fld = def.GetField(sgf.FieldId);
                bool asc = sgf.SortOrder == ScheduleSortOrder.Ascending;

                Func<Element, IComparable> key = e =>
                    GetSortableAdvanced(e.LookupParameter(fld.ColumnHeading));

                if (ordered == null)
                    ordered = asc
                        ? elems.OrderBy(key)
                        : elems.OrderByDescending(key);
                else
                    ordered = asc
                        ? ordered.ThenBy(key)
                        : ordered.ThenByDescending(key);
            }

            if (ordered != null)
            {
                // 4) Critère de retour à l'ordre original Revit
                ordered = ordered.ThenBy(e =>
                    originalIndex.TryGetValue(e.Id.IntegerValue, out var idx) ? idx : int.MaxValue);
                elems = ordered.ToList();
            }

            // 5) SaveFileDialog
            var sfd = new WF.SaveFileDialog
            {
                Filter = "Fichier Excel (*.xlsx)|*.xlsx",
                FileName = vsel.Name + ".xlsx"
            };
            if (sfd.ShowDialog() != WF.DialogResult.OK) return;

            // 6) Export EPPlus
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var fi = new FileInfo(sfd.FileName);
            if (fi.Exists) fi.Delete();

            using (var pkg = new ExcelPackage(fi))
            {
                var ws = pkg.Workbook.Worksheets.Add(vsel.Name);
                ws.Cells.Style.Font.Name = "Calibri";
                ws.Cells.Style.Font.Size = 11;

                // 6a) En-têtes
                ws.Cells[1, 1].Value = "Element Id";
                ws.Cells[1, 1].Style.Font.Bold = true;
                ws.Cells[1, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[1, 1].Style.Fill.BackgroundColor.SetColor(Color.LightGray);

                var fieldOrder = def.GetFieldOrder().ToList();
                for (int c = 0; c < fieldOrder.Count; c++)
                {
                    var fld = def.GetField(fieldOrder[c]);
                    var cell = ws.Cells[1, c + 2];
                    cell.Value = fld.ColumnHeading;
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(Color.LightGray);
                }

                // 6b) Lignes
                for (int r = 0; r < elems.Count; r++)
                {
                    var e = elems[r];
                    ws.Cells[r + 2, 1].Value = e.Id.IntegerValue;

                    for (int c = 0; c < fieldOrder.Count; c++)
                    {
                        var fld = def.GetField(fieldOrder[c]);
                        var prm = e.LookupParameter(fld.ColumnHeading);
                        object val = "";

                        if (prm != null)
                        {
                            switch (prm.StorageType)
                            {
                                case StorageType.Double:
                                    double raw = prm.AsDouble();
                                    var fmt = fld.GetFormatOptions();
                                    var activeFmt = fmt.UseDefault
                                        ? doc.GetUnits().GetFormatOptions(fld.GetSpecTypeId())
                                        : fmt;
                                    double conv = UnitUtils.ConvertFromInternalUnits(
                                        raw, activeFmt.GetUnitTypeId());
                                    val = Math.Round(conv, 3);
                                    break;
                                case StorageType.Integer:
                                    val = prm.AsInteger();
                                    break;
                                case StorageType.String:
                                    val = prm.AsString();
                                    break;
                                case StorageType.ElementId:
                                    val = prm.AsElementId().IntegerValue;
                                    break;
                            }
                        }

                        ws.Cells[r + 2, c + 2].Value = val;
                    }

                    // Zebra striping
                    var bg = (r % 2 == 0)
                        ? Color.White
                        : Color.FromArgb(242, 242, 242);
                    ws.Cells[r + 2, 1, r + 2, fieldOrder.Count + 1]
                      .Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[r + 2, 1, r + 2, fieldOrder.Count + 1]
                      .Style.Fill.BackgroundColor.SetColor(bg);
                }

                // 6c) Mise en forme Tableau
                ws.Cells[ws.Dimension.Address].AutoFitColumns();
                var range = ws.Cells[1, 1, elems.Count + 1, fieldOrder.Count + 1];
                var tableName = vsel.Name.Replace(" ", "_");
                var table = ws.Tables.Add(range, tableName);
                table.ShowHeader = true;
                table.TableStyle = TableStyles.Light1;

                pkg.Save();
            }

            TaskDialog.Show("Terminé", $"Export créé : {sfd.FileName}");
        }

        private void ImportSchedule(UIApplication uiApp, Document doc)
        {
            var ofd = new WF.OpenFileDialog { Filter = "Fichier Excel (*.xlsx)|*.xlsx" };
            if (ofd.ShowDialog() != WF.DialogResult.OK) return;

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using (var pkg = new ExcelPackage(new FileInfo(ofd.FileName)))
            {
                var ws = pkg.Workbook.Worksheets.First();
                int rows = ws.Dimension.End.Row, cols = ws.Dimension.End.Column;
                var headers = Enumerable.Range(1, cols)
                                        .Select(i => ws.Cells[1, i].Text)
                                        .ToArray();

                using (var tx = new Transaction(doc, "Import Excel → Revit"))
                {
                    tx.Start();
                    for (int r = 2; r <= rows; r++)
                    {
                        if (!int.TryParse(ws.Cells[r, 1].Text, out int id)) continue;
                        var e = doc.GetElement(new ElementId(id));
                        if (e == null) continue;

                        for (int c = 2; c <= cols; c++)
                        {
                            var param = e.LookupParameter(headers[c - 1]);
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
                    tx.Commit();
                }
            }

            TaskDialog.Show("Import terminé", "Les données ont été importées dans Revit.");
        }

        /// <summary>
        /// Tri « intelligent » : convertit d’abord la valeur en double si possible,
        /// sinon retourne la chaîne brute (pour un tri numérique correct).
        /// </summary>
        private IComparable GetSortableAdvanced(Parameter prm)
        {
            if (prm == null) return null!;
            switch (prm.StorageType)
            {
                case StorageType.Double:
                    return prm.AsDouble();
                case StorageType.Integer:
                    return prm.AsInteger();
                case StorageType.String:
                    var s = prm.AsString() ?? "";
                    if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out var d))
                        return d;
                    return s;
                case StorageType.ElementId:
                    return prm.AsElementId().IntegerValue;
                default:
                    return prm.AsString() ?? "";
            }
        }
    }
}

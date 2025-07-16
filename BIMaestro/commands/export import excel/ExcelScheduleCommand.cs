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

namespace Modification
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
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .ToList();

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

            var schedFields = def.GetSchedulableFields();
            var idField = schedFields.FirstOrDefault(sf => sf.ParameterId.IntegerValue == (int)BuiltInParameter.ID_PARAM);
            bool hasId = def.GetFieldOrder().Any(fid => def.GetField(fid).ParameterId.IntegerValue == (int)BuiltInParameter.ID_PARAM);
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

            var items = new FilteredElementCollector(doc, vsel.Id)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            var sortFields = def.GetSortGroupFields().ToList();
            IOrderedEnumerable<Element> ordered = null;
            foreach (var sf in sortFields)
            {
                var fieldDef = def.GetField(sf.FieldId);
                var pid = fieldDef.ParameterId;
                Func<Element, IComparable> keySelector = el =>
                {
                    var p = GetElementParameter(el, doc, pid);
                    if (p == null)
                    {
                        switch (p?.StorageType ?? StorageType.String)
                        {
                            case StorageType.Double: return double.MaxValue;
                            case StorageType.Integer: return int.MaxValue;
                            default: return "~~";
                        }
                    }
                    switch (p.StorageType)
                    {
                        case StorageType.Double:
                            return p.AsDouble();
                        case StorageType.Integer:
                            return p.AsInteger();
                        case StorageType.String:
                            var txt = p.AsValueString();
                            if (double.TryParse(txt, out double d)) return d;
                            return txt;
                        case StorageType.ElementId:
                            return p.AsElementId().IntegerValue;
                        default:
                            return p.AsValueString();
                    }
                };

                bool asc = sf.SortOrder == ScheduleSortOrder.Ascending;
                if (ordered == null)
                    ordered = asc ? items.OrderBy(keySelector) : items.OrderByDescending(keySelector);
                else
                    ordered = asc ? ordered.ThenBy(keySelector) : ordered.ThenByDescending(keySelector);
            }
            if (ordered != null)
                items = ordered.ToList();

            var fieldOrder = def.GetFieldOrder().ToList();

            var sfd = new WF.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = vsel.Name + ".xlsx" };
            if (sfd.ShowDialog() != WF.DialogResult.OK) return;

            ExcelPackage.License.SetNonCommercialPersonal("OK1");

            var fileInfo = new FileInfo(sfd.FileName);
            if (fileInfo.Exists) fileInfo.Delete();

            using (var pkg = new ExcelPackage(fileInfo))
            {
                var ws = pkg.Workbook.Worksheets.Add(vsel.Name);
                ws.Cells.Style.Font.Name = "Calibri";
                ws.Cells.Style.Font.Size = 11;

                ws.Cells[1, 1].Value = "Element Id";
                ws.Cells[1, 1].Style.Font.Bold = true;
                ws.Cells[1, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[1, 1].Style.Fill.BackgroundColor.SetColor(Color.LightGray);
                ws.Cells[1, 1].Style.Font.Color.SetColor(Color.Black);
                for (int c = 0; c < fieldOrder.Count; c++)
                {
                    var fld = def.GetField(fieldOrder[c]);
                    var head = fld.ColumnHeading;
                    var cell = ws.Cells[1, c + 2];
                    cell.Value = head;
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    bool modifiable = GetElementParameter(items.First(), doc, fld.ParameterId) is Parameter pp && !pp.IsReadOnly;
                    cell.Style.Fill.BackgroundColor.SetColor(modifiable ? Color.LightGreen : Color.LightCoral);
                    cell.Style.Font.Color.SetColor(Color.Black);
                }
                ws.View.FreezePanes(2, 1);

                int row = 2;
                var prevKeys = new object[sortFields.Count];

                foreach (var el in items)
                {
                    for (int i = 0; i < sortFields.Count; i++)
                    {
                        if (!sortFields[i].ShowHeader) continue;
                        var fld = def.GetField(sortFields[i].FieldId);
                        var p = GetElementParameter(el, doc, fld.ParameterId);
                        var current = p?.AsValueString() ?? string.Empty;
                        if (!Equals(prevKeys[i], current))
                        {
                            var hdrRange = ws.Cells[row, 1, row, fieldOrder.Count + 1];
                            hdrRange.Value = current;
                            hdrRange.Merge = false;
                            hdrRange.Style.Font.Italic = true;
                            hdrRange.Style.Fill.PatternType = ExcelFillStyle.Solid;
                            hdrRange.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(220, 230, 241));
                            row++;
                            prevKeys[i] = current;
                            for (int j = i + 1; j < prevKeys.Length; j++) prevKeys[j] = null;
                        }
                    }

                    ws.Cells[row, 1].Value = el.Id.IntegerValue;
                    for (int c = 0; c < fieldOrder.Count; c++)
                    {
                        var fld = def.GetField(fieldOrder[c]);
                        var p = GetElementParameter(el, doc, fld.ParameterId);
                        ws.Cells[row, c + 2].Value = p?.AsValueString();
                    }
                    var stripe = (row % 2 == 0) ? Color.White : Color.FromArgb(242, 242, 242);
                    ws.Cells[row, 1, row, fieldOrder.Count + 1]
                      .Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[row, 1, row, fieldOrder.Count + 1]
                      .Style.Fill.BackgroundColor.SetColor(stripe);
                    row++;
                }

                ws.Column(1).Hidden = true;

                var totalCols = 1 + fieldOrder.Count;
                var tblRange = ws.Cells[1, 1, row - 1, totalCols];
                var tblName = Regex.Replace(vsel.Name, "\\W+", "_");
                if (!char.IsLetter(tblName, 0)) tblName = "tbl" + tblName;
                var table = ws.Tables.Add(tblRange, tblName);
                table.ShowHeader = true;
                table.TableStyle = TableStyles.Light1;
                ws.Cells[ws.Dimension.Address].AutoFitColumns();
                pkg.Save();
            }

            TaskDialog.Show("Terminé", $"Export créé : {sfd.FileName}");
        }

        private static Parameter GetElementParameter(Element el, Document doc, ElementId paramId)
        {
            if (Enum.IsDefined(typeof(BuiltInParameter), paramId.IntegerValue))
                return el.get_Parameter((BuiltInParameter)paramId.IntegerValue);

            var paramElem = doc.GetElement(paramId) as ParameterElement;
            return paramElem != null
                ? el.get_Parameter(paramElem.GetDefinition())
                : null;
        }

        private void ImportSchedule(UIApplication uiApp, Document doc)
        {
            var ofd = new WF.OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx" };
            if (ofd.ShowDialog() != WF.DialogResult.OK) return;

            ExcelPackage.License.SetNonCommercialPersonal("OK1");

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

                            switch (param.StorageType)
                            {
                                case StorageType.Double:
                                    // Lit la valeur Excel en tant que double
                                    double userVal = ws.Cells[r, c].GetValue<double>();
                                    // Convertit vers unités internes Revit
                                    double internalVal = UnitUtils.ConvertToInternalUnits(
                                        userVal,
                                        param.GetUnitTypeId()
                                    );
                                    param.Set(internalVal);
                                    break;

                                case StorageType.Integer:
                                    if (int.TryParse(ws.Cells[r, c].Text, out var i))
                                        param.Set(i);
                                    break;

                                case StorageType.String:
                                    param.Set(ws.Cells[r, c].Text);
                                    break;

                                case StorageType.ElementId:
                                    if (int.TryParse(ws.Cells[r, c].Text, out var ei))
                                        param.Set(new ElementId(ei));
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

using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using OfficeOpenXml.Table;
using WF = System.Windows.Forms;
using Color = System.Drawing.Color;
using Licensing;        

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class ExcelScheduleCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ExcelScheduleCommand";

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

        // =========================
        // EXPORT
        // =========================
        private void ExportSchedule(Document doc)
        {
            // 1) Sélection de la nomenclature
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsTitleblockRevisionSchedule)
                .OrderBy(vs => vs.Name)
                .ToList();

            if (!schedules.Any())
            {
                TaskDialog.Show("Info", "Aucune nomenclature trouvée.");
                return;
            }

            var combo = new WF.ComboBox { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 350, Left = 10, Top = 10 };
            schedules.ForEach(vs => combo.Items.Add(vs.Name));
            combo.SelectedIndex = 0;

            using (var form = new WF.Form
            {
                Text = "Sélectionnez une nomenclature",
                Width = 420,
                Height = 120,
                StartPosition = WF.FormStartPosition.CenterScreen
            })
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

            // 2) S’assurer que le champ ID est présent (ajout temporaire si besoin)
            bool addedIdTemporarily = false;
            var schedFields = def.GetSchedulableFields();
            var idSchedulable = schedFields.FirstOrDefault(sf => sf.ParameterId.IntegerValue == (int)BuiltInParameter.ID_PARAM);
            bool hasIdField = def.GetFieldOrder().Any(fid => def.GetField(fid).ParameterId.IntegerValue == (int)BuiltInParameter.ID_PARAM);

            if (idSchedulable != null && !hasIdField)
            {
                using (var t = new Transaction(doc, "Ajout champ ElementId (temporaire)"))
                {
                    t.Start();
                    def.AddField(idSchedulable);
                    t.Commit();
                    addedIdTemporarily = true;
                }
                doc.Regenerate();
                vsel = doc.GetElement(vsel.Id) as ViewSchedule;
                def = vsel.Definition;
            }

            // 3) Récupérer les éléments affichés dans la nomenclature
            var items = new FilteredElementCollector(doc, vsel.Id)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            // Respecter le tri de la nomenclature (sans créer de lignes de regroupement)
            var sortFields = def.GetSortGroupFields().ToList();
            if (sortFields.Any())
            {
                IOrderedEnumerable<Element> ordered = null;
                foreach (var sf in sortFields)
                {
                    var fieldDef = def.GetField(sf.FieldId);
                    var pid = fieldDef.ParameterId;

                    Func<Element, IComparable> keySelector = el =>
                    {
                        var p = GetElementParameter(el, doc, pid);
                        if (p == null) return ""; // place les nulls à la fin
                        switch (p.StorageType)
                        {
                            case StorageType.Double:
                                try
                                {
                                    var d = p.AsDouble();
                                    var display = UnitUtils.ConvertFromInternalUnits(d, p.GetUnitTypeId());
                                    return display;
                                }
                                catch { return p.AsDouble(); }
                            case StorageType.Integer:
                                return p.AsInteger();
                            case StorageType.String:
                                return p.AsString() ?? p.AsValueString() ?? "";
                            case StorageType.ElementId:
                                return p.AsElementId().IntegerValue;
                            default:
                                return p.AsValueString() ?? "";
                        }
                    };

                    bool asc = sf.SortOrder == ScheduleSortOrder.Ascending;
                    ordered = ordered == null
                        ? (asc ? items.OrderBy(keySelector) : items.OrderByDescending(keySelector))
                        : (asc ? ordered.ThenBy(keySelector) : ordered.ThenByDescending(keySelector));
                }
                if (ordered != null) items = ordered.ToList();
            }

            // 4) Préparer la liste des champs exportés (ordre d’affichage)
            var fieldOrder = def.GetFieldOrder().ToList();
            var fields = fieldOrder.Select(fid => def.GetField(fid)).ToList();

            // 5) Fichier cible + licence EPPlus
            ConfigureEpplusLicense();

            var exportDir = GetExportFolder();
            var safe = MakeSafeFileName(vsel.Name);
            var fileName = $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            var fullPath = Path.Combine(exportDir, fileName);

            if (File.Exists(fullPath)) File.Delete(fullPath);

            using (var pkg = new ExcelPackage(new FileInfo(fullPath)))
            {
                var ws = pkg.Workbook.Worksheets.Add(CleanSheetName(vsel.Name));
                ws.Cells.Style.Font.Name = "Calibri";
                ws.Cells.Style.Font.Size = 11;

                int col = 1;

                // Colonne 1 : ElementId (masquée)
                ws.Cells[1, col].Value = "Element Id";
                StyleHeader(ws.Cells[1, col], Color.LightGray);
                ws.Column(col).Hidden = true;
                int elementIdCol = col;
                col++;

                // Colonnes des champs (on masque si la colonne est cachée côté nomenclature)
                var hiddenCols = new HashSet<int>();
                var columnParamIds = new List<ElementId>(); // mapping colonne -> paramId

                // Pour déterminer modifiable ou non, on prendra le premier élément (s’il existe)
                Element firstWithParam = items.FirstOrDefault();

                foreach (var fld in fields)
                {
                    if (fld.IsCalculatedField) continue; // pas d’export/import pour champs calculés

                    var header = string.IsNullOrWhiteSpace(fld.ColumnHeading)
                        ? fld.GetName()
                        : fld.ColumnHeading;

                    ws.Cells[1, col].Value = header;

                    bool modifiable = false;
                    Parameter sampleParam = null;
                    if (firstWithParam != null)
                        sampleParam = GetElementParameter(firstWithParam, doc, fld.ParameterId);

                    if (sampleParam != null && !sampleParam.IsReadOnly) modifiable = true;
                    StyleHeader(ws.Cells[1, col], modifiable ? Color.LightGreen : Color.LightCoral);

                    if (fld.IsHidden)
                    {
                        ws.Column(col).Hidden = true;
                        hiddenCols.Add(col);
                    }

                    columnParamIds.Add(fld.ParameterId);
                    col++;
                }

                int totalCols = col - 1;

                // 6) Lignes : 1 élément = 1 ligne
                int row = 2;
                foreach (var el in items)
                {
                    int c = 1;

                    // Element Id
                    ws.Cells[row, c++].Value = el.Id.IntegerValue;

                    // Valeurs des paramètres
                    foreach (var pid in columnParamIds)
                    {
                        var p = GetElementParameter(el, doc, pid);
                        var cell = ws.Cells[row, c];

                        if (p == null || !p.HasValue)
                        {
                            cell.Value = null;
                            c++;
                            continue;
                        }

                        switch (p.StorageType)
                        {
                            case StorageType.Double:
                                try
                                {
                                    double internalVal = p.AsDouble();
                                    double displayVal = UnitUtils.ConvertFromInternalUnits(internalVal, p.GetUnitTypeId());
                                    cell.Value = displayVal; // numérique propre
                                }
                                catch
                                {
                                    cell.Value = p.AsValueString(); // fallback (texte)
                                }
                                break;

                            case StorageType.Integer:
                                cell.Value = p.AsInteger();
                                break;

                            case StorageType.String:
                                cell.Value = p.AsString() ?? p.AsValueString();
                                break;

                            case StorageType.ElementId:
                                cell.Value = p.AsElementId()?.IntegerValue ?? 0;
                                break;

                            default:
                                cell.Value = p.AsValueString();
                                break;
                        }

                        c++;
                    }

                    // zébrage léger
                    var stripe = (row % 2 == 0) ? Color.White : Color.FromArgb(242, 242, 242);
                    ws.Cells[row, 1, row, totalCols]
                        .Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[row, 1, row, totalCols]
                        .Style.Fill.BackgroundColor.SetColor(stripe);

                    row++;
                }

                // Table + mise en forme
                ws.View.FreezePanes(2, 2); // fige en-têtes + id
                var tblRange = ws.Cells[1, 1, row - 1, totalCols];
                var tblName = MakeTableName(vsel.Name);
                var table = ws.Tables.Add(tblRange, tblName);
                table.ShowHeader = true;
                table.TableStyle = TableStyles.Light1;

                ws.Cells[ws.Dimension.Address].AutoFitColumns();

                pkg.Save();
            }

            // 7) Nettoyage du champ ID si ajouté temporairement
            RemoveTempIdFieldIfNeeded(doc, addedIdTemporarily, def);

            // 8) Ouvrir le fichier
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { /* silencieux */ }

            TaskDialog.Show("Terminé", $"Export créé : {fullPath}");
        }

        // =========================
        // IMPORT
        // =========================
        private void ImportSchedule(UIApplication uiApp, Document doc)
        {
            var ofd = new WF.OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx" };
            if (ofd.ShowDialog() != WF.DialogResult.OK) return;

            ConfigureEpplusLicense();

            using (var pkg = new ExcelPackage(new FileInfo(ofd.FileName)))
            {
                var ws = pkg.Workbook.Worksheets.First();
                if (ws.Dimension == null)
                {
                    TaskDialog.Show("Erreur", "Feuille Excel vide.");
                    return;
                }

                int rows = ws.Dimension.End.Row;
                int cols = ws.Dimension.End.Column;

                // En-têtes
                var headers = new string[cols + 1]; // 1-based
                for (int c = 1; c <= cols; c++)
                    headers[c] = ws.Cells[1, c].Text;

                // Vérifier la colonne 1 = Element Id
                if (!string.Equals(headers[1], "Element Id", StringComparison.OrdinalIgnoreCase))
                {
                    TaskDialog.Show("Erreur", "La première colonne doit être 'Element Id'.");
                    return;
                }

                using (var t = new Transaction(doc, "Import Excel → Revit"))
                {
                    t.Start();

                    for (int r = 2; r <= rows; r++)
                    {
                        var idText = ws.Cells[r, 1].Text?.Trim();
                        if (!int.TryParse(idText, out int id)) continue;

                        var e = doc.GetElement(new ElementId(id));
                        if (e == null) continue;

                        for (int c = 2; c <= cols; c++)
                        {
                            // Ignorer les colonnes masquées dans Excel (paramètres cachés + colonnes que tu veux ignorer)
                            if (ws.Column(c).Hidden) continue;

                            var header = headers[c];
                            if (string.IsNullOrWhiteSpace(header)) continue;

                            var param = e.LookupParameter(header);
                            if (param == null || param.IsReadOnly) continue;

                            var cell = ws.Cells[r, c];
                            var txt = cell.Text;

                            // Pas d’écriture si cellule vide
                            if (string.IsNullOrWhiteSpace(txt) && cell.Value == null) continue;

                            try
                            {
                                switch (param.StorageType)
                                {
                                    case StorageType.Double:
                                        if (cell.Value is double dval)
                                        {
                                            double internalVal = UnitUtils.ConvertToInternalUnits(dval, param.GetUnitTypeId());
                                            param.Set(internalVal);
                                        }
                                        else if (double.TryParse(txt, out double d2))
                                        {
                                            double internalVal = UnitUtils.ConvertToInternalUnits(d2, param.GetUnitTypeId());
                                            param.Set(internalVal);
                                        }
                                        break;

                                    case StorageType.Integer:
                                        if (int.TryParse(txt, out var ival))
                                            param.Set(ival);
                                        break;

                                    case StorageType.String:
                                        param.Set(txt ?? cell.Value?.ToString() ?? "");
                                        break;

                                    case StorageType.ElementId:
                                        if (int.TryParse(txt, out var ei))
                                            param.Set(new ElementId(ei));
                                        break;
                                }
                            }
                            catch
                            {
                                // ignore ponctuellement la cellule fautive (units/formatage)
                            }
                        }
                    }

                    t.Commit();
                }
            }

            TaskDialog.Show("Import terminé", "Les données ont été importées dans Revit (colonnes masquées ignorées).");
        }

        // =========================
        // HELPERS
        // =========================
        private static void ConfigureEpplusLicense()
        {
            // EPPlus 8+
            try
            {
                // ATTENTION : NonCommercial = usage non professionnel uniquement.
                ExcelPackage.License.SetNonCommercialPersonal(Environment.UserName);
                return;
            }
            catch
            {
                // EPPlus ≤7 fallback
                try { ExcelPackage.LicenseContext = LicenseContext.NonCommercial; } catch { }
            }
        }

        private static string GetExportFolder()
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = Path.Combine(docs, "RevitLogs", "Export Nomenclatures", "Excel");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string MakeSafeFileName(string baseName)
        {
            var name = Regex.Replace(baseName, @"[\\\/\:\*\?\""<>\|\p{C}]+", "_");
            name = string.IsNullOrWhiteSpace(name) ? "Nomenclature" : name;
            return name.Length > 100 ? name.Substring(0, 100) : name;
        }

        private static void StyleHeader(ExcelRange cell, Color bg)
        {
            cell.Style.Font.Bold = true;
            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(bg);
            cell.Style.Font.Color.SetColor(Color.Black);
        }

        private static string CleanSheetName(string name)
        {
            var cleaned = Regex.Replace(name, @"[\\\/\?\*\[\]\:]", "_");
            return string.IsNullOrWhiteSpace(cleaned) ? "Feuille" : cleaned.Substring(0, Math.Min(31, cleaned.Length));
        }

        private static string MakeTableName(string baseName)
        {
            var t = Regex.Replace(baseName, "\\W+", "_");
            if (string.IsNullOrEmpty(t) || !char.IsLetter(t[0])) t = "tbl_" + t;
            return t.Length > 64 ? t.Substring(0, 64) : t;
        }

        private static void RemoveTempIdFieldIfNeeded(Document doc, bool added, ScheduleDefinition def)
        {
            if (!added) return;
            using (var t = new Transaction(doc, "Retrait champ ElementId (temporaire)"))
            {
                t.Start();
                var order = def.GetFieldOrder().ToList();
                foreach (var fid in order)
                {
                    var f = def.GetField(fid);
                    if (f?.ParameterId.IntegerValue == (int)BuiltInParameter.ID_PARAM)
                    {
                        def.RemoveField(fid);
                        break;
                    }
                }
                t.Commit();
            }
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

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            throw new NotImplementedException();
        }
    }
}

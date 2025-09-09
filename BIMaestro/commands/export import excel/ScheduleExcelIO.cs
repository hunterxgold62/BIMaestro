// ScheduleExcelIO.cs
// Revit 2023 & 2025 – Export/Import Nomenclature <-> Excel (NPOI 2.6.2)
// v4.4
// - Double: rétablit SetValueString(text) en premier, puis TryParseWithUnits, puis fallback conversion/numérique
// - String: garde Set(text) prioritaire (ex. Numéro de feuille), fallback SetValueString
// - Excel: zébrage ET lignes normales au format Texte "@"
// - Compat enums 2023/2025 via EnumCompat (BuiltInParameter Int32/Int64)
// - Commande unique (export/import), import = message "Import terminé ✅"

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ScheduleIO
{
    // ---------- Compatibilité enums Revit 2023/2025 ----------
    internal static class EnumCompat
    {
        private static readonly Type BipType = typeof(BuiltInParameter);
        private static readonly Type BipUnderlying = Enum.GetUnderlyingType(typeof(BuiltInParameter));

        public static bool IsDefinedBip(int rawId)
        {
            object boxed = Convert.ChangeType(rawId, BipUnderlying);
            return Enum.IsDefined(BipType, boxed);
        }

        public static BuiltInParameter ToBip(int rawId)
        {
            object boxed = Convert.ChangeType(rawId, BipUnderlying);
            object enumObj = Enum.ToObject(BipType, boxed);
            return (BuiltInParameter)enumObj;
        }

        public static ElementId ToElementId(BuiltInParameter bip)
        {
            long l = Convert.ToInt64((object)bip);
            return new ElementId(unchecked((int)l)); // ElementId reste Int32
        }
    }

    internal static class PathUtil
    {
        public static string EnsureExportDir()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "Exports");
            Directory.CreateDirectory(root);
            return root;
        }
        public static string EnsureTempDir()
        {
            var root = Path.Combine(EnsureExportDir(), "_tmp");
            Directory.CreateDirectory(root);
            return root;
        }
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Export";
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name;
        }
    }

    internal class ColumnMap
    {
        public string Header { get; set; }
        public string OriginalName { get; set; }
        public string ColumnHeading { get; set; }
        public ElementId ParameterId { get; set; }
        public bool IsCalculatedOrCombined { get; set; }
        public bool IsHidden { get; set; }
        public bool IsWritable { get; set; }
        public int EditableSampleCount { get; set; }
        public int SampleCount { get; set; }
        public StorageType Storage { get; set; } = StorageType.None;
        public string SpecTypeId { get; set; }
        public bool IsYesNo { get; set; }
    }

    internal static class ParamUtils
    {
        public static Parameter GetParameterById(Element e, Document doc, ColumnMap c, bool allowByName)
        {
            Parameter TryOn(Element target)
            {
                if (target == null) return null;

                if (c?.ParameterId != null && c.ParameterId != ElementId.InvalidElementId)
                {
                    int rawId = c.ParameterId.IntegerValue;

                    if (EnumCompat.IsDefinedBip(rawId))
                    {
                        var bip = EnumCompat.ToBip(rawId);
                        try { var p = target.get_Parameter(bip); if (p != null) return p; } catch { }
                    }
                    try
                    {
                        var pe = doc.GetElement(c.ParameterId) as ParameterElement;
                        if (pe != null)
                        {
                            var def = pe.GetDefinition();
                            var p = target.get_Parameter(def);
                            if (p != null) return p;
                        }
                    }
                    catch { }
                }
                if (allowByName && !string.IsNullOrWhiteSpace(c?.OriginalName))
                {
                    try { var p = target.LookupParameter(c.OriginalName); if (p != null) return p; } catch { }
                }
                return null;
            }

            var pInst = TryOn(e);
            if (pInst != null) return pInst;

            Element typeElem = null;
            try { var tid = e.GetTypeId(); if (tid != null && tid != ElementId.InvalidElementId) typeElem = e.Document.GetElement(tid); } catch { }
            var pType = TryOn(typeElem);
            return pType;
        }

        public static string AsReadable(Document doc, Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString() ?? string.Empty;
                    case StorageType.Integer: return p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double: return p.AsValueString() ?? p.AsDouble().ToString("G", CultureInfo.InvariantCulture);
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId) return string.Empty;
                        var el = doc.GetElement(id);
                        return el?.Name ?? id.IntegerValue.ToString(CultureInfo.InvariantCulture);
                    default: return string.Empty;
                }
            }
            catch { return string.Empty; }
        }

        public static bool TryParseWithUnits(Document doc, Parameter p, string text, out double val)
        {
            val = 0;
            var units = doc.GetUnits();
            var specId = p.Definition?.GetDataType();
            if (specId == null) return false;

            var ufType = typeof(UnitFormatUtils);
            var m5 = ufType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                           .FirstOrDefault(m => m.Name == "TryParse" && m.GetParameters().Length == 5);
            if (m5 != null)
            {
                var args5 = new object[] { units, specId, text, null, (double)0 };
                if ((bool)m5.Invoke(null, args5)) { val = (double)args5[4]; return true; }
            }
            var m4 = ufType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                           .FirstOrDefault(m => m.Name == "TryParse" && m.GetParameters().Length == 4);
            if (m4 != null)
            {
                var args4 = new object[] { units, specId, text, (double)0 };
                if ((bool)m4.Invoke(null, args4)) { val = (double)args4[3]; return true; }
            }
            return false;
        }
    }

    internal static class NpoiUtils
    {
        public static IWorkbook CreateWorkbook() => new XSSFWorkbook();

        public static IFont CreateFont(IWorkbook wb, bool bold = false, short size = 11)
        {
            var f = wb.CreateFont();
            f.FontName = "Calibri";
            f.FontHeightInPoints = size;
            f.IsBold = bold;
            return f;
        }

        public static ICellStyle CreateTextStyle(IWorkbook wb)
        {
            var df = wb.CreateDataFormat();
            var st = wb.CreateCellStyle();
            st.DataFormat = df.GetFormat("@"); // Texte
            st.VerticalAlignment = VerticalAlignment.Center;
            st.SetFont(CreateFont(wb, false, 11));
            return st;
        }

        public static ICellStyle CreateHeaderStyle(IWorkbook wb, byte r, byte g, byte b)
        {
            var st = wb.CreateCellStyle();
            st.Alignment = HorizontalAlignment.Center;
            st.VerticalAlignment = VerticalAlignment.Center;
            st.BorderBottom = BorderStyle.Thin;

            if (st is XSSFCellStyle xs)
            {
                xs.SetFillForegroundColor(new XSSFColor(new byte[] { r, g, b }, null));
                xs.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
            }

            st.SetFont(CreateFont(wb, true, 11));
            return st;
        }

        public static ICellStyle CreateZebraStyle(IWorkbook wb, byte r, byte g, byte b)
        {
            var df = wb.CreateDataFormat();
            var st = wb.CreateCellStyle();
            st.VerticalAlignment = VerticalAlignment.Center;
            st.SetFont(CreateFont(wb, false, 11));
            if (st is XSSFCellStyle xs)
            {
                xs.SetFillForegroundColor(new XSSFColor(new byte[] { r, g, b }, null));
                xs.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
            }
            st.DataFormat = df.GetFormat("@"); // Texte aussi
            return st;
        }
    }

    // ==========================================================
    // COMMANDE UNIQUE : export ou import
    // ==========================================================
    [Transaction(TransactionMode.Manual)]
    public class ScheduleExcelIOCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var td = new TaskDialog("Nomenclature ↔ Excel");
            td.MainInstruction = "Que veux-tu faire ?";
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Exporter la nomenclature vers Excel");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Importer les modifications depuis Excel");
            td.CommonButtons = TaskDialogCommonButtons.Cancel;
            var choice = td.Show();

            if (choice == TaskDialogResult.CommandLink1) return DoExport(data, ref message, elements);
            if (choice == TaskDialogResult.CommandLink2) return DoImport(data, ref message, elements);
            return Result.Cancelled;
        }

        // ------------------------- EXPORT -------------------------
        private Result DoExport(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var ui = data.Application.ActiveUIDocument;
                var doc = ui.Document;

                var schedule = doc.ActiveView as ViewSchedule;
                if (schedule == null)
                {
                    TaskDialog.Show("Export Excel", "Active une nomenclature avant de lancer l'export.");
                    return Result.Failed;
                }
                if (schedule.Definition == null || schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule)
                {
                    TaskDialog.Show("Export Excel", "Cette vue de nomenclature n'est pas exportable.");
                    return Result.Failed;
                }
                if (schedule.Definition.IsKeySchedule)
                {
                    TaskDialog.Show("Export Excel", "Les nomenclatures de clés ne sont pas gérées pour l’instant.");
                    return Result.Failed;
                }

                var def = schedule.Definition;

                // Colonnes (ordre d’affichage) + entêtes “affichées”
                var fieldOrder = def.GetFieldOrder();
                var cols = new List<ColumnMap>();
                foreach (var fieldIdx in fieldOrder)
                {
                    var field = def.GetField(fieldIdx);
                    var isCalc = field.IsCalculatedField || field.IsCombinedParameterField;
                    var pid = field.HasSchedulableField ? field.ParameterId : ElementId.InvalidElementId;

                    string original = field.GetName();
                    string heading = original;
                    try
                    {
                        var t = field.GetType();
                        var propColHead = t.GetProperty("ColumnHeading");
                        if (propColHead != null) heading = propColHead.GetValue(field, null) as string ?? original;
                    }
                    catch { }

                    string header = heading;
                    if (!string.Equals(heading, original, StringComparison.Ordinal))
                        header = $"{heading} ({original})";

                    cols.Add(new ColumnMap
                    {
                        Header = header,
                        OriginalName = original,
                        ColumnHeading = heading,
                        ParameterId = pid,
                        IsCalculatedOrCombined = isCalc,
                        IsHidden = field.IsHidden
                    });
                }

                // Éléments visibles
                var inst = new FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType().ToElements().ToList();
                var types = new FilteredElementCollector(doc, schedule.Id).WhereElementIsElementType().ToElements().ToList();
                var all = inst.Concat(types).Distinct(new ElementIdComparer()).ToList();

                // Filtres + tri
                ApplyScheduleFiltersWithElementFilters(doc, schedule, def, all);
                ApplyScheduleSortOrder(doc, def, all);

                // Typage + sondage d’éditabilité
                ProbeTypesForColumns(doc, all.FirstOrDefault(), cols);
                AssessEditability(doc, all, cols);

                // Données
                var rowsEdition = new List<Dictionary<string, string>>();
                foreach (var e in all)
                {
                    var line = new Dictionary<string, string>
                    {
                        ["UniqueId"] = e.UniqueId,
                        ["ElementId"] = e.Id.IntegerValue.ToString(CultureInfo.InvariantCulture)
                    };

                    foreach (var c in cols)
                    {
                        string val = "";
                        try
                        {
                            var p = ParamUtils.GetParameterById(e, doc, c, allowByName: true);
                            if (p != null) val = ParamUtils.AsReadable(doc, p);
                        }
                        catch { }
                        line[c.Header] = val;
                    }
                    rowsEdition.Add(line);
                }

                // Excel
                var exportDir = PathUtil.EnsureExportDir();
                var fileName = PathUtil.SanitizeFileName(schedule.Name) + ".xlsx";
                var path = Path.Combine(exportDir, fileName);

                IWorkbook wb = NpoiUtils.CreateWorkbook();
                CreateEditionSheetModern(wb, cols, rowsEdition);
                WriteMetaSheet(wb, cols);

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    wb.Write(fs);

                var dlg = new TaskDialog("Export Excel");
                dlg.MainInstruction = "Export terminé";
                dlg.MainContent = path;
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Ouvrir le fichier Excel");
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Ouvrir le dossier");
                dlg.CommonButtons = TaskDialogCommonButtons.Close;
                var r = dlg.Show();

                try
                {
                    if (r == TaskDialogResult.CommandLink1)
                        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    else if (r == TaskDialogResult.CommandLink2)
                        Process.Start(new ProcessStartInfo { FileName = exportDir, UseShellExecute = true });
                }
                catch { /* pas d’Excel ? on ignore */ }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ------------------------- IMPORT -------------------------
        private Result DoImport(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var ui = data.Application.ActiveUIDocument;
                var doc = ui.Document;

                var schedule = doc.ActiveView as ViewSchedule;
                if (schedule == null)
                {
                    TaskDialog.Show("Import Excel", "Active une nomenclature pour cibler l’import.");
                    return Result.Failed;
                }

                var exportDir = PathUtil.EnsureExportDir();
                var guess = Directory.GetFiles(exportDir, PathUtil.SanitizeFileName(schedule.Name) + ".xlsx").FirstOrDefault();
                if (guess == null)
                {
                    TaskDialog.Show("Import Excel", $"Fichier introuvable : {PathUtil.SanitizeFileName(schedule.Name)}.xlsx\nDans: {exportDir}\nLance d'abord l'export.");
                    return Result.Failed;
                }

                Dictionary<string, ColumnMap> mapByHeader;
                List<Dictionary<string, string>> dataRows;

                using (var fs = new FileStream(guess, FileMode.Open, FileAccess.Read))
                {
                    IWorkbook wb = new XSSFWorkbook(fs);
                    var ws = wb.GetSheet("Edition");
                    if (ws == null)
                    {
                        TaskDialog.Show("Import Excel", "Onglet 'Edition' introuvable.");
                        return Result.Failed;
                    }

                    var meta = wb.GetSheet("Meta");
                    mapByHeader = (meta != null) ? ReadMeta(meta) : BuildHeaderMapFromActiveSchedule(((ViewSchedule)ui.Document.ActiveView).Definition);
                    dataRows = ReadSheetToRows(ws);
                }

                if (!dataRows.Any() || !dataRows[0].ContainsKey("UniqueId"))
                {
                    TaskDialog.Show("Import Excel", "Fichier invalide : aucune donnée ou colonne 'UniqueId' manquante.");
                    return Result.Failed;
                }

                using (var t = new Transaction(doc, "Import paramètres depuis Excel"))
                {
                    t.Start();

                    foreach (var row in dataRows)
                    {
                        if (!row.TryGetValue("UniqueId", out string uid) || string.IsNullOrWhiteSpace(uid))
                            continue;

                        var e = doc.GetElement(uid);
                        if (e == null) continue;

                        foreach (var kvp in row)
                        {
                            var header = kvp.Key;
                            if (header.Equals("UniqueId", StringComparison.OrdinalIgnoreCase)) continue;
                            if (header.Equals("ElementId", StringComparison.OrdinalIgnoreCase)) continue;

                            if (!mapByHeader.TryGetValue(header, out var cm)) continue;
                            if (cm.ParameterId == ElementId.InvalidElementId) continue;

                            var p = ParamUtils.GetParameterById(e, doc, cm, allowByName: true);
                            if (p == null || p.IsReadOnly) continue;

                            var newText = kvp.Value ?? "";

                            try
                            {
                                if (!string.IsNullOrWhiteSpace(newText))
                                {
                                    if (p.StorageType == StorageType.String)
                                    {
                                        // PRIO String : Set direct, fallback SetValueString
                                        try { p.Set(newText); }
                                        catch { p.SetValueString(newText); }
                                    }
                                    else if (p.StorageType == StorageType.Integer)
                                    {
                                        if (TryParseYesNo(newText, out int yn)) p.Set(yn);
                                        else if (int.TryParse(CleanNumeric(newText, true), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) p.Set(iv);
                                    }
                                    else if (p.StorageType == StorageType.Double)
                                    {
                                        // *** PRIO Double : SetValueString d'abord (tolérant, gère "0,2 m", "4 000 kg", etc.)
                                        bool applied = false;
                                        try
                                        {
                                            p.SetValueString(newText);
                                            applied = true;
                                        }
                                        catch { /* on tente les fallbacks */ }

                                        if (!applied)
                                        {
                                            if (ParamUtils.TryParseWithUnits(doc, p, newText, out double v1))
                                            {
                                                p.Set(v1);
                                                applied = true;
                                            }
                                            else
                                            {
                                                var num = CleanNumeric(newText, false);
                                                if (double.TryParse(num, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double displayVal))
                                                {
                                                    var specId = p.Definition?.GetDataType();
                                                    if (specId != null)
                                                    {
                                                        try
                                                        {
                                                            var fo = doc.GetUnits().GetFormatOptions(specId);
                                                            var unitId = fo.GetUnitTypeId();
                                                            var internalVal = UnitUtils.ConvertToInternalUnits(displayVal, unitId);
                                                            p.Set(internalVal);
                                                            applied = true;
                                                        }
                                                        catch
                                                        {
                                                            p.Set(displayVal);
                                                            applied = true;
                                                        }
                                                    }
                                                    else
                                                    {
                                                        p.Set(displayVal);
                                                        applied = true;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // ElementId / autres : non géré pour import direct
                                    }
                                }
                            }
                            catch
                            {
                                // on ignore pour ne pas bloquer l'import global
                            }
                        }
                    }

                    t.Commit();
                }

                TaskDialog.Show("Import Excel", "Import terminé ✅");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ==========================================================
        // Sous-fonctions communes
        // ==========================================================
        private static (ISheet, ICellStyle) CreateEditionSheetModern(IWorkbook wb, List<ColumnMap> cols, List<Dictionary<string, string>> rowsEdition)
        {
            var sheet = wb.CreateSheet("Edition");
            var text = NpoiUtils.CreateTextStyle(wb);
            var headerGreen = NpoiUtils.CreateHeaderStyle(wb, 216, 245, 209);
            var headerRed = NpoiUtils.CreateHeaderStyle(wb, 255, 214, 214);
            var zebra = NpoiUtils.CreateZebraStyle(wb, 248, 249, 253);

            var headerRow = sheet.CreateRow(0); headerRow.HeightInPoints = 20f;

            var headers = new List<string> { "UniqueId", "ElementId" };
            headers.AddRange(cols.Select(c => c.Header));

            for (int j = 0; j < headers.Count; j++)
            {
                var cell = headerRow.CreateCell(j);
                cell.SetCellValue(headers[j]);
                var cm = cols.FirstOrDefault(c => c.Header.Equals(headers[j], StringComparison.OrdinalIgnoreCase));
                cell.CellStyle = (j >= 2 && cm != null && cm.IsWritable) ? headerGreen : headerRed;
            }

            for (int i = 0; i < rowsEdition.Count; i++)
            {
                var r = sheet.CreateRow(i + 1);
                r.HeightInPoints = 17f;
                var dict = rowsEdition[i];
                for (int j = 0; j < headers.Count; j++)
                {
                    dict.TryGetValue(headers[j], out string val);
                    var c = r.CreateCell(j);
                    c.SetCellValue((val ?? "").StartsWith("=") ? "'" + val : (val ?? ""));
                    c.CellStyle = (i % 2 == 1) ? zebra : text; // 2 styles au format Texte "@"
                }
            }

            sheet.SetColumnHidden(0, true);
            sheet.SetColumnHidden(1, true);
            sheet.CreateFreezePane(0, 1);

            int max = Math.Min(headers.Count, 30);
            for (int c = 0; c < max; c++) { try { sheet.AutoSizeColumn(c); } catch { } }

            return (sheet, text);
        }

        private static void WriteMetaSheet(IWorkbook wb, List<ColumnMap> cols)
        {
            var meta = wb.CreateSheet("Meta");
            var mh = meta.CreateRow(0);
            var metaHeaders = new[]
            {
                "Header","OriginalName","ColumnHeading",
                "ParameterIdInt","IsCalculatedOrCombined","IsHidden",
                "StorageType","SpecTypeId",
                "EditableAll","EditableSampleCount","SampleCount"
            };
            for (int i = 0; i < metaHeaders.Length; i++) mh.CreateCell(i).SetCellValue(metaHeaders[i]);

            int mrow = 1;
            foreach (var c in cols)
            {
                var rr = meta.CreateRow(mrow++);
                rr.CreateCell(0).SetCellValue(c.Header);
                rr.CreateCell(1).SetCellValue(c.OriginalName ?? "");
                rr.CreateCell(2).SetCellValue(c.ColumnHeading ?? "");
                rr.CreateCell(3).SetCellValue(c.ParameterId.IntegerValue);
                rr.CreateCell(4).SetCellValue(c.IsCalculatedOrCombined);
                rr.CreateCell(5).SetCellValue(c.IsHidden);
                rr.CreateCell(6).SetCellValue(c.Storage.ToString());
                rr.CreateCell(7).SetCellValue(c.SpecTypeId ?? "");
                rr.CreateCell(8).SetCellValue(c.IsWritable);
                rr.CreateCell(9).SetCellValue(c.EditableSampleCount);
                rr.CreateCell(10).SetCellValue(c.SampleCount);
            }

            int max = Math.Min(11, 30);
            for (int c = 0; c < max; c++) { try { meta.AutoSizeColumn(c); } catch { } }
        }

        private static void AssessEditability(Document doc, List<Element> elements, List<ColumnMap> cols, int sampleMax = 30)
        {
            var sample = elements.Take(sampleMax).ToList();
            using (var t = new Transaction(doc, "Probe editability (no-op)"))
            {
                t.Start();

                foreach (var c in cols)
                {
                    int ok = 0, tot = 0;

                    foreach (var e in sample)
                    {
                        var p = ParamUtils.GetParameterById(e, doc, c, allowByName: true);
                        if (p == null) continue;
                        tot++;

                        if (p.IsReadOnly) continue;

                        try
                        {
                            bool res = false;
                            switch (p.StorageType)
                            {
                                case StorageType.String: res = p.Set(p.AsString() ?? ""); break;
                                case StorageType.Integer: res = p.Set(p.AsInteger()); break;
                                case StorageType.Double: res = p.Set(p.AsDouble()); break;
                                case StorageType.ElementId: res = false; break;
                            }
                            if (res) ok++;
                        }
                        catch { }
                    }

                    c.EditableSampleCount = ok;
                    c.SampleCount = tot;
                    c.IsWritable = (tot > 0 && ok == tot);
                }

                t.RollBack();
            }
        }

        private static void ProbeTypesForColumns(Document doc, Element probe, List<ColumnMap> cols)
        {
            if (probe == null) return;
            foreach (var c in cols)
            {
                try
                {
                    var p = ParamUtils.GetParameterById(probe, doc, c, allowByName: false);
                    if (p == null) continue;
                    c.Storage = p.StorageType;
                    try
                    {
                        var dt = p.Definition?.GetDataType();
                        if (dt != null && dt.Equals(SpecTypeId.Boolean.YesNo)) c.IsYesNo = true;
                        c.SpecTypeId = dt?.TypeId;
                    }
                    catch { }
                }
                catch { }
            }
        }

        // ---------- Filtres ----------
        private static void ApplyScheduleFiltersWithElementFilters(Document doc, ViewSchedule schedule, ScheduleDefinition def, List<Element> elements)
        {
            int count = def.GetFilterCount();
            if (count == 0) return;

            var efList = new List<ElementFilter>();
            var manualChecks = new List<Func<Element, bool>>();

            for (int i = 0; i < count; i++)
            {
                var f = def.GetFilter(i);
                var fld = def.GetField(f.FieldId);

                var pidEff = GetEffectivePidForFilter(fld, f);

                if (f.FilterType == ScheduleFilterType.HasParameter)
                {
                    manualChecks.Add(e => ParamExists(e, doc, pidEff, fld.GetName()));
                    continue;
                }
                if (f.FilterType == ScheduleFilterType.IsAssociatedWithGlobalParameter ||
                    f.FilterType == ScheduleFilterType.IsNotAssociatedWithGlobalParameter)
                {
                    manualChecks.Add(e =>
                    {
                        var p = new ColumnMap { ParameterId = pidEff, OriginalName = fld.GetName() };
                        var par = ParamUtils.GetParameterById(e, doc, p, allowByName: false);
                        bool associated = par != null && par.GetAssociatedGlobalParameter() != null &&
                                          par.GetAssociatedGlobalParameter() != ElementId.InvalidElementId;
                        return f.FilterType == ScheduleFilterType.IsAssociatedWithGlobalParameter ? associated : !associated;
                    });
                    continue;
                }

                var pvp = new ParameterValueProvider(pidEff);

                if (f.IsStringValue)
                {
                    string val = f.GetStringValue() ?? string.Empty;
                    var eval = GetStringEvaluator(f.FilterType, out bool invert);
                    if (eval != null)
                    {
                        var rule = new FilterStringRule(pvp, eval, val);
                        efList.Add(new ElementParameterFilter(new List<FilterRule> { rule }, invert));
                    }
                    continue;
                }

                if (f.IsIntegerValue)
                {
                    int val = f.GetIntegerValue();
                    var eval = GetNumericEvaluator(f.FilterType, out bool invert);
                    if (eval != null)
                    {
                        var rule = new FilterIntegerRule(pvp, eval, val);
                        efList.Add(new ElementParameterFilter(new List<FilterRule> { rule }, invert));
                    }
                    continue;
                }

                if (f.IsDoubleValue)
                {
                    double val = f.GetDoubleValue();
                    var eval = GetNumericEvaluator(f.FilterType, out bool invert);
                    if (eval != null)
                    {
                        var rule = new FilterDoubleRule(pvp, eval, val, 1e-09);
                        efList.Add(new ElementParameterFilter(new List<FilterRule> { rule }, invert));
                    }
                    continue;
                }

                if (f.IsElementIdValue)
                {
                    var val = f.GetElementIdValue();
                    var eval = GetNumericEvaluator(f.FilterType, out bool invert);
                    if (eval != null)
                    {
                        var rule = new FilterElementIdRule(pvp, eval, val);
                        efList.Add(new ElementParameterFilter(new List<FilterRule> { rule }, invert));
                    }
                    continue;
                }
            }

            if (efList.Count > 0)
            {
                ElementFilter combined = efList.Count == 1 ? efList[0] : (ElementFilter)new LogicalAndFilter(efList);
                var ids = new HashSet<ElementId>(
                    new FilteredElementCollector(doc, schedule.Id).WherePasses(combined).ToElementIds()
                );
                elements.RemoveAll(e => !ids.Contains(e.Id));
            }

            if (manualChecks.Count > 0)
                elements.RemoveAll(e => !manualChecks.All(chk => chk(e)));
        }

        private static ElementId GetEffectivePidForFilter(ScheduleField fld, ScheduleFilter f)
        {
            var pid = fld.HasSchedulableField ? fld.ParameterId : ElementId.InvalidElementId;
            var intId = pid.IntegerValue;

            if (EnumCompat.IsDefinedBip(intId))
            {
                var bip = EnumCompat.ToBip(intId);

                if (bip == BuiltInParameter.ELEM_FAMILY_PARAM && f.IsStringValue)
                    return EnumCompat.ToElementId(BuiltInParameter.ALL_MODEL_FAMILY_NAME);

                if (bip == BuiltInParameter.SYMBOL_ID_PARAM && f.IsStringValue)
                    return EnumCompat.ToElementId(BuiltInParameter.SYMBOL_NAME_PARAM);
            }

            return pid;
        }

        private static bool ParamExists(Element e, Document doc, ElementId pid, string fallbackName)
        {
            var cm = new ColumnMap { ParameterId = pid, OriginalName = fallbackName };
            var p = ParamUtils.GetParameterById(e, doc, cm, allowByName: true);
            return p != null;
        }

        private static FilterStringRuleEvaluator GetStringEvaluator(ScheduleFilterType t, out bool invert)
        {
            invert = false;
            switch (t)
            {
                case ScheduleFilterType.Equal: return new FilterStringEquals();
                case ScheduleFilterType.NotEqual: invert = true; return new FilterStringEquals();
                case ScheduleFilterType.Contains: return new FilterStringContains();
                case ScheduleFilterType.NotContains: invert = true; return new FilterStringContains();
                case ScheduleFilterType.BeginsWith: return new FilterStringBeginsWith();
                case ScheduleFilterType.NotBeginsWith: invert = true; return new FilterStringBeginsWith();
                case ScheduleFilterType.EndsWith: return new FilterStringEndsWith();
                case ScheduleFilterType.NotEndsWith: invert = true; return new FilterStringEndsWith();
                default: return null;
            }
        }

        private static FilterNumericRuleEvaluator GetNumericEvaluator(ScheduleFilterType t, out bool invert)
        {
            invert = false;
            switch (t)
            {
                case ScheduleFilterType.Equal: return new FilterNumericEquals();
                case ScheduleFilterType.NotEqual: invert = true; return new FilterNumericEquals();
                case ScheduleFilterType.GreaterThan: return new FilterNumericGreater();
                case ScheduleFilterType.GreaterThanOrEqual: return new FilterNumericGreaterOrEqual();
                case ScheduleFilterType.LessThan: return new FilterNumericLess();
                case ScheduleFilterType.LessThanOrEqual: return new FilterNumericLessOrEqual();
                default: return null;
            }
        }

        // ---------- Tri identique à la nomenclature ----------
        private static void ApplyScheduleSortOrder(Document doc, ScheduleDefinition def, List<Element> elements)
        {
            int n = def.GetSortGroupFieldCount();
            if (n == 0 || elements.Count <= 1) return;

            IOrderedEnumerable<Element> ordered = null;

            for (int i = 0; i < n; i++)
            {
                var sg = def.GetSortGroupField(i);
                var fld = def.GetField(sg.FieldId);
                var pid = fld.HasSchedulableField ? fld.ParameterId : ElementId.InvalidElementId;

                var cm = new ColumnMap { ParameterId = pid, OriginalName = fld.GetName() };

                Func<Element, IComparable> keySel = (Element e) =>
                {
                    var p = ParamUtils.GetParameterById(e, doc, cm, allowByName: true);
                    if (p == null) return string.Empty;

                    try
                    {
                        switch (p.StorageType)
                        {
                            case StorageType.String: return p.AsString() ?? ParamUtils.AsReadable(doc, p);
                            case StorageType.Integer: return p.AsInteger();
                            case StorageType.Double: return p.AsDouble();
                            case StorageType.ElementId: return p.AsElementId()?.IntegerValue ?? -1;
                            default: return ParamUtils.AsReadable(doc, p);
                        }
                    }
                    catch { return ParamUtils.AsReadable(doc, p); }
                };

                bool asc = sg.SortOrder == ScheduleSortOrder.Ascending;

                if (ordered == null)
                    ordered = asc ? elements.OrderBy(keySel) : elements.OrderByDescending(keySel);
                else
                    ordered = asc ? ordered.ThenBy(keySel) : ordered.ThenByDescending(keySel);
            }

            if (ordered != null) { var list = ordered.ToList(); elements.Clear(); elements.AddRange(list); }
        }

        private static List<Dictionary<string, string>> ReadSheetToRows(ISheet sheet)
        {
            var rows = new List<Dictionary<string, string>>();
            if (sheet == null) return rows;

            var headerRow = sheet.GetRow(0);
            if (headerRow == null) return rows;

            int lastCol = headerRow.LastCellNum;
            var headers = new List<string>();
            for (int c = 0; c < lastCol; c++)
                headers.Add(headerRow.GetCell(c)?.ToString() ?? "");

            for (int r = 1; r <= sheet.LastRowNum; r++)
            {
                var row = sheet.GetRow(r);
                if (row == null) continue;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool any = false;
                for (int c = 0; c < lastCol; c++)
                {
                    string key = headers[c];
                    string val = row.GetCell(c)?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(val)) any = true;
                    dict[key] = val;
                }
                if (any) rows.Add(dict);
            }
            return rows;
        }

        private static Dictionary<string, ColumnMap> BuildHeaderMapFromActiveSchedule(ScheduleDefinition def)
        {
            var map = new Dictionary<string, ColumnMap>(StringComparer.OrdinalIgnoreCase);
            var order = def.GetFieldOrder();
            foreach (var fid in order)
            {
                var f = def.GetField(fid);
                var pid = f.HasSchedulableField ? f.ParameterId : ElementId.InvalidElementId;
                var isCalc = f.IsCalculatedField || f.IsCombinedParameterField;

                string original = f.GetName();
                string heading = original;
                try
                {
                    var t = f.GetType();
                    var propColHead = t.GetProperty("ColumnHeading");
                    if (propColHead != null) heading = propColHead.GetValue(f, null) as string ?? original;
                }
                catch { }

                string header = heading;
                if (!string.Equals(heading, original, StringComparison.Ordinal))
                    header = $"{heading} ({original})";

                map[header] = new ColumnMap
                {
                    Header = header,
                    OriginalName = original,
                    ColumnHeading = heading,
                    ParameterId = pid,
                    IsCalculatedOrCombined = isCalc,
                    IsHidden = f.IsHidden
                };
            }
            return map;
        }

        private static Dictionary<string, ColumnMap> ReadMeta(ISheet meta)
        {
            var dict = new Dictionary<string, ColumnMap>(StringComparer.OrdinalIgnoreCase);
            for (int r = 1; r <= meta.LastRowNum; r++)
            {
                var row = meta.GetRow(r);
                if (row == null) continue;

                string header = row.GetCell(0)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(header)) continue;

                string original = row.GetCell(1)?.ToString() ?? "";
                string heading = row.GetCell(2)?.ToString() ?? "";
                int.TryParse(row.GetCell(3)?.ToString() ?? "0", out int pidInt);
                bool isCalc = string.Equals(row.GetCell(4)?.ToString() ?? "false", "true", StringComparison.OrdinalIgnoreCase);
                bool isHidden = string.Equals(row.GetCell(5)?.ToString() ?? "false", "true", StringComparison.OrdinalIgnoreCase);
                string storage = row.GetCell(6)?.ToString() ?? "";
                string spec = row.GetCell(7)?.ToString() ?? "";
                bool isWritable = string.Equals(row.GetCell(8)?.ToString() ?? "false", "true", StringComparison.OrdinalIgnoreCase);
                int.TryParse(row.GetCell(9)?.ToString() ?? "0", out int ok);
                int.TryParse(row.GetCell(10)?.ToString() ?? "0", out int tot);

                dict[header] = new ColumnMap
                {
                    Header = header,
                    OriginalName = original,
                    ColumnHeading = heading,
                    ParameterId = new ElementId(pidInt),
                    IsCalculatedOrCombined = isCalc,
                    IsHidden = isHidden,
                    Storage = Enum.TryParse(storage, out StorageType st) ? st : StorageType.None,
                    SpecTypeId = string.IsNullOrWhiteSpace(spec) ? null : spec,
                    IsWritable = isWritable,
                    EditableSampleCount = ok,
                    SampleCount = tot
                };
            }
            return dict;
        }

        // ======= utils import =======
        private static string CleanNumeric(string s, bool integersOnly)
        {
            if (string.IsNullOrWhiteSpace(s)) return "0";
            var t = s.Trim()
                     .Replace('\u00A0', ' ')
                     .Replace('\u202F', ' ')
                     .Replace(",", ".")
                     .Replace(" ", "");

            var allowed = integersOnly ? @"[^0-9\-\+]" : @"[^0-9eE\.\-\+]";
            t = Regex.Replace(t, allowed, "");

            int lastDot = t.LastIndexOf('.');
            if (!integersOnly && lastDot > -1)
            {
                var left = t.Substring(0, lastDot).Replace(".", "");
                var right = t.Substring(lastDot + 1);
                t = left + "." + right;
            }

            if (string.IsNullOrEmpty(t) || t == "+" || t == "-") t = "0";
            return t;
        }

        private static bool TryParseYesNo(string s, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim().ToLowerInvariant();
            if (t == "1" || t == "true" || t == "vrai" || t == "oui" || t == "yes") { value = 1; return true; }
            if (t == "0" || t == "false" || t == "faux" || t == "non" || t == "no") { value = 0; return true; }
            return false;
        }

        private class ElementIdComparer : IEqualityComparer<Element>
        {
            public bool Equals(Element x, Element y) => x?.Id.IntegerValue == y?.Id.IntegerValue;
            public int GetHashCode(Element obj) => obj?.Id.IntegerValue.GetHashCode() ?? 0;
        }
    }
}

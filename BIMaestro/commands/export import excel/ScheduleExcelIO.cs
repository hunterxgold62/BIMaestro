// ScheduleExcelIO.cs
// Revit 2023 & 2025 – Export/Import Nomenclature <-> Excel (NPOI 2.6.x)
// v4.8
// - Fix: "Famille" lisée depuis ElementType.FamilyName (ou Family.Name), "Type" depuis ElementType.Name
// - Le tri s'appuie aussi sur ces valeurs corrigées
// - Le reste : collecte robuste, filtres tolérants, tri fidèle, import/export stables

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using NPOI.SS.UserModel;
using Licensing;
using NPOI.XSSF.UserModel;
using NPOI.SS.Util;
using CellType = NPOI.SS.UserModel.CellType;


namespace ScheduleIO
{
    // ---------- Compat enums 2023/2025 ----------
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
            return ElementIdExtensions.CreateElementId(l);
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

        // === NOUVEAU : infos de nomenclature ===
        /// <summary>Index du champ dans ScheduleDefinition.</summary>
        public int FieldIndex { get; set; } = -1;

        /// <summary>Vrai si ce champ est utilisé dans le tri/groupe de la nomenclature.</summary>
        public bool IsGroupingKey { get; set; }

        /// <summary>DisplayType Revit du champ (Standard, Totals, ...).</summary>
        public ScheduleFieldDisplayType DisplayType { get; set; } = ScheduleFieldDisplayType.Standard;

        /// <summary>Texte utilisé par Revit pour "plusieurs valeurs".</summary>
        public string MultipleValuesText { get; set; }

        /// <summary>Vrai si le champ est configuré en "Totaux" dans la nomenclature.</summary>
        public bool IsTotalized
        {
            get { return DisplayType == ScheduleFieldDisplayType.Totals; }
        }
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
                    long rawId = c.ParameterId.GetIdLongValue();

                    if (rawId >= int.MinValue && rawId <= int.MaxValue &&
                        EnumCompat.IsDefinedBip((int)rawId))
                    {
                        var bip = EnumCompat.ToBip((int)rawId);
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
                        return el?.Name ?? id.GetIdLongValue().ToString(CultureInfo.InvariantCulture);
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

    internal static class NpoiStyles
    {
        public static IWorkbook CreateWorkbook() => new XSSFWorkbook();

        public static IFont Font(IWorkbook wb, bool bold = false, short size = 11)
        {
            var f = wb.CreateFont();
            f.FontName = "Calibri";
            f.FontHeightInPoints = size;
            f.IsBold = bold;
            return f;
        }

        public static ICellStyle Text(IWorkbook wb)
        {
            var df = wb.CreateDataFormat();
            var st = wb.CreateCellStyle();
            st.DataFormat = df.GetFormat("@");
            st.VerticalAlignment = VerticalAlignment.Center;
            st.SetFont(Font(wb, false, 11));
            return st;
        }

        public static ICellStyle Header(IWorkbook wb, byte r, byte g, byte b)
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
            st.SetFont(Font(wb, true, 11));
            return st;
        }

        public static ICellStyle Zebra(IWorkbook wb, byte r, byte g, byte b)
        {
            var df = wb.CreateDataFormat();
            var st = wb.CreateCellStyle();
            st.VerticalAlignment = VerticalAlignment.Center;
            st.SetFont(Font(wb, false, 11));
            if (st is XSSFCellStyle xs)
            {
                xs.SetFillForegroundColor(new XSSFColor(new byte[] { r, g, b }, null));
                xs.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
            }
            st.DataFormat = df.GetFormat("@");
            return st;
        }
    }

    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        private NaturalComparer() { }
        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1; if (y == null) return 1;
            int i = 0, j = 0, nx = x.Length, ny = y.Length;
            while (i < nx && j < ny)
            {
                char cx = x[i], cy = y[j];
                if (char.IsDigit(cx) && char.IsDigit(cy))
                {
                    long vx = 0, vy = 0; int sx = i, sy = j;
                    while (i < nx && char.IsDigit(x[i])) { vx = vx * 10 + (x[i] - '0'); i++; }
                    while (j < ny && char.IsDigit(y[j])) { vy = vy * 10 + (y[j] - '0'); j++; }
                    int c = vx.CompareTo(vy); if (c != 0) return c;
                    int lenx = i - sx, leny = j - sy; if (lenx != leny) return lenx.CompareTo(leny);
                }
                else
                {
                    int c = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                    if (c != 0) return c; i++; j++;
                }
            }
            return (nx - i).CompareTo(ny - j);
        }
    }

    // ==========================================================
    // COMMANDE UNIQUE
    // ==========================================================
    [Transaction(TransactionMode.Manual)]
    public class ScheduleExcelIOCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ScheduleExcelIOCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var td = new TaskDialog(UiLanguage.T("Nomenclature ↔ Excel", "Schedule ↔ Excel"));
            td.MainInstruction = UiLanguage.T("Que veux-tu faire ?", "What would you like to do?");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, UiLanguage.T("Exporter la nomenclature vers Excel", "Export the schedule to Excel"));
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, UiLanguage.T("Importer les modifications depuis Excel", "Import changes from Excel"));
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
                if (schedule == null) { TaskDialog.Show(UiLanguage.T("Export Excel", "Excel Export"), UiLanguage.T("Active une nomenclature.", "Activate a schedule.")); return Result.Failed; }
                if (schedule.Definition == null || schedule.IsTemplate || schedule.IsTitleblockRevisionSchedule) { TaskDialog.Show(UiLanguage.T("Export Excel", "Excel Export"), UiLanguage.T("Vue non exportable.", "This schedule cannot be exported.")); return Result.Failed; }
                if (schedule.Definition.IsKeySchedule) { TaskDialog.Show(UiLanguage.T("Export Excel", "Excel Export"), UiLanguage.T("Nomenclatures de clés non gérées.", "Key schedules are not supported.")); return Result.Failed; }

                var def = schedule.Definition;
                // Champs utilisés pour le tri/groupe dans la nomenclature
                var groupingFieldIndices = new HashSet<int>();
                try
                {
                    int sortCount = def.GetSortGroupFieldCount();
                    for (int i = 0; i < sortCount; i++)
                    {
                        var sg = def.GetSortGroupField(i);
                        int idx = def.GetFieldIndex(sg.FieldId);
                        if (def.IsValidFieldIndex(idx))
                            groupingFieldIndices.Add(idx);
                    }
                }
                catch
                {
                    // Si jamais une version exotique casse ça, on retombe sur le comportement "ancien"
                }


                // Colonnes (ordre de la nomenclature)
                var fieldOrder = def.GetFieldOrder();
                var cols = new List<ColumnMap>();
                foreach (var fieldId in fieldOrder)
                {
                    var field = def.GetField(fieldId);
                    var isCalc = field.IsCalculatedField || field.IsCombinedParameterField;
                    var pid = field.HasSchedulableField ? field.ParameterId : ElementId.InvalidElementId;

                    string original = field.GetName();
                    string heading = original;
                    try
                    {
                        // Compat : certaines versions ne publient pas directement ColumnHeading
                        var prop = field.GetType().GetProperty("ColumnHeading");
                        if (prop != null)
                            heading = prop.GetValue(field, null) as string ?? original;
                    }
                    catch { }

                    string header = heading;
                    if (!string.Equals(heading, original, StringComparison.Ordinal))
                        header = $"{heading} ({original})";

                    var cm = new ColumnMap
                    {
                        Header = header,
                        OriginalName = original,
                        ColumnHeading = heading,
                        ParameterId = pid,
                        IsCalculatedOrCombined = isCalc,
                        IsHidden = field.IsHidden,

                        // === NOUVEAU : infos de regroupement ===
                        FieldIndex = field.FieldIndex,
                        IsGroupingKey = groupingFieldIndices.Contains(field.FieldIndex),
                        DisplayType = field.DisplayType,
                        MultipleValuesText = field.MultipleValuesText
                    };

                    cols.Add(cm);
                }


                // Collecte éléments (hôte + fallback + liens)
                var all = CollectElementsForSchedule(doc, schedule, def);

                // Filtrage/tolérance + tri
                var originalCount = all.Count;
                ApplyScheduleFiltersInMemory(doc, schedule, def, all);
                if (all.Count == 0 && originalCount > 0) all = CollectElementsForSchedule(doc, schedule, def);
                ApplyScheduleSortOrderInMemory(doc, def, all);

                // Typage + éditabilité
                ProbeTypesForColumns(all.FirstOrDefault()?.Document, all.FirstOrDefault(), cols);
                AssessEditability(all.FirstOrDefault()?.Document, all, cols);

                // Données (utilise la valeur affichée fiabilisée pour Famille/Type)
                // Données (utilise la valeur affichée fiabilisée pour Famille/Type)
                var rowsEdition = new List<Dictionary<string, string>>();
                foreach (var e in all)
                {
                    var d = e.Document;
                    var line = new Dictionary<string, string>
                    {
                        ["UniqueId"] = e.UniqueId,
                        ["ElementId"] = e.Id.GetIdLongValue().ToString(CultureInfo.InvariantCulture)
                    };

                    foreach (var c in cols)
                    {
                        string val = "";
                        try { val = GetDisplayValueForColumn(d, e, c); } catch { }
                        line[c.Header] = val;
                    }
                    rowsEdition.Add(line);
                }

                // Excel
                var exportDir = PathUtil.EnsureExportDir();
                var fileName = PathUtil.SanitizeFileName(schedule.Name) + ".xlsx";
                var path = Path.Combine(exportDir, fileName);

                // Flag "Détailler chaque occurrence" de la nomenclature
                bool isItemized = def.IsItemized;

                var summaryCols = cols.Where(c => !c.IsHidden).ToList();
                var (rowsSummary, summaryLink) = BuildGroupedRows(cols, summaryCols, rowsEdition, isItemized);


                IWorkbook wb = NpoiStyles.CreateWorkbook();
                string summarySheetName = "Nomenclature";
                CreateSummarySheet(wb, summarySheetName, summaryCols, rowsSummary);

                // ⚠ On passe aussi summaryCols pour mapper les colonnes correctement
                CreateEditionSheetModern(wb, cols, rowsEdition, summarySheetName, summaryLink, summaryCols);
                WriteMetaSheet(wb, cols);



                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) wb.Write(fs);

                var dlg = new TaskDialog(UiLanguage.T("Export Excel", "Excel Export"));
                dlg.MainInstruction = UiLanguage.T("Export terminé", "Export Completed");
                dlg.MainContent = path;
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, UiLanguage.T("Ouvrir le fichier Excel", "Open the Excel file"));
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, UiLanguage.T("Ouvrir le dossier", "Open the folder"));
                dlg.CommonButtons = TaskDialogCommonButtons.Close;
                var r = dlg.Show();
                try
                {
                    if (r == TaskDialogResult.CommandLink1) Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    else if (r == TaskDialogResult.CommandLink2) Process.Start(new ProcessStartInfo { FileName = exportDir, UseShellExecute = true });
                }
                catch { }

                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }

        // ------------------------- IMPORT -------------------------
        private Result DoImport(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var ui = data.Application.ActiveUIDocument;
                var doc = ui.Document;

                var schedule = doc.ActiveView as ViewSchedule;
                if (schedule == null) { TaskDialog.Show(UiLanguage.T("Import Excel", "Excel Import"), UiLanguage.T("Active une nomenclature.", "Activate a schedule.")); return Result.Failed; }

                var exportDir = PathUtil.EnsureExportDir();
                var guess = Directory.GetFiles(exportDir, PathUtil.SanitizeFileName(schedule.Name) + ".xlsx").FirstOrDefault();
                if (guess == null)
                {
                    TaskDialog.Show(UiLanguage.T("Import Excel", "Excel Import"), UiLanguage.T($"Fichier introuvable : {PathUtil.SanitizeFileName(schedule.Name)}.xlsx\nDans: {exportDir}\nLance d'abord l'export.", $"File not found: {PathUtil.SanitizeFileName(schedule.Name)}.xlsx\nIn: {exportDir}\nRun the export first."));
                    return Result.Failed;
                }

                Dictionary<string, ColumnMap> mapByHeader;
                List<Dictionary<string, string>> dataRows;

                using (var fs = new FileStream(guess, FileMode.Open, FileAccess.Read))
                {
                    IWorkbook wb = new XSSFWorkbook(fs);
                    var ws = wb.GetSheet("Edition");
                    if (ws == null) { TaskDialog.Show(UiLanguage.T("Import Excel", "Excel Import"), UiLanguage.T("Onglet 'Edition' introuvable.", "The 'Edition' worksheet was not found.")); return Result.Failed; }

                    var meta = wb.GetSheet("Meta");
                    mapByHeader = (meta != null) ? ReadMeta(meta) : BuildHeaderMapFromActiveSchedule(((ViewSchedule)ui.Document.ActiveView).Definition);
                    var evaluator = wb.GetCreationHelper().CreateFormulaEvaluator();
                    dataRows = ReadSheetToRows(ws, evaluator);
                }

                if (!dataRows.Any() || !dataRows[0].ContainsKey("UniqueId"))
                {
                    TaskDialog.Show(UiLanguage.T("Import Excel", "Excel Import"), UiLanguage.T("Fichier invalide : aucune donnée ou 'UniqueId' manquant.", "Invalid file: no data or missing 'UniqueId'."));
                    return Result.Failed;
                }

                using (var t = new Transaction(doc, "Import paramètres depuis Excel"))
                {
                    t.Start();

                    foreach (var row in dataRows)
                    {
                        if (!row.TryGetValue("UniqueId", out string uid) || string.IsNullOrWhiteSpace(uid)) continue;

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
                                        bool applied = false;
                                        try { p.SetValueString(newText); applied = true; } catch { }
                                        if (!applied)
                                        {
                                            if (ParamUtils.TryParseWithUnits(doc, p, newText, out double v1)) { p.Set(v1); applied = true; }
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
                                                        catch { p.Set(displayVal); applied = true; }
                                                    }
                                                    else { p.Set(displayVal); applied = true; }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { /* ignore */ }
                        }
                    }

                    t.Commit();
                }

                TaskDialog.Show(UiLanguage.T("Import Excel", "Excel Import"), UiLanguage.T("Import terminé ✅", "Import completed ✅"));
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }

        // ==========================================================
        // Collecte éléments
        // ==========================================================
        private static List<Element> CollectElementsForSchedule(Document hostDoc, ViewSchedule vs, ScheduleDefinition def)
        {
            var all = new List<Element>();

            try
            {
                all.AddRange(new FilteredElementCollector(hostDoc, vs.Id).WhereElementIsNotElementType().ToElements());
                all.AddRange(new FilteredElementCollector(hostDoc, vs.Id).WhereElementIsElementType().ToElements());
            }
            catch { }

            if (all.Count == 0)
            {
                ElementId catId = ElementId.InvalidElementId;
                try { var prop = typeof(ScheduleDefinition).GetProperty("CategoryId"); if (prop != null) catId = (ElementId)prop.GetValue(def, null); } catch { }

                var col = new FilteredElementCollector(hostDoc).WhereElementIsNotElementType();
                if (catId != null && catId != ElementId.InvalidElementId) col = col.WherePasses(new ElementCategoryFilter(catId));
                all.AddRange(col.ToElements());
            }

            try
            {
                var links = new FilteredElementCollector(hostDoc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
                ElementId catId = ElementId.InvalidElementId;
                try { var prop = typeof(ScheduleDefinition).GetProperty("CategoryId"); if (prop != null) catId = (ElementId)prop.GetValue(def, null); } catch { }

                foreach (var li in links)
                {
                    var ld = li.GetLinkDocument(); if (ld == null) continue;
                    var col = new FilteredElementCollector(ld).WhereElementIsNotElementType();
                    if (catId != null && catId != ElementId.InvalidElementId) col = col.WherePasses(new ElementCategoryFilter(catId));
                    all.AddRange(col.ToElements());
                }
            }
            catch { }

            return all.Distinct(new ElementIdComparer()).ToList();
        }

        // ==========================================================
        // Filtrage tolérant & Tri fidèle
        // ==========================================================
        private static void ApplyScheduleFiltersInMemory(Document hostDoc, ViewSchedule schedule, ScheduleDefinition def, List<Element> elements)
        {
            int count = def.GetFilterCount();
            if (count == 0) return;

            bool? Passes(Element e, ScheduleFilter f, ScheduleField fld)
            {
                var d = e.Document;

                if (!fld.HasSchedulableField || fld.IsCalculatedField || fld.IsCombinedParameterField)
                    return null;

                var pid = fld.ParameterId;
                var cm = new ColumnMap { ParameterId = pid, OriginalName = fld.GetName() };
                var p = ParamUtils.GetParameterById(e, d, cm, allowByName: true);

                if (f.FilterType == ScheduleFilterType.HasParameter) return p != null;

                if (f.FilterType == ScheduleFilterType.IsAssociatedWithGlobalParameter || f.FilterType == ScheduleFilterType.IsNotAssociatedWithGlobalParameter)
                {
                    if (p == null) return (bool?)false;
                    bool associated = p.GetAssociatedGlobalParameter() != null && p.GetAssociatedGlobalParameter() != ElementId.InvalidElementId;
                    return (f.FilterType == ScheduleFilterType.IsAssociatedWithGlobalParameter) ? associated : !associated;
                }

                if (f.IsStringValue)
                {
                    string left = (p == null) ? "" : ParamUtils.AsReadable(d, p) ?? "";
                    string right = f.GetStringValue() ?? "";
                    if (p != null && p.StorageType == StorageType.ElementId)
                    {
                        var id = p.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId) left = "Non placée";
                    }
                    return EvalString(left, right, f.FilterType);
                }

                if (f.IsIntegerValue)
                {
                    if (p == null) return false;
                    int right = f.GetIntegerValue();
                    int left = 0;
                    try { left = (p.StorageType == StorageType.Integer) ? p.AsInteger() : SafeInt(ParamUtils.AsReadable(d, p)); } catch { }
                    return EvalNumeric(left, right, f.FilterType);
                }

                if (f.IsDoubleValue)
                {
                    if (p == null) return false;
                    double right = f.GetDoubleValue();
                    double left = 0;
                    try { left = (p.StorageType == StorageType.Double) ? p.AsDouble() : SafeDouble(ParamUtils.AsReadable(d, p)); } catch { }
                    return EvalNumeric(left, right, f.FilterType);
                }

                if (f.IsElementIdValue)
                {
                    if (p == null) return false;
                    var right = f.GetElementIdValue();
                    ElementId left = ElementId.InvalidElementId;
                    if (p.StorageType == StorageType.ElementId) left = p.AsElementId();
                    if (left == null) left = ElementId.InvalidElementId;
                    return left.GetIdLongValue() == right.GetIdLongValue();
                }

                return null;
            }

            elements.RemoveAll(e =>
            {
                for (int i = 0; i < count; i++)
                {
                    var f = def.GetFilter(i);
                    var fld = def.GetField(f.FieldId);
                    var res = Passes(e, f, fld);
                    if (res.HasValue)
                    {
                        if (!res.Value) return true;
                    }
                }
                return false;
            });
        }

        private static void ApplyScheduleSortOrderInMemory(Document hostDoc, ScheduleDefinition def, List<Element> elements)
        {
            int n = def.GetSortGroupFieldCount();
            if (n == 0 || elements.Count <= 1) return;

            var nat = NaturalComparer.Instance;
            IOrderedEnumerable<Element> ordered = null;

            for (int i = 0; i < n; i++)
            {
                var sg = def.GetSortGroupField(i);
                var fld = def.GetField(sg.FieldId);
                var pid = fld.HasSchedulableField ? fld.ParameterId : ElementId.InvalidElementId;

                var cm = new ColumnMap { ParameterId = pid, OriginalName = fld.GetName() };

                bool hasInt = false, hasDouble = false, hasElemId = false, isLevelField = false;
                int scanMax = Math.Min(elements.Count, 200);
                for (int k = 0; k < scanMax; k++)
                {
                    var e = elements[k];
                    var d = e.Document;
                    var p = ParamUtils.GetParameterById(e, d, cm, allowByName: true);
                    if (p == null) continue;

                    if (p.StorageType == StorageType.Integer) { hasInt = true; continue; }
                    if (p.StorageType == StorageType.Double) { hasDouble = true; continue; }
                    if (p.StorageType == StorageType.ElementId)
                    {
                        hasElemId = true;
                        try
                        {
                            var id = p.AsElementId();
                            if (id != null && id != ElementId.InvalidElementId && d.GetElement(id) is Level)
                                isLevelField = true;
                        }
                        catch { }
                    }
                }

                Func<Element, int> keyInt = e =>
                {
                    var d = e.Document; var p = ParamUtils.GetParameterById(e, d, cm, allowByName: true);
                    if (p == null) return 0;
                    try { return (p.StorageType == StorageType.Integer) ? p.AsInteger() : SafeInt(ParamUtils.AsReadable(d, p)); }
                    catch { return 0; }
                };
                Func<Element, double> keyDouble = e =>
                {
                    var d = e.Document; var p = ParamUtils.GetParameterById(e, d, cm, allowByName: true);
                    if (p == null) return 0.0;
                    try { return (p.StorageType == StorageType.Double) ? p.AsDouble() : SafeDouble(ParamUtils.AsReadable(d, p)); }
                    catch { return 0.0; }
                };
                Func<Element, double> keyLevelElev = e =>
                {
                    var d = e.Document; var p = ParamUtils.GetParameterById(e, d, cm, allowByName: true);
                    if (p == null) return double.MinValue;
                    try
                    {
                        var id = (p.StorageType == StorageType.ElementId) ? p.AsElementId() : ElementId.InvalidElementId;
                        if (id == null || id == ElementId.InvalidElementId) return double.MinValue;
                        var lvl = d.GetElement(id) as Level;
                        if (lvl != null) return lvl.Elevation;
                    }
                    catch { }
                    return double.MinValue;
                };
                Func<Element, string> keyString = e =>
                {
                    var d = e.Document;
                    return GetDisplayValueForColumn(d, e, cm); // <-- utilise la valeur corrigée (Famille/Type)
                };

                bool asc = (sg.SortOrder == ScheduleSortOrder.Ascending);

                if (hasInt && !hasDouble && !hasElemId)
                {
                    ordered = (ordered == null)
                        ? (asc ? elements.OrderBy(keyInt) : elements.OrderByDescending(keyInt))
                        : (asc ? ordered.ThenBy(keyInt) : ordered.ThenByDescending(keyInt));
                }
                else if (hasDouble && !hasElemId)
                {
                    ordered = (ordered == null)
                        ? (asc ? elements.OrderBy(keyDouble) : elements.OrderByDescending(keyDouble))
                        : (asc ? ordered.ThenBy(keyDouble) : ordered.ThenByDescending(keyDouble));
                }
                else if (hasElemId && isLevelField)
                {
                    ordered = (ordered == null)
                        ? (asc ? elements.OrderBy(keyLevelElev) : elements.OrderByDescending(keyLevelElev))
                        : (asc ? ordered.ThenBy(keyLevelElev) : ordered.ThenByDescending(keyLevelElev));
                }
                else
                {
                    ordered = (ordered == null)
                        ? (asc ? elements.OrderBy(keyString, nat) : elements.OrderByDescending(keyString, nat))
                        : (asc ? ordered.ThenBy(keyString, nat) : ordered.ThenByDescending(keyString, nat));
                }
            }

            if (ordered != null)
            {
                var list = ordered.ToList();
                elements.Clear();
                elements.AddRange(list);
            }
        }

        // ==========================================================
        // Excel
        // ==========================================================
        private struct AggregateState
        {
            public double Sum;
            public int Count;
            public char DecimalChar;
            public int Decimals;
            public string Suffix;
            public bool HasTemplate;
            public bool IntegersOnly;
        }
       

        /// <summary>
        /// Construit les lignes "Nomenclature" (résumé) à partir des lignes d'édition,
        /// en reproduisant le comportement de Revit :
        /// - Si IsItemized = true : aucune agrégation, 1 ligne par occurrence.
        /// - Si IsItemized = false : regroupement sur les champs de tri/groupe,
        ///   totaux sur les champs DisplayType = Totals, "plusieurs valeurs" sinon.
        /// </summary>
        private static (List<Dictionary<string, string>>, List<int>) BuildGroupedRows(
            List<ColumnMap> allCols,
            List<ColumnMap> summaryCols,
            List<Dictionary<string, string>> rowsEdition,
            bool isItemized)
        {
            var summary = new List<Dictionary<string, string>>();
            var summaryIndexByRow = new List<int>(rowsEdition.Count);

            if (rowsEdition == null || rowsEdition.Count == 0 || summaryCols.Count == 0)
                return (summary, summaryIndexByRow);

            // ----- Cas 1 : "Détailler chaque occurrence" coché → aucun regroupement -----
            if (isItemized)
            {
                for (int i = 0; i < rowsEdition.Count; i++)
                {
                    var src = rowsEdition[i];
                    var dst = new Dictionary<string, string>();
                    foreach (var c in summaryCols)
                    {
                        src.TryGetValue(c.Header, out string v);
                        dst[c.Header] = v ?? string.Empty;
                    }
                    summary.Add(dst);
                    summaryIndexByRow.Add(i);
                }
                return (summary, summaryIndexByRow);
            }

            // ----- Cas 2 : "Détailler chaque occurrence" décoché → agrégation -----

            // Colonnes utilisées pour la clé de regroupement : celles du tri/groupe Revit
            var groupingCols = allCols.Where(c => c.IsGroupingKey).ToList();
            bool hasGroupingCols = groupingCols.Count > 0;

            var keyToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var aggregates = new List<Dictionary<string, AggregateState>>();
            var multiValueFlags = new List<Dictionary<string, bool>>(rowsEdition.Count);

            bool TryParseNumeric(string raw, bool integersOnly, out double val, out char decChar, out int decimals, out string suffix)
            {
                val = 0; decChar = '.'; decimals = 0; suffix = string.Empty;
                if (string.IsNullOrWhiteSpace(raw)) return false;

                string cleaned = CleanNumeric(raw, integersOnly);
                if (!double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out val))
                    return false;

                var trimmed = raw.Trim();
                if (!integersOnly)
                {
                    int idxComma = trimmed.LastIndexOf(',');
                    int idxDot = trimmed.LastIndexOf('.');
                    int idx = Math.Max(idxComma, idxDot);
                    if (idx >= 0 && idx < trimmed.Length - 1)
                    {
                        decChar = trimmed[idx];
                        decimals = Math.Min(6, Math.Max(0, trimmed.Length - idx - 1));
                    }
                }

                suffix = ExtractSuffix(trimmed);
                return true;
            }

            string FormatAggregate(AggregateState st)
            {
                string fmt = st.IntegersOnly ? "0" : $"F{Math.Max(0, st.Decimals)}";
                string number = st.Sum.ToString(fmt, CultureInfo.InvariantCulture);
                if (st.DecimalChar == ',') number = number.Replace('.', ',');
                return string.IsNullOrWhiteSpace(st.Suffix) ? number : (number + st.Suffix);
            }

            foreach (var row in rowsEdition)
            {
                // --- Clé de groupe : valeurs des champs de tri/groupe ---
                string key;
                if (hasGroupingCols)
                {
                    key = string.Join("\u001F", groupingCols.Select(c =>
                    {
                        row.TryGetValue(c.Header, out string v);
                        return v ?? string.Empty;
                    }));
                }
                else
                {
                    // Aucun tri/groupe défini : un seul groupe pour toute la nomenclature
                    key = "__ALL__";
                }

                if (!keyToIndex.TryGetValue(key, out int idx))
                {
                    idx = summary.Count;
                    keyToIndex[key] = idx;

                    var copy = new Dictionary<string, string>();
                    foreach (var c in summaryCols)
                    {
                        row.TryGetValue(c.Header, out string v);
                        copy[c.Header] = v ?? string.Empty;
                    }
                    summary.Add(copy);
                    aggregates.Add(new Dictionary<string, AggregateState>(StringComparer.OrdinalIgnoreCase));
                    multiValueFlags.Add(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
                }

                summaryIndexByRow.Add(idx);

                var aggForGroup = aggregates[idx];
                var multiFlags = multiValueFlags[idx];

                // --- Mise à jour des colonnes du groupe ---
                foreach (var c in summaryCols)
                {
                    row.TryGetValue(c.Header, out string raw);
                    raw ??= string.Empty;
                    bool integersOnly = c.Storage == StorageType.Integer || c.IsYesNo;

                    if (c.IsTotalized)
                    {
                        // Colonne configurée en "Totaux" dans la nomenclature Revit → on somme
                        if (!TryParseNumeric(raw, integersOnly, out double val, out char decChar, out int decimals, out string suffix))
                            continue;

                        if (!aggForGroup.TryGetValue(c.Header, out var st))
                        {
                            st = new AggregateState
                            {
                                Sum = 0,
                                Count = 0,
                                DecimalChar = decChar,
                                Decimals = decimals,
                                Suffix = suffix,
                                HasTemplate = !string.IsNullOrWhiteSpace(raw),
                                IntegersOnly = integersOnly
                            };
                        }

                        st.Sum += val;
                        st.Count++;

                        if (!st.HasTemplate && !string.IsNullOrWhiteSpace(raw))
                        {
                            st.DecimalChar = decChar;
                            st.Decimals = decimals;
                            st.Suffix = suffix;
                            st.HasTemplate = true;
                        }

                        aggForGroup[c.Header] = st;
                    }
                    else
                    {
                        // Colonne "Standard" : si plusieurs valeurs différentes dans le groupe, afficher "plusieurs valeurs"
                        if (!multiFlags.TryGetValue(c.Header, out bool alreadyMulti))
                        {
                            multiFlags[c.Header] = false; // 1ère valeur pour cette colonne dans ce groupe
                        }
                        else if (!alreadyMulti)
                        {
                            string existing = summary[idx].TryGetValue(c.Header, out var ex) ? (ex ?? string.Empty) : string.Empty;
                            if (!string.Equals(existing, raw, StringComparison.OrdinalIgnoreCase))
                            {
                                multiFlags[c.Header] = true;
                                string mv = !string.IsNullOrEmpty(c.MultipleValuesText)
                                    ? c.MultipleValuesText
                                    : "<plusieurs valeurs>";
                                summary[idx][c.Header] = mv;
                            }
                        }
                    }
                }
            }

            // Application finale des totaux dans les lignes de résumé
            for (int i = 0; i < summary.Count; i++)
            {
                var agg = aggregates[i];
                if (agg == null || agg.Count == 0) continue;

                var dict = summary[i];
                foreach (var kvp in agg)
                {
                    var st = kvp.Value;
                    if (st.Count == 0) continue;
                    dict[kvp.Key] = FormatAggregate(st);
                }
            }

            return (summary, summaryIndexByRow);
        }


        private static ISheet CreateSummarySheet(IWorkbook wb, string name, List<ColumnMap> cols, List<Dictionary<string, string>> rowsSummary)
        {
            var sheet = wb.CreateSheet(name);
            var text = NpoiStyles.Text(wb);
            var headerOk = NpoiStyles.Header(wb, 216, 245, 209);
            var headerLock = NpoiStyles.Header(wb, 255, 214, 214);
            var zebra = NpoiStyles.Zebra(wb, 248, 249, 253);

            var headerRow = sheet.CreateRow(0); headerRow.HeightInPoints = 20f;
            var headers = cols.Select(c => c.Header).ToList();

            for (int j = 0; j < headers.Count; j++)
            {
                var cell = headerRow.CreateCell(j);
                cell.SetCellValue(headers[j]);
                var cm = cols.FirstOrDefault(c => c.Header.Equals(headers[j], StringComparison.OrdinalIgnoreCase));
                cell.CellStyle = (cm != null && cm.IsWritable) ? headerOk : headerLock;
            }

            for (int i = 0; i < rowsSummary.Count; i++)
            {
                var r = sheet.CreateRow(i + 1);
                r.HeightInPoints = 17f;
                var dict = rowsSummary[i];
                for (int j = 0; j < headers.Count; j++)
                {
                    dict.TryGetValue(headers[j], out string val);
                    var c = r.CreateCell(j);
                    c.SetCellValue((val ?? "").StartsWith("=") ? "'" + val : (val ?? ""));
                    c.CellStyle = (i % 2 == 1) ? zebra : text;
                }
            }

            sheet.CreateFreezePane(0, 1);
            int max = Math.Min(headers.Count, 30);
            for (int c = 0; c < max; c++) { try { sheet.AutoSizeColumn(c); } catch { } }

            return sheet;
        }

        private static (ISheet, ICellStyle) CreateEditionSheetModern(
     IWorkbook wb,
     List<ColumnMap> cols,
     List<Dictionary<string, string>> rowsEdition,
     string summarySheetName = null,
     List<int> summaryIndexByRow = null,
     List<ColumnMap> summaryCols = null)
        {
            var sheet = wb.CreateSheet("Edition");
            var text = NpoiStyles.Text(wb);
            var headerOk = NpoiStyles.Header(wb, 216, 245, 209);
            var headerLock = NpoiStyles.Header(wb, 255, 214, 214);
            var zebra = NpoiStyles.Zebra(wb, 248, 249, 253);

            var headerRow = sheet.CreateRow(0);
            headerRow.HeightInPoints = 20f;

            // Colonnes de l’onglet Edition
            var headers = new List<string> { "UniqueId", "ElementId" };
            headers.AddRange(cols.Select(c => c.Header));

            // Mapping : Header -> index de colonne dans "Nomenclature"
            Dictionary<string, int> summaryColIndex = null;
            // Ensemble des colonnes configurées en "Totaux" dans Revit
            HashSet<string> totalizedHeaders = null;

            if (!string.IsNullOrEmpty(summarySheetName) && summaryCols != null)
            {
                summaryColIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                totalizedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < summaryCols.Count; i++)
                {
                    var sc = summaryCols[i];
                    summaryColIndex[sc.Header] = i;
                    if (sc.IsTotalized)
                        totalizedHeaders.Add(sc.Header);
                }
            }
        

            // Ligne d’en-têtes
            for (int j = 0; j < headers.Count; j++)
            {
                var cell = headerRow.CreateCell(j);
                cell.SetCellValue(headers[j]);

                var cm = cols.FirstOrDefault(c => c.Header.Equals(headers[j], StringComparison.OrdinalIgnoreCase));
                // UniqueId / ElementId verrouillés
                cell.CellStyle = (j >= 2 && cm != null && cm.IsWritable) ? headerOk : headerLock;
            }

            // Lignes de données
            for (int i = 0; i < rowsEdition.Count; i++)
            {
                var r = sheet.CreateRow(i + 1);
                r.HeightInPoints = 17f;
                var dict = rowsEdition[i];

                for (int j = 0; j < headers.Count; j++)
                {
                    dict.TryGetValue(headers[j], out string val);
                    var c = r.CreateCell(j);

                    int summaryColIdx = -1;

                    bool canLinkToSummary =
                        !string.IsNullOrEmpty(summarySheetName) &&
                        summaryIndexByRow != null &&
                        summaryIndexByRow.Count > i &&
                        j >= 2 && // pas UniqueId / ElementId
                        summaryColIndex != null &&
                        summaryColIndex.TryGetValue(headers[j], out summaryColIdx);

                    // Colonne en "Totaux" dans la nomenclature ?
                    bool isTotalized =
                        totalizedHeaders != null &&
                        totalizedHeaders.Contains(headers[j]);

                    // ⚠ Si la colonne est en "Totaux" dans Revit, on NE fait PAS de lien :
                    // on garde la valeur brute de l’élément.
                    if (canLinkToSummary && !isTotalized)
                    {
                        var escapedSheet = summarySheetName.Replace("'", "''");
                        string colRef = CellReference.ConvertNumToColString(summaryColIdx);
                        int summaryRow = summaryIndexByRow[i] + 2; // +1 entête +1 car 1-based
                        c.SetCellFormula($"'{escapedSheet}'!{colRef}{summaryRow}");
                    }
                    else
                    {
                        c.SetCellValue((val ?? "").StartsWith("=") ? "'" + val : (val ?? ""));
                    }

                    c.CellStyle = (i % 2 == 1) ? zebra : text;
                }
            }

            // Cacher UniqueId / ElementId
            sheet.SetColumnHidden(0, true);
            sheet.SetColumnHidden(1, true);
            sheet.CreateFreezePane(0, 1);

            int max = Math.Min(headers.Count, 30);
            for (int cIdx = 0; cIdx < max; cIdx++)
            {
                try { sheet.AutoSizeColumn(cIdx); } catch { }
            }

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
                rr.CreateCell(3).SetCellValue(
                    c.ParameterId.GetIdLongValue().ToString(CultureInfo.InvariantCulture));
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

        // ==========================================================
        // Utilitaires communs
        // ==========================================================
        private static string GetCellText(ICell cell, IFormulaEvaluator evaluator)
        {
            if (cell == null) return string.Empty;

            if (evaluator != null && cell.CellType == CellType.Formula)
            {
                var v = evaluator.Evaluate(cell);
                if (v != null)
                {
                    switch (v.CellType)
                    {
                        case CellType.String: return v.StringValue ?? string.Empty;
                        case CellType.Numeric: return v.NumberValue.ToString(CultureInfo.InvariantCulture);
                        case CellType.Boolean: return v.BooleanValue ? "TRUE" : "FALSE";
                        default: return cell.ToString();
                    }
                }
            }

            return cell.ToString();
        }

        private static List<Dictionary<string, string>> ReadSheetToRows(ISheet sheet, IFormulaEvaluator evaluator)
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
                    string val = GetCellText(row.GetCell(c), evaluator);
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
                    var prop = f.GetType().GetProperty("ColumnHeading");
                    if (prop != null) heading = prop.GetValue(f, null) as string ?? original;
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
                long.TryParse(
                    row.GetCell(3)?.ToString() ?? "0",
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long pidValue);
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
                    ParameterId = ElementIdExtensions.CreateElementId(pidValue),
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

        private static string CleanNumeric(string s, bool integersOnly)
        {
            if (string.IsNullOrWhiteSpace(s)) return "0";
            var t = s.Trim().Replace('\u00A0', ' ').Replace('\u202F', ' ').Replace(",", ".").Replace(" ", "");
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
        private static string ExtractSuffix(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var trimmed = s.TrimEnd();
            int lastDigit = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (char.IsDigit(trimmed[i])) lastDigit = i;
            }

            if (lastDigit == -1 || lastDigit == trimmed.Length - 1) return string.Empty;
            return trimmed.Substring(lastDigit + 1);
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
            public bool Equals(Element x, Element y)
            {
                if (x == null && y == null) return true;
                if (x == null || y == null) return false;
                if (!ReferenceEquals(x.Document, y.Document)) return false;
                return x.Id.GetIdLongValue() == y.Id.GetIdLongValue();
            }
            public int GetHashCode(Element obj)
            {
                unchecked
                {
                    int h1 = obj.Document.GetHashCode();
                    int h2 = obj.Id.GetIdLongValue().GetHashCode();
                    return (h1 * 397) ^ h2;
                }
            }
        }

        private static void ProbeTypesForColumns(Document doc, Element probe, List<ColumnMap> cols)
        {
            if (probe == null) return;
            doc = probe.Document;
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

        private static void AssessEditability(Document doc, List<Element> elements, List<ColumnMap> cols, int sampleMax = 30)
        {
            if (elements.Count == 0) return;
            doc = elements[0].Document;

            var sample = elements.Take(sampleMax).ToList();
            using (var t = new Transaction(doc, "Probe editability (no-op)"))
            {
                t.Start();

                foreach (var c in cols)
                {
                    int ok = 0, tot = 0;

                    foreach (var e in sample)
                    {
                        var p = ParamUtils.GetParameterById(e, e.Document, c, allowByName: true);
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

        // ---------- helpers d’évaluation ----------
        private static bool EvalString(string left, string right, ScheduleFilterType t)
        {
            var l = left ?? ""; var r = right ?? "";
            switch (t)
            {
                case ScheduleFilterType.Equal: return string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
                case ScheduleFilterType.NotEqual: return !string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
                case ScheduleFilterType.Contains: return l.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0;
                case ScheduleFilterType.NotContains: return l.IndexOf(r, StringComparison.OrdinalIgnoreCase) < 0;
                case ScheduleFilterType.BeginsWith: return l.StartsWith(r, StringComparison.OrdinalIgnoreCase);
                case ScheduleFilterType.NotBeginsWith: return !l.StartsWith(r, StringComparison.OrdinalIgnoreCase);
                case ScheduleFilterType.EndsWith: return l.EndsWith(r, StringComparison.OrdinalIgnoreCase);
                case ScheduleFilterType.NotEndsWith: return !l.EndsWith(r, StringComparison.OrdinalIgnoreCase);
                default: return true;
            }
        }
        private static bool EvalNumeric(double left, double right, ScheduleFilterType t)
        {
            switch (t)
            {
                case ScheduleFilterType.Equal: return Math.Abs(left - right) < 1e-9;
                case ScheduleFilterType.NotEqual: return Math.Abs(left - right) >= 1e-9;
                case ScheduleFilterType.GreaterThan: return left > right;
                case ScheduleFilterType.GreaterThanOrEqual: return left >= right;
                case ScheduleFilterType.LessThan: return left < right;
                case ScheduleFilterType.LessThanOrEqual: return left <= right;
                default: return true;
            }
        }

        // ==========================================================
        // *** NOUVEAU *** : valeur affichée fiable pour une colonne
        // ==========================================================
        private static string GetDisplayValueForColumn(Document d, Element e, ColumnMap c)
        {
            // Si on reconnaît des BIP "systèmes", appliquer la règle explicite
            try
            {
                long pidValue = c?.ParameterId?.GetIdLongValue() ?? 0L;
                if (pidValue >= int.MinValue && pidValue <= int.MaxValue &&
                    EnumCompat.IsDefinedBip((int)pidValue))
                {
                    int pidInt = (int)pidValue;
                    var bip = EnumCompat.ToBip(pidInt);

                    // TYPE (nom de type)
                    if (bip == BuiltInParameter.SYMBOL_NAME_PARAM ||
                        bip == BuiltInParameter.ALL_MODEL_TYPE_NAME ||
                        bip == BuiltInParameter.SYMBOL_FAMILY_AND_TYPE_NAMES_PARAM /* colonnes combinées souvent mappées ici */)
                    {
                        var et = d.GetElement(e.GetTypeId()) as ElementType;
                        return et?.Name ?? "";
                    }

                    // FAMILLE (nom de famille)
                    if (bip == BuiltInParameter.ELEM_FAMILY_PARAM ||
                        bip == BuiltInParameter.ALL_MODEL_FAMILY_NAME)
                    {
                        var et = d.GetElement(e.GetTypeId()) as ElementType;
                        if (et is FamilySymbol fs) return fs.Family?.Name ?? et.FamilyName ?? "";
                        return et?.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME)?.AsString()
                               ?? et?.FamilyName
                               ?? "";
                    }
                }
            }
            catch { }

            // Par défaut : via Parameter (avec rendu lisible)
            var p = ParamUtils.GetParameterById(e, d, c, allowByName: true);
            if (p != null) return ParamUtils.AsReadable(d, p);

            // Dernier recours : pour certains ID vides d’ElementId → "Non placée"
            if (p != null && p.StorageType == StorageType.ElementId)
            {
                var id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return "Non placée";
            }
            return "";
        }

        private static int SafeInt(string s) { int.TryParse(CleanNumeric(s, true), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v); return v; }
        private static double SafeDouble(string s) { double.TryParse(CleanNumeric(s, false), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double v); return v; }
    }
}

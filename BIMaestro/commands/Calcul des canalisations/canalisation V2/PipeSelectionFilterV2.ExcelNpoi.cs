using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NPOI.SS.UserModel;
using NPOI.SS.UserModel.Charts;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace Analyse
{
    public partial class PipeLengthByDiameterCommandV2
    {
        private sealed class PipeExcelStyles
        {
            public PipeExcelStyles(XSSFWorkbook workbook)
            {
                Bold = CreateStyle(workbook, true, null);
                Header = CreateStyle(workbook, true, System.Drawing.Color.LightBlue);
                Total = CreateStyle(workbook, true, System.Drawing.Color.LightGreen);
            }

            public ICellStyle Bold { get; }
            public ICellStyle Header { get; }
            public ICellStyle Total { get; }

            public ICellStyle CreateNetworkHeader(XSSFWorkbook workbook, System.Drawing.Color color)
            {
                return CreateStyle(workbook, true, color);
            }

            private static ICellStyle CreateStyle(
                XSSFWorkbook workbook,
                bool bold,
                System.Drawing.Color? fillColor)
            {
                ICellStyle style = workbook.CreateCellStyle();
                IFont font = workbook.CreateFont();
                font.IsBold = bold;
                style.SetFont(font);

                if (fillColor.HasValue)
                {
                    style.FillPattern = FillPattern.SolidForeground;
                    if (style is XSSFCellStyle xssfStyle)
                    {
                        xssfStyle.SetFillForegroundColor(ToXssfColor(fillColor.Value));
                    }
                }

                return style;
            }
        }

        private string ExportToExcelNpoi(
            string projectName,
            Dictionary<double, double> pipeData,
            Dictionary<double, double> pipeFittingData,
            Dictionary<string, double> ductData,
            Dictionary<string, double> ductFittingData,
            bool includeDucts,
            Dictionary<string, int> elbowCounts,
            Dictionary<string, int> teeCounts,
            Dictionary<double, (double DiametreInterieur, double DiametreExterieur)> dnToDiameters,
            Dictionary<double, double> pipeVolumes,
            string singleSystemType,
            Dictionary<string, NetworkAggregation> networkAggregates,
            Dictionary<string, int> pipeAccessoryCounts,
            Dictionary<string, System.Drawing.Color> networkColors)
        {
            string folderName = includeDucts ? "LongueurCanalisations-Gaine" : "LongueurCanalisations";
            string folderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                folderName);
            Directory.CreateDirectory(folderPath);

            string fileName;
            if (!string.IsNullOrEmpty(singleSystemType))
            {
                string safeSystemType = string.Join("_", singleSystemType.Split(Path.GetInvalidFileNameChars()));
                fileName = $"{projectName}_{safeSystemType}_LongueurElements_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            }
            else
            {
                fileName = $"{projectName}_LongueurElements_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            }

            string filePath = Path.Combine(folderPath, fileName);

            using (var workbook = new XSSFWorkbook())
            {
                var styles = new PipeExcelStyles(workbook);
                WriteGeneralSheet(
                    workbook,
                    styles,
                    projectName,
                    pipeData,
                    pipeFittingData,
                    ductData,
                    ductFittingData,
                    includeDucts,
                    elbowCounts,
                    teeCounts,
                    dnToDiameters,
                    pipeVolumes,
                    singleSystemType);

                WriteNetworkSheets(
                    workbook,
                    styles,
                    networkAggregates,
                    networkColors,
                    includeDucts);

                WriteChartSheet(workbook, styles, pipeData, elbowCounts);
                WriteAccessorySheet(workbook, styles, pipeAccessoryCounts);

                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    workbook.Write(stream);
                }
            }

            return filePath;
        }

        private static void WriteGeneralSheet(
            XSSFWorkbook workbook,
            PipeExcelStyles styles,
            string projectName,
            Dictionary<double, double> pipeData,
            Dictionary<double, double> pipeFittingData,
            Dictionary<string, double> ductData,
            Dictionary<string, double> ductFittingData,
            bool includeDucts,
            Dictionary<string, int> elbowCounts,
            Dictionary<string, int> teeCounts,
            Dictionary<double, (double DiametreInterieur, double DiametreExterieur)> dnToDiameters,
            Dictionary<double, double> pipeVolumes,
            string singleSystemType)
        {
            ISheet sheet = workbook.CreateSheet("Général");
            int row = 0;

            SetText(sheet, row, 0, "Nom de la maquette", styles.Bold);
            SetText(sheet, row, 1, projectName, styles.Bold);
            row += 2;

            if (!string.IsNullOrEmpty(singleSystemType))
            {
                SetText(sheet, row, 0, "Système sélectionné", styles.Bold);
                SetText(sheet, row, 1, singleSystemType, styles.Bold);
                row += 2;
            }

            if (pipeData.Count > 0)
            {
                WriteDoubleSection(
                    sheet, ref row, styles,
                    "Longueur totale des canalisations par diamètre (DN)",
                    "DN (mm)", "Longueur (m)",
                    pipeData.OrderBy(item => item.Key),
                    key => key.ToString("N0"), true);
            }

            if (dnToDiameters.Count > 0)
            {
                WriteHeaderRow(
                    sheet, row++, styles.Header,
                    "Légende des diamètres (DN, Diamètre Intérieur, Diamètre Extérieur)", "", "");
                WriteHeaderRow(
                    sheet, row++, styles.Header,
                    "DN (mm)", "Diamètre Int. (mm)", "Diamètre Ext. (mm)");

                foreach (var item in dnToDiameters.OrderBy(item => item.Key))
                {
                    SetText(sheet, row, 0, item.Key.ToString("N0"));
                    SetText(sheet, row, 1, item.Value.DiametreInterieur.ToString("N1"));
                    SetText(sheet, row, 2, item.Value.DiametreExterieur.ToString("N1"));
                    row++;
                }

                row += 2;
            }

            if (pipeVolumes.Count > 0)
            {
                WriteDoubleSection(
                    sheet, ref row, styles,
                    "Volume total d'eau par diamètre intérieur",
                    "Diamètre Int. (mm)", "Volume (m³)",
                    pipeVolumes.OrderBy(item => item.Key),
                    key => key.ToString("N0"), true);
            }

            if (elbowCounts.Count > 0)
            {
                WriteCountSection(
                    sheet, ref row, styles,
                    "Nombre de coudes par diamètre",
                    "Dimensions", "Nombre",
                    elbowCounts.OrderBy(item => item.Key),
                    key => key);
            }

            if (teeCounts.Count > 0)
            {
                WriteCountSection(
                    sheet, ref row, styles,
                    "Nombre de tés par diamètre",
                    "Dimensions", "Nombre",
                    teeCounts.OrderBy(item => item.Key),
                    key => key);
            }

            if (includeDucts && ductData.Count > 0)
            {
                WriteDoubleSection(
                    sheet, ref row, styles,
                    "Longueur totale des gaines par dimension",
                    "Dimension", "Longueur (m)",
                    ductData.OrderBy(item => item.Key),
                    key => key, true);

                if (ductFittingData.Count > 0)
                {
                    WriteDoubleSection(
                        sheet, ref row, styles,
                        "Accessoires de gaines (approximatif)",
                        "Dimension", "Longueur (m)",
                        ductFittingData.OrderBy(item => item.Key),
                        key => key, true);
                }
            }

            if (pipeFittingData.Count > 0)
            {
                WriteDoubleSection(
                    sheet, ref row, styles,
                    "Accessoires de canalisations (approximatif)",
                    "Diamètre (mm)", "Longueur (m)",
                    pipeFittingData.OrderBy(item => item.Key),
                    key => key.ToString("N0"), true);
            }

            AutoSizeColumns(sheet, 3);
        }

        private static void WriteNetworkSheets(
            XSSFWorkbook workbook,
            PipeExcelStyles styles,
            Dictionary<string, NetworkAggregation> networkAggregates,
            Dictionary<string, System.Drawing.Color> networkColors,
            bool includeDucts)
        {
            var orderedNetworks = networkAggregates
                .OrderByDescending(network => network.Value.PipeLengths.Values.Sum())
                .ToList();

            int sheetNumber = 1;
            foreach (var network in orderedNetworks)
            {
                var sheet = (XSSFSheet)workbook.CreateSheet($"Réseau {sheetNumber}");
                System.Drawing.Color networkColor = networkColors.TryGetValue(network.Key, out var configuredColor)
                    ? configuredColor
                    : System.Drawing.Color.LightBlue;

                sheet.TabColor = ToXssfColor(networkColor);
                ICellStyle networkHeaderStyle = styles.CreateNetworkHeader(workbook, networkColor);

                int row = 0;
                SetText(sheet, row, 0, "Réseau :", networkHeaderStyle);
                SetText(sheet, row, 1, network.Key, networkHeaderStyle);
                row += 2;

                NetworkAggregation values = network.Value;
                if (values.PipeLengths.Count > 0)
                {
                    WriteDoubleSection(
                        sheet, ref row, styles,
                        "Canalisations par DN", "DN (mm)", "Longueur (m)",
                        values.PipeLengths.OrderBy(item => item.Key),
                        key => key.ToString("N0"), true);
                }

                if (values.PipeVolumes.Count > 0)
                {
                    WriteDoubleSection(
                        sheet, ref row, styles,
                        "Volume d'eau par diamètre intérieur", "Diamètre Int. (mm)", "Volume (m³)",
                        values.PipeVolumes.OrderBy(item => item.Key),
                        key => key.ToString("N0"), true);
                }

                if (values.ElbowCounts.Count > 0)
                {
                    WriteCountSection(
                        sheet, ref row, styles,
                        "Nombre de coudes", "Dimensions", "Nombre",
                        values.ElbowCounts.OrderBy(item => item.Key),
                        key => key);
                }

                if (values.TeeCounts.Count > 0)
                {
                    WriteCountSection(
                        sheet, ref row, styles,
                        "Nombre de tés", "Dimensions", "Nombre",
                        values.TeeCounts.OrderBy(item => item.Key),
                        key => key);
                }

                if (includeDucts && values.DuctLengths.Count > 0)
                {
                    WriteDoubleSection(
                        sheet, ref row, styles,
                        "Gaines par dimension", "Dimension", "Longueur (m)",
                        values.DuctLengths.OrderBy(item => item.Key),
                        key => key, true);
                }

                if (values.PipeFittingLengths.Count > 0)
                {
                    WriteDoubleSection(
                        sheet, ref row, styles,
                        "Accessoires de canalisations (approximatif)", "Diamètre (mm)", "Longueur (m)",
                        values.PipeFittingLengths.OrderBy(item => item.Key),
                        key => key.ToString("N0"), true);
                }

                AutoSizeColumns(sheet, 3);
                sheetNumber++;
            }
        }

        private static void WriteChartSheet(
            XSSFWorkbook workbook,
            PipeExcelStyles styles,
            Dictionary<double, double> pipeData,
            Dictionary<string, int> elbowCounts)
        {
            if (pipeData.Count == 0 && elbowCounts.Count == 0)
                return;

            ISheet sheet = workbook.CreateSheet("Graphique");
            int chartRow = 0;

            if (pipeData.Count > 0)
            {
                WriteHeaderRow(sheet, chartRow++, styles.Header, "DN (mm)", "Longueur (m)");
                int dataStartRow = chartRow;
                foreach (var item in pipeData.OrderBy(item => item.Key))
                {
                    SetNumber(sheet, chartRow, 0, item.Key);
                    SetNumber(sheet, chartRow, 1, item.Value);
                    chartRow++;
                }

                AddColumnChart(
                    sheet,
                    dataStartRow,
                    chartRow - 1,
                    "Longueur des canalisations par DN",
                    3, 0, 13, 20);
            }

            if (elbowCounts.Count > 0)
            {
                int headerRow = chartRow + (pipeData.Count > 0 ? 2 : 0);
                int dataStartRow = headerRow + 1;
                int currentRow = dataStartRow;

                WriteHeaderRow(sheet, headerRow, styles.Header, "Dimensions", "Nombre de coudes");
                foreach (var item in elbowCounts.OrderBy(item => item.Key))
                {
                    SetText(sheet, currentRow, 0, item.Key);
                    SetNumber(sheet, currentRow, 1, item.Value);
                    currentRow++;
                }

                AddPieChart(
                    sheet,
                    dataStartRow,
                    currentRow - 1,
                    "Répartition des coudes par dimensions",
                    6, headerRow, 16, headerRow + 20);
            }

            AutoSizeColumns(sheet, 2);
        }

        private static void WriteAccessorySheet(
            XSSFWorkbook workbook,
            PipeExcelStyles styles,
            Dictionary<string, int> pipeAccessoryCounts)
        {
            ISheet sheet = workbook.CreateSheet("Accessoires Canalisation");
            int row = 0;
            WriteHeaderRow(sheet, row++, styles.Header, "Type d'accessoire", "Quantité");

            int total = 0;
            foreach (var item in pipeAccessoryCounts.OrderBy(item => item.Key))
            {
                SetText(sheet, row, 0, item.Key);
                SetNumber(sheet, row, 1, item.Value);
                total += item.Value;
                row++;
            }

            SetText(sheet, row, 0, "Total", styles.Total);
            SetNumber(sheet, row, 1, total, styles.Total);
            AutoSizeColumns(sheet, 2);
        }

        private static void AddColumnChart(
            ISheet sheet,
            int firstDataRow,
            int lastDataRow,
            string title,
            int startColumn,
            int startRow,
            int endColumn,
            int endRow)
        {
            if (lastDataRow < firstDataRow)
                return;

            var drawing = (XSSFDrawing)sheet.CreateDrawingPatriarch();
            IClientAnchor anchor = drawing.CreateAnchor(0, 0, 0, 0, startColumn, startRow, endColumn, endRow);
            var chart = (XSSFChart)drawing.CreateChart(anchor);
            chart.SetTitle(title);

            var chartData = chart.ChartDataFactory.CreateColumnChartData<double, double>();
            var categories = DataSources.FromNumericCellRange(
                sheet, new CellRangeAddress(firstDataRow, lastDataRow, 0, 0));
            var values = DataSources.FromNumericCellRange(
                sheet, new CellRangeAddress(firstDataRow, lastDataRow, 1, 1));
            chartData.AddSeries(categories, values);
            chartData.SetBarGrouping(BarGrouping.Clustered);

            IChartAxis categoryAxis = chart.CreateCategoryAxis(AxisPosition.Bottom);
            IValueAxis valueAxis = chart.CreateValueAxis(AxisPosition.Left);
            chart.Plot(chartData, new IChartAxis[] { categoryAxis, valueAxis });
        }

        private static void AddPieChart(
            ISheet sheet,
            int firstDataRow,
            int lastDataRow,
            string title,
            int startColumn,
            int startRow,
            int endColumn,
            int endRow)
        {
            if (lastDataRow < firstDataRow)
                return;

            var drawing = (XSSFDrawing)sheet.CreateDrawingPatriarch();
            IClientAnchor anchor = drawing.CreateAnchor(0, 0, 0, 0, startColumn, startRow, endColumn, endRow);
            var chart = (XSSFChart)drawing.CreateChart(anchor);
            chart.SetTitle(title);

            var chartData = chart.ChartDataFactory.CreatePieChartData<string, double>();
            var categories = DataSources.FromStringCellRange(
                sheet, new CellRangeAddress(firstDataRow, lastDataRow, 0, 0));
            var values = DataSources.FromNumericCellRange(
                sheet, new CellRangeAddress(firstDataRow, lastDataRow, 1, 1));
            chartData.AddSeries(categories, values);
            chart.Plot(chartData, Array.Empty<IChartAxis>());
        }

        private static void WriteDoubleSection<TKey>(
            ISheet sheet,
            ref int row,
            PipeExcelStyles styles,
            string title,
            string keyHeader,
            string valueHeader,
            IEnumerable<KeyValuePair<TKey, double>> entries,
            Func<TKey, string> keyFormatter,
            bool includeTotal)
        {
            WriteHeaderRow(sheet, row++, styles.Header, title, "");
            WriteHeaderRow(sheet, row++, styles.Header, keyHeader, valueHeader);

            double total = 0;
            foreach (var entry in entries)
            {
                SetText(sheet, row, 0, keyFormatter(entry.Key));
                SetNumber(sheet, row, 1, entry.Value);
                total += entry.Value;
                row++;
            }

            if (includeTotal)
            {
                SetText(sheet, row, 0, "Total", styles.Total);
                SetNumber(sheet, row, 1, total, styles.Total);
            }

            row += 2;
        }

        private static void WriteCountSection<TKey>(
            ISheet sheet,
            ref int row,
            PipeExcelStyles styles,
            string title,
            string keyHeader,
            string valueHeader,
            IEnumerable<KeyValuePair<TKey, int>> entries,
            Func<TKey, string> keyFormatter)
        {
            WriteHeaderRow(sheet, row++, styles.Header, title, "");
            WriteHeaderRow(sheet, row++, styles.Header, keyHeader, valueHeader);

            foreach (var entry in entries)
            {
                SetText(sheet, row, 0, keyFormatter(entry.Key));
                SetNumber(sheet, row, 1, entry.Value);
                row++;
            }

            row += 2;
        }

        private static void WriteHeaderRow(
            ISheet sheet,
            int rowIndex,
            ICellStyle style,
            params string[] values)
        {
            for (int column = 0; column < values.Length; column++)
            {
                SetText(sheet, rowIndex, column, values[column], style);
            }
        }

        private static void SetText(
            ISheet sheet,
            int rowIndex,
            int columnIndex,
            string value,
            ICellStyle style = null)
        {
            ICell cell = GetOrCreateCell(sheet, rowIndex, columnIndex);
            cell.SetCellValue(value ?? string.Empty);
            if (style != null)
                cell.CellStyle = style;
        }

        private static void SetNumber(
            ISheet sheet,
            int rowIndex,
            int columnIndex,
            double value,
            ICellStyle style = null)
        {
            ICell cell = GetOrCreateCell(sheet, rowIndex, columnIndex);
            cell.SetCellValue(value);
            if (style != null)
                cell.CellStyle = style;
        }

        private static ICell GetOrCreateCell(ISheet sheet, int rowIndex, int columnIndex)
        {
            IRow row = sheet.GetRow(rowIndex) ?? sheet.CreateRow(rowIndex);
            return row.GetCell(columnIndex) ?? row.CreateCell(columnIndex);
        }

        private static void AutoSizeColumns(ISheet sheet, int columnCount)
        {
            for (int column = 0; column < columnCount; column++)
            {
                try
                {
                    sheet.AutoSizeColumn(column);
                }
                catch
                {
                    // Une largeur par défaut reste utilisable si une police manque sur le poste.
                }
            }
        }

        private static XSSFColor ToXssfColor(System.Drawing.Color color)
        {
            return new XSSFColor(new byte[] { color.R, color.G, color.B }, null);
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace BIMaestro.Dashboard
{
    [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
    public partial class TimeSeriesDashboardWindow : Window
    {
        private readonly string _excelPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "Historique_Temps_Revit.xlsx");
        private List<Entry> _saved = new List<Entry>();
        private List<Entry> _all = new List<Entry>();
        private List<Entry> _detail = new List<Entry>();
        private List<ModelChoice> _choices = new List<ModelChoice>();
        private List<Total> _detailTotals = new List<Total>();
        private readonly DispatcherTimer _timer;
        private bool _ready, _batch;
        private int _days = 7;
        private int _invalidRows;
        private string _loadError;
        private static readonly string[] Palette = { "#2F80ED", "#27AE60", "#9B51E0", "#F2994A", "#EB5757", "#219EA6", "#64748B", "#B66B95" };

        public TimeSeriesDashboardWindow(string currentDocumentPath = null)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            From.SelectedDate = DateTime.Today.AddDays(-14);
            To.SelectedDate = DateTime.Today;
            Version.ItemsSource = new[] { "Toutes" };
            Version.SelectedIndex = 0;
            _ready = true;
            Reload();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += (s, e) => Reload();
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
        }

        private void Excel_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (!File.Exists(_excelPath)) { MessageBox.Show(this, "Aucun historique Excel enregistré pour le moment.", "BIMaestro"); return; }
                Process.Start(new ProcessStartInfo(_excelPath) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void Reload_Click(object s, RoutedEventArgs e) => Reload();
        private void Reload()
        {
            try
            {
                var saved = new List<Entry>();
                int invalid = 0;
                if (File.Exists(_excelPath))
                {
                    using var stream = new FileStream(_excelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var book = new XSSFWorkbook(stream);
                    var sheet = book.GetSheet("Historique_Temps_Revit");
                    if (sheet == null) throw new InvalidDataException("La feuille Historique_Temps_Revit est absente.");
                    var format = new DataFormatter();
                    for (int i = sheet.FirstRowNum + 1; i <= sheet.LastRowNum; i++)
                    {
                        var row = sheet.GetRow(i);
                        if (row == null || !Cell(row, 0, format).Equals("Fermé", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!TryDate(Cell(row, 4, format), Cell(row, 5, format), out var when)) { invalid++; continue; }
                        double hours = Hours(row.GetCell(6), format);
                        if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0) { invalid++; continue; }
                        if (hours == 0) continue;
                        saved.Add(new Entry { Id = Normalize(Cell(row, 1, format)), Name = Cell(row, 2, format), Version = Cell(row, 3, format), When = when, Hours = hours,
                            Kind = Cell(row, 8, format), Path = First(Cell(row, 10, format), Cell(row, 11, format), Cell(row, 1, format)), Parameters = Cell(row, 17, format) });
                    }
                }
                _saved = saved;
                _invalidRows = invalid;
                _loadError = null;
            }
            catch (Exception ex) { _loadError = "Lecture de l’historique impossible : " + ex.Message; }
            // Current-process open sessions are not yet present as closed rows in the workbook.
            _all = _saved.Concat(ExcelLogger.GetDashboardEntries().Select(x => new Entry {
                Id = Normalize(x.DocumentId), Name = x.Name, Version = x.Version, Kind = x.Kind,
                Path = First(x.Path, x.DocumentId), Parameters = x.Parameters, When = x.When, Hours = x.Hours, Live = true
            })).ToList();
            var old = _choices.ToDictionary(x => x.Id, x => x.Selected, StringComparer.OrdinalIgnoreCase);
            _choices = _all.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => new ModelChoice {
                Id = g.Key, Name = First(g.Last().Name, g.Key), Path = g.Last().Path,
                Selected = !old.TryGetValue(g.Key, out bool value) || value
            }).OrderBy(x => x.Name).ToList();
            _batch = true;
            var version = Version.SelectedItem as string;
            Version.ItemsSource = new[] { "Toutes" }.Concat(_all.Select(x => x.Version).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
            Version.SelectedItem = version ?? "Toutes";
            if (Version.SelectedIndex < 0) Version.SelectedIndex = 0;
            _batch = false;
            Refresh();
        }

        private void Range_Click(object s, RoutedEventArgs e) { _days = int.Parse((string)((Button)s).Tag); Refresh(); }
        private void Filter_Changed(object s, RoutedEventArgs e) { if (_ready && !_batch) Refresh(); }
        private void Search_Changed(object s, TextChangedEventArgs e) { if (_ready && !_batch) Refresh(); }
        private void Selection_Changed(object s, RoutedEventArgs e)
        {
            // Bindings update before the delayed refresh; ignore recycled CheckBox events.
            if (_ready && !_batch) Dispatcher.BeginInvoke(new Action(RefreshDetail), DispatcherPriority.Background);
        }
        private void Tabs_Changed(object s, SelectionChangedEventArgs e) { if (_ready && ReferenceEquals(e.Source, Tabs)) Refresh(); }
        private void SelectAll_Click(object s, RoutedEventArgs e) => SelectVisible(true);
        private void ClearSelection_Click(object s, RoutedEventArgs e) => SelectVisible(false);
        private void SelectVisible(bool value) { foreach (var item in Picker.Items.Cast<ModelChoice>()) item.Selected = value; RefreshDetail(); }
        private void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string id) OpenAnalysis(id);
        }
        private void DetailRange_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !int.TryParse(button.Tag?.ToString(), out int days)) return;
            _batch = true; From.SelectedDate = DateTime.Today.AddDays(1 - days); To.SelectedDate = DateTime.Today; _batch = false; Refresh();
        }
        private void ResetFilters_Click(object sender, RoutedEventArgs e)
        {
            _batch = true; Search.Text = ""; ParameterSearch.Text = ""; Kind.SelectedIndex = 0; Version.SelectedIndex = 0;
            From.SelectedDate = DateTime.Today.AddDays(-14); To.SelectedDate = DateTime.Today;
            foreach (var choice in _choices) choice.Selected = true;
            _batch = false; Refresh();
        }
        private void Overview_Open(object s, MouseButtonEventArgs e)
        {
            if (OverviewTable.SelectedItem is Total total) OpenAnalysis(total.Id);
        }
        private void OpenAnalysis(string id)
        {
            _batch = true;
            From.SelectedDate = DateTime.Today.AddDays(1 - _days); To.SelectedDate = DateTime.Today;
            Search.Text = ""; ParameterSearch.Text = ""; Kind.SelectedIndex = 0; Version.SelectedIndex = 0;
            foreach (var item in _choices) item.Selected = string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase);
            Tabs.SelectedIndex = 1; _batch = false; Refresh();
        }
        private void Refresh()
        {
            if (!_ready || _batch) return;
            DateTime start = DateTime.Today.AddDays(1 - _days);
            var recent = _all.Where(x => x.When.Date >= start && x.When.Date <= DateTime.Today).ToList();
            var totals = Totals(recent);
            OverviewPeriod.Text = start.ToString("dd MMM") + " — " + DateTime.Today.ToString("dd MMM yyyy");
            OverviewHours.Text = Duration(recent.Sum(x => x.Hours));
            OverviewAverage.Text = Duration(recent.Sum(x => x.Hours) / Math.Max(1, recent.Select(x => x.When.Date).Distinct().Count()));
            OverviewCount.Text = totals.Count.ToString();
            int workedDays = recent.Select(x => x.When.Date).Distinct().Count();
            OverviewWorkedDays.Text = workedDays + " jour(s) avec activité";
            double previous = _all.Where(x => x.When.Date >= start.AddDays(-_days) && x.When.Date < start).Sum(x => x.Hours);
            double difference = recent.Sum(x => x.Hours) - previous;
            OverviewTrend.Text = previous > 0 ? (difference >= 0 ? "+" : "−") + Duration(Math.Abs(difference)) + " vs les " + _days + " jours précédents" : "Pas d’activité sur la période précédente";
            Seven.FontWeight = _days == 7 ? FontWeights.Bold : FontWeights.Normal;
            Fifteen.FontWeight = _days == 15 ? FontWeights.Bold : FontWeights.Normal;
            Seven.SetResourceReference(Control.BorderBrushProperty, _days == 7 ? "Focus" : "Border");
            Fifteen.SetResourceReference(Control.BorderBrushProperty, _days == 15 ? "Focus" : "Border");
            OverviewPlot.Model = Chart(recent, start, DateTime.Today);
            OverviewTable.ItemsSource = totals;
            OverviewEmpty.Visibility = totals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _batch = true;
            var available = new HashSet<string>(_all.Where(MatchesFilters).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            Picker.ItemsSource = _choices.Where(x => available.Contains(x.Id)).ToList();
            _batch = false;
            RefreshDetail();
            Status.Text = _loadError ?? ("Historique local · temps actif et sessions ouvertes · actualisé à " + DateTime.Now.ToString("HH:mm") +
                (_invalidRows > 0 ? " · " + _invalidRows + " ligne(s) illisible(s) ignorée(s)" : ""));
        }
        private bool MatchesFilters(Entry x)
        {
            string type = First(x.Kind, Path.GetExtension(x.Path ?? "").TrimStart('.')).ToUpperInvariant();
            return (Kind.SelectedIndex == 0 || type == (Kind.SelectedIndex == 1 ? "RVT" : "RFA"))
                && (Version.SelectedIndex <= 0 || x.Version == (string)Version.SelectedItem)
                && Contains(x.Name + " " + x.Path, Search.Text)
                && Contains(x.Parameters, ParameterSearch.Text);
        }
        private void RefreshDetail()
        {
            if (!_ready || _batch) return;
            var ids = new HashSet<string>(_choices.Where(x => x.Selected).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            DateTime start = From.SelectedDate?.Date ?? DateTime.Today.AddDays(-14), end = To.SelectedDate?.Date ?? DateTime.Today;
            bool valid = start <= end;
            _detail = valid ? _all.Where(x => x.When.Date >= start && x.When.Date <= end && ids.Contains(x.Id) && MatchesFilters(x)).ToList() : new List<Entry>();
            _detailTotals = Totals(_detail);
            DetailTable.ItemsSource = _detailTotals;
            DetailSummary.Text = valid ? Duration(_detail.Sum(x => x.Hours)) : "Période invalide";
            DetailContext.Text = valid ? _detailTotals.Count + " document(s) · " + _detail.Select(x => x.When.Date).Distinct().Count() + " jour(s) travaillé(s) · " + start.ToString("dd/MM/yyyy") + " au " + end.ToString("dd/MM/yyyy") : "La date de début doit précéder la date de fin.";
            DetailChartTitle.Text = (end - start).Days < 31 ? "Temps actif par jour" : "Évolution du temps actif · périodes regroupées";
            SelectionCount.Text = Picker.Items.Cast<ModelChoice>().Count(x => x.Selected) + " document(s) coché(s) parmi les résultats";
            DetailPlot.Model = valid ? Chart(_detail, start, end) : new PlotModel();
            DetailEmpty.Visibility = valid && _detail.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PdfButton.IsEnabled = valid && _detail.Count > 0 && _loadError == null;
        }

        private static List<Total> Totals(List<Entry> rows)
        {
            double sum = rows.Sum(x => x.Hours);
            return rows.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => new Total {
                Id = g.Key, Name = First(g.Last().Name, g.Key), Path = g.Last().Path,
                Hours = g.Sum(x => x.Hours), Days = g.Select(x => x.When.Date).Distinct().Count(), Last = g.Max(x => x.When),
                Versions = string.Join(", ", g.Select(x => x.Version).Distinct()), Share = sum > 0 ? 100 * g.Sum(x => x.Hours) / sum : 0,
                Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[ColorIndex(g.Key)]))
            }).OrderByDescending(x => x.Hours).ThenBy(x => x.Name).ToList();
        }
        private static int ColorIndex(string id) { unchecked { uint h = 2166136261; foreach (char c in id.ToUpperInvariant()) h = (h ^ c) * 16777619; return (int)(h % Palette.Length); } }
        private static OxyColor ThemeColor(string key)
        {
            var brush = Application.Current.FindResource(key) as SolidColorBrush;
            if (brush == null) throw new InvalidOperationException("Couleur du thème introuvable : " + key);
            return OxyColor.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
        }
        private static string PdfColor(OxyColor color) => string.Join(" ", new[] { color.R, color.G, color.B }.Select(x => (x / 255.0).ToString("0.###", CultureInfo.InvariantCulture)));
        private static PlotModel Chart(List<Entry> rows, DateTime start, DateTime end)
        {
            var model = new PlotModel { PlotAreaBorderColor = ThemeColor("Border"), TextColor = ThemeColor("Text.Secondary"), Background = ThemeColor("Surface") };
            int days = (end - start).Days + 1;
            int bucketDays = days <= 31 ? 1 : days <= 180 ? 7 : Math.Max(30, (int)Math.Ceiling(days / 60.0));
            int count = (int)Math.Ceiling(days / (double)bucketDays);
            var axis = new CategoryAxis { Position = AxisPosition.Bottom, GapWidth = 0.35, Angle = count > 15 ? -45 : 0 };
            for (int i = 0; i < count; i++) axis.Labels.Add(start.AddDays(i * bucketDays).ToString(days <= 15 ? "ddd dd" : days > 365 ? "dd/MM/yy" : "dd/MM", CultureInfo.GetCultureInfo("fr-FR")));
            model.Axes.Add(axis);
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, MaximumPadding = 0.2, Title = bucketDays == 1 ? "Heures / jour" : "Heures / période de " + bucketDays + " jours", MajorGridlineStyle = LineStyle.Dot });
            model.Legends.Add(new OxyPlot.Legends.Legend { LegendPosition = OxyPlot.Legends.LegendPosition.BottomCenter, LegendPlacement = OxyPlot.Legends.LegendPlacement.Outside, LegendOrientation = OxyPlot.Legends.LegendOrientation.Horizontal });
            var totals = Totals(rows);
            var top = new HashSet<string>(totals.Take(7).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            double[] baseline = new double[count];
            foreach (var group in rows.GroupBy(x => top.Contains(x.Id) ? x.Id : "__others", StringComparer.OrdinalIgnoreCase))
            {
                var total = totals.FirstOrDefault(x => string.Equals(x.Id, group.Key, StringComparison.OrdinalIgnoreCase));
                var series = new RectangleBarSeries { Title = total?.Name ?? "Autres", FillColor = OxyColor.Parse(total == null ? "#94A3B8" : Palette[ColorIndex(group.Key)]), StrokeThickness = 0,
                    TrackerFormatString = "{0}\n{1}: {2:0.00} h" };
                var hours = group.GroupBy(x => (x.When.Date - start).Days / bucketDays).ToDictionary(x => x.Key, x => x.Sum(y => y.Hours));
                for (int i = 0; i < count; i++) { hours.TryGetValue(i, out double h); if (h > 0) series.Items.Add(new RectangleBarItem(i - .34, baseline[i], i + .34, baseline[i] + h)); baseline[i] += h; }
                model.Series.Add(series);
            }
            if (days <= 15)
            {
                for (int i = 0; i < count; i++)
                    if (baseline[i] > 0) model.Annotations.Add(new OxyPlot.Annotations.TextAnnotation {
                        Text = Duration(baseline[i]), TextPosition = new DataPoint(i, baseline[i]),
                        TextVerticalAlignment = OxyPlot.VerticalAlignment.Bottom, Stroke = OxyColors.Transparent,
                        TextColor = ThemeColor("Text.Primary"), FontSize = 11
                    });
            }
            return model;
        }
        private void Pdf_Click(object s, RoutedEventArgs e)
        {
            if (!PdfButton.IsEnabled) return;
            var dialog = new SaveFileDialog { Filter = "Rapport PDF (*.pdf)|*.pdf", FileName = "BIMaestro-Temps-" + DateTime.Today.ToString("yyyy-MM-dd") + ".pdf" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                TimePdfReport.Write(dialog.FileName, From.SelectedDate?.Date ?? DateTime.Today.AddDays(-14), To.SelectedDate?.Date ?? DateTime.Today, _detail, _detailTotals,
                    "Type : " + ((ComboBoxItem)Kind.SelectedItem).Content + " · Revit : " + Version.SelectedItem + " · Recherche : " + Search.Text + " · Paramètres : " + ParameterSearch.Text, PdfColor(ThemeColor("Brand")));
                MessageBox.Show(this, "Rapport PDF enregistré.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, "Export PDF impossible : " + ex.Message, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        internal static string Duration(double hours) { long minutes = (long)Math.Round(hours * 60, MidpointRounding.AwayFromZero); return (minutes / 60) + " h " + (minutes % 60).ToString("00"); }
        private static string Cell(IRow row, int i, DataFormatter format) => format.FormatCellValue(row.GetCell(i))?.Trim() ?? "";
        private static string First(params string[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        private static string Normalize(string id) => (id ?? "").Split('|').Last().Trim();
        private static bool Contains(string value, string query) => Fold(value).IndexOf(Fold(query), StringComparison.OrdinalIgnoreCase) >= 0;
        private static string Fold(string value) => new string((value ?? "").Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        private static bool TryDate(string date, string time, out DateTime result)
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("fr-FR"), CultureInfo.CurrentCulture })
                if (DateTime.TryParse(date + " " + time, culture, DateTimeStyles.AllowWhiteSpaces, out result)) return true;
            result = default; return false;
        }
        private static double Hours(ICell cell, DataFormatter format)
        {
            if (cell == null) return 0;
            if (cell.CellType == CellType.Numeric) return cell.NumericCellValue * 24;
            string value = format.FormatCellValue(cell);
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts) || TimeSpan.TryParse(value, CultureInfo.GetCultureInfo("fr-FR"), out ts)) return ts.TotalHours;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double days) || double.TryParse(value, NumberStyles.Float, CultureInfo.GetCultureInfo("fr-FR"), out days)) return days * 24;
            return double.NaN;
        }
        internal class Entry { public string Id, Name, Path, Version, Kind, Parameters; public DateTime When; public double Hours; public bool Live; }
        [Obfuscation(Exclude = true, ApplyToMembers = true)]
        internal class Total { public string Id { get; set; } public string Name { get; set; } public string Path { get; set; } public double Hours { get; set; } public string Duration => TimeSeriesDashboardWindow.Duration(Hours); public int Days { get; set; } public DateTime Last { get; set; } public string Versions { get; set; } public double Share { get; set; } public string ShareLabel => Share.ToString("0.#", CultureInfo.CurrentCulture) + " %"; public Brush Brush { get; set; } }
        [Obfuscation(Exclude = true, ApplyToMembers = true)]
        private class ModelChoice : INotifyPropertyChanged
        {
            public string Id { get; set; } public string Name { get; set; } public string Path { get; set; }
            private bool _selected;
            public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}

namespace BIMaestro.Dashboard
{
    // Small vector PDF writer, using the standard PDF Helvetica fonts (WinAnsi).
    // No printer configuration or new runtime dependency is required.
    internal static class TimePdfReport
    {
        private static readonly Encoding Encoding = System.Text.Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string Literal(string value) => (value ?? "").Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ").Replace("\n", " ");
        private static string Short(string value, int max) => string.IsNullOrEmpty(value) ? "—" : value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        private static void Text(StringBuilder page, string value, double x, double y, int size = 10, bool bold = false)
            => page.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(size).Append(" Tf 0.12 0.18 0.26 rg ").Append(Number(x)).Append(' ').Append(Number(y)).Append(" Td (").Append(Literal(value)).Append(") Tj ET\n");
        private static void Rect(StringBuilder page, double x, double y, double width, double height, string color)
            => page.Append(color).Append(" rg ").Append(Number(x)).Append(' ').Append(Number(y)).Append(' ').Append(Number(width)).Append(' ').Append(Number(height)).Append(" re f\n");
        internal static void Write(string path, DateTime start, DateTime end, List<TimeSeriesDashboardWindow.Entry> rows, List<TimeSeriesDashboardWindow.Total> totals, string filters, string brandColor)
        {
            var pages = new List<StringBuilder>();
            StringBuilder page = null;
            double y = 0;
            Action newPage = () => {
                page = new StringBuilder(); pages.Add(page);
                Rect(page, 0, 785, 595, 57, brandColor);
                page.Append("BT /F2 18 Tf 1 1 1 rg 36 806 Td (BIMaestro | Rapport de temps) Tj ET\n");
                Text(page, start.ToString("dd/MM/yyyy") + " au " + end.ToString("dd/MM/yyyy") + " · temps actif", 36, 763, 11);
                Text(page, "Maquette / famille", 36, 721, 10, true); Text(page, "Temps", 350, 721, 10, true); Text(page, "Jours", 432, 721, 10, true); Text(page, "Dernière activité", 477, 721, 9, true);
                y = 699;
            };
            newPage();
            Text(page, "Total : " + TimeSeriesDashboardWindow.Duration(rows.Sum(x => x.Hours)) + " · " + totals.Count + " document(s)", 36, y, 14, true); y -= 25;
            Text(page, "Dont sessions ouvertes : " + TimeSeriesDashboardWindow.Duration(rows.Where(x => x.Live).Sum(x => x.Hours)), 36, y); y -= 18;
            foreach (string line in Wrap(filters, 96)) { Text(page, line, 36, y, 9); y -= 14; }
            Text(page, "Week-ends inclus · historique local et sessions ouvertes de cette instance.", 36, y, 9); y -= 30;
            Text(page, "Répartition du temps par maquette", 36, y, 12, true); y -= 22;
            double max = Math.Max(0.001, totals.Max(x => x.Hours));
            foreach (var total in totals.Take(8))
            {
                Text(page, Short(total.Name, 44), 36, y, 9);
                Rect(page, 272, y - 1, 190 * total.Hours / max, 8, "0.18 0.50 0.93");
                Text(page, total.Duration, 477, y, 9); y -= 22;
            }
            y -= 20;
            Text(page, "Détail de la sélection", 36, y, 12, true); y -= 24;
            foreach (var total in totals)
            {
                var nameLines = Wrap(total.Name, 49).ToList();
                var pathLines = Wrap(total.Path, 105).ToList();
                int height = Math.Max(1, nameLines.Count) * 14 + pathLines.Count * 11 + 28;
                // Long paths are split across pages rather than silently truncated.
                if (y - Math.Min(height, 600) < 60) newPage();
                double top = y;
                foreach (string line in nameLines) { if (y < 60) newPage(); Text(page, line, 36, y, 10, true); y -= 14; }
                Text(page, total.Duration, 350, top, 10); Text(page, total.Days.ToString(), 432, top, 10); Text(page, total.Last.ToString("dd/MM/yyyy"), 477, top, 9);
                foreach (string line in pathLines) { if (y < 60) newPage(); Text(page, line, 36, y, 8); y -= 11; }
                if (y < 60) newPage();
                Text(page, "Revit : " + total.Versions, 36, y, 8); y -= 24;
            }
            for (int i = 0; i < pages.Count; i++) Text(pages[i], "Généré le " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " · Page " + (i + 1) + " / " + pages.Count, 36, 30, 9);
            var objects = new List<byte[]>();
            Action<string> add = value => objects.Add(Encoding.GetBytes(value));
            add("<< /Type /Catalog /Pages 2 0 R >>");
            add("<< /Type /Pages /Count " + pages.Count + " /Kids [" + string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => (5 + i * 2) + " 0 R")) + "] >>");
            add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            for (int i = 0; i < pages.Count; i++)
            {
                add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents " + (6 + i * 2) + " 0 R >>");
                byte[] content = Encoding.GetBytes(pages[i].ToString());
                add("<< /Length " + content.Length + " >>\nstream\n" + pages[i] + "endstream");
            }
            using var output = new MemoryStream();
            Action<string> write = value => { var bytes = Encoding.GetBytes(value); output.Write(bytes, 0, bytes.Length); };
            write("%PDF-1.4\n%âãÏÓ\n");
            var offsets = new List<long>();
            for (int i = 0; i < objects.Count; i++) { offsets.Add(output.Position); write((i + 1) + " 0 obj\n"); output.Write(objects[i], 0, objects[i].Length); write("\nendobj\n"); }
            long xref = output.Position;
            write("xref\n0 " + (objects.Count + 1) + "\n0000000000 65535 f \n");
            foreach (long offset in offsets) write(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
            write("trailer\n<< /Size " + (objects.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            File.WriteAllBytes(path, output.ToArray());
        }
        private static IEnumerable<string> Wrap(string text, int width)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            for (int i = 0; i < text.Length; i += width) yield return text.Substring(i, Math.Min(width, text.Length - i));
        }
    }
}

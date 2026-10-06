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
using FontWeights = System.Windows.FontWeights;
using LineStyle = OxyPlot.LineStyle;

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

        public TimeSeriesDashboardWindow(string currentDocumentPath = null)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            OverviewPlot.SizeChanged += (s, e) => FitChart(OverviewPlot);
            DetailPlot.SizeChanged += (s, e) => FitChart(DetailPlot);
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
            })).Where(x => x.Hours > 0 && !double.IsNaN(x.Hours) && !double.IsInfinity(x.Hours)).ToList();
            RebuildChoices();
            _batch = true;
            var version = Version.SelectedItem as string;
            Version.ItemsSource = new[] { "Toutes" }.Concat(_all.Select(x => x.Version).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
            Version.SelectedItem = version ?? "Toutes";
            if (Version.SelectedIndex < 0) Version.SelectedIndex = 0;
            _batch = false;
            Refresh();
        }
        private void RebuildChoices()
        {
            var old = _choices.ToDictionary(x => x.Id, x => x.Selected, StringComparer.OrdinalIgnoreCase);
            bool selectNew = old.Count == 0 || old.Values.All(value => value);
            _choices = _all.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => new ModelChoice {
                Id = g.Key, Name = First(g.Last().Name, g.Key), Path = g.Last().Path,
                Selected = old.TryGetValue(g.Key, out bool value) ? value : selectNew
            }).OrderBy(x => x.Name).ToList();
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
            OverviewWorkedDays.Text = workedDays + (workedDays == 1 ? " jour avec activité" : " jours avec activité");
            double previous = _all.Where(x => x.When.Date >= start.AddDays(-_days) && x.When.Date < start).Sum(x => x.Hours);
            double difference = recent.Sum(x => x.Hours) - previous;
            OverviewTrend.Text = previous > 0 ? (Math.Abs(difference) < 1.0 / 120 ? "Stable" : (difference >= 0 ? "+" : "−") + Duration(Math.Abs(difference))) + " sur la période précédente" : "Aucune activité sur la période précédente";
            Seven.FontWeight = _days == 7 ? FontWeights.Bold : FontWeights.Normal;
            Fifteen.FontWeight = _days == 15 ? FontWeights.Bold : FontWeights.Normal;
            Seven.SetResourceReference(Control.BorderBrushProperty, _days == 7 ? "Focus" : "Border");
            Fifteen.SetResourceReference(Control.BorderBrushProperty, _days == 15 ? "Focus" : "Border");
            OverviewPlot.Model = Chart(recent, start, DateTime.Today);
            FitChart(OverviewPlot);
            OverviewTable.ItemsSource = totals;
            OverviewEmpty.Visibility = totals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            OverviewPlot.Visibility = totals.Count == 0 ? Visibility.Hidden : Visibility.Visible;
            OverviewDocumentsEmpty.Visibility = OverviewEmpty.Visibility;
            _batch = true;
            DateTime from = From.SelectedDate?.Date ?? DateTime.Today.AddDays(-14), to = To.SelectedDate?.Date ?? DateTime.Today;
            var available = new HashSet<string>(_all.Where(x => x.When.Date >= from && x.When.Date <= to && MatchesFilters(x)).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
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
            int activeDays = _detail.Select(x => x.When.Date).Distinct().Count();
            DetailContext.Text = valid ? _detailTotals.Count + (_detailTotals.Count == 1 ? " document" : " documents") + " · " + activeDays + (activeDays == 1 ? " jour actif" : " jours actifs") + " · " + start.ToString("dd/MM/yyyy") + " au " + end.ToString("dd/MM/yyyy") : "La date de début doit précéder la date de fin.";
            DetailChartTitle.Text = (end - start).Days < 31 ? "Voir le temps actif par jour" : "Voir l’évolution du temps actif · périodes regroupées";
            SelectionCount.Text = Picker.Items.Cast<ModelChoice>().Count(x => x.Selected) + " / " + Picker.Items.Count + " documents sélectionnés";
            DetailPlot.Model = valid ? Chart(_detail, start, end) : new PlotModel();
            FitChart(DetailPlot);
            DetailEmpty.Visibility = valid && _detail.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DetailDocumentsEmpty.Text = !valid ? "Corrigez la période pour afficher les documents." : ids.Count == 0 ? "Cochez un document pour afficher son temps actif." : "Aucune activité pour cette période et ces filtres.";
            DetailDocumentsEmpty.Visibility = _detail.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DetailPlot.Visibility = _detail.Count == 0 ? Visibility.Hidden : Visibility.Visible;
            PdfButton.IsEnabled = valid && _detail.Count > 0 && _loadError == null;
        }

        private static List<Total> Totals(List<Entry> rows)
        {
            double sum = rows.Sum(x => x.Hours);
            return rows.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => new Total {
                Id = g.Key, Name = First(g.Last().Name, g.Key), Path = g.Last().Path,
                Hours = g.Sum(x => x.Hours), Days = g.Select(x => x.When.Date).Distinct().Count(), Last = g.Max(x => x.When),
                Versions = string.Join(", ", g.Select(x => x.Version).Distinct()), Share = sum > 0 ? 100 * g.Sum(x => x.Hours) / sum : 0
            }).OrderByDescending(x => x.Hours).ThenBy(x => x.Name).ToList();
        }
        private OxyColor ThemeColor(string key)
        {
            var brush = TryFindResource(key) as SolidColorBrush;
            if (brush == null) throw new InvalidOperationException("Couleur du thème introuvable : " + key);
            return OxyColor.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
        }
        private static string PdfColor(OxyColor color) => string.Join(" ", new[] { color.R, color.G, color.B }.Select(x => (x / 255.0).ToString("0.###", CultureInfo.InvariantCulture)));
        private static void FitChart(OxyPlot.Wpf.PlotView plot)
        {
            var vertical = plot.Model?.Axes.OfType<LinearAxis>().FirstOrDefault(axis => axis.Position == AxisPosition.Left);
            if (vertical == null || plot.ActualHeight <= 0) return;
            int intervals = Math.Max(1, Math.Min(5, (int)((plot.ActualHeight - 64) / 28)));
            double desired = vertical.Maximum / intervals;
            double unit = Math.Pow(10, Math.Floor(Math.Log10(desired)));
            vertical.MajorStep = desired <= .25 ? .25 : desired <= .5 ? .5 : desired <= 1 ? 1 : new[] { 1.0, 2, 2.5, 5, 10 }.Select(x => x * unit).First(x => x >= desired);
            plot.InvalidatePlot(false);
        }
        private PlotModel Chart(List<Entry> rows, DateTime start, DateTime end)
        {
            var model = new PlotModel { PlotAreaBorderColor = OxyColors.Transparent, TextColor = ThemeColor("Text.Secondary"), Background = ThemeColor("Surface"), IsLegendVisible = false, DefaultFontSize = 11, PlotMargins = new OxyThickness(58, 16, 12, 48) };
            int days = (end - start).Days + 1;
            int bucketDays = days <= 31 ? 1 : days <= 180 ? 7 : Math.Max(30, (int)Math.Ceiling(days / 60.0));
            int count = (int)Math.Ceiling(days / (double)bucketDays);
            var axis = new CategoryAxis { Position = AxisPosition.Bottom, GapWidth = 0.35, Angle = count > 7 ? -45 : 0, MajorStep = Math.Max(1, (int)Math.Ceiling(count / 8.0)), TickStyle = TickStyle.None, IsZoomEnabled = false, IsPanEnabled = false };
            for (int i = 0; i < count; i++) axis.Labels.Add(start.AddDays(i * bucketDays).ToString(days <= 7 ? "ddd\ndd" : days <= 15 ? "dd/MM" : days > 365 ? "dd/MM/yy" : "dd/MM", CultureInfo.GetCultureInfo("fr-FR")));
            model.Axes.Add(axis);
            var hours = rows.GroupBy(x => (x.When.Date - start).Days / bucketDays).ToDictionary(x => x.Key, x => x.Sum(y => y.Hours));
            double peak = hours.Count == 0 ? 0 : hours.Values.Max();
            double step = peak <= 1 ? .25 : peak <= 2 ? .5 : peak <= 4 ? 1 : peak <= 8 ? 2 : Math.Pow(2, Math.Ceiling(Math.Log(peak / 4, 2)));
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Maximum = Math.Max(step * 4, Math.Ceiling(peak * 1.25 / step) * step), MajorStep = step,
                LabelFormatter = value => value == 0 ? "0" : value < 1 ? Math.Round(value * 60) + " min" : Duration(value).Replace(" h 00", " h"), MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = ThemeColor("Divider"), TickStyle = TickStyle.None, IsZoomEnabled = false, IsPanEnabled = false });
            var series = new RectangleBarSeries { Title = "Temps actif", FillColor = ThemeColor("Brand"), StrokeThickness = 0, LabelFormatString = null, TrackerFormatString = "{0}\n{7}" };
            for (int i = 0; i < count; i++)
            {
                hours.TryGetValue(i, out double h);
                var bucketStart = start.AddDays(i * bucketDays);
                var bucketEnd = end.AddDays(-Math.Max(0, (end - bucketStart).Days - bucketDays + 1));
                string period = bucketStart.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
                if (bucketDays > 1) period += " au " + bucketEnd.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
                if (h > 0) series.Items.Add(new RectangleBarItem(i - .3, 0, i + .3, h) { Title = period + " · " + Duration(h) });
            }
            model.Series.Add(series);
            if (days <= 15)
            {
                for (int i = 0; i < count; i++)
                    if (hours.TryGetValue(i, out double h) && h > 0 && count <= 7) model.Annotations.Add(new OxyPlot.Annotations.TextAnnotation {
                        Text = Duration(h), TextPosition = new DataPoint(i, h),
                        TextVerticalAlignment = OxyPlot.VerticalAlignment.Bottom, Stroke = OxyColors.Transparent,
                        TextColor = ThemeColor("Text.Primary"), FontSize = 11
                    });
            }
            return model;
        }
        private void Pdf_Click(object s, RoutedEventArgs e)
        {
            if (!PdfButton.IsEnabled) return;
            // SaveFileDialog runs a nested message loop: the refresh timer may fire.
            var from = From.SelectedDate?.Date ?? DateTime.Today.AddDays(-14);
            var to = To.SelectedDate?.Date ?? DateTime.Today;
            var rows = _detail.ToList();
            var totals = _detailTotals.ToList();
            var filters = new List<string> { "Type : " + ((ComboBoxItem)Kind.SelectedItem).Content, "Revit : " + Version.SelectedItem };
            if (!string.IsNullOrWhiteSpace(Search.Text)) filters.Add("Recherche : " + Search.Text);
            if (!string.IsNullOrWhiteSpace(ParameterSearch.Text)) filters.Add("Paramètres : " + ParameterSearch.Text);
            var dialog = new SaveFileDialog { Filter = "Rapport PDF (*.pdf)|*.pdf", FileName = "BIMaestro-Temps-" + DateTime.Today.ToString("yyyy-MM-dd") + ".pdf" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                TimePdfReport.Write(dialog.FileName, from, to, rows, totals,
                    string.Join(" · ", filters), PdfColor(ThemeColor("Brand")));
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
            foreach (var culture in new[] { CultureInfo.GetCultureInfo("fr-FR"), CultureInfo.InvariantCulture, CultureInfo.CurrentCulture })
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
        internal class Total { public string Id { get; set; } public string Name { get; set; } public string Path { get; set; } public double Hours { get; set; } public string Duration => TimeSeriesDashboardWindow.Duration(Hours); public int Days { get; set; } public DateTime Last { get; set; } public string Versions { get; set; } public double Share { get; set; } public string ShareLabel => Share.ToString("0.#", CultureInfo.CurrentCulture) + " %"; }
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
        // Standard Helvetica/Helvetica-Bold WinAnsi advances (1/1000 em).
        // Use the same metrics as the PDF fonts, including accents, when wrapping.
        private static readonly int[] RegularWidths = { 761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584,761,556,761,222,556,333,1000,556,556,333,1000,667,333,1000,761,611,761,761,222,222,333,333,350,556,1000,333,1000,500,333,944,761,500,667,278,333,556,556,556,556,260,556,333,737,370,556,584,333,737,333,400,584,333,333,333,556,537,278,333,333,365,556,834,834,834,611,667,667,667,667,667,667,1000,722,667,667,667,667,278,278,278,278,722,722,778,778,778,778,778,584,778,722,722,722,722,667,667,611,556,556,556,556,556,556,889,500,556,556,556,556,278,278,278,278,556,556,556,556,556,556,556,584,611,556,556,556,556,500,556,500 };
        private static readonly int[] BoldWidths = { 761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,761,278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,333,611,556,778,556,556,500,389,280,389,584,761,556,761,278,556,500,1000,556,556,333,1000,667,333,1000,761,611,761,761,278,278,500,500,350,556,1000,333,1000,556,333,944,761,500,667,278,333,556,556,556,556,280,556,333,737,370,556,584,333,737,333,400,584,333,333,333,611,556,278,333,333,365,556,834,834,834,611,722,722,722,722,722,722,1000,722,667,667,667,667,278,278,278,278,722,722,778,778,778,778,778,584,778,722,722,722,722,667,667,611,556,556,556,556,556,556,889,556,556,556,556,556,278,278,278,278,611,611,611,611,611,611,611,584,611,611,611,611,611,556,611,556 };
        private static double Width(string value, int size, bool bold = false)
            => Encoding.GetBytes(value ?? "").Sum(c => (bold ? BoldWidths : RegularWidths)[c]) * size / 1000.0;
        private static string Short(string value, double width, int size, bool bold = false)
        {
            value = string.IsNullOrWhiteSpace(value) ? "-" : value;
            if (Width(value, size, bold) <= width) return value;
            while (value.Length > 0 && Width(value + "…", size, bold) > width) value = value.Substring(0, value.Length - 1);
            return value + "…";
        }
        private static void RightText(StringBuilder page, string value, double right, double y, int size = 10, bool bold = false)
            => Text(page, value, right - Width(value, size, bold), y, size, bold);
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
                y = 724;
            };
            Action detailHeading = () => {
                Text(page, "Détail de la sélection", 36, y, 12, true); y -= 28;
                Rect(page, 36, y - 9, 523, 25, "0.96 0.97 0.98");
                Text(page, "Maquette / famille", 42, y, 9, true);
                RightText(page, "Temps actif", 400, y, 9, true);
                RightText(page, "Jours", 447, y, 9, true);
                RightText(page, "Dernière activité", 553, y, 9, true);
                y -= 30;
            };
            Action detailPage = () => { newPage(); detailHeading(); };
            newPage();
            Text(page, "Total : " + TimeSeriesDashboardWindow.Duration(rows.Sum(x => x.Hours)) + " · " + totals.Count + (totals.Count == 1 ? " document" : " documents"), 36, y, 16, true); y -= 26;
            Text(page, "Dont sessions ouvertes : " + TimeSeriesDashboardWindow.Duration(rows.Where(x => x.Live).Sum(x => x.Hours)), 36, y); y -= 18;
            foreach (string line in Wrap(filters, 523, 9)) { if (y < 70) newPage(); Text(page, line, 36, y, 9); y -= 14; }
            if (y < 110) newPage();
            Text(page, "Week-ends inclus · historique local et sessions ouvertes de cette instance.", 36, y, 9); y -= 30;
            Text(page, "Répartition du temps par document", 36, y, 12, true); y -= 22;
            double max = Math.Max(0.001, totals.Select(x => x.Hours).DefaultIfEmpty(0).Max());
            foreach (var total in totals.Take(8))
            {
                if (y < 70) { newPage(); Text(page, "Répartition du temps par document (suite)", 36, y, 12, true); y -= 26; }
                Text(page, Short(total.Name, 224, 9), 36, y, 9);
                Rect(page, 272, y - 1, 190 * total.Hours / max, 8, brandColor);
                RightText(page, total.Duration, 559, y, 9); y -= 22;
            }
            if (totals.Count > 8) { Text(page, "Les 8 documents les plus utilisés ; la liste complète figure ci-dessous.", 36, y, 9); y -= 18; }
            if (totals.Count == 0) { Text(page, "Aucune activité sur cette sélection.", 36, y, 10); y -= 22; }
            y -= 20;
            var first = totals.FirstOrDefault();
            int firstHeight = first == null ? 0 : Wrap(string.IsNullOrWhiteSpace(first.Name) ? "Sans nom" : first.Name, 290, 10, true).Count() * 14
                + (Wrap(first.Path, 523, 8).Count() + Wrap("Revit : " + first.Versions, 523, 8).Count()) * 11 + 14;
            // Keep the section heading, column labels and first row together.
            if (y - 58 - Math.Min(firstHeight, 606) < 60) newPage();
            detailHeading();
            foreach (var total in totals)
            {
                var nameLines = Wrap(string.IsNullOrWhiteSpace(total.Name) ? "Sans nom" : total.Name, 290, 10, true).ToList();
                var pathLines = Wrap(total.Path, 523, 8).ToList();
                var versionLines = Wrap("Revit : " + total.Versions, 523, 8).ToList();
                int height = nameLines.Count * 14 + (pathLines.Count + versionLines.Count) * 11 + 14;
                // Long paths are split across pages rather than silently truncated.
                if (y - Math.Min(height, 606) < 60) detailPage();
                RightText(page, total.Duration, 400, y, 10);
                RightText(page, total.Days.ToString(), 447, y, 10);
                RightText(page, total.Last.ToString("dd/MM/yyyy"), 559, y, 9);
                foreach (string line in nameLines) { if (y < 60) detailPage(); Text(page, line, 36, y, 10, true); y -= 14; }
                foreach (string line in pathLines.Concat(versionLines)) { if (y < 60) detailPage(); Text(page, line, 36, y, 8); y -= 11; }
                Rect(page, 36, y - 3, 523, 0.5, "0.9 0.92 0.94");
                y -= 14;
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
        private static IEnumerable<string> Wrap(string text, double width, int size, bool bold = false)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            while (text.Length > 0)
            {
                int length = 0;
                double advance = 0;
                int breakAt = 0;
                while (length < text.Length)
                {
                    advance += Width(text.Substring(length, 1), size, bold);
                    if (advance > width && length > 0) break;
                    char c = text[length++];
                    if (char.IsWhiteSpace(c) || c == '\\' || c == '/' || c == '_' || c == '-') breakAt = length;
                }
                if (length < text.Length && breakAt > 0) length = breakAt;
                yield return text.Substring(0, length);
                text = text.Substring(length);
            }
        }
    }
}

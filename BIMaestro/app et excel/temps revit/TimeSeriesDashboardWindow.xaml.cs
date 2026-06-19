using Newtonsoft.Json;
using OfficeOpenXml;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using OxyPlot.Wpf;
using System.Linq;
using System.Reflection;            // <-- IMPORTANT
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SWM = System.Windows.Media;

namespace BIMaestro.Dashboard
{
    // Empêche le renommage/strip de la classe ET de ses membres (handlers XAML, champs x:Name, etc.)
    [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
    public partial class TimeSeriesDashboardWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/analyse?outil=temps-par-projet";
        // ===== FICHIERS =====
        private readonly string _excelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "Historique_Temps_Revit.xlsx");
        private readonly string _prefsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence");
        private string PrefsPath => Path.Combine(_prefsDir, "dashboard_prefs.json");

        // ===== ÉTAT UI =====
        private bool _uiReady = false;

        // ===== OxyPlot =====
        private PlotModel _plotModel;
        private readonly HashSet<string> _hiddenBars = new(StringComparer.OrdinalIgnoreCase);

        // Debounce recherche
        private DispatcherTimer _searchDebounce;

        private readonly string _currentDocumentPath;
        private readonly List<VersionLegendItem> _revitLegendItems = new();

        // ===== Data =====
        private List<LogRow> _rows = new();
        private List<ProjectItem> _projects = new();
        private List<ProjectItem> _displayProjects = new();
        private List<ProjectItem> _filteredProjects = new();
        private Dictionary<string, double> _hoursByProject = new(StringComparer.Ordinal);

        private const int DEFAULT_TOP_N = 20;
        private const int TOP_N_MIN = 1;
        private const int TOP_N_MAX = 100;

        private enum AutoGran { Day, Week, Month }
        private enum DocumentKind { Rvt, Rfa }

        private Prefs _prefs = new();

        public TimeSeriesDashboardWindow(string currentDocumentPath = null)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            _currentDocumentPath = currentDocumentPath;
            Title = "BIMaestro — Temps par type de document";
            AddHotkeys();

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); RefreshSearch(); };

            _dpFrom.SelectedDate = DateTime.Today.AddMonths(-1);
            _dpTo.SelectedDate = DateTime.Today;
            _tgOverview.IsChecked = true;
            _tgCompare.IsChecked = false;
            _tgRvt.IsChecked = true;
            _tgRfa.IsChecked = false;

            Environment.SetEnvironmentVariable("EPPlusLicenseContext", "NonCommercial", EnvironmentVariableTarget.Process);

            _plotModel = new PlotModel { Title = "Temps passé" };
            _plotView.Model = _plotModel;

            LoadData();
            BuildProjectList();
            LoadPrefs();
            ApplyPrefsToUi();

            _uiReady = true;
            RefreshAll();
            RefreshSearch();

            Closing += (s, e) => SavePrefs();
        }

        private void AddHotkeys()
        {
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => FocusSearch()), new KeyGesture(Key.F, ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => ExportPng()), new KeyGesture(Key.E, ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => CopyChartToClipboard()), new KeyGesture(Key.C, ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => ResetFilters()), new KeyGesture(Key.R, ModifierKeys.Control)));
        }

        // ===== Handlers XAML (ils doivent garder leur NOM exact) =====
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d’ouvrir la page d’aide : {ex.Message}", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnDateChanged(object sender, SelectionChangedEventArgs e) { if (!_uiReady) return; RefreshAll(); }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            if (sender == _tgOverview) _tgCompare.IsChecked = false;
            if (sender == _tgCompare) _tgOverview.IsChecked = false;
            DrawChart();
        }

        private void DocTypeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            if (sender == _tgRvt)
            {
                _tgRvt.IsChecked = true;
                _tgRfa.IsChecked = false;
            }
            else if (sender == _tgRfa)
            {
                _tgRfa.IsChecked = true;
                _tgRvt.IsChecked = false;
            }
            RefreshAll();
            RefreshSearch();
        }

        private void Legend_Checked(object sender, RoutedEventArgs e) { if (!_uiReady) return; DrawChart(); }

        private void Search_KeyDown(object sender, KeyEventArgs e)
        {
            if (!_uiReady) return;
            if (e.Key == Key.Escape) { _tbSearch.Text = string.Empty; _tbSearch.Focus(); }
        }
        private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (!_uiReady) return; _searchDebounce.Start(); }
        private void ClearSearch_Click(object sender, RoutedEventArgs e) { if (!_uiReady) return; _tbSearch.Text = ""; _tbSearch.Focus(); }

        private void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            TryOpenLocation();
        }

        private void Suggestion_Click(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            if (sender is Button btn)
            {
                if (btn.DataContext is ProjectItem item)
                    _tbSearch.Text = item.BaseName ?? string.Empty;
                else
                    _tbSearch.Text = btn.Content?.ToString() ?? string.Empty;

                string path = btn.Tag?.ToString();
                TryOpenLocation(path);
            }
        }

        private void Chip7_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; SetRange(DateTime.Today.AddDays(-7), DateTime.Today); }
        private void Chip30_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; SetRange(DateTime.Today.AddDays(-30), DateTime.Today); }
        private void ChipYtd_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; SetRange(new DateTime(DateTime.Today.Year, 1, 1), DateTime.Today); }
        private void Chip12m_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; SetRange(DateTime.Today.AddYears(-1), DateTime.Today); }
        private void ChipAll_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; SetRange(null, null); }

        private void BtnTopNMinus_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; AdjustTopN(-1); }
        private void BtnTopNPlus_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; AdjustTopN(+1); }
        private void TopN_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !char.IsDigit(e.Text, 0);
        private void TopN_TextChanged(object sender, TextChangedEventArgs e) { if (!_uiReady) return; if (int.TryParse(_tbTopN.Text, out int n)) SetTopN(n); }
        private void ChipTop_Click(object sender, RoutedEventArgs e) { if (!_uiReady) return; if (sender is Button b && int.TryParse(b.Content?.ToString(), out int n)) SetTopN(n); }

        private void OpenExcel_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; OpenExcel(); }
        private void ExportPng_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; ExportPng(); }
        private void Reset_Click(object s, RoutedEventArgs e) { if (!_uiReady) return; ResetFilters(); }

        private void SetRange(DateTime? from, DateTime? to) { _dpFrom.SelectedDate = from; _dpTo.SelectedDate = to; RefreshAll(); }
        private void FocusSearch() => _tbSearch.Focus();

        // ===== DATA =====
        private void LoadData()
        {
            _rows.Clear();
            if (!File.Exists(_excelPath)) return;

            using var pkg = new ExcelPackage(new FileInfo(_excelPath));
            var ws = pkg.Workbook.Worksheets["Historique_Temps_Revit"];
            if (ws == null || ws.Dimension == null) return;

            int r0 = ws.Dimension.Start.Row + 1, rn = ws.Dimension.End.Row;
            for (int r = r0; r <= rn; r++)
            {
                string ev = (ws.Cells[r, 1].Value ?? "").ToString();
                if (string.IsNullOrWhiteSpace(ev)) continue;
                string docId = NormalizeDocumentId((ws.Cells[r, 2].Value ?? "").ToString());
                string docName = (ws.Cells[r, 3].Value ?? "").ToString();
                string revitVer = (ws.Cells[r, 4].Value ?? "").ToString();
                string dateStr = (ws.Cells[r, 5].Value ?? "").ToString();
                string timeStr = (ws.Cells[r, 6].Value ?? "").ToString();
                object durObj = ws.Cells[r, 7].Value;
                _rows.Add(new LogRow
                {
                    Event = ev,
                    DocumentId = docId,
                    GroupId = docId,
                    DocumentName = docName,
                    RevitVersion = revitVer,
                    When = ParseDateTimeFlexible(dateStr, timeStr),
                    Duration = ParseDurationFlexible(durObj)
                });
            }
        }

        private DocumentKind GetActiveKind() => (_tgRfa?.IsChecked == true) ? DocumentKind.Rfa : DocumentKind.Rvt;

        private static DocumentKind GetKind(string documentId, string documentName)
        {
            string id = (documentId ?? string.Empty).ToLowerInvariant();
            string name = (documentName ?? string.Empty).ToLowerInvariant();
            if (id.EndsWith(".rfa") || name.EndsWith(".rfa")) return DocumentKind.Rfa;
            return DocumentKind.Rvt;
        }

        private bool MatchesDocumentKind(ProjectItem item) => MatchesDocumentKind(item?.LocationId ?? item?.DocumentId, item?.Name);

        private bool MatchesDocumentKind(string documentId, string documentName)
        {
            var active = GetActiveKind();
            return GetKind(documentId, documentName) == active;
        }

        private void BuildProjectList()
        {
            _projects = (_rows ?? Enumerable.Empty<LogRow>())
                .Where(r => !string.IsNullOrWhiteSpace(r.GroupId))
                .GroupBy(r => r.GroupId)
                .Select(g =>
                {
                    var ordered = g.OrderByDescending(r => r.When)
                        .ThenByDescending(r => GetEventPriority(r.Event))
                        .ToList();

                    var latestEntry = ordered.FirstOrDefault();
                    var latestOpenEntry = ordered.FirstOrDefault(r =>
                        string.Equals(r.Event, "Ouvert", StringComparison.OrdinalIgnoreCase));
                    var versionSource = latestOpenEntry ?? latestEntry;

                    string id = g.Key;
                    string name = string.IsNullOrWhiteSpace(latestEntry?.DocumentName) ? "(sans nom)" : latestEntry.DocumentName;
                    string locationId = latestEntry?.DocumentId ?? id;
                    return new ProjectItem
                    {
                        DocumentId = id,
                        LocationId = locationId,
                        Name = name,
                        BaseName = GetBaseName(name, locationId),
                        Folder = GetLastFolder(locationId),
                        Tail = SafeDocIdTail(locationId),
                        RevitVersion = NormalizeRevitVersion(versionSource?.RevitVersion),
                        RevitVersionLabel = BuildRevitVersionLabel(versionSource?.RevitVersion),
                        RevitVersionBrush = GetVersionBrush(versionSource?.RevitVersion),
                        Hours = 0,
                        LastSeen = latestEntry?.When ?? DateTime.MinValue
                    };
                }).ToList();

            BuildRevitLegendItems();
        }

        // ===== REFRESH =====
        private void RefreshAll()
        {
            DateTime? d0 = _dpFrom.SelectedDate;
            DateTime? d1 = _dpTo.SelectedDate?.AddDays(1).AddTicks(-1);

            _hoursByProject = _rows
                .Where(r => r.Event.Equals("Fermé", StringComparison.OrdinalIgnoreCase))
                .Where(r => MatchesDocumentKind(r.DocumentId, r.DocumentName))
                .Where(r => !d0.HasValue || r.When >= d0.Value)
                .Where(r => !d1.HasValue || r.When <= d1.Value)
                .GroupBy(r => r.GroupId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Duration.TotalHours), StringComparer.Ordinal);

            BuildDisplayProjects();
            DrawChart();
            UpdateKpis();
        }

        private void BuildDisplayProjects()
        {
            IEnumerable<ProjectItem> seq = _projects ?? Enumerable.Empty<ProjectItem>();
            seq = seq.Where(MatchesDocumentKind);

            foreach (var p in seq)
                p.Hours = _hoursByProject.TryGetValue(p.DocumentId, out double h) ? h : 0.0;

            _displayProjects = seq.OrderByDescending(p => p.Hours)
                                  .ThenBy(p => p.BaseName)
                                  .ThenBy(p => p.Folder)
                                  .ToList();
        }


        private void RefreshSearch()
        {
            string q = RemoveDiacritics((_tbSearch?.Text ?? "").Trim());
            IEnumerable<ProjectItem> seq = (_projects ?? Enumerable.Empty<ProjectItem>()).Where(MatchesDocumentKind);

            foreach (var p in seq)
                p.Hours = _hoursByProject.TryGetValue(p.DocumentId, out double h) ? h : 0.0;

            if (!string.IsNullOrEmpty(q))
            {
                seq = seq.Where(p =>
                    RemoveDiacritics(p.BaseName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    RemoveDiacritics(p.Folder ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    RemoveDiacritics(p.Tail ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                         .OrderByDescending(p => p.LastSeen)
                         .ThenByDescending(p => p.Hours)
                         .ThenBy(p => p.BaseName);

                _filteredProjects = seq.Take(30).ToList();
                if (_lblCount != null) _lblCount.Text = _filteredProjects.Count + " résultat(s)";
                if (_icSuggestions != null) _icSuggestions.ItemsSource = _filteredProjects.Take(15).ToList();
            }
            else
            {
                _filteredProjects = new List<ProjectItem>();
                if (_lblCount != null) _lblCount.Text = "Commencez à taper pour afficher des raccourcis";
                if (_icSuggestions != null) _icSuggestions.ItemsSource = null;
            }
        }

        // ===== CHART =====
        private void UpdateLegendLayout()
        {
            bool showLegend = _cbLegend?.IsChecked == true;

            if (_legendBorder != null)
                _legendBorder.Visibility = showLegend ? Visibility.Visible : Visibility.Collapsed;

            if (_colLegend != null)
                _colLegend.Width = showLegend ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);

            if (_chartBorder != null)
                _chartBorder.SetValue(Grid.ColumnSpanProperty, showLegend ? 1 : 2);
        }

        private void DrawChart()
        {
            if (_plotView == null) return;

            UpdateLegendLayout();

            var model = new PlotModel();


            var selected = (_displayProjects?.Count > 0 ? _displayProjects : _projects.Where(MatchesDocumentKind)).ToList();

            DateTime? d0 = _dpFrom.SelectedDate;
            DateTime? d1 = _dpTo.SelectedDate?.AddDays(1).AddTicks(-1);

            IEnumerable<LogRow> closed = _rows
                .Where(r => r.Event.Equals("Fermé", StringComparison.OrdinalIgnoreCase))
                .Where(r => MatchesDocumentKind(r.DocumentId, r.DocumentName));
            if (d0.HasValue) closed = closed.Where(r => r.When >= d0.Value);
            if (d1.HasValue) closed = closed.Where(r => r.When <= d1.Value);

            int topN = GetTopN();
            var gran = ChooseAutoGraninality(d0 ?? DateTime.MinValue, d1 ?? DateTime.MaxValue);

            if (_tgOverview.IsChecked == true)
            {
                var totals = closed
                    .Where(r => selected.Any(s => s.DocumentId == r.GroupId))
                    .GroupBy(r => r.GroupId)
                    .Select(g => new
                    {
                        DocId = g.Key,
                        Hours = g.Sum(x => x.Duration.TotalHours),
                        Name = _projects.FirstOrDefault(p => p.DocumentId == g.Key)?.BaseName ?? "(?)"
                    })
                    .OrderByDescending(x => x.Hours)
                    .ToList();

                var top = totals.Take(topN).ToList();
                double others = Math.Max(0, totals.Skip(topN).Sum(x => x.Hours));
                if (others > 0.0001) top.Add(new { DocId = "others", Hours = others, Name = "Autres" });

                var shown = top.Where(t => !_hiddenBars.Contains(t.DocId ?? t.Name)).ToList();

                var catAxis = new CategoryAxis { Position = AxisPosition.Bottom, Angle = -50 };
                foreach (var t in shown) catAxis.Labels.Add(Short(t.Name));
                model.Axes.Add(catAxis);

                model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "heures (total)", Minimum = 0 });
                model.Title = $"Temps passé — Aperçu (Top {topN})";

                var rect = new RectangleBarSeries { Title = $"Top {topN}", StrokeThickness = 0.5, FillColor = GetOxyColor(0) };
                for (int i = 0; i < shown.Count; i++)
                {
                    double v = Math.Round(shown[i].Hours, 2);
                    rect.Items.Add(new RectangleBarItem(i - 0.4, 0, i + 0.4, v));
                }
                model.Series.Add(rect);

                if (_cbLegend.IsChecked == true)
                {
                    var oc = GetOxyColor(0);
                    var br = new SWM.SolidColorBrush(SWM.Color.FromArgb(oc.A, oc.R, oc.G, oc.B)); br.Freeze();
                    var items = new List<LegendItemVM>();
                    foreach (var t in top)
                    {
                        string hiddenKey = t.DocId ?? t.Name;
                        bool currentlyShown = !_hiddenBars.Contains(hiddenKey);
                        var legendItem = new LegendItemVM(t.Name, currentlyShown, () =>
                        {
                            if (_hiddenBars.Contains(hiddenKey)) _hiddenBars.Remove(hiddenKey);
                            else _hiddenBars.Add(hiddenKey);
                            DrawChart();
                        })
                        { Brush = br };

                        items.Add(legendItem);
                    }
                    _legendList.ItemsSource = items;
                }
                else _legendList.ItemsSource = null;
            }
            else
            {
                Func<DateTime, DateTime> bucket = dt =>
                {
                    if (gran == AutoGran.Week) return StartOfIsoWeek(dt);
                    if (gran == AutoGran.Month) return new DateTime(dt.Year, dt.Month, 1);
                    return dt.Date;
                };

                var grouped = closed
                    .Where(r => selected.Any(s => s.DocumentId == r.GroupId))
                    .GroupBy(r => r.GroupId)
                    .OrderByDescending(g => g.Sum(x => x.Duration.TotalHours))
                    .Take(topN)
                    .ToList();

                var allBuckets = grouped
                    .SelectMany(g => g.GroupBy(x => bucket(x.When)).Select(x => x.Key))
                    .Distinct()
                    .OrderBy(k => k)
                    .ToList();

                var labels = allBuckets.Select(k => FormatBucket(k, gran)).ToList();
                var labelIndex = labels.Select((lab, i) => new { lab, i }).ToDictionary(x => x.lab, x => x.i);

                model.Axes.Add(new CategoryAxis { Position = AxisPosition.Bottom, Angle = -50, ItemsSource = labels });
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = $"heures / {(gran == AutoGran.Day ? "jour" : (gran == AutoGran.Week ? "semaine" : "mois"))}", Minimum = 0 });
                model.Title = $"Temps passé — Comparer (Top {topN})";

                int idx = 0;
                var legendItems = new List<LegendItemVM>();

                foreach (var g in grouped)
                {
                    var proj = _projects.FirstOrDefault(p => p.DocumentId == g.Key);
                    string legend = proj != null ? (Short(proj.BaseName) + " — " + Short(proj.Folder)) : g.Key;

                    var ls = new LineSeries
                    {
                        Title = legend,
                        StrokeThickness = 2.5,
                        MarkerType = MarkerType.Circle,
                        MarkerSize = 3.5,
                        Color = GetOxyColor(idx),
                        TrackerFormatString = "{0}\n{1}: {2:0.00} h"
                    };

                    var byBucket = g.GroupBy(x => bucket(x.When))
                                    .Select(x => new { L = FormatBucket(x.Key, gran), Hours = x.Sum(z => z.Duration.TotalHours) })
                                    .OrderBy(x => labelIndex[x.L])
                                    .ToList();

                    foreach (var p in byBucket)
                        ls.Points.Add(new DataPoint(labelIndex[p.L], Math.Round(p.Hours, 2)));

                    model.Series.Add(ls);

                    if (_cbLegend.IsChecked == true)
                    {
                        var oc = GetOxyColor(idx);
                        var br = new SWM.SolidColorBrush(SWM.Color.FromArgb(oc.A, oc.R, oc.G, oc.B)); br.Freeze();
                        legendItems.Add(new LegendItemVM(legend, ls.IsVisible, () => { ls.IsVisible = !ls.IsVisible; _plotView.InvalidatePlot(true); }) { Brush = br });
                    }
                    idx++;
                }

                _legendList.ItemsSource = (_cbLegend.IsChecked == true) ? legendItems : null;
            }

            _plotModel = model;
            _plotView.Model = _plotModel;
            _plotView.InvalidatePlot(true);
        }

        private OxyColor GetOxyColor(int index)
        {
            double hue = (index * 37) % 360;
            var c = HslToColor(hue, 0.55, 0.60);
            return OxyColor.FromArgb(c.A, c.R, c.G, c.B);
        }

        // ===== KPI =====
        private void UpdateKpis()
        {
            var selectedItems = (_displayProjects?.Count > 0 ? _displayProjects : _projects.Where(MatchesDocumentKind)).ToList();
            if (selectedItems.Count == 0) return;

            var selected = new HashSet<string>(selectedItems.Select(p => p.DocumentId));
            DateTime d0 = _dpFrom.SelectedDate ?? DateTime.MinValue;
            DateTime d1 = _dpTo.SelectedDate.HasValue ? _dpTo.SelectedDate.Value.AddDays(1).AddTicks(-1) : DateTime.MaxValue;

            var inRangeAll = _rows.Where(r => r.Event.Equals("Fermé", StringComparison.OrdinalIgnoreCase))
                                  .Where(r => r.When >= d0 && r.When <= d1)
                                  .Where(r => selected.Contains(r.GroupId));

            var inRangeWeekdays = inRangeAll
                .Where(r => r.When.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                .ToList();

            double totalH = inRangeWeekdays.Sum(r => r.Duration.TotalHours);
            int workedWeekdays = inRangeWeekdays.Select(r => r.When.Date).Distinct().Count();
            double avg = workedWeekdays == 0 ? 0.0 : totalH / workedWeekdays;

            int projects = selectedItems.Count;
            _kpiHours.Text = totalH.ToString("0.0") + " h";
            _kpiProjects.Text = projects.ToString();
            _kpiAvg.Text = avg.ToString("0.0") + " h";
        }

        private void ExportPng()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "dashboard_temps.png", Filter = "Image PNG|*.png" };
            if (dlg.ShowDialog() != true) return;

            _plotView.UpdateLayout();
            _plotModel?.InvalidatePlot(true);

            int width = (int)Math.Max(400, _plotView.ActualWidth > 0 ? _plotView.ActualWidth : _plotView.DesiredSize.Width);
            int height = (int)Math.Max(260, _plotView.ActualHeight > 0 ? _plotView.ActualHeight : _plotView.DesiredSize.Height);

            // Ensure a solid background while exporting with the resolution-only overload
            var originalBackground = _plotModel?.Background;
            if (_plotModel != null)
            {
                _plotModel.Background = OxyColors.White;
            }

            PngExporter.Export(_plotModel, dlg.FileName, width, height, 96);

            if (_plotModel != null)
            {
                _plotModel.Background = (OxyColor)originalBackground;
            }

            MessageBox.Show("Exporté : " + dlg.FileName);
        }

        private void CopyChartToClipboard()
        {
            var bmp = RenderVisualToBitmap(_plotView);
            Clipboard.SetImage(bmp);
        }

        private void OpenExcel()
        {
            try
            {
                if (File.Exists(_excelPath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _excelPath, UseShellExecute = true });
                else
                    MessageBox.Show("Fichier Excel introuvable : " + _excelPath);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void TryOpenLocation(string preferredPath = null)
        {
            try
            {
                string path = null;
                if (!string.IsNullOrWhiteSpace(preferredPath))
                    path = preferredPath;
                else
                {
                    var firstMatch = _filteredProjects?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(_tbSearch?.Text) && firstMatch != null)
                        path = firstMatch.LocationId;
                    else if (!string.IsNullOrWhiteSpace(_currentDocumentPath))
                        path = _currentDocumentPath;
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    MessageBox.Show("Aucun chemin détecté pour ouvrir l'emplacement.");
                    return;
                }

                var candidateFolders = ExtractCandidateFolders(path).ToList();
                var openedFolders = new List<string>();

                foreach (var folder in candidateFolders)
                {
                    if (!openedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    {
                        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
                        openedFolders.Add(folder);
                    }
                }

                if (openedFolders.Count == 0)
                {
                    MessageBox.Show("Dossier introuvable pour : " + path);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private static IEnumerable<string> ExtractCandidateFolders(string documentId)
        {
            if (string.IsNullOrWhiteSpace(documentId))
                yield break;

            var parts = documentId.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawPart in parts)
            {
                string part = (rawPart ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                string folder = Directory.Exists(part) ? part : Path.GetDirectoryName(part);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    yield return folder;
            }
        }

        private void ResetFilters()
        {
            _tbSearch.Text = "";
            SetTopN(DEFAULT_TOP_N);
            _dpFrom.SelectedDate = DateTime.Today.AddMonths(-1);
            _dpTo.SelectedDate = DateTime.Today;
            _tgOverview.IsChecked = true; _tgCompare.IsChecked = false;
            _cbLegend.IsChecked = true;
            _tgRvt.IsChecked = true; _tgRfa.IsChecked = false;
            _hiddenBars.Clear();
            RefreshAll();
            RefreshSearch();
        }

        private void LoadPrefs()
        {
            try
            {
                Directory.CreateDirectory(_prefsDir);
                if (!File.Exists(PrefsPath)) return;
                var json = File.ReadAllText(PrefsPath, Encoding.UTF8);
                _prefs = JsonConvert.DeserializeObject<Prefs>(json) ?? new Prefs();
            }
            catch { _prefs = new Prefs(); }
        }

        private void ApplyPrefsToUi()
        {
            try
            {
                if (_prefs.From.HasValue) _dpFrom.SelectedDate = _prefs.From.Value;
                if (_prefs.To.HasValue) _dpTo.SelectedDate = _prefs.To.Value;
                SetTopN(ClampInt(_prefs.TopN <= 0 ? DEFAULT_TOP_N : _prefs.TopN, TOP_N_MIN, TOP_N_MAX));
                _tgOverview.IsChecked = _prefs.Mode != "Compare";
                _tgCompare.IsChecked = _prefs.Mode == "Compare";
                _cbLegend.IsChecked = _prefs.LegendShown;
                _tgRfa.IsChecked = _prefs.DocType == "Rfa";
                _tgRvt.IsChecked = _prefs.DocType != "Rfa";
            }
            catch { }
        }

        private void SavePrefs()
        {
            try
            {
                Directory.CreateDirectory(_prefsDir);
                _prefs.From = _dpFrom.SelectedDate;
                _prefs.To = _dpTo.SelectedDate;
                _prefs.TopN = GetTopN();
                _prefs.Mode = _tgCompare.IsChecked == true ? "Compare" : "Overview";
                _prefs.LegendShown = _cbLegend.IsChecked == true;
                _prefs.DocType = GetActiveKind() == DocumentKind.Rfa ? "Rfa" : "Rvt";
                File.WriteAllText(PrefsPath, JsonConvert.SerializeObject(_prefs, Formatting.Indented), Encoding.UTF8);
            }
            catch { }
        }

        private int GetTopN() { if (!int.TryParse(_tbTopN.Text, out int n)) n = DEFAULT_TOP_N; return ClampInt(n, TOP_N_MIN, TOP_N_MAX); }
        private void SetTopN(int n) { _tbTopN.Text = ClampInt(n, TOP_N_MIN, TOP_N_MAX).ToString(); if (_uiReady) { DrawChart(); SavePrefs(); } }
        private void AdjustTopN(int delta) => SetTopN(GetTopN() + delta);

        // ===== Utils =====
        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in normalized)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static AutoGran ChooseAutoGraninality(DateTime from, DateTime to)
        {
            double span = (to - from).Duration().TotalDays;
            if (span <= 60) return AutoGran.Day;
            if (span <= 420) return AutoGran.Week;
            return AutoGran.Month;
        }

        private static string GetBaseName(string docName, string docId)
        {
            string s = docName ?? "";
            if (string.IsNullOrWhiteSpace(s) || s.IndexOf('.') < 0)
            { try { s = Path.GetFileNameWithoutExtension(docId ?? ""); } catch { } }
            else
            { try { s = Path.GetFileNameWithoutExtension(s); } catch { } }
            return string.IsNullOrWhiteSpace(s) ? "(sans nom)" : s;
        }

        private static string NormalizeDocumentId(string docId)
        {
            if (string.IsNullOrWhiteSpace(docId))
                return docId;

            var parts = docId.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim())
                             .Where(p => !string.IsNullOrWhiteSpace(p))
                             .ToArray();

            return parts.Length > 1 ? parts[parts.Length - 1] : docId.Trim();
        }



        private static int GetEventPriority(string eventName)
        {
            if (string.Equals(eventName, "Ouvert", StringComparison.OrdinalIgnoreCase))
                return 2;

            if (string.Equals(eventName, "Fermé", StringComparison.OrdinalIgnoreCase))
                return 1;

            return 0;
        }

        private void BuildRevitLegendItems()
        {
            _revitLegendItems.Clear();

            var items = (_projects ?? Enumerable.Empty<ProjectItem>())
                .Select(p => NormalizeRevitVersion(p.RevitVersion))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(v => new VersionLegendItem
                {
                    Label = BuildRevitLegendLabel(v),
                    Brush = GetVersionBrush(v)
                })
                .ToList();

            if (items.Count == 0)
            {
                items.Add(new VersionLegendItem
                {
                    Label = "Inconnue",
                    Brush = GetVersionBrush(null)
                });
            }

            _revitLegendItems.AddRange(items);

            if (_icRevitLegend != null)
                _icRevitLegend.ItemsSource = _revitLegendItems.ToList();
        }

        private static string NormalizeRevitVersion(string rawVersion)
        {
            if (string.IsNullOrWhiteSpace(rawVersion))
                return string.Empty;

            string text = rawVersion.Trim();
            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (digits.Length >= 4)
                return digits.Substring(0, 4);

            return text;
        }

        private static string BuildRevitVersionLabel(string rawVersion)
        {
            string normalized = NormalizeRevitVersion(rawVersion);
            return string.IsNullOrWhiteSpace(normalized) ? "Version inconnue" : $"Revit {normalized}";
        }

        private static string BuildRevitLegendLabel(string rawVersion)
        {
            string normalized = NormalizeRevitVersion(rawVersion);
            if (string.IsNullOrWhiteSpace(normalized))
                return "Inconnue";

            return normalized.Length >= 2 ? $"V{normalized.Substring(normalized.Length - 2)}" : $"V{normalized}";
        }

        private static Brush GetVersionBrush(string rawVersion)
        {
            string version = NormalizeRevitVersion(rawVersion);
            string hex = version switch
            {
                "2023" => "#4F46E5",
                "2024" => "#0891B2",
                "2025" => "#16A34A",
                "2026" => "#EA580C",
                "2027" => "#7C3AED",
                _ => "#6B7280"
            };

            return (Brush)new BrushConverter().ConvertFrom(hex);
        }

        private static string GetLastFolder(string id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id)) return "";
                string dir = Path.GetDirectoryName(id);
                return string.IsNullOrEmpty(dir) ? "" : new DirectoryInfo(dir).Name;
            }
            catch { return ""; }
        }

        private static string Short(string s) => string.IsNullOrWhiteSpace(s) ? s : (s.Length <= 28 ? s : s.Substring(0, 25) + "…");

        private static string SafeDocIdTail(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "(id)";
            string s = id;

            int i = s.LastIndexOf('|');
            if (i >= 0 && i < s.Length - 1)
                s = s.Substring(i + 1);

            int j1 = s.LastIndexOf('\\');
            int j2 = s.LastIndexOf('/');
            int j = (j1 > j2) ? j1 : j2;
            if (j >= 0 && j < s.Length - 1)
                s = s.Substring(j + 1);

            return s;
        }


        private static DateTime StartOfIsoWeek(DateTime dt)
        {
            DayOfWeek day = CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(dt);
            int delta = (day == DayOfWeek.Sunday) ? -6 : ((int)DayOfWeek.Monday - (int)day);
            return dt.Date.AddDays(delta);
        }
        private static string FormatBucket(DateTime k, AutoGran gran)
        {
            if (gran == AutoGran.Week) return k.Year.ToString("0000") + "-S" + GetIsoWeekOfYear(k).ToString("00");
            if (gran == AutoGran.Month) return k.ToString("yyyy-MM");
            return k.ToString("yyyy-MM-dd");
        }
        private static int GetIsoWeekOfYear(DateTime time)
        {
            DayOfWeek day = CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(time);
            if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday) time = time.AddDays(3);
            return CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(time, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        }
        private static DateTime ParseDateTimeFlexible(string d, string t)
        {
            string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm:ss", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy H:mm:ss", "yyyy-MM-dd", "dd/MM/yyyy" };
            CultureInfo[] cultures = { CultureInfo.InvariantCulture, new CultureInfo("fr-FR"), CultureInfo.CurrentCulture };
            foreach (var c in cultures)
                foreach (var f in formats)
                    if (DateTime.TryParseExact(string.IsNullOrEmpty(t) ? d : (d + " " + t), f, c, DateTimeStyles.AssumeLocal, out DateTime dt)) return dt;

            if (DateTime.TryParse(d + " " + t, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime dt2)) return dt2;
            return DateTime.Now;
        }
        private static TimeSpan ParseDurationFlexible(object o)
        {
            try
            {
                if (o == null) return TimeSpan.Zero;
                if (o is TimeSpan ts0) return ts0;

                if (o is double d)
                {
                    if (double.IsNaN(d) || double.IsInfinity(d)) return TimeSpan.Zero;
                    d = ClampDouble(d, -365000d, 365000d);
                    return TimeSpan.FromDays(d);
                }
                if (o is DateTime dt)
                {
                    double oa = dt.ToOADate();
                    double frac = oa - Math.Floor(oa);
                    return TimeSpan.FromDays(frac);
                }
                string s = o.ToString().Trim();
                if (TimeSpan.TryParse(s, out TimeSpan ts1)) return ts1;

                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double dInv)
                    || double.TryParse(s, NumberStyles.Any, new CultureInfo("fr-FR"), out dInv)
                    || double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out dInv))
                {
                    dInv = ClampDouble(dInv, -365000d, 365000d);
                    return TimeSpan.FromDays(dInv);
                }
                return TimeSpan.Zero;
            }
            catch { return TimeSpan.Zero; }
        }

        private static System.Drawing.Color HslToColor(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1)), m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            int R = (int)Math.Round((r + m) * 255), G = (int)Math.Round((g + m) * 255), B = (int)Math.Round((b + m) * 255);
            return System.Drawing.Color.FromArgb(255, R, G, B);
        }
        private static SWM.SolidColorBrush ToBrush(System.Drawing.Color c)
        { var mc = SWM.Color.FromArgb(c.A, c.R, c.G, c.B); var br = new SWM.SolidColorBrush(mc); br.Freeze(); return br; }

        private static int ClampInt(int value, int min, int max) { if (value < min) return min; if (value > max) return max; return value; }
        private static double ClampDouble(double value, double min, double max) { if (value < min) return min; if (value > max) return max; return value; }

        private static System.Windows.Media.Imaging.BitmapSource RenderVisualToBitmap(FrameworkElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new Rect(element.DesiredSize));
            int w = Math.Max(1, (int)Math.Round(element.ActualWidth > 0 ? element.ActualWidth : element.DesiredSize.Width));
            int h = Math.Max(1, (int)Math.Round(element.ActualHeight > 0 ? element.ActualHeight : element.DesiredSize.Height));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(element);
            return rtb;
        }
        private static void SaveVisualToPng(FrameworkElement element, string path)
        {
            var bmp = RenderVisualToBitmap(element);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            enc.Save(fs);
        }

        // ===== Models & Prefs =====
        // Ces classes sont utilisées par le XAML (Binding). Il FAUT conserver les noms publics.
        [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
        private class LogRow { public string Event, GroupId, DocumentId, DocumentName, RevitVersion; public DateTime When; public TimeSpan Duration; }

        [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
        private class ProjectItem
        {
            public string DocumentId { get; set; }
            public string LocationId { get; set; }
            public string Name { get; set; }
            public string BaseName { get; set; }
            public string Folder { get; set; }
            public string Tail { get; set; }
            public string RevitVersion { get; set; }
            public string RevitVersionLabel { get; set; }
            public Brush RevitVersionBrush { get; set; }
            public double Hours { get; set; }
            public DateTime LastSeen { get; set; }
        }

        [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
        private class VersionLegendItem
        {
            public string Label { get; set; }
            public Brush Brush { get; set; }
        }

        [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
        private class LegendItemVM : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isChecked;
            public string Label { get; set; }
            public Action Toggle { get; set; }
            public Brush Brush { get; set; } = Brushes.Black;
            public bool IsChecked
            {
                get { return _isChecked; }
                set
                {
                    if (_isChecked == value) return;
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
                    Toggle?.Invoke();
                }
            }
            public LegendItemVM(string label, bool isChecked, Action toggle)
            {
                Label = label;
                Toggle = toggle;
                _isChecked = isChecked;
            }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }

        [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
        private class Prefs
        {
            public DateTime? From { get; set; }
            public DateTime? To { get; set; }
            public string Sort { get; set; } = "HoursDesc";
            public int TopN { get; set; } = DEFAULT_TOP_N;
            public string Mode { get; set; } = "Overview";
            public bool LegendShown { get; set; } = true;
            public string DocType { get; set; } = "Rvt";
        }

        [Obfuscation(Exclude = true, ApplyToMembers = true, StripAfterObfuscation = false)]
        private class RelayCommand : ICommand
        {
            private readonly Action<object> _act; public RelayCommand(Action<object> act) { _act = act; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object p) => true;
            public void Execute(object p) => _act(p);
        }
    }
}

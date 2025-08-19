using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives; // ToggleButton
using System.Windows.Forms.DataVisualization.Charting;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Grid = System.Windows.Controls.Grid;
using SDColor = System.Drawing.Color;
using SWM = System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace BIMaestro.Dashboard
{
    [Transaction(TransactionMode.Manual)]
    public class ShowTimeDashboard : IExternalCommand
    {
        public Result Execute(ExternalCommandData cdata, ref string message, ElementSet elements)
        {
            try { new TimeSeriesDashboardWindow().Show(); return Result.Succeeded; }
            catch (Exception ex) { TaskDialog.Show("Dashboard", ex.ToString()); return Result.Failed; }
        }
    }

    public class TimeSeriesDashboardWindow : Window
    {
        // ===== FICHIERS =====
        private readonly string _excelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "Historique_Temps_Revit.xlsx");
        private readonly string _prefsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence");
        private string PrefsPath { get { return Path.Combine(_prefsDir, "dashboard_prefs.json"); } }

        // ===== UI =====
        private TextBox _tbSearch;
        private Button _btnClearSearch;
        private ComboBox _cbSort;
        private ListView _lvProjects;
        private GridViewColumn _colFolder;
        private CheckBox _cbShowFolder;
        private DatePicker _dpFrom, _dpTo;
        private ToggleButton _tgOverview, _tgCompare;
        private TextBlock _lblCount;
        private WindowsFormsHost _chartHost;
        private Chart _chart;
        private ListView _legendList;
        private CheckBox _cbLegend;

        // KPI
        private TextBlock _kpiHours, _kpiProjects, _kpiAvg;

        // TopN
        private TextBox _tbTopN;
        private Button _btnTopNMinus, _btnTopNPlus;
        private Button _chipTop5, _chipTop10, _chipTop20, _chipTop50, _chipTop100;

        // Debounce
        private DispatcherTimer _searchDebounce;

        // ===== Data =====
        private List<LogRow> _rows = new List<LogRow>();
        private List<ProjectItem> _projects = new List<ProjectItem>();
        private List<ProjectItem> _filteredProjects = new List<ProjectItem>();
        private Dictionary<string, double> _hoursByProject = new Dictionary<string, double>(StringComparer.Ordinal);

        private const int DEFAULT_TOP_N = 20;
        private const int TOP_N_MIN = 1;
        private const int TOP_N_MAX = 100;

        private enum SortMode { HoursDesc, NameAZ }
        private enum AutoGran { Day, Week, Month }

        private Prefs _prefs = new Prefs();

        public TimeSeriesDashboardWindow()
        {
            Title = "BIMaestro — Temps par projet";
            Width = 1220; Height = 780; MinWidth = 1080; MinHeight = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(248, 249, 251));

            // Hotkeys
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => FocusSearch()), new KeyGesture(Key.F, ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => ExportPng()), new KeyGesture(Key.E, ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => CopyChartToClipboard()), new KeyGesture(Key.C, ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => ExportCsv()), new KeyGesture(Key.C, ModifierKeys.Control | ModifierKeys.Shift)));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => ResetFilters()), new KeyGesture(Key.R, ModifierKeys.Control)));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => _lvProjects.SelectAll()), new KeyGesture(Key.A, ModifierKeys.Control)));

            Content = BuildUi();

            LoadPrefs();
            LoadData();
            BuildProjectList();
            ApplyPrefsToUi();
            RefreshAll();

            Closing += (s, e) => SavePrefs();
        }

        private UIElement BuildUi()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // toolbar
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body

            // ===== HEADER KPI =====
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var k1 = MakeKpiCard("Heures sur la période", "0.0 h");
            var k2 = MakeKpiCard("Projets sélectionnés", "0");
            var k3 = MakeKpiCard("Moyenne / jour", "0.0 h");
            _kpiHours = k1.Value; _kpiProjects = k2.Value; _kpiAvg = k3.Value;

            header.Children.Add(k1.Container); Grid.SetColumn(k1.Container, 0);
            header.Children.Add(k2.Container); Grid.SetColumn(k2.Container, 1);
            header.Children.Add(k3.Container); Grid.SetColumn(k3.Container, 2);
            root.Children.Add(header); Grid.SetRow(header, 0);

            // ===== TOOLBAR =====
            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            // → 3 colonnes : dates | mode+TopN | actions (avec Recherche AU-DESSUS des boutons, dont "Ouvrir Excel")
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Dates + chips
            var dates = new StackPanel { Orientation = Orientation.Vertical };
            var datesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            _dpFrom = new DatePicker { SelectedDate = DateTime.Today.AddMonths(-1), Width = 130, Margin = new Thickness(0, 0, 6, 0) };
            _dpTo = new DatePicker { SelectedDate = DateTime.Today, Width = 130 };
            datesRow.Children.Add(_dpFrom); datesRow.Children.Add(_dpTo);
            dates.Children.Add(datesRow);
            var chipsDates = new StackPanel { Orientation = Orientation.Horizontal };
            chipsDates.Children.Add(MakeChip("7 j", () => SetRange(DateTime.Today.AddDays(-7), DateTime.Today)));
            chipsDates.Children.Add(MakeChip("30 j", () => SetRange(DateTime.Today.AddDays(-30), DateTime.Today)));
            chipsDates.Children.Add(MakeChip("YTD", () => SetRange(new DateTime(DateTime.Today.Year, 1, 1), DateTime.Today)));
            chipsDates.Children.Add(MakeChip("12 mois", () => SetRange(DateTime.Today.AddYears(-1), DateTime.Today)));
            chipsDates.Children.Add(MakeChip("Tout", () => SetRange(null, null)));
            dates.Children.Add(chipsDates);
            toolbar.Children.Add(dates); Grid.SetColumn(dates, 0);

            // Mode + TopN
            var mode = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 0, 8, 0) };

            var seg = new Grid();
            seg.ColumnDefinitions.Add(new ColumnDefinition());
            seg.ColumnDefinitions.Add(new ColumnDefinition());
            _tgOverview = MakeSegment("Aperçu (barres)");
            _tgCompare = MakeSegment("Comparer (courbes)");
            _tgOverview.IsChecked = true;
            _tgOverview.Checked += delegate { _tgCompare.IsChecked = false; DrawChart(); };
            _tgCompare.Checked += delegate { _tgOverview.IsChecked = false; DrawChart(); };
            seg.Children.Add(_tgOverview); Grid.SetColumn(_tgOverview, 0);
            seg.Children.Add(_tgCompare); Grid.SetColumn(_tgCompare, 1);
            mode.Children.Add(seg);

            var sortLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            sortLine.Children.Add(new TextBlock { Text = "Trier :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _cbSort = new ComboBox { Width = 140 };
            _cbSort.Items.Add("Heures ↓"); _cbSort.Items.Add("Nom A→Z"); _cbSort.SelectedIndex = 0;
            sortLine.Children.Add(_cbSort);

            sortLine.Children.Add(new TextBlock { Text = "  Top N :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
            _btnTopNMinus = TinyBtn("–", delegate { AdjustTopN(-1); });
            _tbTopN = new TextBox { Width = 44, Height = 26, Text = DEFAULT_TOP_N.ToString(), HorizontalContentAlignment = HorizontalAlignment.Center };
            _tbTopN.PreviewTextInput += (s, e) => e.Handled = !char.IsDigit(e.Text, 0);
            DataObject.AddPastingHandler(_tbTopN, (s, e) => { int tmp; var str = e.DataObject.GetData(typeof(string)) as string; if (!int.TryParse(str, out tmp)) e.CancelCommand(); });
            _tbTopN.TextChanged += (s, e) => { // mise à jour en temps réel
                int n; if (!int.TryParse(_tbTopN.Text, out n)) return;
                _tbTopN.BorderBrush = null; SetTopN(n);
            };
            _btnTopNPlus = TinyBtn("+", delegate { AdjustTopN(+1); });

            sortLine.Children.Add(_btnTopNMinus);
            sortLine.Children.Add(_tbTopN);
            sortLine.Children.Add(_btnTopNPlus);
            mode.Children.Add(sortLine);

            var chipsTop = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _chipTop5 = MakeChip("5", delegate { SetTopN(5); });
            _chipTop10 = MakeChip("10", delegate { SetTopN(10); });
            _chipTop20 = MakeChip("20", delegate { SetTopN(20); });
            _chipTop50 = MakeChip("50", delegate { SetTopN(50); });
            _chipTop100 = MakeChip("100", delegate { SetTopN(100); });
            chipsTop.Children.Add(_chipTop5); chipsTop.Children.Add(_chipTop10); chipsTop.Children.Add(_chipTop20); chipsTop.Children.Add(_chipTop50); chipsTop.Children.Add(_chipTop100);
            mode.Children.Add(chipsTop);

            toolbar.Children.Add(mode); Grid.SetColumn(mode, 1);

            // Actions (vertical) : Recherche AU-DESSUS + boutons (dont "Ouvrir Excel")
            var actions = new Grid { Margin = new Thickness(8, 0, 0, 0) };
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // recherche
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // boutons

            var searchPanel = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchLabel = new TextBlock { Text = "Recherche (nom, dossier, fin de chemin)", Margin = new Thickness(0, 0, 0, 4) };
            var searchStack = new StackPanel { Orientation = Orientation.Vertical };
            searchStack.Children.Add(searchLabel);

            var searchRow = new Grid();
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _tbSearch = new TextBox { Height = 28, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = "Ctrl+F. Échap pour effacer. Accent-insensible." };
            _btnClearSearch = TinyBtn("✕", delegate { _tbSearch.Text = ""; _tbSearch.Focus(); });

            searchRow.Children.Add(_tbSearch); Grid.SetColumn(_tbSearch, 0);
            searchRow.Children.Add(_btnClearSearch); Grid.SetColumn(_btnClearSearch, 1);

            searchStack.Children.Add(searchRow);
            searchPanel.Children.Add(searchStack);
            actions.Children.Add(searchPanel); Grid.SetRow(searchPanel, 0);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var btnOpenExcel = PrimaryButton("Ouvrir Excel", delegate { OpenExcel(); });
            var btnExportCsv = GhostButton("Exporter CSV", delegate { ExportCsv(); });
            var btnExportPng = GhostButton("Exporter PNG", delegate { ExportPng(); });
            var btnReset = GhostButton("Réinitialiser", delegate { ResetFilters(); });
            buttons.Children.Add(btnOpenExcel); buttons.Children.Add(btnExportCsv); buttons.Children.Add(btnExportPng); buttons.Children.Add(btnReset);
            actions.Children.Add(buttons); Grid.SetRow(buttons, 1);

            toolbar.Children.Add(actions); Grid.SetColumn(actions, 2);

            root.Children.Add(toolbar); Grid.SetRow(toolbar, 1);

            // ===== BODY =====
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

            // Left: liste (propre + colonne Dossier masquable)
            var leftCard = Card();
            var left = new Grid { Margin = new Thickness(12) };
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var leftHeader = new StackPanel { Orientation = Orientation.Horizontal };
            leftHeader.Children.Add(new TextBlock { Text = "Projets", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
            _cbShowFolder = new CheckBox { Content = "Afficher dossier", IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
            _cbShowFolder.Checked += delegate { if (_colFolder != null) _colFolder.Width = 140; };
            _cbShowFolder.Unchecked += delegate { if (_colFolder != null) _colFolder.Width = 0; };
            leftHeader.Children.Add(_cbShowFolder);
            left.Children.Add(leftHeader); Grid.SetRow(leftHeader, 0);

            _lvProjects = new ListView
            {
                SelectionMode = SelectionMode.Multiple,
                MinHeight = 220,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                AlternationCount = 2
            };

            var itemStyle = new Style(typeof(ListViewItem));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new System.Windows.Data.Binding("DocumentId")));
            var tAlt = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
            tAlt.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(248, 249, 251))));
            itemStyle.Triggers.Add(tAlt);
            var tSel = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            tSel.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(226, 236, 247))));
            itemStyle.Triggers.Add(tSel);
            _lvProjects.ItemContainerStyle = itemStyle;

            var gv = new GridView();
            var colName = new GridViewColumn { Header = "Projet", Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding("BaseName") };
            _colFolder = new GridViewColumn { Header = "Dossier", Width = 0, DisplayMemberBinding = new System.Windows.Data.Binding("Folder") };
            var colHours = new GridViewColumn
            {
                Header = "h",
                Width = 60,
                DisplayMemberBinding = new System.Windows.Data.Binding("Hours") { StringFormat = "0.0" }
            };
            gv.Columns.Add(colName);
            gv.Columns.Add(_colFolder);
            gv.Columns.Add(colHours);
            _lvProjects.View = gv;

            left.Children.Add(_lvProjects); Grid.SetRow(_lvProjects, 1);
            _lblCount = new TextBlock { Text = "0 projet(s)", Margin = new Thickness(0, 8, 0, 0) };
            left.Children.Add(_lblCount); Grid.SetRow(_lblCount, 2);

            leftCard.Child = left;
            body.Children.Add(leftCard); Grid.SetColumn(leftCard, 0);

            // Chart
            var chartCard = Card();
            var chartWrap = new Grid { Margin = new Thickness(12) };
            chartWrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chartWrap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var chartTitle = new TextBlock { Text = "Visualisation du temps", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };
            chartWrap.Children.Add(chartTitle); Grid.SetRow(chartTitle, 0);

            _chart = BuildChart();
            _chartHost = new WindowsFormsHost { Child = _chart };
            chartWrap.Children.Add(_chartHost); Grid.SetRow(_chartHost, 1);
            chartCard.Child = chartWrap;
            body.Children.Add(chartCard); Grid.SetColumn(chartCard, 1);

            // Légende
            var legCard = Card();
            var legendPanel = new Grid { Margin = new Thickness(12) };
            legendPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            legendPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var legHeader = new StackPanel { Orientation = Orientation.Horizontal };
            var legTitle = new TextBlock { Text = "Légende", FontWeight = FontWeights.SemiBold, FontSize = 14 };
            _cbLegend = new CheckBox { Content = "Afficher", Margin = new Thickness(12, 0, 0, 0), IsChecked = false };
            _cbLegend.Checked += delegate { DrawChart(); };
            _cbLegend.Unchecked += delegate { DrawChart(); };
            legHeader.Children.Add(legTitle);
            legHeader.Children.Add(_cbLegend);
            legendPanel.Children.Add(legHeader);

            _legendList = new ListView();
            ApplyLegendTemplate(_legendList);
            legendPanel.Children.Add(_legendList); Grid.SetRow(_legendList, 1);

            legCard.Child = legendPanel;
            body.Children.Add(legCard); Grid.SetColumn(legCard, 2);

            root.Children.Add(body); Grid.SetRow(body, 2);

            // Events
            _lvProjects.SelectionChanged += (s, e) => { DrawChart(); UpdateKpis(); };
            _cbSort.SelectionChanged += (s, e) => RefreshAll();
            _dpFrom.SelectedDateChanged += (s, e) => RefreshAll();
            _dpTo.SelectedDateChanged += (s, e) => RefreshAll();
            _tbSearch.KeyDown += (s, e) => { if (e.Key == Key.Escape) _tbSearch.Text = ""; };
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); RefreshAll(); };
            _tbSearch.TextChanged += (s, e) => _searchDebounce.Start();

            _lvProjects.MouseDoubleClick += (s, e) =>
            {
                var it = _lvProjects.SelectedItem as ProjectItem;
                if (it != null)
                {
                    _lvProjects.SelectedItems.Clear();
                    _lvProjects.SelectedItems.Add(it);
                    DrawChart(); UpdateKpis();
                }
            };

            return root;
        }

        // ===== UI HELPERS =====
        private Border Card()
        {
            return new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(0),
                SnapsToDevicePixels = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.08 }
            };
        }

        private (FrameworkElement Container, TextBlock Value) MakeKpiCard(string title, string value)
        {
            var wrap = new Grid { Margin = new Thickness(0, 0, 12, 0) };
            var card = Card();
            var inner = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16) };
            inner.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.FromRgb(120, 128, 140)) });
            var val = new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
            inner.Children.Add(val);
            card.Child = inner; wrap.Children.Add(card);
            return (wrap, val);
        }

        private ToggleButton MakeSegment(string text)
        {
            var t = new ToggleButton
            {
                Content = text,
                Height = 32,
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(242, 245, 248)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 231)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            t.Checked += delegate { t.Background = new SolidColorBrush(Color.FromRgb(226, 236, 247)); };
            t.Unchecked += delegate { t.Background = new SolidColorBrush(Color.FromRgb(242, 245, 248)); };
            return t;
        }

        private Button PrimaryButton(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text,
                Height = 30,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromRgb(22, 119, 255)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(22, 119, 255)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            b.Click += (s, e) => onClick(); return b;
        }

        private Button GhostButton(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text,
                Height = 30,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromRgb(248, 249, 251)),
                Foreground = new SolidColorBrush(Color.FromRgb(34, 38, 46)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 231)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            b.Click += (s, e) => onClick(); return b;
        }

        private Button TinyBtn(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text,
                Width = 28,
                Height = 26,
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(242, 245, 248)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 231)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            b.Click += (s, e) => onClick(); return b;
        }

        private Button MakeChip(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(242, 245, 248)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 231)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            b.Click += (s, e) => onClick(); return b;
        }

        private void ApplyLegendTemplate(ListView lv)
        {
            var spFactory = new FrameworkElementFactory(typeof(StackPanel));
            spFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            spFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));

            var dot = new FrameworkElementFactory(typeof(Border));
            dot.SetValue(Border.WidthProperty, 10.0); dot.SetValue(Border.HeightProperty, 10.0);
            dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            dot.SetValue(Border.MarginProperty, new Thickness(0, 0, 8, 0));
            dot.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Brush"));
            spFactory.AppendChild(dot);

            var chk = new FrameworkElementFactory(typeof(CheckBox));
            chk.SetBinding(CheckBox.ContentProperty, new System.Windows.Data.Binding("Label"));
            chk.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding("IsChecked")
            {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            });
            spFactory.AppendChild(chk);

            lv.ItemTemplate = new DataTemplate { VisualTree = spFactory };
        }

        private Chart BuildChart()
        {
            var ch = new Chart { BackColor = SDColor.White, AntiAliasing = AntiAliasingStyles.All, TextAntiAliasingQuality = TextAntiAliasingQuality.High };
            var ca = new ChartArea("Main");
            ca.BackColor = SDColor.White;
            ca.AxisX.Interval = 1; ca.AxisX.MajorGrid.Enabled = false; ca.AxisX.LabelStyle.Angle = -50; ca.AxisX.LineColor = SDColor.FromArgb(210, 210, 210);
            ca.AxisY.MajorGrid.LineColor = SDColor.FromArgb(235, 235, 235); ca.AxisY.MajorGrid.LineDashStyle = ChartDashStyle.Dash; ca.AxisY.LineColor = SDColor.FromArgb(210, 210, 210);
            ch.ChartAreas.Add(ca); ch.Legends.Clear();
            ch.Titles.Add("Temps passé");
            ch.GetToolTipText += (s, e) =>
            {
                if (e.HitTestResult != null && e.HitTestResult.Series != null && e.HitTestResult.PointIndex >= 0)
                {
                    var srs = e.HitTestResult.Series; var dp = srs.Points[e.HitTestResult.PointIndex];
                    e.Text = string.Format("{0}\n{1:0.00} h", (dp.AxisLabel ?? srs.Name), dp.YValues[0]);
                }
            };
            var cms = new System.Windows.Forms.ContextMenuStrip();
            cms.Items.Add("Copier l'image", null, (o, ev) => CopyChartToClipboard());
            cms.Items.Add("Afficher tout", null, (o, ev) => { foreach (Series s in ch.Series) s.Enabled = true; DrawChart(); });
            ch.ContextMenuStrip = cms;
            return ch;
        }

        // ===== DATA =====
        private void LoadData()
        {
            _rows.Clear();
            if (!File.Exists(_excelPath)) return;
            using (var pkg = new ExcelPackage(new FileInfo(_excelPath)))
            {
                var ws = pkg.Workbook.Worksheets["Historique_Temps_Revit"];
                if (ws == null || ws.Dimension == null) return;

                int r0 = ws.Dimension.Start.Row + 1, rn = ws.Dimension.End.Row;
                for (int r = r0; r <= rn; r++)
                {
                    string ev = (ws.Cells[r, 1].Value ?? "").ToString();
                    if (string.IsNullOrWhiteSpace(ev)) continue;
                    string docId = (ws.Cells[r, 2].Value ?? "").ToString();
                    string docName = (ws.Cells[r, 3].Value ?? "").ToString();
                    string revitVer = (ws.Cells[r, 4].Value ?? "").ToString();
                    string dateStr = (ws.Cells[r, 5].Value ?? "").ToString();
                    string timeStr = (ws.Cells[r, 6].Value ?? "").ToString();
                    object durObj = ws.Cells[r, 7].Value;

                    _rows.Add(new LogRow
                    {
                        Event = ev,
                        DocumentId = docId,
                        DocumentName = docName,
                        RevitVersion = revitVer,
                        When = ParseDateTimeFlexible(dateStr, timeStr),
                        Duration = ParseDurationFlexible(durObj)
                    });
                }
            }
        }

        private void BuildProjectList()
        {
            var closed = _rows.Where(r => string.Equals(r.Event, "Fermé", StringComparison.OrdinalIgnoreCase));
            _projects = closed
                .GroupBy(r => r.DocumentId)
                .Select(g =>
                {
                    string id = g.Key;
                    string name = string.IsNullOrWhiteSpace(g.First().DocumentName) ? "(sans nom)" : g.First().DocumentName;
                    return new ProjectItem
                    {
                        DocumentId = id,
                        Name = name,
                        BaseName = GetBaseName(name, id),
                        Folder = GetLastFolder(id),
                        Tail = SafeDocIdTail(id),
                        Hours = 0
                    };
                }).ToList();
        }

        // ===== REFRESH =====
        private void RefreshAll()
        {
            DateTime? d0 = _dpFrom.SelectedDate;
            DateTime? d1 = _dpTo.SelectedDate?.AddDays(1).AddTicks(-1);

            _hoursByProject = _rows
                .Where(r => r.Event.Equals("Fermé", StringComparison.OrdinalIgnoreCase))
                .Where(r => !d0.HasValue || r.When >= d0.Value)
                .Where(r => !d1.HasValue || r.When <= d1.Value)
                .GroupBy(r => r.DocumentId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Duration.TotalHours), StringComparer.Ordinal);

            ApplyProjectSearchFilterAndSort();
            DrawChart();
            UpdateKpis();
        }

        private void ApplyProjectSearchFilterAndSort()
        {
            string q = RemoveDiacritics((_tbSearch != null ? _tbSearch.Text : "").Trim());
            SortMode sm = (_cbSort != null && _cbSort.SelectedIndex == 1) ? SortMode.NameAZ : SortMode.HoursDesc;

            IEnumerable<ProjectItem> seq = _projects;
            foreach (var p in seq) p.Hours = _hoursByProject.ContainsKey(p.DocumentId) ? _hoursByProject[p.DocumentId] : 0.0;

            if (!string.IsNullOrEmpty(q))
            {
                seq = seq.Where(p =>
                    RemoveDiacritics(p.BaseName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    RemoveDiacritics(p.Folder ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    RemoveDiacritics(p.Tail ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            _filteredProjects = (sm == SortMode.HoursDesc)
                ? seq.OrderByDescending(p => p.Hours).ThenBy(p => p.BaseName).ToList()
                : seq.OrderBy(p => p.BaseName).ThenBy(p => p.Folder).ToList();

            _lvProjects.ItemsSource = _filteredProjects;
            _lblCount.Text = _filteredProjects.Count + " projet(s)";
            if (string.IsNullOrEmpty(q) && _lvProjects.SelectedItems.Count == 0) _lvProjects.SelectAll();
        }

        // ===== CHART =====
        private void DrawChart()
        {
            _chart.Series.Clear();
            var selected = _lvProjects.SelectedItems.Cast<ProjectItem>().ToList();
            DateTime? d0 = _dpFrom.SelectedDate;
            DateTime? d1 = _dpTo.SelectedDate != null ? _dpTo.SelectedDate.Value.AddDays(1).AddTicks(-1) : (DateTime?)null;

            IEnumerable<LogRow> closed = _rows.Where(r => r.Event.Equals("Fermé", StringComparison.OrdinalIgnoreCase));
            if (d0.HasValue) closed = closed.Where(r => r.When >= d0.Value);
            if (d1.HasValue) closed = closed.Where(r => r.When <= d1.Value);

            int topN = GetTopN();
            var gran = ChooseAutoGranularity(d0 ?? DateTime.MinValue, d1 ?? DateTime.MaxValue);

            if (_tgOverview.IsChecked == true)
            {
                var totals = closed
                    .Where(r => selected.Any(s => s.DocumentId == r.DocumentId))
                    .GroupBy(r => r.DocumentId)
                    .Select(g => new
                    {
                        DocId = g.Key,
                        Hours = g.Sum(x => x.Duration.TotalHours),
                        Name = _projects.FirstOrDefault(p => p.DocumentId == g.Key) != null ? _projects.FirstOrDefault(p => p.DocumentId == g.Key).BaseName : "(?)"
                    })
                    .OrderByDescending(x => x.Hours)
                    .ToList();

                var top = totals.Take(topN).ToList();
                double others = Math.Max(0, totals.Skip(topN).Sum(x => x.Hours));

                var series = new Series("Top " + topN) { ChartType = SeriesChartType.Column };
                series.Color = GetSeriesColor(0);

                foreach (var t in top)
                {
                    int pIndex = series.Points.AddXY(Short(t.Name), Math.Round(t.Hours, 2));
                    series.Points[pIndex].ToolTip = t.Name + "\n" + t.Hours.ToString("0.00") + " h";
                }
                if (others > 0.0001)
                {
                    int pIndex = series.Points.AddXY("Autres", Math.Round(others, 2));
                    series.Points[pIndex].Color = SDColor.FromArgb(210, 210, 210);
                    series.Points[pIndex].ToolTip = "Autres\n" + others.ToString("0.00") + " h";
                }

                _chart.Series.Add(series);
                _chart.ChartAreas[0].AxisY.Title = "heures (total)";
                _chart.Titles[0].Text = "Temps passé — Aperçu (Top " + topN + ")";

                if (_cbLegend.IsChecked == true) BuildLegendForBars(series);
                else _legendList.ItemsSource = null;
            }
            else
            {
                Func<DateTime, DateTime> bucket = delegate (DateTime dt)
                {
                    if (gran == AutoGran.Week) return StartOfIsoWeek(dt);
                    if (gran == AutoGran.Month) return new DateTime(dt.Year, dt.Month, 1);
                    return dt.Date;
                };

                var grouped = closed
                    .Where(r => selected.Any(s => s.DocumentId == r.DocumentId))
                    .GroupBy(r => r.DocumentId)
                    .OrderByDescending(g => g.Sum(x => x.Duration.TotalHours))
                    .Take(topN)
                    .ToList();

                int idx = 0;
                foreach (var g in grouped)
                {
                    var proj = _projects.FirstOrDefault(p => p.DocumentId == g.Key);
                    string legend = proj != null ? (Short(proj.BaseName) + " — " + Short(proj.Folder)) : g.Key;

                    var series = new Series("S_" + idx)
                    {
                        ChartType = SeriesChartType.Spline,
                        BorderWidth = 3,
                        MarkerStyle = MarkerStyle.Circle,
                        MarkerSize = 5
                    };
                    var c = GetSeriesColor(idx);
                    series.Color = c; series.Tag = legend;

                    var byBucket = g.GroupBy(x => bucket(x.When))
                                    .Select(x => new { K = x.Key, Hours = x.Sum(z => z.Duration.TotalHours) })
                                    .OrderBy(x => x.K)
                                    .ToList();

                    foreach (var p in byBucket)
                    {
                        int dpIndex = series.Points.AddXY(FormatBucket(p.K, gran), Math.Round(p.Hours, 2));
                        series.Points[dpIndex].ToolTip = legend + "\n" + p.Hours.ToString("0.00") + " h";
                    }
                    _chart.Series.Add(series);
                    idx++;
                }

                _chart.ChartAreas[0].AxisY.Title = "heures / " + (gran == AutoGran.Day ? "jour" : (gran == AutoGran.Week ? "semaine" : "mois"));
                _chart.Titles[0].Text = "Temps passé — Comparer (Top " + topN + ")";
                if (_cbLegend.IsChecked == true) BuildLegendForLines();
                else _legendList.ItemsSource = null;
            }
        }

        private void BuildLegendForBars(Series series)
        {
            var items = new List<LegendItemVM>();
            foreach (var dp in series.Points)
            {
                string label = dp.AxisLabel ?? "…";
                SDColor c = dp.Color.IsEmpty ? series.Color : dp.Color;
                items.Add(new LegendItemVM(label, delegate { dp.IsEmpty = !dp.IsEmpty; }) { Brush = ToBrush(c) });
            }
            _legendList.ItemsSource = items;
        }

        private void BuildLegendForLines()
        {
            var items = new List<LegendItemVM>();
            foreach (Series s in _chart.Series)
            {
                string label = (s.Tag as string) ?? s.Name;
                items.Add(new LegendItemVM(label, delegate { s.Enabled = !s.Enabled; }) { Brush = ToBrush(s.Color) });
            }
            _legendList.ItemsSource = items;
        }

        // ===== KPI =====
        // ===== KPI =====
        private void UpdateKpis()
        {
            var selected = new HashSet<string>(_lvProjects.SelectedItems.Cast<ProjectItem>().Select(p => p.DocumentId));
            DateTime d0 = _dpFrom.SelectedDate ?? DateTime.MinValue;
            DateTime d1 = _dpTo.SelectedDate.HasValue ? _dpTo.SelectedDate.Value.AddDays(1).AddTicks(-1) : DateTime.MaxValue;

            // 1) Filtrer par plage + projets sélectionnés
            var inRangeAll = _rows.Where(r => r.Event.Equals("Fermé", StringComparison.OrdinalIgnoreCase))
                                  .Where(r => r.When >= d0 && r.When <= d1)
                                  .Where(r => selected.Contains(r.DocumentId));

            // 2) Exclure systématiquement le week-end (samedi/dimanche)
            var inRangeWeekdays = inRangeAll
                .Where(r => r.When.DayOfWeek != DayOfWeek.Saturday && r.When.DayOfWeek != DayOfWeek.Sunday)
                .ToList();

            // 3) Heures totales (hors week-end)
            double totalH = inRangeWeekdays.Sum(r => r.Duration.TotalHours);

            // 4) Nombre de jours ouvrés effectivement travaillés (distincts)
            int workedWeekdays = inRangeWeekdays
                .Select(r => r.When.Date)
                .Distinct()
                .Count();

            // 5) Moyenne/jour (0 si aucun jour ouvré travaillé)
            double avg = workedWeekdays == 0 ? 0.0 : totalH / workedWeekdays;

            // 6) KPI
            int projects = _lvProjects.SelectedItems.Count;
            _kpiHours.Text = totalH.ToString("0.0") + " h";  // total hors week-end
            _kpiProjects.Text = projects.ToString();
            _kpiAvg.Text = avg.ToString("0.0") + " h";
        }


        // ===== ACTIONS =====
        private void ExportPng()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "dashboard_temps.png", Filter = "Image PNG|*.png" };
            if (dlg.ShowDialog() == true) { _chart.SaveImage(dlg.FileName, ChartImageFormat.Png); MessageBox.Show("Exporté : " + dlg.FileName); }
        }

        private void CopyChartToClipboard()
        {
            using (var ms = new MemoryStream())
            {
                _chart.SaveImage(ms, ChartImageFormat.Png);
                ms.Position = 0;
                var img = new System.Windows.Media.Imaging.PngBitmapDecoder(ms, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.Default);
                Clipboard.SetImage(img.Frames[0]);
            }
        }

        private void ExportCsv()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "dashboard_temps.csv", Filter = "CSV|*.csv" };
            if (dlg.ShowDialog() != true) return;

            var selected = new HashSet<string>(_lvProjects.SelectedItems.Cast<ProjectItem>().Select(p => p.DocumentId));
            DateTime d0 = _dpFrom.SelectedDate ?? DateTime.MinValue;
            DateTime d1 = _dpTo.SelectedDate.HasValue ? _dpTo.SelectedDate.Value.AddDays(1).AddTicks(-1) : DateTime.MaxValue;

            var rows = _rows.Where(r => r.Event.Equals("Fermé", StringComparison.OrdinalIgnoreCase))
                            .Where(r => r.When >= d0 && r.When <= d1)
                            .Where(r => selected.Contains(r.DocumentId))
                            .Select(r => new
                            {
                                r.DocumentName,
                                r.DocumentId,
                                Date = r.When.ToString("yyyy-MM-dd"),
                                Time = r.When.ToString("HH:mm:ss"),
                                Hours = r.Duration.TotalHours
                            });

            var sb = new StringBuilder();
            sb.AppendLine("DocumentName,DocumentId,Date,Time,Hours");
            foreach (var r in rows)
                sb.AppendLine(string.Format("{0},{1},{2},{3},{4}",
                    Csv(r.DocumentName), Csv(r.DocumentId), r.Date, r.Time, r.Hours.ToString("0.###")));

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show("Exporté : " + dlg.FileName);
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
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

        private void ResetFilters()
        {
            _tbSearch.Text = "";
            _cbSort.SelectedIndex = 0;
            SetTopN(DEFAULT_TOP_N);
            _dpFrom.SelectedDate = DateTime.Today.AddMonths(-1);
            _dpTo.SelectedDate = DateTime.Today;
            _tgOverview.IsChecked = true; _tgCompare.IsChecked = false;
            _cbLegend.IsChecked = false;
            _cbShowFolder.IsChecked = false;
            _lvProjects.SelectAll();
            RefreshAll();
        }

        // ===== PREFS =====
        private void LoadPrefs()
        {
            try
            {
                Directory.CreateDirectory(_prefsDir);
                if (!File.Exists(PrefsPath)) return;
                var json = File.ReadAllText(PrefsPath, Encoding.UTF8);
                _prefs = JsonSerializer.Deserialize<Prefs>(json) ?? new Prefs();
            }
            catch { _prefs = new Prefs(); }
        }
        private void ApplyPrefsToUi()
        {
            try
            {
                if (_prefs.From.HasValue) _dpFrom.SelectedDate = _prefs.From.Value;
                if (_prefs.To.HasValue) _dpTo.SelectedDate = _prefs.To.Value;
                _cbSort.SelectedIndex = _prefs.Sort == "NameAZ" ? 1 : 0;
                SetTopN(ClampInt(_prefs.TopN <= 0 ? DEFAULT_TOP_N : _prefs.TopN, TOP_N_MIN, TOP_N_MAX));
                _tgOverview.IsChecked = _prefs.Mode != "Compare";
                _tgCompare.IsChecked = _prefs.Mode == "Compare";
                _cbLegend.IsChecked = _prefs.LegendShown;
                _cbShowFolder.IsChecked = _prefs.ShowFolder;
                if (_colFolder != null) _colFolder.Width = (_prefs.ShowFolder ? 140 : 0);
            }
            catch { /* ignore */ }
        }
        private void SavePrefs()
        {
            try
            {
                Directory.CreateDirectory(_prefsDir);
                _prefs.From = _dpFrom.SelectedDate;
                _prefs.To = _dpTo.SelectedDate;
                _prefs.Sort = _cbSort.SelectedIndex == 1 ? "NameAZ" : "HoursDesc";
                _prefs.TopN = GetTopN();
                _prefs.Mode = _tgCompare.IsChecked == true ? "Compare" : "Overview";
                _prefs.LegendShown = _cbLegend.IsChecked == true;
                _prefs.ShowFolder = _cbShowFolder.IsChecked == true;
                File.WriteAllText(PrefsPath, JsonSerializer.Serialize(_prefs, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
            catch { /* ignore */ }
        }

        // ===== TOP N helpers =====
        private int GetTopN()
        {
            int n;
            if (!int.TryParse(_tbTopN.Text, out n)) n = DEFAULT_TOP_N;
            return ClampInt(n, TOP_N_MIN, TOP_N_MAX);
        }
        private void SetTopN(int n)
        {
            _tbTopN.Text = ClampInt(n, TOP_N_MIN, TOP_N_MAX).ToString();
            DrawChart(); SavePrefs();
        }
        private void AdjustTopN(int delta) { SetTopN(GetTopN() + delta); }

        // ===== DIVERS =====
        private void SetRange(DateTime? from, DateTime? to) { _dpFrom.SelectedDate = from; _dpTo.SelectedDate = to; RefreshAll(); }
        private void FocusSearch() { _tbSearch.Focus(); }

        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in normalized)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static AutoGran ChooseAutoGranularity(DateTime from, DateTime to)
        { double span = (to - from).Duration().TotalDays; if (span <= 60) return AutoGran.Day; if (span <= 420) return AutoGran.Week; return AutoGran.Month; }

        private static string GetBaseName(string docName, string docId)
        {
            string s = docName ?? "";
            if (string.IsNullOrWhiteSpace(s) || s.IndexOf('.') < 0) { try { s = Path.GetFileNameWithoutExtension(docId ?? ""); } catch { } }
            else { try { s = Path.GetFileNameWithoutExtension(s); } catch { } }
            return string.IsNullOrWhiteSpace(s) ? "(sans nom)" : s;
        }
        private static string GetLastFolder(string id)
        { try { if (string.IsNullOrWhiteSpace(id)) return ""; string dir = Path.GetDirectoryName(id); return string.IsNullOrEmpty(dir) ? "" : new DirectoryInfo(dir).Name; } catch { return ""; } }
        private static string Short(string s) { if (string.IsNullOrWhiteSpace(s)) return s; return s.Length <= 28 ? s : s.Substring(0, 25) + "…"; }

        private static string SafeDocIdTail(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "(id)";
            string s = id;
            int i = s.LastIndexOf('|'); if (i >= 0 && i < s.Length - 1) s = s.Substring(i + 1);
            int j1 = s.LastIndexOf('\\'); int j2 = s.LastIndexOf('/'); int j = (j1 > j2) ? j1 : j2;
            if (j >= 0 && j < s.Length - 1) s = s.Substring(j + 1);
            return s;
        }

        private static DateTime StartOfIsoWeek(DateTime dt)
        {
            DayOfWeek day = CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(dt);
            int delta = (day == DayOfWeek.Sunday) ? -6 : ((int)DayOfWeek.Monday - (int)day);
            return dt.Date.AddDays(delta);
        }
        private static string FormatBucket(DateTime k, AutoGran gran)
        { if (gran == AutoGran.Week) return k.Year.ToString("0000") + "-S" + GetIsoWeekOfYear(k).ToString("00"); if (gran == AutoGran.Month) return k.ToString("yyyy-MM"); return k.ToString("yyyy-MM-dd"); }
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
                {
                    DateTime dt;
                    if (DateTime.TryParseExact(string.IsNullOrEmpty(t) ? d : (d + " " + t), f, c, DateTimeStyles.AssumeLocal, out dt)) return dt;
                }
            DateTime dt2;
            if (DateTime.TryParse(d + " " + t, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt2)) return dt2;
            return DateTime.Now;
        }
        private static TimeSpan ParseDurationFlexible(object o)
        {
            try
            {
                if (o == null) return TimeSpan.Zero;
                TimeSpan ts0;
                if (o is TimeSpan) { ts0 = (TimeSpan)o; return ts0; }
                if (o is double)
                {
                    double d = (double)o;
                    if (double.IsNaN(d) || double.IsInfinity(d)) return TimeSpan.Zero;
                    d = ClampDouble(d, -365000d, 365000d);
                    return TimeSpan.FromDays(d);
                }
                if (o is DateTime)
                {
                    DateTime dt = (DateTime)o;
                    double oa = dt.ToOADate();
                    double frac = oa - Math.Floor(oa);
                    return TimeSpan.FromDays(frac);
                }
                string s = o.ToString().Trim();
                if (TimeSpan.TryParse(s, out ts0)) return ts0;
                double dInv;
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out dInv)
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

        // ===== Couleurs =====
        private static SDColor GetSeriesColor(int index)
        { double hue = (index * 37) % 360; return HslToColor(hue, 0.55, 0.60); }
        private static SDColor HslToColor(double h, double s, double l)
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
            return SDColor.FromArgb(255, R, G, B);
        }
        private static SWM.SolidColorBrush ToBrush(SDColor c)
        { var mc = SWM.Color.FromArgb(c.A, c.R, c.G, c.B); var br = new SWM.SolidColorBrush(mc); br.Freeze(); return br; }

        // ===== Utils .NET 4.x =====
        private static int ClampInt(int value, int min, int max)
        { if (value < min) return min; if (value > max) return max; return value; }
        private static double ClampDouble(double value, double min, double max)
        { if (value < min) return min; if (value > max) return max; return value; }

        // ===== Models & Prefs =====
        private class LogRow { public string Event, DocumentId, DocumentName, RevitVersion; public DateTime When; public TimeSpan Duration; }
        private class ProjectItem { public string DocumentId { get; set; } public string Name { get; set; } public string BaseName { get; set; } public string Folder { get; set; } public string Tail { get; set; } public double Hours { get; set; } }
        private class LegendItemVM : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isChecked = true; public string Label { get; set; }
            public Action Toggle { get; set; }
            public SWM.Brush Brush { get; set; } = SWM.Brushes.Black;
            public bool IsChecked { get { return _isChecked; } set { if (_isChecked == value) return; _isChecked = value; if (PropertyChanged != null) PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs("IsChecked")); if (Toggle != null) Toggle(); } }
            public LegendItemVM(string label, Action toggle) { Label = label; Toggle = toggle; }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }
        private class Prefs { public DateTime? From { get; set; } public DateTime? To { get; set; } public string Sort { get; set; } = "HoursDesc"; public int TopN { get; set; } = DEFAULT_TOP_N; public string Mode { get; set; } = "Overview"; public bool LegendShown { get; set; } = false; public bool ShowFolder { get; set; } = false; }

        private class RelayCommand : ICommand
        {
            private readonly Action<object> _act; public RelayCommand(Action<object> act) { _act = act; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object p) { return true; }
            public void Execute(object p) { _act(p); }
        }
    }
}

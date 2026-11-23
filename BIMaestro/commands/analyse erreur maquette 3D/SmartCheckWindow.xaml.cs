using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using WpfVisibility = System.Windows.Visibility;
using Microsoft.Win32;
using Modification;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Analyse
{
    public partial class SmartCheckWindow : Window
    {
        private readonly ExternalEvent _externalEvent;
        private readonly SmartExternalHandler _handler;
        private SmartScanSetup _setup;
        private List<ModelIssue> _all = new List<ModelIssue>();
        private List<ModelIssue> _filtered = new List<ModelIssue>();
        private readonly ObservableCollection<ModelIssue> _visible = new ObservableCollection<ModelIssue>();
        private readonly HashSet<string> _categories = new HashSet<string>();
        private Action<IEnumerable<ModelIssue>> _restoreResults;
        private string _thumbnailFolder;
        private int _publishedCount, _activeCount, _confirmedCount, _approximateCount;
        private bool Scanning => _session != null && !_session.Complete;
        private SmartScanSession _session;
        private SmartScanSession _reportedScanSession;
        private string _previousSignature;
        private string _currentSignature;
        private HashSet<string> _previousKeys;
        private bool _ready, _busy, _stale, _binding, _closing, _closeConfirmed;
        private bool _startAfterRefresh;
        private SmartAction? _queuedAction;
        private readonly DispatcherTimer _queueTimer;
        private SmartIssueInspector _inspector;
        public Document OwnerDocument => _setup.Document;
        public SmartCheckWindow(ExternalEvent externalEvent, SmartExternalHandler handler, SmartScanSetup setup)
        {
            ThemeManager.EnsureThemeLoaded(); InitializeComponent();
            _externalEvent = externalEvent; _handler = handler; _setup = setup;
            ResultsList.ItemsSource = _visible;
            ScopeCombo.ItemsSource = Choices(new[] { "Maquette entière", "Vue active", "Sélection" });
            ScopeCombo.SelectedIndex = setup.Selection.Count > 0 ? 2 : 0;
            StatusCombo.ItemsSource = Choices(new[] { "À traiter", "Tous", ModelIssue.StatusToFix, ModelIssue.StatusReview, ModelIssue.StatusFixed, ModelIssue.StatusIgnored });
            StatusCombo.SelectedIndex = 0;
            CategoryCombo.ItemsSource = Choices(new[] { "Tous les contrôles" }); CategoryCombo.SelectedIndex = 0;
            LinksList.ItemsSource = setup.Links;
            var preferences = SmartCheckState.LoadPreferences();
            if (preferences != null)
            {
                PipesCheck.IsChecked = preferences.Pipes; DuctsCheck.IsChecked = preferences.Ducts;
                TraysCheck.IsChecked = preferences.CableTrays; ConduitsCheck.IsChecked = preferences.Conduits;
                FittingsCheck.IsChecked = preferences.Fittings; EquipmentCheck.IsChecked = preferences.Equipment;
                GenericCheck.IsChecked = preferences.GenericModels; LocalCheck.IsChecked = preferences.LocalClashes;
                LinksCheck.IsChecked = preferences.LinkedClashes; InsulationCheck.IsChecked = preferences.IncludeInsulation;
                ConnectorsCheck.IsChecked = preferences.OpenConnectors; WallsCheck.IsChecked = preferences.WallSupports;
                VolumeBox.Text = preferences.MinimumVolumeMm3.ToString("G", System.Globalization.CultureInfo.CurrentCulture);
            }
            _queueTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(35) };
            _queueTimer.Tick += Queue_Tick;
            _handler.Completed += Handler_Completed;
            _handler.Failed += Handler_Failed;
            _handler.SetupRefreshed += Setup_Refreshed;
            _handler.ModelChanged += Model_Changed;
            _ready = true; UpdateDocumentText();
            BIMaestro.Tutorials.DemoTourService.AttachIfRequested("clash-3d", this);
            Closed += (s, e) =>
            {
                _queueTimer.Stop(); _inspector?.Close();
                _handler.Completed -= Handler_Completed; _handler.Failed -= Handler_Failed;
                _handler.SetupRefreshed -= Setup_Refreshed; _handler.ModelChanged -= Model_Changed;
                _handler.Dispose(); _externalEvent.Dispose();
            };
        }
        private void UpdateDocumentText() => DocumentText.Text = _setup.Title + " · " + _setup.Selection.Count + UiLanguage.Get("SmartCheckWindow.xaml.SelectedElementS")
            + _setup.Links.Count(l => l.Loaded) + UiLanguage.Get("SmartCheckWindow.xaml.AvailableLinkSOrImportS");
        private SmartScanOptions ReadOptions()
        {
            double volume;
            if (!double.TryParse(VolumeBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out volume) || double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0 || volume > 1000000000)
                throw new InvalidOperationException(UiLanguage.Get("SmartCheckWindow.xaml.TheThresholdMustBeANumberBetween0"));
            var options = new SmartScanOptions
            {
                Scope = (SmartScanScope)ScopeCombo.SelectedIndex,
                Pipes = PipesCheck.IsChecked == true, Ducts = DuctsCheck.IsChecked == true,
                CableTrays = TraysCheck.IsChecked == true, Conduits = ConduitsCheck.IsChecked == true,
                Fittings = FittingsCheck.IsChecked == true, Equipment = EquipmentCheck.IsChecked == true,
                GenericModels = GenericCheck.IsChecked == true, LocalClashes = LocalCheck.IsChecked == true,
                LinkedClashes = LinksCheck.IsChecked == true, IncludeInsulation = InsulationCheck.IsChecked == true,
                OpenConnectors = ConnectorsCheck.IsChecked == true, WallSupports = WallsCheck.IsChecked == true,
                MinimumVolumeMm3 = volume, LinkIds = _setup.Links.Where(l => l.Selected).Select(l => l.Id).ToList()
            };
            if (options.SourceCategories().Count == 0 && !options.WallSupports) throw new InvalidOperationException(UiLanguage.Get("SmartCheckWindow.xaml.SelectAtLeastOneCategoryToCheck"));
            if (!options.LocalClashes && !options.LinkedClashes && !options.OpenConnectors && !options.WallSupports)
                throw new InvalidOperationException(UiLanguage.Get("SmartCheckWindow.xaml.EnableAtLeastOneCheck"));
            if (options.Scope == SmartScanScope.Selection && _setup.Selection.Count == 0)
                throw new InvalidOperationException(UiLanguage.Get("SmartCheckWindow.xaml.TheSelectionIsEmptySelectElementsInRevit"));
            if (options.Scope == SmartScanScope.ActiveView && !_setup.ViewSupported)
                throw new InvalidOperationException(UiLanguage.Get("SmartCheckWindow.xaml.ThisViewCannotBeAnalyzedChooseAModel"));
            return options;
        }
        private void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _queuedAction.HasValue || Scanning) return;
            // Read the current selection/view in a valid API callback, including changes made after opening this window.
            _startAfterRefresh = true;
            RunText.Text = UiLanguage.Get("SmartCheckWindow.xaml.RefreshingTheScopeBeforeAnalysis");
            Queue(SmartAction.RefreshSetup);
        }
        private void StartScan()
        {
            try
            {
                var options = ReadOptions();
                if (!_tutorialPrepared) SmartCheckState.SavePreferences(options);
                _tutorialActionFailed = false;
                SettingsExpander.IsExpanded = false;
                _inspector?.Close(); _inspector = null;
                _previousKeys = _session != null && _session.Complete && !_session.Cancelled && _session.Error == null && !_stale
                    ? new HashSet<string>(_all.Select(i => i.IssueKey)) : null;
                // Model edits make the old result stale, but it remains a valid previous comparison snapshot.
                if (_session != null && _session.Complete && !_session.Cancelled && _session.Error == null)
                    _previousKeys = new HashSet<string>(_all.Select(i => i.IssueKey));
                _currentSignature = options.Signature + "|" + (options.Scope == SmartScanScope.ActiveView ? _setup.ViewId.GetIdLongValue().ToString()
                    : options.Scope == SmartScanScope.Selection ? string.Join(",", _setup.Selection.Select(id => id.GetIdLongValue()).OrderBy(id => id)) : "model");
                _session?.Dispose(); _session = new SmartScanSession(_setup, options); _handler.Session = _session;
                _all = new List<ModelIssue>(); _filtered = new List<ModelIssue>(); _visible.Clear(); _categories.Clear();
                ResultsList.ItemsSource = _visible;
                _publishedCount = _activeCount = _confirmedCount = _approximateCount = 0;
                _restoreResults = SmartCheckState.CreateRestorer(_setup.DocumentKey);
                _thumbnailFolder = SmartCheckState.GetThumbnailFolder(_setup.DocumentKey);
                UpdateCategories();
                _stale = false; StaleText.Visibility = WpfVisibility.Collapsed;
                SummaryText.Text = UiLanguage.Get("SmartCheckWindow.xaml.ProvisionalSummaryAnalysisInProgress"); FilterPanel.Visibility = WpfVisibility.Visible;
                ResultCountText.Text = UiLanguage.Get("SmartCheckWindow.xaml.ResultsAppearAsTheyAreFound");
                EmptyPanel.Visibility = WpfVisibility.Visible; EmptyTitle.Text = UiLanguage.Get("SmartCheckWindow.xaml.AnalysisInProgress");
                EmptyDescription.Text = UiLanguage.Get("SmartCheckWindow.xaml.PreparingElementsAndLinksResultsWillAppearAs");
                RunProgress.Visibility = WpfVisibility.Visible; RunProgress.IsIndeterminate = true;
                SetScanning(true); Queue(SmartAction.ScanBatch);
            }
            catch (Exception ex) { RunText.Text = ex.Message; SettingsExpander.IsExpanded = true; }
        }
        private void Cancel_Click(object sender, RoutedEventArgs e)
        { if (_session != null) { _session.CancelRequested = true; RunText.Text = UiLanguage.Get("SmartCheckWindow.xaml.CancellationRequested"); CancelButton.IsEnabled = false; } }
        private void RefreshScope_Click(object sender, RoutedEventArgs e) => Queue(SmartAction.RefreshSetup);
        private void Setup_Refreshed(SmartScanSetup next)
        {
            // Focusing a clash selects one pipe in Revit. The prepared exercise
            // must keep analysing its three pipes when that live selection changes.
            if (_tutorialPrepared) next.Selection = BIMaestro.Tutorials.DemoClashExercise.Sources(OwnerDocument);
            var selected = new HashSet<long>(_setup.Links.Where(l => l.Selected).Select(l => l.Id.GetIdLongValue()));
            foreach (var link in next.Links) link.Selected = selected.Contains(link.Id.GetIdLongValue()) && link.Loaded;
            _setup = next; LinksList.ItemsSource = next.Links; UpdateDocumentText();
            RunText.Text = UiLanguage.Get("SmartCheckWindow.xaml.ViewSelectionAndLinksRefreshedChooseTheScope");
        }
        private void Model_Changed()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Model_Changed)); return; }
            _stale = true; StaleText.Visibility = WpfVisibility.Visible;
            _inspector?.MarkStale();
            UpdateFocusActions();
            if (_session != null && !_session.Complete) RunText.Text = UiLanguage.Get("SmartCheckWindow.xaml.TheModelChangedDuringTheAnalysisCancellingAnd");
        }
        private void Queue(SmartAction action)
        {
            if (_busy || _queuedAction.HasValue) return;
            _tutorialActionFailed = false;
            _queuedAction = action; _queueTimer.Start();
            UpdateFocusActions();
        }
        private void Queue_Tick(object sender, EventArgs e)
        {
            if (_busy || !_queuedAction.HasValue) return;
            _handler.Action = _queuedAction.Value;
            try
            {
                var request = _externalEvent.Raise();
                if (request == ExternalEventRequest.Accepted) { _busy = true; _queuedAction = null; _queueTimer.Stop(); }
                else if (request != ExternalEventRequest.Pending) { _queuedAction = null; _queueTimer.Stop(); Handler_Failed(UiLanguage.Get("SmartCheckWindow.xaml.RevitDidNotAcceptTheActionCloseAny")); }
            }
            catch (Exception ex) { _queuedAction = null; _queueTimer.Stop(); Handler_Failed(ex.Message); }
        }
        private void Handler_Failed(string message)
        {
            _startAfterRefresh = false;
            _tutorialActionFailed = true;
            if (_tutorialPrepared) TutorialFixButton.IsEnabled = !_tutorialCorrected;
            RunText.Text = message;
            _inspector?.SetActionMessage(message);
            if (_session != null && !_session.Complete) { _session.CancelRequested = true; _session.Advance(); }
        }
        private void Handler_Completed()
        {
            _busy = false;
            if (_handler.Action == SmartAction.RefreshSetup && _startAfterRefresh)
            {
                _startAfterRefresh = false;
                if (!_closing) StartScan();
            }
            if (_handler.Action == SmartAction.ScanBatch)
            {
                RunProgress.IsIndeterminate = _session.SourceCount == 0 || _session.ProcessedSources == 0;
                RunProgress.Value = _session.Progress;
                RunText.Text = _session.Stage + " · " + _session.ProcessedSources + "/" + _session.SourceCount + UiLanguage.Get("SmartCheckWindow.xaml.ElementS")
                    + _session.CandidateCount + UiLanguage.Get("SmartCheckWindow.xaml.ObstacleS") + _session.Issues.Count + UiLanguage.Get("SmartCheckWindow.xaml.ResultS") + _session.Seconds.ToString("F1") + " s";
                PublishResults();
                _inspector?.RefreshPreview(); _inspector?.SetScanning(Scanning);
                if (!_session.Complete) { Queue(SmartAction.ScanBatch); return; }
                FinishScan();
            }
            _inspector?.RefreshPreview();
            UpdateFocusActions();
            if (_handler.Action == SmartAction.CreateReservation && !string.IsNullOrWhiteSpace(_handler.ReservationMessage))
            { RunText.Text = _handler.ReservationMessage; _inspector?.SetActionMessage(_handler.ReservationMessage); }
            if (!_closing) TutorialActionCompleted();
            if (_closing)
            {
                if (_handler.Action == SmartAction.CloseSession)
                {
                    _closeConfirmed = true;
                    // Dispose the ExternalEvent only after its Execute callback has returned.
                    Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.ApplicationIdle);
                }
                else Queue(SmartAction.CloseSession);
            }
        }
        private void FinishScan()
        {
            PublishResults();
            SetScanning(false); RunProgress.Visibility = WpfVisibility.Collapsed;
            int absent = 0;
            if (_previousKeys != null && _previousSignature == _currentSignature && !_session.Cancelled && _session.Error == null)
            {
                var current = new HashSet<string>(_all.Select(i => i.IssueKey)); absent = _previousKeys.Count(k => !current.Contains(k));
            }
            if (!_session.Cancelled && _session.Error == null) _previousSignature = _currentSignature;
            var partial = _session.Cancelled || _session.Error != null;
            if (!partial && !ReferenceEquals(_reportedScanSession, _session))
            {
                _reportedScanSession = _session;
                Licensing.OperationTelemetry.Track("SmartClashCommand", "Clash 3D", "analysis", _tutorialPrepared,
                    new Dictionary<string, int> { ["analyses_completed"] = 1,
                        ["objects_analyzed"] = _session.SourceCount, ["issues_found"] = _session.Issues.Count });
            }
            RunText.Text = (partial ? UiLanguage.Get("SmartCheckWindow.xaml.AnalysisInterruptedPartialResults") : _session.Stage) + " · " + _session.SourceCount + UiLanguage.Get("SmartCheckWindow.xaml.ElementS") + _session.Seconds.ToString("F1") + " s"
                + (_session.Diagnostics.Count > 0 ? " · " + _session.Diagnostics.Count + UiLanguage.Get("SmartCheckWindow.xaml.ItemSToReviewInTheSummary") : "");
            if (_session.Error != null) RunText.Text += " · " + _session.Error;
            if (absent > 0) RunText.Text += " · " + absent + UiLanguage.Get("SmartCheckWindow.xaml.ResultSNoLongerPresentSinceThePrevious");
            UpdateCategories();
            FilterPanel.Visibility = WpfVisibility.Visible; DiagnosticsButton.IsEnabled = true;
            ApplyFilters();
            TutorialScanCompleted();
        }
        private void PublishResults()
        {
            if (_session == null) return;
            bool categoriesChanged = false;
            if (_publishedCount < _session.Issues.Count)
            {
                var batch = _session.Issues.GetRange(_publishedCount, _session.Issues.Count - _publishedCount);
                if (_restoreResults == null) _restoreResults = SmartCheckState.CreateRestorer(_setup.DocumentKey);
                _restoreResults(batch);
                if (_thumbnailFolder == null) _thumbnailFolder = SmartCheckState.GetThumbnailFolder(_setup.DocumentKey);
                foreach (var issue in batch)
                {
                    var preview = Path.Combine(_thumbnailFolder, SmartClashReport.PreviewName(issue));
                    if (File.Exists(preview)) issue.ThumbnailPath = preview;
                    if (_previousKeys != null && _previousSignature == _currentSignature) issue.IsNew = !_previousKeys.Contains(issue.IssueKey);
                    _all.Add(issue);
                    if (!issue.Ignored) _activeCount++;
                    if (!issue.IsApproximate && issue.Kind != IssueKind.MepUnconnected) _confirmedCount++;
                    if (issue.IsApproximate) _approximateCount++;
                    if (_categories.Add(issue.Category)) categoriesChanged = true;
                    if (MatchesFilters(issue)) { _filtered.Add(issue); _visible.Add(issue); }
                }
                _publishedCount = _session.Issues.Count;
            }
            if (categoriesChanged) UpdateCategories();
            UpdateResultText();
        }
        private void UpdateCategories()
        {
            _binding = true;
            var category = SelectedValue(CategoryCombo);
            var categories = new[] { "Tous les contrôles" }.Concat(_categories.OrderBy(s => s));
            var categoryChoices = Choices(categories);
            CategoryCombo.ItemsSource = categoryChoices;
            CategoryCombo.SelectedItem = categoryChoices.FirstOrDefault(c => c.Value == category) ?? categoryChoices[0];
            _binding = false;
        }
        private void SetScanning(bool scanning)
        {
            ScopePanel.IsEnabled = !scanning; SettingsExpander.IsEnabled = !scanning; AnalyzeButton.IsEnabled = !scanning;
            CancelButton.Visibility = scanning ? WpfVisibility.Visible : WpfVisibility.Collapsed; CancelButton.IsEnabled = scanning;
            OverviewButton.IsEnabled = !scanning && _filtered.Count > 0; ExportButton.IsEnabled = !scanning && _session != null;
            DiagnosticsButton.IsEnabled = !scanning && _session != null;
            RestoreButton.IsEnabled = !scanning; ResultsList.Tag = !scanning;
            _inspector?.SetScanning(scanning);
        }
        private void Filter_Changed(object sender, RoutedEventArgs e) { if (_ready && !_binding) ApplyFilters(); }
        private void ApplyFilters()
        {
            var selected = ResultsList.SelectedItem as ModelIssue;
            var matches = _all.Where(MatchesFilters);
            _filtered = (Scanning ? matches : matches.OrderBy(i => i.PriorityRank).ThenBy(i => i.LevelName).ThenBy(i => i.ElementIdValue)).ToList();
            _visible.Clear(); foreach (var issue in _filtered) _visible.Add(issue);
            if (selected != null && _filtered.Contains(selected)) ResultsList.SelectedItem = selected;
            _activeCount = _all.Count(i => !i.Ignored);
            _confirmedCount = _all.Count(i => !i.IsApproximate && i.Kind != IssueKind.MepUnconnected);
            _approximateCount = _all.Count(i => i.IsApproximate);
            UpdateResultText();
        }
        private bool MatchesFilters(ModelIssue issue)
        {
            var search = (SearchBox.Text ?? "").Trim(); var status = SelectedValue(StatusCombo); var category = SelectedValue(CategoryCombo);
            return (status == "Tous" || status == "À traiter" && !issue.Ignored || issue.StatusText == status)
                && (category == "Tous les contrôles" || issue.Category == category)
                && (search.Length == 0 || issue.SearchText.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }
        private void UpdateResultText()
        {
            SummaryText.Text = (Scanning ? UiLanguage.Get("SmartCheckWindow.xaml.ProvisionalSummary") : _session != null && (_session.Cancelled || _session.Error != null) ? UiLanguage.Get("SmartCheckWindow.xaml.PartialSummary") : "")
                + _activeCount + UiLanguage.Get("SmartCheckWindow.xaml.ToResolve") + _confirmedCount + UiLanguage.Get("SmartCheckWindow.xaml.ConfirmedIntersectionS") + _approximateCount + UiLanguage.Get("SmartCheckWindow.xaml.SuspectedClashEs");
            ResultCountText.Text = _filtered.Count + " / " + _all.Count + UiLanguage.Get("SmartCheckWindow.xaml.ResultS2") + (Scanning ? UiLanguage.Get("SmartCheckWindow.xaml.ProgressiveDisplayEnterDetails") : UiLanguage.Get("SmartCheckWindow.xaml.EnterDetails"));
            EmptyPanel.Visibility = _filtered.Count == 0 ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            if (_filtered.Count == 0)
            {
                bool partial = _session == null || _session.Cancelled || _session.Error != null;
                EmptyTitle.Text = _all.Count > 0 ? UiLanguage.Get("SmartCheckWindow.xaml.NoResultsMatchTheseFilters") : Scanning ? UiLanguage.Get("SmartCheckWindow.xaml.AnalysisInProgress") : _session?.Error != null ? UiLanguage.Get("SmartCheckWindow.xaml.AnalysisStoppedByAnError") : partial ? UiLanguage.Get("SmartCheckWindow.xaml.NoResultsAvailable") : UiLanguage.Get("SmartCheckWindow.xaml.NoClashesDetectedInThisScope");
                EmptyDescription.Text = _all.Count > 0 ? UiLanguage.Get("SmartCheckWindow.xaml.ChangeTheStatusOrCheckOrClearThe") : Scanning
                    ? UiLanguage.Get("SmartCheckWindow.xaml.ElementsAndLinksArePreparedFirstThenResults")
                    : _session?.Error != null
                    ? UiLanguage.Get("SmartCheckWindow.xaml.ThisResultDoesNotProveThatThereAre")
                    : partial
                    ? UiLanguage.Get("SmartCheckWindow.xaml.RunAFullAnalysisToGetASummary") : _session.SourceCount == 0
                    ? UiLanguage.Get("SmartCheckWindow.xaml.NoElementsWereFoundInTheSelectedCategories")
                    : UiLanguage.Get("SmartCheckWindow.xaml.SeeTheSummaryForTheCategoriesLinksAnd");
            }
            OverviewButton.IsEnabled = !Scanning && _filtered.Count > 0; ExportButton.IsEnabled = !Scanning && _session != null;
        }
        private ModelIssue IssueFrom(object sender) => (sender as FrameworkElement)?.DataContext as ModelIssue ?? ResultsList.SelectedItem as ModelIssue;
        private void Focus_Click(object sender, RoutedEventArgs e) => Focus(IssueFrom(sender));
        private void Context_Click(object sender, RoutedEventArgs e) => ToggleContext();
        private void ToggleContext()
        {
            if (Scanning || _busy || _queuedAction.HasValue || _handler.DisplayedIssue == null) return;
            Queue(SmartAction.ToggleContext);
        }
        private void Reservation_Click(object sender, RoutedEventArgs e) => CreateReservation(_handler.DisplayedIssue);
        private void CreateReservation(ModelIssue issue)
        {
            if (issue == null || !issue.CanCreateReservation || issue.IsApproximate || Scanning || _busy || _queuedAction.HasValue || _stale) return;
            _handler.TutorialReservation = _tutorialPrepared;
            _tutorialActionFailed = false;
            _handler.FocusIssue = issue; RunText.Text = UiLanguage.Get("SmartCheckWindow.xaml.CreatingTheOpeningWithTheAutoOpeningSettings");
            Queue(SmartAction.CreateReservation);
        }
        private void UpdateFocusActions()
        {
            var issue = _handler.DisplayedIssue;
            FocusBar.Visibility = issue == null ? WpfVisibility.Collapsed : WpfVisibility.Visible;
            FocusBarText.Text = _handler.ContextVisible ? UiLanguage.Get("SmartCheckWindow.xaml.ContextWithoutSectionBox") : UiLanguage.Get("SmartCheckWindow.xaml.ClashIsolatedInRevit");
            ContextButton.Content = _handler.ContextVisible ? UiLanguage.Get("SmartCheckWindow.xaml.IsolateClash") : UiLanguage.Get("SmartCheckWindow.xaml.ShowSurroundings");
            ContextButton.IsEnabled = !Scanning && !_busy && !_queuedAction.HasValue;
            ReservationButton.Visibility = issue?.CanCreateReservation == true && !issue.IsApproximate ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            ReservationButton.IsEnabled = !Scanning && !_busy && !_queuedAction.HasValue && !_stale;
            _inspector?.SetActions(issue, _handler.ContextVisible, Scanning || _busy || _queuedAction.HasValue, _stale);
        }
        private void Focus(ModelIssue issue)
        {
            if (issue == null || _busy || _queuedAction.HasValue || _session != null && !_session.Complete) return;
            _handler.FocusIssue = issue; Queue(SmartAction.FocusApply);
        }
        private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var node = e.OriginalSource as DependencyObject;
            while (node != null)
            {
                if (node is System.Windows.Controls.Primitives.ButtonBase) return;
                node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            Focus(ResultsList.SelectedItem as ModelIssue);
        }
        private void Details_Click(object sender, RoutedEventArgs e) => Inspect(IssueFrom(sender));
        private void Inspect(ModelIssue issue)
        {
            if (issue == null) return;
            _inspector?.Close();
            _inspector = new SmartIssueInspector(issue, Focus, SaveDecision, CapturePreview, Navigate, ToggleContext, CreateReservation) { Owner = this };
            _inspector.SetScanning(Scanning);
            if (_stale) _inspector.MarkStale();
            _inspector.Show();
            UpdateFocusActions();
            if (_tutorialPrepared && IsTutorialCrossing(issue))
            { _tutorialDetailOpened = true; BIMaestro.Tutorials.DemoTourService.ReportAction(this, "clash-detail-opened"); }
        }
        private bool SaveDecision(ModelIssue issue, string status, string comment)
        {
            if (_stale) { MessageBox.Show(this, UiLanguage.Get("SmartCheckWindow.xaml.RunTheAnalysisAgainBeforeSavingADecision"), "Clash 3D"); return false; }
            SmartCheckState.SetIssueStatus(_setup.DocumentKey, issue, status, comment, Environment.UserName);
            ApplyFilters();
            if (!Scanning) RunText.Text = SmartCheckState.LastSaveError == null ? UiLanguage.Get("SmartCheckWindow.xaml.DecisionSavedResolvedRemainsAManualDecisionThe")
                : UiLanguage.Get("SmartCheckWindow.xaml.DecisionUpdatedInThisSessionButNotSaved") + SmartCheckState.LastSaveError;
            return SmartCheckState.LastSaveError == null;
        }
        private void CapturePreview(ModelIssue issue)
        {
            if (Scanning || _busy || _queuedAction.HasValue) return;
            _handler.FocusIssue = issue; _handler.ThumbnailFolder = SmartCheckState.GetThumbnailFolder(_setup.DocumentKey);
            Queue(SmartAction.GenerateThumbnails);
        }
        private void Navigate(ModelIssue current, int offset)
        {
            int index = _filtered.IndexOf(current); if (_filtered.Count == 0) return;
            index = Math.Max(0, Math.Min(_filtered.Count - 1, index + offset)); ResultsList.SelectedItem = _filtered[index];
            ResultsList.ScrollIntoView(_filtered[index]); Inspect(_filtered[index]);
        }
        private void Overview_Click(object sender, RoutedEventArgs e)
        { _handler.FocusIssues = _filtered; _handler.ShowAllEnabled = true; Queue(SmartAction.ShowAllApply); }
        private void Restore_Click(object sender, RoutedEventArgs e) => Queue(SmartAction.RestoreView);
        private void Diagnostics_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var text = UiLanguage.Get("SmartCheckWindow.xaml.Scope") + _session.Options.Scope + UiLanguage.Get("SmartCheckWindow.xaml.SourceElements") + _session.SourceCount + UiLanguage.Get("SmartCheckWindow.xaml.IndexedObstacles") + _session.CandidateCount
                + UiLanguage.Get("SmartCheckWindow.xaml.PairsChecked") + _session.TestedPairs + UiLanguage.Get("SmartCheckWindow.xaml.Threshold") + _session.Options.MinimumVolumeMm3 + " mm³\n"
                + UiLanguage.Get("SmartCheckWindow.xaml.SimpleContactsAreNotIntersectionsDirectPhysicalConnecti")
                + UiLanguage.Get("SmartCheckWindow.xaml.OpeningsAreNotValidatedByFamilyNameWall")
                + "\n\n" + (_session.Error ?? "") + "\n" + (_session.Diagnostics.Count == 0 ? UiLanguage.Get("SmartCheckWindow.xaml.NoReadIssuesReported") : string.Join("\n", _session.Diagnostics));
            var win = new Window { Title = UiLanguage.Get("SmartCheckWindow.xaml.Clash3DAnalysisSummary"), Owner = this, Width = 680, Height = 520, MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20), BorderThickness = new Thickness(0) } };
            win.Show();
        }
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var dialog = new SaveFileDialog { Title = UiLanguage.Get("SmartCheckWindow.xaml.ExportDisplayedResults"), FileName = "Clash3D_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                Filter = UiLanguage.Get("SmartCheckWindow.xaml.StandaloneReportHtmlHtmlCSVTableCsvCsv"), AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                WriteExport(dialog.FileName, dialog.FilterIndex == 2);
                Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            }
            catch (Exception ex) { RunText.Text = "Export : " + ex.Message; }
        }
        internal void WriteExport(string path, bool csv)
        {
            if (_session == null) throw new InvalidOperationException(UiLanguage.Get("SmartCheckWindow.xaml.RunAnAnalysisBeforeExporting"));
            var text = csv ? SmartClashReport.Csv(_filtered)
                : SmartClashReport.Html(_setup.Title, _filtered, _session, _stale, StatusCombo.SelectedItem + " · " + CategoryCombo.SelectedItem + " · " + SearchBox.Text);
            if (_tutorialPrepared && !_stale && DemoClashExerciseExportReady())
                text = csv ? SmartClashReport.TutorialCsv(_tutorialInitialCsv, _filtered)
                    : SmartClashReport.TutorialHtml(_tutorialInitialHtml, text, _tutorialReservationId.GetIdLongValue().ToString());
            File.WriteAllText(path, text, new UTF8Encoding(csv));
            Licensing.OperationTelemetry.Track("SmartClashCommand", "Clash 3D", csv ? "export_csv" : "export_html",
                _tutorialPrepared, new Dictionary<string, int> { ["files_exported"] = 1 });
            RunText.Text = UiLanguage.Get("SmartCheckWindow.xaml.ExportComplete") + path;
            if (_tutorialPrepared && !_stale && DemoClashExerciseExportReady())
            { _tutorialExportWritten = true; BIMaestro.Tutorials.DemoTourService.ReportAction(this, "clash-export-written"); }
        }
        private void Help_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://www.bimaestro.fr/analyse?outil=clash-3d") { UseShellExecute = true }); }
            catch (Exception ex) { RunText.Text = ex.Message; }
        }
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !(e.OriginalSource is TextBox) && ResultsList.SelectedItem is ModelIssue issue) { Inspect(issue); e.Handled = true; }
            if (e.Key == Key.Escape) { if (_session != null && !_session.Complete) Cancel_Click(sender, e); else Close(); e.Handled = true; }
        }
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_closeConfirmed) return;
            e.Cancel = true; _closing = true;
            if (_session != null && !_session.Complete) { _session.CancelRequested = true; if (!_busy && !_queuedAction.HasValue) Queue(SmartAction.ScanBatch); }
            else if (!_busy && !_queuedAction.HasValue) Queue(SmartAction.CloseSession);
        }
        private static List<SmartDisplayChoice> Choices(IEnumerable<string> values) => values.Select(v => new SmartDisplayChoice(v)).ToList();
        private static string SelectedValue(ComboBox combo) => (combo.SelectedItem as SmartDisplayChoice)?.Value;
    }
}

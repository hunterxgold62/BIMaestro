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
        private void UpdateDocumentText() => DocumentText.Text = _setup.Title + " · " + _setup.Selection.Count + UiLanguage.T(" objet(s) sélectionné(s) · ", " selected element(s) · ")
            + _setup.Links.Count(l => l.Loaded) + UiLanguage.T(" lien(s) ou import(s) disponible(s)", " available link(s) or import(s)");
        private SmartScanOptions ReadOptions()
        {
            double volume;
            if (!double.TryParse(VolumeBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out volume) || double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0 || volume > 1000000000)
                throw new InvalidOperationException(UiLanguage.T("Le seuil doit être un nombre compris entre 0 et 1 000 000 000 mm³.", "The threshold must be a number between 0 and 1,000,000,000 mm³."));
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
            if (options.SourceCategories().Count == 0 && !options.WallSupports) throw new InvalidOperationException(UiLanguage.T("Choisissez au moins une catégorie à contrôler.", "Select at least one category to check."));
            if (!options.LocalClashes && !options.LinkedClashes && !options.OpenConnectors && !options.WallSupports)
                throw new InvalidOperationException(UiLanguage.T("Activez au moins un contrôle.", "Enable at least one check."));
            if (options.Scope == SmartScanScope.Selection && _setup.Selection.Count == 0)
                throw new InvalidOperationException(UiLanguage.T("La sélection est vide. Sélectionnez des objets dans Revit et cliquez sur Actualiser.", "The selection is empty. Select elements in Revit and click Refresh."));
            if (options.Scope == SmartScanScope.ActiveView && !_setup.ViewSupported)
                throw new InvalidOperationException(UiLanguage.T("Cette vue ne permet pas l'analyse. Choisissez une vue de modèle puis cliquez sur Actualiser.", "This view cannot be analyzed. Choose a model view, then click Refresh."));
            return options;
        }
        private void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _queuedAction.HasValue || Scanning) return;
            // Read the current selection/view in a valid API callback, including changes made after opening this window.
            _startAfterRefresh = true;
            RunText.Text = UiLanguage.T("Actualisation du périmètre avant l'analyse…", "Refreshing the scope before analysis…");
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
                SummaryText.Text = UiLanguage.T("Bilan provisoire · analyse en cours", "Provisional summary · analysis in progress"); FilterPanel.Visibility = WpfVisibility.Visible;
                ResultCountText.Text = UiLanguage.T("Les résultats apparaissent au fur et à mesure.", "Results appear as they are found.");
                EmptyPanel.Visibility = WpfVisibility.Visible; EmptyTitle.Text = UiLanguage.T("Analyse en cours", "Analysis in progress");
                EmptyDescription.Text = UiLanguage.T("Préparation des objets et des liens. Les résultats apparaîtront dès leur détection, puis leurs aperçus.", "Preparing elements and links. Results will appear as soon as they are detected, followed by their previews.");
                RunProgress.Visibility = WpfVisibility.Visible; RunProgress.IsIndeterminate = true;
                SetScanning(true); Queue(SmartAction.ScanBatch);
            }
            catch (Exception ex) { RunText.Text = ex.Message; SettingsExpander.IsExpanded = true; }
        }
        private void Cancel_Click(object sender, RoutedEventArgs e)
        { if (_session != null) { _session.CancelRequested = true; RunText.Text = UiLanguage.T("Annulation demandée…", "Cancellation requested…"); CancelButton.IsEnabled = false; } }
        private void RefreshScope_Click(object sender, RoutedEventArgs e) => Queue(SmartAction.RefreshSetup);
        private void Setup_Refreshed(SmartScanSetup next)
        {
            // Focusing a clash selects one pipe in Revit. The prepared exercise
            // must keep analysing its three pipes when that live selection changes.
            if (_tutorialPrepared) next.Selection = BIMaestro.Tutorials.DemoClashExercise.Sources(OwnerDocument);
            var selected = new HashSet<long>(_setup.Links.Where(l => l.Selected).Select(l => l.Id.GetIdLongValue()));
            foreach (var link in next.Links) link.Selected = selected.Contains(link.Id.GetIdLongValue()) && link.Loaded;
            _setup = next; LinksList.ItemsSource = next.Links; UpdateDocumentText();
            RunText.Text = UiLanguage.T("Vue, sélection et liens actualisés. Choisissez le périmètre puis lancez l'analyse.", "View, selection and links refreshed. Choose the scope, then run the analysis.");
        }
        private void Model_Changed()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Model_Changed)); return; }
            _stale = true; StaleText.Visibility = WpfVisibility.Visible;
            _inspector?.MarkStale();
            UpdateFocusActions();
            if (_session != null && !_session.Complete) RunText.Text = UiLanguage.T("La maquette a changé pendant l'analyse. Annulation et conservation des résultats partiels.", "The model changed during the analysis. Cancelling and keeping partial results.");
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
                else if (request != ExternalEventRequest.Pending) { _queuedAction = null; _queueTimer.Stop(); Handler_Failed(UiLanguage.T("Revit n'a pas accepté l'action. Réessayez après avoir fermé ses dialogues.", "Revit did not accept the action. Close any open Revit dialogs and try again.")); }
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
                RunText.Text = _session.Stage + " · " + _session.ProcessedSources + "/" + _session.SourceCount + UiLanguage.T(" objet(s) · ", " element(s) · ")
                    + _session.CandidateCount + UiLanguage.T(" obstacle(s) · ", " obstacle(s) · ") + _session.Issues.Count + UiLanguage.T(" résultat(s) · ", " result(s) · ") + _session.Seconds.ToString("F1") + " s";
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
            RunText.Text = (partial ? UiLanguage.T("Analyse interrompue · résultats partiels", "Analysis interrupted · partial results") : _session.Stage) + " · " + _session.SourceCount + UiLanguage.T(" objet(s) · ", " element(s) · ") + _session.Seconds.ToString("F1") + " s"
                + (_session.Diagnostics.Count > 0 ? " · " + _session.Diagnostics.Count + UiLanguage.T(" point(s) à consulter dans le bilan", " item(s) to review in the summary") : "");
            if (_session.Error != null) RunText.Text += " · " + _session.Error;
            if (absent > 0) RunText.Text += " · " + absent + UiLanguage.T(" résultat(s) absent(s) depuis la précédente analyse du même périmètre", " result(s) no longer present since the previous analysis of the same scope");
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
            SummaryText.Text = (Scanning ? UiLanguage.T("Bilan provisoire · ", "Provisional summary · ") : _session != null && (_session.Cancelled || _session.Error != null) ? UiLanguage.T("Bilan partiel · ", "Partial summary · ") : "")
                + _activeCount + UiLanguage.T(" à traiter · ", " to resolve · ") + _confirmedCount + UiLanguage.T(" intersection(s) confirmée(s) · ", " confirmed intersection(s) · ") + _approximateCount + UiLanguage.T(" suspicion(s)", " suspected clash(es)");
            ResultCountText.Text = _filtered.Count + " / " + _all.Count + UiLanguage.T(" résultat(s)", " result(s)") + (Scanning ? UiLanguage.T(" · affichage progressif · Entrée : détail", " · progressive display · Enter: details") : UiLanguage.T(" · Entrée : détail", " · Enter: details"));
            EmptyPanel.Visibility = _filtered.Count == 0 ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            if (_filtered.Count == 0)
            {
                bool partial = _session == null || _session.Cancelled || _session.Error != null;
                EmptyTitle.Text = _all.Count > 0 ? UiLanguage.T("Aucun résultat avec ces filtres", "No results match these filters") : Scanning ? UiLanguage.T("Analyse en cours", "Analysis in progress") : _session?.Error != null ? UiLanguage.T("Analyse interrompue par une erreur", "Analysis stopped by an error") : partial ? UiLanguage.T("Aucun résultat disponible", "No results available") : UiLanguage.T("Aucun conflit détecté dans ce périmètre", "No clashes detected in this scope");
                EmptyDescription.Text = _all.Count > 0 ? UiLanguage.T("Changez le statut ou le contrôle, ou effacez la recherche.", "Change the status or check, or clear the search.") : Scanning
                    ? UiLanguage.T("Les objets et les liens sont préparés, puis les résultats apparaissent dès leur détection. Le bilan reste provisoire jusqu'à la fin.", "Elements and links are prepared first, then results appear as soon as they are detected. The summary remains provisional until the end.")
                    : _session?.Error != null
                    ? UiLanguage.T("Ce résultat ne permet pas de conclure à l’absence de conflits. Consultez le bilan pour le détail de l’erreur avant de relancer.", "This result does not prove that there are no clashes. Check the summary for error details before running again.")
                    : partial
                    ? UiLanguage.T("Lancez une analyse complète pour obtenir un bilan.", "Run a full analysis to get a summary.") : _session.SourceCount == 0
                    ? UiLanguage.T("Aucun objet des catégories choisies n'a été trouvé. Ajustez les catégories ou le périmètre.", "No elements were found in the selected categories. Adjust the categories or the scope.")
                    : UiLanguage.T("Consultez le bilan pour connaître les catégories, les liens et les éventuelles limites de l'analyse.", "See the summary for the categories, links and any limitations of the analysis.");
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
            _handler.FocusIssue = issue; RunText.Text = UiLanguage.T("Création de la réservation avec les réglages d'Autoréservation…", "Creating the opening with the Auto-Opening settings…");
            Queue(SmartAction.CreateReservation);
        }
        private void UpdateFocusActions()
        {
            var issue = _handler.DisplayedIssue;
            FocusBar.Visibility = issue == null ? WpfVisibility.Collapsed : WpfVisibility.Visible;
            FocusBarText.Text = _handler.ContextVisible ? UiLanguage.T("Contexte sans coupe", "Context without section box") : UiLanguage.T("Conflit isolé dans Revit", "Clash isolated in Revit");
            ContextButton.Content = _handler.ContextVisible ? UiLanguage.T("Isoler le conflit", "Isolate clash") : UiLanguage.T("Voir autour", "Show surroundings");
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
            if (_stale) { MessageBox.Show(this, UiLanguage.T("Relancez l'analyse avant d'enregistrer une décision sur une maquette modifiée.", "Run the analysis again before saving a decision on a modified model."), "Clash 3D"); return false; }
            SmartCheckState.SetIssueStatus(_setup.DocumentKey, issue, status, comment, Environment.UserName);
            ApplyFilters();
            if (!Scanning) RunText.Text = SmartCheckState.LastSaveError == null ? UiLanguage.T("Décision enregistrée. « Traité » reste une décision manuelle, sans correction automatique de la maquette.", "Decision saved. \"Resolved\" remains a manual decision; the model is not corrected automatically.")
                : UiLanguage.T("Décision mise à jour dans cette session, mais non sauvegardée : ", "Decision updated in this session but not saved: ") + SmartCheckState.LastSaveError;
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
            var text = UiLanguage.T("Périmètre : ", "Scope: ") + _session.Options.Scope + UiLanguage.T("\nObjets de départ : ", "\nSource elements: ") + _session.SourceCount + UiLanguage.T("\nObstacles indexés : ", "\nIndexed obstacles: ") + _session.CandidateCount
                + UiLanguage.T("\nPaires examinées : ", "\nPairs checked: ") + _session.TestedPairs + UiLanguage.T("\nSeuil : ", "\nThreshold: ") + _session.Options.MinimumVolumeMm3 + " mm³\n"
                + UiLanguage.T("\nLes contacts simples ne sont pas des intersections. Les connexions physiques directes sont exclues. Les maillages et géométries partielles produisent des suspicions.", "\nSimple contacts are not intersections. Direct physical connections are excluded. Meshes and partial geometry produce suspected clashes.")
                + UiLanguage.T("\nLes réservations ne sont pas validées par nom de famille ; les intersections de parois restent à coordonner.", "\nOpenings are not validated by family name; wall intersections still need coordination.")
                + "\n\n" + (_session.Error ?? "") + "\n" + (_session.Diagnostics.Count == 0 ? UiLanguage.T("Aucune anomalie de lecture signalée.", "No read issues reported.") : string.Join("\n", _session.Diagnostics));
            var win = new Window { Title = UiLanguage.T("Clash 3D · bilan de l'analyse", "Clash 3D · analysis summary"), Owner = this, Width = 680, Height = 520, MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20), BorderThickness = new Thickness(0) } };
            win.Show();
        }
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var dialog = new SaveFileDialog { Title = UiLanguage.T("Exporter les résultats affichés", "Export displayed results"), FileName = "Clash3D_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                Filter = UiLanguage.T("Rapport autonome (*.html)|*.html|Tableau CSV (*.csv)|*.csv", "Standalone report (*.html)|*.html|CSV table (*.csv)|*.csv"), AddExtension = true };
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
            if (_session == null) throw new InvalidOperationException(UiLanguage.T("Lancez une analyse avant d'exporter.", "Run an analysis before exporting."));
            var text = csv ? SmartClashReport.Csv(_filtered)
                : SmartClashReport.Html(_setup.Title, _filtered, _session, _stale, StatusCombo.SelectedItem + " · " + CategoryCombo.SelectedItem + " · " + SearchBox.Text);
            if (_tutorialPrepared && !_stale && DemoClashExerciseExportReady())
                text = csv ? SmartClashReport.TutorialCsv(_tutorialInitialCsv, _filtered)
                    : SmartClashReport.TutorialHtml(_tutorialInitialHtml, text, _tutorialReservationId.GetIdLongValue().ToString());
            File.WriteAllText(path, text, new UTF8Encoding(csv));
            Licensing.OperationTelemetry.Track("SmartClashCommand", "Clash 3D", csv ? "export_csv" : "export_html",
                _tutorialPrepared, new Dictionary<string, int> { ["files_exported"] = 1 });
            RunText.Text = UiLanguage.T("Export terminé : ", "Export complete: ") + path;
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

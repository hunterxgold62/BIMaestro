using Autodesk.Revit.DB;
using BIMaestro.Tutorials;
using System;
using System.Linq;
using System.Windows;
using WpfVisibility = System.Windows.Visibility;

namespace Analyse
{
    public partial class SmartCheckWindow
    {
        private bool _tutorialPrepared, _tutorialCorrected, _tutorialActionFailed, _tutorialRescanVerified;
        private bool _tutorialInitialVerified, _tutorialDetailOpened, _tutorialFocusObserved, _tutorialExportWritten;
        private ElementId _tutorialCrossA, _tutorialCrossB;
        private bool _tutorialWallObserved, _tutorialReservationCreated, _tutorialReservationRescanVerified;
        private ElementId _tutorialReservationId;
        private bool _tutorialInspectWall;
        private Window _tutorialObservation;

        internal bool RestartPreparedTutorial(SmartScanSetup setup)
        {
            if (_busy || _queuedAction.HasValue || _session != null && !_session.Complete) return false;
            _inspector?.Close(); _inspector = null;
            _session?.Dispose(); _session = null; _setup = setup;
            _handler.Session = null; _tutorialRescanVerified = false;
            _previousKeys = null; _previousSignature = null; _currentSignature = null;
            _all.Clear(); _filtered.Clear(); _visible.Clear(); ResultsList.ItemsSource = _visible; _categories.Clear();
            _publishedCount = _activeCount = _confirmedCount = _approximateCount = 0;
            _restoreResults = null; _thumbnailFolder = null;
            FilterPanel.Visibility = System.Windows.Visibility.Collapsed; SummaryText.Text = "";
            _stale = false; StaleText.Visibility = System.Windows.Visibility.Collapsed;
            _tutorialPrepared = false; _tutorialCorrected = false;
            UpdateDocumentText();
            return DemoTourService.AttachIfRequested("clash-3d", this);
        }

        internal void ConfigureTutorial(bool prepared)
        {
            TutorialColumn.Width = new System.Windows.GridLength(344);
            Width = Math.Max(Width, 1180); MinWidth = 1100;
            if (!prepared || _tutorialPrepared) return;
            if (!DemoClashExercise.IsTraining(OwnerDocument) ||
                !_setup.Selection.OrderBy(id => id.GetIdLongValue()).SequenceEqual(DemoClashExercise.Sources(OwnerDocument).OrderBy(id => id.GetIdLongValue())))
                throw new InvalidOperationException("Relance le parcours Clash 3D pour préparer les trois tuyaux d'essai.");
            _tutorialPrepared = true; _tutorialCorrected = false;
            _tutorialInitialVerified = _tutorialDetailOpened = _tutorialFocusObserved = _tutorialExportWritten = _tutorialRescanVerified = false;
            _tutorialWallObserved = _tutorialReservationCreated = _tutorialReservationRescanVerified = _tutorialInspectWall = false;
            _tutorialReservationId = null;
            var pipes = _setup.Selection.Select(OwnerDocument.GetElement).ToList();
            _tutorialCrossA = pipes.Single(p => p.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == DemoClashExercise.Prefix + "CROSS_A").Id;
            _tutorialCrossB = pipes.Single(p => p.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == DemoClashExercise.Prefix + "CROSS_B").Id;
            ScopeCombo.SelectedIndex = 2;
            PipesCheck.IsChecked = LocalCheck.IsChecked = true;
            DuctsCheck.IsChecked = TraysCheck.IsChecked = ConduitsCheck.IsChecked = FittingsCheck.IsChecked =
                EquipmentCheck.IsChecked = GenericCheck.IsChecked = LinksCheck.IsChecked = InsulationCheck.IsChecked =
                ConnectorsCheck.IsChecked = WallsCheck.IsChecked = false;
            VolumeBox.Text = "10"; SearchBox.Clear(); StatusCombo.SelectedIndex = 1; CategoryCombo.SelectedIndex = 0;
            SettingsExpander.IsExpanded = true;
        }

        internal void PrepareTutorialStep(string target, string completionEvent = null)
        {
            _tutorialInspectWall = completionEvent == "clash-wall-observed";
            SettingsExpander.IsExpanded = target == "ScopeCombo" || target == "LocalCheck" || target == "VolumeBox";
            TutorialFixButton.Visibility = _tutorialPrepared && target == "TutorialFixButton" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            TutorialInspectButton.Visibility = _tutorialPrepared && target == "TutorialInspectButton" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            TutorialFocusButton.Visibility = _tutorialPrepared && target == "TutorialFocusButton" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            TutorialFixButton.IsEnabled = !_tutorialCorrected && !_busy && !_queuedAction.HasValue;
            if (target == "TutorialInspectButton" || target == "TutorialFocusButton")
            {
                var crossing = _filtered.FirstOrDefault(issue => _tutorialInspectWall ? DemoClashExercise.IsWall(OwnerDocument, issue) : IsTutorialCrossing(issue));
                if (crossing != null) { ResultsList.SelectedItem = crossing; ResultsList.ScrollIntoView(crossing); }
            }
        }

        internal bool HasCompletedTutorialAction(string action)
        {
            if (!_tutorialPrepared || action == null) return false;
            switch (action)
            {
                case "clash-scan-verified": return _tutorialInitialVerified;
                case "clash-detail-opened": return _tutorialDetailOpened;
                case "clash-focus-observed": return _tutorialFocusObserved;
                case "clash-corrected": return _tutorialCorrected;
                case "clash-rescan-verified": return _tutorialRescanVerified && !_stale;
                case "clash-export-written": return _tutorialExportWritten;
                case "clash-wall-observed": return _tutorialWallObserved;
                case "clash-reservation-created": return _tutorialReservationCreated && OwnerDocument.GetElement(_tutorialReservationId) is FamilyInstance;
                case "clash-reservation-verified": return _tutorialReservationRescanVerified && !_stale;
                default: return false;
            }
        }

        internal void EndTutorial()
        {
            _tutorialPrepared = false;
            _handler.TutorialReservation = false;
            _tutorialObservation?.Close(); _tutorialObservation = null;
            TutorialFixButton.Visibility = System.Windows.Visibility.Collapsed;
            TutorialInspectButton.Visibility = TutorialFocusButton.Visibility = System.Windows.Visibility.Collapsed;
            TutorialColumn.Width = new System.Windows.GridLength(0);
            MinWidth = 760;
        }

        private bool IsTutorialCrossing(ModelIssue issue) => issue != null && !issue.IsApproximate && issue.Kind == IssueKind.LocalClash &&
            (issue.ElementId == _tutorialCrossA && issue.RelatedId == _tutorialCrossB || issue.ElementId == _tutorialCrossB && issue.RelatedId == _tutorialCrossA);

        private void TutorialScanCompleted()
        {
            if (!_tutorialPrepared) return;
            if (_tutorialReservationCreated)
            {
                _tutorialReservationRescanVerified = OwnerDocument.GetElement(_tutorialReservationId) is FamilyInstance &&
                    _session.Complete && !_session.Cancelled && _session.Error == null && _session.SourceCount == 3 &&
                    _session.Options.Scope == SmartScanScope.Selection && _session.Options.MinimumVolumeMm3 == 10 &&
                    _session.Options.LocalClashes && _session.Issues.Count <= 1 && _session.Issues.All(issue => DemoClashExercise.IsWall(OwnerDocument, issue));
                if (_tutorialReservationRescanVerified) DemoTourService.ReportAction(this, "clash-reservation-verified");
                else RunText.Text += " · Le guide attend une analyse complète des trois tuyaux d'essai après la réservation.";
                return;
            }
            bool verified = DemoClashExercise.Verify(OwnerDocument, _session, _tutorialCorrected);
            _tutorialRescanVerified = verified && _tutorialCorrected;
            if (verified && !_tutorialCorrected) _tutorialInitialVerified = true;
            if (verified)
                DemoTourService.ReportAction(this, _tutorialCorrected ? "clash-rescan-verified" : "clash-scan-verified");
            else RunText.Text += " · Le guide attend " + (_tutorialCorrected ? "la seule traversée du mur" : "les deux intersections d'essai") +
                ". Garde Sélection, Tuyaux, Collisions dans la maquette et le seuil de 10 mm³ ; désactive les autres contrôles, puis relance.";
        }

        private bool DemoClashExerciseExportReady() => _tutorialCorrected && _tutorialReservationCreated && _tutorialReservationRescanVerified && _session != null && _session.Complete &&
            !_session.Cancelled && _session.Error == null && _all.Count <= 1 && _filtered.Count == _all.Count &&
            string.IsNullOrWhiteSpace(SearchBox.Text);

        private void TutorialFix_Click(object sender, RoutedEventArgs args)
        {
            if (!_tutorialPrepared || _tutorialCorrected || _busy || _queuedAction.HasValue) return;
            _tutorialActionFailed = false;
            TutorialFixButton.IsEnabled = false;
            Queue(SmartAction.TutorialCorrect);
        }

        private void TutorialInspect_Click(object sender, RoutedEventArgs args) => Inspect(ResultsList.SelectedItem as ModelIssue);
        private void TutorialFocus_Click(object sender, RoutedEventArgs args) => Focus(ResultsList.SelectedItem as ModelIssue);
        private void Tutorial_Click(object sender, RoutedEventArgs args)
        {
            if (_tutorialPrepared && DemoTourService.IsActive(this))
            { RunText.Text = "Poursuis le guide actuel. Pour recommencer la scène, quitte le guide puis relance Clash 3D depuis Parcours guidés."; return; }
            DemoTourService.StartInWindow("clash-3d", this);
        }

        private void TutorialActionCompleted()
        {
            if (!_tutorialPrepared || _tutorialActionFailed) return;
            if (_handler.Action == SmartAction.TutorialCorrect)
            {
                _tutorialCorrected = true;
                RunText.Text = "Le tuyau d'essai a été relevé de 300 mm. Relance l'analyse pour vérifier la disparition du conflit entre tuyaux.";
                DemoTourService.ReportAction(this, "clash-corrected");
            }
            else if (_handler.Action == SmartAction.CreateReservation && _handler.TutorialReservation &&
                DemoClashExercise.IsWall(OwnerDocument, _handler.FocusIssue) && _handler.CreatedReservationId != null &&
                OwnerDocument.GetElement(_handler.CreatedReservationId) is FamilyInstance reservation &&
                reservation.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == DemoClashExercise.Prefix + "RESERVATION")
            {
                _tutorialReservationId = reservation.Id; _tutorialReservationCreated = true; _tutorialReservationRescanVerified = false;
                DemoTourService.ReportAction(this, "clash-reservation-created");
            }
            else if (_handler.Action == SmartAction.FocusApply && _tutorialInspectWall && DemoClashExercise.IsWall(OwnerDocument, _handler.FocusIssue))
                _tutorialObservation = DemoTourService.ObserveHistoryResult(this, new System.Windows.Interop.WindowInteropHelper(this).Owner,
                    "Observer la traversée du mur", "Observe le tuyau orange et le mur bleu. De retour dans Clash 3D, le bouton Créer la réservation utilisera la famille rectangulaire fournie pour cet essai ; tes réglages personnels restent conservés.",
                    () => { _tutorialWallObserved = true; DemoTourService.ReportAction(this, "clash-wall-observed"); });
            else if (_handler.Action == SmartAction.FocusApply && IsTutorialCrossing(_handler.FocusIssue))
                _tutorialObservation = DemoTourService.ObserveHistoryResult(this, new System.Windows.Interop.WindowInteropHelper(this).Owner,
                    "Observer le conflit dans Revit", "L'objet contrôlé est orange et l'obstacle bleu. Observe leur croisement : le guide va ensuite relever le tuyau d'essai de 300 mm. Dans ton projet, choisis une correction adaptée au réseau et à ses raccordements.",
                    () => { _tutorialFocusObserved = true; DemoTourService.ReportAction(this, "clash-focus-observed"); });
        }
    }
}

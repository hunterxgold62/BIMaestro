using Autodesk.Revit.UI;
using BIMaestro.Localization;
using System;
using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using System.Windows.Threading;

namespace BIMaestro.ViewHover
{
    public partial class ViewThumbnailBatchWindow : Window
    {
        private readonly ViewThumbnailBatchStartHandler _startHandler;
        private readonly ExternalEvent _startEvent;
        private readonly DispatcherTimer _displayTimer;
        private bool _updatingHoverSettings;

        internal ViewThumbnailBatchWindow(
            ViewThumbnailBatchStartHandler startHandler,
            ExternalEvent startEvent,
            string documentTitle)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            foreach (int size in new[] { 50, 75, 100, 125, 150, 200 })
            {
                string label = size == 100 ? UiLanguage.T("100 % (actuelle)", "100% (current)") : size + " %";
                BrowserSizeComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = size });
                ViewTabsSizeComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = size });
            }
            OnHoverPreferencesChanged();
            ViewHoverPreviewPreferences.Changed += OnHoverPreferencesChanged;
            _startHandler = startHandler;
            _startEvent = startEvent;
            DocumentText.Text = UiLanguage.T("Projet : ", "Project: ") +
                                (documentTitle ?? string.Empty);

            _startHandler.StartFailed += OnStartFailed;
            ViewHoverPreviewService.BatchProgressChanged +=
                OnBatchProgressChanged;
            Closed += OnClosed;

            _displayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _displayTimer.Tick += (_, __) =>
                UpdateProgress(ViewHoverPreviewService.GetBatchProgress());
            _displayTimer.Start();

            UpdateProgress(ViewHoverPreviewService.GetBatchProgress());
        }

        private void OnHoverPreferencesChanged()
        {
            _updatingHoverSettings = true;
            try
            {
                HoverPreviewCheckBox.IsChecked = ViewHoverPreviewPreferences.Enabled;
                SelectSize(BrowserSizeComboBox, ViewHoverPreviewPreferences.BrowserSizePercent);
                SelectSize(ViewTabsSizeComboBox, ViewHoverPreviewPreferences.ViewTabsSizePercent);
            }
            finally { _updatingHoverSettings = false; }
        }

        private static void SelectSize(ComboBox comboBox, int size)
        {
            foreach (ComboBoxItem item in comboBox.Items)
                if ((int)item.Tag == size) { comboBox.SelectedItem = item; break; }
        }

        private void PreviewSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingHoverSettings ||
                !(BrowserSizeComboBox?.SelectedItem is ComboBoxItem browser) ||
                !(ViewTabsSizeComboBox?.SelectedItem is ComboBoxItem tabs)) return;
            SavePreviewSizes((int)browser.Tag, (int)tabs.Tag);
        }

        private void ResetPreviewSizes_Click(object sender, RoutedEventArgs e) => SavePreviewSizes(100, 100);

        private void SavePreviewSizes(int browser, int tabs)
        {
            if (ViewHoverPreviewPreferences.SetSizes(browser, tabs)) return;
            OnHoverPreferencesChanged();
            MessageBox.Show(UiLanguage.T("Impossible d’enregistrer la taille des miniatures.",
                "Unable to save the thumbnail sizes."), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void HoverPreviewCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewHoverPreviewPreferences.SetEnabled(HoverPreviewCheckBox.IsChecked == true))
            {
                OnHoverPreferencesChanged();
                MessageBox.Show(UiLanguage.T("Impossible d’enregistrer le réglage des miniatures au survol.",
                    "Unable to save the hover thumbnail setting."), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            ViewPreviewBatchMode mode = ModeComboBox.SelectedIndex switch
            {
                1 => ViewPreviewBatchMode.MissingOnly,
                2 => ViewPreviewBatchMode.All,
                _ => ViewPreviewBatchMode.MissingAndStale
            };
            _startHandler.Request(mode);
            ExternalEventRequest request = _startEvent.Raise();
            if (request == ExternalEventRequest.Denied)
            {
                OnStartFailed(
                    UiLanguage.T(
                        "Revit ne peut pas démarrer le traitement maintenant.",
                        "Revit cannot start the process right now."));
                return;
            }

            StartButton.IsEnabled = false;
            ModeComboBox.IsEnabled = false;
            StatusText.Text = UiLanguage.T("Préparation de la file…", "Preparing the queue…");
        }

        private void PauseResumeButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ViewPreviewBatchProgress progress =
                ViewHoverPreviewService.GetBatchProgress();
            if (progress?.IsPaused == true)
                ViewHoverPreviewService.ResumeBatch();
            else
                ViewHoverPreviewService.PauseBatch();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            ViewHoverPreviewService.StopBatch();
        }

        private void OnStartFailed(string error)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = string.IsNullOrWhiteSpace(error)
                    ? UiLanguage.T("Impossible de démarrer le traitement.", "Unable to start the process.")
                    : error;
                StartButton.IsEnabled = true;
                ModeComboBox.IsEnabled = true;
            }));
        }

        private void OnBatchProgressChanged(
            ViewPreviewBatchProgress progress)
        {
            Dispatcher.BeginInvoke(new Action(() =>
                UpdateProgress(progress)));
        }

        private void UpdateProgress(ViewPreviewBatchProgress progress)
        {
            if (progress == null)
            {
                StartButton.IsEnabled = true;
                ModeComboBox.IsEnabled = true;
                PauseResumeButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                return;
            }

            DocumentText.Text = UiLanguage.T("Projet : ", "Project: ") + progress.DocumentTitle;
            StatusText.Text = UiLanguage.T(progress.Status ?? string.Empty);
            CounterText.Text = progress.Completed + " / " + progress.Total;
            ProgressBar.Maximum = Math.Max(1, progress.Total);
            ProgressBar.Value = Math.Min(
                progress.Completed,
                Math.Max(1, progress.Total));
            CurrentViewText.Text = UiLanguage.T("Vue : ", "View: ") +
                (string.IsNullOrWhiteSpace(progress.CurrentViewName)
                    ? "—"
                    : progress.CurrentViewName);
            ElapsedText.Text = UiLanguage.T("Écoulé : ", "Elapsed: ") + FormatDuration(progress.Elapsed);
            RemainingText.Text = UiLanguage.T("Restant estimé : ", "Estimated remaining: ") +
                (progress.EstimatedRemaining.HasValue
                    ? FormatDuration(progress.EstimatedRemaining.Value)
                    : "—");
            FailedText.Text = UiLanguage.T("Échecs : ", "Failures: ") + progress.Failed;

            bool finished = progress.IsCompleted || progress.IsCanceled;
            StartButton.IsEnabled = finished;
            ModeComboBox.IsEnabled = finished;
            PauseResumeButton.IsEnabled = !finished;
            StopButton.IsEnabled = !finished;
            PauseResumeButton.Content = progress.IsPaused
                ? UiLanguage.T("Reprendre", "Resume")
                : UiLanguage.T("Pause", "Pause");
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            if (duration.TotalHours >= 1)
                return string.Format(
                    "{0:00}h{1:00}m",
                    (int)duration.TotalHours,
                    duration.Minutes);
            if (duration.TotalMinutes >= 1)
                return string.Format(
                    "{0:00}m{1:00}s",
                    (int)duration.TotalMinutes,
                    duration.Seconds);
            return Math.Max(0, (int)duration.TotalSeconds) + "s";
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _displayTimer.Stop();
            ViewHoverPreviewPreferences.Changed -= OnHoverPreferencesChanged;
            ViewHoverPreviewService.BatchProgressChanged -=
                OnBatchProgressChanged;
            _startHandler.StartFailed -= OnStartFailed;
            try { _startEvent.Dispose(); }
            catch { }
        }
    }
}

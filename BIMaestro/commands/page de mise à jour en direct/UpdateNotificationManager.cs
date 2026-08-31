using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using BIMaestro.Localization;

namespace Page
{
    internal static class UpdateNotificationManager
    {
        private const string ReasonMuteToday = "mute_today";
        private const string ReasonLater = "later";
        private static readonly TimeSpan LaterSuppressDuration = TimeSpan.FromDays(7);

        private static readonly object Sync = new object();

        private static bool _initialized;
        private static bool _checkCompleted;
        private static bool _hasUpdate;
        private static bool _shouldPrompt;
        private static bool _promptShown;
        private static bool _passiveSignalApplied;

        private static Version _currentVersion = new Version(0, 0, 0);
        private static Version _latestVersion;

        private static UpdateNotifierState _state;

        public static void Initialize(UIControlledApplication app)
        {
            if (app == null) return;

            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;

                _state = LoadState();
                _currentVersion = ParseVersion(BIMaestroApp.PluginVersion) ?? new Version(0, 0, 0);

                app.Idling += App_Idling;
            }

            _ = Task.Run(CheckLatestVersionNoThrowAsync);
        }

        private static void App_Idling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (sender is UIApplication uiapp)
                AppUI.SetUiApplication(uiapp);

            if (!_checkCompleted) return;

            if (_hasUpdate && !_passiveSignalApplied)
                ApplyPassiveSignal();

            if (!_hasUpdate || !_shouldPrompt || _promptShown) return;

            if (ShowNonBlockingPrompt())
                _promptShown = true;
        }

        private static async Task CheckLatestVersionNoThrowAsync()
        {
            try
            {
                var manifest = await UpdateCheckService.FetchManifestAsync();
                if (manifest?.Version == null)
                {
                    lock (Sync) { _checkCompleted = true; }
                    return;
                }

                lock (Sync)
                {
                    _latestVersion = manifest.Version;
                    _hasUpdate = _currentVersion.CompareTo(_latestVersion) < 0;

                    if (_hasUpdate)
                    {
                        var now = DateTime.UtcNow;
                        // Important: on n'honore la suppression que pour les raisons explicites.
                        bool isRecognizedReason = string.Equals(_state?.SuppressReason, ReasonMuteToday, StringComparison.Ordinal)
                                               || string.Equals(_state?.SuppressReason, ReasonLater, StringComparison.Ordinal);

                        bool snoozed = isRecognizedReason
                                      && _state?.SuppressUntilUtc.HasValue == true
                                      && _state.SuppressUntilUtc.Value > now;

                        _shouldPrompt = !snoozed;
                    }

                    _checkCompleted = true;
                }
            }
            catch
            {
                lock (Sync) { _checkCompleted = true; }
            }
        }

        private static Version ParseVersion(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            return Version.TryParse(input.Trim(), out Version version) ? version : null;
        }

        private static void ApplyPassiveSignal()
        {
            try
            {
                var info = AppUI.GetRibbonButtonById("NOTE_MAJ");
                var btn = info?.PushButton;
                if (btn == null) return;

                btn.ItemText = UiLanguage.T("Note\nMAJ !", "Update\nAvailable!");
                btn.ToolTip = UiLanguage.T("Une mise à jour BIMaestro est disponible.\nInstallée: v", "A BIMaestro Update Is Available.\nInstalled: v") + _currentVersion +
                    UiLanguage.T("\nDisponible: v", "\nAvailable: v") + _latestVersion;
                _passiveSignalApplied = true;
            }
            catch
            {
                // Ignore UI update failures
            }
        }

        private static bool ShowNonBlockingPrompt()
        {
            try
            {
                var prompt = new UpdatePromptWindow(
                    _latestVersion?.ToString() ?? "?",
                    _currentVersion?.ToString() ?? "?");

                // Revit n'est pas toujours une app WPF "classique" avec MainWindow valide.
                // On attache la fenêtre au handle natif principal pour garantir l'affichage au premier plan.
                IntPtr revitMainHwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (revitMainHwnd != IntPtr.Zero)
                    new WindowInteropHelper(prompt).Owner = revitMainHwnd;
                else
                {
                    Window owner = Application.Current?.MainWindow;
                    if (owner != null && owner.IsLoaded)
                        prompt.Owner = owner;
                }

                prompt.ShowDialog();

                _state.LastPromptUtc = DateTime.UtcNow;

                if (prompt.MuteToday)
                {
                    _state.SuppressReason = ReasonMuteToday;
                    _state.SuppressUntilUtc = DateTime.UtcNow.Date.AddDays(1);
                }
                else if (prompt.Result == UpdatePromptResult.Later)
                {
                    _state.SuppressReason = ReasonLater;
                    _state.SuppressUntilUtc = DateTime.UtcNow + LaterSuppressDuration;
                }
                else
                {
                    // Pas de snooze implicite : si pas de MAJ réelle, on veut re-notifier au prochain lancement.
                    _state.SuppressReason = null;
                    _state.SuppressUntilUtc = null;
                }

                SaveState(_state);

                return true;
            }
            catch
            {
                return false;
            }
        }
        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BIMaestro",
            "update_notifier_state.json");

        private static UpdateNotifierState LoadState()
        {
            try
            {
                string path = StatePath;
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (!File.Exists(path)) return new UpdateNotifierState();

                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<UpdateNotifierState>(json) ?? new UpdateNotifierState();
            }
            catch
            {
                return new UpdateNotifierState();
            }
        }

        private static void SaveState(UpdateNotifierState state)
        {
            try
            {
                string path = StatePath;
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch
            {
                // ignore file errors
            }
        }

        private class UpdateNotifierState
        {
            public DateTime? LastPromptUtc { get; set; }
            public DateTime? SuppressUntilUtc { get; set; }
            public string SuppressReason { get; set; }
        }
    }
}

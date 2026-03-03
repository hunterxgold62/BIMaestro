using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Page
{
    internal static class UpdateNotificationManager
    {
        private const string SiteUrl = "https://sites.google.com/view/bimaestro";
        private const string DownloadUrl = "https://www.bimaestro.fr/t%C3%A9l%C3%A9chargement";
        private const string ReasonMuteToday = "mute_today";
        private const string ReasonLater = "later";

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

            _promptShown = true;
            ShowNonBlockingPrompt();
        }

        private static async Task CheckLatestVersionNoThrowAsync()
        {
            try
            {
                var latest = await FetchLatestVersionAsync();
                if (latest == null)
                {
                    lock (Sync) { _checkCompleted = true; }
                    return;
                }

                lock (Sync)
                {
                    _latestVersion = latest;
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

        private static async Task<Version> FetchLatestVersionAsync()
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(8);
                string html = await http.GetStringAsync(SiteUrl);
                return ParseVersion(html);
            }
        }

        private static Version ParseVersion(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            var m4 = Regex.Match(input, @"\b(\d+)\.(\d+)\.(\d+)\.(\d+)\b");
            if (m4.Success)
            {
                return new Version(
                    int.Parse(m4.Groups[1].Value),
                    int.Parse(m4.Groups[2].Value),
                    int.Parse(m4.Groups[3].Value),
                    int.Parse(m4.Groups[4].Value));
            }

            var m3 = Regex.Match(input, @"\b(\d+)\.(\d+)\.(\d+)\b");
            if (m3.Success)
            {
                return new Version(
                    int.Parse(m3.Groups[1].Value),
                    int.Parse(m3.Groups[2].Value),
                    int.Parse(m3.Groups[3].Value));
            }

            return null;
        }

        private static void ApplyPassiveSignal()
        {
            try
            {
                var info = AppUI.GetRibbonButtonById("NOTE_MAJ");
                var btn = info?.PushButton;
                if (btn == null) return;

                btn.ItemText = "Note\nMAJ !";
                btn.ToolTip = $"Une mise à jour BIMaestro est disponible.\nInstallée: v{_currentVersion}\nDisponible: v{_latestVersion}";
                _passiveSignalApplied = true;
            }
            catch
            {
                // Ignore UI update failures
            }
        }

        private static void ShowNonBlockingPrompt()
        {
            try
            {
                var td = new TaskDialog("BIMaestro - Mise à jour disponible")
                {
                    MainInstruction = $"Nouvelle version BIMaestro : v{_latestVersion}",
                    MainContent = $"Version installée : v{_currentVersion}\n\nVoulez-vous ouvrir la page de mise à jour ?",
                    VerificationText = "Ne plus afficher aujourd'hui",
                    CommonButtons = TaskDialogCommonButtons.Close
                };

                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Mettre à jour");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Plus tard");

                var result = td.Show();
                bool muteToday = td.WasVerificationChecked();

                _state.LastPromptUtc = DateTime.UtcNow;

                if (muteToday)
                {
                    _state.SuppressReason = ReasonMuteToday;
                    _state.SuppressUntilUtc = DateTime.UtcNow.Date.AddDays(1);
                }
                else if (result == TaskDialogResult.CommandLink2)
                {
                    _state.SuppressReason = ReasonLater;
                    _state.SuppressUntilUtc = DateTime.UtcNow.AddDays(1);
                }
                else
                {
                    // Pas de snooze implicite : si pas de MAJ réelle, on veut re-notifier au prochain lancement.
                    _state.SuppressReason = null;
                    _state.SuppressUntilUtc = null;
                }

                SaveState(_state);

                if (result == TaskDialogResult.CommandLink1)
                    OpenDownloadPage();
            }
            catch
            {
                // no-op
            }
        }

        private static void OpenDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            }
            catch
            {
                // no-op
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

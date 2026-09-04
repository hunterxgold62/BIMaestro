using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMaestro.Localization;
using Licensing;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace Page
{
    /// <summary>
    /// Gère les invitations de soutien sans dépendre du réseau :
    /// une fois après une vraie mise à jour, puis une fois après 20 usages réussis
    /// dans cette version du plugin.
    /// </summary>
    internal static class SupportPromptManager
    {
        private const int UsageThreshold = 20;
        private const string SupportUrl = "https://ko-fi.com/bimaestro";

        private static readonly object Sync = new object();

        private static SupportPromptState _state;
        private static bool _initialized;
        private static bool _dialogOpen;
        private static bool _usagePromptPending;

        public static void Initialize(UIControlledApplication application)
        {
            if (application == null) return;

            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;

                string currentVersion = NormalizeVersion(global::BIMaestroApp.PluginVersion);
                _state = LoadState();

                // Au premier déploiement de ce mécanisme, welcome_state permet de
                // distinguer un utilisateur existant d'une installation neuve.
                if (string.IsNullOrWhiteSpace(_state.Version))
                {
                    _state.Version = currentVersion;
                    _state.UsageCount = 0;
                    _state.UsagePromptShown = false;
                    if (HasEvidenceOfPriorUse())
                        _state.PendingUpdatePromptVersion = currentVersion;
                    SaveState(_state);
                }
                else if (!string.Equals(_state.Version, currentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    string previousVersion = _state.Version;
                    _state.Version = currentVersion;
                    _state.PreviousVersion = previousVersion;
                    _state.UsageCount = 0;
                    _state.UsagePromptShown = false;
                    _state.PendingUpdatePromptVersion = currentVersion;
                    SaveState(_state);
                }

                _usagePromptPending = !_state.UsagePromptShown
                    && _state.UsageCount >= UsageThreshold;

                application.Idling += OnIdling;
            }
        }

        public static void RegisterSuccessfulUse(string buttonId)
        {
            // Cliquer volontairement sur "Soutenir" ne doit pas rapprocher une
            // nouvelle sollicitation.
            if (string.Equals(buttonId, "SupportCommand", StringComparison.OrdinalIgnoreCase))
                return;

            lock (Sync)
            {
                if (!_initialized || _state == null || _state.UsagePromptShown)
                    return;

                if (_state.UsageCount < UsageThreshold)
                    _state.UsageCount++;

                if (_state.UsageCount >= UsageThreshold)
                    _usagePromptPending = true;

                SaveState(_state);
            }
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication uiapp)) return;

            bool showUpdate;
            bool showUsage;

            lock (Sync)
            {
                if (_dialogOpen || _state == null) return;

                showUpdate = !string.IsNullOrWhiteSpace(_state.PendingUpdatePromptVersion);
                showUsage = !showUpdate && _usagePromptPending && !_state.UsagePromptShown;
                if (!showUpdate && !showUsage) return;

                _dialogOpen = true;
            }

            try
            {
                bool wantsToSupport = showUpdate
                    ? ShowPrompt(SupportPromptKind.AfterUpdate, uiapp)
                    : ShowPrompt(SupportPromptKind.AfterUsage, uiapp);

                lock (Sync)
                {
                    if (showUpdate)
                        _state.PendingUpdatePromptVersion = null;
                    else
                    {
                        _state.UsagePromptShown = true;
                        _usagePromptPending = false;
                    }

                    SaveState(_state);
                }

                if (wantsToSupport)
                    OpenSupportPage();
            }
            catch
            {
                // Une invitation de soutien ne doit jamais gêner Revit.
            }
            finally
            {
                lock (Sync) { _dialogOpen = false; }
            }
        }

        private static bool ShowPrompt(SupportPromptKind kind, UIApplication uiapp)
        {
            var window = new SupportPromptWindow(kind);
            IntPtr ownerHandle = uiapp?.MainWindowHandle ?? Process.GetCurrentProcess().MainWindowHandle;

            if (ownerHandle != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = ownerHandle;
            else
            {
                Window owner = Application.Current?.MainWindow;
                if (owner != null && owner.IsLoaded)
                    window.Owner = owner;
            }

            window.ShowDialog();
            return window.WantsToSupport;
        }

        private static void OpenSupportPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SupportUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    UiLanguage.T("BIMaestro - Soutenir", "BIMaestro - Support"),
                    UiLanguage.T(
                        $"Impossible d'ouvrir la page Ko-fi : {ex.Message}",
                        $"Unable to open the Ko-fi page: {ex.Message}"));
            }
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "0.0.0";
            string clean = version.Trim();
            int metadataIndex = clean.IndexOf('+');
            return metadataIndex >= 0 ? clean.Substring(0, metadataIndex) : clean;
        }

        private static string StatePath => Path.Combine(Paths.LicenseDir, "support_prompt_state.json");

        private static bool HasEvidenceOfPriorUse()
        {
            try
            {
                var welcomeState = BIMaestro.Welcome.WelcomeStorage.LoadOrCreate();
                return welcomeState.FirstCommandUtc.HasValue
                    || welcomeState.WelcomeShown
                    || welcomeState.HardDismissed;
            }
            catch
            {
                return false;
            }
        }

        private static SupportPromptState LoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return new SupportPromptState();
                string json = File.ReadAllText(StatePath);
                return JsonConvert.DeserializeObject<SupportPromptState>(json) ?? new SupportPromptState();
            }
            catch
            {
                return new SupportPromptState();
            }
        }

        private static void SaveState(SupportPromptState state)
        {
            try
            {
                string directory = Path.GetDirectoryName(StatePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(StatePath, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch
            {
                // Le suivi local ne doit jamais empêcher l'utilisation d'un bouton.
            }
        }

        private sealed class SupportPromptState
        {
            public string Version { get; set; }
            public string PreviousVersion { get; set; }
            public int UsageCount { get; set; }
            public bool UsagePromptShown { get; set; }
            public string PendingUpdatePromptVersion { get; set; }
        }
    }
}

using Autodesk.Revit.UI;
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Interop;

namespace BIMaestro.Welcome
{
    public static class WelcomeManager
    {
        private static readonly TimeSpan DelayAfterFirstUse = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan SnoozeDuration = TimeSpan.FromDays(7);
        private static readonly TimeSpan MinAttemptSpacing = TimeSpan.FromHours(6);

        private static readonly object _sync = new object();
        private static WelcomeState _state;
        private static bool _initialized;

        private static Timer _timer;
        private static volatile bool _shouldShow;
        private static int _showing;

        private static UIApplication _lastUiApp;

        // JWT licence courant (pour sync)
        private static string _jwtLicenseToken;

        // CommandId du bouton Guide (récupéré à la création du ruban)
        private static RevitCommandId _guideCommandId;

        public static void Initialize(UIControlledApplication app)
        {
            lock (_sync)
            {
                if (_initialized) return;
                _initialized = true;

                _state = WelcomeStorage.LoadOrCreate();
                app.Idling += App_Idling;
            }
        }

        /// <summary>
        /// À appeler quand tu crées le PushButton du guide : WelcomeManager.SetGuideCommandId(pb.CommandId)
        /// </summary>
        public static void SetGuideCommandId(RevitCommandId cmdId)
        {
            lock (_sync)
            {
                _guideCommandId = cmdId;
            }
        }

        /// <summary>
        /// Appelle ça au début de chaque commande BIMaestro.
        /// Passe le JWT licence si tu l’as (très recommandé).
        /// </summary>
        public static void NotifyFirstCommandUsed(UIApplication uiapp, string jwtLicenseToken = null)
        {
            if (uiapp == null) return;

            lock (_sync)
            {
                _lastUiApp = uiapp;
                if (!string.IsNullOrWhiteSpace(jwtLicenseToken))
                    _jwtLicenseToken = jwtLicenseToken;

                _state ??= WelcomeStorage.LoadOrCreate();

                if (!ShouldPromptForCurrentVersion(_state)) return;

                if (!_state.FirstCommandUtc.HasValue)
                {
                    _state.FirstCommandUtc = DateTime.UtcNow;
                    WelcomeStorage.Save(_state);
                    ArmTimer();
                }
                else
                {
                    var due = _state.FirstCommandUtc.Value + DelayAfterFirstUse;
                    if (DateTime.UtcNow >= due) _shouldShow = true;
                    else ArmTimer();
                }
            }
        }

        /// <summary>
        /// Si l’utilisateur avait déjà opt-in (email stocké) mais que tu n’avais pas de JWT à l’époque,
        /// appelle ça juste après validation licence pour pousser les infos.
        /// </summary>
        public static void TrySyncPendingProfile(string jwtLicenseToken)
        {
            if (string.IsNullOrWhiteSpace(jwtLicenseToken)) return;

            lock (_sync)
            {
                _jwtLicenseToken = jwtLicenseToken;
                _state ??= WelcomeStorage.LoadOrCreate();
            }

            TryUpsertProfileNoThrow();
        }
        /// <summary>
        /// Met à jour le profil depuis l'onglet Paramètres et tente la synchro Supabase si possible.
        /// </summary>
        public static void UpdateProfileFromSettings(string email, string firstName, string lastName)
        {
            lock (_sync)
            {
                _state ??= WelcomeStorage.LoadOrCreate();

                _state.Email = NormalizeValue(email);
                _state.FirstName = NormalizeValue(firstName);
                _state.LastName = NormalizeValue(lastName);

                var hasEmail = !string.IsNullOrWhiteSpace(_state.Email);
                _state.EmailOptIn = hasEmail;
                if (hasEmail)
                {
                    _state.OptInUtc ??= DateTime.UtcNow;
                }
                else
                {
                    _state.OptInUtc = null;
                }
                _state.ProfilePending = true;
                WelcomeStorage.Save(_state);
            }

            TryUpsertProfileNoThrow();
        }
        private static void ArmTimer()
        {
            _timer?.Dispose();

            var now = DateTime.UtcNow;
            var dueUtc = (_state.FirstCommandUtc ?? now) + DelayAfterFirstUse;
            var ms = Math.Max(0, (int)Math.Min(int.MaxValue, (dueUtc - now).TotalMilliseconds));

            _timer = new Timer(_ => _shouldShow = true, null, ms, Timeout.Infinite);
        }

        private static void App_Idling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (!_shouldShow) return;

            lock (_sync)
            {
                _state ??= WelcomeStorage.LoadOrCreate();

                if (!ShouldPromptForCurrentVersion(_state)) { _shouldShow = false; return; }
                if (_state.LastAttemptUtc.HasValue && DateTime.UtcNow - _state.LastAttemptUtc.Value < MinAttemptSpacing)
                {
                    _shouldShow = false;
                    return;
                }

                if (Interlocked.Exchange(ref _showing, 1) == 1)
                {
                    _shouldShow = false;
                    return;
                }

                _state.LastAttemptUtc = DateTime.UtcNow;
                WelcomeStorage.Save(_state);
            }

            try
            {
                var win = new WelcomeWindow();

                // ✅ Owner Revit sans dépendance Autodesk.Windows
                var hwnd = RevitWindowHandle.GetRevitMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    var helper = new WindowInteropHelper(win);
                    helper.Owner = hwnd;
                }

                win.ShowDialog();

                lock (_sync)
                {
                    _state.LastWelcomePromptVersion = CurrentPluginVersion;
                    WelcomeStorage.Save(_state);

                    if (win.ResultAction == WelcomeResultAction.OpenGuide)
                    {
                        // Option : on considère le welcome terminé pour éviter qu’il revienne.
                        // L’opt-in restera accessible via Paramètres.
                        _state.WelcomeShown = true;
                        WelcomeStorage.Save(_state);

                        _shouldShow = false;
                        PostGuideCommand();
                        return;
                    }

                    if (win.ResultAction == WelcomeResultAction.OptIn)
                    {
                        _state.EmailOptIn = true;
                        _state.Email = win.Email;
                        _state.FirstName = win.FirstName;
                        _state.LastName = win.LastName;
                        _state.OptInUtc = DateTime.UtcNow;
                        _state.ProfilePending = true;

                        _state.WelcomeShown = true;
                        WelcomeStorage.Save(_state);

                        TryUpsertProfileNoThrow();
                    }
                    else if (win.ResultAction == WelcomeResultAction.Snooze)
                    {
                        _state.SnoozeUntilUtc = DateTime.UtcNow + SnoozeDuration;
                        WelcomeStorage.Save(_state);
                    }
                    else if (win.ResultAction == WelcomeResultAction.Dismiss)
                    {
                        _state.HardDismissed = true;
                        WelcomeStorage.Save(_state);
                    }

                    _shouldShow = false;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _showing, 0);
            }
        }

        private static void PostGuideCommand()
        {
            UIApplication uiapp;
            RevitCommandId cmdId;

            lock (_sync)
            {
                uiapp = _lastUiApp;
                cmdId = _guideCommandId;
            }

            if (uiapp == null || cmdId == null) return;

            try
            {
                uiapp.PostCommand(cmdId);
            }
            catch
            {
                // ignore volontairement
            }
        }

        private static void TryUpsertProfileNoThrow()
        {
            WelcomeState s;
            string jwt;

            lock (_sync)
            {
                s = _state ?? WelcomeStorage.LoadOrCreate();
                jwt = _jwtLicenseToken;
            }

            if (s == null) return;
            if (!s.ProfilePending) return;
            if (string.IsNullOrWhiteSpace(jwt)) return;

            // Ici tu stockes un hash stable (comme tu le fais déjà ailleurs)
            var machineHash = Licensing.LicenseManager.ComputeMachineId();

            var success = Licensing.LicenseManager.TryUpsertUserProfileNoThrow(
                jwtLicenseToken: jwt,
                installId: s.InstallId,
                email: s.Email,
                firstName: s.FirstName,
                lastName: s.LastName,
                machineIdHash: machineHash
            );

            if (!success) return;

            lock (_sync)
            {
                s.ProfilePending = false;
                WelcomeStorage.Save(s);
            }
        }
        private static string NormalizeValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string CurrentPluginVersion =>
            NormalizeValue(BIMaestroApp.PluginVersion) ?? "dev";

        private static bool ShouldPromptForCurrentVersion(WelcomeState state)
        {
            if (state == null) return true;
            if (!string.IsNullOrWhiteSpace(state.Email)) return false;

            return !string.Equals(
                NormalizeValue(state.LastWelcomePromptVersion),
                CurrentPluginVersion,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Récupère le handle de la fenêtre principale Revit sans référencer Autodesk.Windows (AdWindows.dll).
    /// </summary>
    internal static class RevitWindowHandle
    {
        public static IntPtr GetRevitMainWindowHandle()
        {
            // 1) Tentative via Autodesk.Windows.ComponentManager (si présent) - par réflexion
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("Autodesk.Windows.ComponentManager");
                    if (t == null) continue;

                    var propAppWin = t.GetProperty("ApplicationWindow");
                    var appWin = propAppWin?.GetValue(null, null);
                    if (appWin == null) break;

                    var propHandle = appWin.GetType().GetProperty("Handle");
                    var handleVal = propHandle?.GetValue(appWin, null);

                    if (handleVal is IntPtr hwnd && hwnd != IntPtr.Zero)
                        return hwnd;

                    break;
                }
            }
            catch
            {
                // ignore
            }

            // 2) Fallback (souvent OK)
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero) return hwnd;
            }
            catch
            {
                // ignore
            }

            return IntPtr.Zero;
        }
    }
}

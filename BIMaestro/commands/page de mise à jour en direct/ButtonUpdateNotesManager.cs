using Autodesk.Revit.UI;
using Licensing;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

namespace Page
{
    internal static class ButtonUpdateNotesManager
    {
        private const string FirstAssumedPreviousVersion = "1.0.6.1";
        private static readonly object Sync = new object();
        private static ButtonUpdateNotesState _state;

        public static void TryShowButtonNotesBeforeLaunch(string buttonId, string commandClass, UIApplication uiapp)
        {
            try
            {
                var currentVersion = GetCurrentVersion();
                if (currentVersion == null)
                    return;

                var notes = ButtonUpdateNotesCatalog.Notes
                    .Where(note => IsSameButton(note, buttonId, commandClass))
                    .Where(note => IsSameVersion(note.Version, currentVersion))
                    .Where(note => !HasSeen(note, currentVersion))
                    .ToList();

                if (notes.Count == 0)
                    return;

                var state = GetState();
                ShowWindow(notes, GetPreviousVersionForDisplay(state, currentVersion), currentVersion, uiapp);

                foreach (var note in notes)
                    MarkSeen(note, currentVersion);

                state.LastKnownVersion = currentVersion.ToString();
                SaveState(state);
            }
            catch
            {
                // Never block a Revit command for release notes.
            }
        }

        private static bool IsSameButton(ButtonUpdateNote note, string buttonId, string commandClass)
        {
            if (note == null)
                return false;

            return (!string.IsNullOrWhiteSpace(note.ButtonId) &&
                    string.Equals(note.ButtonId, buttonId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(note.CommandClass) &&
                    string.Equals(note.CommandClass, commandClass, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasSeen(ButtonUpdateNote note, Version currentVersion)
        {
            var key = BuildSeenKey(note);
            if (string.IsNullOrWhiteSpace(key))
                return true;

            if (!GetState().SeenByButton.TryGetValue(key, out var seenVersionText))
                return false;

            var seenVersion = ParseVersion(seenVersionText);
            return seenVersion != null && seenVersion.CompareTo(currentVersion) >= 0;
        }

        private static void MarkSeen(ButtonUpdateNote note, Version currentVersion)
        {
            var key = BuildSeenKey(note);
            if (string.IsNullOrWhiteSpace(key))
                return;

            var state = GetState();
            state.SeenByButton[key] = currentVersion.ToString();
            state.LastSeenUtc = DateTime.UtcNow;
        }

        private static string BuildSeenKey(ButtonUpdateNote note)
        {
            if (!string.IsNullOrWhiteSpace(note?.ButtonId))
                return note.ButtonId.Trim();

            return note?.CommandClass?.Trim();
        }

        private static string GetPreviousVersionForDisplay(ButtonUpdateNotesState state, Version currentVersion)
        {
            if (currentVersion != null &&
                string.Equals(currentVersion.ToString(), "1.0.6.2", StringComparison.OrdinalIgnoreCase))
            {
                return FirstAssumedPreviousVersion;
            }

            return state?.LastKnownVersion;
        }

        private static void ShowWindow(List<ButtonUpdateNote> notes, string previousVersion, Version currentVersion, UIApplication uiapp)
        {
            var window = new ButtonUpdateNotesWindow(notes, previousVersion, currentVersion.ToString());

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
        }

        private static bool IsSameVersion(string versionText, Version currentVersion)
        {
            var noteVersion = ParseVersion(versionText);
            return noteVersion != null && currentVersion != null && noteVersion.CompareTo(currentVersion) == 0;
        }

        private static Version GetCurrentVersion()
        {
            var versionText = global::BIMaestroApp.PluginVersion;
            if (string.IsNullOrWhiteSpace(versionText))
            {
                var asm = Assembly.GetExecutingAssembly();
                versionText = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                    ?? asm.GetName().Version?.ToString();
            }

            return ParseVersion(versionText);
        }

        private static Version ParseVersion(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var clean = input.Trim();
            var plusIndex = clean.IndexOf('+');
            if (plusIndex >= 0)
                clean = clean.Substring(0, plusIndex);

            return Version.TryParse(clean, out var version) ? version : null;
        }

        private static ButtonUpdateNotesState GetState()
        {
            lock (Sync)
            {
                return _state ?? (_state = LoadState());
            }
        }

        private static string StatePath => Path.Combine(
            Paths.LicenseDir,
            "button_update_notes_seen.json");

        private static ButtonUpdateNotesState LoadState()
        {
            try
            {
                var path = StatePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(path))
                {
                    return new ButtonUpdateNotesState
                    {
                        LastKnownVersion = FirstAssumedPreviousVersion
                    };
                }

                var json = File.ReadAllText(path);
                var state = JsonConvert.DeserializeObject<ButtonUpdateNotesState>(json) ?? new ButtonUpdateNotesState();
                if (string.IsNullOrWhiteSpace(state.LastKnownVersion))
                    state.LastKnownVersion = FirstAssumedPreviousVersion;
                return state;
            }
            catch
            {
                return new ButtonUpdateNotesState
                {
                    LastKnownVersion = FirstAssumedPreviousVersion
                };
            }
        }

        private static void SaveState(ButtonUpdateNotesState state)
        {
            if (state == null)
                return;

            try
            {
                var path = StatePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        private sealed class ButtonUpdateNotesState
        {
            public Dictionary<string, string> SeenByButton { get; set; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public DateTime? LastSeenUtc { get; set; }
            public string LastKnownVersion { get; set; }
        }
    }
}

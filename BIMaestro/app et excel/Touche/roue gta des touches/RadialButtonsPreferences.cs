using Newtonsoft.Json;
using BIMaestro.RibbonLayout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BIMaestro.UI
{
    public sealed class RadialButtonsPreferences
    {
        public int Version { get; set; } = 1;
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> PinnedButtonIds { get; set; } =
            Enumerable.Repeat<string>(null, RadialButtonsPreferencesManager.SlotCount).ToList();
        public RadialHotkeyPreference Hotkey { get; set; }
    }

    public sealed class RadialHotkeyPreference
    {
        public int Modifiers { get; set; }
        public int VirtualKey { get; set; }
    }

    public static class RadialButtonsPreferencesManager
    {
        public const int SlotCount = 16;
        private const string RadialButtonId = "RadialMenuButtonsCommand";
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "RadialButtons.json");
        private static readonly Mutex PreferencesMutex =
            new Mutex(false, @"Local\BIMaestro.RadialButtonsPreferences");
        private static RadialButtonsPreferences _lastValidPreferences;

        public static RadialButtonsPreferences Load()
        {
            return WithLock(() => Clone(LoadCore()), () => Clone(_lastValidPreferences ?? Normalize(null)));
        }

        public static bool Save(RadialButtonsPreferences preferences)
        {
            return WithLock(() => SaveCore(preferences), () => false);
        }

        public static bool SetPinnedButton(int slotIndex, string buttonId)
        {
            if (slotIndex < 0 || slotIndex >= SlotCount) return false;
            return WithLock(() =>
            {
                var preferences = LoadCore();
                if (IsAllowedButtonId(buttonId))
                {
                    for (int i = 0; i < preferences.PinnedButtonIds.Count; i++)
                        if (i != slotIndex && string.Equals(preferences.PinnedButtonIds[i], buttonId, StringComparison.OrdinalIgnoreCase))
                            preferences.PinnedButtonIds[i] = null;
                }
                preferences.PinnedButtonIds[slotIndex] = IsAllowedButtonId(buttonId) ? buttonId : null;
                return SaveCore(preferences);
            }, () => false);
        }

        public static bool ResetPinnedButtons()
        {
            return WithLock(() =>
            {
                var preferences = LoadCore();
                preferences.PinnedButtonIds = Enumerable.Repeat<string>(null, SlotCount).ToList();
                return SaveCore(preferences);
            }, () => false);
        }

        private static RadialButtonsPreferences LoadCore()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    var empty = Normalize(null);
                    _lastValidPreferences = Clone(empty);
                    return empty;
                }

                string json;
                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(stream))
                    json = reader.ReadToEnd();

                var loaded = Normalize(JsonConvert.DeserializeObject<RadialButtonsPreferences>(json));
                _lastValidPreferences = Clone(loaded);
                return loaded;
            }
            catch
            {
                // Une lecture concurrente ne doit jamais effacer visuellement les
                // favoris : on conserve le dernier état valide de cette session.
                return Clone(_lastValidPreferences ?? Normalize(null));
            }
        }

        private static bool SaveCore(RadialButtonsPreferences preferences)
        {
            string temporaryPath = null;
            try
            {
                var normalized = Normalize(Clone(preferences));
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(normalized, Formatting.Indented));
                if (File.Exists(FilePath)) File.Replace(temporaryPath, FilePath, null);
                else File.Move(temporaryPath, FilePath);
                temporaryPath = null;
                _lastValidPreferences = Clone(normalized);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath))
                {
                    try { File.Delete(temporaryPath); } catch { }
                }
            }
        }

        private static T WithLock<T>(Func<T> action, Func<T> fallback)
        {
            bool acquired = false;
            try
            {
                try { acquired = PreferencesMutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { acquired = true; }
                return acquired ? action() : fallback();
            }
            catch
            {
                return fallback();
            }
            finally
            {
                if (acquired)
                {
                    try { PreferencesMutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static RadialButtonsPreferences Clone(RadialButtonsPreferences preferences)
        {
            var source = Normalize(preferences);
            return new RadialButtonsPreferences
            {
                Version = source.Version,
                PinnedButtonIds = source.PinnedButtonIds.ToList(),
                Hotkey = source.Hotkey == null ? null : new RadialHotkeyPreference
                {
                    Modifiers = source.Hotkey.Modifiers,
                    VirtualKey = source.Hotkey.VirtualKey
                }
            };
        }

        public static List<RibbonButtonInfo> MergeButtons(
            IReadOnlyList<RibbonButtonInfo> registry,
            IReadOnlyList<ButtonRecentManager.RecentEntry> recents,
            RadialButtonsPreferences preferences)
        {
            var buttons = (registry ?? Array.Empty<RibbonButtonInfo>())
                .Where(b => b != null && IsAllowedButtonId(b.Id))
                .ToList();
            var byId = buttons.Where(b => !string.IsNullOrWhiteSpace(b.Id))
                .GroupBy(b => b.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var byCommand = buttons.Where(b => !string.IsNullOrWhiteSpace(b.CommandClass))
                .GroupBy(b => b.CommandClass, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var result = Enumerable.Repeat<RibbonButtonInfo>(null, SlotCount).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = Normalize(preferences);

            for (int i = 0; i < SlotCount; i++)
            {
                string id = normalized.PinnedButtonIds[i];
                if (!string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id, out var pinned) && used.Add(pinned.Id))
                    result[i] = pinned;
            }

            foreach (var recent in recents ?? Array.Empty<ButtonRecentManager.RecentEntry>())
            {
                RibbonButtonInfo info = null;
                if (!string.IsNullOrWhiteSpace(recent?.ButtonId)) byId.TryGetValue(recent.ButtonId, out info);
                if (info == null && !string.IsNullOrWhiteSpace(recent?.CommandClass)) byCommand.TryGetValue(recent.CommandClass, out info);
                if (info == null || !used.Add(info.Id)) continue;
                int empty = result.FindIndex(item => item == null);
                if (empty < 0) break;
                result[empty] = info;
            }

            return result;
        }

        private static RadialButtonsPreferences Normalize(RadialButtonsPreferences preferences)
        {
            preferences ??= new RadialButtonsPreferences();
            preferences.Version = 1;
            var slots = preferences.PinnedButtonIds ?? new List<string>();
            preferences.PinnedButtonIds = Enumerable.Range(0, SlotCount)
                .Select(i => i < slots.Count && IsAllowedButtonId(slots[i]) ? slots[i] : null)
                .ToList();
            if (preferences.Hotkey != null &&
                (preferences.Hotkey.Modifiers == 0 || preferences.Hotkey.VirtualKey <= 0))
                preferences.Hotkey = null;
            return preferences;
        }

        private static bool IsAllowedButtonId(string id) =>
            !string.IsNullOrWhiteSpace(id) &&
            !string.Equals(id, RadialButtonId, StringComparison.OrdinalIgnoreCase);
    }
}

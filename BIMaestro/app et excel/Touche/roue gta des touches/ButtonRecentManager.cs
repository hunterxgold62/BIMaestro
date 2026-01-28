using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMaestro.UI
{
    public static class ButtonRecentManager
    {
        public class RecentEntry
        {
            public string ButtonId { get; set; }
            public string CommandClass { get; set; }
            public DateTime Utc { get; set; }
        }

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "RecentButtons.json");

        public static void RegisterUse(string buttonId, string commandClass)
        {
            if (string.IsNullOrWhiteSpace(buttonId) && string.IsNullOrWhiteSpace(commandClass)) return;
            try
            {
                var list = LoadRaw();
                list.RemoveAll(x =>
                    (string.IsNullOrWhiteSpace(commandClass) == false
                        && string.Equals(x.CommandClass, commandClass, StringComparison.OrdinalIgnoreCase))
                    || (string.IsNullOrWhiteSpace(buttonId) == false
                        && string.Equals(x.ButtonId, buttonId, StringComparison.OrdinalIgnoreCase)));
                list.Insert(0, new RecentEntry { ButtonId = buttonId, CommandClass = commandClass, Utc = DateTime.UtcNow });
                list = list.Take(500).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(list));
            }
            catch { }
        }

        public static List<RecentEntry> LoadMostRecentDistinct(int take, Func<RecentEntry, bool> existsFilter = null)
        {
            try
            {
                var list = LoadRaw();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var results = new List<RecentEntry>();

                foreach (var entry in list)
                {
                    if (entry == null) continue;
                    var key = GetEntryKey(entry);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (!seen.Add(key)) continue;
                    if (existsFilter != null && !existsFilter(entry)) continue;

                    results.Add(entry);
                    if (results.Count >= take) break;
                }

                return results;
            }
            catch { return new List<RecentEntry>(); }
        }

        private static List<RecentEntry> LoadRaw()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<RecentEntry>();
                var json = File.ReadAllText(FilePath);
                return JsonConvert.DeserializeObject<List<RecentEntry>>(json) ?? new List<RecentEntry>();
            }
            catch { return new List<RecentEntry>(); }
        }

        private static string GetEntryKey(RecentEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.CommandClass)) return entry.CommandClass;
            if (!string.IsNullOrWhiteSpace(entry.ButtonId)) return entry.ButtonId;
            return null;
        }
    }
}

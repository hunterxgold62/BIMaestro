using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Famille
{
    public static class FamilyRecentManager
    {
        private class RecentEntry
        {
            public string Path { get; set; }
            public DateTime Utc { get; set; }
        }

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "FamilyRecent.json");

        public static void RegisterUse(string familyPath)
        {
            try
            {
                var list = LoadRaw();
                list.RemoveAll(x => x.Path.Equals(familyPath, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, new RecentEntry { Path = familyPath, Utc = DateTime.UtcNow });
                // garde large, on tronquera à la lecture
                list = list.Take(500).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(list));
            }
            catch { }
        }

        public static List<string> LoadMostRecentDistinct(int take, Func<string, bool> existsFilter = null)
        {
            try
            {
                var list = LoadRaw();
                var q = list.Select(x => x.Path)
                            .Where(p => string.IsNullOrWhiteSpace(p) == false)
                            .Distinct(StringComparer.OrdinalIgnoreCase);
                if (existsFilter != null) q = q.Where(existsFilter);
                return q.Take(take).ToList();
            }
            catch { return new List<string>(); }
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
    }
}

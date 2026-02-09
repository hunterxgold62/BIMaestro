using Autodesk.Revit.DB;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Analyse
{
    public static class SmartCheckState
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, HashSet<string>> IgnoredByDoc =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs", "SauvegardePréférence",  "smartcheck_ignored.json");
        static SmartCheckState()
        {
            LoadFromDisk();
        }

        public static string GetDocKey(Document doc)
        {
            if (doc == null) return string.Empty;
            var path = doc.PathName;
            if (!string.IsNullOrWhiteSpace(path)) return path.ToLowerInvariant();
            return $"DOC::{doc.Title}";
        }

        public static void RestoreIgnored(string docKey, IEnumerable<ModelIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(docKey) || issues == null) return;

            HashSet<string> set;
            lock (Sync)
            {
                if (!IgnoredByDoc.TryGetValue(docKey, out set)) return;
                set = new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var issue in issues)
            {
                issue.Ignored = set.Contains(BuildKey(issue));
            }
        }

        public static void SetIgnored(string docKey, ModelIssue issue, bool ignored)
        {
            if (string.IsNullOrWhiteSpace(docKey) || issue == null) return;

            var key = BuildKey(issue);
            lock (Sync)
            {
                if (!IgnoredByDoc.TryGetValue(docKey, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    IgnoredByDoc[docKey] = set;
                }

                if (ignored) set.Add(key);
                else set.Remove(key);

                if (set.Count == 0)
                    IgnoredByDoc.Remove(docKey);

                SaveToDisk();
            }
        }

        private static string BuildKey(ModelIssue issue)
        {
            var id = issue?.ElementId?.GetIdValue() ?? -1;
            var related = issue?.RelatedId?.GetIdValue() ?? -1;
            return $"{issue?.Kind}|{id}|{related}";
        }

        private static void LoadFromDisk()
        {
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(StorePath)) return;

                    var json = File.ReadAllText(StorePath);
                    if (string.IsNullOrWhiteSpace(json)) return;

                    var data = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
                    if (data == null) return;

                    IgnoredByDoc.Clear();
                    foreach (var kvp in data)
                    {
                        if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null) continue;

                        var set = new HashSet<string>(
                            kvp.Value.Where(v => !string.IsNullOrWhiteSpace(v)),
                            StringComparer.OrdinalIgnoreCase);

                        if (set.Count > 0)
                            IgnoredByDoc[kvp.Key] = set;
                    }
                }
                catch
                {
                    // Ignore les erreurs de lecture pour ne pas bloquer la commande.
                }
            }
        }

        private static void SaveToDisk()
        {
            try
            {
                var folder = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                var snapshot = IgnoredByDoc.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase);

                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(StorePath, json);
            }
            catch
            {
                // Ignore les erreurs d'écriture pour ne pas interrompre l'UX.
            }
        }
    }
}
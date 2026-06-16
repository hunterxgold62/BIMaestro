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
        private static readonly Dictionary<string, Dictionary<string, IssueStatusRecord>> StatusByDoc =
            new Dictionary<string, Dictionary<string, IssueStatusRecord>>(StringComparer.OrdinalIgnoreCase);

        private static readonly string StoreFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence");

        private static readonly string StorePath = Path.Combine(StoreFolder, "smartcheck_ignored.json");
        private static readonly string StatusStorePath = Path.Combine(StoreFolder, "smartcheck_status.json");

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

            HashSet<string> ignoredSet = null;
            Dictionary<string, IssueStatusRecord> statusSet = null;
            lock (Sync)
            {
                if (IgnoredByDoc.TryGetValue(docKey, out var set))
                    ignoredSet = new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
                if (StatusByDoc.TryGetValue(docKey, out var records))
                    statusSet = new Dictionary<string, IssueStatusRecord>(records, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var issue in issues)
            {
                var key = BuildKey(issue);
                var legacyIgnored = ignoredSet != null && ignoredSet.Contains(key);

                if (statusSet != null && statusSet.TryGetValue(key, out var record) && record != null)
                {
                    issue.Status = NormalizeStatus(record.Status);
                    issue.StatusComment = record.Comment;
                    issue.StatusUser = record.User;
                    issue.StatusUpdatedUtc = record.UpdatedUtc;
                    issue.Ignored = ModelIssue.IsResolvedStatus(issue.Status);
                }
                else if (legacyIgnored)
                {
                    issue.Status = ModelIssue.StatusFixed;
                    issue.Ignored = true;
                }
                else
                {
                    issue.Status = ModelIssue.StatusActive;
                    issue.Ignored = false;
                }
            }
        }

        public static void SetIgnored(string docKey, ModelIssue issue, bool ignored)
        {
            SetIssueStatus(
                docKey,
                issue,
                ignored ? ModelIssue.StatusFixed : ModelIssue.StatusActive,
                issue?.StatusComment,
                Environment.UserName);
        }

        public static void SetIssueStatus(string docKey, ModelIssue issue, string status, string comment, string user)
        {
            if (string.IsNullOrWhiteSpace(docKey) || issue == null) return;

            var key = BuildKey(issue);
            var normalized = NormalizeStatus(status);
            var now = DateTime.UtcNow;

            issue.Status = normalized;
            issue.Ignored = ModelIssue.IsResolvedStatus(normalized);
            issue.StatusComment = comment;
            issue.StatusUser = string.IsNullOrWhiteSpace(user) ? Environment.UserName : user;
            issue.StatusUpdatedUtc = now;

            lock (Sync)
            {
                if (!StatusByDoc.TryGetValue(docKey, out var records))
                {
                    records = new Dictionary<string, IssueStatusRecord>(StringComparer.OrdinalIgnoreCase);
                    StatusByDoc[docKey] = records;
                }

                records[key] = new IssueStatusRecord
                {
                    Status = normalized,
                    Comment = comment,
                    User = issue.StatusUser,
                    UpdatedUtc = now
                };

                if (!IgnoredByDoc.TryGetValue(docKey, out var ignoredSet))
                {
                    ignoredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    IgnoredByDoc[docKey] = ignoredSet;
                }

                if (issue.Ignored) ignoredSet.Add(key);
                else ignoredSet.Remove(key);

                if (ignoredSet.Count == 0)
                    IgnoredByDoc.Remove(docKey);

                SaveToDisk();
            }
        }

        public static string GetThumbnailFolder(string docKey)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs", "Clash3D", "Miniatures");
            var safe = MakeSafeFileName(string.IsNullOrWhiteSpace(docKey) ? "document" : docKey);
            return Path.Combine(root, safe);
        }

        public static string GetReportFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs", "Clash3D", "Rapports");
        }

        private static string NormalizeStatus(string status)
        {
            if (string.Equals(status, ModelIssue.StatusToFix, StringComparison.OrdinalIgnoreCase)) return ModelIssue.StatusToFix;
            if (string.Equals(status, ModelIssue.StatusIgnored, StringComparison.OrdinalIgnoreCase)) return ModelIssue.StatusIgnored;
            if (string.Equals(status, ModelIssue.StatusFixed, StringComparison.OrdinalIgnoreCase)) return ModelIssue.StatusFixed;
            if (string.Equals(status, ModelIssue.StatusReview, StringComparison.OrdinalIgnoreCase)) return ModelIssue.StatusReview;
            return ModelIssue.StatusActive;
        }

        private static string BuildKey(ModelIssue issue)
            => issue?.IssueKey ?? string.Empty;

        private static void LoadFromDisk()
        {
            lock (Sync)
            {
                LoadIgnoredFromDisk();
                LoadStatusFromDisk();
            }
        }

        private static void LoadIgnoredFromDisk()
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

        private static void LoadStatusFromDisk()
        {
            try
            {
                if (!File.Exists(StatusStorePath)) return;

                var json = File.ReadAllText(StatusStorePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, IssueStatusRecord>>>(json);
                if (data == null) return;

                StatusByDoc.Clear();
                foreach (var kvp in data)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null) continue;
                    var records = kvp.Value
                        .Where(r => !string.IsNullOrWhiteSpace(r.Key) && r.Value != null)
                        .ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

                    if (records.Count > 0)
                        StatusByDoc[kvp.Key] = records;
                }
            }
            catch
            {
                // Ignore les erreurs de lecture pour ne pas bloquer la commande.
            }
        }

        private static void SaveToDisk()
        {
            try
            {
                if (!Directory.Exists(StoreFolder))
                    Directory.CreateDirectory(StoreFolder);

                var ignoredSnapshot = IgnoredByDoc.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(StorePath, JsonConvert.SerializeObject(ignoredSnapshot, Formatting.Indented));

                var statusSnapshot = StatusByDoc.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value,
                    StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(StatusStorePath, JsonConvert.SerializeObject(statusSnapshot, Formatting.Indented));
            }
            catch
            {
                // Ignore les erreurs d'écriture pour ne pas interrompre l'UX.
            }
        }

        private static string MakeSafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return safe.Length > 80 ? safe.Substring(0, 80) : safe;
        }

        private class IssueStatusRecord
        {
            public string Status { get; set; }
            public string Comment { get; set; }
            public string User { get; set; }
            public DateTime? UpdatedUtc { get; set; }
        }
    }
}

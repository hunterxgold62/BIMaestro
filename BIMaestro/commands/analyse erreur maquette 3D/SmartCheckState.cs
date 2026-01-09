using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace Analyse
{
    public static class SmartCheckState
    {
        private static readonly Dictionary<string, HashSet<string>> IgnoredByDoc = new(StringComparer.OrdinalIgnoreCase);

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
            if (!IgnoredByDoc.TryGetValue(docKey, out var set)) return;

            foreach (var issue in issues)
            {
                if (set.Contains(BuildKey(issue))) issue.Ignored = true;
            }
        }

        public static void SetIgnored(string docKey, ModelIssue issue, bool ignored)
        {
            if (string.IsNullOrWhiteSpace(docKey) || issue == null) return;
            var key = BuildKey(issue);
            if (!IgnoredByDoc.TryGetValue(docKey, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                IgnoredByDoc[docKey] = set;
            }

            if (ignored) set.Add(key);
            else set.Remove(key);
        }

        private static string BuildKey(ModelIssue issue)
        {
            var id = issue?.ElementId?.GetIdValue() ?? -1;
            var related = issue?.RelatedId?.GetIdValue() ?? -1;
            var msg = issue?.Message ?? string.Empty;
            return $"{issue?.Kind}|{id}|{related}|{msg}";
        }
    }
}
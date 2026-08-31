using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.ViewHover
{
    internal sealed class ViewDeckTabIdentity
    {
        internal string DocumentTitle;
        internal string[] Titles;

        internal static T Resolve<T>(string title, string toolTip, IEnumerable<T> views,
            Func<T, ViewDeckTabIdentity> identity) where T : class
        {
            var matches = views.Where(view => identity(view).Titles.Any(candidate =>
                string.Equals(candidate, title, StringComparison.Ordinal))).ToList();
            if (matches.Count == 0) return null;
            string documentLabel = (toolTip ?? string.Empty)
                .Split(new[] { " - " }, 2, StringSplitOptions.None)[0].Trim();
            var qualified = matches.Where(view => string.Equals(
                NormalizeDocumentLabel(identity(view).DocumentTitle), NormalizeDocumentLabel(documentLabel),
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (qualified.Count > 0) return qualified.Count == 1 ? qualified[0] : null;
            // Never take a same-name view when the tooltip names another document.
            if (!string.IsNullOrEmpty(toolTip) && toolTip.Contains(" - ")) return null;
            return matches.Count == 1 ? matches[0] : null;
        }

        private static string NormalizeDocumentLabel(string value)
        {
            string label = (value ?? string.Empty).Trim();
            foreach (string extension in new[] { ".rvt", ".rfa", ".rte" })
                if (label.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return label.Substring(0, label.Length - extension.Length);
            return label;
        }
    }
}

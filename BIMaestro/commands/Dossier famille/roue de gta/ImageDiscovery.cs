using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMaestro.UI
{
    internal static class ImageDiscovery
    {
        public static IEnumerable<string> FindInDocuments(int maxCount = 200)
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(docs) || !Directory.Exists(docs))
                yield break;

            var patterns = new[] { "*.png", "*.jpg", "*.jpeg" };
            var results = new List<string>();

            foreach (var pattern in patterns)
            {
                IEnumerable<string> files = Enumerable.Empty<string>();
                try { files = Directory.EnumerateFiles(docs, pattern, SearchOption.AllDirectories); }
                catch { /* dossiers protégés : ignorer */ }

                foreach (var f in files)
                {
                    results.Add(f);
                    if (results.Count >= maxCount * 3) break;
                }
                if (results.Count >= maxCount * 3) break;
            }

            var rng = new Random();
            foreach (var r in results.OrderBy(_ => rng.Next()).Take(maxCount))
                yield return r;
        }
    }
}

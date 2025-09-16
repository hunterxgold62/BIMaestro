using System;
using System.Collections.Generic;
using System.IO;

namespace Famille
{
    /// <summary>
    /// Résout le chemin d’image "catalogue" (.png/.jpg) le plus probable.
    /// </summary>
    public static class ImageResolver
    {
        private static readonly Dictionary<string, string> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static string Resolve(string familiesRoot, string imagesRoot, string familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath)) return null;

            try
            {
                familyPath = Path.GetFullPath(familyPath);
                familiesRoot = string.IsNullOrWhiteSpace(familiesRoot) ? "" : Path.GetFullPath(familiesRoot);
                imagesRoot = string.IsNullOrWhiteSpace(imagesRoot) ? "" : Path.GetFullPath(imagesRoot);
            }
            catch { }

            if (_cache.TryGetValue(familyPath, out var hit)) return hit;

            try
            {
                string rel = familiesRoot.Length > 0
                    ? FamilyBrowserWindow.GetRelativePath(familiesRoot, familyPath)
                    : Path.GetFileName(familyPath);

                string InImg(string ext) =>
                    imagesRoot.Length == 0 ? null : Path.ChangeExtension(Path.Combine(imagesRoot, rel), ext);

                string[] candidates =
                {
                    InImg(".png"),
                    Path.ChangeExtension(familyPath, ".png"),
                    InImg(".jpg"),
                    Path.ChangeExtension(familyPath, ".jpg"),
                };

                foreach (var c in candidates)
                    if (!string.IsNullOrEmpty(c) && File.Exists(c))
                        return _cache[familyPath] = Path.GetFullPath(c);

                return _cache[familyPath] = null;
            }
            catch { return null; }
        }

        public static void ClearCaches() => _cache.Clear();
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace Famille
{
    /// <summary>
    /// Résout le chemin d’image (png/jpg) à partir du .rfa :
    /// 1) imagesRoot + chemin relatif .rfa -> .png
    /// 2) même dossier que le .rfa -> .png
    /// 3) même logique en .jpg
    /// </summary>
    public static class ImageResolver
    {
        private static readonly Dictionary<string, string> _cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Resolve(string familiesRoot, string imagesRoot, string familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath)) return null;

            try
            {
                familyPath = Path.GetFullPath(familyPath);
                familiesRoot = string.IsNullOrWhiteSpace(familiesRoot) ? "" : Path.GetFullPath(familiesRoot);
                imagesRoot = string.IsNullOrWhiteSpace(imagesRoot) ? "" : Path.GetFullPath(imagesRoot);
            }
            catch { /* laisse tomber la normalisation si souci */ }

            if (_cache.TryGetValue(familyPath, out var hit))
                return hit;

            try
            {
                string rel = familiesRoot.Length > 0 ? FamilyBrowserWindow.GetRelativePath(familiesRoot, familyPath)
                                                     : Path.GetFileName(familyPath);

                string test(string root, string relative, string ext)
                    => string.IsNullOrEmpty(root) ? null : Path.ChangeExtension(Path.Combine(root, relative), ext);

                string[] candidates =
                {
            test(imagesRoot, rel, ".png"),
            Path.ChangeExtension(familyPath, ".png"),
            test(imagesRoot, rel, ".jpg"),
            Path.ChangeExtension(familyPath, ".jpg"),
        };

                foreach (var c in candidates)
                {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c))
                        return _cache[familyPath] = Path.GetFullPath(c);
                }

                return _cache[familyPath] = null;
            }
            catch
            {
                return null;
            }
        }


        public static void ClearCaches() => _cache.Clear();
    }
}

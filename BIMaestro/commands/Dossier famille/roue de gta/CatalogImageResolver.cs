using System;
using System.IO;

namespace Famille
{
    public static class CatalogImageResolver
    {
        private static string _familiesRoot;
        private static string _imagesRoot;

        public static void Initialize(string familiesRoot, string imagesRoot)
        {
            _familiesRoot = familiesRoot ?? string.Empty;
            _imagesRoot = string.IsNullOrWhiteSpace(imagesRoot) ? _familiesRoot : imagesRoot;
        }

        public static string Resolve(string familiesRoot, string imagesRoot, string familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath) || !File.Exists(familyPath))
                return null;

            // 1) racines proposées -> sinon racines en mémoire
            string famRoot = !string.IsNullOrWhiteSpace(familiesRoot) ? familiesRoot : _familiesRoot;
            string imgRoot = !string.IsNullOrWhiteSpace(imagesRoot) ? imagesRoot : _imagesRoot;

            // 2) si encore vides, on TENTE A CHAQUE FOIS de charger depuis le disque
            if (string.IsNullOrWhiteSpace(famRoot) || string.IsNullOrWhiteSpace(imgRoot))
            {
                EnsureRoots(); // lit CheminsFamille.json si dispo
                famRoot = _familiesRoot;
                imgRoot = _imagesRoot;
            }

            imgRoot = string.IsNullOrWhiteSpace(imgRoot) ? famRoot : imgRoot;

            try
            {
                string rel = GetRelativePathSafe(famRoot, familyPath);
                string nameNoExt = Path.GetFileNameWithoutExtension(familyPath);

                string InMirror(string ext) => Path.ChangeExtension(Path.Combine(imgRoot, rel), ext);
                string NextTo(string ext) => Path.ChangeExtension(familyPath, ext);
                string InFlat(string ext) => Path.Combine(imgRoot, nameNoExt + ext);

                string[] candidates =
                {
                    InMirror(".png"), NextTo(".png"), InFlat(".png"),
                    InMirror(".jpg"), NextTo(".jpg"), InFlat(".jpg"),
                };

                foreach (var c in candidates)
                    if (!string.IsNullOrEmpty(c) && File.Exists(c))
                        return c;

                return null;
            }
            catch { return null; }
        }

        private static void EnsureRoots()
        {
            try
            {
                var file = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs", "SauvegardePréférence", "CheminsFamille.json");

                if (!File.Exists(file)) return;

                var json = File.ReadAllText(file);
                var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<FolderSettings>(json);
                if (cfg == null) return;

                _familiesRoot = cfg.FamiliesFolder ?? _familiesRoot ?? string.Empty;
                _imagesRoot = string.IsNullOrWhiteSpace(cfg.ImagesFolder) ? _familiesRoot : cfg.ImagesFolder;
            }
            catch { /* silencieux */ }
        }

        private static string GetRelativePathSafe(string root, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(root)) return Path.GetFileName(path);
                var fromUri = new Uri(root.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? root : root + Path.DirectorySeparatorChar);
                var toUri = new Uri(path);
                var relUri = fromUri.MakeRelativeUri(toUri);
                return Uri.UnescapeDataString(relUri.ToString())
                          .Replace('/', Path.DirectorySeparatorChar);
            }
            catch { return Path.GetFileName(path); }
        }

        private class FolderSettings
        {
            public string FamiliesFolder { get; set; }
            public string ImagesFolder { get; set; }
        }
    }
}

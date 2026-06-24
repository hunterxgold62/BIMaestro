using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Famille
{
    public sealed class FamilyIndexService : IDisposable
    {
        public sealed class Entry
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public string Category { get; set; }
            public string NormalizedName { get; set; }
            public string NormalizedFolder { get; set; }
            public string RevitSavedVersion { get; set; }
            public long? FileSizeBytes { get; set; }
            public DateTime? CreatedUtc { get; set; }
            public DateTime? LastModifiedUtc { get; set; }
            public bool HasDocumentation { get; set; }
            public bool HasCatalogImage { get; set; }
        }

        private readonly string _familiesRoot;
        private readonly string _imagesRoot;

        private readonly object _lock = new();
        private List<Entry> _items = new();
        private CancellationTokenSource _cts = new();

        public bool IsReady { get; private set; }
        public string StatusText { get; private set; } = "Index non démarré.";
        public event Action IndexUpdated;

        public FamilyIndexService(string familiesRoot, string imagesRoot)
        {
            _familiesRoot = familiesRoot ?? "";
            _imagesRoot = imagesRoot ?? "";
        }

        public Task StartAsync()
        {
            return Task.Run(() => BuildIndex(_cts.Token));
        }

        private void BuildIndex(CancellationToken ct)
        {
            try
            {
                IsReady = false;
                UpdateStatus("Recherche des familles…");

                var list = new List<Entry>(capacity: 8192);
                int n = 0;


                foreach (var f in Directory.EnumerateFiles(_familiesRoot, "*.rfa", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) return;

                    list.Add(CreateFastEntry(f));

                    n++;
                    if (n == 1 || n % 25 == 0)
                    {
                        lock (_lock) _items = list.ToList();
                        UpdateStatus($"Familles trouvées : {n}…");
                    }
                }

                lock (_lock) _items = list;
                IsReady = true;
                UpdateStatus($"Recherche prête ({_items.Count} familles).");

                EnrichIndexMetadata(list, ct);
            }
            catch (Exception ex)
            {
                UpdateStatus("Index erreur : " + ex.Message);
            }
        }

        private Entry CreateFastEntry(string familyPath)
        {
            string name = Path.GetFileNameWithoutExtension(familyPath);
            string low = name.ToLowerInvariant();
            string cat = "Général";
            if (low.Contains("porte")) cat = "Porte";
            else if (low.Contains("fenetre") || low.Contains("fenêtre")) cat = "Fenêtre";

            return new Entry
            {
                Name = name,
                Path = familyPath,
                Category = cat,
                NormalizedName = StripDiacritics(name).ToLowerInvariant(),
                NormalizedFolder = StripDiacritics(GetRelativeFolder(familyPath)).ToLowerInvariant()
            };
        }

        private void EnrichIndexMetadata(List<Entry> list, CancellationToken ct)
        {
            foreach (var entry in list)
            {
                if (ct.IsCancellationRequested) return;
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
                    continue;

                try
                {
                    var meta = FamilyMetadataProvider.RequestFastMetadataAsync(entry.Path).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(meta?.Category))
                        entry.Category = meta.Category.Trim();

                    if (!string.IsNullOrWhiteSpace(meta?.RevitSavedVersion))
                        entry.RevitSavedVersion = meta.RevitSavedVersion.Trim();

                    if (meta?.FileSizeBytes != null)
                        entry.FileSizeBytes = meta.FileSizeBytes;

                    var info = SafeGetFileInfo(entry.Path);
                    entry.FileSizeBytes = entry.FileSizeBytes ?? info?.Length;
                    entry.CreatedUtc = info?.CreationTimeUtc;
                    entry.LastModifiedUtc = info?.LastWriteTimeUtc;

                    entry.HasDocumentation = HasDocumentationFile(entry.Path);
                    entry.HasCatalogImage = HasCatalogImage(entry.Path);
                }
                catch
                {
                }
            }

            lock (_lock) _items = list.ToList();
            UpdateStatus($"Index complet ({list.Count} familles).");
        }

        public IEnumerable<Entry> Search(string term, int max = 8000)
        {
            if (string.IsNullOrWhiteSpace(term)) return Array.Empty<Entry>();
            term = StripDiacritics(term).ToLowerInvariant();

            List<Entry> snapshot;
            lock (_lock) snapshot = _items;

            var q = snapshot.Where(e => e.NormalizedName.Contains(term));
            if (max > 0) q = q.Take(max);
            return q.ToList();
        }

        public IEnumerable<Entry> GetAll(int max = 0)
        {
            List<Entry> snapshot;
            lock (_lock) snapshot = _items;

            var q = snapshot.AsEnumerable();
            if (max > 0) q = q.Take(max);
            return q.ToList();
        }

        private void UpdateStatus(string s)
        {
            StatusText = s;
            IndexUpdated?.Invoke();
        }

        private FileInfo SafeGetFileInfo(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info : null;
            }
            catch
            {
                return null;
            }
        }

        private string GetRelativeFolder(string familyPath)
        {
            try
            {
                var root = _familiesRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? _familiesRoot
                    : _familiesRoot + Path.DirectorySeparatorChar;
                var rootUri = new Uri(root);
                var pathUri = new Uri(familyPath);
                var relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
                return Path.GetDirectoryName(relative) ?? string.Empty;
            }
            catch
            {
                return Path.GetDirectoryName(familyPath) ?? string.Empty;
            }
        }

        private static bool HasDocumentationFile(string familyPath)
            => !string.IsNullOrWhiteSpace(familyPath) && File.Exists(familyPath + ".docs.json");

        private bool HasCatalogImage(string familyPath)
        {
            try
            {
                string rel = GetRelativePath(_familiesRoot, familyPath);
                string fileNameNoExt = Path.GetFileNameWithoutExtension(familyPath);

                string InImgMirror(string ext) => Path.ChangeExtension(Path.Combine(_imagesRoot, rel), ext);
                string InImgFlat(string ext) => Path.Combine(_imagesRoot, fileNameNoExt + ext);
                string NextToFamily(string ext) => Path.ChangeExtension(familyPath, ext);

                string[] candidates =
                {
                    InImgMirror(".png"), NextToFamily(".png"), InImgFlat(".png"),
                    InImgMirror(".jpg"), NextToFamily(".jpg"), InImgFlat(".jpg"),
                };

                return candidates.Any(c => !string.IsNullOrEmpty(c) && File.Exists(c));
            }
            catch
            {
                return false;
            }
        }

        private static string GetRelativePath(string relativeTo, string path)
        {
            try
            {
                var fromUri = new Uri(relativeTo.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? relativeTo : relativeTo + Path.DirectorySeparatorChar);
                var toUri = new Uri(path);
                return Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return Path.GetFileName(path);
            }
        }

        private static string StripDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark) sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
        }
    }
}

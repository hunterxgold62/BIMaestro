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
                UpdateStatus("Indexation des familles…");

                var list = new List<Entry>(capacity: 8192);
                int n = 0;


                foreach (var f in Directory.EnumerateFiles(_familiesRoot, "*.rfa", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) return;

                    string name = Path.GetFileNameWithoutExtension(f);
                    string low = name.ToLowerInvariant();
                    string cat = "Général";
                    if (low.Contains("porte")) cat = "Porte";
                    else if (low.Contains("fenetre") || low.Contains("fenêtre")) cat = "Fenêtre";

                    list.Add(new Entry
                    {
                        Name = name,
                        Path = f,
                        Category = cat,
                        NormalizedName = StripDiacritics(name).ToLowerInvariant()
                    });

                    n++;
                    if (n % 500 == 0)
                    {
                        lock (_lock) _items = list.ToList();
                        UpdateStatus($"Index : {n} familles…");
                    }
                }

                lock (_lock) _items = list;
                IsReady = true;
                UpdateStatus($"Index prêt ({_items.Count} familles).");
            }
            catch (Exception ex)
            {
                UpdateStatus("Index erreur : " + ex.Message);
            }
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

        private void UpdateStatus(string s)
        {
            StatusText = s;
            IndexUpdated?.Invoke();
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

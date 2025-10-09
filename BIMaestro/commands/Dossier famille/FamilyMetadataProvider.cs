using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Famille
{
    /// <summary>
    /// Métadonnées rapides extraites du bloc PartAtom/XML d'un fichier .RFA,
    /// sans ouvrir Revit.
    /// </summary>
    public sealed class FamilyPartAtomMeta
    {
        public string OmniClassCode { get; set; }          // ex: "23.40.20.00"
        public string Category { get; set; }               // ex: "Mobilier"
        public string RevitSavedVersion { get; set; }      // ex: "2023"
        public DateTime? UpdatedUtc { get; set; }          // ex: 2025-02-18T09:47:20Z
        public string Path { get; set; }                   // chemin du fichier (pour debug/cache)
    }

    internal static class FamilyMetadataProvider
    {
        private sealed class MetadataRequest
        {
            public string FamilyPath;
            public TaskCompletionSource<string> Tcs; // pour compat: code OmniClass seul
        }

        private sealed class Handler : IExternalEventHandler
        {
            private readonly Queue<MetadataRequest> _queue;
            public Handler(Queue<MetadataRequest> queue) => _queue = queue;
            public string GetName() => nameof(FamilyMetadataProvider);

            public void Execute(UIApplication app)
            {
                MetadataRequest request;
                while ((request = Dequeue()) != null)
                {
                    string result = null;
                    try
                    {
                        // API historique : ne renvoie que le code OmniClass
                        result = ExtractOmniClassNumber(request.FamilyPath);
                    }
                    catch
                    {
                        result = null;
                    }
                    request.Tcs.TrySetResult(result);
                }
            }

            private MetadataRequest Dequeue()
            {
                lock (_queue)
                {
                    return _queue.Count > 0 ? _queue.Dequeue() : null;
                }
            }
        }

        private static readonly Queue<MetadataRequest> _queue = new();
        private static ExternalEvent _eventRef;
        private static Handler _handler;

        // .NET Framework : pas d'Encoding.Latin1 → ISO-8859-1
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        public static bool IsAvailable => _eventRef != null;

        public static void Initialize(UIApplication app)
        {
            if (app == null || _handler != null) return;

            _handler = new Handler(_queue);
            try
            {
                _eventRef = ExternalEvent.Create(_handler);
            }
            catch
            {
                _handler = null;
                _eventRef = null;
            }
        }

        // ================== API existante (OmniClass uniquement) ==================

        public static Task<string> RequestOmniClassNumberAsync(string familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath) || _eventRef == null)
                return Task.FromResult<string>(null);

            var request = new MetadataRequest
            {
                FamilyPath = familyPath,
                Tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            lock (_queue) { _queue.Enqueue(request); }

            try { _eventRef.Raise(); }
            catch { request.Tcs.TrySetResult(null); }

            return request.Tcs.Task;
        }

        private static string ExtractOmniClassNumber(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            if (TryReadOmniClassFromFile(path, out var code) && !string.IsNullOrWhiteSpace(code))
                return code;

            return null;
        }

        private static bool TryReadOmniClassFromFile(string path, out string code)
        {
            code = null;
            try
            {
                const int chunk = 256 * 1024;  // 256 Ko
                const int overlap = 4 * 1024;  // 4 Ko
                byte[] buffer = new byte[chunk + overlap];

                using (var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    1 << 16, FileOptions.SequentialScan))
                {
                    int carry = 0;
                    while (true)
                    {
                        int read = fs.Read(buffer, carry, chunk);
                        if (read <= 0) break;
                        int total = read + carry;

                        string text = Latin1.GetString(buffer, 0, total);

                        int idx = text.IndexOf("omniclass", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            int start = Math.Max(0, idx - 2048);
                            int len = Math.Min(4096, text.Length - start);
                            string window = text.Substring(start, len);

                            var m = Regex.Match(window, @"\b(\d{2}\.\d{2}\.\d{2}\.\d{2}(?:\.\d{2})?)\b");
                            if (m.Success)
                            {
                                code = m.Groups[1].Value;
                                return true;
                            }
                        }

                        if (total > overlap)
                        {
                            Buffer.BlockCopy(buffer, total - overlap, buffer, 0, overlap);
                            carry = overlap;
                        }
                        else carry = total;
                    }
                }
            }
            catch { /* ignore */ }
            return false;
        }

        // ================== NOUVELLE API : toutes les métadonnées utiles ==================

        /// <summary>
        /// Retourne (sans ouvrir Revit) l'OmniClass, la catégorie, la version de sauvegarde Revit,
        /// et la date "updated" si présentes. Ultra-rapide.
        /// </summary>
        public static Task<FamilyPartAtomMeta> RequestFastMetadataAsync(string familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath) || !File.Exists(familyPath))
                return Task.FromResult<FamilyPartAtomMeta>(null);

            // Pas besoin d'ExternalEvent : on ne touche pas à l'API Revit.
            return Task.Run(() => ExtractFastMetadata(familyPath));
        }

        private static FamilyPartAtomMeta ExtractFastMetadata(string path)
        {
            if (!TryReadPartAtomMetadata(path,
                    out var omni, out var category, out var productVersion, out var updatedUtc))
                return null;

            return new FamilyPartAtomMeta
            {
                Path = path,
                OmniClassCode = omni,
                Category = category,
                RevitSavedVersion = productVersion,
                UpdatedUtc = updatedUtc
            };
        }

        /// <summary>
        /// Lecture par blocs du .RFA et extraction via regex tolérantes :
        /// - OmniClass autour de "omniclass"
        /// - Category = 1er &lt;category&gt;&lt;term&gt;…&lt;/term&gt; non numérique
        /// - ProductVersion = (A:)?product-version
        /// - Updated = &lt;updated&gt;…&lt;/updated&gt;
        /// </summary>
        private static bool TryReadPartAtomMetadata(
            string path,
            out string omniClassCode,
            out string category,
            out string productVersion,
            out DateTime? updatedUtc)
        {
            omniClassCode = null; category = null; productVersion = null; updatedUtc = null;

            try
            {
                const int chunk = 256 * 1024;  // 256 Ko
                const int overlap = 4 * 1024;  // 4 Ko
                byte[] buffer = new byte[chunk + overlap];

                // Regex précompilées
                var rxOmni = new Regex(@"\b(\d{2}\.\d{2}\.\d{2}\.\d{2}(?:\.\d{2})?)\b",
                                       RegexOptions.Compiled);
                var rxCategoryTerm = new Regex(@"<\s*category\s*>\s*<\s*term\s*>\s*([^<]{1,100}?)\s*</\s*term\s*>",
                                               RegexOptions.IgnoreCase | RegexOptions.Compiled);
                var rxProductVersion = new Regex(@"<\s*(?:[A-Za-z0-9]+:)?product-version\s*>\s*(\d{4})\s*</\s*(?:[A-Za-z0-9]+:)?product-version\s*>",
                                                 RegexOptions.IgnoreCase | RegexOptions.Compiled);
                var rxUpdated = new Regex(@"<\s*updated\s*>\s*([0-9T:\-\.Z\+]+)\s*</\s*updated\s*>",
                                          RegexOptions.IgnoreCase | RegexOptions.Compiled);

                using (var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    1 << 16, FileOptions.SequentialScan))
                {
                    int carry = 0;
                    while (true)
                    {
                        int read = fs.Read(buffer, carry, chunk);
                        if (read <= 0) break;
                        int total = read + carry;

                        string text = Latin1.GetString(buffer, 0, total);

                        // 1) OmniClass : on repère "omniclass" et on scanne une fenêtre locale
                        if (omniClassCode == null)
                        {
                            int idx = text.IndexOf("omniclass", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                int start = Math.Max(0, idx - 2048);
                                int len = Math.Min(4096, text.Length - start);
                                string window = text.Substring(start, len);
                                var m = rxOmni.Match(window);
                                if (m.Success) omniClassCode = m.Groups[1].Value;
                            }
                        }

                        // 2) Product version : global
                        if (productVersion == null)
                        {
                            var m = rxProductVersion.Match(text);
                            if (m.Success) productVersion = m.Groups[1].Value;
                        }

                        // 3) Updated : global
                        if (updatedUtc == null)
                        {
                            var m = rxUpdated.Match(text);
                            if (m.Success && DateTime.TryParse(
                                    m.Groups[1].Value,
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                    out var dt))
                            {
                                updatedUtc = dt;
                            }
                        }

                        // 4) Category : on retient le 1er term non numérique/dot-pattern
                        if (category == null)
                        {
                            foreach (Match m in rxCategoryTerm.Matches(text))
                            {
                                var term = m.Groups[1].Value.Trim();
                                // Écarte les codes type 23.40.20.00(.XX)
                                if (!Regex.IsMatch(term, @"^\d{2}(\.\d{2}){3,4}$"))
                                {
                                    category = term;
                                    break;
                                }
                            }
                        }

                        // Si tout trouvé, on peut quitter tôt
                        if (omniClassCode != null && category != null && productVersion != null && updatedUtc.HasValue)
                            return true;

                        // chevauchement
                        if (total > overlap)
                        {
                            Buffer.BlockCopy(buffer, total - overlap, buffer, 0, overlap);
                            carry = overlap;
                        }
                        else carry = total;
                    }
                }

                // Succès si au moins une info utile trouvée
                return omniClassCode != null || category != null || productVersion != null || updatedUtc.HasValue;
            }
            catch
            {
                return false;
            }
        }
    }
}

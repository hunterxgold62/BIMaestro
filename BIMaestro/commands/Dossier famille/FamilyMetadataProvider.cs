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
    /// Métadonnées utiles pour le navigateur de familles (sans ouvrir Revit).
    /// Conserve UpdatedUtc pour compat avec ton code existant.
    /// </summary>
    public sealed class FamilyPartAtomMeta
    {
        // (1) OmniClass
        public string OmniClassCode { get; set; }          // ex: "23.40.20.00" ou "23.40.20.14.14.21"
        public string OmniClassTitle { get; set; }         // optionnel si trouvé

        // (2) Catégorie Revit
        public string Category { get; set; }               // ex: "Mobilier"

        // (3) Version Revit de sauvegarde (année)
        public string RevitSavedVersion { get; set; }      // ex: "2023"

        // (4) Dernière sauvegarde (gardé pour compatibilité UI)
        public DateTime? UpdatedUtc { get; set; }

        // (5) Taille du fichier
        public long? FileSizeBytes { get; set; }

        public string Path { get; set; }
    }

    /// <summary>
    /// Fournit des métadonnées par lecture directe du fichier .RFA (PartAtom/texte).
    /// API conservée (Initialize/IsAvailable/RequestOmniClassNumberAsync).
    /// </summary>
    public static class FamilyMetadataProvider
    {
        // ===== Compat API attendue par ton plugin =====
        public static bool IsAvailable => true;

        // Conserver la signature même si elle est no-op (pas d'ExternalEvent ici).
        public static void Initialize(UIApplication app) { /* no-op pour compat */ }

        // API historique : renvoie uniquement le code OmniClass (ou null).
        public static Task<string> RequestOmniClassNumberAsync(string familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath) || !File.Exists(familyPath))
                return Task.FromResult<string>(null);

            string code = ExtractOmniClassNumber(familyPath);
            return Task.FromResult(code);
        }

        // ===== Nouvelle API rapide (si tu l'utilises déjà) =====
        public static Task<FamilyPartAtomMeta> RequestFastMetadataAsync(string familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath) || !File.Exists(familyPath))
                return Task.FromResult<FamilyPartAtomMeta>(null);

            return Task.Run(() => ExtractFastMetadata(familyPath));
        }

        public static async Task<List<FamilyPartAtomMeta>> RequestFastMetadataBatchAsync(IEnumerable<string> rfaPaths)
        {
            var results = new List<FamilyPartAtomMeta>();
            if (rfaPaths == null) return results;

            var tasks = new List<Task<FamilyPartAtomMeta>>();
            foreach (var p in rfaPaths) tasks.Add(RequestFastMetadataAsync(p));
            results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
            return results;
        }

        // ===== Implémentation =====

        // .NET Framework : pas d'Encoding.Latin1 → ISO-8859-1 pour un mapping 1:1 byte→char
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        // Regex précompilées
        private static readonly Regex RxOmniCode = new Regex(@"\b(\d{2}(?:\.\d{2}){3,10})\b", RegexOptions.Compiled);
        private static readonly Regex RxOmniTitle = new Regex(@"(Titre[_\-\s]*Omniclass|Omni\s*Class\s*Title|Omniclass\s*Title)[^>]*>\s*([^<]{1,200})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxCategoryTerm = new Regex(@"<\s*category\s*>\s*<\s*term\s*>\s*([^<]{1,200}?)\s*</\s*term\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxProductVersion = new Regex(@"<\s*(?:[A-Za-z0-9]+:)?product-version\s*>\s*(\d{4})\s*</\s*(?:[A-Za-z0-9]+:)?product-version\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxUpdated = new Regex(@"<\s*updated\s*>\s*([0-9T:\-\.Z\+]+)\s*</\s*updated\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);



        private static string ExtractOmniClassNumber(string path)
        {
            string omni = null;
            try
            {
                const int chunk = 256 * 1024;
                const int overlap = 4 * 1024;
                byte[] buffer = new byte[chunk + overlap];

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
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
                            int start = Math.Max(0, idx - 4096);
                            int len = Math.Min(8192, text.Length - start);
                            string window = text.Substring(start, len);
                            var m = RxOmniCode.Match(window);
                            if (m.Success) { omni = m.Groups[1].Value; return omni; }
                        }

                        if (total > overlap) { Buffer.BlockCopy(buffer, total - overlap, buffer, 0, overlap); carry = overlap; }
                        else carry = total;
                    }
                }
            }
            catch { /* ignore → null */ }
            return omni;
        }

        private static FamilyPartAtomMeta ExtractFastMetadata(string path)
        {
            var meta = new FamilyPartAtomMeta
            {
                Path = path,
                FileSizeBytes = SafeGetSize(path)
            };

            TryReadPartAtom(path, meta);
            return meta;
        }

        private static long? SafeGetSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return null; }
        }

        private static void TryReadPartAtom(string path, FamilyPartAtomMeta meta)
        {
            try
            {
                const int chunk = 256 * 1024;
                const int overlap = 4 * 1024;
                byte[] buffer = new byte[chunk + overlap];

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
                {
                    int carry = 0;
                    while (true)
                    {
                        int read = fs.Read(buffer, carry, chunk);
                        if (read <= 0) break;
                        int total = read + carry;

                        string text = Latin1.GetString(buffer, 0, total);

                        // OmniClass (code + titre à proximité)
                        if (meta.OmniClassCode == null)
                        {
                            int idx = text.IndexOf("omniclass", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                int start = Math.Max(0, idx - 4096);
                                int len = Math.Min(8192, text.Length - start);
                                string window = text.Substring(start, len);

                                var m = RxOmniCode.Match(window);
                                if (m.Success) meta.OmniClassCode = m.Groups[1].Value;

                                if (meta.OmniClassTitle == null)
                                {
                                    var t = RxOmniTitle.Match(window);
                                    if (t.Success)
                                        meta.OmniClassTitle = FixUtf8Mojibake(t.Groups[2].Value.Trim());
                                }
                            }
                        }

                        // Catégorie (1er term non code OmniClass)
                        if (meta.Category == null)
                        {
                            foreach (Match m in RxCategoryTerm.Matches(text))
                            {
                                var termRaw = m.Groups[1].Value.Trim();
                                var term = FixUtf8Mojibake(termRaw);
                                if (!LooksLikeOmniClassCode(term))
                                {
                                    meta.Category = term;
                                    break;
                                }
                            }
                        }

                        // Version Revit
                        if (meta.RevitSavedVersion == null)
                        {
                            var m = RxProductVersion.Match(text);
                            if (m.Success) meta.RevitSavedVersion = m.Groups[1].Value;
                        }

                        // Dernière mise à jour (pour compat UpdatedUtc)
                        if (meta.UpdatedUtc == null)
                        {
                            var m = RxUpdated.Match(text);
                            if (m.Success && DateTime.TryParse(
                                    m.Groups[1].Value,
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                    out var dt))
                            {
                                meta.UpdatedUtc = dt;
                            }
                        }



                        // chevauchement
                        if (total > overlap) { Buffer.BlockCopy(buffer, total - overlap, buffer, 0, overlap); carry = overlap; }
                        else carry = total;

                        // Stop anticipé si l'essentiel est déjà trouvé
                        if (meta.OmniClassCode != null && meta.Category != null && meta.RevitSavedVersion != null)
                        {
                            // on continue un peu pour tenter OmniClassTitle, sinon on pourrait break
                        }
                    }
                }
            }
            catch
            {
                // silencieux : on préfère renvoyer ce qu'on a (ou nulls) plutôt que planter l'UI
            }
        }

        // ===== Helpers =====

        private static bool LooksLikeOmniClassCode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return Regex.IsMatch(s.Trim(), @"^\d{2}(?:\.\d{2}){3,10}$");
        }

        // Répare les “gÃ©nie” → “génie” si l’UTF-8 a été lu en Latin-1.
        private static string FixUtf8Mojibake(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('Ã') >= 0 || s.IndexOf('Â') >= 0)
            {
                try
                {
                    var bytes = Latin1.GetBytes(s);
                    var utf8 = Encoding.UTF8.GetString(bytes);
                    if (utf8.IndexOf('Ã') < 0 && utf8.IndexOf('Â') < 0) return utf8;
                }
                catch { }
            }
            return s;
        }

    }
}

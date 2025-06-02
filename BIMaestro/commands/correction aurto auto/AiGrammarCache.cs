using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ScanTextRevit
{
    /// <summary>
    /// Cache simple pour stocker les réponses de l'API et éviter des appels redondants.
    /// </summary>
    public class AiGrammarCache
    {
        private Dictionary<string, List<CorrectionItem>> _cache = new Dictionary<string, List<CorrectionItem>>();

        public string ComputeHash(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                return string.Concat(hashBytes.Select(b => b.ToString("X2")));
            }
        }

        public bool TryGet(string key, out List<CorrectionItem> corrections)
        {
            return _cache.TryGetValue(key, out corrections);
        }

        public void Add(string key, List<CorrectionItem> corrections)
        {
            if (!_cache.ContainsKey(key))
                _cache[key] = corrections;
        }
    }
}
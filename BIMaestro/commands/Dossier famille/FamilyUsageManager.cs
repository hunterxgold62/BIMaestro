using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Famille
{
    public static class FamilyUsageManager
    {
        // Chemin du JSON d’usage
        private static readonly string UsageFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "FamilyUsage.json");

        // Lit ou renvoie un dictionnaire vide
        public static Dictionary<string, int> Load()
        {
            if (!File.Exists(UsageFile))
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var json = File.ReadAllText(UsageFile);
                return JsonConvert.DeserializeObject<Dictionary<string, int>>(json)
                       ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        // Sauvegarde le dictionnaire en JSON
        public static void Save(Dictionary<string, int> usage)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UsageFile));
            File.WriteAllText(UsageFile, JsonConvert.SerializeObject(usage));
        }

        // Incrémente le compteur pour une famille donnée
        public static void RegisterUse(string familyPath)
        {
            var usage = Load();
            usage[familyPath] = usage.TryGetValue(familyPath, out var count) ? count + 1 : 1;
            Save(usage);
        }
    }
}

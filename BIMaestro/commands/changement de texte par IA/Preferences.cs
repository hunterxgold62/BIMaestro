using System;
using System.IO;
using Newtonsoft.Json;

namespace IA
{
    public class Preferences
    {
        public bool IsDarkTheme { get; set; }

        // Génère un chemin automatique vers \Documents\RevitLogs\SauvegardePreference\preferences.json
        private static string GetPrefFilePath()
        {
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                "SauvegardePréférence"
            );
            Directory.CreateDirectory(baseDir); // crée le dossier si besoin
            return Path.Combine(baseDir, "thème IA.json");
        }

        public static Preferences LoadPreferences()
        {
            try
            {
                string filePath = GetPrefFilePath();
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    Preferences prefs = JsonConvert.DeserializeObject<Preferences>(json);
                    if (prefs != null)
                    {
                        return prefs;
                    }
                }
            }
            catch
            {
                // Ignorer l'erreur et renvoyer la config par défaut
            }
            return new Preferences { IsDarkTheme = false };
        }

        public static void SavePreferences(Preferences prefs)
        {
            try
            {
                string filePath = GetPrefFilePath();
                string json = JsonConvert.SerializeObject(prefs, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch
            {
                // Gérer erreur écriture si besoin
            }
        }
    }
}

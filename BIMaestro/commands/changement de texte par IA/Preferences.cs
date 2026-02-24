using System;
using System.IO;
using Newtonsoft.Json;

namespace IA { }

public class Preferences
{
    private static string GetPrefFilePath()
    {
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "SauvegardePréférence"
        );
        Directory.CreateDirectory(baseDir);
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
        }

        return new Preferences();
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
        }
    }
}
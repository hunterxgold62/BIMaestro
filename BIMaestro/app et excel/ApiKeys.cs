using System;
using System.IO;

namespace IA
{
    public static class ApiKeys
    {
        private static readonly string basePath = @"P:\0-Boîte à outils Revit\5-Logiciels\Plugin Revit\Clé IA";

        public static string OpenAIKey => ReadKeyFromFile("Clé IA OpenIA.txt");
        public static string DeepSeekKey => ReadKeyFromFile("Clé IA DeepSeek.txt");

        private static string ReadKeyFromFile(string fileName)
        {
            string filePath = Path.Combine(basePath, fileName);

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Le fichier de clé API est introuvable : {filePath}");

            string key = File.ReadAllText(filePath).Trim();

            if (string.IsNullOrEmpty(key))
                throw new Exception($"La clé API dans le fichier '{filePath}' est vide.");

            return key;
        }
    }
}

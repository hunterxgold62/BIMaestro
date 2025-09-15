using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Famille
{
    public class Collection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public List<string> Paths { get; set; } = new List<string>();
    }

    public static class CollectionStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "Collections.json");

        public static List<Collection> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<Collection>();
                var json = File.ReadAllText(FilePath);
                return JsonConvert.DeserializeObject<List<Collection>>(json) ?? new List<Collection>();
            }
            catch { return new List<Collection>(); }
        }

        public static void Save(List<Collection> collections)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(collections, Formatting.Indented));
        }
    }
}

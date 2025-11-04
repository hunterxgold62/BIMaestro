using System;
using System.IO;
using Newtonsoft.Json;

namespace Famille
{
    public class RadialMenuCollectionState
    {
        public bool UseCollection { get; set; }
        public string ActiveCollectionId { get; set; }
        public string LastCollectionId { get; set; }
    }

    public static class RadialMenuCollectionStateStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "RadialMenuCollectionState.json");

        public static RadialMenuCollectionState Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new RadialMenuCollectionState();

                var json = File.ReadAllText(FilePath);
                var state = JsonConvert.DeserializeObject<RadialMenuCollectionState>(json);
                return state ?? new RadialMenuCollectionState();
            }
            catch
            {
                return new RadialMenuCollectionState();
            }
        }

        public static void Save(RadialMenuCollectionState state)
        {
            try
            {
                var toSave = state ?? new RadialMenuCollectionState();
                var folder = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                var json = JsonConvert.SerializeObject(toSave, Formatting.Indented);
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // ignore
            }
        }

        public static void UpdateLastSelection(string collectionId)
        {
            var state = Load();
            state.LastCollectionId = string.IsNullOrWhiteSpace(collectionId) ? null : collectionId;
            Save(state);
        }
    }
}

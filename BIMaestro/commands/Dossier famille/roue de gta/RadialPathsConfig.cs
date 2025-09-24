using System;
using System.IO;
using Newtonsoft.Json;

namespace BIMaestro.UI
{
    internal sealed class RadialPathsConfig
    {
        public string FamiliesFolder { get; set; }
        public string ImagesFolder { get; set; }

        public static RadialPathsConfig LoadOrNull()
        {
            try
            {
                var file = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs", "SauvegardePréférence", "CheminsFamille.json");
                if (!File.Exists(file)) return null;
                return JsonConvert.DeserializeObject<RadialPathsConfig>(File.ReadAllText(file));
            }
            catch { return null; }
        }
    }
}

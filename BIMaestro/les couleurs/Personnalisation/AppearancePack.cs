using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Newtonsoft.Json;

namespace Couleur
{
    public sealed class AppearancePack
    {
        public string Format { get; set; } = "BIMaestro.Appearance";
        public int Version { get; set; } = 1;
        public bool RibbonEnabled { get; set; }
        public bool FullPanels { get; set; }
        public Dictionary<string, RibbonPanelColorScheme> Ribbon { get; set; }
        public ProjectBrowserColorSettings Browser { get; set; }
        public BrowserIconSettings Icons { get; set; }
    }

    public static class AppearancePackFile
    {
        private static JsonSerializerSettings Settings => new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 32,
            Converters = { new PackColorConverter() }
        };

        public static void Export(string path, AppearancePack pack)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(pack, Formatting.Indented, Settings));
        }

        public static AppearancePack Import(string path)
        {
            if (new FileInfo(path).Length > 20 * 1024 * 1024)
                throw new InvalidDataException("Le pack dépasse 20 Mo.");
            var json = File.ReadAllText(path);
            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 32 })
            {
                var header = Newtonsoft.Json.Linq.JObject.Load(reader);
                if ((string)header["Format"] != "BIMaestro.Appearance" || (int?)header["Version"] != 1)
                    throw new InvalidDataException("Ce fichier n’est pas un pack BIMaestro compatible.");
            }
            var pack = JsonConvert.DeserializeObject<AppearancePack>(json, Settings);
            if (pack == null || pack.Format != "BIMaestro.Appearance" || pack.Version != 1 ||
                pack.Ribbon == null || pack.Browser == null || pack.Icons == null ||
                pack.Icons.Rules == null || pack.Icons.CustomAssets == null || pack.Browser.CategoryColorRules == null)
                throw new InvalidDataException("Ce fichier n’est pas un pack BIMaestro compatible.");
            if (pack.Ribbon.Any(p => p.Value == null) || pack.Icons.Rules.Any(r => r == null) ||
                pack.Browser.CategoryColorRules.Any(r => r == null))
                throw new InvalidDataException("Le pack contient une règle incomplète.");
            var ids = new HashSet<string>(ProjectBrowserIcons.Assets(new BrowserIconSettings()).Select(a => a.Id));
            foreach (var asset in pack.Icons.CustomAssets)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.Id) || !ids.Add(asset.Id))
                    throw new InvalidDataException("Le pack contient une image invalide ou en double.");
                using (var stream = new MemoryStream(Convert.FromBase64String(asset.Data ?? "")))
                    asset.Data = ProjectBrowserIcons.EncodeImage(stream);
            }
            if (pack.Icons.Rules.Any(r => r.IconId == null || !ids.Contains(r.IconId)))
                throw new InvalidDataException("Une icône utilisée par ce pack est manquante.");
            return pack;
        }

        private sealed class PackColorConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(Color);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var color = (Color)value;
                writer.WriteValue($"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");
            }
            public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType != JsonToken.String)
                    throw new JsonSerializationException("Couleur invalide dans le pack.");
                return ColorConverter.ConvertFromString((string)reader.Value);
            }
        }
    }
}

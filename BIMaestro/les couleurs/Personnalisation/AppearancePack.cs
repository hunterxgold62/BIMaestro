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
        // Defaults keep packs created before scoped exports fully compatible.
        public bool IncludesRibbon { get; set; } = true;
        public bool IncludesBrowser { get; set; } = true;
        public bool IncludesProperties { get; set; }
        public bool IncludesHome { get; set; }
        public BIMaestro.PropertiesAppearance.AppearancePreferences Properties { get; set; }
        public BIMaestro.Home.HomeAppearanceSettings Home { get; set; }
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
            Converters = { new PackColorConverter(), new PropertyLayoutConverter() }
        };

        public static void Export(string path, AppearancePack pack)
        {
            string json = Serialize(pack);
            Deserialize(json); // A backup must be restorable before the caller can reset anything.
            string target = Path.GetFullPath(path), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static AppearancePack Import(string path)
        {
            if (new FileInfo(path).Length > 20 * 1024 * 1024)
                throw new InvalidDataException("Le pack dépasse 20 Mo.");
            return Deserialize(File.ReadAllText(path));
        }

        internal static string Serialize(AppearancePack pack)
        {
            if (pack == null)
                throw new ArgumentNullException(nameof(pack));

            return JsonConvert.SerializeObject(pack, Formatting.Indented, Settings);
        }

        internal static AppearancePack Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 20 * 1024 * 1024)
                throw new InvalidDataException("Le pack est vide ou dépasse 20 Mo.");

            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 32 })
            {
                var header = Newtonsoft.Json.Linq.JObject.Load(reader);
                if ((string)header["Format"] != "BIMaestro.Appearance" || (int?)header["Version"] != 1)
                    throw new InvalidDataException("Ce fichier n’est pas un pack BIMaestro compatible.");
            }
            var pack = JsonConvert.DeserializeObject<AppearancePack>(json, Settings);
            if (pack == null || pack.Format != "BIMaestro.Appearance" || pack.Version != 1 ||
                (!pack.IncludesRibbon && !pack.IncludesBrowser && !pack.IncludesProperties && !pack.IncludesHome) ||
                (pack.IncludesRibbon && pack.Ribbon == null) ||
                (pack.IncludesBrowser && (pack.Browser == null || pack.Icons == null ||
                    pack.Icons.Rules == null || pack.Icons.CustomAssets == null || pack.Browser.CategoryColorRules == null)))
                throw new InvalidDataException("Ce fichier n’est pas un pack BIMaestro compatible.");
            if (pack.IncludesProperties) ValidateProperties(pack.Properties);
            if (pack.IncludesHome && (pack.Home == null || !new[] { "native", "2023", "2024", "2025", "2026" }.Contains(pack.Home.Style)))
                throw new InvalidDataException("Le pack contient un accueil Revit invalide.");
            if ((pack.IncludesRibbon && pack.Ribbon.Any(p => p.Value == null)) ||
                (pack.IncludesBrowser && (pack.Icons.Rules.Any(r => r == null) ||
                    pack.Browser.CategoryColorRules.Any(r => r == null))))
                throw new InvalidDataException("Le pack contient une règle incomplète.");
            if (!pack.IncludesBrowser)
                return pack;
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

        private static void ValidateProperties(BIMaestro.PropertiesAppearance.AppearancePreferences preferences)
        {
            if (preferences == null || preferences.Version != 1 || preferences.Profiles == null ||
                preferences.Profiles.Any(p => p == null || string.IsNullOrWhiteSpace(p.Key) || p.Rules == null || p.Layout == null) ||
                preferences.Profiles.Select(p => p.Key).Distinct().Count() != preferences.Profiles.Count)
                throw new InvalidDataException("Le pack contient des profils de propriétés invalides.");
            foreach (var profile in preferences.Profiles)
            {
                var layout = profile.Layout;
                if (layout.Groups == null || layout.Groups.Any(string.IsNullOrWhiteSpace) || layout.Parameters == null ||
                    layout.Parameters.Any(p => p.Value == null || p.Value.Any(string.IsNullOrWhiteSpace)) ||
                    layout.Parameters.ContainsKey("bimaestro:custom-groups:v1") ||
                    profile.Rules.Any(r => r == null || (r.Kind != "group" && r.Kind != "parameter") ||
                        string.IsNullOrWhiteSpace(r.TargetKey) || r.Light == null || r.Dark == null ||
                        new[] { r.Light.Text, r.Light.Background, r.Dark.Text, r.Dark.Background }.Any(c =>
                            !string.IsNullOrEmpty(c) && !BIMaestro.PropertiesAppearance.AppearancePreferences.Parse(c).HasValue)))
                    throw new InvalidDataException("Le pack contient une personnalisation de propriétés incomplète.");
                if (layout.CustomGroups == null) continue;
                var keys = new HashSet<string>(); var names = new HashSet<string>(); var members = new HashSet<string>();
                if (layout.CustomGroups.Count > 20) throw new InvalidDataException("Maximum : 20 groupes personnalisés par profil.");
                foreach (var group in layout.CustomGroups)
                {
                    if (group == null || string.IsNullOrWhiteSpace(group.Key) || !group.Key.StartsWith("custom:", StringComparison.Ordinal) ||
                        !keys.Add(group.Key) || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 80 || group.Name.Any(char.IsControl) ||
                        !names.Add(BIMaestro.PropertiesAppearance.AppearancePreferences.Normalize(group.Name)) || group.Members == null ||
                        group.Members.Count > 256 || group.Members.Any(m => string.IsNullOrWhiteSpace(m) || !members.Add(m)))
                        throw new InvalidDataException("Le pack contient un groupe personnalisé invalide.");
                }
            }
        }

        // Local property files reference sidecars. Packs carry the complete groups inline.
        private sealed class PropertyLayoutConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(BIMaestro.PropertiesAppearance.AppearanceLayout);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var layout = (BIMaestro.PropertiesAppearance.AppearanceLayout)value;
                writer.WriteStartObject();
                writer.WritePropertyName("Groups"); serializer.Serialize(writer, layout.Groups);
                writer.WritePropertyName("Parameters"); serializer.Serialize(writer, layout.Parameters.Where(p => p.Key != "bimaestro:custom-groups:v1").ToDictionary(p => p.Key, p => p.Value));
                writer.WritePropertyName("CustomGroups"); serializer.Serialize(writer, layout.CustomGroups);
                writer.WriteEndObject();
            }
            public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
            {
                var value = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new BIMaestro.PropertiesAppearance.AppearanceLayout {
                    Groups = value["Groups"]?.ToObject<List<string>>(serializer),
                    Parameters = value["Parameters"]?.ToObject<Dictionary<string, List<string>>>(serializer),
                    CustomGroups = value["CustomGroups"]?.ToObject<List<BIMaestro.PropertiesAppearance.AppearanceCustomGroup>>(serializer)
                };
            }
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

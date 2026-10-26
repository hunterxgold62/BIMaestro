using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Security.Cryptography;
using System.Windows.Media;
using Newtonsoft.Json;

namespace BIMaestro.PropertiesAppearance
{
    public sealed class PaletteColors
    {
        public string Text { get; set; }
        public string Background { get; set; }
        public PaletteColors Copy() => new PaletteColors { Text = Text, Background = Background };
        public bool IsEmpty => string.IsNullOrEmpty(Text) && string.IsNullOrEmpty(Background);
    }

    public sealed class AppearanceRule
    {
        public string Kind { get; set; } = "group";
        public string TargetKey { get; set; } = "";
        public string Label { get; set; } = "";
        public PaletteColors Light { get; set; } = new PaletteColors();
        public PaletteColors Dark { get; set; } = new PaletteColors();
        public AppearanceRule Copy() => new AppearanceRule { Kind = Kind, TargetKey = TargetKey, Label = Label, Light = Light.Copy(), Dark = Dark.Copy() };
    }

    public sealed class AppearanceCustomGroup
    {
        public string Key { get; set; } = "custom:" + Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Mes paramètres";
        public List<string> Members { get; set; } = new List<string>();
        public AppearanceCustomGroup Copy() => new AppearanceCustomGroup { Key=Key,Name=Name,Members=Members.ToList() };
    }

    public sealed class AppearanceLayout
    {
        public List<string> Groups { get; set; } = new List<string>();
        public Dictionary<string, List<string>> Parameters { get; set; } = new Dictionary<string, List<string>>();
        // null inherits the wider scope; an explicit empty list clears inherited custom groups.
        [JsonIgnore] public List<AppearanceCustomGroup> CustomGroups { get; set; }
        [JsonIgnore] public bool IsEmpty => Groups.Count == 0 && !Parameters.Any(p => p.Value.Count > 0) && CustomGroups==null;
        public AppearanceLayout Copy() => new AppearanceLayout { Groups = Groups.ToList(), Parameters = Parameters.ToDictionary(p => p.Key, p => p.Value.ToList()),CustomGroups=CustomGroups?.Select(g=>g.Copy()).ToList() };
    }

    public sealed class AppearanceProfile
    {
        public string Key { get; set; } = "all";
        public string Name { get; set; } = "Toutes les propriétés";
        public List<AppearanceRule> Rules { get; set; } = new List<AppearanceRule>();
        public AppearanceLayout Layout { get; set; } = new AppearanceLayout();
        public AppearanceProfile Copy() => new AppearanceProfile { Key = Key, Name = Name, Rules = Rules.Select(r => r.Copy()).ToList(), Layout = Layout.Copy() };
        public override string ToString() => Name;
    }

    public sealed class AppearancePreferences
    {
        public int Version { get; set; } = 1;
        public bool Enabled { get; set; }
        public List<AppearanceProfile> Profiles { get; set; } = new List<AppearanceProfile>();
        public IEnumerable<AppearanceProfile> Matching(string category, string type) => new[] { "all", category, type }
            .Where(k => !string.IsNullOrEmpty(k)).Select(k => Profiles.FirstOrDefault(p => p.Key == k)).Where(p => p != null);

        public PaletteColors Resolve(string category, string type, string group, string parameter, bool dark)
        {
            PaletteColors result = null;
            foreach (var profile in Matching(category, type))
                foreach (var rule in profile.Rules.OrderBy(r => r.Kind == "parameter" ? 1 : 0))
                    if ((rule.Kind == "group" && rule.TargetKey == group) ||
                        (rule.Kind == "parameter" && parameter != null && rule.TargetKey == parameter))
                    {
                        var colors = dark ? rule.Dark : rule.Light;
                        if (!colors.IsEmpty) result = colors;
                    }
            return result;
        }

        public static string Normalize(string value)
        {
            var b = new StringBuilder();
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormD))
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && !char.IsWhiteSpace(c))
                    b.Append(char.ToUpperInvariant(c));
            return b.ToString();
        }

        public static Color? Parse(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try { var c = (Color)ColorConverter.ConvertFromString(value); return c.A == 255 ? c : (Color?)null; }
            catch { return null; }
        }

        public static double Contrast(Color a, Color b)
        {
            double L(Color c) { double S(byte v) { double x = v / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); } return .2126 * S(c.R) + .7152 * S(c.G) + .0722 * S(c.B); }
            double x = L(a), y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }

        public static Color ReadableText(PaletteColors colors, bool dark)
        {
            // An explicit choice must match the picker in both the preview and Revit.
            // Keep black ink by default when only a background is chosen.
            var chosen = Parse(colors.Text);
            if (chosen.HasValue) return chosen.Value;
            return Colors.Black;
        }
    }

    internal static class AppearanceStore
    {
        // Existing BIMaestro versions preserve parameter-order entries but drop unknown JSON
        // properties. Keep immutable group snapshots and reference them through that known map.
        private const string GroupsReference="bimaestro:custom-groups:v1";
        private static string Digest(string json)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-","");}
        private static void ReadGroups(AppearancePreferences preferences,Newtonsoft.Json.Linq.JObject document)
        {
            foreach(var profile in preferences.Profiles)
            {
                if(profile.Layout==null)continue;
                if(profile.Layout.Parameters!=null&&profile.Layout.Parameters.TryGetValue(GroupsReference,out var reference))
                {
                    if(reference==null||reference.Count!=1||reference[0]==null||reference[0].Length!=64||reference[0].Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("Référence de groupes personnalisés invalide.");
                    string json=File.ReadAllText(Path.Combine(FilePath+".groups",reference[0].ToUpperInvariant()+".json"));
                    if(!string.Equals(Digest(json),reference[0],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("La sauvegarde des groupes personnalisés a été modifiée.");
                    profile.Layout.CustomGroups=JsonConvert.DeserializeObject<List<AppearanceCustomGroup>>(json)??throw new InvalidDataException("Sauvegarde des groupes vide.");
                }
                else
                {
                    // Migrate inline group definitions from the initial experimental builds.
                    var legacy=document["Profiles"]?.FirstOrDefault(p=>(string)p["Key"]==profile.Key)?["Layout"]?["CustomGroups"];
                    if(legacy!=null&&legacy.Type!=Newtonsoft.Json.Linq.JTokenType.Null)profile.Layout.CustomGroups=legacy.ToObject<List<AppearanceCustomGroup>>();
                }
            }
        }
        private static void WriteGroups(AppearanceLayout layout)
        {
            if(layout.CustomGroups==null){layout.Parameters.Remove(GroupsReference);return;}
            string json=JsonConvert.SerializeObject(layout.CustomGroups,Formatting.None),hash=Digest(json),folder=FilePath+".groups";
            Directory.CreateDirectory(folder);string target=Path.Combine(folder,hash+".json");
            if(!File.Exists(target))
            {
                string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{File.WriteAllText(temp,json,new UTF8Encoding(false));File.Move(temp,target);}finally{if(File.Exists(temp))File.Delete(temp);}
            }
            else if(Digest(File.ReadAllText(target))!=hash)throw new InvalidDataException("La sauvegarde des groupes personnalisés est incohérente.");
            layout.Parameters[GroupsReference]=new List<string>{hash};
        }
        #if BIMAESTRO_PROPERTIES_TESTS
        internal static string TestFilePath;
#endif
        internal static string FilePath =>
#if BIMAESTRO_PROPERTIES_TESTS
            TestFilePath ??
#endif
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMaestro", "PropertiesAppearance.json");
        internal static AppearancePreferences Load()
        {
            if (!File.Exists(FilePath)) return new AppearancePreferences();
            string json=File.ReadAllText(FilePath);
            var value = JsonConvert.DeserializeObject<AppearancePreferences>(json);
            if (value == null || value.Version != 1) throw new InvalidDataException("Format de préférences des propriétés non reconnu.");
            value.Profiles = value.Profiles ?? new List<AppearanceProfile>();
            ReadGroups(value,Newtonsoft.Json.Linq.JObject.Parse(json));
            foreach (var p in value.Profiles)
            {
                p.Rules = p.Rules ?? new List<AppearanceRule>();
                p.Layout = p.Layout ?? new AppearanceLayout(); p.Layout.Groups = p.Layout.Groups ?? new List<string>();
                p.Layout.Parameters = p.Layout.Parameters ?? new Dictionary<string, List<string>>();
                foreach (var key in p.Layout.Parameters.Keys.ToArray()) p.Layout.Parameters[key] = p.Layout.Parameters[key] ?? new List<string>();
                if(p.Layout.CustomGroups!=null)
                {
                    if(p.Layout.CustomGroups.Count>20)throw new InvalidDataException("Maximum : 20 groupes personnalisés par profil.");
                    var keys=new HashSet<string>();var names=new HashSet<string>();var members=new HashSet<string>();
                    foreach(var g in p.Layout.CustomGroups)
                    {
                        if(g==null||string.IsNullOrWhiteSpace(g.Key)||!g.Key.StartsWith("custom:",StringComparison.Ordinal)||!keys.Add(g.Key)||string.IsNullOrWhiteSpace(g.Name)||g.Name.Length>80||g.Name.Any(char.IsControl)||!names.Add(AppearancePreferences.Normalize(g.Name)))throw new InvalidDataException("Groupe personnalisé invalide ou dupliqué.");
                        g.Members=g.Members??new List<string>();
                        if(g.Members.Count>256||g.Members.Any(k=>string.IsNullOrWhiteSpace(k)||!members.Add(k)))throw new InvalidDataException("Un paramètre ne peut appartenir qu’à un groupe personnalisé.");
                    }
                }
                foreach (var r in p.Rules) { r.Light = r.Light ?? new PaletteColors(); r.Dark = r.Dark ?? new PaletteColors(); }
            }
            return value;
        }

        // Merge a single profile under a cross-process lock: another Revit's profiles survive.
        internal static AppearancePreferences Save(AppearanceProfile profile, bool enabled) => SaveMany(new[] { profile }, enabled);

        internal static AppearancePreferences SaveMany(IEnumerable<AppearanceProfile> profiles, bool enabled) => Persist(profiles, enabled, false);
        internal static AppearancePreferences ResetAll() => Persist(new AppearanceProfile[0], false, true);
        private static AppearancePreferences Persist(IEnumerable<AppearanceProfile> profiles, bool enabled, bool resetAll)
        {
            using (var mutex = new Mutex(false, "Local\\BIMaestro.PropertiesAppearance." + Environment.UserName))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Les préférences sont utilisées dans une autre fenêtre Revit. Réessayez.");
                    var latest = Load(); latest.Enabled = enabled; if (resetAll) latest.Profiles.Clear();
                    foreach (var profile in profiles)
                    {
                        latest.Profiles.RemoveAll(p => p.Key == profile.Key);
                        if (!profile.Layout.IsEmpty || profile.Rules.Any(r => !r.Light.IsEmpty || !r.Dark.IsEmpty)) latest.Profiles.Add(profile.Copy());
                    }
                    foreach(var profile in latest.Profiles)WriteGroups(profile.Layout);
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temp, JsonConvert.SerializeObject(latest, Formatting.Indented), new UTF8Encoding(false));
                        if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak"); else File.Move(temp, FilePath);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    return latest;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
    }
}

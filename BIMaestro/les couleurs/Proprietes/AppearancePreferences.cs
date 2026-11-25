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
        // Style of the rule that produced these colors (runtime only, see AppearanceStyles).
        [JsonIgnore] public string Style { get; set; }
        [JsonIgnore] public string Icon { get; set; }
        // "2/5" when a condition matches only part of a multiple selection.
        [JsonIgnore] public string Badge { get; set; }
        public PaletteColors Copy() => new PaletteColors { Text = Text, Background = Background, Style = Style, Icon = Icon, Badge = Badge };
        public bool IsEmpty => string.IsNullOrEmpty(Text) && string.IsNullOrEmpty(Background);
    }

    public sealed class AppearanceRule
    {
        public string Kind { get; set; } = "group";
        public string TargetKey { get; set; } = "";
        public string Label { get; set; } = "";
        public PaletteColors Light { get; set; } = new PaletteColors();
        public PaletteColors Dark { get; set; } = new PaletteColors();
        // null or "Fond" keeps the historical full-cell painting.
        public string Style { get; set; }
        // Derive the pale background and the dark variant from the main color.
        public bool AutoTint { get; set; }
        // Built-in project browser icon id shown after the label (for example "structure").
        public string Icon { get; set; }
        // Optional: the look applies only when the parameter value matches (parameter rules only).
        public RuleCondition When { get; set; }
        public AppearanceRule Copy() => new AppearanceRule { Kind = Kind, TargetKey = TargetKey, Label = Label, Light = Light.Copy(), Dark = Dark.Copy(), Style = Style, AutoTint = AutoTint, Icon = Icon, When = When?.Copy() };
    }

    public sealed class RuleCondition
    {
        internal static readonly string[] Operators = { "=", "!=", "contains", "empty", "notempty", ">", "<" };
        public string Operator { get; set; } = "=";
        public string Value { get; set; } = "";
        public RuleCondition Copy() => new RuleCondition { Operator = Operator, Value = Value };
        internal bool NeedsValue => Operator != "empty" && Operator != "notempty";
        // Compares with the value as Revit displays it, in the project units.
        internal bool Matches(string displayed)
        {
            string a = Clean(displayed), b = Clean(Value);
            switch (Operator)
            {
                case "=": return string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);
                case "!=": return !string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);
                case "contains": return b.Length > 0 && a.IndexOf(b, StringComparison.CurrentCultureIgnoreCase) >= 0;
                case "empty": return a.Length == 0;
                case "notempty": return a.Length > 0;
                case ">": return Number(a, out double x) && Number(b, out double y) && x > y;
                case "<": return Number(a, out double u) && Number(b, out double v) && u < v;
            }
            return false;
        }
        private static string Clean(string s) => (s ?? "").Replace(' ', ' ').Trim();
        private static bool Number(string s, out double value)
        {
            // "4.0000", "38.400 m²", "12,5 mm": read the leading number, dot or comma.
            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"-?\d+(?:[.,]\d+)?");
            return double.TryParse(m.Success ? m.Value.Replace(',', '.') : "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }
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

        // values: displayed value of the parameter for each selected element (null when unknown).
        public PaletteColors Resolve(string category, string type, string group, string parameter, bool dark, Func<string, IList<string>> values = null)
        {
            PaletteColors result = null;
            PaletteColors zebra = null;
            foreach (var profile in Matching(category, type))
                // Group looks first, then parameter looks, then conditional looks (which win when they match).
                foreach (var rule in profile.Rules.OrderBy(r => r.Kind == "parameter" ? 1 : 0).ThenBy(r => r.When != null ? 1 : 0))
                    if ((rule.Kind == "group" && rule.TargetKey == group) ||
                        (rule.Kind == "parameter" && parameter != null && rule.TargetKey == parameter))
                    {
                        var colors = rule.AutoTint ? AppearanceStyles.Tint(rule, dark) : dark ? rule.Dark : rule.Light;
                        if (colors == null || colors.IsEmpty) continue;
                        string badge = null;
                        if (rule.When != null)
                        {
                            // A condition only reads a parameter value: never on a group title.
                            if (rule.Kind != "parameter" || parameter == null) continue;
                            var list = values?.Invoke(parameter);
                            if (list == null || list.Count == 0) continue;
                            int match = list.Count(v => rule.When.Matches(v));
                            if (match == 0) continue;
                            if (match < list.Count) badge = match + "/" + list.Count;
                        }
                        var set = AppearanceStyles.Parse(rule.Style);
                        bool inherited = rule.Kind == "group" && parameter != null;
                        // Alternate rows come from the group, even when a parameter has its own look.
                        if (inherited && set.Zebra) { zebra = colors.Copy(); zebra.Style = AppearanceStyles.ZebraOnly; }
                        if (inherited && set.HeaderOnly) continue;
                        result = colors.Copy(); result.Style = set.ToString();
                        // The group icon belongs to the group title, not to each of its rows.
                        result.Icon = inherited ? null : rule.Icon;
                        result.Badge = badge;
                    }
            if (zebra != null)
            {
                if (result == null) return zebra;
                var set = AppearanceStyles.Parse(result.Style); set.Zebra = true; result.Style = set.ToString();
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

    // Drawing styles for a rule. Each one keeps Revit values and editors untouched.
    // A look is made of independent layers: a background, the text color, one marker,
    // plus the "header only" scope and alternate rows. Stored as "FondDegrade+TexteCouleur+Pastille".
    internal sealed class StyleSet
    {
        internal const string BgNone = "", BgFill = "FondPlein", BgGradient = "FondDegrade";
        internal const string MarkNone = "", MarkStripe = "Lisere", MarkDot = "Pastille", MarkOutline = "Contour";
        internal string Background = BgFill, Marker = MarkNone;
        internal bool Text = true, HeaderOnly, Zebra;
        internal bool IsClassic => Background == BgFill && Text && Marker == MarkNone && !HeaderOnly && !Zebra;
        public override string ToString()
        {
            var parts = new List<string>();
            if (Background != BgNone) parts.Add(Background);
            if (Text) parts.Add("TexteCouleur");
            if (Marker != MarkNone) parts.Add(Marker);
            if (HeaderOnly) parts.Add("EnTete");
            if (Zebra) parts.Add("Zebre");
            return parts.Count == 0 ? "Aucun" : string.Join("+", parts);
        }
    }

    internal static class AppearanceStyles
    {
        internal const string ZebraOnly = "Zebre";
        // Values written by the first test build, one style at a time.
        private static readonly Dictionary<string, string> Legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fond"] = "FondPlein+TexteCouleur", ["Texte"] = "TexteCouleur", ["Degrade"] = "FondDegrade+TexteCouleur",
            ["Lisere"] = "Lisere", ["Pastille"] = "Pastille", ["EnTete"] = "FondPlein+TexteCouleur+EnTete",
        };
        internal static StyleSet Parse(string style)
        {
            var set = new StyleSet();
            if (string.IsNullOrWhiteSpace(style)) return set;
            if (Legacy.TryGetValue(style.Trim(), out var modern)) style = modern;
            set.Background = StyleSet.BgNone; set.Text = false;
            foreach (var token in style.Split('+').Select(t => t.Trim()))
            {
                if (token.Equals(StyleSet.BgFill, StringComparison.OrdinalIgnoreCase)) set.Background = StyleSet.BgFill;
                else if (token.Equals(StyleSet.BgGradient, StringComparison.OrdinalIgnoreCase)) set.Background = StyleSet.BgGradient;
                else if (token.Equals("TexteCouleur", StringComparison.OrdinalIgnoreCase)) set.Text = true;
                else if (token.Equals(StyleSet.MarkStripe, StringComparison.OrdinalIgnoreCase)) set.Marker = StyleSet.MarkStripe;
                else if (token.Equals(StyleSet.MarkDot, StringComparison.OrdinalIgnoreCase)) set.Marker = StyleSet.MarkDot;
                else if (token.Equals(StyleSet.MarkOutline, StringComparison.OrdinalIgnoreCase)) set.Marker = StyleSet.MarkOutline;
                else if (token.Equals("EnTete", StringComparison.OrdinalIgnoreCase)) set.HeaderOnly = true;
                else if (token.Equals("Zebre", StringComparison.OrdinalIgnoreCase)) set.Zebra = true;
            }
            return set;
        }
        // null keeps the file identical to older versions for the classic look.
        internal static string Store(StyleSet set) => set.IsClassic ? null : set.ToString();
        // Accent used by the stripe and the dot: the text color, else the background.
        internal static Color? Accent(PaletteColors colors) => AppearancePreferences.Parse(colors?.Text) ?? AppearancePreferences.Parse(colors?.Background);
        internal static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount), (byte)Math.Round(a.B + (b.B - a.B) * amount));
        // One chosen color gives a pale background in light mode and a readable pair in dark mode.
        internal static PaletteColors Tint(AppearanceRule rule, bool dark)
        {
            var main = AppearancePreferences.Parse(rule.Light?.Text) ?? AppearancePreferences.Parse(rule.Light?.Background)
                ?? AppearancePreferences.Parse(rule.Dark?.Text) ?? AppearancePreferences.Parse(rule.Dark?.Background);
            if (!main.HasValue) return null;
            var c = main.Value;
            return dark
                ? new PaletteColors { Text = Mix(c, Colors.White, .55).ToString(), Background = Mix(Color.FromRgb(59, 68, 83), c, .28).ToString() }
                : new PaletteColors { Text = c.ToString(), Background = Mix(Colors.White, c, .12).ToString() };
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

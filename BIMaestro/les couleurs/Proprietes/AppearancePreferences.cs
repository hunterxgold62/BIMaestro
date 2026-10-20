using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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

    public sealed class AppearanceProfile
    {
        public string Key { get; set; } = "all";
        public string Name { get; set; } = "Toutes les propriétés";
        public List<AppearanceRule> Rules { get; set; } = new List<AppearanceRule>();
        public AppearanceProfile Copy() => new AppearanceProfile { Key = Key, Name = Name, Rules = Rules.Select(r => r.Copy()).ToList() };
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
            var bg = Parse(colors.Background) ?? (dark ? Color.FromRgb(59, 68, 83) : Colors.White);
            var fg = Parse(colors.Text) ?? (dark ? Colors.White : Color.FromRgb(25, 25, 25));
            if (Contrast(fg, bg) >= 4.5) return fg;
            return Contrast(Colors.Black, bg) >= Contrast(Colors.White, bg) ? Colors.Black : Colors.White;
        }
    }

    internal static class AppearanceStore
    {
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
            var value = JsonConvert.DeserializeObject<AppearancePreferences>(File.ReadAllText(FilePath));
            if (value == null || value.Version != 1) throw new InvalidDataException("Format de préférences des propriétés non reconnu.");
            value.Profiles = value.Profiles ?? new List<AppearanceProfile>();
            foreach (var p in value.Profiles)
            {
                p.Rules = p.Rules ?? new List<AppearanceRule>();
                foreach (var r in p.Rules) { r.Light = r.Light ?? new PaletteColors(); r.Dark = r.Dark ?? new PaletteColors(); }
            }
            return value;
        }

        // Merge a single profile under a cross-process lock: another Revit's profiles survive.
        internal static AppearancePreferences Save(AppearanceProfile profile, bool enabled) => SaveMany(new[] { profile }, enabled);

        internal static AppearancePreferences SaveMany(IEnumerable<AppearanceProfile> profiles, bool enabled)
        {
            using (var mutex = new Mutex(false, "Local\\BIMaestro.PropertiesAppearance." + Environment.UserName))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Les préférences sont utilisées dans une autre fenêtre Revit. Réessayez.");
                    var latest = Load(); latest.Enabled = enabled;
                    foreach (var profile in profiles)
                    {
                        latest.Profiles.RemoveAll(p => p.Key == profile.Key);
                        if (profile.Rules.Any(r => !r.Light.IsEmpty || !r.Dark.IsEmpty)) latest.Profiles.Add(profile.Copy());
                    }
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

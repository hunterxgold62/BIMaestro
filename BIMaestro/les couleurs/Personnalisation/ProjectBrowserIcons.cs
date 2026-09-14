using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace Couleur
{
    public sealed class BrowserIconRule
    {
        public string Name { get; set; } = "";
        public string IconId { get; set; } = "lighting";
    }

    public sealed class BrowserIconAsset
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Data { get; set; }
        [JsonIgnore]
        public ImageSource Preview
        {
            get
            {
                try
                {
                    using (var stream = new MemoryStream(Convert.FromBase64String(Data)))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
                catch { return null; }
            }
        }
    }

    public sealed class BrowserIconSettings : INotifyPropertyChanged
    {
        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
        }
        public ObservableCollection<BrowserIconRule> Rules { get; set; } = new ObservableCollection<BrowserIconRule>();
        public ObservableCollection<BrowserIconAsset> CustomAssets { get; set; } = new ObservableCollection<BrowserIconAsset>();
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public static class ProjectBrowserIcons
    {
        private static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(RibbonColorPreferences.PreferenceFilePath), "iconesArborescence.json");
        private static BrowserIconSettings _current;
        private static readonly Lazy<List<BrowserIconAsset>> BuiltIns = new Lazy<List<BrowserIconAsset>>(() =>
        {
            var result = new List<BrowserIconAsset>();
            var names = new[] { "Éclairage", "Électricité", "Ventilation", "Plomberie",
                "Architecture", "Structure", "Coordination", "Chauffage", "Sécurité incendie", "Réseaux informatiques" };
            var ids = new[] { "lighting", "power", "ventilation", "plumbing",
                "architecture", "structure", "coordination", "heating", "fire", "data" };
            for (int i = 0; i < ids.Length; i++)
                using (var stream = typeof(ProjectBrowserIcons).Assembly.GetManifestResourceStream(
                    "BIMaestro.Resources.BrowserIcons." + ids[i] + ".png"))
                    if (stream != null)
                        result.Add(new BrowserIconAsset { Id = ids[i], Name = names[i], Data = EncodeImage(stream) });
            return result;
        });

        public static IEnumerable<BrowserIconAsset> Assets(BrowserIconSettings settings) =>
            BuiltIns.Value.Concat(settings.CustomAssets);

        public static BrowserIconSettings Defaults()
        {
            var settings = new BrowserIconSettings();
            foreach (var pair in new[] {
                new[] { "Éclairage", "lighting" }, new[] { "Lighting", "lighting" },
                new[] { "Électricité", "power" }, new[] { "Electrical", "power" }, new[] { "Power", "power" },
                new[] { "Ventilation", "ventilation" }, new[] { "HVAC", "ventilation" },
                new[] { "Plomberie", "plumbing" }, new[] { "Plumbing", "plumbing" } })
                settings.Rules.Add(new BrowserIconRule { Name = pair[0], IconId = pair[1] });
            return settings;
        }

        public static BrowserIconSettings Load()
        {
            try
            {
                var settings = File.Exists(FilePath)
                    ? JsonConvert.DeserializeObject<BrowserIconSettings>(File.ReadAllText(FilePath)) : Defaults();
                settings = settings ?? Defaults();
                settings.Rules = settings.Rules ?? new ObservableCollection<BrowserIconRule>();
                settings.CustomAssets = settings.CustomAssets ?? new ObservableCollection<BrowserIconAsset>();
                return settings;
            }
            catch { return Defaults(); }
        }

        public static void Save(BrowserIconSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(settings, Formatting.Indented));
            _current = Load();
        }

        // Decode and re-encode imports as small PNGs; no external paths or executable SVG enter the browser.
        public static string EncodeImage(Stream stream)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = new MemoryStream())
            {
                encoder.Save(output);
                return Convert.ToBase64String(output.ToArray());
            }
        }

        public static string CreateScript(string revitVersion)
        {
            if (!int.TryParse(revitVersion, out int version) || version < 2024 || version > 2027)
                return DisposeScript;
            var settings = _current ?? (_current = Load());
            var assets = Assets(settings).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Data);
            var rules = settings.Rules.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name) &&
                r.IconId != null && assets.ContainsKey(r.IconId)).Select(r => new {
                    name = r.Name.Trim(), source = "data:image/png;base64," + assets[r.IconId]
                });
            using (var stream = typeof(ProjectBrowserIcons).Assembly.GetManifestResourceStream(
                "BIMaestro.Resources.BrowserIcons.browser-icons.js"))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd().Replace("__ICON_CONFIGURATION__",
                    JsonConvert.SerializeObject(new { enabled = settings.Enabled, rules }));
        }

        public const string DisposeScript = "if(window.__bimaestroBrowserIcons){window.__bimaestroBrowserIcons.dispose();delete window.__bimaestroBrowserIcons;}";
    }
}

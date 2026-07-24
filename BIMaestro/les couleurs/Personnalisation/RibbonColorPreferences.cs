using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace Couleur
{
    public enum RibbonGradientDirection
    {
        Horizontal,
        Vertical,
        Diagonal
    }

    public sealed class RibbonPanelColorScheme
    {
        public RibbonPanelColorScheme(
            Color backgroundColor,
            Color backgroundEndColor,
            Color textColor,
            bool isGradient = false,
            RibbonGradientDirection gradientDirection = RibbonGradientDirection.Horizontal)
        {
            BackgroundColor = backgroundColor;
            BackgroundEndColor = backgroundEndColor;
            TextColor = textColor;
            IsGradient = isGradient;
            GradientDirection = gradientDirection;
        }

        public Color BackgroundColor { get; }

        public Color BackgroundEndColor { get; }

        public Color TextColor { get; }

        public bool IsGradient { get; }

        public RibbonGradientDirection GradientDirection { get; }

        public Brush CreateBackgroundBrush()
        {
            if (!IsGradient)
                return new SolidColorBrush(BackgroundColor);

            double angle;
            switch (GradientDirection)
            {
                case RibbonGradientDirection.Vertical:
                    angle = 90;
                    break;
                case RibbonGradientDirection.Diagonal:
                    angle = 45;
                    break;
                default:
                    angle = 0;
                    break;
            }

            return new LinearGradientBrush(
                BackgroundColor,
                BackgroundEndColor,
                angle);
        }
    }

    /// <summary>
    /// Charge et enregistre les couleurs de fond et de texte des panneaux BIMaestro.
    /// Les valeurs sont conservées dans Documents\RevitLogs\SauvegardePréférence.
    /// </summary>
    public static class RibbonColorPreferences
    {
        private static readonly object SyncRoot = new object();

        private static readonly Dictionary<string, RibbonPanelColorScheme> DefaultColors =
            new Dictionary<string, RibbonPanelColorScheme>(StringComparer.OrdinalIgnoreCase)
            {
                { "Outils de Visualisation", CreateDefault(Color.FromRgb(255, 230, 230)) },
                { "Modification", CreateDefault(Color.FromRgb(230, 255, 230)) },
                { "Outils IA", CreateDefault(Color.FromRgb(230, 230, 255)) },
                { "Couleur et information", CreateDefault(Color.FromRgb(230, 230, 230)) },
                { "Panneaux réservés au test", CreateDefault(Color.FromRgb(255, 255, 230)) },
                { "Analyse", CreateDefault(Color.FromRgb(230, 255, 255)) },
                { "Spécifique aux familles", CreateDefault(Color.FromRgb(255, 230, 255)) }
            };

        private static Dictionary<string, RibbonPanelColorScheme> _cachedColors;

        public static string PreferenceFilePath { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                "SauvegardePréférence",
                "couleursRuban.json");

        public static IReadOnlyList<string> PanelNames { get; } =
            DefaultColors.Keys.ToList().AsReadOnly();

        public static Dictionary<string, RibbonPanelColorScheme> Load()
        {
            lock (SyncRoot)
            {
                if (_cachedColors != null)
                    return Clone(_cachedColors);

                var colors = Clone(DefaultColors);

                try
                {
                    if (File.Exists(PreferenceFilePath))
                    {
                        string json = File.ReadAllText(PreferenceFilePath, Encoding.UTF8);
                        ReadSavedColors(JObject.Parse(json), colors);
                    }
                }
                catch
                {
                    // Une préférence absente ou endommagée ne doit jamais bloquer Revit.
                    colors = Clone(DefaultColors);
                }

                _cachedColors = colors;
                return Clone(_cachedColors);
            }
        }

        public static Dictionary<string, RibbonPanelColorScheme> GetDefaults()
        {
            return Clone(DefaultColors);
        }

        public static Dictionary<string, RibbonPanelColorScheme> CreateSchemes()
        {
            return Load();
        }

        public static bool IsKnownPanel(string panelName)
        {
            return !string.IsNullOrWhiteSpace(panelName) &&
                   DefaultColors.ContainsKey(panelName);
        }

        public static void Save(IDictionary<string, RibbonPanelColorScheme> colors)
        {
            if (colors == null)
                throw new ArgumentNullException(nameof(colors));

            lock (SyncRoot)
            {
                var normalized = Clone(DefaultColors);
                foreach (string panelName in PanelNames)
                {
                    if (colors.TryGetValue(panelName, out RibbonPanelColorScheme scheme) &&
                        scheme != null)
                    {
                        normalized[panelName] = Clone(scheme);
                    }
                }

                string directory = Path.GetDirectoryName(PreferenceFilePath);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new InvalidOperationException("Le dossier de sauvegarde est introuvable.");

                Directory.CreateDirectory(directory);

                var serializedColors = normalized.ToDictionary(
                    item => item.Key,
                    item => new SavedPanelColors
                    {
                        Fond = ToHex(item.Value.BackgroundColor),
                        Fin = ToHex(item.Value.BackgroundEndColor),
                        Texte = ToHex(item.Value.TextColor),
                        Degrade = item.Value.IsGradient,
                        Direction = item.Value.GradientDirection.ToString()
                    },
                    StringComparer.OrdinalIgnoreCase);

                string json = JsonConvert.SerializeObject(serializedColors, Formatting.Indented);
                File.WriteAllText(PreferenceFilePath, json, new UTF8Encoding(false));
                _cachedColors = normalized;
            }
        }

        private static RibbonPanelColorScheme CreateDefault(Color backgroundColor)
        {
            return new RibbonPanelColorScheme(
                backgroundColor,
                backgroundColor,
                Colors.Black);
        }

        private static void ReadSavedColors(
            JObject savedColors,
            IDictionary<string, RibbonPanelColorScheme> destination)
        {
            foreach (JProperty item in savedColors.Properties())
            {
                if (!DefaultColors.TryGetValue(item.Name, out RibbonPanelColorScheme defaults))
                    continue;

                // Compatibilité avec la première version :
                // "Nom du panneau": "#FFE6E6E6"
                if (item.Value.Type == JTokenType.String)
                {
                    if (TryParseColor(item.Value.Value<string>(), out Color legacyBackground))
                    {
                        destination[item.Name] =
                            new RibbonPanelColorScheme(
                                legacyBackground,
                                legacyBackground,
                                defaults.TextColor);
                    }

                    continue;
                }

                if (!(item.Value is JObject savedScheme))
                    continue;

                Color background = defaults.BackgroundColor;
                Color backgroundEnd = defaults.BackgroundEndColor;
                Color text = defaults.TextColor;
                bool isGradient = false;
                RibbonGradientDirection direction = RibbonGradientDirection.Horizontal;

                if (TryParseColor(savedScheme.Value<string>("Fond"), out Color savedBackground))
                    background = savedBackground;

                if (TryParseColor(savedScheme.Value<string>("Fin"), out Color savedBackgroundEnd))
                    backgroundEnd = savedBackgroundEnd;
                else
                    backgroundEnd = background;

                if (TryParseColor(savedScheme.Value<string>("Texte"), out Color savedText))
                    text = savedText;

                isGradient = savedScheme.Value<bool?>("Degrade") ?? false;

                string savedDirection = savedScheme.Value<string>("Direction");
                if (!string.IsNullOrWhiteSpace(savedDirection) &&
                    Enum.TryParse(savedDirection, true, out RibbonGradientDirection parsedDirection))
                {
                    direction = parsedDirection;
                }

                destination[item.Name] = new RibbonPanelColorScheme(
                    background,
                    backgroundEnd,
                    text,
                    isGradient,
                    direction);
            }
        }

        private static Dictionary<string, RibbonPanelColorScheme> Clone(
            IReadOnlyDictionary<string, RibbonPanelColorScheme> source)
        {
            return source.ToDictionary(
                item => item.Key,
                item => Clone(item.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        private static RibbonPanelColorScheme Clone(RibbonPanelColorScheme source)
        {
            return new RibbonPanelColorScheme(
                source.BackgroundColor,
                source.BackgroundEndColor,
                source.TextColor,
                source.IsGradient,
                source.GradientDirection);
        }

        private static bool TryParseColor(string value, out Color color)
        {
            color = Colors.Transparent;

            try
            {
                object converted = ColorConverter.ConvertFromString(value);
                if (converted is Color parsed)
                {
                    color = parsed;
                    return true;
                }
            }
            catch
            {
                // La valeur invalide est simplement ignorée.
            }

            return false;
        }

        private static string ToHex(Color color)
        {
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private sealed class SavedPanelColors
        {
            public string Fond { get; set; }

            public string Fin { get; set; }

            public string Texte { get; set; }

            public bool Degrade { get; set; }

            public string Direction { get; set; }
        }
    }
}

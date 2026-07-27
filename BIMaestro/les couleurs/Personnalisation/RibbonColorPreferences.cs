using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Couleur
{
    public enum RibbonGradientDirection
    {
        Horizontal,
        Vertical,
        Diagonal
    }

    public enum RibbonBackgroundPattern
    {
        Standard,
        FrenchFlag,
        FrenchFlagContinuous,
        ChristmasFestive,
        Confetti,
        PokeBallPixel
    }

    public sealed class RibbonPanelColorScheme
    {
        public RibbonPanelColorScheme(
            Color backgroundColor,
            Color backgroundEndColor,
            Color textColor,
            bool isGradient = false,
            RibbonGradientDirection gradientDirection = RibbonGradientDirection.Horizontal,
            RibbonBackgroundPattern backgroundPattern = RibbonBackgroundPattern.Standard,
            double patternStart = 0,
            double patternEnd = 1)
        {
            BackgroundColor = backgroundColor;
            BackgroundEndColor = backgroundEndColor;
            TextColor = textColor;
            IsGradient = isGradient;
            GradientDirection = gradientDirection;
            BackgroundPattern = backgroundPattern;
            PatternStart = patternStart;
            PatternEnd = patternEnd;
        }

        public Color BackgroundColor { get; }

        public Color BackgroundEndColor { get; }

        public Color TextColor { get; }

        public bool IsGradient { get; }

        public RibbonGradientDirection GradientDirection { get; }

        public RibbonBackgroundPattern BackgroundPattern { get; }

        public double PatternStart { get; }

        public double PatternEnd { get; }

        public Brush CreateBackgroundBrush()
        {
            if (BackgroundPattern == RibbonBackgroundPattern.FrenchFlag)
                return CreateFrenchFlagBrush(0, 1);

            if (BackgroundPattern == RibbonBackgroundPattern.FrenchFlagContinuous)
                return CreateFrenchFlagBrush(PatternStart, PatternEnd);

            if (BackgroundPattern == RibbonBackgroundPattern.ChristmasFestive)
                return CreateChristmasFestiveBrush();

            if (BackgroundPattern == RibbonBackgroundPattern.Confetti)
                return CreateConfettiBrush();

            if (BackgroundPattern == RibbonBackgroundPattern.PokeBallPixel)
                return CreatePokeBallSoftBrush();

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

        public Brush CreateBackgroundBrush(double actualStart, double actualEnd)
        {
            return BackgroundPattern == RibbonBackgroundPattern.FrenchFlagContinuous
                ? CreateFrenchFlagBrush(actualStart, actualEnd)
                : CreateBackgroundBrush();
        }

        private static Brush CreateFrenchFlagBrush(double segmentStart, double segmentEnd)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0.5),
                EndPoint = new System.Windows.Point(1, 0.5)
            };

            double start = Math.Max(0, Math.Min(1, segmentStart));
            double end = Math.Max(start + 0.0001, Math.Min(1, segmentEnd));
            double span = end - start;

            AddFlagBand(
                brush,
                Color.FromRgb(0, 85, 164),
                0,
                1.0 / 3.0,
                start,
                end,
                span);
            AddFlagBand(
                brush,
                Colors.White,
                1.0 / 3.0,
                2.0 / 3.0,
                start,
                end,
                span);
            AddFlagBand(
                brush,
                Color.FromRgb(239, 65, 53),
                2.0 / 3.0,
                1,
                start,
                end,
                span);

            if (brush.GradientStops.Count == 0)
                brush.GradientStops.Add(new GradientStop(Colors.White, 0));

            return brush;
        }

        private static void AddFlagBand(
            LinearGradientBrush brush,
            Color color,
            double bandStart,
            double bandEnd,
            double segmentStart,
            double segmentEnd,
            double segmentSpan)
        {
            double overlapStart = Math.Max(bandStart, segmentStart);
            double overlapEnd = Math.Min(bandEnd, segmentEnd);
            if (overlapEnd <= overlapStart)
                return;

            double localStart = (overlapStart - segmentStart) / segmentSpan;
            double localEnd = (overlapEnd - segmentStart) / segmentSpan;
            brush.GradientStops.Add(new GradientStop(color, localStart));
            brush.GradientStops.Add(new GradientStop(color, localEnd));
        }

        private static Brush CreateChristmasFestiveBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1)
            };

            brush.GradientStops.Add(new GradientStop(Color.FromRgb(5, 72, 46), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(18, 122, 70), 0.18));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(196, 145, 2), 0.30));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(176, 28, 28), 0.42));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(111, 16, 16), 0.58));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(218, 165, 32), 0.70));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(20, 107, 58), 0.82));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(5, 72, 46), 1));
            return brush;
        }

        private static Brush CreateConfettiBrush()
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(Color.FromRgb(255, 248, 225)),
                    null,
                    new RectangleGeometry(new System.Windows.Rect(0, 0, 72, 42))));

            AddConfettiCircle(drawing, Color.FromRgb(255, 45, 138), 9, 9, 4);
            AddConfettiCircle(drawing, Color.FromRgb(0, 200, 255), 34, 31, 3.5);
            AddConfettiCircle(drawing, Color.FromRgb(118, 255, 3), 59, 12, 4);
            AddConfettiCircle(drawing, Color.FromRgb(255, 214, 0), 67, 35, 3);
            AddConfettiCircle(drawing, Color.FromRgb(156, 39, 176), 23, 20, 3);

            AddConfettiStripe(drawing, Color.FromRgb(255, 111, 0), 44, 6, 12, 4, 25);
            AddConfettiStripe(drawing, Color.FromRgb(0, 188, 212), 5, 30, 11, 3, -20);
            AddConfettiStripe(drawing, Color.FromRgb(124, 77, 255), 53, 26, 12, 3, 40);

            return new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(0, 0, 72, 42),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new System.Windows.Rect(0, 0, 72, 42),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };
        }

        private static void AddConfettiCircle(
            DrawingGroup drawing,
            Color color,
            double x,
            double y,
            double radius)
        {
            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(color),
                    null,
                    new EllipseGeometry(
                        new System.Windows.Point(x, y),
                        radius,
                        radius)));
        }

        private static void AddConfettiStripe(
            DrawingGroup drawing,
            Color color,
            double x,
            double y,
            double width,
            double height,
            double angle)
        {
            var geometry = new RectangleGeometry(
                new System.Windows.Rect(x, y, width, height),
                1.5,
                1.5)
            {
                Transform = new RotateTransform(
                    angle,
                    x + width / 2,
                    y + height / 2)
            };

            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(color),
                    null,
                    geometry));
        }

        private Brush CreatePokeBallSoftBrush()
        {
            const double width = 72;
            const double height = 34;
            var drawing = new DrawingGroup();
            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(BackgroundColor),
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(0, 0, width, height))));

            var motif = new DrawingGroup
            {
                Opacity = 0.26
            };
            AddSmoothPokeBall(motif, 14, 9.5, 7.5);
            drawing.Children.Add(motif);

            return new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(0, 0, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new System.Windows.Rect(0, 0, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                Transform = RibbonPatternAnimation.GetPokeBallTransform()
            };
        }

        private static void AddSmoothPokeBall(
            DrawingGroup drawing,
            double centerX,
            double centerY,
            double radius)
        {
            var circle = new EllipseGeometry(
                new System.Windows.Point(centerX, centerY),
                radius,
                radius);
            var clippedBall = new DrawingGroup
            {
                ClipGeometry = circle
            };

            clippedBall.Children.Add(
                new GeometryDrawing(
                    Brushes.White,
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(
                            centerX - radius,
                            centerY - radius,
                            radius * 2,
                            radius * 2))));
            clippedBall.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(Color.FromRgb(238, 51, 57)),
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(
                            centerX - radius,
                            centerY - radius,
                            radius * 2,
                            radius))));
            clippedBall.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(Color.FromRgb(74, 45, 50)),
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(
                            centerX - radius,
                            centerY - 1.3,
                            radius * 2,
                            2.6))));
            drawing.Children.Add(clippedBall);

            var outlinePen = new Pen(
                new SolidColorBrush(Color.FromRgb(74, 45, 50)),
                1.15);
            drawing.Children.Add(
                new GeometryDrawing(
                    null,
                    outlinePen,
                    circle));

            drawing.Children.Add(
                new GeometryDrawing(
                    Brushes.White,
                    outlinePen,
                    new EllipseGeometry(
                        new System.Windows.Point(centerX, centerY),
                        2.25,
                        2.25)));
        }
    }

    internal static class RibbonPatternAnimation
    {
        private const double PokeBallTileWidth = 72;
        private static readonly TranslateTransform PokeBallTransform =
            new TranslateTransform();
        private static bool _isStarted;

        public static Transform GetPokeBallTransform()
        {
            EnsureStarted();
            return PokeBallTransform;
        }

        private static void EnsureStarted()
        {
            if (_isStarted)
                return;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = PokeBallTileWidth,
                Duration = TimeSpan.FromSeconds(6),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Une cadence plafonnée suffit pour un motif très lent et évite
            // de demander à Revit un rafraîchissement à 60 images/s.
            Timeline.SetDesiredFrameRate(animation, 15);
            PokeBallTransform.BeginAnimation(
                TranslateTransform.XProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
            _isStarted = true;
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
                { "Analyse", CreateDefault(Color.FromRgb(230, 255, 255)) },
                { "Spécifique aux familles", CreateDefault(Color.FromRgb(255, 230, 255)) },
                { "Couleur et information", CreateDefault(Color.FromRgb(230, 230, 230)) },
                { "Panneaux réservés au test", CreateDefault(Color.FromRgb(255, 255, 230)) }
            };

        private static Dictionary<string, RibbonPanelColorScheme> _cachedColors;

        public static string PreferenceFilePath { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                "SauvegardePréférence",
                "couleursRuban.json");

        internal static object PreferenceSyncRoot => SyncRoot;

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
                        JObject root = LoadPreferenceRoot();
                        JObject savedBimColors =
                            root["BIMaestro"] as JObject ?? root;
                        ReadSavedColors(savedBimColors, colors);
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
                        Direction = item.Value.GradientDirection.ToString(),
                        Motif = item.Value.BackgroundPattern.ToString(),
                        DebutMotif = item.Value.PatternStart,
                        FinMotif = item.Value.PatternEnd
                    },
                    StringComparer.OrdinalIgnoreCase);

                JObject root = LoadPreferenceRoot();
                root["BIMaestro"] = JObject.FromObject(serializedColors);
                SavePreferenceRoot(root);
                _cachedColors = normalized;
            }
        }

        internal static JObject LoadPreferenceRoot()
        {
            if (!File.Exists(PreferenceFilePath))
                return new JObject();

            string json = File.ReadAllText(PreferenceFilePath, Encoding.UTF8);
            JObject saved = JObject.Parse(json);
            if (saved["BIMaestro"] is JObject)
                return saved;

            JToken encorePlus = saved["EncorePlus"];
            if (encorePlus != null)
            {
                var legacyBimColors = new JObject(
                    saved.Properties()
                        .Where(property =>
                            !string.Equals(
                                property.Name,
                                "EncorePlus",
                                StringComparison.OrdinalIgnoreCase))
                        .Select(property =>
                            new JProperty(
                                property.Name,
                                property.Value.DeepClone())));

                return new JObject
                {
                    ["BIMaestro"] = legacyBimColors,
                    ["EncorePlus"] = encorePlus.DeepClone()
                };
            }

            return new JObject
            {
                ["BIMaestro"] = saved
            };
        }

        internal static void SavePreferenceRoot(JObject root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            string directory = Path.GetDirectoryName(PreferenceFilePath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException(
                    "Le dossier de sauvegarde est introuvable.");

            Directory.CreateDirectory(directory);
            string json = JsonConvert.SerializeObject(root, Formatting.Indented);
            File.WriteAllText(
                PreferenceFilePath,
                json,
                new UTF8Encoding(false));
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
                RibbonBackgroundPattern pattern = RibbonBackgroundPattern.Standard;
                double patternStart = 0;
                double patternEnd = 1;

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

                string savedPattern = savedScheme.Value<string>("Motif");
                if (!string.IsNullOrWhiteSpace(savedPattern) &&
                    Enum.TryParse(savedPattern, true, out RibbonBackgroundPattern parsedPattern))
                {
                    pattern = parsedPattern;
                }

                patternStart = savedScheme.Value<double?>("DebutMotif") ?? 0;
                patternEnd = savedScheme.Value<double?>("FinMotif") ?? 1;

                destination[item.Name] = new RibbonPanelColorScheme(
                    background,
                    backgroundEnd,
                    text,
                    isGradient,
                    direction,
                    pattern,
                    patternStart,
                    patternEnd);
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
                source.GradientDirection,
                source.BackgroundPattern,
                source.PatternStart,
                source.PatternEnd);
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

            public string Motif { get; set; }

            public double DebutMotif { get; set; }

            public double FinMotif { get; set; }
        }
    }
}

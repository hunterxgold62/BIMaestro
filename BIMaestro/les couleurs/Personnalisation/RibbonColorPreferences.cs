using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Couleur
{
    public sealed class ProjectBrowserCategoryColorRule :
        INotifyPropertyChanged
    {
        private string _categoryName = string.Empty;
        private Color _color = Color.FromRgb(209, 250, 229);

        public string CategoryName
        {
            get => _categoryName;
            set
            {
                string safeValue = value ?? string.Empty;
                if (_categoryName == safeValue) return;
                _categoryName = safeValue;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(CategoryName)));
            }
        }

        public Color Color
        {
            get => _color;
            set
            {
                if (_color == value) return;
                _color = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(Color)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ProjectBrowserCategoryColorRule Clone()
        {
            return new ProjectBrowserCategoryColorRule
            {
                CategoryName = CategoryName,
                Color = Color
            };
        }
    }

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
        PokeBallPixel,
        AnimatedPokemonPixelContinuous,
        AnimatedRainbowContinuous,
        AnimatedPastelBubblesContinuous,
        AnimatedPastelWavesContinuous,
        AnimatedPastelStarsContinuous,
        AnimatedSoftCloudsContinuous
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

        public bool IsContinuousAcrossRibbon =>
            BackgroundPattern == RibbonBackgroundPattern.FrenchFlagContinuous ||
            BackgroundPattern == RibbonBackgroundPattern.PokeBallPixel ||
            BackgroundPattern == RibbonBackgroundPattern.AnimatedPokemonPixelContinuous ||
            BackgroundPattern == RibbonBackgroundPattern.AnimatedRainbowContinuous ||
            BackgroundPattern == RibbonBackgroundPattern.AnimatedPastelBubblesContinuous ||
            BackgroundPattern == RibbonBackgroundPattern.AnimatedPastelWavesContinuous ||
            BackgroundPattern == RibbonBackgroundPattern.AnimatedPastelStarsContinuous ||
            BackgroundPattern == RibbonBackgroundPattern.AnimatedSoftCloudsContinuous;

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

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPokemonPixelContinuous)
            {
                return CreateAnimatedPokemonPixelBrush(0);
            }

            if (BackgroundPattern == RibbonBackgroundPattern.AnimatedRainbowContinuous)
                return CreateAnimatedRainbowBrush(0, 420);

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelBubblesContinuous)
            {
                return CreateAnimatedPastelBubblesBrush(0);
            }

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelWavesContinuous)
            {
                return CreateAnimatedPastelWavesBrush(0);
            }

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelStarsContinuous)
            {
                return CreateAnimatedPastelStarsBrush(0);
            }

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedSoftCloudsContinuous)
            {
                return CreateAnimatedSoftCloudsBrush(0);
            }

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

        public Brush CreateBackgroundBrush(
            double physicalStart,
            double physicalEnd,
            double physicalTotalWidth)
        {
            double totalWidth = Math.Max(1, physicalTotalWidth);

            if (BackgroundPattern == RibbonBackgroundPattern.FrenchFlagContinuous)
            {
                return CreateFrenchFlagBrush(
                    physicalStart / totalWidth,
                    physicalEnd / totalWidth);
            }

            if (BackgroundPattern == RibbonBackgroundPattern.AnimatedRainbowContinuous)
                return CreateAnimatedRainbowBrush(physicalStart, totalWidth);

            if (BackgroundPattern == RibbonBackgroundPattern.PokeBallPixel)
                return CreatePokeBallSoftBrush(physicalStart);

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPokemonPixelContinuous)
            {
                return CreateAnimatedPokemonPixelBrush(physicalStart);
            }

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelBubblesContinuous)
            {
                return CreateAnimatedPastelBubblesBrush(physicalStart);
            }

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelWavesContinuous)
            {
                return CreateAnimatedPastelWavesBrush(physicalStart);
            }

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelStarsContinuous)
            {
                return CreateAnimatedPastelStarsBrush(physicalStart);
            }

            if (BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedSoftCloudsContinuous)
            {
                return CreateAnimatedSoftCloudsBrush(physicalStart);
            }

            return CreateBackgroundBrush();
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

        private static Brush CreateAnimatedRainbowBrush(
            double segmentStart,
            double totalWidth)
        {
            double width = Math.Max(1, totalWidth);
            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new System.Windows.Point(-segmentStart, 0),
                EndPoint = new System.Windows.Point(width - segmentStart, 0),
                SpreadMethod = GradientSpreadMethod.Repeat,
                Transform = RibbonPatternAnimation.GetRainbowTransform(width)
            };

            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 107, 120), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 174, 105), 0.14));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 230, 109), 0.28));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(111, 227, 161), 0.42));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(101, 214, 232), 0.56));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(108, 140, 255), 0.70));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(183, 133, 244), 0.84));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 107, 120), 1));
            return brush;
        }

        private Brush CreateAnimatedPokemonPixelBrush(double segmentStart)
        {
            const double width = 720;
            const double height = 20;
            const double pixelSize = 0.75;

            var drawing = new DrawingGroup();
            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(BackgroundColor),
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(0, 0, width, height))));

            var sprites = new DrawingGroup
            {
                Opacity = 0.80
            };
            AddPixelSprite(
                sprites,
                CharmanderPixels,
                new Dictionary<char, Color>
                {
                    ['K'] = Color.FromRgb(0, 0, 0),
                    ['O'] = Color.FromRgb(255, 102, 34),
                    ['Y'] = Color.FromRgb(247, 194, 54),
                    ['R'] = Color.FromRgb(252, 55, 33)
                },
                52.125,
                3.25,
                pixelSize);
            AddPixelSprite(
                sprites,
                BulbasaurPixels,
                new Dictionary<char, Color>
                {
                    ['K'] = Color.FromRgb(19, 27, 30),
                    ['T'] = Color.FromRgb(30, 188, 178),
                    ['R'] = Color.FromRgb(227, 19, 23),
                    ['G'] = Color.FromRgb(19, 139, 20),
                    ['D'] = Color.FromRgb(1, 136, 104)
                },
                171.375,
                1.75,
                pixelSize);
            AddPixelSprite(
                sprites,
                PikachuPixels,
                new Dictionary<char, Color>
                {
                    ['K'] = Color.FromRgb(23, 24, 22),
                    ['Y'] = Color.FromRgb(253, 213, 30),
                    ['B'] = Color.FromRgb(154, 93, 51),
                    ['R'] = Color.FromRgb(227, 64, 63)
                },
                291.375,
                1,
                pixelSize);
            AddPixelSprite(
                sprites,
                SquirtlePixels,
                new Dictionary<char, Color>
                {
                    ['K'] = Color.FromRgb(21, 24, 24),
                    ['N'] = Color.FromRgb(173, 118, 50),
                    ['B'] = Color.FromRgb(141, 217, 237),
                    ['C'] = Color.FromRgb(249, 231, 190)
                },
                412.875,
                2.5,
                pixelSize);
            AddPixelSprite(
                sprites,
                SnorlaxPixels,
                new Dictionary<char, Color>
                {
                    ['K'] = Color.FromRgb(24, 23, 21),
                    ['T'] = Color.FromRgb(38, 79, 77),
                    ['S'] = Color.FromRgb(201, 174, 128),
                    ['C'] = Color.FromRgb(229, 212, 164)
                },
                533.25,
                3.25,
                pixelSize);
            AddPixelSprite(
                sprites,
                AshPixels,
                new Dictionary<char, Color>
                {
                    ['K'] = Color.FromRgb(18, 18, 18),
                    ['R'] = Color.FromRgb(234, 73, 58),
                    ['L'] = Color.FromRgb(61, 178, 76),
                    ['S'] = Color.FromRgb(250, 196, 141),
                    ['G'] = Color.FromRgb(23, 127, 44)
                },
                653.25,
                4,
                pixelSize);
            drawing.Children.Add(sprites);

            return new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(
                    -segmentStart,
                    0,
                    width,
                    height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new System.Windows.Rect(0, 0, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                Transform = RibbonPatternAnimation.GetPokemonTransform()
            };
        }

        private static void AddPixelSprite(
            DrawingGroup drawing,
            IReadOnlyList<string> pixels,
            IReadOnlyDictionary<char, Color> palette,
            double originX,
            double originY,
            double pixelSize)
        {
            for (int row = 0; row < pixels.Count; row++)
            {
                string line = pixels[row];
                for (int column = 0; column < line.Length; column++)
                {
                    char key = line[column];
                    if (!palette.TryGetValue(key, out Color color))
                        continue;

                    drawing.Children.Add(
                        new GeometryDrawing(
                            new SolidColorBrush(color),
                            null,
                            new RectangleGeometry(
                                new System.Windows.Rect(
                                    originX + column * pixelSize,
                                    originY + row * pixelSize,
                                    pixelSize,
                                    pixelSize))));
                }
            }
        }

        private static readonly string[] PikachuPixels =
        {
            ".KK...............KK...",
            ".KKKK............KKK...",
            ".KKYYK..........KYKK...",
            "..KYYYK........KYYK....",
            "..KYYYYKKKKKK.KYYYK..K.",
            "...KYYYYYYYYYKYYYYK.KYK",
            "....KYYYYYYYYYYYYK.KYYK",
            "....KYYYYYYYYYYYK.KYYYK",
            "...KYYK.YYYYYK.YKKYYYYK",
            "...KYYKKYYYYYKKYYKYYYYK",
            "...KYYYYYYKYYYYYYKYYYK.",
            ".KKKRRRYKYYYKYRRRKYYK..",
            "KYYKRRRYYKKKYYRRKYKY...",
            "KYYYKKYYYYYYYYYK.YYKK..",
            ".KYYYYYYYYYYYYYYKYYYKK.",
            "..KYYYYYYYYYYYYYYKYKK..",
            "...KKYYYYYYYYYYKYKBBK..",
            "...KYYYYYYYYYKYKYKBK...",
            "...KYYYYYYYYYKYKYKK....",
            "...KYYYYYYYYYYKYYK.....",
            "...KYYYYYYYYYYYYYK.....",
            "....KYYYYYYYYYYYK......",
            "...KYYYKKKKKKKYYYK.....",
            "...KKKK.......KKKK....."
        };

        private static readonly string[] CharmanderPixels =
        {
            "....KKKK.........K...",
            "...KOOOOK.......KRK..",
            "..KOOOOOOK......KRRK.",
            "..KOOOOOOK......KRRK.",
            ".KOOOOOOOOK....KRRRRK",
            "KOOOO.KOOOK....KRRYRK",
            "KOOOOKKOOOOK...KRYYRK",
            "KOOOOKKOOOOK....KYKK.",
            ".KOOOOOOOOOOK...KOK..",
            "..KKOOOOOOOOOK.KOOK..",
            "....KKKOOKOOOKKOOK...",
            ".....KYYKOOOOOKOOK...",
            ".....KYYYKKOOOKOK....",
            "....K.KYYYOOOOKK.....",
            ".....KKKYYOOOKK......",
            "........KKKOKK.......",
            ".........K.O.K.......",
            "..........KKKK......."
        };

        private static readonly string[] BulbasaurPixels =
        {
            "...............KKK.....",
            "..............KGGGK....",
            "...........KKKGGGGK....",
            "...KK....KGGGGGGGGGK...",
            "..KTTKKKKKKGGKKKGGGGK..",
            "..KTTTTTTTTKKTTKGGGGGK.",
            "..KTTTTDDDTTTTTKGGGGGGK",
            ".KTTTTDDDTTTTTTKGGGGGGK",
            ".KTTDTTDTTTTTTTTKGGGGGK",
            ".K.KTTTTTDTKKTTTKGGGGGK",
            ".K.RTTTTTTKR.KTTDKGGGGK",
            "KT.KTTTTTK.R.KTTTTKGGK.",
            "KTTRTTTTTKRR.KTTTTTKK..",
            "KKTTKTKTTTTTTTTTTTTTDK.",
            ".KKTTTTTTTTTKKTTDTTDTK.",
            "..KKKKKKKKKKTTTTTTDDDTK",
            "...KDDDTTTTTTKTTKTDDDTK",
            "..KDKDDDDDDDKTTTKTTDTTK",
            "..KTTDKKDDDKTTDTKKTTTTK",
            "..KTDTTKKKKKTDTTKK.KTK.",
            "..K.K.KK...K.K.K.KKKK..",
            "...KKKK.....KKK........"
        };

        private static readonly string[] SquirtlePixels =
        {
            "....KKKKKK.........",
            "...KBBBBBBK........",
            "..KBBBBBBBBK.......",
            ".KBBBBBBBBBBK......",
            ".K.BBBB.KBBBK......",
            ".KKBBBBKKBBBK......",
            ".KKBBBBKKBBBK......",
            "KBBBBBBBBBBBKK.....",
            ".KBKBBBBBKBKNNK....",
            "..KBKKKKKBKKKNNK...",
            ".KKKCCCCCKBBKNNK...",
            "KBBKKKKKKBBBKNKKKK.",
            "KBBKCCKCKBBBKNKBBBK",
            ".KKCCCKCCKKKNNKBBBK",
            "..KKCCKCCCCKNKBBKBK",
            "..KBKKKKCKKKKBBKBBK",
            "..KBKCCCKBBBKBBKKK.",
            "..KBBKKKKBBBKKKK...",
            ".KBBBK...KBBK......",
            ".KKKK....KKKK......"
        };

        private static readonly string[] SnorlaxPixels =
        {
            "....KK......KK....",
            "...KTTKKKKKKTTK...",
            "...KTTTTTTTTTTK...",
            "...KTTCCTTCCTTK...",
            "...KTCCCCCCCCTK...",
            "...KTCKKCCKKCTK...",
            "...KTCCCCCCCCTK...",
            "..KKKCCKKKKCCKKK..",
            ".KTTTKCCCCCCKTTTK.",
            "KTTTTTCCCCCCTTTTTK",
            "KTTTKKCCCCCCCKTTTK",
            ".KKKTCCCCCCCCCKKK.",
            ".KTKKCCCCCCCCKKTK.",
            ".KKCCKCCCCCCKCCKK.",
            ".KCCCCKCCCCKCCCCK.",
            ".KCSSCKTTTTKCSSCK.",
            "..KSSCKKKKKKCSSK..",
            "...KKK......KKK..."
        };

        private static readonly string[] AshPixels =
        {
            ".....KKKKKKKK.....",
            "....KRRRRRRRRK....",
            "...KRRR..L.RRRK...",
            "...KRR..G...RRK...",
            "...KRR.GGGG.RRK...",
            "...KRKKKKKKKKRK...",
            "..KKKRRRRRRRRKKK..",
            ".KKKSKKKKKKKKSKKK.",
            "KKSKS.KSKKSK.SKSKK",
            "KKSKS.KSKSSK.SKSKK",
            ".KKSS.KSSSSK.SSKK.",
            "..KKSSSSSSSSSSKK..",
            "...KSSSKKKSSSSK...",
            "....KSSSSSSSSK....",
            ".....KKSSSSKK.....",
            ".......KKKK......."
        };

        private Brush CreateAnimatedPastelBubblesBrush(double segmentStart)
        {
            const double width = 120;
            const double height = 48;
            var drawing = new DrawingGroup();
            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(BackgroundColor),
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(0, 0, width, height))));

            AddPastelBubble(
                drawing,
                Color.FromArgb(115, 92, 184, 255),
                18,
                14,
                8);
            AddPastelBubble(
                drawing,
                Color.FromArgb(100, 165, 121, 255),
                61,
                36,
                11);
            AddPastelBubble(
                drawing,
                Color.FromArgb(105, 83, 214, 198),
                102,
                11,
                6.5);
            AddPastelBubble(
                drawing,
                Color.FromArgb(90, 255, 151, 196),
                110,
                42,
                4.5);

            return new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(
                    -segmentStart,
                    0,
                    width,
                    height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new System.Windows.Rect(0, 0, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                Transform = RibbonPatternAnimation.GetBubblesTransform()
            };
        }

        private static void AddPastelBubble(
            DrawingGroup drawing,
            Color color,
            double centerX,
            double centerY,
            double radius)
        {
            var fill = new SolidColorBrush(
                Color.FromArgb(
                    (byte)Math.Min(255, color.A / 2 + 30),
                    color.R,
                    color.G,
                    color.B));
            var outline = new Pen(new SolidColorBrush(color), 1.35);
            drawing.Children.Add(
                new GeometryDrawing(
                    fill,
                    outline,
                    new EllipseGeometry(
                        new System.Windows.Point(centerX, centerY),
                        radius,
                        radius)));

            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                    null,
                    new EllipseGeometry(
                        new System.Windows.Point(
                            centerX - radius * 0.32,
                            centerY - radius * 0.32),
                        Math.Max(1.1, radius * 0.19),
                        Math.Max(1.1, radius * 0.19))));
        }

        private Brush CreateAnimatedPastelWavesBrush(double segmentStart)
        {
            const double width = 180;
            const double height = 48;
            var drawing = new DrawingGroup();
            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(BackgroundColor),
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(0, 0, width, height))));

            AddPastelWave(
                drawing,
                Color.FromArgb(105, 84, 190, 225),
                12,
                4.5);
            AddPastelWave(
                drawing,
                Color.FromArgb(95, 103, 133, 238),
                26,
                5.5);
            AddPastelWave(
                drawing,
                Color.FromArgb(90, 151, 113, 229),
                40,
                4);

            return new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(
                    -segmentStart,
                    0,
                    width,
                    height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new System.Windows.Rect(0, 0, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                Transform = RibbonPatternAnimation.GetWavesTransform()
            };
        }

        private static void AddPastelWave(
            DrawingGroup drawing,
            Color color,
            double centerY,
            double amplitude)
        {
            const double width = 180;
            var figure = new PathFigure
            {
                StartPoint = new System.Windows.Point(0, centerY),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(
                new BezierSegment(
                    new System.Windows.Point(30, centerY - amplitude),
                    new System.Windows.Point(60, centerY - amplitude),
                    new System.Windows.Point(90, centerY),
                    true));
            figure.Segments.Add(
                new BezierSegment(
                    new System.Windows.Point(120, centerY + amplitude),
                    new System.Windows.Point(150, centerY + amplitude),
                    new System.Windows.Point(width, centerY),
                    true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            drawing.Children.Add(
                new GeometryDrawing(
                    null,
                    new Pen(new SolidColorBrush(color), 2.4)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round
                    },
                    geometry));
        }

        private Brush CreateAnimatedPastelStarsBrush(double segmentStart)
        {
            const double width = 160;
            const double height = 20;
            var background = new LinearGradientBrush(
                BackgroundColor,
                BackgroundEndColor,
                0);
            var drawing = new DrawingGroup();
            drawing.Children.Add(
                new GeometryDrawing(
                    background,
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(0, 0, width, height))));

            AddPastelStar(drawing, 14, 6, 3.5, Color.FromRgb(255, 255, 255));
            AddPastelStar(drawing, 39, 15, 2.4, Color.FromRgb(255, 215, 128));
            AddPastelStar(drawing, 65, 7, 2.8, Color.FromRgb(255, 177, 211));
            AddPastelStar(drawing, 91, 13, 3.8, Color.FromRgb(255, 255, 255));
            AddPastelStar(drawing, 119, 5, 2.2, Color.FromRgb(255, 225, 145));
            AddPastelStar(drawing, 143, 14, 3, Color.FromRgb(204, 184, 255));

            return new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(
                    -segmentStart,
                    0,
                    width,
                    height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new System.Windows.Rect(0, 0, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                Transform = RibbonPatternAnimation.GetStarsTransform()
            };
        }

        private static void AddPastelStar(
            DrawingGroup drawing,
            double centerX,
            double centerY,
            double radius,
            Color color)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                for (int pointIndex = 0; pointIndex < 10; pointIndex++)
                {
                    double angle = -Math.PI / 2 + pointIndex * Math.PI / 5;
                    double pointRadius =
                        pointIndex % 2 == 0 ? radius : radius * 0.42;
                    var point = new System.Windows.Point(
                        centerX + Math.Cos(angle) * pointRadius,
                        centerY + Math.Sin(angle) * pointRadius);

                    if (pointIndex == 0)
                        context.BeginFigure(point, true, true);
                    else
                        context.LineTo(point, true, false);
                }
            }

            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(Color.FromArgb(205, color.R, color.G, color.B)),
                    new Pen(
                        new SolidColorBrush(
                            Color.FromArgb(115, color.R, color.G, color.B)),
                        0.65),
                    geometry));
        }

        private Brush CreateAnimatedSoftCloudsBrush(double segmentStart)
        {
            const double width = 220;
            const double height = 20;
            var background = new LinearGradientBrush(
                BackgroundColor,
                BackgroundEndColor,
                90);
            var drawing = new DrawingGroup();
            drawing.Children.Add(
                new GeometryDrawing(
                    background,
                    null,
                    new RectangleGeometry(
                        new System.Windows.Rect(0, 0, width, height))));

            AddSoftCloud(drawing, 28, 11, 0.72);
            AddSoftCloud(drawing, 104, 7, 0.52);
            AddSoftCloud(drawing, 180, 13, 0.66);

            return new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(
                    -segmentStart,
                    0,
                    width,
                    height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new System.Windows.Rect(0, 0, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                Transform = RibbonPatternAnimation.GetCloudsTransform()
            };
        }

        private static void AddSoftCloud(
            DrawingGroup drawing,
            double centerX,
            double centerY,
            double scale)
        {
            var cloud = new GeometryGroup();
            cloud.Children.Add(
                new EllipseGeometry(
                    new System.Windows.Point(centerX - 7 * scale, centerY),
                    7 * scale,
                    4.5 * scale));
            cloud.Children.Add(
                new EllipseGeometry(
                    new System.Windows.Point(centerX, centerY - 3 * scale),
                    8 * scale,
                    6.5 * scale));
            cloud.Children.Add(
                new EllipseGeometry(
                    new System.Windows.Point(centerX + 8 * scale, centerY),
                    7 * scale,
                    4.5 * scale));
            cloud.Children.Add(
                new RectangleGeometry(
                    new System.Windows.Rect(
                        centerX - 13 * scale,
                        centerY,
                        26 * scale,
                        4.5 * scale),
                    2.2 * scale,
                    2.2 * scale));

            drawing.Children.Add(
                new GeometryDrawing(
                    new SolidColorBrush(Color.FromArgb(195, 255, 255, 255)),
                    new Pen(
                        new SolidColorBrush(
                            Color.FromArgb(75, 113, 158, 195)),
                        0.7),
                    cloud));
        }

        private Brush CreatePokeBallSoftBrush(double segmentStart = 0)
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
                Viewport = new System.Windows.Rect(
                    -segmentStart,
                    0,
                    width,
                    height),
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
        private const double RainbowPixelsPerSecond = 40;
        private static readonly TranslateTransform PokeBallTransform =
            new TranslateTransform();
        private static readonly TranslateTransform PokemonTransform =
            new TranslateTransform();
        private static readonly TranslateTransform BubblesTransform =
            new TranslateTransform();
        private static readonly TranslateTransform WavesTransform =
            new TranslateTransform();
        private static readonly TranslateTransform StarsTransform =
            new TranslateTransform();
        private static readonly TranslateTransform CloudsTransform =
            new TranslateTransform();
        private static readonly Dictionary<int, TranslateTransform> RainbowTransforms =
            new Dictionary<int, TranslateTransform>();
        private static bool _isStarted;
        private static bool _isPokemonStarted;
        private static bool _areBubblesStarted;
        private static bool _areWavesStarted;
        private static bool _areStarsStarted;
        private static bool _areCloudsStarted;

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

        public static Transform GetRainbowTransform(double totalWidth)
        {
            int animationWidth = Math.Max(1, (int)Math.Round(totalWidth));
            if (RainbowTransforms.TryGetValue(
                    animationWidth,
                    out TranslateTransform existing))
            {
                return existing;
            }

            var transform = new TranslateTransform();
            var animation = new DoubleAnimation
            {
                From = 0,
                To = animationWidth,
                Duration = TimeSpan.FromSeconds(
                    Math.Max(8, animationWidth / RainbowPixelsPerSecond)),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Timeline.SetDesiredFrameRate(animation, 15);
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
            RainbowTransforms[animationWidth] = transform;
            return transform;
        }

        public static Transform GetPokemonTransform()
        {
            if (_isPokemonStarted)
                return PokemonTransform;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 720,
                Duration = TimeSpan.FromSeconds(45),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Timeline.SetDesiredFrameRate(animation, 15);
            PokemonTransform.BeginAnimation(
                TranslateTransform.XProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
            _isPokemonStarted = true;
            return PokemonTransform;
        }

        public static Transform GetBubblesTransform()
        {
            if (_areBubblesStarted)
                return BubblesTransform;

            var xAnimation = new DoubleAnimation
            {
                From = 0,
                To = 120,
                Duration = TimeSpan.FromSeconds(10),
                RepeatBehavior = RepeatBehavior.Forever
            };
            var yAnimation = new DoubleAnimation
            {
                From = 0,
                To = -48,
                Duration = TimeSpan.FromSeconds(10),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Timeline.SetDesiredFrameRate(xAnimation, 15);
            Timeline.SetDesiredFrameRate(yAnimation, 15);
            BubblesTransform.BeginAnimation(
                TranslateTransform.XProperty,
                xAnimation,
                HandoffBehavior.SnapshotAndReplace);
            BubblesTransform.BeginAnimation(
                TranslateTransform.YProperty,
                yAnimation,
                HandoffBehavior.SnapshotAndReplace);
            _areBubblesStarted = true;
            return BubblesTransform;
        }

        public static Transform GetWavesTransform()
        {
            if (_areWavesStarted)
                return WavesTransform;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 180,
                Duration = TimeSpan.FromSeconds(10),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Timeline.SetDesiredFrameRate(animation, 15);
            WavesTransform.BeginAnimation(
                TranslateTransform.XProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
            _areWavesStarted = true;
            return WavesTransform;
        }

        public static Transform GetStarsTransform()
        {
            if (_areStarsStarted)
                return StarsTransform;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 160,
                Duration = TimeSpan.FromSeconds(14),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Timeline.SetDesiredFrameRate(animation, 15);
            StarsTransform.BeginAnimation(
                TranslateTransform.XProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
            _areStarsStarted = true;
            return StarsTransform;
        }

        public static Transform GetCloudsTransform()
        {
            if (_areCloudsStarted)
                return CloudsTransform;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 220,
                Duration = TimeSpan.FromSeconds(24),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Timeline.SetDesiredFrameRate(animation, 15);
            CloudsTransform.BeginAnimation(
                TranslateTransform.XProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
            _areCloudsStarted = true;
            return CloudsTransform;
        }
    }

    /// <summary>
    /// Charge et enregistre les couleurs de fond et de texte des panneaux BIMaestro.
    /// Les valeurs sont conservées dans Documents\RevitLogs\SauvegardePréférence.
    /// </summary>
    public static class RibbonColorPreferences
    {
        private static readonly object SyncRoot = new object();
        private const string LegacyBetaPanelName = "Panneaux réservés au test";
        private const string BetaPanelName = "Beta";

        private static readonly Dictionary<string, RibbonPanelColorScheme> DefaultColors =
            new Dictionary<string, RibbonPanelColorScheme>(StringComparer.OrdinalIgnoreCase)
            {
                { "Outils de Visualisation", CreateDefault(Color.FromRgb(255, 230, 230)) },
                { "Modification", CreateDefault(Color.FromRgb(230, 255, 230)) },
                { "Outils IA", CreateDefault(Color.FromRgb(230, 230, 255)) },
                { "Analyse", CreateDefault(Color.FromRgb(230, 255, 255)) },
                { "Spécifique aux familles", CreateDefault(Color.FromRgb(255, 230, 255)) },
                { "Couleur et information", CreateDefault(Color.FromRgb(230, 230, 230)) },
                { BetaPanelName, CreateDefault(Color.FromRgb(255, 255, 230)) }
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
                string panelName = string.Equals(
                    item.Name,
                    LegacyBetaPanelName,
                    StringComparison.OrdinalIgnoreCase)
                    ? BetaPanelName
                    : item.Name;

                if (!DefaultColors.TryGetValue(panelName, out RibbonPanelColorScheme defaults))
                    continue;

                // Compatibilité avec la première version :
                // "Nom du panneau": "#FFE6E6E6"
                if (item.Value.Type == JTokenType.String)
                {
                    if (TryParseColor(item.Value.Value<string>(), out Color legacyBackground))
                    {
                        destination[panelName] =
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

                destination[panelName] = new RibbonPanelColorScheme(
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

    public sealed class ProjectBrowserColorSettings :
        System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isEnabled;
        private string _backgroundMode;
        private Color _backgroundColor;
        private Color _textColor;
        private Color _accentColor;
        private bool _isSheetViewSearchEnabled;
        private bool _isActiveViewParentHighlightEnabled;
        private Color _activeViewParentColor;
        private bool _isViewTypeColoringEnabled;
        private bool _isViewTypeParentColoringEnabled;
        private bool _isCategoryColoringEnabled;
        private string _viewColorTarget;
        private Color _planViewColor;
        private Color _sectionViewColor;
        private Color _threeDViewColor;
        private Color _elevationViewColor;
        private Color _scheduleViewColor;
        private Color _otherViewColor;

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }

        public string BackgroundMode
        {
            get => _backgroundMode;
            set => SetField(ref _backgroundMode, value);
        }

        public Color BackgroundColor
        {
            get => _backgroundColor;
            set => SetField(ref _backgroundColor, value);
        }

        public Color TextColor
        {
            get => _textColor;
            set => SetField(ref _textColor, value);
        }

        public Color AccentColor
        {
            get => _accentColor;
            set => SetField(ref _accentColor, value);
        }

        public bool IsSheetViewSearchEnabled
        {
            get => _isSheetViewSearchEnabled;
            set => SetField(ref _isSheetViewSearchEnabled, value);
        }

        public bool IsActiveViewParentHighlightEnabled
        {
            get => _isActiveViewParentHighlightEnabled;
            set => SetField(ref _isActiveViewParentHighlightEnabled, value);
        }

        public Color ActiveViewParentColor
        {
            get => _activeViewParentColor;
            set => SetField(ref _activeViewParentColor, value);
        }

        public bool IsViewTypeColoringEnabled
        {
            get => _isViewTypeColoringEnabled;
            set => SetField(ref _isViewTypeColoringEnabled, value);
        }

        public bool IsViewTypeParentColoringEnabled
        {
            get => _isViewTypeParentColoringEnabled;
            set => SetField(ref _isViewTypeParentColoringEnabled, value);
        }

        public bool IsCategoryColoringEnabled
        {
            get => _isCategoryColoringEnabled;
            set => SetField(ref _isCategoryColoringEnabled, value);
        }

        public string ViewColorTarget
        {
            get => _viewColorTarget;
            set => SetField(
                ref _viewColorTarget,
                string.Equals(
                    value,
                    "Texte",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Texte"
                    : "Fond");
        }

        public ObservableCollection<ProjectBrowserCategoryColorRule>
            CategoryColorRules { get; set; } =
                new ObservableCollection<ProjectBrowserCategoryColorRule>();

        public Color PlanViewColor
        {
            get => _planViewColor;
            set => SetField(ref _planViewColor, value);
        }

        public Color SectionViewColor
        {
            get => _sectionViewColor;
            set => SetField(ref _sectionViewColor, value);
        }

        public Color ThreeDViewColor
        {
            get => _threeDViewColor;
            set => SetField(ref _threeDViewColor, value);
        }

        public Color ElevationViewColor
        {
            get => _elevationViewColor;
            set => SetField(ref _elevationViewColor, value);
        }

        public Color ScheduleViewColor
        {
            get => _scheduleViewColor;
            set => SetField(ref _scheduleViewColor, value);
        }

        public Color OtherViewColor
        {
            get => _otherViewColor;
            set => SetField(ref _otherViewColor, value);
        }

        public event System.ComponentModel.PropertyChangedEventHandler
            PropertyChanged;

        private void SetField<T>(
            ref T field,
            T value,
            [System.Runtime.CompilerServices.CallerMemberName]
            string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;
            PropertyChanged?.Invoke(
                this,
                new System.ComponentModel.PropertyChangedEventArgs(
                    propertyName));
        }
    }

    public static class ProjectBrowserColorPreferences
    {
        private static ProjectBrowserColorSettings _cached;

        public static ProjectBrowserColorSettings GetDefaults()
        {
            return new ProjectBrowserColorSettings
            {
                IsEnabled = false,
                BackgroundMode = "Bulles pastel",
                BackgroundColor = Color.FromRgb(255, 249, 252),
                TextColor = Color.FromRgb(58, 61, 72),
                AccentColor = Color.FromRgb(219, 87, 142),
                IsSheetViewSearchEnabled = true,
                IsActiveViewParentHighlightEnabled = true,
                ActiveViewParentColor = Color.FromRgb(220, 54, 69),
                IsViewTypeColoringEnabled = false,
                IsViewTypeParentColoringEnabled = true,
                IsCategoryColoringEnabled = false,
                ViewColorTarget = "Fond",
                PlanViewColor = Color.FromRgb(219, 234, 254),
                SectionViewColor = Color.FromRgb(252, 221, 235),
                ThreeDViewColor = Color.FromRgb(254, 215, 215),
                ElevationViewColor = Color.FromRgb(254, 229, 195),
                ScheduleViewColor = Color.FromRgb(209, 250, 229),
                OtherViewColor = Color.FromRgb(237, 233, 254)
            };
        }

        public static ProjectBrowserColorSettings Load()
        {
            lock (RibbonColorPreferences.PreferenceSyncRoot)
            {
                if (_cached != null)
                    return Clone(_cached);

                ProjectBrowserColorSettings settings = GetDefaults();
                try
                {
                    JObject root =
                        RibbonColorPreferences.LoadPreferenceRoot();
                    if (root["Arborescence"] is JObject saved)
                    {
                        settings.IsEnabled =
                            saved.Value<bool?>("Activer") ??
                            settings.IsEnabled;
                        settings.BackgroundMode = NormalizeMode(
                            saved.Value<string>("ModeFond"));
                        settings.IsSheetViewSearchEnabled =
                            saved.Value<bool?>("RechercheVueFeuille") ??
                            settings.IsSheetViewSearchEnabled;
                        settings.IsActiveViewParentHighlightEnabled =
                            saved.Value<bool?>("RepererParentVueActive") ??
                            settings.IsActiveViewParentHighlightEnabled;
                        settings.IsViewTypeColoringEnabled =
                            saved.Value<bool?>("ColorerTypesVues") ??
                            settings.IsViewTypeColoringEnabled;
                        settings.IsViewTypeParentColoringEnabled =
                            saved.Value<bool?>("ColorerParentsVues") ??
                            settings.IsViewTypeParentColoringEnabled;
                        settings.IsCategoryColoringEnabled =
                            saved.Value<bool?>("ColorerCategories") ??
                            settings.IsCategoryColoringEnabled;
                        settings.ViewColorTarget =
                            saved.Value<string>("CibleCouleursVues");

                        if (TryParseColor(
                                saved.Value<string>("Fond"),
                                out Color background))
                        {
                            settings.BackgroundColor = background;
                        }

                        if (TryParseColor(
                                saved.Value<string>("Texte"),
                                out Color text))
                        {
                            settings.TextColor = text;
                        }

                        if (TryParseColor(
                                saved.Value<string>("Accent"),
                                out Color accent))
                        {
                            settings.AccentColor = accent;
                        }

                        if (TryParseColor(
                                saved.Value<string>(
                                    "CouleurParentVueActive"),
                                out Color activeViewParent))
                        {
                            settings.ActiveViewParentColor =
                                activeViewParent;
                        }

                        LoadColor(saved, "CouleurPlans", color =>
                            settings.PlanViewColor = color);
                        LoadColor(saved, "CouleurCoupes", color =>
                            settings.SectionViewColor = color);
                        LoadColor(saved, "Couleur3D", color =>
                            settings.ThreeDViewColor = color);
                        LoadColor(saved, "CouleurElevations", color =>
                            settings.ElevationViewColor = color);
                        LoadColor(saved, "CouleurNomenclatures", color =>
                            settings.ScheduleViewColor = color);
                        LoadColor(saved, "CouleurAutresVues", color =>
                            settings.OtherViewColor = color);

                        if (saved["ReglesCategories"] is JArray rules)
                        {
                            settings.CategoryColorRules.Clear();
                            foreach (JObject rule in rules.OfType<JObject>())
                            {
                                string name =
                                    rule.Value<string>("Nom")?.Trim();
                                if (string.IsNullOrWhiteSpace(name) ||
                                    !TryParseColor(
                                        rule.Value<string>("Couleur"),
                                        out Color ruleColor))
                                {
                                    continue;
                                }

                                settings.CategoryColorRules.Add(
                                    new ProjectBrowserCategoryColorRule
                                    {
                                        CategoryName = name,
                                        Color = ruleColor
                                    });
                            }
                        }
                    }
                }
                catch
                {
                    settings = GetDefaults();
                }

                _cached = settings;
                return Clone(_cached);
            }
        }

        public static void Save(ProjectBrowserColorSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            lock (RibbonColorPreferences.PreferenceSyncRoot)
            {
                ProjectBrowserColorSettings normalized = Clone(settings);
                normalized.BackgroundMode =
                    NormalizeMode(normalized.BackgroundMode);

                JObject root =
                    RibbonColorPreferences.LoadPreferenceRoot();
                root["Arborescence"] = new JObject
                {
                    ["Activer"] = normalized.IsEnabled,
                    ["ModeFond"] = normalized.BackgroundMode,
                    ["Fond"] = ToHex(normalized.BackgroundColor),
                    ["Texte"] = ToHex(normalized.TextColor),
                    ["Accent"] = ToHex(normalized.AccentColor),
                    ["RechercheVueFeuille"] =
                        normalized.IsSheetViewSearchEnabled,
                    ["RepererParentVueActive"] =
                        normalized.IsActiveViewParentHighlightEnabled,
                    ["CouleurParentVueActive"] =
                        ToHex(normalized.ActiveViewParentColor),
                    ["ColorerTypesVues"] =
                        normalized.IsViewTypeColoringEnabled,
                    ["ColorerParentsVues"] =
                        normalized.IsViewTypeParentColoringEnabled,
                    ["ColorerCategories"] =
                        normalized.IsCategoryColoringEnabled,
                    ["CibleCouleursVues"] =
                        normalized.ViewColorTarget,
                    ["CouleurPlans"] = ToHex(normalized.PlanViewColor),
                    ["CouleurCoupes"] = ToHex(normalized.SectionViewColor),
                    ["Couleur3D"] = ToHex(normalized.ThreeDViewColor),
                    ["CouleurElevations"] =
                        ToHex(normalized.ElevationViewColor),
                    ["CouleurNomenclatures"] =
                        ToHex(normalized.ScheduleViewColor),
                    ["CouleurAutresVues"] =
                        ToHex(normalized.OtherViewColor),
                    ["ReglesCategories"] = new JArray(
                        normalized.CategoryColorRules
                            .Where(rule =>
                                !string.IsNullOrWhiteSpace(
                                    rule.CategoryName))
                            .Select(rule => new JObject
                            {
                                ["Nom"] = rule.CategoryName.Trim(),
                                ["Couleur"] = ToHex(rule.Color)
                            }))
                };
                RibbonColorPreferences.SavePreferenceRoot(root);
                _cached = normalized;
            }
        }

        internal static ProjectBrowserColorSettings Clone(
            ProjectBrowserColorSettings source)
        {
            return new ProjectBrowserColorSettings
            {
                IsEnabled = source.IsEnabled,
                BackgroundMode = NormalizeMode(source.BackgroundMode),
                BackgroundColor = source.BackgroundColor,
                TextColor = source.TextColor,
                AccentColor = source.AccentColor,
                IsSheetViewSearchEnabled =
                    source.IsSheetViewSearchEnabled,
                IsActiveViewParentHighlightEnabled =
                    source.IsActiveViewParentHighlightEnabled,
                ActiveViewParentColor =
                    source.ActiveViewParentColor,
                IsViewTypeColoringEnabled =
                    source.IsViewTypeColoringEnabled,
                IsViewTypeParentColoringEnabled =
                    source.IsViewTypeParentColoringEnabled,
                IsCategoryColoringEnabled =
                    source.IsCategoryColoringEnabled,
                ViewColorTarget = source.ViewColorTarget,
                PlanViewColor = source.PlanViewColor,
                SectionViewColor = source.SectionViewColor,
                ThreeDViewColor = source.ThreeDViewColor,
                ElevationViewColor = source.ElevationViewColor,
                ScheduleViewColor = source.ScheduleViewColor,
                OtherViewColor = source.OtherViewColor,
                CategoryColorRules = new ObservableCollection<
                    ProjectBrowserCategoryColorRule>(
                    source.CategoryColorRules
                        .Select(rule => rule.Clone()))
            };
        }

        private static void LoadColor(
            JObject saved,
            string propertyName,
            Action<Color> apply)
        {
            if (TryParseColor(
                    saved.Value<string>(propertyName),
                    out Color color))
            {
                apply(color);
            }
        }

        private static string NormalizeMode(string value)
        {
            string[] supportedModes =
            {
                "Uni",
                "Bulles pastel",
                "Vagues pastel",
                "Rubans fluides",
                "Courbes topographiques",
                "Grille d'architecte",
                "Aurore boréale",
                "Constellation douce",
                "Lucioles pastel",
                "Dégradé pastel animé"
            };
            if (string.Equals(
                    value,
                    "Aurore pastel",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Dégradé pastel animé";
            }

            return supportedModes.FirstOrDefault(mode =>
                       string.Equals(
                           mode,
                           value,
                           StringComparison.OrdinalIgnoreCase)) ??
                   "Bulles pastel";
        }

        internal static bool TryParseColor(
            string value,
            out Color color)
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
                // Une couleur invalide restaure simplement la valeur par défaut.
            }

            return false;
        }

        internal static string ToHex(Color color)
        {
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }
    }

    public sealed class ProjectBrowserColorProfile
    {
        public string Name { get; set; }

        public ProjectBrowserColorSettings Settings { get; set; }

        public override string ToString() => Name ?? string.Empty;
    }

    public static class ProjectBrowserColorProfilePreferences
    {
        private const string RootPropertyName = "ProfilsArborescence";

        public static IReadOnlyList<ProjectBrowserColorProfile> Load()
        {
            lock (RibbonColorPreferences.PreferenceSyncRoot)
            {
                var profiles = new List<ProjectBrowserColorProfile>();
                try
                {
                    JObject root =
                        RibbonColorPreferences.LoadPreferenceRoot();
                    if (!(root[RootPropertyName] is JArray savedProfiles))
                        return profiles.AsReadOnly();

                    foreach (JObject saved in
                             savedProfiles.OfType<JObject>())
                    {
                        string name = saved.Value<string>("Nom")?.Trim();
                        if (string.IsNullOrWhiteSpace(name) ||
                            !(saved["Configuration"] is JObject configuration))
                        {
                            continue;
                        }

                        profiles.Add(new ProjectBrowserColorProfile
                        {
                            Name = name,
                            Settings = Deserialize(configuration)
                        });
                    }
                }
                catch
                {
                    profiles.Clear();
                }

                return profiles
                    .OrderBy(profile => profile.Name)
                    .ToList()
                    .AsReadOnly();
            }
        }

        public static void Save(
            string name,
            ProjectBrowserColorSettings settings)
        {
            string safeName = name?.Trim();
            if (string.IsNullOrWhiteSpace(safeName))
                throw new ArgumentException(
                    "Saisissez un nom pour le profil.",
                    nameof(name));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            lock (RibbonColorPreferences.PreferenceSyncRoot)
            {
                JObject root =
                    RibbonColorPreferences.LoadPreferenceRoot();
                JArray profiles =
                    root[RootPropertyName] as JArray ?? new JArray();
                JObject existing = profiles
                    .OfType<JObject>()
                    .FirstOrDefault(item => string.Equals(
                        item.Value<string>("Nom"),
                        safeName,
                        StringComparison.OrdinalIgnoreCase));
                existing?.Remove();
                profiles.Add(new JObject
                {
                    ["Nom"] = safeName,
                    ["Configuration"] = Serialize(settings)
                });
                root[RootPropertyName] = profiles;
                RibbonColorPreferences.SavePreferenceRoot(root);
            }
        }

        public static void Delete(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (RibbonColorPreferences.PreferenceSyncRoot)
            {
                JObject root =
                    RibbonColorPreferences.LoadPreferenceRoot();
                if (!(root[RootPropertyName] is JArray profiles)) return;
                foreach (JObject profile in profiles
                             .OfType<JObject>()
                             .Where(item => string.Equals(
                                 item.Value<string>("Nom"),
                                 name,
                                 StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    profile.Remove();
                }
                RibbonColorPreferences.SavePreferenceRoot(root);
            }
        }

        private static JObject Serialize(
            ProjectBrowserColorSettings source)
        {
            ProjectBrowserColorSettings settings =
                ProjectBrowserColorPreferences.Clone(source);
            return new JObject
            {
                ["Activer"] = settings.IsEnabled,
                ["ModeFond"] = settings.BackgroundMode,
                ["Fond"] = ProjectBrowserColorPreferences.ToHex(
                    settings.BackgroundColor),
                ["Texte"] = ProjectBrowserColorPreferences.ToHex(
                    settings.TextColor),
                ["Accent"] = ProjectBrowserColorPreferences.ToHex(
                    settings.AccentColor),
                ["ColorerTypesVues"] =
                    settings.IsViewTypeColoringEnabled,
                ["ColorerParentsVues"] =
                    settings.IsViewTypeParentColoringEnabled,
                ["ColorerCategories"] =
                    settings.IsCategoryColoringEnabled,
                ["CibleCouleursVues"] = settings.ViewColorTarget,
                ["CouleurPlans"] = ProjectBrowserColorPreferences.ToHex(
                    settings.PlanViewColor),
                ["CouleurCoupes"] = ProjectBrowserColorPreferences.ToHex(
                    settings.SectionViewColor),
                ["Couleur3D"] = ProjectBrowserColorPreferences.ToHex(
                    settings.ThreeDViewColor),
                ["CouleurElevations"] =
                    ProjectBrowserColorPreferences.ToHex(
                        settings.ElevationViewColor),
                ["CouleurNomenclatures"] =
                    ProjectBrowserColorPreferences.ToHex(
                        settings.ScheduleViewColor),
                ["CouleurAutresVues"] =
                    ProjectBrowserColorPreferences.ToHex(
                        settings.OtherViewColor),
                ["ReglesCategories"] = new JArray(
                    settings.CategoryColorRules
                        .Where(rule =>
                            !string.IsNullOrWhiteSpace(rule.CategoryName))
                        .Select(rule => new JObject
                        {
                            ["Nom"] = rule.CategoryName.Trim(),
                            ["Couleur"] =
                                ProjectBrowserColorPreferences.ToHex(
                                    rule.Color)
                        }))
            };
        }

        private static ProjectBrowserColorSettings Deserialize(
            JObject saved)
        {
            ProjectBrowserColorSettings settings =
                ProjectBrowserColorPreferences.GetDefaults();
            settings.IsEnabled =
                saved.Value<bool?>("Activer") ?? settings.IsEnabled;
            settings.BackgroundMode =
                saved.Value<string>("ModeFond") ?? settings.BackgroundMode;
            settings.IsViewTypeColoringEnabled =
                saved.Value<bool?>("ColorerTypesVues") ?? false;
            settings.IsViewTypeParentColoringEnabled =
                saved.Value<bool?>("ColorerParentsVues") ?? true;
            settings.IsCategoryColoringEnabled =
                saved.Value<bool?>("ColorerCategories") ?? false;
            settings.ViewColorTarget =
                saved.Value<string>("CibleCouleursVues");

            LoadColor(saved, "Fond", color =>
                settings.BackgroundColor = color);
            LoadColor(saved, "Texte", color =>
                settings.TextColor = color);
            LoadColor(saved, "Accent", color =>
                settings.AccentColor = color);
            LoadColor(saved, "CouleurPlans", color =>
                settings.PlanViewColor = color);
            LoadColor(saved, "CouleurCoupes", color =>
                settings.SectionViewColor = color);
            LoadColor(saved, "Couleur3D", color =>
                settings.ThreeDViewColor = color);
            LoadColor(saved, "CouleurElevations", color =>
                settings.ElevationViewColor = color);
            LoadColor(saved, "CouleurNomenclatures", color =>
                settings.ScheduleViewColor = color);
            LoadColor(saved, "CouleurAutresVues", color =>
                settings.OtherViewColor = color);

            settings.CategoryColorRules.Clear();
            if (saved["ReglesCategories"] is JArray rules)
            {
                foreach (JObject rule in rules.OfType<JObject>())
                {
                    string ruleName = rule.Value<string>("Nom")?.Trim();
                    if (string.IsNullOrWhiteSpace(ruleName) ||
                        !ProjectBrowserColorPreferences.TryParseColor(
                            rule.Value<string>("Couleur"),
                            out Color ruleColor))
                    {
                        continue;
                    }
                    settings.CategoryColorRules.Add(
                        new ProjectBrowserCategoryColorRule
                        {
                            CategoryName = ruleName,
                            Color = ruleColor
                        });
                }
            }
            return settings;
        }

        private static void LoadColor(
            JObject saved,
            string propertyName,
            Action<Color> apply)
        {
            if (ProjectBrowserColorPreferences.TryParseColor(
                    saved.Value<string>(propertyName),
                    out Color color))
            {
                apply(color);
            }
        }
    }
}

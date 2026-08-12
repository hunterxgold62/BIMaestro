using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Couleur
{
    public static class RibbonColorPresetCatalog
    {
        public static IReadOnlyList<string> StandardPresetNames { get; } =
            new[]
            {
                "Pastel",
                "Sombre",
                "Contraste élevé",
                "Arc-en-ciel",
                "Océan",
                "Coucher de soleil",
                "Confettis",
                "Noël",
                "France",
                "France continue"
            };

        public static IReadOnlyList<string> AnimatedPresetNames { get; } =
            new[]
            {
                "Pastel animé",
                "Pokéball douce",
                "Pokémon pixel",
                "Arc-en-ciel animé",
                "Bulles pastel",
                "Vagues pastel",
                "Étoiles pastel",
                "Nuages doux"
            };

        public static IReadOnlyList<string> PresetNames { get; } =
            StandardPresetNames
                .Concat(AnimatedPresetNames)
                .ToList()
                .AsReadOnly();

        public static Dictionary<string, RibbonPanelColorScheme> Create(string presetName)
        {
            switch (presetName)
            {
                case "Pastel":
                    return RibbonColorPreferences.GetDefaults();
                case "Pastel animé":
                    return CreateAnimatedPastelMix();
                case "Sombre":
                    return CreateSombre();
                case "Contraste élevé":
                    return CreateHighContrast();
                case "Arc-en-ciel":
                    return CreateRainbow();
                case "Océan":
                    return CreateOcean();
                case "Coucher de soleil":
                    return CreateSunset();
                case "Confettis":
                    return CreateConfetti();
                case "Pokéball douce":
                    return CreatePokeBallSoft();
                case "Pokémon pixel":
                    return CreateAnimatedPokemonPixel();
                case "Arc-en-ciel animé":
                    return CreateAnimatedRainbow();
                case "Bulles pastel":
                    return CreateAnimatedPastelBubbles();
                case "Vagues pastel":
                    return CreateAnimatedPastelWaves();
                case "Étoiles pastel":
                    return CreateAnimatedPastelStars();
                case "Nuages doux":
                    return CreateAnimatedSoftClouds();
                case "Noël":
                    return CreateChristmas();
                case "France":
                    return CreateFrance();
                case "France continue":
                    return CreateContinuousFrance();
                default:
                    return RibbonColorPreferences.GetDefaults();
            }
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateSombre()
        {
            Color[] starts =
            {
                FromHex("#111827"), FromHex("#172554"), FromHex("#312E81"),
                FromHex("#052E16"), FromHex("#3F3F46"), FromHex("#083344"),
                FromHex("#3B0764")
            };
            Color[] ends =
            {
                FromHex("#374151"), FromHex("#1E3A8A"), FromHex("#581C87"),
                FromHex("#166534"), FromHex("#18181B"), FromHex("#155E75"),
                FromHex("#6B21A8")
            };

            return Build((_, index) =>
                Gradient(starts[index], ends[index], Colors.White));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateHighContrast()
        {
            return Build((_, index) =>
                index % 2 == 0
                    ? Solid(Colors.Black, Colors.White)
                    : Solid(FromHex("#FFFF00"), Colors.Black));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateRainbow()
        {
            Color[] starts =
            {
                FromHex("#FF8A80"), FromHex("#FFD180"), FromHex("#FFFF8D"),
                FromHex("#B9F6CA"), FromHex("#80D8FF"), FromHex("#8C9EFF"),
                FromHex("#EA80FC")
            };
            Color[] ends =
            {
                FromHex("#FF5252"), FromHex("#FFAB40"), FromHex("#FFD740"),
                FromHex("#69F0AE"), FromHex("#40C4FF"), FromHex("#536DFE"),
                FromHex("#E040FB")
            };

            return Build((_, index) =>
                Gradient(starts[index], ends[index], Colors.Black));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateOcean()
        {
            Color[] starts =
            {
                FromHex("#082F49"), FromHex("#0C4A6E"), FromHex("#075985"),
                FromHex("#155E75"), FromHex("#0F766E"), FromHex("#115E59"),
                FromHex("#134E4A")
            };
            Color[] ends =
            {
                FromHex("#0369A1"), FromHex("#0284C7"), FromHex("#0891B2"),
                FromHex("#0E7490"), FromHex("#0D9488"), FromHex("#14B8A6"),
                FromHex("#0F766E")
            };

            return Build((_, index) =>
                Gradient(starts[index], ends[index], Colors.White));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateSunset()
        {
            Color[] starts =
            {
                FromHex("#FFE4E6"), FromHex("#FCE7F3"), FromHex("#FAE8FF"),
                FromHex("#FEF3C7"), FromHex("#FFEDD5"), FromHex("#FFE4E6"),
                FromHex("#FDE68A")
            };
            Color[] ends =
            {
                FromHex("#FDBA74"), FromHex("#F9A8D4"), FromHex("#D8B4FE"),
                FromHex("#FDE68A"), FromHex("#FDBA74"), FromHex("#FDA4AF"),
                FromHex("#FBCFE8")
            };

            return Build((_, index) =>
                Gradient(starts[index], ends[index], FromHex("#4A2533")));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateConfetti()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#FFF8E1"),
                    FromHex("#FF2D8A"),
                    FromHex("#14213D"),
                    false,
                    RibbonGradientDirection.Diagonal,
                    RibbonBackgroundPattern.Confetti));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreatePokeBallSoft()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#FFF7F8"),
                    FromHex("#EE3339"),
                    FromHex("#4A2D32"),
                    false,
                    RibbonGradientDirection.Horizontal,
                    RibbonBackgroundPattern.PokeBallPixel));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateAnimatedPokemonPixel()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#FFF2AD"),
                    FromHex("#B9DFFF"),
                    FromHex("#26384A"),
                    false,
                    RibbonGradientDirection.Horizontal,
                    RibbonBackgroundPattern.AnimatedPokemonPixelContinuous));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateAnimatedPastelMix()
        {
            Dictionary<string, RibbonPanelColorScheme> result =
                RibbonColorPreferences.GetDefaults();

            SetPattern(
                result,
                "Outils de Visualisation",
                RibbonBackgroundPattern.AnimatedPastelStarsContinuous);
            SetPattern(
                result,
                "Modification",
                RibbonBackgroundPattern.AnimatedPastelWavesContinuous);
            SetPattern(
                result,
                "Outils IA",
                RibbonBackgroundPattern.AnimatedPastelBubblesContinuous);
            SetPattern(
                result,
                "Analyse",
                RibbonBackgroundPattern.AnimatedSoftCloudsContinuous);
            SetPattern(
                result,
                "Spécifique aux familles",
                RibbonBackgroundPattern.PokeBallPixel);
            SetPattern(
                result,
                "Couleur et information",
                RibbonBackgroundPattern.AnimatedPokemonPixelContinuous);
            SetPattern(
                result,
                "Beta",
                RibbonBackgroundPattern.AnimatedRainbowContinuous);

            return result;
        }

        private static void SetPattern(
            IDictionary<string, RibbonPanelColorScheme> schemes,
            string panelName,
            RibbonBackgroundPattern pattern)
        {
            if (!schemes.TryGetValue(panelName, out RibbonPanelColorScheme scheme))
                return;

            schemes[panelName] = new RibbonPanelColorScheme(
                scheme.BackgroundColor,
                scheme.BackgroundEndColor,
                scheme.TextColor,
                false,
                RibbonGradientDirection.Horizontal,
                pattern);
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateAnimatedRainbow()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#FF7B89"),
                    FromHex("#8E7CFF"),
                    FromHex("#273143"),
                    false,
                    RibbonGradientDirection.Horizontal,
                    RibbonBackgroundPattern.AnimatedRainbowContinuous));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateAnimatedPastelBubbles()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#F2F8FF"),
                    FromHex("#A9D9FF"),
                    FromHex("#29445F"),
                    false,
                    RibbonGradientDirection.Diagonal,
                    RibbonBackgroundPattern.AnimatedPastelBubblesContinuous));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateAnimatedPastelWaves()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#F4FBFF"),
                    FromHex("#93DDE8"),
                    FromHex("#24445A"),
                    false,
                    RibbonGradientDirection.Horizontal,
                    RibbonBackgroundPattern.AnimatedPastelWavesContinuous));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateAnimatedPastelStars()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#FFF4FB"),
                    FromHex("#E9E4FF"),
                    FromHex("#493B62"),
                    false,
                    RibbonGradientDirection.Horizontal,
                    RibbonBackgroundPattern.AnimatedPastelStarsContinuous));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateAnimatedSoftClouds()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#EAF8FF"),
                    FromHex("#BBDFF7"),
                    FromHex("#294B66"),
                    false,
                    RibbonGradientDirection.Vertical,
                    RibbonBackgroundPattern.AnimatedSoftCloudsContinuous));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateChristmas()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#05482E"),
                    FromHex("#B01C1C"),
                    Colors.White,
                    false,
                    RibbonGradientDirection.Diagonal,
                    RibbonBackgroundPattern.ChristmasFestive));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateFrance()
        {
            return Build((_, __) =>
                new RibbonPanelColorScheme(
                    FromHex("#0055A4"),
                    FromHex("#EF4135"),
                    Colors.Black,
                    false,
                    RibbonGradientDirection.Horizontal,
                    RibbonBackgroundPattern.FrenchFlag));
        }

        private static Dictionary<string, RibbonPanelColorScheme> CreateContinuousFrance()
        {
            var result = CreateFrance();
            string[] ribbonOrder =
            {
                "Outils de Visualisation",
                "Modification",
                "Outils IA",
                "Analyse",
                "Spécifique aux familles",
                "Couleur et information"
            };

            for (int index = 0; index < ribbonOrder.Length; index++)
            {
                double start = (double)index / ribbonOrder.Length;
                double end = (double)(index + 1) / ribbonOrder.Length;
                double midpoint = (start + end) / 2;
                Color text = midpoint < 1.0 / 3.0 || midpoint > 2.0 / 3.0
                    ? Colors.White
                    : Colors.Black;

                result[ribbonOrder[index]] = new RibbonPanelColorScheme(
                    FromHex("#0055A4"),
                    FromHex("#EF4135"),
                    text,
                    false,
                    RibbonGradientDirection.Horizontal,
                    RibbonBackgroundPattern.FrenchFlagContinuous,
                    start,
                    end);
            }

            return result;
        }

        private static Dictionary<string, RibbonPanelColorScheme> Build(
            Func<string, int, RibbonPanelColorScheme> factory)
        {
            return RibbonColorPreferences.PanelNames
                .Select((panelName, index) => new
                {
                    PanelName = panelName,
                    Scheme = factory(panelName, index)
                })
                .ToDictionary(
                    item => item.PanelName,
                    item => item.Scheme,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static RibbonPanelColorScheme Solid(Color background, Color text)
        {
            return new RibbonPanelColorScheme(background, background, text);
        }

        private static RibbonPanelColorScheme Gradient(
            Color start,
            Color end,
            Color text)
        {
            return new RibbonPanelColorScheme(
                start,
                end,
                text,
                true,
                RibbonGradientDirection.Horizontal);
        }

        private static Color FromHex(string value)
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
    }
}

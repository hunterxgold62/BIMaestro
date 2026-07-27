using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Couleur
{
    public partial class ColorPreferencesWindow : Window
    {
        private readonly System.IntPtr _mainWindowHandle;
        private string _selectedPresetName;

        public ColorPreferencesWindow(System.IntPtr mainWindowHandle)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            _mainWindowHandle = mainWindowHandle;
            PanelColors = CreateItems(RibbonColorPreferences.Load());
            PreferenceFilePath =
                $"Sauvegarde : {RibbonColorPreferences.PreferenceFilePath}";
            _selectedPresetName = RibbonColorPresetCatalog.PresetNames.FirstOrDefault();
            DataContext = this;
        }

        public ObservableCollection<PanelColorItem> PanelColors { get; }

        public string PreferenceFilePath { get; }

        public IReadOnlyList<string> PresetNames =>
            RibbonColorPresetCatalog.PresetNames;

        public string SelectedPresetName
        {
            get => _selectedPresetName;
            set => _selectedPresetName = value;
        }

        private void ApplyPresetButton_Click(object sender, RoutedEventArgs e)
        {
            Dictionary<string, RibbonPanelColorScheme> preset =
                RibbonColorPresetCatalog.Create(SelectedPresetName);

            foreach (PanelColorItem item in PanelColors)
            {
                if (preset.TryGetValue(item.PanelName, out RibbonPanelColorScheme scheme))
                    item.ApplyScheme(scheme);
            }
        }

        private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            Dictionary<string, RibbonPanelColorScheme> defaults =
                RibbonColorPreferences.GetDefaults();

            foreach (PanelColorItem item in PanelColors)
            {
                if (defaults.TryGetValue(item.PanelName, out RibbonPanelColorScheme scheme))
                    item.ApplyScheme(scheme);
            }
        }

        private void OpenRevitColorsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                SaveCurrentColors();
                RevitColorPreferencesWindow.ShowModeless(_mainWindowHandle);
                DialogResult = true;
            }
            catch (System.Exception ex)
            {
                ShowSaveError(ex);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveCurrentColors();
                DialogResult = true;
            }
            catch (System.Exception ex)
            {
                ShowSaveError(ex);
            }
        }

        private void SaveCurrentColors()
        {
            var colors = PanelColors.ToDictionary(
                item => item.PanelName,
                item => item.CreateScheme());
            RibbonColorPreferences.Save(colors);
        }

        private static void ShowSaveError(System.Exception ex)
        {
            MessageBox.Show(
                $"Impossible d’enregistrer les couleurs.\n\n{ex.Message}",
                "BIMaestro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static ObservableCollection<PanelColorItem> CreateItems(
            IReadOnlyDictionary<string, RibbonPanelColorScheme> colors)
        {
            return new ObservableCollection<PanelColorItem>(
                RibbonColorPreferences.PanelNames.Select(panelName =>
                {
                    RibbonPanelColorScheme scheme =
                        colors.TryGetValue(panelName, out RibbonPanelColorScheme savedScheme)
                            ? savedScheme
                            : new RibbonPanelColorScheme(
                                Colors.Transparent,
                                Colors.Transparent,
                                Colors.Black);

                    return new PanelColorItem(panelName, scheme);
                }));
        }
    }

    public sealed class PanelColorItem : INotifyPropertyChanged
    {
        private static readonly IReadOnlyList<string> AvailableBackgroundModes =
            new[]
            {
                "Uni", "Horizontal", "Vertical", "Diagonal",
                "France", "France continue", "Noël festif", "Confettis",
                "Pokéball douce"
            };

        private Color? _backgroundColor;
        private Color? _backgroundEndColor;
        private Color? _textColor;
        private string _backgroundMode;
        private double _patternStart;
        private double _patternEnd;

        public PanelColorItem(string panelName, RibbonPanelColorScheme scheme)
        {
            PanelName = panelName;
            ApplyScheme(scheme, false);
        }

        public string PanelName { get; }

        public IReadOnlyList<string> BackgroundModes => AvailableBackgroundModes;

        public Color? BackgroundColor
        {
            get => _backgroundColor;
            set
            {
                if (_backgroundColor == value)
                    return;

                _backgroundColor = value;
                OnPropertyChanged();

                if (!IsGradient)
                {
                    _backgroundEndColor = value;
                    OnPropertyChanged(nameof(BackgroundEndColor));
                }

                OnPropertyChanged(nameof(BackgroundBrush));
            }
        }

        public Color? BackgroundEndColor
        {
            get => _backgroundEndColor;
            set
            {
                if (_backgroundEndColor == value)
                    return;

                _backgroundEndColor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BackgroundBrush));
            }
        }

        public Color? TextColor
        {
            get => _textColor;
            set
            {
                if (_textColor == value)
                    return;

                _textColor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TextBrush));
            }
        }

        public string BackgroundMode
        {
            get => _backgroundMode;
            set
            {
                string normalized = AvailableBackgroundModes.Contains(value)
                    ? value
                    : "Uni";

                if (_backgroundMode == normalized)
                    return;

                _backgroundMode = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGradient));
                OnPropertyChanged(nameof(CanEditEndColor));
                OnPropertyChanged(nameof(BackgroundBrush));
            }
        }

        public bool IsGradient =>
            string.Equals(BackgroundMode, "Horizontal", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(BackgroundMode, "Vertical", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(BackgroundMode, "Diagonal", System.StringComparison.OrdinalIgnoreCase);

        public bool CanEditEndColor => IsGradient;

        public Brush BackgroundBrush => CreateScheme().CreateBackgroundBrush();

        public Brush TextBrush =>
            new SolidColorBrush(TextColor ?? Colors.Transparent);

        public RibbonPanelColorScheme CreateScheme()
        {
            return new RibbonPanelColorScheme(
                BackgroundColor ?? Colors.Transparent,
                BackgroundEndColor ?? BackgroundColor ?? Colors.Transparent,
                TextColor ?? Colors.Transparent,
                IsGradient,
                GetGradientDirection(BackgroundMode),
                GetBackgroundPattern(BackgroundMode),
                _patternStart,
                _patternEnd);
        }

        public void ApplyScheme(RibbonPanelColorScheme scheme)
        {
            ApplyScheme(scheme, true);
        }

        private void ApplyScheme(RibbonPanelColorScheme scheme, bool notify)
        {
            _backgroundColor = scheme.BackgroundColor;
            _backgroundEndColor = scheme.BackgroundEndColor;
            _textColor = scheme.TextColor;
            _backgroundMode = GetBackgroundMode(scheme);
            _patternStart = scheme.PatternStart;
            _patternEnd = scheme.PatternEnd;

            if (!notify)
                return;

            OnPropertyChanged(nameof(BackgroundColor));
            OnPropertyChanged(nameof(BackgroundEndColor));
            OnPropertyChanged(nameof(TextColor));
            OnPropertyChanged(nameof(BackgroundMode));
            OnPropertyChanged(nameof(IsGradient));
            OnPropertyChanged(nameof(CanEditEndColor));
            OnPropertyChanged(nameof(BackgroundBrush));
            OnPropertyChanged(nameof(TextBrush));
        }

        private static string GetBackgroundMode(RibbonPanelColorScheme scheme)
        {
            if (scheme.BackgroundPattern == RibbonBackgroundPattern.FrenchFlag)
                return "France";

            if (scheme.BackgroundPattern == RibbonBackgroundPattern.FrenchFlagContinuous)
                return "France continue";

            if (scheme.BackgroundPattern == RibbonBackgroundPattern.ChristmasFestive)
                return "Noël festif";

            if (scheme.BackgroundPattern == RibbonBackgroundPattern.Confetti)
                return "Confettis";

            if (scheme.BackgroundPattern == RibbonBackgroundPattern.PokeBallPixel)
                return "Pokéball douce";

            return scheme.IsGradient
                ? scheme.GradientDirection.ToString()
                : "Uni";
        }

        private static RibbonBackgroundPattern GetBackgroundPattern(string mode)
        {
            if (string.Equals(mode, "France", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.FrenchFlag;

            if (string.Equals(mode, "France continue", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.FrenchFlagContinuous;

            if (string.Equals(mode, "Noël festif", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.ChristmasFestive;

            if (string.Equals(mode, "Confettis", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.Confetti;

            if (string.Equals(mode, "Pokéball douce", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.PokeBallPixel;

            return RibbonBackgroundPattern.Standard;
        }

        private static RibbonGradientDirection GetGradientDirection(string mode)
        {
            return System.Enum.TryParse(
                mode,
                true,
                out RibbonGradientDirection direction)
                ? direction
                : RibbonGradientDirection.Horizontal;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

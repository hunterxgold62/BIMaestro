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
        public ColorPreferencesWindow()
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            PanelColors = CreateItems(RibbonColorPreferences.Load());
            PreferenceFilePath =
                $"Sauvegarde : {RibbonColorPreferences.PreferenceFilePath}";
            DataContext = this;
        }

        public ObservableCollection<PanelColorItem> PanelColors { get; }

        public string PreferenceFilePath { get; }

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

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var colors = PanelColors.ToDictionary(
                    item => item.PanelName,
                    item => item.CreateScheme());

                RibbonColorPreferences.Save(colors);
                DialogResult = true;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"Impossible d’enregistrer les couleurs.\n\n{ex.Message}",
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
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
            new[] { "Uni", "Horizontal", "Vertical", "Diagonal" };

        private Color? _backgroundColor;
        private Color? _backgroundEndColor;
        private Color? _textColor;
        private string _backgroundMode;

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
                OnPropertyChanged(nameof(BackgroundBrush));
            }
        }

        public bool IsGradient =>
            !string.Equals(BackgroundMode, "Uni", System.StringComparison.OrdinalIgnoreCase);

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
                GetGradientDirection(BackgroundMode));
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

            if (!notify)
                return;

            OnPropertyChanged(nameof(BackgroundColor));
            OnPropertyChanged(nameof(BackgroundEndColor));
            OnPropertyChanged(nameof(TextColor));
            OnPropertyChanged(nameof(BackgroundMode));
            OnPropertyChanged(nameof(IsGradient));
            OnPropertyChanged(nameof(BackgroundBrush));
            OnPropertyChanged(nameof(TextBrush));
        }

        private static string GetBackgroundMode(RibbonPanelColorScheme scheme)
        {
            return scheme.IsGradient
                ? scheme.GradientDirection.ToString()
                : "Uni";
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

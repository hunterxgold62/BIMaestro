using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using BrowserOrganization = Autodesk.Revit.DB.BrowserOrganization;
using Document = Autodesk.Revit.DB.Document;
using FilteredElementCollector = Autodesk.Revit.DB.FilteredElementCollector;
using FolderItemInfo = Autodesk.Revit.DB.FolderItemInfo;
using ViewSheet = Autodesk.Revit.DB.ViewSheet;

namespace Couleur
{
    public partial class ColorPreferencesWindow :
        Window,
        INotifyPropertyChanged
    {
        private readonly System.IntPtr _mainWindowHandle;
        private readonly Document _document;
        private string _selectedPresetName;
        private ProjectBrowserColorSettings _browserPreferences;
        private readonly System.Random _previewRandom = new System.Random();
        private string _browserPreviewPrimaryViewName;
        private string _browserPreviewSecondaryViewName;
        private string _browserPreviewSectionName;
        private bool _isUpdatingBrowserColoringMode;
        private ProjectBrowserColorProfile _selectedBrowserColorProfile;
        private string _newBrowserColorProfileName = string.Empty;

        public ColorPreferencesWindow(
            System.IntPtr mainWindowHandle,
            Document document = null)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            _mainWindowHandle = mainWindowHandle;
            _document = document;
            PanelColors = CreateItems(RibbonColorPreferences.Load());
            BrowserPreferences =
                ProjectBrowserColorPreferences.Load();
            RefreshBrowserProfiles();
            PreferenceFilePath =
                $"Sauvegarde : {RibbonColorPreferences.PreferenceFilePath}";
            PresetEntries = RibbonColorPresetCatalog.StandardPresetNames
                .Select(name => new PresetMenuEntry(name, false))
                .Concat(new[] { new PresetMenuEntry("Animé", true) })
                .Concat(
                    RibbonColorPresetCatalog.AnimatedPresetNames
                        .Select(name => new PresetMenuEntry(name, false)))
                .ToList()
                .AsReadOnly();
            _selectedPresetName =
                RibbonColorPresetCatalog.StandardPresetNames.FirstOrDefault();
            GenerateBrowserPreviewNames();
            DetectBrowserCategories();
            DataContext = this;
        }

        public ObservableCollection<PanelColorItem> PanelColors { get; }

        public ObservableCollection<BrowserCategorySuggestion>
            BrowserCategorySuggestions { get; } =
                new ObservableCollection<BrowserCategorySuggestion>();

        public ObservableCollection<ProjectBrowserColorProfile>
            BrowserColorProfiles { get; } =
                new ObservableCollection<ProjectBrowserColorProfile>();

        public ProjectBrowserColorProfile SelectedBrowserColorProfile
        {
            get => _selectedBrowserColorProfile;
            set
            {
                _selectedBrowserColorProfile = value;
                OnPropertyChanged();
            }
        }

        public string NewBrowserColorProfileName
        {
            get => _newBrowserColorProfileName;
            set
            {
                _newBrowserColorProfileName = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string BrowserCategorySuggestionTitle =>
            BrowserCategorySuggestions.Count == 0
                ? "Aucune nouvelle catégorie détectée"
                : $"{BrowserCategorySuggestions.Count} catégories détectées";

        public string PreferenceFilePath { get; }

        public IReadOnlyList<PresetMenuEntry> PresetEntries { get; }

        public IReadOnlyList<string> BrowserBackgroundModes { get; } =
            new[]
            {
                "Uni",
                "Bulles pastel",
                "Vagues pastel",
                "Lucioles pastel",
                "Dégradé pastel animé"
            };

        public IReadOnlyList<string> BrowserColoringModes { get; } =
            new[]
            {
                "Aucune coloration",
                "Par type de vue",
                "Par catégories personnelles",
                "Combiner les deux"
            };

        public IReadOnlyList<string> BrowserViewColorTargets { get; } =
            new[] { "Fond", "Texte" };

        public string BrowserColoringMode
        {
            get
            {
                bool types =
                    BrowserPreferences?.IsViewTypeColoringEnabled == true;
                bool categories =
                    BrowserPreferences?.IsCategoryColoringEnabled == true;
                if (types && categories) return "Combiner les deux";
                if (types) return "Par type de vue";
                if (categories) return "Par catégories personnelles";
                return "Aucune coloration";
            }
            set
            {
                if (BrowserPreferences == null) return;
                _isUpdatingBrowserColoringMode = true;
                try
                {
                    BrowserPreferences.IsViewTypeColoringEnabled =
                        value == "Par type de vue" ||
                        value == "Combiner les deux";
                    BrowserPreferences.IsCategoryColoringEnabled =
                        value == "Par catégories personnelles" ||
                        value == "Combiner les deux";
                }
                finally
                {
                    _isUpdatingBrowserColoringMode = false;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(BrowserTypeColoringVisibility));
                OnPropertyChanged(nameof(BrowserCategoryColoringVisibility));
            }
        }

        public Visibility BrowserTypeColoringVisibility =>
            BrowserPreferences?.IsViewTypeColoringEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility BrowserCategoryColoringVisibility =>
            BrowserPreferences?.IsCategoryColoringEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public ProjectBrowserColorSettings BrowserPreferences
        {
            get => _browserPreferences;
            private set
            {
                if (_browserPreferences != null)
                {
                    _browserPreferences.PropertyChanged -=
                        BrowserPreferences_PropertyChanged;
                }

                _browserPreferences = value;
                if (_browserPreferences != null)
                {
                    _browserPreferences.PropertyChanged +=
                        BrowserPreferences_PropertyChanged;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(BrowserColoringMode));
                OnPropertyChanged(nameof(BrowserTypeColoringVisibility));
                OnPropertyChanged(nameof(BrowserCategoryColoringVisibility));
                NotifyBrowserPreviewChanged();
            }
        }

        public Brush BrowserPreviewBackgroundBrush =>
            new SolidColorBrush(
                BrowserPreferences?.BackgroundColor ?? Colors.White);

        public Brush BrowserPreviewTextBrush =>
            new SolidColorBrush(
                BrowserPreferences?.TextColor ?? Colors.Black);

        public Brush BrowserPreviewAccentBrush =>
            new SolidColorBrush(
                BrowserPreferences?.AccentColor ?? Colors.DodgerBlue);

        public Brush BrowserPreviewActiveParentBrush =>
            new SolidColorBrush(
                BrowserPreferences?.ActiveViewParentColor ?? Colors.Red);

        public Brush BrowserPreviewPlanBrush =>
            CreateViewTypePreviewBrush(
                BrowserPreferences?.PlanViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewSectionBrush =>
            CreateViewTypePreviewBrush(
                BrowserPreferences?.SectionViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewThreeDBrush =>
            CreateViewTypePreviewBrush(
                BrowserPreferences?.ThreeDViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewPlanParentBrush =>
            BrowserPreferences?.IsViewTypeColoringEnabled == true &&
            BrowserPreferences?.IsViewTypeParentColoringEnabled == true &&
            BrowserPreferences?.ViewColorTarget == "Fond"
                ? new SolidColorBrush(BrowserPreferences.PlanViewColor)
                : Brushes.Transparent;

        public Brush BrowserPreviewPlanTextBrush =>
            CreateViewTypePreviewTextBrush(
                BrowserPreferences?.PlanViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewPlanParentTextBrush =>
            BrowserPreferences?.IsViewTypeColoringEnabled == true &&
            BrowserPreferences?.IsViewTypeParentColoringEnabled == true &&
            BrowserPreferences?.ViewColorTarget == "Texte"
                ? new SolidColorBrush(BrowserPreferences.PlanViewColor)
                : BrowserPreviewTextBrush;

        public Brush BrowserPreviewSectionTextBrush =>
            CreateViewTypePreviewTextBrush(
                BrowserPreferences?.SectionViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewThreeDTextBrush =>
            CreateViewTypePreviewTextBrush(
                BrowserPreferences?.ThreeDViewColor ?? Colors.Transparent);

        public Visibility BrowserBubblesVisibility =>
            BrowserModeVisibility("Bulles pastel");

        public Visibility BrowserWavesVisibility =>
            BrowserModeVisibility("Vagues pastel");

        public Visibility BrowserFirefliesVisibility =>
            BrowserModeVisibility("Lucioles pastel");

        public Visibility BrowserAuroraVisibility =>
            BrowserModeVisibility("Dégradé pastel animé");

        public string BrowserPreviewPrimaryViewName
        {
            get => _browserPreviewPrimaryViewName;
            private set
            {
                _browserPreviewPrimaryViewName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BrowserPreviewSearchLabel));
            }
        }

        public string BrowserPreviewSecondaryViewName
        {
            get => _browserPreviewSecondaryViewName;
            private set
            {
                _browserPreviewSecondaryViewName = value;
                OnPropertyChanged();
            }
        }

        public string BrowserPreviewSectionName
        {
            get => _browserPreviewSectionName;
            private set
            {
                _browserPreviewSectionName = value;
                OnPropertyChanged();
            }
        }

        public string BrowserPreviewSearchLabel =>
            $"Rechercher : {BrowserPreviewPrimaryViewName}";

        public Visibility BrowserSearchVisibility =>
            BrowserPreferences?.IsSheetViewSearchEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility BrowserActiveParentVisibility =>
            BrowserPreferences?.IsActiveViewParentHighlightEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility BrowserDisabledVisibility =>
            BrowserPreferences?.IsEnabled == true
                ? Visibility.Collapsed
                : Visibility.Visible;

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

        private void BrowserPreferences_PropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            if (!_isUpdatingBrowserColoringMode)
            {
                OnPropertyChanged(nameof(BrowserColoringMode));
                OnPropertyChanged(nameof(BrowserTypeColoringVisibility));
                OnPropertyChanged(nameof(BrowserCategoryColoringVisibility));
            }
            NotifyBrowserPreviewChanged();
        }

        private void RegenerateBrowserPreviewButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            GenerateBrowserPreviewNames();
        }

        private void AddBrowserCategoryRuleButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            Color[] colors =
            {
                Color.FromRgb(209, 250, 229),
                Color.FromRgb(219, 234, 254),
                Color.FromRgb(252, 221, 235),
                Color.FromRgb(254, 229, 195),
                Color.FromRgb(237, 233, 254)
            };
            int index = BrowserPreferences.CategoryColorRules.Count;
            BrowserPreferences.CategoryColorRules.Add(
                new ProjectBrowserCategoryColorRule
                {
                    CategoryName = string.Empty,
                    Color = colors[index % colors.Length]
                });
            BrowserPreferences.IsCategoryColoringEnabled = true;
        }

        private void RemoveBrowserCategoryRuleButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is FrameworkElement element &&
                element.DataContext is ProjectBrowserCategoryColorRule rule)
            {
                BrowserPreferences.CategoryColorRules.Remove(rule);
            }
        }

        private void RefreshBrowserCategoriesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            DetectBrowserCategories();
        }

        private void AddDetectedBrowserCategoryButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) ||
                !(element.DataContext is BrowserCategorySuggestion suggestion))
            {
                return;
            }

            bool alreadyExists = BrowserPreferences.CategoryColorRules.Any(
                rule => string.Equals(
                    rule.CategoryName?.Trim(),
                    suggestion.Name,
                    System.StringComparison.OrdinalIgnoreCase));
            if (!alreadyExists)
            {
                BrowserPreferences.CategoryColorRules.Add(
                    new ProjectBrowserCategoryColorRule
                    {
                        CategoryName = suggestion.Name,
                        Color = suggestion.SuggestedColor
                    });
            }

            BrowserPreferences.IsCategoryColoringEnabled = true;
            BrowserCategorySuggestions.Remove(suggestion);
            OnPropertyChanged(nameof(BrowserCategorySuggestionTitle));
        }

        private void SaveBrowserProfileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                ProjectBrowserColorProfilePreferences.Save(
                    NewBrowserColorProfileName,
                    BrowserPreferences);
                string savedName = NewBrowserColorProfileName.Trim();
                RefreshBrowserProfiles(savedName);
                NewBrowserColorProfileName = string.Empty;
            }
            catch (System.Exception ex)
            {
                ShowSaveError(ex);
            }
        }

        private void ApplyBrowserProfileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (SelectedBrowserColorProfile?.Settings == null) return;
            BrowserPreferences = ProjectBrowserColorPreferences.Clone(
                SelectedBrowserColorProfile.Settings);
            DetectBrowserCategories();
        }

        private void DeleteBrowserProfileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (SelectedBrowserColorProfile == null) return;
            ProjectBrowserColorProfilePreferences.Delete(
                SelectedBrowserColorProfile.Name);
            RefreshBrowserProfiles();
        }

        private void RefreshBrowserProfiles(string selectedName = null)
        {
            BrowserColorProfiles.Clear();
            foreach (ProjectBrowserColorProfile profile in
                     ProjectBrowserColorProfilePreferences.Load())
            {
                BrowserColorProfiles.Add(profile);
            }
            SelectedBrowserColorProfile = BrowserColorProfiles
                .FirstOrDefault(profile => string.Equals(
                    profile.Name,
                    selectedName,
                    System.StringComparison.OrdinalIgnoreCase)) ??
                BrowserColorProfiles.FirstOrDefault();
        }

        private void DetectBrowserCategories()
        {
            BrowserCategorySuggestions.Clear();
            if (_document == null)
            {
                OnPropertyChanged(nameof(BrowserCategorySuggestionTitle));
                return;
            }

            var counts = new Dictionary<string, int>(
                System.StringComparer.OrdinalIgnoreCase);
            try
            {
                BrowserOrganization organization =
                    BrowserOrganization
                        .GetCurrentBrowserOrganizationForViews(_document);
                foreach (Autodesk.Revit.DB.View view in
                         new FilteredElementCollector(_document)
                             .OfClass(typeof(Autodesk.Revit.DB.View))
                             .Cast<Autodesk.Revit.DB.View>())
                {
                    if (view == null ||
                        view.IsTemplate ||
                        view is ViewSheet ||
                        organization == null ||
                        !organization.AreFiltersSatisfied(view.Id))
                    {
                        continue;
                    }

                    IList<FolderItemInfo> folderItems = null;
                    try
                    {
                        folderItems = organization
                            .GetFolderItems(view.Id)
                            ?.Where(item => item != null)
                            .ToList();
                        if (folderItems == null) continue;

                        foreach (string name in folderItems
                                     .Select(item => item.Name?.Trim())
                                     .Where(name =>
                                         !string.IsNullOrWhiteSpace(name))
                                     .Distinct(
                                         System.StringComparer
                                             .OrdinalIgnoreCase))
                        {
                            counts[name] = counts.TryGetValue(
                                name,
                                out int count)
                                ? count + 1
                                : 1;
                        }
                    }
                    finally
                    {
                        if (folderItems != null)
                        {
                            foreach (FolderItemInfo item in folderItems)
                            {
                                try { item.Dispose(); }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Une organisation en cours de modification sera relue avec
                // le bouton Actualiser lorsque Revit sera de nouveau disponible.
            }

            var existingNames = new HashSet<string>(
                BrowserPreferences.CategoryColorRules
                    .Select(rule => rule.CategoryName?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                System.StringComparer.OrdinalIgnoreCase);
            Color[] palette =
            {
                Color.FromRgb(209, 250, 229),
                Color.FromRgb(219, 234, 254),
                Color.FromRgb(252, 221, 235),
                Color.FromRgb(254, 229, 195),
                Color.FromRgb(237, 233, 254),
                Color.FromRgb(254, 240, 138)
            };
            int colorIndex = 0;
            foreach (KeyValuePair<string, int> item in counts
                         .Where(item => !existingNames.Contains(item.Key))
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key))
            {
                BrowserCategorySuggestions.Add(
                    new BrowserCategorySuggestion(
                        item.Key,
                        item.Value,
                        palette[colorIndex++ % palette.Length]));
            }

            OnPropertyChanged(nameof(BrowserCategorySuggestionTitle));
        }

        private void GenerateBrowserPreviewNames()
        {
            string[] disciplines =
            {
                "Architecture", "Structure", "Aménagement", "Coordination"
            };
            string[] levels =
            {
                "RDC", "Niveau 01", "Niveau 02", "Toiture"
            };
            string[] sections =
            {
                "Coupe AA · Hall central",
                "Coupe BB · Escalier principal",
                "Façade · Nord",
                "Détail · Entrée principale"
            };

            string level = levels[_previewRandom.Next(levels.Length)];
            string secondLevel;
            do
            {
                secondLevel = levels[_previewRandom.Next(levels.Length)];
            }
            while (secondLevel == level);

            BrowserPreviewPrimaryViewName =
                $"Plan {disciplines[_previewRandom.Next(disciplines.Length)]} · {level}";
            BrowserPreviewSecondaryViewName =
                $"Plan {disciplines[_previewRandom.Next(disciplines.Length)]} · {secondLevel}";
            BrowserPreviewSectionName =
                sections[_previewRandom.Next(sections.Length)];
        }

        private Visibility BrowserModeVisibility(string mode)
        {
            return string.Equals(
                       BrowserPreferences?.BackgroundMode,
                       mode,
                       System.StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void NotifyBrowserPreviewChanged()
        {
            OnPropertyChanged(nameof(BrowserPreviewBackgroundBrush));
            OnPropertyChanged(nameof(BrowserPreviewTextBrush));
            OnPropertyChanged(nameof(BrowserPreviewAccentBrush));
            OnPropertyChanged(nameof(BrowserPreviewActiveParentBrush));
            OnPropertyChanged(nameof(BrowserPreviewPlanBrush));
            OnPropertyChanged(nameof(BrowserPreviewSectionBrush));
            OnPropertyChanged(nameof(BrowserPreviewThreeDBrush));
            OnPropertyChanged(nameof(BrowserPreviewPlanParentBrush));
            OnPropertyChanged(nameof(BrowserPreviewPlanTextBrush));
            OnPropertyChanged(nameof(BrowserPreviewPlanParentTextBrush));
            OnPropertyChanged(nameof(BrowserPreviewSectionTextBrush));
            OnPropertyChanged(nameof(BrowserPreviewThreeDTextBrush));
            OnPropertyChanged(nameof(BrowserBubblesVisibility));
            OnPropertyChanged(nameof(BrowserWavesVisibility));
            OnPropertyChanged(nameof(BrowserFirefliesVisibility));
            OnPropertyChanged(nameof(BrowserAuroraVisibility));
            OnPropertyChanged(nameof(BrowserSearchVisibility));
            OnPropertyChanged(nameof(BrowserActiveParentVisibility));
            OnPropertyChanged(nameof(BrowserDisabledVisibility));
        }

        private Brush CreateViewTypePreviewBrush(Color color)
        {
            return BrowserPreferences?.IsViewTypeColoringEnabled == true &&
                   BrowserPreferences?.ViewColorTarget == "Fond"
                ? new SolidColorBrush(color)
                : Brushes.Transparent;
        }

        private Brush CreateViewTypePreviewTextBrush(Color color)
        {
            return BrowserPreferences?.IsViewTypeColoringEnabled == true &&
                   BrowserPreferences?.ViewColorTarget == "Texte"
                ? new SolidColorBrush(color)
                : BrowserPreviewTextBrush;
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

            BrowserPreferences =
                ProjectBrowserColorPreferences.GetDefaults();
        }

        private void ResetBrowserDefaultsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ProjectBrowserColorSettings reset =
                ProjectBrowserColorPreferences.GetDefaults();
            reset.IsEnabled = false;
            reset.IsActiveViewParentHighlightEnabled = false;
            reset.BackgroundMode = "Uni";
            BrowserPreferences = reset;
            ProjectBrowserColorPreferences.Save(reset);
            ProjectBrowserColoring.Reset();
            ProjectBrowserColoring.Apply(_mainWindowHandle);
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
            ProjectBrowserColorPreferences.Save(
                BrowserPreferences);
            ProjectBrowserColoring.Reset();
            ProjectBrowserColoring.Apply(_mainWindowHandle);
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

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(
            [CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class PresetMenuEntry
    {
        public PresetMenuEntry(string name, bool isHeader)
        {
            Name = name;
            IsHeader = isHeader;
        }

        public string Name { get; }

        public bool IsHeader { get; }
    }

    public sealed class PanelColorItem : INotifyPropertyChanged
    {
        private static readonly IReadOnlyList<string> AvailableBackgroundModes =
            new[]
            {
                "Uni", "Horizontal", "Vertical", "Diagonal",
                "France", "France continue", "Noël festif", "Confettis",
                "Pokéball douce", "Pokémon pixel", "Arc-en-ciel animé",
                "Bulles pastel", "Vagues pastel", "Étoiles pastel",
                "Nuages doux"
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

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPokemonPixelContinuous)
            {
                return "Pokémon pixel";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedRainbowContinuous)
            {
                return "Arc-en-ciel animé";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelBubblesContinuous)
            {
                return "Bulles pastel";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelWavesContinuous)
            {
                return "Vagues pastel";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelStarsContinuous)
            {
                return "Étoiles pastel";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedSoftCloudsContinuous)
            {
                return "Nuages doux";
            }

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

            if (string.Equals(
                    mode,
                    "Pokémon pixel",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedPokemonPixelContinuous;
            }

            if (string.Equals(
                    mode,
                    "Arc-en-ciel animé",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedRainbowContinuous;
            }

            if (string.Equals(
                    mode,
                    "Bulles pastel",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedPastelBubblesContinuous;
            }

            if (string.Equals(
                    mode,
                    "Vagues pastel",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedPastelWavesContinuous;
            }

            if (string.Equals(
                    mode,
                    "Étoiles pastel",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedPastelStarsContinuous;
            }

            if (string.Equals(
                    mode,
                    "Nuages doux",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedSoftCloudsContinuous;
            }

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

    public sealed class BrowserCategorySuggestion
    {
        public BrowserCategorySuggestion(
            string name,
            int viewCount,
            Color suggestedColor)
        {
            Name = name;
            ViewCount = viewCount;
            SuggestedColor = suggestedColor;
        }

        public string Name { get; }

        public int ViewCount { get; }

        public Color SuggestedColor { get; }

        public string CountLabel =>
            ViewCount == 1 ? "1 vue" : $"{ViewCount} vues";
    }
}

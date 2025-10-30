using Autodesk.Revit.UI;
using BIMaestro.UI;
using Famille.Orbit3D;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Microsoft.Win32;

namespace Famille
{
    public partial class FamilyBrowserWindow : Window, INotifyPropertyChanged
    {
        // ===== Constantes =====
        private const string FavoritesCollectionId = "builtin_favoris";
        private const string FavoritesCollectionName = "Favoris";

        // ===== Chemins =====
        private string rootFolderPath = @"\\intranet.cabinet-merlin.fr\groupe-merlin\Gerland-Energie\Affaires\0-Boîte à outils Revit\0-Bibliothèque\A-Famille Revit";
        private string familiesFolder = @"\\intranet.cabinet-merlin.fr\groupe-merlin\Gerland-Energie\Affaires\0-Boîte à outils Revit\0-Bibliothèque\A-Famille Revit";
        private string imagesFolder = @"\\intranet.cabinet-merlin.fr\groupe-merlin\Gerland-Energie\Affaires\0-Boîte à outils Revit\0-Bibliothèque\B-Famille Revit Image";

        private readonly string favoritesFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "Favorites.txt");
        private readonly string configFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "Config.txt");
        private readonly string pathsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "CheminsFamille.json");
        private readonly string workFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "FamilleRevit");

        // Cache de vignettes (disque)
        private readonly string thumbCacheFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "RevitLogs", "CacheVignettes");

        // Options (exposables dans Paramètres si tu veux)
        private bool useShellThumbs = true;   // ON par défaut
        private bool useRevitPreview = false;  // ON : fallback natif Revit lorsque pas d'image catalogue
        private bool detailedViewMode = false;

        private string _activeCategoryFilter;
        private string _activeVersionFilter;
        private bool _sizeSortDescending;
        private bool _dateSortDescending;
        private bool _isResorting;

        // ===== Données UI =====
        private List<FamilyItem> allFamilies = new();
        private List<FamilyItem> displayedFamilies = new();
        private string currentFolderPath;
        private readonly int _revitMajorVersion;

        public string RootFolderName => System.IO.Path.GetFileName(rootFolderPath);

        public string ActiveCategoryFilter
        {
            get => _activeCategoryFilter;
            private set
            {
                if (string.Equals(_activeCategoryFilter, value, StringComparison.OrdinalIgnoreCase)) return;
                _activeCategoryFilter = value;
                NotifyPropertyChanged(nameof(ActiveCategoryFilter));
            }
        }

        public string ActiveVersionFilter
        {
            get => _activeVersionFilter;
            private set
            {
                if (string.Equals(_activeVersionFilter, value, StringComparison.OrdinalIgnoreCase)) return;
                _activeVersionFilter = value;
                NotifyPropertyChanged(nameof(ActiveVersionFilter));
            }
        }

        public bool IsSizeSortActive => _sizeSortDescending;
        public bool IsDateSortActive => _dateSortDescending;

        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // Pagination & recherche
        private const int PageSize = 200;
        private int _nextIndex = 0;
        private List<FamilyItem> _currentResult = new();
        private readonly DispatcherTimer _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        // Index global (auto)
        private FamilyIndexService _index;
        private bool _globalSearchMode = false;

        // Concurrence & cache mémoire pour images
        private static readonly System.Threading.SemaphoreSlim _thumbGate = new(4);
        private static readonly Dictionary<string, ImageSource> _bitmapCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, FamilyPartAtomMeta> _metadataCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _metadataPending =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _metadataLock = new();
        private static readonly object _documentationLock = new();
        private static readonly Dictionary<string, List<FamilyDocumentLink>> _documentationCache =
            new(StringComparer.OrdinalIgnoreCase);

        // ===== Collections =====
        private ObservableCollection<Collection> _collections = new();
        private Collection _selectedCollection;
        private bool _useGhostSwitch;

        public FamilyBrowserWindow()
        {
            InitializeComponent();
            DataContext = this;

            if (FamilyBrowserCommand.uiapp?.Application?.VersionNumber != null &&
                int.TryParse(FamilyBrowserCommand.uiapp.Application.VersionNumber, out var major))
            {
                _revitMajorVersion = major;
            }

            FamilyPreviewBridge.PreviewVisibilityChanged += OnPreviewVisibilityChanged;

            if (AlwaysOnTopSwitch != null)
            {
                AlwaysOnTopSwitch.FollowScope = SettingsPanelRoot;
            }

            UpdateGhostFollowMode();

            LoadSavedPaths();


            _searchDebounce.Tick += (s, e) =>
            {
                _searchDebounce.Stop();
                ApplyFilters();
            };

            if (!Directory.Exists(familiesFolder) || !Directory.Exists(imagesFolder))
            {
                if (!PromptForFolders())
                {
                    Close();
                    return;
                }
            }
            currentFolderPath = rootFolderPath;

            _index = new FamilyIndexService(familiesFolder, imagesFolder);
            _index.IndexUpdated += OnIndexUpdated;
        }

        protected override void OnClosed(EventArgs e)
        {
            FamilyPreviewBridge.PreviewVisibilityChanged -= OnPreviewVisibilityChanged;
            base.OnClosed(e);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            UpdateTheme();
            EnsureFilesExist();

            // Collections (inclut Favoris non supprimable)
            LoadCollections();
            EnsureFavoritesCollection();
            ImportFavoritesTxtIntoFavoritesCollection(); // synchro compat


            Famille.CatalogImageResolver.Initialize(familiesFolder, imagesFolder);
            Famille.FamilyThumbnailProvider.Initialize(FamilyBrowserCommand.uiapp);
            Famille.FamilyMetadataProvider.Initialize(FamilyBrowserCommand.uiapp);

            // Arbo + Top-8
            LoadFolderTree();

            PlaceholderText.Visibility = Visibility.Visible;

            // Index
            UpdateGhostFollowMode();
            await _index.StartAsync();
        }

        private void OnIndexUpdated()
        {
            Dispatcher.Invoke(() =>
            {
                if (_globalSearchMode) ApplyFilters();
            });
        }

        #region Arbo & chargement dossier

        private void LoadFolderTree()
        {
            currentFolderPath = rootFolderPath;
            FolderTreeView.Items.Clear();

            if (!Directory.Exists(familiesFolder))
            {
                MessageBox.Show(this, "Le dossier de familles spécifié n'existe pas.", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var root = new DirectoryInfo(familiesFolder);
            foreach (var sub in root.GetDirectories())
            {
                var node = CreateDirectoryNode(sub);
                node.IsExpanded = false;
                FolderTreeView.Items.Add(node);
            }

            bool showTop8AtStartup = (ShowTop8CheckBox?.IsChecked == true);

            allFamilies.Clear();
            displayedFamilies.Clear();
            FamilyListView.ItemsSource = displayedFamilies;
            UpdateFamilyListViewMode();
            UpdateCount(0);

            RefreshTop8_UsageOnly();

            if (!showTop8AtStartup && FolderTreeView.Items.Count > 0)
                ((TreeViewItem)FolderTreeView.Items[0]).IsSelected = true;
        }

        private TreeViewItem CreateDirectoryNode(DirectoryInfo dir)
        {
            var item = new TreeViewItem { Header = dir.Name, Tag = dir.FullName };
            foreach (var sub in dir.GetDirectories())
                item.Items.Add(CreateDirectoryNode(sub));
            return item;
        }

        private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (FolderTreeView.SelectedItem is not TreeViewItem tv) return;

            ResetInteractiveFilters();

            if (!string.IsNullOrWhiteSpace(SearchBox.Text) || _globalSearchMode)
            {
                _globalSearchMode = false;
                SearchBox.Text = "";
                Keyboard.ClearFocus();
            }

            currentFolderPath = tv.Tag.ToString();
            LoadFamilies(currentFolderPath, recursive: false);

            BeginPaging(ApplyInteractiveSorting(new List<FamilyItem>(allFamilies)));

            TopFamiliesView.Visibility = Visibility.Collapsed;
            TopSeparator.Visibility = Visibility.Collapsed;
        }

        private void LoadFamilies(string path, bool recursive)
        {
            ResetInteractiveFilters();
            allFamilies.Clear();
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            foreach (var f in Directory.EnumerateFiles(path, "*.rfa", opt))
                allFamilies.Add(CreateFamilyItemFromPath(f));

            allFamilies = allFamilies
                .OrderBy(f =>
                {
                    var p = f.Name.Split('-');
                    if (p.Length == 2 && int.TryParse(p[1], out int n))
                        return (p[0], n);
                    return (f.Name, int.MaxValue);
                })
                .ToList();

            TopFamiliesView.Visibility = Visibility.Collapsed;
            TopSeparator.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region Top-8

        private void RefreshTop8_UsageOnly()
        {
            bool show = (ShowTop8CheckBox?.IsChecked == true);
            if (!show)
            {
                TopFamiliesView.Visibility = Visibility.Collapsed;
                TopSeparator.Visibility = Visibility.Collapsed;
                return;
            }

            var usage = FamilyUsageManager.Load();
            var topPaths = usage.OrderByDescending(kv => kv.Value)
                                .Select(kv => kv.Key)
                                .Where(File.Exists)
                                .Take(8)
                                .ToList();

            var topItems = new List<FamilyItem>();
            foreach (var p in topPaths)
                topItems.Add(CreateFamilyItemFromPath(p));

            TopFamiliesView.ItemsSource = topItems;
            var vis = topItems.Any() ? Visibility.Visible : Visibility.Collapsed;
            TopFamiliesView.Visibility = vis;
            TopSeparator.Visibility = vis;

            foreach (var it in topItems)
            {
                LoadThumbnailForFamilyItem(it);
                LoadMetadataForFamilyItem(it);
            }

        }

        private void ShowTop8CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (string.Equals(currentFolderPath, rootFolderPath, StringComparison.OrdinalIgnoreCase))
                RefreshTop8_UsageOnly();
        }

        #endregion

        #region Favoris (★)  + compat Favorites.txt  via Collection "Favoris"

        private void ImportFavoritesTxtIntoFavoritesCollection()
        {
            try
            {
                if (!File.Exists(favoritesFile)) return;
                var favs = new HashSet<string>(File.ReadAllLines(favoritesFile), StringComparer.OrdinalIgnoreCase);
                var favCol = GetFavoritesCollection();
                foreach (var p in favs)
                    if (!favCol.Paths.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        favCol.Paths.Add(p);
                SaveCollections();
            }
            catch { }
        }

        private void ExportFavoritesCollectionToTxt()
        {
            try
            {
                var favCol = GetFavoritesCollection();
                Directory.CreateDirectory(Path.GetDirectoryName(favoritesFile));
                File.WriteAllLines(favoritesFile, favCol.Paths);
            }
            catch { }
        }

        private void FavoriteButton_Click(object s, RoutedEventArgs e)
        {
            if (s is not Button btn || btn.DataContext is not FamilyItem fam) return;

            // toggle visuel
            fam.IsFavorite = !fam.IsFavorite;

            var favCol = GetFavoritesCollection();

            if (fam.IsFavorite)
            {
                if (!favCol.Paths.Any(p => p.Equals(fam.Path, StringComparison.OrdinalIgnoreCase)))
                    favCol.Paths.Add(fam.Path);
            }
            else
            {
                favCol.Paths.RemoveAll(p => p.Equals(fam.Path, StringComparison.OrdinalIgnoreCase));
            }

            SaveCollections();
            ExportFavoritesCollectionToTxt();
        }

        private void MarkFavoritesInView(IEnumerable<FamilyItem> items)
        {
            try
            {
                var favCol = GetFavoritesCollection();
                var set = new HashSet<string>(favCol.Paths, StringComparer.OrdinalIgnoreCase);
                foreach (var fam in items)
                    fam.IsFavorite = set.Contains(fam.Path);
            }
            catch { }
        }

        #endregion

        #region Interactivité bulles & mise en avant

        private void CategoryPill_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not FamilyItem fam) return;
            SetCategoryFilter(fam.Category);
        }

        private void VersionPill_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not FamilyItem fam) return;
            SetVersionFilter(fam.RevitSavedVersion);
        }

        private void SizePill_Click(object sender, RoutedEventArgs e)
        {
            ToggleSizeSorting();
        }

        private void DateSortButton_Click(object sender, RoutedEventArgs e)
        {
            if (DetailedViewCheckBox?.IsChecked != true)
                return;

            ToggleDateSorting();
        }

        private void SetCategoryFilter(string category)
        {
            string normalized = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
            if (!string.IsNullOrEmpty(ActiveCategoryFilter) &&
                normalized != null &&
                string.Equals(ActiveCategoryFilter, normalized, StringComparison.OrdinalIgnoreCase))
            {
                normalized = null;
            }

            ActiveCategoryFilter = normalized;
            ApplyFilters();
        }

        private void SetVersionFilter(string version)
        {
            string normalized = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            if (!string.IsNullOrEmpty(ActiveVersionFilter) &&
                normalized != null &&
                string.Equals(ActiveVersionFilter, normalized, StringComparison.OrdinalIgnoreCase))
            {
                normalized = null;
            }

            ActiveVersionFilter = normalized;
            ApplyFilters();
        }

        private void ToggleSizeSorting()
        {
            _sizeSortDescending = !_sizeSortDescending;
            NotifyPropertyChanged(nameof(IsSizeSortActive));
            ApplyFilters();
        }

        private void ToggleDateSorting()
        {
            _dateSortDescending = !_dateSortDescending;
            NotifyPropertyChanged(nameof(IsDateSortActive));
            ApplyFilters();
        }

        private void ResetInteractiveFilters()
        {
            ActiveCategoryFilter = null;
            ActiveVersionFilter = null;

            if (_sizeSortDescending)
            {
                _sizeSortDescending = false;
                NotifyPropertyChanged(nameof(IsSizeSortActive));
            }

            if (_dateSortDescending)
            {
                _dateSortDescending = false;
                NotifyPropertyChanged(nameof(IsDateSortActive));
            }
        }
        private List<FamilyItem> ApplyInteractiveSorting(List<FamilyItem> source)
        {
            if (source == null)
                return new List<FamilyItem>();

            var result = source.ToList();
            if (result.Count == 0)
                return result;

            var categoryFilter = ActiveCategoryFilter;
            var versionFilter = ActiveVersionFilter;
            bool hasCategory = !string.IsNullOrEmpty(categoryFilter);
            bool hasVersion = !string.IsNullOrEmpty(versionFilter);
            bool hasSizeSort = _sizeSortDescending;
            bool hasDateSort = _dateSortDescending;

            if (!hasCategory && !hasVersion && !hasSizeSort && !hasDateSort)
                return result;

            bool CategoryMatches(FamilyItem item)
            {
                if (!hasCategory || item == null)
                    return false;

                return !string.IsNullOrWhiteSpace(item.Category) &&
                       string.Equals(item.Category.Trim(), categoryFilter, StringComparison.OrdinalIgnoreCase);
            }

            bool VersionMatches(FamilyItem item)
            {
                if (!hasVersion || item == null)
                    return false;

                return !string.IsNullOrWhiteSpace(item.RevitSavedVersion) &&
                       string.Equals(item.RevitSavedVersion.Trim(), versionFilter, StringComparison.OrdinalIgnoreCase);
            }

            result.Sort((a, b) =>
            {
                if (hasCategory)
                {
                    int catCmp = CategoryMatches(b).CompareTo(CategoryMatches(a));
                    if (catCmp != 0)
                        return catCmp;
                }

                if (hasVersion)
                {
                    int verCmp = VersionMatches(b).CompareTo(VersionMatches(a));
                    if (verCmp != 0)
                        return verCmp;
                }

                if (hasDateSort)
                {
                    var aDate = a?.LastUpdatedUtc ?? DateTime.MinValue;
                    var bDate = b?.LastUpdatedUtc ?? DateTime.MinValue;
                    int dateCmp = bDate.CompareTo(aDate);
                    if (dateCmp != 0)
                        return dateCmp;
                }

                if (hasSizeSort)
                {
                    long aSize = a?.FileSizeBytes ?? -1;
                    long bSize = b?.FileSizeBytes ?? -1;
                    int sizeCmp = bSize.CompareTo(aSize);
                    if (sizeCmp != 0)
                        return sizeCmp;
                }

                var aName = a?.Name ?? string.Empty;
                var bName = b?.Name ?? string.Empty;
                return CultureInfo.CurrentCulture.CompareInfo.Compare(aName, bName, CompareOptions.IgnoreCase);
            });

            return result;
        }

        private void ResortCurrentView()
        {
            if (_isResorting)
                return;

            if (_currentResult == null || _currentResult.Count == 0)
                return;

            var sorted = ApplyInteractiveSorting(_currentResult);
            if (sorted.Count == 0)
            {
                try
                {
                    _isResorting = true;
                    _currentResult = sorted;
                    displayedFamilies = new List<FamilyItem>();
                    FamilyListView.ItemsSource = displayedFamilies;
                    UpdateCount(0);
                    PagingStatusText.Text = "Aucun résultat.";
                }
                finally
                {
                    _isResorting = false;
                }
                return;
            }

            try
            {
                _isResorting = true;

                _currentResult = sorted;
                int loadedCount = Math.Min(displayedFamilies?.Count ?? 0, _currentResult.Count);
                displayedFamilies = _currentResult.GetRange(0, loadedCount);
                _nextIndex = loadedCount;

                FamilyListView.ItemsSource = null;
                FamilyListView.ItemsSource = displayedFamilies;

                foreach (var item in displayedFamilies)
                {
                    LoadThumbnailForFamilyItem(item);
                    LoadMetadataForFamilyItem(item);
                }

                MarkFavoritesInView(displayedFamilies);
                UpdateCount(_currentResult.Count);

                PagingStatusText.Text = _nextIndex >= _currentResult.Count
                    ? "Fin de la liste."
                    : $"Affichés : {displayedFamilies.Count}/{_currentResult.Count}";
            }
            finally
            {
                _isResorting = false;
            }
        }

        #endregion


        #region Recherche + pagination

        private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _globalSearchMode = true;

            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _globalSearchMode = false;
                BeginPaging(ApplyInteractiveSorting(new List<FamilyItem>(allFamilies)));
            }
        }

        private void SearchBox_TextChanged(object s, TextChangedEventArgs e)
        {
            ResetInteractiveFilters();
            PlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void ApplyFilters()
        {
            var txt = StripDiacritics(SearchBox.Text ?? "").ToLowerInvariant();

            if (_globalSearchMode)
            {
                if (!_index.IsReady)
                {
                    BeginPaging(ApplyInteractiveSorting(new List<FamilyItem>()));
                    PagingStatusText.Visibility = Visibility.Visible;
                    PagingStatusText.Text = "Index en cours de préparation…";
                    UpdateCount(0);
                    return;
                }

                if (txt.Length < 2)
                {
                    BeginPaging(ApplyInteractiveSorting(new List<FamilyItem>()));
                    PagingStatusText.Visibility = Visibility.Visible;
                    PagingStatusText.Text = "Tape au moins 2 caractères pour rechercher partout.";
                    UpdateCount(0);
                    return;
                }

                var hits = _index.Search(txt, max: 8000);
                var items = hits.Select(e =>
                {
                    var size = TryGetFileSize(e.Path);
                    return new FamilyItem
                    {
                        Name = e.Name,
                        Path = e.Path,
                        Category = e.Category,
                        NormalizedName = e.NormalizedName,
                        FileSizeBytes = size,
                        FileSizeText = FormatFileSize(size),
                        DocumentationAvailable = HasDocumentationFile(e.Path)
                    };
                }).ToList();

                BeginPaging(ApplyInteractiveSorting(items));
                return;
            }
            else
            {
                IEnumerable<FamilyItem> baseSet = allFamilies;
                if (!string.IsNullOrEmpty(txt))
                    baseSet = baseSet.Where(f => f.NormalizedName.Contains(txt));

                BeginPaging(ApplyInteractiveSorting(baseSet.ToList()));
            }
        }


        private void BeginPaging(List<FamilyItem> fullResult)
        {
            _currentResult = fullResult ?? new List<FamilyItem>();
            _nextIndex = 0;
            displayedFamilies = new List<FamilyItem>();
            FamilyListView.ItemsSource = displayedFamilies;

            PagingStatusText.Visibility = Visibility.Visible;
            PagingStatusText.Text = _currentResult.Count == 0 ? "Aucun résultat." : "Chargement...";

            AppendNextPage();
            UpdateCount(_currentResult.Count);
        }

        private void AppendNextPage()
        {
            if (_nextIndex >= _currentResult.Count)
            {
                PagingStatusText.Text = "Fin de la liste.";
                return;
            }

            int take = Math.Min(PageSize, _currentResult.Count - _nextIndex);
            var slice = _currentResult.GetRange(_nextIndex, take);
            _nextIndex += take;

            foreach (var item in slice)
            {
                displayedFamilies.Add(item);
                LoadThumbnailForFamilyItem(item);
                LoadMetadataForFamilyItem(item);
            }

            // rafraîchit source
            FamilyListView.ItemsSource = null;
            FamilyListView.ItemsSource = displayedFamilies;

            // Met à jour les étoiles selon la collection Favoris
            MarkFavoritesInView(displayedFamilies);

            PagingStatusText.Text = _nextIndex >= _currentResult.Count
                ? "Fin de la liste."
                : $"Affichés : {displayedFamilies.Count}/{_currentResult.Count}";
        }

        private void ItemsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange <= 0) return;
            var sv = (ScrollViewer)sender;
            if (sv.ScrollableHeight <= 0) return;

            double ratio = sv.VerticalOffset / sv.ScrollableHeight;
            if (ratio > 0.85)
                AppendNextPage();
        }

        private void UpdateCount(int? c = null)
        {
            if (CountTextBlock == null) return;
            CountTextBlock.Text = (c ?? displayedFamilies.Count).ToString();
        }

        #endregion

        #region Documentation

        private void DocumentationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is not FamilyItem fam)
                return;

            OpenDocumentationForFamily(fam, allowPrompt: true);
        }

        private void DocumentationBadge_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not FamilyItem fam)
                return;

            OpenDocumentationForFamily(fam, allowPrompt: false);
            e.Handled = true;
        }

        private void OpenDocumentationForFamily(FamilyItem fam, bool allowPrompt)
        {
            if (fam == null)
                return;

            EnsureDocumentationLoaded(fam);

            if (!fam.HasDocumentation)
            {
                if (allowPrompt)
                {
                    if (!PromptAddDocumentation(fam))
                    {
                        return;
                    }

                    if (!fam.HasDocumentation)
                    {
                        MessageBox.Show(this,
                            "Aucun document n'a été associé à cette famille.",
                            "Documentation",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }
                }
                else
                {
                    fam.DocumentationAvailable = fam.HasDocumentation;
                    MessageBox.Show(this,
                        "Aucun document n'a été associé à cette famille.",
                        "Documentation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }

            if (fam.DocumentationLinks.Count == 1)
            {
                OpenDocument(fam.DocumentationLinks[0]);
                return;
            }

            ShowDocumentationPicker(fam);
        }

        private void EnsureDocumentationLoaded(FamilyItem fam)
        {
            if (fam == null || string.IsNullOrWhiteSpace(fam.Path))
                return;

            List<FamilyDocumentLink> docs;

            lock (_documentationLock)
            {
                if (!_documentationCache.TryGetValue(fam.Path, out docs))
                {
                    docs = LoadDocumentationFromDisk(fam.Path);
                    _documentationCache[fam.Path] = docs;
                }
            }

            fam.DocumentationLinks.Clear();
            foreach (var link in docs)
            {
                fam.DocumentationLinks.Add(link.Clone());
            }

            fam.DocumentationAvailable = fam.DocumentationLinks.Count > 0;
        }

        private List<FamilyDocumentLink> LoadDocumentationFromDisk(string familyPath)
        {
            var docFile = GetDocumentationFilePath(familyPath);
            if (string.IsNullOrWhiteSpace(docFile) || !File.Exists(docFile))
                return new List<FamilyDocumentLink>();

            try
            {
                var json = File.ReadAllText(docFile, Encoding.UTF8);
                var payload = JsonConvert.DeserializeObject<FamilyDocumentationPayload>(json);
                var docs = new List<FamilyDocumentLink>();

                if (payload?.Documents != null)
                {
                    foreach (var link in payload.Documents)
                    {
                        if (link == null || string.IsNullOrWhiteSpace(link.FilePath))
                            continue;

                        if (string.IsNullOrWhiteSpace(link.Label))
                            link.Label = System.IO.Path.GetFileName(link.FilePath);

                        if (string.IsNullOrWhiteSpace(link.Type))
                        {
                            var ext = System.IO.Path.GetExtension(link.FilePath)?.TrimStart('.');
                            link.Type = string.IsNullOrWhiteSpace(ext) ? null : ext.ToUpperInvariant();
                        }

                        docs.Add(link);
                    }
                }

                return docs
                    .OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<FamilyDocumentLink>();
            }
        }

        private static string GetDocumentationFilePath(string familyPath)
            => string.IsNullOrWhiteSpace(familyPath) ? null : familyPath + ".docs.json";

        private static bool HasDocumentationFile(string familyPath)
        {
            var docFile = GetDocumentationFilePath(familyPath);
            return !string.IsNullOrWhiteSpace(docFile) && File.Exists(docFile);
        }

        private bool PromptAddDocumentation(FamilyItem fam)
        {
            if (fam == null || string.IsNullOrWhiteSpace(fam.Path))
                return false;

            EnsureDocumentationLoaded(fam);

            var dialog = new OpenFileDialog
            {
                Title = "Sélectionner des documents",
                Filter = "Tous les fichiers|*.*",
                Multiselect = true,
                CheckFileExists = true,
                CheckPathExists = true
            };

            try
            {
                var familyDirectory = System.IO.Path.GetDirectoryName(fam.Path);
                if (!string.IsNullOrWhiteSpace(familyDirectory) && Directory.Exists(familyDirectory))
                {
                    dialog.InitialDirectory = familyDirectory;
                }
            }
            catch
            {
                // ignore
            }

            if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0)
                return false;

            bool added = false;

            foreach (var selected in dialog.FileNames)
            {
                if (string.IsNullOrWhiteSpace(selected))
                    continue;

                var normalized = selected.Trim();

                if (fam.DocumentationLinks.Any(d => string.Equals(d.FilePath, normalized, StringComparison.OrdinalIgnoreCase)))
                    continue;

                fam.DocumentationLinks.Add(new FamilyDocumentLink
                {
                    FilePath = normalized,
                    Label = System.IO.Path.GetFileName(normalized),
                    Type = System.IO.Path.GetExtension(normalized)?.TrimStart('.')?.ToUpperInvariant()
                });
                added = true;
            }

            if (!added)
                return false;

            SortDocumentationLinks(fam);

            if (!PersistDocumentation(fam))
            {
                EnsureDocumentationLoaded(fam);
                return false;
            }

            return true;
        }

        private void SortDocumentationLinks(FamilyItem fam)
        {
            if (fam?.DocumentationLinks == null || fam.DocumentationLinks.Count <= 1)
                return;

            var existing = fam.DocumentationLinks.ToList();
            var ordered = existing
                .OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            bool alreadyOrdered = existing.SequenceEqual(ordered);
            if (alreadyOrdered)
                return;

            fam.DocumentationLinks.Clear();
            foreach (var link in ordered)
            {
                fam.DocumentationLinks.Add(link);
            }
        }

        private bool PersistDocumentation(FamilyItem fam)
        {
            if (fam == null || string.IsNullOrWhiteSpace(fam.Path))
                return false;

            var snapshot = fam.DocumentationLinks.Select(l => l.Clone()).ToList();

            lock (_documentationLock)
            {
                _documentationCache[fam.Path] = snapshot.Select(l => l.Clone()).ToList();
            }

            var docFile = GetDocumentationFilePath(fam.Path);
            if (string.IsNullOrWhiteSpace(docFile))
                return false;

            try
            {
                if (snapshot.Count == 0)
                {
                    if (File.Exists(docFile))
                        File.Delete(docFile);
                }
                else
                {
                    var payload = new FamilyDocumentationPayload { Documents = snapshot };
                    var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                    File.WriteAllText(docFile, json, Encoding.UTF8);
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Impossible d'enregistrer la documentation :\n" + ex.Message,
                    "Documentation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private void ShowDocumentationPicker(FamilyItem fam)
        {
            if (fam == null)
                return;

            EnsureDocumentationLoaded(fam);

            var dialog = new Window
            {
                Title = $"Documentation - {fam.Name}",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 420,
                MinHeight = 260
            };

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var listBox = new ListBox
            {
                ItemsSource = fam.DocumentationLinks,
                DisplayMemberPath = nameof(FamilyDocumentLink.DisplayName),
                MinWidth = 380,
                MinHeight = 220,
                SelectionMode = SelectionMode.Extended
            };
            dialog.Loaded += (_, __) => listBox.Focus();
            listBox.KeyDown += (s, args) =>
            {
                if (args.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    listBox.SelectAll();
                    args.Handled = true;
                }
            };
            Grid.SetRow(listBox, 0);
            root.Children.Add(listBox);

            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            Grid.SetRow(buttonsPanel, 1);
            root.Children.Add(buttonsPanel);

            List<FamilyDocumentLink> selectedLinks = null;
            bool openAll = false;

            var closeButton = new Button
            {
                Content = "Fermer",
                MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };
            closeButton.Click += (_, __) => dialog.DialogResult = false;

            var openSelectedButton = new Button
            {
                Content = "Ouvrir la sélection",
                MinWidth = 140,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
            };
            openSelectedButton.Click += (_, __) =>
            {
                if (listBox.SelectedItems.Count == 0)
                {
                    MessageBox.Show(dialog,
                        "Sélectionne au moins un document.",
                        "Documentation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                selectedLinks = listBox.SelectedItems.Cast<FamilyDocumentLink>().ToList();
                dialog.DialogResult = true;
            };

            listBox.MouseDoubleClick += (_, __) =>
            {
                if (listBox.SelectedItems.Count == 1)
                {
                    selectedLinks = new List<FamilyDocumentLink> { (FamilyDocumentLink)listBox.SelectedItem };
                    dialog.DialogResult = true;
                }
            };

            var openAllButton = new Button
            {
                Content = "Tout ouvrir",
                MinWidth = 110,
                Margin = new Thickness(8, 0, 0, 0)
            };
            openAllButton.Click += (_, __) =>
            {
                openAll = true;
                dialog.DialogResult = true;
            };

            var addButton = new Button
            {
                Content = "Ajouter...",
                MinWidth = 100
            };
            addButton.Click += (_, __) =>
            {
                if (PromptAddDocumentation(fam))
                {
                    listBox.Items.Refresh();
                }
            };

            buttonsPanel.Children.Add(addButton);
            buttonsPanel.Children.Add(openAllButton);
            buttonsPanel.Children.Add(openSelectedButton);
            buttonsPanel.Children.Add(closeButton);

            dialog.Content = root;

            if (dialog.ShowDialog() == true)
            {
                if (openAll)
                {
                    foreach (var link in fam.DocumentationLinks)
                    {
                        OpenDocument(link);
                    }
                }
                else if (selectedLinks != null)
                {
                    foreach (var link in selectedLinks)
                    {
                        OpenDocument(link);
                    }
                }
            }
        }

        private void OpenDocument(FamilyDocumentLink link)
        {
            if (link == null)
                return;

            var rawPath = link.FilePath;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                MessageBox.Show(this,
                    "Le chemin du document est vide.",
                    "Documentation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string expanded;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());
            }
            catch
            {
                expanded = rawPath.Trim();
            }

            bool isUrl = Uri.TryCreate(expanded, UriKind.Absolute, out var uri)
                         && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp);

            if (!isUrl && !File.Exists(expanded) && !Directory.Exists(expanded))
            {
                MessageBox.Show(this,
                    $"Le document \"{link.DisplayName}\" est introuvable :\n{expanded}",
                    "Documentation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = expanded,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Impossible d'ouvrir \"{link.DisplayName}\" :\n{ex.Message}",
                    "Documentation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        #endregion

        #region Actions (ouvrir/charger/recharger)

        private void ReloadFamily_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is FamilyItem fam)
            {
                FamilyBrowserCommand.ReloadFamilyHandlerInstance.FamilyPath = fam.Path;
                FamilyBrowserCommand.ReloadFamilyEventInstance.Raise();
            }
        }
        public class Preview3DHandler : IExternalEventHandler
        {
            public string FamilyPath { get; set; }

            public void Execute(UIApplication app)
                => Orbit3D.FamilyPreviewBridge.ShowPreview(app, FamilyPath);

            public string GetName() => "Preview3D";
        }

        private void FamilyItem_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            if (sender is Border b && b.DataContext is FamilyItem fam)
            {
                FamilyBrowserCommand.LoadFamilyHandlerInstance.FamilyPath = fam.Path;
                FamilyBrowserCommand.LoadFamilyEventInstance.Raise();
            }
        }

        private void OpenFamilyFile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is FamilyItem fam)
            {
                try
                {
                    Directory.CreateDirectory(workFolder);
                    string fileName = Path.GetFileName(fam.Path);
                    string targetPath = Path.Combine(workFolder, fileName);
                    File.Copy(fam.Path, targetPath, overwrite: true);
                    Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Impossible d’ouvrir la famille en mode travail :\n" + ex.Message,
                        "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        // ====== APERÇU 3D (bouton "3D" sur la tuile) ======
        private void OnPreview3DClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            FamilyItem fam = (sender as FrameworkElement)?.DataContext as FamilyItem;
            if (fam == null || string.IsNullOrWhiteSpace(fam.Path) || !File.Exists(fam.Path))
            {
                MessageBox.Show(this, "Fichier famille introuvable.", "Aperçu 3D",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Famille.FamilyBrowserCommand.Preview3DHandlerInstance.FamilyPath = fam.Path;
            Famille.FamilyBrowserCommand.Preview3DEventInstance.Raise();
        }

        private void AllFamiliesButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            if (_globalSearchMode)
            {
                _globalSearchMode = false;
                BeginPaging(ApplyInteractiveSorting(new List<FamilyItem>()));
                PagingStatusText.Text = "Tape au moins 2 caractères pour rechercher partout.";
                UpdateCount(0);
                RefreshTop8_UsageOnly();
                return;
            }

            allFamilies.Clear();
            displayedFamilies.Clear();
            FamilyListView.ItemsSource = displayedFamilies;
            UpdateCount(0);
            RefreshTop8_UsageOnly();
        }

        private void CollectionActionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            if (button.ContextMenu == null)
                return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        private void CollectionShareMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCollection == null || _selectedCollection.Paths.Count == 0)
            {
                MessageBox.Show(this,
                    "Sélectionne une collection non vide avant de la partager.",
                    "Collections",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (TrySaveCollectionAsJson(_selectedCollection,
                    "Exporter la collection",
                    "collection",
                    out var savedPath))
            {
                MessageBox.Show(this,
                    $"Collection exportée vers :\n{savedPath}",
                    "Export collection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void CollectionDownloadMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCollection == null || _selectedCollection.Paths.Count == 0)
            {
                MessageBox.Show(this,
                    "Sélectionne une collection non vide avant de télécharger les familles.",
                    "Collections",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Choisir le dossier de destination"
            };

            if (dialog.ShowDialog() != WinForms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
                return;

            var targetFolder = dialog.SelectedPath;
            try
            {
                Directory.CreateDirectory(targetFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Impossible de créer le dossier de destination :\n" + ex.Message,
                    "Téléchargement de la collection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
            int copied = 0;
            var missing = new List<string>();
            var errors = new List<string>();

            foreach (var path in _selectedCollection.Paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                try
                {
                    if (!File.Exists(path))
                    {
                        missing.Add(Path.GetFileName(path));
                        continue;
                    }

                    var fileName = Path.GetFileName(path);
                    var destination = GetUniqueDestinationPath(targetFolder, fileName);
                    if (destination == null)
                    {
                        errors.Add(fileName + " (chemin invalide)");
                        continue;
                    }

                    File.Copy(path, destination, overwrite: false);
                    copied++;
                }
                catch (Exception ex)
                {
                    errors.Add(Path.GetFileName(path) + " : " + ex.Message);
                }
            }

            var summary = new StringBuilder();
            summary.AppendLine($"Familles copiées : {copied}");
            summary.AppendLine($"Destination : {targetFolder}");

            if (missing.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Introuvables :");
                foreach (var name in missing.Take(10))
                    summary.AppendLine(" - " + name);
                if (missing.Count > 10)
                    summary.AppendLine($" - (+ {missing.Count - 10} autres)");
            }

            if (errors.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Erreurs de copie :");
                foreach (var err in errors.Take(10))
                    summary.AppendLine(" - " + err);
                if (errors.Count > 10)
                    summary.AppendLine($" - (+ {errors.Count - 10} autres)");
            }

            MessageBox.Show(this,
                summary.ToString(),
                "Téléchargement de la collection",
                MessageBoxButton.OK,
                errors.Count > 0 || missing.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private void CollectionLoad_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCollection == null || _selectedCollection.Paths.Count == 0) return;
            // Toujours en overwrite (handler dédié)
            FamilyBrowserCommand.LoadCollectionHandlerInstance.FamilyPaths = new List<string>(_selectedCollection.Paths);
            FamilyBrowserCommand.LoadCollectionEventInstance.Raise();
        }

        #endregion

        #region Vignettes (catalogue -> cache -> Shell -> placeholder)

        // Affiche l’image "catalogue" si elle existe (PNG/JPG).
        // Sinon : cache → Shell → placeholder. IMPORTANT : le Shell NE s’active PAS si une image "prévue" existe.
        // ====== VIGNETTES (catalogue -> cache -> Revit(type) -> Shell -> placeholder) ======
        private void LoadThumbnailForFamilyItem(FamilyItem fam)
        {
            if (fam == null || fam.Icon != null) return;

            Task.Run(async () =>
            {
                await _thumbGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    const int SIZE = 256;

                    // 0) cache mémoire
                    if (_bitmapCache.TryGetValue(fam.Path, out var memCached))
                    {
                        Dispatcher.Invoke(() => fam.Icon = memCached);
                        return;
                    }

                    // 1) image "catalogue" (PNG/JPG à côté ou miroir)
                    if (TryGetCatalogImagePath(fam.Path, out var plannedImagePath))
                    {
                        var bmp = LoadBitmapImage(plannedImagePath, SIZE);
                        if (bmp != null)
                        {
                            _bitmapCache[fam.Path] = bmp;
                            Dispatcher.Invoke(() => fam.Icon = bmp);
                        }
                        else
                        {
                            Dispatcher.Invoke(() => fam.Icon = CreateSolidPlaceholder(180, 180));
                        }
                        return;
                    }

                    // 2) cache disque
                    if (ThumbnailCache.TryGet(thumbCacheFolder, fam.Path, SIZE, out var cached) && File.Exists(cached))
                    {
                        var bmp = LoadBitmapImage(cached, SIZE);
                        if (bmp != null)
                        {
                            _bitmapCache[fam.Path] = bmp;
                            Dispatcher.Invoke(() => fam.Icon = bmp);
                            return;
                        }
                    }

                    // 3) *** Revit : Type Image -> preview de type ***
                    //    NB: FamilyThumbnailProvider bloque l'ouverture si upgrade nécessaire
                    if (useRevitPreview)
                    {
                        var revitThumb = await FamilyThumbnailProvider
                                            .RequestFromFamilyFileAsync(fam.Path, SIZE)
                                            .ConfigureAwait(false);

                        if (revitThumb != null)
                        {
                            try { ThumbnailCache.Save(thumbCacheFolder, fam.Path, SIZE, revitThumb); } catch { }
                            _bitmapCache[fam.Path] = revitThumb;
                            Dispatcher.Invoke(() => fam.Icon = revitThumb);
                            return;
                        }
                    }

                    // 4) Shell (3D) en dernier recours uniquement
                    if (useShellThumbs && ShellThumbnailProvider.TryGetThumbnail(fam.Path, SIZE, out var shellBmp))
                    {
                        try { ThumbnailCache.Save(thumbCacheFolder, fam.Path, SIZE, shellBmp); } catch { }
                        _bitmapCache[fam.Path] = shellBmp;
                        Dispatcher.Invoke(() => fam.Icon = shellBmp);
                        return;
                    }

                    // 5) placeholder
                    Dispatcher.Invoke(() => fam.Icon = CreateSolidPlaceholder(180, 180));
                }
                finally
                {
                    _thumbGate.Release();
                }
            });
        }

        private void LoadMetadataForFamilyItem(FamilyItem fam)
        {
            if (fam == null || string.IsNullOrEmpty(fam.Path)) return;

            FamilyPartAtomMeta cached;
            lock (_metadataLock)
            {
                if (_metadataCache.TryGetValue(fam.Path, out cached))
                {
                    UpdateMetadataBinding(fam, cached);
                    return;
                }

                if (_metadataPending.Contains(fam.Path))
                    return;

                _metadataPending.Add(fam.Path);
            }

            Task.Run(async () =>
            {
                FamilyPartAtomMeta meta = null;
                try
                {
                    meta = await FamilyMetadataProvider
                                    .RequestFastMetadataAsync(fam.Path)
                                    .ConfigureAwait(false);
                }
                catch
                {
                    meta = null;
                }

                lock (_metadataLock)
                {
                    _metadataPending.Remove(fam.Path);
                    _metadataCache[fam.Path] = meta;
                }

                UpdateMetadataBinding(fam, meta);
            });
        }

        private void UpdateMetadataBinding(FamilyItem fam, FamilyPartAtomMeta meta)
        {
            if (fam == null) return;

            void Apply()
            {
                fam.OmniClassNumber = string.IsNullOrWhiteSpace(meta?.OmniClassCode)
                    ? null
                    : meta.OmniClassCode.Trim();

                if (meta?.FileSizeBytes.HasValue == true)
                    fam.FileSizeBytes = meta.FileSizeBytes;

                fam.FileSizeText = FormatFileSize(meta?.FileSizeBytes ?? fam.FileSizeBytes);

                if (meta != null)
                {
                    fam.Category = string.IsNullOrWhiteSpace(meta.Category)
                        ? null
                        : meta.Category.Trim();

                    fam.RevitSavedVersion = string.IsNullOrWhiteSpace(meta.RevitSavedVersion)
                        ? null
                        : meta.RevitSavedVersion.Trim();

                    if (meta.UpdatedUtc.HasValue)
                    {
                        fam.LastUpdatedUtc = meta.UpdatedUtc.Value;
                        try
                        {
                            var local = TimeZoneInfo.ConvertTimeFromUtc(meta.UpdatedUtc.Value, TimeZoneInfo.Local);
                            fam.LastUpdatedText = local.ToString("g", CultureInfo.CurrentCulture);
                        }
                        catch
                        {
                            fam.LastUpdatedText = meta.UpdatedUtc.Value.ToString("u", CultureInfo.InvariantCulture);
                        }
                    }
                    else
                    {
                        fam.LastUpdatedUtc = null;
                        fam.LastUpdatedText = null;
                    }
                }
                else
                {
                    fam.LastUpdatedUtc = null;
                    fam.LastUpdatedText = null;
                    fam.RevitSavedVersion = null;
                    if (string.IsNullOrWhiteSpace(fam.Category))
                        fam.Category = null;
                }

                if (!_isResorting &&
                    (_sizeSortDescending ||
                     _dateSortDescending ||
                     !string.IsNullOrEmpty(ActiveCategoryFilter) ||
                     !string.IsNullOrEmpty(ActiveVersionFilter)))
                {
                    ResortCurrentView();
                }
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }


        private static string FormatFileSize(long? bytes)
        {
            if (!bytes.HasValue || bytes.Value <= 0)
                return null;

            double mb = bytes.Value / (1024d * 1024d);
            if (mb >= 1d)
                return string.Format(CultureInfo.CurrentCulture, "{0:N2} Mo", mb);

            double kb = bytes.Value / 1024d;
            if (kb >= 1d)
                return string.Format(CultureInfo.CurrentCulture, "{0:N0} Ko", kb);

            return string.Format(
                CultureInfo.CurrentCulture,
                "{0} octet{1}",
                bytes.Value,
                bytes.Value > 1 ? "s" : string.Empty);
        }


        // Détecte si une image "catalogue" est PRÉVUE (existe sur disque) — PNG ou JPG
        private bool TryGetCatalogImagePath(string familyPath, out string imgPath)
        {
            imgPath = null;
            try
            {
                string rel = GetRelativePath(familiesFolder, familyPath);
                string fileNameNoExt = Path.GetFileNameWithoutExtension(familyPath);

                string InImgMirror(string ext) => Path.ChangeExtension(Path.Combine(imagesFolder, rel), ext);
                string InImgFlat(string ext) => Path.Combine(imagesFolder, fileNameNoExt + ext);
                string NextToFamily(string ext) => Path.ChangeExtension(familyPath, ext);

                string[] candidates =
                {
            InImgMirror(".png"), NextToFamily(".png"), InImgFlat(".png"),
            InImgMirror(".jpg"), NextToFamily(".jpg"), InImgFlat(".jpg"),
        };

                foreach (var c in candidates)
                    if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    { imgPath = c; return true; }

                return false;
            }
            catch { return false; }
        }


        // Remplace l'ancienne version (avec FileStream) par celle-ci
        private static ImageSource LoadBitmapImage(string path, int decodeWidth)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path, UriKind.Absolute); // ← charge par URI (plus robuste sur réseau)
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.DecodePixelWidth = decodeWidth;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch
            {
                return null;
            }
        }


        private static ImageSource CreateSolidPlaceholder(int w, int h)
        {
            var pixels = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte c = (byte)((((x / 8) + (y / 8)) % 2 == 0) ? 230 : 210); // damier léger
                    pixels[i + 0] = c; // B
                    pixels[i + 1] = c; // G
                    pixels[i + 2] = c; // R
                    pixels[i + 3] = 255; // A
                }
            }
            var bmp = BitmapSource.Create(
                w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bmp.Freeze();
            return bmp;
        }

        #endregion

        #region Config / thèmes / chemins

        private bool PromptForFolders()
        {
            MessageBox.Show(this,
                "Le dossier par défaut n'a pas été trouvé.\n\n" +
                "1) Choisis d'abord le dossier qui contient tes familles Revit (.rfa).\n" +
                "2) Ensuite sélectionne le dossier des images (.png) avec le même nom que les familles.\n\n" +
                "Tu pourras modifier ces chemins plus tard depuis l'onglet Paramètres.",
                "Chemins introuvables",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            if (!TrySelectFolders(out var selectedFamilies, out var selectedImages))
                return false;

            ApplySelectedFolders(selectedFamilies, selectedImages, showSuccessMessage: true);
            return true;
        }

        private bool TrySelectFolders(out string selectedFamilies, out string selectedImages)
        {
            selectedFamilies = null;
            selectedImages = null;

            var famDialog = new WinForms.FolderBrowserDialog
            {
                Description = "Choisis le dossier avec les fichiers .rfa puis clique sur OK.",
                SelectedPath = Directory.Exists(familiesFolder) ? familiesFolder : string.Empty
            };
            if (famDialog.ShowDialog() != WinForms.DialogResult.OK)
                return false;

            selectedFamilies = famDialog.SelectedPath;

            var imgDialog = new WinForms.FolderBrowserDialog
            {
                Description = "Choisis le dossier avec les images (.png) nommées comme les fichiers .rfa, puis clique sur OK.",
                SelectedPath = Directory.Exists(imagesFolder) ? imagesFolder : selectedFamilies
            };
            if (imgDialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                selectedImages = imgDialog.SelectedPath;
            }
            else
            {
                MessageBox.Show(this,
                    "Aucun dossier d'images choisi. Les vignettes ne seront pas affichées.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                selectedImages = selectedFamilies;
            }

            return true;
        }

        private void ApplySelectedFolders(string newFamiliesFolder, string newImagesFolder, bool showSuccessMessage)
        {
            familiesFolder = newFamiliesFolder;
            imagesFolder = newImagesFolder;
            rootFolderPath = familiesFolder;

            SavePaths();
            NotifyPropertyChanged(nameof(RootFolderName));

            CatalogImageResolver.Initialize(familiesFolder, imagesFolder);
            ImageResolver.ClearCaches();
            _bitmapCache.Clear();

            _index?.Dispose();
            _index = new FamilyIndexService(familiesFolder, imagesFolder);
            _index.IndexUpdated += OnIndexUpdated;
            _ = _index.StartAsync();

            LoadFolderTree();
            _globalSearchMode = false;
            if (SearchBox != null) SearchBox.Text = string.Empty;
            if (PlaceholderText != null) PlaceholderText.Visibility = Visibility.Visible;

            if (showSuccessMessage)
            {
                MessageBox.Show(this,
                    "Les dossiers sont enregistrés. Tu peux les modifier à tout moment dans l'onglet Paramètres.",
                    "Chemins enregistrés", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void LoadSavedPaths()
        {
            try
            {
                if (File.Exists(pathsFile))
                {
                    var json = File.ReadAllText(pathsFile);
                    var cfg = JsonConvert.DeserializeObject<FolderSettings>(json);
                    if (cfg != null)
                    {
                        familiesFolder = cfg.FamiliesFolder;
                        imagesFolder = cfg.ImagesFolder;
                        rootFolderPath = familiesFolder;
                        NotifyPropertyChanged(nameof(RootFolderName));
                    }
                }
            }
            catch { }
        }

        private void SavePaths()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pathsFile));
                var cfg = new FolderSettings { FamiliesFolder = familiesFolder, ImagesFolder = imagesFolder };
                var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
                File.WriteAllText(pathsFile, json);
            }
            catch { }
        }

        private class FolderSettings
        {
            public string FamiliesFolder { get; set; }
            public string ImagesFolder { get; set; }
        }

        private void EnsureFilesExist()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(favoritesFile));
                if (!File.Exists(favoritesFile)) File.WriteAllText(favoritesFile, "");
                Directory.CreateDirectory(Path.GetDirectoryName(configFile));
                if (!File.Exists(configFile)) File.WriteAllText(configFile, "");
                Directory.CreateDirectory(thumbCacheFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erreur création fichiers config : " + ex.Message,
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetAlwaysOnTopState(bool isOn)
        {
            if (AlwaysOnTopSwitch != null)
                AlwaysOnTopSwitch.IsOn = isOn;



            this.Topmost = isOn;
        }

        private bool GetAlwaysOnTopState()
        {
            return AlwaysOnTopSwitch?.IsOn == true;
        }


        private void LoadConfig()
        {
            string top = "#FFF2F2F2", bottom = "#FFFFFFFF", panel = "#F0F0F0",
                   treeBg = "#F0F0F0", itemsBg = "Transparent", tabBg = "Transparent";
            bool dark = false;
            bool showTop8 = false;
            bool alwaysOnTop = false;
            bool detailedView = false;

            if (File.Exists(configFile))
            {
                foreach (var line in File.ReadAllLines(configFile))
                {
                    if (line.StartsWith("TopColor=", StringComparison.OrdinalIgnoreCase)) top = line.Substring("TopColor=".Length);
                    else if (line.StartsWith("BottomColor=", StringComparison.OrdinalIgnoreCase)) bottom = line.Substring("BottomColor=".Length);
                    else if (line.StartsWith("PanelBackground=", StringComparison.OrdinalIgnoreCase)) panel = line.Substring("PanelBackground=".Length);
                    else if (line.StartsWith("TreeViewBackground=", StringComparison.OrdinalIgnoreCase)) treeBg = line.Substring("TreeViewBackground=".Length);
                    else if (line.StartsWith("ItemsBackground=", StringComparison.OrdinalIgnoreCase)) itemsBg = line.Substring("ItemsBackground=".Length);
                    else if (line.StartsWith("TabBackground=", StringComparison.OrdinalIgnoreCase)) tabBg = line.Substring("TabBackground=".Length);
                    else if (line.StartsWith("DarkMode=", StringComparison.OrdinalIgnoreCase)) bool.TryParse(line.Substring("DarkMode=".Length), out dark);
                    else if (line.StartsWith("ShowTop8=", StringComparison.OrdinalIgnoreCase)) bool.TryParse(line.Substring("ShowTop8=".Length), out showTop8);
                    else if (line.StartsWith("AlwaysOnTop=", StringComparison.OrdinalIgnoreCase)) bool.TryParse(line.Substring("AlwaysOnTop=".Length), out alwaysOnTop);
                    else if (line.StartsWith("DetailedView=", StringComparison.OrdinalIgnoreCase)) bool.TryParse(line.Substring("DetailedView=".Length), out detailedView);
                    else if (line.StartsWith("UseShellThumbs=", StringComparison.OrdinalIgnoreCase)) bool.TryParse(line.Substring("UseShellThumbs=".Length), out useShellThumbs);
                    else if (line.StartsWith("UseRevitPreview=", StringComparison.OrdinalIgnoreCase)) bool.TryParse(line.Substring("UseRevitPreview=".Length), out useRevitPreview);
                }
            }

            TopColorPicker.SelectedColor = ColorFromHex(top);
            BottomColorPicker.SelectedColor = ColorFromHex(bottom);
            PanelBackgroundPicker.SelectedColor = ColorFromHex(panel);
            TreeViewBackgroundPicker.SelectedColor = ColorFromHex(treeBg);
            ItemsBackgroundPicker.SelectedColor = ColorFromHex(itemsBg);
            TabBackgroundPicker.SelectedColor = ColorFromHex(tabBg);
            if (DarkModeSwitch != null) DarkModeSwitch.IsOn = dark; if (ShowTop8CheckBox != null) ShowTop8CheckBox.IsChecked = showTop8;
            if (AlwaysOnTopSwitch != null) AlwaysOnTopSwitch.IsOn = alwaysOnTop;
            detailedViewMode = detailedView;
            if (DetailedViewCheckBox != null) DetailedViewCheckBox.IsChecked = detailedViewMode;

            UpdateFamilyListViewMode();
        }

        private void SaveConfig_Click(object s, RoutedEventArgs e)
        {
            var lines = new[]
            {
                "TopColor="    + ColorToHex(TopColorPicker.SelectedColor ?? Colors.White),
                "BottomColor=" + ColorToHex(BottomColorPicker.SelectedColor ?? Colors.White),
                "PanelBackground="    + ColorToHex(PanelBackgroundPicker.SelectedColor ?? Colors.Transparent),
                "TreeViewBackground=" + ColorToHex(TreeViewBackgroundPicker.SelectedColor ?? Colors.Transparent),
                "ItemsBackground="    + (ItemsBackgroundPicker.SelectedColor == Colors.Transparent ? "Transparent" : ColorToHex(ItemsBackgroundPicker.SelectedColor.Value)),
                "TabBackground="      + (TabBackgroundPicker.SelectedColor   == Colors.Transparent ? "Transparent" : ColorToHex(TabBackgroundPicker.SelectedColor.Value)),
                "DarkMode="   + ((DarkModeSwitch?.IsOn == true) ? "true" : "false"),
                "ShowTop8="   + ((ShowTop8CheckBox?.IsChecked == true) ? "true" : "false"),
                "AlwaysOnTop="+ ((AlwaysOnTopSwitch?.IsOn == true) ? "true" : "false"),
                "DetailedView=" + ((DetailedViewCheckBox?.IsChecked == true) ? "true" : "false"),
                "UseShellThumbs="  + (useShellThumbs  ? "true" : "false"),
                "UseRevitPreview=" + (useRevitPreview ? "true" : "false"),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(configFile));
            File.WriteAllLines(configFile, lines);
            MessageBox.Show("Configuration enregistrée. Redémarrez pour appliquer.");
        }

        private void ResetConfig_Click(object s, RoutedEventArgs e)
        {
            TopColorPicker.SelectedColor = ColorFromHex("#FFF2F2F2");
            BottomColorPicker.SelectedColor = ColorFromHex("#FFFFFFFF");
            PanelBackgroundPicker.SelectedColor = ColorFromHex("#F0F0F0");
            TreeViewBackgroundPicker.SelectedColor = ColorFromHex("#F0F0F0");
            ItemsBackgroundPicker.SelectedColor = Colors.Transparent;
            TabBackgroundPicker.SelectedColor = Colors.Transparent;
            if (DarkModeSwitch != null) DarkModeSwitch.IsOn = false;
            if (ShowTop8CheckBox != null) ShowTop8CheckBox.IsChecked = false;
            if (AlwaysOnTopSwitch != null) AlwaysOnTopSwitch.IsOn = false;
            if (DetailedViewCheckBox != null) DetailedViewCheckBox.IsChecked = false;
            UpdateTheme();
            SaveConfig_Click(s, e);
        }
        private void ChangePaths_Click(object sender, RoutedEventArgs e)
        {
            if (!TrySelectFolders(out var selectedFamilies, out var selectedImages))
                return;

            ApplySelectedFolders(selectedFamilies, selectedImages, showSuccessMessage: true);
        }
        private void DarkModeSwitch_Toggled(object sender, RoutedPropertyChangedEventArgs<bool> e) => UpdateTheme();

        private void ApplyColors_Click(object sender, RoutedEventArgs e) => UpdateTheme();

        private void UpdateTheme()
        {
            bool isDark = (DarkModeSwitch?.IsOn == true);

            if (isDark)
            {
                Resources["BackgroundGradient"] = new LinearGradientBrush(
                    new GradientStopCollection {
                        new GradientStop(Colors.Black, 0),
                        new GradientStop(Colors.DarkGray, 1)
                    }, new Point(0, 0), new Point(0, 1));
                Resources["PanelBackground"] = new SolidColorBrush(Color.FromRgb(51, 51, 51));
                Resources["TreeViewBackground"] = new SolidColorBrush(Color.FromRgb(51, 51, 51));
                Resources["ItemsBackground"] = new SolidColorBrush(Color.FromRgb(34, 34, 34));
                Resources["TabBackground"] = new SolidColorBrush(Color.FromRgb(68, 68, 68));
                Resources["ImageBackground"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                Resources["PrimaryText"] = new SolidColorBrush(Colors.White);
            }
            else
            {
                var top = TopColorPicker.SelectedColor ?? Colors.White;
                var bot = BottomColorPicker.SelectedColor ?? Colors.White;
                Resources["BackgroundGradient"] = new LinearGradientBrush(
                    new GradientStopCollection {
                        new GradientStop(top, 0),
                        new GradientStop(bot, 1)
                    }, new Point(0, 0), new Point(0, 1));
                var panel = PanelBackgroundPicker.SelectedColor ?? Colors.White;
                var treeBg = TreeViewBackgroundPicker.SelectedColor ?? Colors.White;
                var itemsBg = ItemsBackgroundPicker.SelectedColor ?? Colors.Transparent;
                var tabBg = TabBackgroundPicker.SelectedColor ?? Colors.Transparent;
                Resources["PanelBackground"] = new SolidColorBrush(panel);
                Resources["TreeViewBackground"] = new SolidColorBrush(treeBg);
                Resources["ItemsBackground"] = new SolidColorBrush(itemsBg);
                Resources["TabBackground"] = new SolidColorBrush(tabBg);
                Resources["ImageBackground"] = new SolidColorBrush(Colors.Transparent);
                Resources["PrimaryText"] = new SolidColorBrush(Colors.Black);
            }
        }

        private void DetailedViewCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool newMode = DetailedViewCheckBox?.IsChecked == true;
            bool wasDateSortActive = _dateSortDescending;

            detailedViewMode = newMode;
            UpdateFamilyListViewMode();

            if (!newMode && wasDateSortActive)
            {
                _dateSortDescending = false;
                NotifyPropertyChanged(nameof(IsDateSortActive));
                ApplyFilters();
            }
        }

        private void UpdateFamilyListViewMode()
        {
            if (FamilyListView == null) return;

            var templateKey = detailedViewMode ? "FamilyItemDetailedTemplate" : "FamilyItemTemplate";
            if (TryFindResource(templateKey) is DataTemplate template)
            {
                FamilyListView.ItemTemplate = template;
            }

            var panelKey = detailedViewMode ? "FamilyListStackPanel" : "FamilyListWrapPanel";
            if (TryFindResource(panelKey) is ItemsPanelTemplate panel)
            {
                FamilyListView.ItemsPanel = panel;
            }

            var currentItems = displayedFamilies;
            FamilyListView.ItemsSource = null;
            FamilyListView.ItemsSource = currentItems;
            FamilyListView.Items.Refresh();

            if (detailedViewMode && currentItems != null)
            {
                foreach (var item in currentItems)
                    LoadMetadataForFamilyItem(item);
            }
        }

        public static string GetRelativePath(string relativeTo, string path)
        {
            var fromUri = new Uri(relativeTo.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? relativeTo : relativeTo + Path.DirectorySeparatorChar);
            var toUri = new Uri(path);
            var relUri = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(relUri.ToString())
                      .Replace('/', Path.DirectorySeparatorChar);
        }

        private string ColorToHex(Color c)
            => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private Color ColorFromHex(string hex)
            => hex.Equals("Transparent", StringComparison.OrdinalIgnoreCase)
               ? Colors.Transparent
               : (Color)ColorConverter.ConvertFromString(hex);

        private static string StripDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark) sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        #endregion

        #region Collections

        private sealed class CollectionExportPayload
        {
            public string Name { get; set; }
            public List<string> Paths { get; set; } = new();
        }

        private void LoadCollections()
        {
            _collections = new ObservableCollection<Collection>(CollectionStore.Load());
            CollectionCombo.ItemsSource = _collections;
            if (CollectionCombo.SelectedIndex < 0 && _collections.Count > 0)
                CollectionCombo.SelectedIndex = 0;
        }

        private void EnsureFavoritesCollection()
        {
            var fav = _collections.FirstOrDefault(c => c.Id == FavoritesCollectionId)
                   ?? _collections.FirstOrDefault(c => string.Equals(c.Name, FavoritesCollectionName, StringComparison.OrdinalIgnoreCase));

            if (fav == null)
            {
                fav = new Collection { Id = FavoritesCollectionId, Name = FavoritesCollectionName };
                _collections.Insert(0, fav);
            }
            else
            {
                fav.Id = FavoritesCollectionId; // normalise
                fav.Name = FavoritesCollectionName; // fige le nom
                _collections.Remove(fav);
                _collections.Insert(0, fav);
            }

            SaveCollections();

            // sélectionner Favoris par défaut
            CollectionCombo.SelectedItem = fav;
            _selectedCollection = fav;
            RefreshCollectionContent();
        }

        private Collection GetFavoritesCollection()
            => _collections.First(c => c.Id == FavoritesCollectionId);

        private void SaveCollections()
        {
            CollectionStore.Save(new List<Collection>(_collections));
        }

        private void CollectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedCollection = CollectionCombo.SelectedItem as Collection;
            RefreshCollectionContent();
        }

        private void RefreshCollectionContent()
        {
            var items = new List<FamilyItem>();
            if (_selectedCollection != null)
            {
                foreach (var p in _selectedCollection.Paths)
                    if (File.Exists(p)) items.Add(CreateFamilyItemFromPath(p));
            }
            CollectionListView.ItemsSource = items;
            foreach (var it in items) LoadThumbnailForFamilyItem(it);
            CollectionCountText.Text = items.Count.ToString();
        }

        private void CollectionNew_Click(object sender, RoutedEventArgs e)
        {
            var name = Microsoft.VisualBasic.Interaction.InputBox("Nom de la collection :", "Nouvelle collection", "Nouvelle collection");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (string.Equals(name, FavoritesCollectionName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Le nom « Favoris » est réservé.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var c = new Collection { Name = name };
            _collections.Add(c);
            SaveCollections();
            CollectionCombo.SelectedItem = c;
        }

        private void CollectionRename_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCollection == null) return;
            if (_selectedCollection.Id == FavoritesCollectionId)
            {
                MessageBox.Show("La collection « Favoris » ne peut pas être renommée.", "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var name = Microsoft.VisualBasic.Interaction.InputBox("Nouveau nom :", "Renommer la collection", _selectedCollection.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (string.Equals(name, FavoritesCollectionName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Le nom « Favoris » est réservé.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _selectedCollection.Name = name;
            SaveCollections();

            var i = CollectionCombo.SelectedIndex;
            CollectionCombo.ItemsSource = null;
            CollectionCombo.ItemsSource = _collections;
            CollectionCombo.SelectedIndex = i;
        }

        private void CollectionDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCollection == null) return;
            if (_selectedCollection.Id == FavoritesCollectionId)
            {
                MessageBox.Show("La collection « Favoris » ne peut pas être supprimée.", "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show($"Supprimer la collection « {_selectedCollection.Name} » ?",
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var idx = CollectionCombo.SelectedIndex;
            _collections.Remove(_selectedCollection);
            SaveCollections();

            if (_collections.Count == 0) EnsureFavoritesCollection();
            else
            {
                CollectionCombo.ItemsSource = _collections;
                CollectionCombo.SelectedIndex = Math.Max(0, Math.Min(idx - 1, _collections.Count - 1));
            }
        }

        // Ajout express -> collection active
        private void AddToActiveCollection_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is not FamilyItem fam) return;

            if (_selectedCollection == null)
                EnsureFavoritesCollection();

            if (!_selectedCollection.Paths.Any(p => p.Equals(fam.Path, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedCollection.Paths.Add(fam.Path);
                SaveCollections();
                RefreshCollectionContent();

                if (_selectedCollection.Id == FavoritesCollectionId)
                {
                    fam.IsFavorite = true;
                    ExportFavoritesCollectionToTxt();
                }
            }
        }

        // 🗑 : retirer un élément de la collection sélectionnée
        private void RemoveFromCollection_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedCollection == null) return;
            if ((sender as Button)?.DataContext is not FamilyItem fam) return;

            _selectedCollection.Paths.RemoveAll(p => p.Equals(fam.Path, StringComparison.OrdinalIgnoreCase));
            SaveCollections();
            RefreshCollectionContent();

            if (_selectedCollection.Id == FavoritesCollectionId)
            {
                // retire l'étoile dans les vues
                foreach (var it in displayedFamilies)
                    if (string.Equals(it.Path, fam.Path, StringComparison.OrdinalIgnoreCase))
                        it.IsFavorite = false;

                foreach (var it in allFamilies)
                    if (string.Equals(it.Path, fam.Path, StringComparison.OrdinalIgnoreCase))
                        it.IsFavorite = false;

                ExportFavoritesCollectionToTxt();
            }
        }

        private void AlwaysOnTopSwitch_Toggled(object sender, RoutedPropertyChangedEventArgs<bool> e)
        {
            this.Topmost = AlwaysOnTopSwitch?.IsOn == true;
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, e.OriginalSource)) return;
            UpdateGhostFollowMode();
        }

        private void UpdateGhostFollowMode()
        {
            if (AlwaysOnTopSwitch == null) return;
            bool isSettingsTabVisible = SettingsTabItem?.IsSelected == true;
            AlwaysOnTopSwitch.EyeFollowGlobal = isSettingsTabVisible;
        }

        private void OnPreviewVisibilityChanged(object sender, bool isVisible)
        {
            if (AlwaysOnTopSwitch == null) return;

            void Apply()
            {
                bool shouldSuspend = isVisible && _revitMajorVersion >= 2025;
                AlwaysOnTopSwitch.SetTrackingSuspended(shouldSuspend);
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)Apply);
            }
            else
            {
                Apply();
            }
        }

        #endregion

        #region Modèle

        private static bool TryParseRevitMajorVersion(string version, out int major)
        {
            major = 0;
            if (string.IsNullOrWhiteSpace(version)) return false;

            int i = 0;
            while (i < version.Length && !char.IsDigit(version[i])) i++;
            if (i == version.Length) return false;

            int start = i;
            while (i < version.Length && char.IsDigit(version[i])) i++;
            if (start == i) return false;

            var digits = version.Substring(start, i - start);
            return int.TryParse(digits, out major);
        }

        private long? TryGetFileSize(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : (long?)null;
            }
            catch
            {
                return null;
            }
        }

        private static string MakeSafeFileName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = fallback;

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            }

            var result = sb.ToString().Trim();
            if (string.IsNullOrEmpty(result))
                result = fallback;

            return result;
        }

        private static string GetUniqueDestinationPath(string folder, string fileName)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return null;

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "fichier.rfa";

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "fichier";

            var candidate = Path.Combine(folder, baseName + extension);
            int counter = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(folder, $"{baseName} ({counter}){extension}");
                counter++;
            }

            return candidate;
        }

        private FamilyItem CreateFamilyItemFromPath(string path)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var size = TryGetFileSize(path);
            return new FamilyItem
            {
                Name = name,
                Path = path,
                Icon = null,
                NormalizedName = StripDiacritics(name).ToLowerInvariant(),
                FileSizeBytes = size,
                FileSizeText = FormatFileSize(size),
                DocumentationAvailable = HasDocumentationFile(path)
            };
        }

        #endregion
    }

    // ======================= FamilyItem =======================

    public class FamilyItem : INotifyPropertyChanged
    {
        public FamilyItem()
        {
            DocumentationLinks = new ObservableCollection<FamilyDocumentLink>();
            DocumentationLinks.CollectionChanged += DocumentationLinks_CollectionChanged;
        }

        public string Name { get; set; }
        public string Path { get; set; }
        private string _category;
        public string Category
        {
            get => _category;
            set { if (_category != value) { _category = value; OnPropertyChanged(nameof(Category)); } }
        }
        public string NormalizedName { get; set; }

        private string _omniClassNumber;
        public string OmniClassNumber
        {
            get => _omniClassNumber;
            set { if (_omniClassNumber != value) { _omniClassNumber = value; OnPropertyChanged(nameof(OmniClassNumber)); } }
        }

        private ImageSource _icon;
        public ImageSource Icon
        {
            get => _icon;
            set { if (_icon != value) { _icon = value; OnPropertyChanged(nameof(Icon)); } }
        }

        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set { if (_isFavorite != value) { _isFavorite = value; OnPropertyChanged(nameof(IsFavorite)); } }
        }

        private string _revitSavedVersion;
        public string RevitSavedVersion
        {
            get => _revitSavedVersion;
            set { if (_revitSavedVersion != value) { _revitSavedVersion = value; OnPropertyChanged(nameof(RevitSavedVersion)); } }
        }

        private DateTime? _lastUpdatedUtc;
        public DateTime? LastUpdatedUtc
        {
            get => _lastUpdatedUtc;
            set { if (_lastUpdatedUtc != value) { _lastUpdatedUtc = value; OnPropertyChanged(nameof(LastUpdatedUtc)); } }
        }

        private string _lastUpdatedText;
        public string LastUpdatedText
        {
            get => _lastUpdatedText;
            set { if (_lastUpdatedText != value) { _lastUpdatedText = value; OnPropertyChanged(nameof(LastUpdatedText)); } }
        }

        private string _fileSizeText;
        public string FileSizeText
        {
            get => _fileSizeText;
            set { if (_fileSizeText != value) { _fileSizeText = value; OnPropertyChanged(nameof(FileSizeText)); } }
        }
        private long? _fileSizeBytes;
        public long? FileSizeBytes
        {
            get => _fileSizeBytes;
            set { if (_fileSizeBytes != value) { _fileSizeBytes = value; OnPropertyChanged(nameof(FileSizeBytes)); } }
        }

        private bool _documentationAvailable;
        public bool DocumentationAvailable
        {
            get => _documentationAvailable;
            set { if (_documentationAvailable != value) { _documentationAvailable = value; OnPropertyChanged(nameof(DocumentationAvailable)); } }
        }

        public ObservableCollection<FamilyDocumentLink> DocumentationLinks { get; }

        public bool HasDocumentation => DocumentationLinks?.Count > 0;

        private void DocumentationLinks_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(DocumentationLinks));
            OnPropertyChanged(nameof(HasDocumentation));
            DocumentationAvailable = DocumentationLinks?.Count > 0;
        }



        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public class FamilyDocumentLink
    {
        public string Label { get; set; }
        public string FilePath { get; set; }
        public string Type { get; set; }

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Label))
                    return Label;
                if (string.IsNullOrWhiteSpace(FilePath))
                    return string.Empty;
                try
                {
                    return System.IO.Path.GetFileName(FilePath);
                }
                catch
                {
                    return FilePath;
                }
            }
        }

        public FamilyDocumentLink Clone()
            => new FamilyDocumentLink
            {
                Label = Label,
                FilePath = FilePath,
                Type = Type
            };

        public override string ToString() => DisplayName;
    }

    internal class FamilyDocumentationPayload
    {
        public List<FamilyDocumentLink> Documents { get; set; } = new();
    }
}
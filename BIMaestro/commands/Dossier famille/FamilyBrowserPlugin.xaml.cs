using Autodesk.Revit.UI;
using BIMaestro.UI;
using Famille.Orbit3D;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace Famille
{
    public partial class FamilyBrowserWindow : Window, INotifyPropertyChanged
    {
        // ===== Constantes =====
        private const string FavoritesCollectionId = "builtin_favoris";
        private const string FavoritesCollectionName = "Favoris";

        // ===== Chemins =====
        private string rootFolderPath = @"P:\0-Boîte à outils Revit\0-Bibliothèque\A-Famille Revi";
        private string familiesFolder = @"P:\0-Boîte à outils Revit\0-Bibliothèque\A-Famille Revi";
        private string imagesFolder = @"P:\0-Boîte à outils Revit\0-Bibliothèque\B-Famille Revit Imag";

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

        // ===== Données UI =====
        private List<FamilyItem> allFamilies = new();
        private List<FamilyItem> displayedFamilies = new();
        private string currentFolderPath;

        public string RootFolderName => System.IO.Path.GetFileName(rootFolderPath);
        public string ActiveCategoryFilter => _activeCategoryFilter;
        public string ActiveVersionFilter => _activeVersionFilter;
        public bool IsSizeSortActive => _isSizeSortActive;

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

        private string _activeCategoryFilter;
        private string _activeVersionFilter;
        private bool _isSizeSortActive;
        private bool _pendingSizeResort;

        // ===== Collections =====
        private ObservableCollection<Collection> _collections = new();
        private Collection _selectedCollection;

        public FamilyBrowserWindow()
        {
            InitializeComponent();
            DataContext = this;

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

            if (!string.IsNullOrWhiteSpace(SearchBox.Text) || _globalSearchMode)
            {
                _globalSearchMode = false;
                SearchBox.Text = "";
                Keyboard.ClearFocus();
            }

            currentFolderPath = tv.Tag.ToString();
            LoadFamilies(currentFolderPath, recursive: false);

            BeginPaging(allFamilies);

            TopFamiliesView.Visibility = Visibility.Collapsed;
            TopSeparator.Visibility = Visibility.Collapsed;
        }

        private void LoadFamilies(string path, bool recursive)
        {
            ClearChipFilters();

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

            for (int i = 0; i < allFamilies.Count; i++)
                allFamilies[i].NaturalOrder = i;

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

        #region Pastilles interactives

        private void CategoryChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not FamilyItem fam) return;

            var target = fam.Category;
            if (EqualsIgnoreCase(_activeCategoryFilter, target))
                SetActiveCategoryFilter(null);
            else
                SetActiveCategoryFilter(target);

            e.Handled = true;
        }

        private void VersionChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not FamilyItem fam) return;

            var target = fam.RevitSavedVersion;
            if (EqualsIgnoreCase(_activeVersionFilter, target))
                SetActiveVersionFilter(null);
            else
                SetActiveVersionFilter(target);

            e.Handled = true;
        }

        private void SizeChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not FamilyItem fam) return;

            // assure la récupération du poids même si les métadonnées ne sont pas encore là
            if (!_isSizeSortActive)
                _ = GetFileSizeValue(fam);

            SetSizeSortActive(!_isSizeSortActive, applySort: true);
            e.Handled = true;
        }

        private void ClearChipFilters()
        {
            SetActiveCategoryFilter(null);
            SetActiveVersionFilter(null);
            SetSizeSortActive(false, applySort: false);
        }

        private void SetActiveCategoryFilter(string category)
        {
            string normalized = NormalizeFilterValue(category);
            if (EqualsIgnoreCase(_activeCategoryFilter, normalized))
                return;

            _activeCategoryFilter = normalized;
            OnPropertyChanged(nameof(ActiveCategoryFilter));
        }

        private void SetActiveVersionFilter(string version)
        {
            string normalized = NormalizeFilterValue(version);
            if (EqualsIgnoreCase(_activeVersionFilter, normalized))
                return;

            _activeVersionFilter = normalized;
            OnPropertyChanged(nameof(ActiveVersionFilter));
        }

        private void SetSizeSortActive(bool active, bool applySort)
        {
            if (_isSizeSortActive == active)
            {
                if (applySort) ApplySizeSort();
                return;
            }

            _isSizeSortActive = active;
            OnPropertyChanged(nameof(IsSizeSortActive));
            if (!active)
                _pendingSizeResort = false;

            if (applySort)
                ApplySizeSort();
        }

        private static bool EqualsIgnoreCase(string left, string right)
        {
            if (left == null && right == null) return true;
            if (left == null || right == null) return false;
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFilterValue(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private void ApplySizeSort()
        {
            if (_currentResult == null) return;

            var source = _currentResult.ToList();
            List<FamilyItem> ordered;

            if (_isSizeSortActive)
            {
                ordered = source
                    .OrderByDescending(GetFileSizeValue)
                    .ThenBy(f => f.NaturalOrder)
                    .ToList();
            }
            else
            {
                ordered = source
                    .OrderBy(f => f.NaturalOrder)
                    .ToList();
            }

            BeginPaging(ordered);
        }

        private long GetFileSizeValue(FamilyItem fam)
        {
            if (fam == null) return long.MinValue;

            if (fam.FileSizeBytes.HasValue)
                return fam.FileSizeBytes.Value;

            lock (_metadataLock)
            {
                if (_metadataCache.TryGetValue(fam.Path, out var meta) && meta?.FileSizeBytes.HasValue == true)
                {
                    fam.FileSizeBytes = meta.FileSizeBytes;
                    return meta.FileSizeBytes.Value;
                }
            }

            try
            {
                var info = new FileInfo(fam.Path);
                fam.FileSizeBytes = info.Length;
                return info.Length;
            }
            catch
            {
                return long.MinValue;
            }
        }

        private void RequestSizeResort()
        {
            if (!_isSizeSortActive || _pendingSizeResort) return;

            _pendingSizeResort = true;
            Dispatcher.InvokeAsync(() =>
            {
                _pendingSizeResort = false;
                ApplySizeSort();
            }, DispatcherPriority.Background);
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
                BeginPaging(allFamilies);
            }
        }

        private void SearchBox_TextChanged(object s, TextChangedEventArgs e)
        {
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
                    BeginPaging(new List<FamilyItem>());
                    PagingStatusText.Visibility = Visibility.Visible;
                    PagingStatusText.Text = "Index en cours de préparation…";
                    UpdateCount(0);
                    return;
                }

                if (txt.Length < 2)
                {
                    BeginPaging(new List<FamilyItem>());
                    PagingStatusText.Visibility = Visibility.Visible;
                    PagingStatusText.Text = "Tape au moins 2 caractères pour rechercher partout.";
                    UpdateCount(0);
                    return;
                }

                var hits = _index.Search(txt, max: 8000);
                var items = hits.Select((e, idx) => new FamilyItem
                {
                    Name = e.Name,
                    Path = e.Path,
                    Category = e.Category,
                    NormalizedName = e.NormalizedName,
                    NaturalOrder = idx
                }).ToList();

                BeginPaging(items);
                return;
            }
            else
            {
                IEnumerable<FamilyItem> baseSet = allFamilies;
                if (!string.IsNullOrEmpty(txt))
                    baseSet = baseSet.Where(f => f.NormalizedName.Contains(txt));

                BeginPaging(baseSet.ToList());
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
                BeginPaging(new List<FamilyItem>());
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

                fam.FileSizeText = FormatFileSize(meta?.FileSizeBytes);
                fam.FileSizeBytes = meta?.FileSizeBytes;

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
                        fam.LastUpdatedText = null;
                    }
                }
                else
                {
                    fam.LastUpdatedText = null;
                    fam.RevitSavedVersion = null;
                    if (string.IsNullOrWhiteSpace(fam.Category))
                        fam.Category = null;
                    fam.FileSizeText = null;
                    fam.FileSizeBytes = null;
                }

                if (meta?.FileSizeBytes.HasValue == true)
                    RequestSizeResort();
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
                "Ces chemins seront enregistrés pour ne plus te le demander.",
                "Chemins introuvables",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            var famDialog = new WinForms.FolderBrowserDialog { Description = "Choisis le dossier avec les fichiers .rfa puis clique sur OK." };
            if (famDialog.ShowDialog() != WinForms.DialogResult.OK)
                return false;
            familiesFolder = famDialog.SelectedPath;
            rootFolderPath = familiesFolder;

            var imgDialog = new WinForms.FolderBrowserDialog { Description = "Choisis le dossier avec les images (.png) nommées comme les fichiers .rfa, puis clique sur OK." };
            if (imgDialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                imagesFolder = imgDialog.SelectedPath;
            }
            else
            {
                MessageBox.Show(this, "Aucun dossier d'images choisi. Les vignettes ne seront pas affichées.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                imagesFolder = familiesFolder;
            }

            SavePaths();
            MessageBox.Show(this,
                "Les dossiers sont enregistrés. Pour les modifier plus tard, supprime le fichier 'CheminsFamille.json' dans Documents/RevitLogs/SauvegardePréférence.",
                "Chemins enregistrés", MessageBoxButton.OK, MessageBoxImage.Information);

            ImageResolver.ClearCaches();
            _bitmapCache.Clear();

            _index?.Dispose();
            _index = new FamilyIndexService(familiesFolder, imagesFolder);
            _index.IndexUpdated += OnIndexUpdated;
            _ = _index.StartAsync();

            return true;
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
            if (DarkModeCheckBox != null) DarkModeCheckBox.IsChecked = dark;
            if (ShowTop8CheckBox != null) ShowTop8CheckBox.IsChecked = showTop8;
            if (AlwaysOnTopCheckBox != null) AlwaysOnTopCheckBox.IsChecked = alwaysOnTop;
            detailedViewMode = detailedView;
            if (DetailedViewCheckBox != null) DetailedViewCheckBox.IsChecked = detailedViewMode;

            this.Topmost = alwaysOnTop;
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
                "DarkMode="   + ((DarkModeCheckBox?.IsChecked == true) ? "true" : "false"),
                "ShowTop8="   + ((ShowTop8CheckBox?.IsChecked == true) ? "true" : "false"),
                "AlwaysOnTop="+ ((AlwaysOnTopCheckBox?.IsChecked == true) ? "true" : "false"),
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
            if (DarkModeCheckBox != null) DarkModeCheckBox.IsChecked = false;
            if (ShowTop8CheckBox != null) ShowTop8CheckBox.IsChecked = false;
            if (AlwaysOnTopCheckBox != null) AlwaysOnTopCheckBox.IsChecked = false;
            if (DetailedViewCheckBox != null) DetailedViewCheckBox.IsChecked = false;
            this.Topmost = false;
            UpdateTheme();
            SaveConfig_Click(s, e);
        }

        private void ApplyColors_Click(object sender, RoutedEventArgs e) => UpdateTheme();

        private void UpdateTheme()
        {
            bool isDark = (DarkModeCheckBox?.IsChecked == true);

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
            detailedViewMode = DetailedViewCheckBox?.IsChecked == true;
            UpdateFamilyListViewMode();
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

        private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            this.Topmost = AlwaysOnTopCheckBox?.IsChecked == true;
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        #endregion

        #region Modèle

        private FamilyItem CreateFamilyItemFromPath(string path)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            return new FamilyItem
            {
                Name = name,
                Path = path,
                Icon = null,
                NormalizedName = StripDiacritics(name).ToLowerInvariant()
            };
        }

        #endregion
    }

    // ======================= FamilyItem =======================

    public class FamilyItem : INotifyPropertyChanged
    {
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

        public int NaturalOrder { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public class ChipActiveConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return false;

            var value = values[0] as string;
            var active = values[1] as string;

            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(active))
                return false;

            return string.Equals(value.Trim(), active.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public class FamilyHighlightConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 4)
                return false;

            var category = values[0] as string;
            var activeCategory = values[1] as string;
            var version = values[2] as string;
            var activeVersion = values[3] as string;

            bool categoryMatch = !string.IsNullOrWhiteSpace(activeCategory) && AreEqual(category, activeCategory);
            bool versionMatch = !string.IsNullOrWhiteSpace(activeVersion) && AreEqual(version, activeVersion);

            return categoryMatch || versionMatch;
        }

        private static bool AreEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

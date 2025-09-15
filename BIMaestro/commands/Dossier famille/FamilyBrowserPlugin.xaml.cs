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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using WinForms = System.Windows.Forms;
using Newtonsoft.Json;

namespace Famille
{
    public partial class FamilyBrowserWindow : Window
    {
        // ===== Constantes =====
        private const string FavoritesCollectionId = "builtin_favoris";
        private const string FavoritesCollectionName = "Favoris";

        // ===== Chemins =====
        private string rootFolderPath = @"P:\0-Boîte à outils Revit\0-Bibliothèque\A-Famille Revit";
        private string familiesFolder = @"P:\0-Boîte à outils Revit\0-Bibliothèque\A-Famille Revit";
        private string imagesFolder = @"P:\0-Boîte à outils Revit\0-Bibliothèque\B-Famille Revit Image";

        private readonly string favoritesFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "Favorites.txt");
        private readonly string configFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "Config.txt");
        private readonly string pathsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "CheminsFamille.json");
        private readonly string workFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "FamilleRevit");

        // ===== Données UI =====
        private List<FamilyItem> allFamilies = new();
        private List<FamilyItem> displayedFamilies = new();
        private List<FamilyItem> favoriteFamilies = new();
        private string currentFolderPath;

        public string RootFolderName => System.IO.Path.GetFileName(rootFolderPath);
        public List<FamilyItem> FavoriteFamilies => favoriteFamilies;

        // Pagination & recherche
        private const int PageSize = 200;
        private int _nextIndex = 0;
        private List<FamilyItem> _currentResult = new();
        private readonly DispatcherTimer _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        // Index global (auto)
        private FamilyIndexService _index;
        private bool _globalSearchMode = false;

        // Vignettes
        private static readonly System.Threading.SemaphoreSlim _thumbGate = new(4);
        private static readonly Dictionary<string, BitmapImage> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);

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

            // Favoris (★ visuel)
            MarkFavoritesInAllFamilies();

            // Arbo + Top-8
            LoadFolderTree();

            PlaceholderText.Visibility = Visibility.Visible;

            // Index
            IndexStatusText.Text = "Chargement de l'index…";
            await _index.StartAsync();
            IndexStatusText.Text = _index.StatusText;
        }

        private void OnIndexUpdated()
        {
            Dispatcher.Invoke(() =>
            {
                IndexStatusText.Text = _index.StatusText;
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
                IndexStatusText.Text = _index.StatusText;
            }

            currentFolderPath = tv.Tag.ToString();
            LoadFamilies(currentFolderPath, recursive: false);
            MarkFavoritesInAllFamilies(); // maj des ★

            BeginPaging(allFamilies);

            TopFamiliesView.Visibility = Visibility.Collapsed;
            TopSeparator.Visibility = Visibility.Collapsed;
        }

        private void LoadFamilies(string path, bool recursive)
        {
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

            foreach (var it in topItems) LoadThumbnailForFamilyItem(it);
        }

        private void ShowTop8CheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (string.Equals(currentFolderPath, rootFolderPath, StringComparison.OrdinalIgnoreCase))
                RefreshTop8_UsageOnly();
        }

        #endregion

        #region Favoris (★)  + compat Favorites.txt

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

        private void LoadFavoritesFromFile()
        {
            favoriteFamilies.Clear();
            if (!File.Exists(favoritesFile)) return;

            var paths = File.ReadAllLines(favoritesFile);
            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    var ext = CreateFamilyItemFromPath(p);
                    ext.IsFavorite = true;
                    favoriteFamilies.Add(ext);
                    LoadThumbnailForFamilyItem(ext);
                }
            }
            UpdateFavoritesUI();
        }

        private void UpdateFavoritesUI() { /* pas nécessaire ici */ }

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

        private void MarkFavoritesInAllFamilies()
        {
            try
            {
                var favCol = GetFavoritesCollection();
                var set = new HashSet<string>(favCol.Paths, StringComparer.OrdinalIgnoreCase);
                foreach (var fam in allFamilies)
                    fam.IsFavorite = set.Contains(fam.Path);
                foreach (var fam in displayedFamilies)
                    fam.IsFavorite = set.Contains(fam.Path);
            }
            catch { }
        }

        #endregion

        #region Recherche + pagination

        private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _globalSearchMode = true;
            IndexStatusText.Text = _index.IsReady
                ? "Recherche globale (index prêt)."
                : "Recherche globale (index en cours…)";
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _globalSearchMode = false;
                IndexStatusText.Text = _index.StatusText;
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
                var items = hits.Select(e => new FamilyItem
                {
                    Name = e.Name,
                    Path = e.Path,
                    Category = e.Category,
                    NormalizedName = e.NormalizedName
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
            }

            FamilyListView.ItemsSource = null;
            FamilyListView.ItemsSource = displayedFamilies;

            // Met à jour les étoiles selon la collection Favoris
            MarkFavoritesInAllFamilies();

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

        private void AllFamiliesButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            if (_globalSearchMode)
            {
                _globalSearchMode = false;
                IndexStatusText.Text = _index.StatusText;
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
            // Toujours en overwrite
            FamilyBrowserCommand.LoadCollectionHandlerInstance.FamilyPaths = new List<string>(_selectedCollection.Paths);
            FamilyBrowserCommand.LoadCollectionEventInstance.Raise();
        }

        #endregion

        #region Vignettes

        private void LoadThumbnailForFamilyItem(FamilyItem fam)
        {
            if (fam == null || fam.Icon != null) return;

            Task.Run(async () =>
            {
                await _thumbGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var full = ImageResolver.Resolve(familiesFolder, imagesFolder, fam.Path);
                    if (string.IsNullOrEmpty(full) || !File.Exists(full))
                    {
                        // Placeholder ultra léger pour éviter les vides
                        Dispatcher.Invoke(() =>
                        {
                            var ph = new BitmapImage();
                            ph.BeginInit();
                            ph.UriSource = new Uri("pack://application:,,,/"); // pixel transparent
                            ph.DecodePixelWidth = 1;
                            ph.EndInit();
                            ph.Freeze();
                            fam.Icon = ph;
                        });
                        return;
                    }

                    // cache ?
                    if (_bitmapCache.TryGetValue(full, out var cachedBmp))
                    {
                        Dispatcher.Invoke(() => fam.Icon = cachedBmp);
                        return;
                    }

                    BitmapImage bmp = null;

                    // 1) Essai par Uri absolue
                    try
                    {
                        var uri = new Uri(full, UriKind.Absolute);
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = uri;
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                        bi.DecodePixelWidth = 256;
                        bi.EndInit();
                        bi.Freeze();
                        bmp = bi;
                    }
                    catch
                    {
                        // 2) Fallback par flux
                        try
                        {
                            using (var fs = File.OpenRead(full))
                            {
                                var bi = new BitmapImage();
                                bi.BeginInit();
                                bi.CacheOption = BitmapCacheOption.OnLoad;
                                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                                bi.DecodePixelWidth = 256;
                                bi.StreamSource = fs;
                                bi.EndInit();
                                bi.Freeze();
                                bmp = bi;
                            }
                        }
                        catch { /* on laisse bmp = null */ }
                    }

                    if (bmp != null)
                    {
                        _bitmapCache[full] = bmp;
                        Dispatcher.Invoke(() => fam.Icon = bmp);
                    }
                    else
                    {
                        // Fallback dernier recours (pixel)
                        Dispatcher.Invoke(() =>
                        {
                            var ph = new BitmapImage();
                            ph.BeginInit();
                            ph.UriSource = new Uri("pack://application:,,,/");
                            ph.DecodePixelWidth = 1;
                            ph.EndInit();
                            ph.Freeze();
                            fam.Icon = ph;
                        });
                    }
                }
                finally
                {
                    _thumbGate.Release();
                }
            });
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

            this.Topmost = alwaysOnTop;
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
                SetFavoriteFlagInViews(fam.Path, false);
                ExportFavoritesCollectionToTxt();
            }
        }

        private void SetFavoriteFlagInViews(string path, bool value)
        {
            foreach (var it in displayedFamilies)
                if (string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase))
                    it.IsFavorite = value;

            foreach (var it in allFamilies)
                if (string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase))
                    it.IsFavorite = value;
        }

        #endregion

        #region Handlers divers

        private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            this.Topmost = AlwaysOnTopCheckBox?.IsChecked == true;
        }

        #endregion

        #region Modèle

        private FamilyItem CreateFamilyItemFromPath(string path)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var cat = "Général";
            var low = name.ToLowerInvariant();
            if (low.Contains("porte")) cat = "Porte";
            else if (low.Contains("fenetre") || low.Contains("fenêtre")) cat = "Fenêtre";

            return new FamilyItem
            {
                Name = name,
                Path = path,
                Category = cat,
                Icon = null,
                NormalizedName = StripDiacritics(name).ToLowerInvariant()
            };
        }

        #endregion
    }

    public class FamilyItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Category { get; set; }
        public string NormalizedName { get; set; }

        private BitmapImage _icon;
        public BitmapImage Icon
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}

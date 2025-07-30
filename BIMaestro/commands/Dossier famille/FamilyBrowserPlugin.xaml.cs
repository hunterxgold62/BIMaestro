using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace Famille
{
    public partial class FamilyBrowserWindow : Window
    {
        private readonly string familiesFolder = @"P:\0-Boîte à outils Revit\0-Bibliothèque\A-Famille Revit";
        private readonly string imagesFolder = @"P:\0-Boîte à outils Revit\0-Bibliothèque\B-Famille Revit Image";
        private readonly string favoritesFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "Favorites.txt");
        private readonly string configFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "Config.txt");

        private List<FamilyItem> allFamilies = new List<FamilyItem>();
        private List<FamilyItem> displayedFamilies = new List<FamilyItem>();
        private List<FamilyItem> favoriteFamilies = new List<FamilyItem>();
        public List<FamilyItem> FavoriteFamilies => favoriteFamilies;

        public FamilyBrowserWindow()
        {
            InitializeComponent();
            EnsureFilesExist();
            DataContext = this;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            UpdateTheme();

            LoadFavoritesFromFile();
            LoadFolderTree();
            StartThumbnailLoading();

            FolderTreeView.SelectedItemChanged += FolderTreeView_SelectedItemChanged;
            PlaceholderText.Visibility = Visibility.Visible;
        }

        #region Dossiers & familles

        private void LoadFolderTree()
        {
            FolderTreeView.Items.Clear();
            if (!Directory.Exists(familiesFolder))
            {
                MessageBox.Show(this, "Le dossier de familles spécifié n'existe pas.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var root = new DirectoryInfo(familiesFolder);
            var node = CreateDirectoryNode(root);
            FolderTreeView.Items.Add(node);
            node.IsExpanded = true;
            node.IsSelected = true;
            LoadFamilies(familiesFolder, true);
            displayedFamilies = allFamilies.ToList();
            FamilyListView.ItemsSource = displayedFamilies;
            UpdateCount();
        }

        private TreeViewItem CreateDirectoryNode(DirectoryInfo dir)
        {
            var item = new TreeViewItem { Header = dir.Name, Tag = dir.FullName };
            foreach (var sub in dir.GetDirectories())
                item.Items.Add(CreateDirectoryNode(sub));
            return item;
        }

        private void FolderTreeView_SelectedItemChanged(object s, RoutedPropertyChangedEventArgs<object> e)
        {
            if (FolderTreeView.SelectedItem is TreeViewItem tv)
            {
                LoadFamilies(tv.Tag.ToString(), true);
                displayedFamilies = allFamilies.ToList();
                MarkFavoritesInAllFamilies();
                FamilyListView.ItemsSource = displayedFamilies;
                SearchBox.Text = "";
                ApplyFilters();
                StartThumbnailLoading();
            }
        }

        private void LoadFamilies(string path, bool recursive)
        {
            allFamilies.Clear();
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var f in Directory.GetFiles(path, "*.rfa", opt))
                if (File.Exists(f))
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
        }

        private void MarkFavoritesInAllFamilies()
        {
            if (!File.Exists(favoritesFile)) return;
            var favs = File.ReadAllLines(favoritesFile);
            foreach (var fam in allFamilies)
                fam.IsFavorite = favs.Contains(fam.Path);
        }

        #endregion

        #region Chargement asynchrone des vignettes

        private void StartThumbnailLoading()
        {
            var imgRoot = imagesFolder;
            var famRoot = familiesFolder;

            Task.Run(() =>
            {
                foreach (var fam in allFamilies)
                {
                    var rel = GetRelativePath(famRoot, fam.Path);
                    var img = Path.ChangeExtension(rel, ".png");
                    var full = Path.Combine(imgRoot, img);
                    if (!File.Exists(full)) continue;
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(full, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        Dispatcher.Invoke(() => fam.Icon = bmp);
                    }
                    catch { }
                }
            });
        }

        private void LoadThumbnailForFamilyItem(FamilyItem fam)
        {
            Task.Run(() =>
            {
                var rel = GetRelativePath(familiesFolder, fam.Path);
                var img = Path.ChangeExtension(rel, ".png");
                var full = Path.Combine(imagesFolder, img);
                if (!File.Exists(full)) return;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(full, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    Dispatcher.Invoke(() => fam.Icon = bmp);
                }
                catch { }
            });
        }

        #endregion

        #region Gestion des favoris

        private void LoadFavoritesFromFile()
        {
            favoriteFamilies.Clear();
            if (!File.Exists(favoritesFile)) return;
            foreach (var p in File.ReadAllLines(favoritesFile))
            {
                var ex = allFamilies.FirstOrDefault(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase));
                if (ex != null)
                {
                    ex.IsFavorite = true;
                    favoriteFamilies.Add(ex);
                }
                else if (File.Exists(p))
                {
                    var fi = CreateFamilyItemFromPath(p);
                    fi.IsFavorite = true;
                    favoriteFamilies.Add(fi);
                    LoadThumbnailForFamilyItem(fi);
                }
            }
            UpdateFavoritesUI();
        }

        private void UpdateFavoritesUI()
        {
            FavoritesListView.ItemsSource = null;
            FavoritesListView.ItemsSource = favoriteFamilies;
        }

        private void FavoriteButton_Click(object s, RoutedEventArgs e)
        {
            if (s is Button btn && btn.DataContext is FamilyItem fam)
            {
                fam.IsFavorite = !fam.IsFavorite;
                if (fam.IsFavorite)
                {
                    if (!favoriteFamilies.Any(f => f.Path == fam.Path))
                        favoriteFamilies.Add(fam);
                }
                else
                {
                    var rem = favoriteFamilies.FirstOrDefault(f => f.Path == fam.Path);
                    if (rem != null) favoriteFamilies.Remove(rem);
                }
                File.WriteAllLines(favoritesFile, favoriteFamilies.Select(f => f.Path));
                UpdateFavoritesUI();
            }
        }

        #endregion

        #region Recherche & affichage

        private void SearchBox_TextChanged(object s, TextChangedEventArgs e)
        {
            PlaceholderText.Visibility =
                string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            var txt = SearchBox.Text.ToLower();
            var filt = displayedFamilies.Where(f => f.Name.ToLower().Contains(txt));
            FamilyListView.ItemsSource = filt;
            UpdateCount(filt.Count());
        }

        private void UpdateCount(int? c = null)
        {
            if (CountTextBlock == null) return;
            if (!c.HasValue) c = displayedFamilies.Count;
            CountTextBlock.Text = c.Value.ToString();
        }

        #endregion

        #region Configuration & thèmes

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
                MessageBox.Show(this, "Erreur création fichiers config : " + ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadConfig()
        {
            string top = "#FFF2F2F2", bottom = "#FFFFFFFF", panel = "#F0F0F0",
                   treeBg = "#F0F0F0", itemsBg = "Transparent", tabBg = "Transparent";
            bool dark = false;

            if (File.Exists(configFile))
            {
                foreach (var line in File.ReadAllLines(configFile))
                {
                    if (line.StartsWith("TopColor=", StringComparison.OrdinalIgnoreCase))
                        top = line.Substring("TopColor=".Length);
                    else if (line.StartsWith("BottomColor=", StringComparison.OrdinalIgnoreCase))
                        bottom = line.Substring("BottomColor=".Length);
                    else if (line.StartsWith("PanelBackground=", StringComparison.OrdinalIgnoreCase))
                        panel = line.Substring("PanelBackground=".Length);
                    else if (line.StartsWith("TreeViewBackground=", StringComparison.OrdinalIgnoreCase))
                        treeBg = line.Substring("TreeViewBackground=".Length);
                    else if (line.StartsWith("ItemsBackground=", StringComparison.OrdinalIgnoreCase))
                        itemsBg = line.Substring("ItemsBackground=".Length);
                    else if (line.StartsWith("TabBackground=", StringComparison.OrdinalIgnoreCase))
                        tabBg = line.Substring("TabBackground=".Length);
                    else if (line.StartsWith("DarkMode=", StringComparison.OrdinalIgnoreCase))
                        bool.TryParse(line.Substring("DarkMode=".Length), out dark);
                }
            }

            TopColorPicker.SelectedColor = ColorFromHex(top);
            BottomColorPicker.SelectedColor = ColorFromHex(bottom);
            PanelBackgroundPicker.SelectedColor = ColorFromHex(panel);
            TreeViewBackgroundPicker.SelectedColor = ColorFromHex(treeBg);
            ItemsBackgroundPicker.SelectedColor = ColorFromHex(itemsBg);
            TabBackgroundPicker.SelectedColor = ColorFromHex(tabBg);
            DarkModeCheckBox.IsChecked = dark;
        }

        private void SaveConfig_Click(object s, RoutedEventArgs e)
        {
            var lines = new[]
            {
                "TopColor="    + ColorToHex(TopColorPicker.SelectedColor ?? Colors.White),
                "BottomColor=" + ColorToHex(BottomColorPicker.SelectedColor ?? Colors.White),
                "PanelBackground="      + ColorToHex(PanelBackgroundPicker.SelectedColor ?? Colors.Transparent),
                "TreeViewBackground="   + ColorToHex(TreeViewBackgroundPicker.SelectedColor ?? Colors.Transparent),
                "ItemsBackground="      + (ItemsBackgroundPicker.SelectedColor == Colors.Transparent
                                              ? "Transparent"
                                              : ColorToHex(ItemsBackgroundPicker.SelectedColor.Value)),
                "TabBackground="        + (TabBackgroundPicker.SelectedColor   == Colors.Transparent
                                              ? "Transparent"
                                              : ColorToHex(TabBackgroundPicker.SelectedColor.Value)),
                "DarkMode=" + (DarkModeCheckBox.IsChecked == true ? "true" : "false")
            };
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
            DarkModeCheckBox.IsChecked = false;
            UpdateTheme();
            SaveConfig_Click(s, e);
        }

        private void UpdateTheme()
        {
            bool isDark = DarkModeCheckBox.IsChecked == true;

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

        #endregion

        #region Interaction UI

        private void ApplyColors_Click(object sender, RoutedEventArgs e) => UpdateTheme();

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
                // on revient à LoadFamilyHandler qui gèrera aussi le placement
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
                    Process.Start(new ProcessStartInfo(fam.Path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Impossible d’ouvrir le fichier : " + ex.Message);
                }
            }
        }

        #endregion

        #region Helpers

        public static string GetRelativePath(string relativeTo, string path)
        {
            var fromUri = new Uri(relativeTo.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? relativeTo : relativeTo + Path.DirectorySeparatorChar);
            var toUri = new Uri(path);
            var relUri = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(relUri.ToString())
                      .Replace('/', Path.DirectorySeparatorChar);
        }

        private FamilyItem CreateFamilyItemFromPath(string path)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var cat = "Général";
            var low = name.ToLower();
            if (low.Contains("porte")) cat = "Porte";
            else if (low.Contains("fenêtre") || low.Contains("fenetre")) cat = "Fenêtre";

            return new FamilyItem
            {
                Name = name,
                Path = path,
                Category = cat,
                Icon = null
            };
        }

        private string ColorToHex(Color c)
            => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private Color ColorFromHex(string hex)
            => hex.Equals("Transparent", StringComparison.OrdinalIgnoreCase)
               ? Colors.Transparent
               : (Color)ColorConverter.ConvertFromString(hex);

        #endregion
    }

    public class FamilyItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Category { get; set; }

        private BitmapImage _icon;
        public BitmapImage Icon
        {
            get => _icon;
            set
            {
                if (_icon != value)
                {
                    _icon = value;
                    OnPropertyChanged(nameof(Icon));
                }
            }
        }

        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged(nameof(IsFavorite));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}

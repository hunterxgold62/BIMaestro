using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FamilyBrowserPlugin
{
    public partial class FamilyBrowserWindow : Window
    {
        private string familiesFolder = @"P:\0-Boîte à outils Revit\0-Bibliothèque\A-Famille Revit";
        private string imagesFolder = @"P:\0-Boîte à outils Revit\0-Bibliothèque\B-Famille Revit Image";
        private string favoritesFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "Favorites.txt");
        private string configFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "SauvegardePréférence", "Config.txt");

        private List<FamilyItem> allFamilies = new List<FamilyItem>();
        private List<FamilyItem> displayedFamilies = new List<FamilyItem>();
        private List<FamilyItem> favoriteFamilies = new List<FamilyItem>();

        // Pour le binding dans l'onglet Favoris
        public List<FamilyItem> FavoriteFamilies
        {
            get { return favoriteFamilies; }
        }

        public FamilyBrowserWindow()
        {
            InitializeComponent();
            EnsureFilesExist();
            DataContext = this;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            UpdateTheme(); // Applique le thème (PrimaryText, ImageBackground, etc.)

            LoadFavoritesFromFile();
            LoadFolderTree();
            MarkFavoritesInAllFamilies();
            FolderTreeView.SelectedItemChanged += FolderTreeView_SelectedItemChanged;
            PlaceholderText.Visibility = Visibility.Visible;
        }

        #region Chargement et affichage des dossiers & familles

        private void LoadFolderTree()
        {
            if (!Directory.Exists(familiesFolder))
            {
                MessageBox.Show(this, "Le dossier de familles spécifié n'existe pas.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var rootDirectoryInfo = new DirectoryInfo(familiesFolder);
            TreeViewItem rootItem = CreateDirectoryNode(rootDirectoryInfo);
            FolderTreeView.Items.Add(rootItem);
            rootItem.IsExpanded = true;
            rootItem.IsSelected = true;
            LoadFamilies(familiesFolder, recursive: true);
            displayedFamilies = new List<FamilyItem>(allFamilies);
            FamilyListView.ItemsSource = displayedFamilies;
            UpdateCount();
        }

        private TreeViewItem CreateDirectoryNode(DirectoryInfo directoryInfo)
        {
            var directoryNode = new TreeViewItem
            {
                Header = directoryInfo.Name,
                Tag = directoryInfo.FullName
            };
            foreach (var directory in directoryInfo.GetDirectories())
            {
                directoryNode.Items.Add(CreateDirectoryNode(directory));
            }
            return directoryNode;
        }

        private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (FolderTreeView.SelectedItem is TreeViewItem selectedItem)
            {
                string selectedPath = selectedItem.Tag.ToString();
                LoadFamilies(selectedPath, recursive: true);
                displayedFamilies = new List<FamilyItem>(allFamilies);
                MarkFavoritesInAllFamilies();
                FamilyListView.ItemsSource = displayedFamilies;
                SearchBox.Text = "";
                ApplyFilters();
            }
        }

        private void LoadFamilies(string path, bool recursive = true)
        {
            allFamilies.Clear();
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var familyFiles = Directory.GetFiles(path, "*.rfa", searchOption);
            foreach (var file in familyFiles)
            {
                var family = CreateFamilyItemFromPath(file);
                if (family != null)
                    allFamilies.Add(family);
            }
            allFamilies = allFamilies.OrderBy(f => ParseFamilyNameForSorting(f.Name)).ToList();
        }

        private (string prefix, int number) ParseFamilyNameForSorting(string name)
        {
            var parts = name.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[1], out int num))
                return (parts[0], num);
            return (name, int.MaxValue);
        }

        // Met à jour IsFavorite selon le fichier favorites
        private void MarkFavoritesInAllFamilies()
        {
            if (!File.Exists(favoritesFile))
                return;
            var favPaths = File.ReadAllLines(favoritesFile);
            foreach (var fam in allFamilies)
            {
                fam.IsFavorite = favPaths.Contains(fam.Path);
            }
        }

        #endregion

        #region Gestion des fichiers et configuration

        private void EnsureFilesExist()
        {
            try
            {
                if (!File.Exists(favoritesFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(favoritesFile));
                    File.WriteAllText(favoritesFile, string.Empty);
                }
                if (!File.Exists(configFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(configFile));
                    File.WriteAllText(configFile, string.Empty);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erreur lors de la création des fichiers de configuration : " + ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadConfig()
        {
            // Valeurs par défaut
            string topColorHex = "#FFF2F2F2";
            string bottomColorHex = "#FFFFFFFF";
            string panelHex = "#F0F0F0";
            string treeViewHex = "#F0F0F0";
            string itemsHex = "Transparent";
            string tabHex = "Transparent";
            bool darkMode = false;
            if (File.Exists(configFile))
            {
                var lines = File.ReadAllLines(configFile);
                foreach (var line in lines)
                {
                    if (line.StartsWith("TopColor=", StringComparison.OrdinalIgnoreCase))
                        topColorHex = line.Substring("TopColor=".Length).Trim();
                    else if (line.StartsWith("BottomColor=", StringComparison.OrdinalIgnoreCase))
                        bottomColorHex = line.Substring("BottomColor=".Length).Trim();
                    else if (line.StartsWith("PanelBackground=", StringComparison.OrdinalIgnoreCase))
                        panelHex = line.Substring("PanelBackground=".Length).Trim();
                    else if (line.StartsWith("TreeViewBackground=", StringComparison.OrdinalIgnoreCase))
                        treeViewHex = line.Substring("TreeViewBackground=".Length).Trim();
                    else if (line.StartsWith("ItemsBackground=", StringComparison.OrdinalIgnoreCase))
                        itemsHex = line.Substring("ItemsBackground=".Length).Trim();
                    else if (line.StartsWith("TabBackground=", StringComparison.OrdinalIgnoreCase))
                        tabHex = line.Substring("TabBackground=".Length).Trim();
                    else if (line.StartsWith("DarkMode=", StringComparison.OrdinalIgnoreCase))
                    {
                        bool.TryParse(line.Substring("DarkMode=".Length).Trim(), out darkMode);
                    }
                }
            }
            TopColorPicker.SelectedColor = ColorFromHex(topColorHex);
            BottomColorPicker.SelectedColor = ColorFromHex(bottomColorHex);
            PanelBackgroundPicker.SelectedColor = ColorFromHex(panelHex);
            TreeViewBackgroundPicker.SelectedColor = ColorFromHex(treeViewHex);
            ItemsBackgroundPicker.SelectedColor = ColorFromHex(itemsHex);
            TabBackgroundPicker.SelectedColor = ColorFromHex(tabHex);
            DarkModeCheckBox.IsChecked = darkMode;
        }

        private void SaveConfig()
        {
            var lines = new string[]
            {
                "TopColor=" + (TopColorPicker.SelectedColor == null ? "#FFF2F2F2" : ColorToHex(TopColorPicker.SelectedColor.Value)),
                "BottomColor=" + (BottomColorPicker.SelectedColor == null ? "#FFFFFFFF" : ColorToHex(BottomColorPicker.SelectedColor.Value)),
                "PanelBackground=" + (PanelBackgroundPicker.SelectedColor == Colors.Transparent ? "Transparent" : ColorToHex(PanelBackgroundPicker.SelectedColor.Value)),
                "TreeViewBackground=" + (TreeViewBackgroundPicker.SelectedColor == Colors.Transparent ? "Transparent" : ColorToHex(TreeViewBackgroundPicker.SelectedColor.Value)),
                "ItemsBackground=" + (ItemsBackgroundPicker.SelectedColor == Colors.Transparent ? "Transparent" : ColorToHex(ItemsBackgroundPicker.SelectedColor.Value)),
                "TabBackground=" + (TabBackgroundPicker.SelectedColor == Colors.Transparent ? "Transparent" : ColorToHex(TabBackgroundPicker.SelectedColor.Value)),
                "DarkMode=" + (DarkModeCheckBox.IsChecked == true ? "true" : "false")
            };
            File.WriteAllLines(configFile, lines);
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            MessageBox.Show("Configuration enregistrée avec succès. Redémarre l'application pour appliquer définitivement les changements.");
        }

        private void ResetConfig_Click(object sender, RoutedEventArgs e)
        {
            TopColorPicker.SelectedColor = ColorFromHex("#FFF2F2F2");
            BottomColorPicker.SelectedColor = ColorFromHex("#FFFFFFFF");
            PanelBackgroundPicker.SelectedColor = ColorFromHex("#F0F0F0");
            TreeViewBackgroundPicker.SelectedColor = ColorFromHex("#F0F0F0");
            ItemsBackgroundPicker.SelectedColor = Colors.Transparent;
            TabBackgroundPicker.SelectedColor = Colors.Transparent;
            DarkModeCheckBox.IsChecked = false;
            UpdateTheme();
            SaveConfig();
            MessageBox.Show("Configuration réinitialisée aux valeurs par défaut. Redémarre l'application pour voir l'effet complet.");
        }

        #endregion

        #region Création et chargement des objets FamilyItem

        private FamilyItem CreateFamilyItemFromPath(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            string familyName = Path.GetFileNameWithoutExtension(filePath);
            // Affecte la catégorie en fonction du nom (informative ici)
            string category = "Général";
            if (familyName.ToLower().Contains("porte"))
                category = "Porte";
            else if (familyName.ToLower().Contains("fenêtre") || familyName.ToLower().Contains("fenetre"))
                category = "Fenêtre";
            string relativePath = GetRelativePath(familiesFolder, filePath);
            string imageRelativePath = Path.ChangeExtension(relativePath, ".png");
            string imagePath = Path.Combine(imagesFolder, imageRelativePath);
            BitmapImage thumbnail = LoadThumbnail(imagePath);
            return new FamilyItem
            {
                Name = familyName,
                Path = filePath,
                Icon = thumbnail,
                Category = category
            };
        }

        private BitmapImage LoadThumbnail(string imagePath)
        {
            if (!File.Exists(imagePath))
                return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Gestion de la recherche et affichage

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            string searchText = SearchBox.Text.ToLower();
            var filtered = displayedFamilies.Where(f => f.Name.ToLower().Contains(searchText));
            FamilyListView.ItemsSource = filtered;
            UpdateCount(filtered.Count());
        }

        private void UpdateCount(int? count = null)
        {
            if (CountTextBlock != null)
            {
                if (!count.HasValue)
                    count = displayedFamilies.Count();
                CountTextBlock.Text = count.Value.ToString();
            }
        }

        #endregion

        #region Gestion des favoris

        private void LoadFavoritesFromFile()
        {
            favoriteFamilies.Clear();
            if (File.Exists(favoritesFile))
            {
                var lines = File.ReadAllLines(favoritesFile);
                foreach (var line in lines)
                {
                    if (File.Exists(line))
                    {
                        var family = CreateFamilyItemFromPath(line);
                        if (family != null)
                        {
                            family.IsFavorite = true;
                            favoriteFamilies.Add(family);
                        }
                    }
                }
            }
            UpdateFavoritesUI();
        }

        private void UpdateFavoritesUI()
        {
            if (FavoritesListView != null)
            {
                FavoritesListView.ItemsSource = null;
                FavoritesListView.ItemsSource = favoriteFamilies;
            }
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is FamilyItem family)
            {
                family.IsFavorite = !family.IsFavorite;
                if (family.IsFavorite)
                {
                    if (!favoriteFamilies.Any(f => f.Path == family.Path))
                        favoriteFamilies.Add(family);
                }
                else
                {
                    var itemToRemove = favoriteFamilies.FirstOrDefault(f => f.Path == family.Path);
                    if (itemToRemove != null)
                        favoriteFamilies.Remove(itemToRemove);
                }
                SaveFavoritesToFile();
                UpdateFavoritesUI();
            }
        }

        private void SaveFavoritesToFile()
        {
            try
            {
                var lines = favoriteFamilies.Select(f => f.Path).ToArray();
                File.WriteAllLines(favoritesFile, lines);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Erreur lors de la sauvegarde des favoris : " + ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Divers et utilitaires

        private void FamilyItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Ne réagir qu'en cas de double-clic
            if (e.ClickCount != 2) return;

            if (sender is Border border && border.DataContext is FamilyItem family)
            {
                if (FamilyBrowserCommand.LoadFamilyHandlerInstance != null && FamilyBrowserCommand.LoadFamilyEventInstance != null)
                {
                    FamilyBrowserCommand.LoadFamilyHandlerInstance.FamilyPath = family.Path;
                    FamilyBrowserCommand.LoadFamilyEventInstance.Raise();
                }
            }
        }

        /// <summary>
        /// Ouvre le fichier .rfa dans l'application associée.
        /// </summary>
        private void OpenFamilyFile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.DataContext is FamilyItem family)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(family.Path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FamilyBrowserCommand.MainWindowRef, "Impossible d'ouvrir le fichier de famille : " + ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public static string GetRelativePath(string relativeTo, string path)
        {
            var fromUri = new Uri(relativeTo.EndsWith(Path.DirectorySeparatorChar.ToString()) ? relativeTo : relativeTo + Path.DirectorySeparatorChar);
            var toUri = new Uri(path);
            var relativeUri = fromUri.MakeRelativeUri(toUri);
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());
            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }

        private string ColorToHex(Color c)
        {
            return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private Color ColorFromHex(string hex)
        {
            if (hex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
                return Colors.Transparent;
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        /// <summary>
        /// Applique le thème sombre ou clair (mise à jour de PrimaryText, ImageBackground, etc.).
        /// </summary>
        private void UpdateTheme()
        {
            if (DarkModeCheckBox.IsChecked == true)
            {
                // Mode sombre
                Resources["BackgroundGradient"] = new LinearGradientBrush(new GradientStopCollection {
                    new GradientStop(Colors.Black, 0),
                    new GradientStop(Colors.DarkGray, 1)
                })
                { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                Resources["PanelBackground"] = new SolidColorBrush(Color.FromRgb(51, 51, 51));
                Resources["TreeViewBackground"] = new SolidColorBrush(Color.FromRgb(51, 51, 51));
                Resources["ItemsBackground"] = new SolidColorBrush(Color.FromRgb(34, 34, 34));
                Resources["TabBackground"] = new SolidColorBrush(Color.FromRgb(68, 68, 68));
                Resources["ImageBackground"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                Resources["PrimaryText"] = new SolidColorBrush(Colors.White);
            }
            else
            {
                // Mode clair
                UpdateGradient();
                Resources["ImageBackground"] = new SolidColorBrush(Colors.Transparent);
                Resources["PrimaryText"] = new SolidColorBrush(Colors.Black);
            }
        }

        private void UpdateGradient()
        {
            try
            {
                var topColor = TopColorPicker.SelectedColor ?? Colors.White;
                var bottomColor = BottomColorPicker.SelectedColor ?? Colors.White;
                var panelColor = PanelBackgroundPicker.SelectedColor ?? Colors.White;
                var treeColor = TreeViewBackgroundPicker.SelectedColor ?? Colors.White;
                var itemsColor = ItemsBackgroundPicker.SelectedColor ?? Colors.Transparent;
                var tabColor = TabBackgroundPicker.SelectedColor ?? Colors.Transparent;
                var newGradient = new LinearGradientBrush(new GradientStopCollection {
                    new GradientStop(topColor, 0),
                    new GradientStop(bottomColor, 1)
                })
                { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                Resources["BackgroundGradient"] = newGradient;
                Resources["PanelBackground"] = new SolidColorBrush(panelColor);
                Resources["TreeViewBackground"] = new SolidColorBrush(treeColor);
                Resources["ItemsBackground"] = new SolidColorBrush(itemsColor);
                Resources["TabBackground"] = new SolidColorBrush(tabColor);
            }
            catch (Exception)
            {
                MessageBox.Show("Pour appliquer ces couleurs, veuillez soit enregistrer et redémarrer, soit sélectionner des couleurs valides.");
            }
        }

        private void ApplyColors_Click(object sender, RoutedEventArgs e)
        {
            UpdateTheme();
        }

        #endregion
    }

    // Classe FamilyItem implémentant INotifyPropertyChanged pour actualiser IsFavorite
    public class FamilyItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public BitmapImage Icon { get; set; }
        public string Category { get; set; } = "Général";

        private bool _isFavorite = false;
        public bool IsFavorite
        {
            get { return _isFavorite; }
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged("IsFavorite");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

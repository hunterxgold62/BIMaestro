using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.IO;
using Newtonsoft.Json;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;

namespace ScanTextRevit
{
    public partial class CorrectionResultWindow : Window
    {
        public UIDocument UiDoc { get; set; }

        // Stocke les corrections regroupées par clé (ex. "Feuille : …" ou "Vue : …")
        private Dictionary<string, List<CorrectionItem>> _allResults = new Dictionary<string, List<CorrectionItem>>();

        // Filtre courant : "", "Erreur" ou "Mineur"
        private string _currentCategoryFilter = "";

        // Préférences (mode sombre)
        private Preferences _preferences;

        // Chemin de sauvegarde des préférences
        private static string PrefFilePath
        {
            get
            {
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs",
                    "SauvegardePréférence"
                );
                Directory.CreateDirectory(baseDir);
                return Path.Combine(baseDir, "thème IA auto.json");
            }
        }

        public CorrectionResultWindow()
        {
            InitializeComponent();
            LoadPreferences();
            ApplyTheme();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { this.DragMove(); } catch { }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// Met à jour la barre de progression (0 à 100).
        /// </summary>
        public void UpdateProgressBar(double percent)
        {
            ProgressBar.Value = percent;
            ProgressText.Text = $"{(int)percent}%";
        }

        /// <summary>
        /// Ajoute les résultats partiels en filtrant les corrections inutiles
        /// (celles dont l'Explanation contient "aucune correction nécessaire" ou "aucune erreur détectée")
        /// puis rafraîchit l’affichage.
        /// </summary>
        public void AddPartialResults(string key, List<CorrectionItem> corrections)
        {
            if (!_allResults.ContainsKey(key))
                _allResults[key] = new List<CorrectionItem>();

            foreach (var c in corrections)
            {
                // Filtrage des corrections inutiles
                string expl = c.Explanation?.ToLower() ?? "";
                if (expl.Contains("aucune correction nécessaire") || expl.Contains("aucune erreur détectée"))
                    continue;

                string uniqueKey = $"{c.ElementId}||{c.OriginalText}||{c.CorrectedText}";
                if (!_allResults[key].Any(existing => $"{existing.ElementId}||{existing.OriginalText}||{existing.CorrectedText}" == uniqueKey))
                {
                    _allResults[key].Add(c);
                }
            }
            RefreshDisplay();
        }

        /// <summary>
        /// Appelée lorsque tous les chunks sont terminés.
        /// </summary>
        public void OnAllChunksCompleted()
        {
            ProgressBarPanel.Visibility = System.Windows.Visibility.Collapsed;
            TextBlock done = new TextBlock
            {
                Text = "Toutes les corrections sont terminées.",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.DarkGreen,
                Margin = new Thickness(10, 10, 10, 10)
            };
            CorrectionsPanel.Children.Add(done);
        }

        /// <summary>
        /// Rafraîchit l’affichage en affichant uniquement les groupes ayant au moins une correction.
        /// </summary>
        private void RefreshDisplay()
        {
            CorrectionsPanel.Children.Clear();

            foreach (var kvp in _allResults)
            {
                string groupKey = kvp.Key;
                var filtered = string.IsNullOrEmpty(_currentCategoryFilter)
                    ? kvp.Value
                    : kvp.Value.Where(c => c.Category.Equals(_currentCategoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                if (filtered.Count > 0)
                {
                    AddHeader(groupKey);
                    foreach (var item in filtered)
                    {
                        AddCard(item);
                    }
                }
            }
        }

        private void AddHeader(string text)
        {
            TextBlock header = new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = this.Foreground,
                Margin = new Thickness(10, 12, 10, 4)
            };
            CorrectionsPanel.Children.Add(header);
        }

        private void AddCard(CorrectionItem item)
        {
            bool isDark = _preferences.DarkMode;

            Border card = new Border
            {
                // En mode sombre, fond légèrement moins contrasté
                Background = isDark ? new SolidColorBrush(Color.FromRgb(62, 62, 66)) : Brushes.White,
                CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(isDark ? Color.FromRgb(90, 90, 90) : Color.FromRgb(220, 220, 220)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(30, 4, 10, 4),
                Padding = new Thickness(10)
            };

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Texte original
            TextBlock originalText = new TextBlock
            {
                Text = "Texte original : " + item.OriginalText,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = isDark ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black)
            };
            Grid.SetRow(originalText, 0);
            grid.Children.Add(originalText);

            // Texte corrigé et boutons
            StackPanel correctedPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 6)
            };

            // Couleur selon la catégorie (Erreur = vert, Mineur = orange)
            Color correctedColor = Colors.DarkGreen;
            if (!string.IsNullOrEmpty(item.Category) &&
                item.Category.Equals("Mineur", StringComparison.OrdinalIgnoreCase))
            {
                correctedColor = Colors.Orange;
            }
            TextBlock correctedText = new TextBlock
            {
                Text = "Texte corrigé : " + item.CorrectedText,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Width = 600,
                Foreground = new SolidColorBrush(correctedColor)
            };
            correctedPanel.Children.Add(correctedText);

            // Bouton "Copier"
            Button copyButton = new Button
            {
                Content = "Copier",
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Foreground = isDark ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black),
                BorderThickness = new Thickness(1),
                BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(120, 120, 120)) : new SolidColorBrush(Colors.Black)
            };
            copyButton.Click += (s, e) => Clipboard.SetText(item.CorrectedText);
            correctedPanel.Children.Add(copyButton);

            // Bouton "Afficher"
            Button showButton = new Button
            {
                Content = "Afficher",
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = isDark ? new SolidColorBrush(Color.FromRgb(120, 120, 120)) : new SolidColorBrush(Colors.Black)
            };
            if (int.TryParse(item.ElementId?.Trim(), out int dummy))
            {
                showButton.Click += (s, e) => ShowElement(item.ElementId);
                showButton.IsEnabled = true;
            }
            else
            {
                showButton.IsEnabled = false;
                // En mode sombre, pour un bouton désactivé, on force une couleur visible
                if (isDark)
                {
                    showButton.Foreground = new SolidColorBrush(Colors.LightGray);
                    showButton.BorderBrush = new SolidColorBrush(Colors.LightGray);
                }
            }
            // Pour assurer une bonne lisibilité, définissons la couleur si le bouton est actif
            if (showButton.IsEnabled)
            {
                showButton.Foreground = isDark ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black);
            }
            correctedPanel.Children.Add(showButton);

            Grid.SetRow(correctedPanel, 1);
            grid.Children.Add(correctedPanel);

            // Explication
            TextBlock explanationText = new TextBlock
            {
                Text = "Explication : " + item.Explanation,
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Foreground = isDark ? new SolidColorBrush(Colors.LightGray) : new SolidColorBrush(Colors.Gray)
            };
            Grid.SetRow(explanationText, 2);
            grid.Children.Add(explanationText);

            card.Child = grid;
            CorrectionsPanel.Children.Add(card);
        }


        private void ShowElement(string elementIdStr)
        {
            try
            {
                if (UiDoc != null && int.TryParse(elementIdStr?.Trim(), out int idValue))
                {
                    var elemId = new ElementId(idValue);
                    var element = UiDoc.Document.GetElement(elemId);
                    if (element == null)
                    {
                        MessageBox.Show("L'élément n'a pas pu être trouvé dans le document.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // Tenter de déterminer la vue propriétaire de l'élément
                    ElementId ownerViewId = null;
                    if (element is TextNote tn)
                    {
                        ownerViewId = tn.OwnerViewId;
                    }
                    else if (element is IndependentTag tag)
                    {
                        ownerViewId = tag.OwnerViewId;
                    }
                    // Vous pouvez ajouter d'autres cas si nécessaire

                    // Si on a trouvé une vue propriétaire et que ce n'est pas la vue active, on change de vue
                    if (ownerViewId != null && !ownerViewId.Equals(UiDoc.ActiveView.Id))
                    {
                        View ownerView = UiDoc.Document.GetElement(ownerViewId) as View;
                        if (ownerView != null)
                        {
                            UiDoc.RequestViewChange(ownerView);
                        }
                    }

                    // Afficher et sélectionner l'élément
                    UiDoc.ShowElements(new List<ElementId> { elemId });
                    UiDoc.Selection.SetElementIds(new List<ElementId> { elemId });
                }
                else
                {
                    MessageBox.Show("L'identifiant de l'élément n'est pas valide.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur lors de l'affichage de l'élément : " + ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }



        // Gestion des Préférences

        private void LoadPreferences()
        {
            if (File.Exists(PrefFilePath))
            {
                try
                {
                    string json = File.ReadAllText(PrefFilePath);
                    _preferences = JsonConvert.DeserializeObject<Preferences>(json);
                }
                catch
                {
                    _preferences = new Preferences();
                }
            }
            else
            {
                _preferences = new Preferences();
            }
            DarkModeCheckBox.IsChecked = _preferences.DarkMode;
        }

        private void SavePreferences()
        {
            string json = JsonConvert.SerializeObject(_preferences, Formatting.Indented);
            File.WriteAllText(PrefFilePath, json);
        }

        private void ApplyTheme()
        {
            if (_preferences.DarkMode)
            {
                // Palette harmonieuse pour le mode sombre
                MainBorder.Background = new SolidColorBrush(Color.FromRgb(45, 45, 48));
                this.Foreground = new SolidColorBrush(Colors.White);
                FilterPanel.Background = MainBorder.Background;
                ProgressBarPanel.Background = MainBorder.Background;
                CorrectionsPanel.Background = MainBorder.Background;
                // Pour le bouton "Afficher toutes", choisissez une couleur contrastante
                ShowAllFilterButton.Foreground = new SolidColorBrush(Colors.Cyan);
            }
            else
            {
                MainBorder.Background = new SolidColorBrush(Color.FromRgb(250, 250, 250));
                this.Foreground = new SolidColorBrush(Colors.Black);
                FilterPanel.Background = MainBorder.Background;
                ProgressBarPanel.Background = MainBorder.Background;
                CorrectionsPanel.Background = MainBorder.Background;
                ShowAllFilterButton.Foreground = new SolidColorBrush(Colors.Blue);
            }
        }


        // Événements des filtres

        private void ErrorFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _currentCategoryFilter = "Erreur";
            RefreshDisplay();
        }

        private void MinorFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _currentCategoryFilter = "Mineur";
            RefreshDisplay();
        }

        private void ShowAllFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _currentCategoryFilter = "";
            RefreshDisplay();
        }

        private void DarkModeCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _preferences.DarkMode = true;
            ApplyTheme();
            SavePreferences();
            RefreshDisplay();
        }

        private void DarkModeCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _preferences.DarkMode = false;
            ApplyTheme();
            SavePreferences();
            RefreshDisplay();
        }
    }

    // Classe de préférences unique
    public class Preferences
    {
        public bool DarkMode { get; set; } = false;
    }
}

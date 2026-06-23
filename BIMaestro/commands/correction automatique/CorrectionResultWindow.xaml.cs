using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using System.IO;
using Newtonsoft.Json;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;

namespace ScanTextRevit
{
    public partial class CorrectionResultWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/ia?outil=audit-texte-ia";
        public UIDocument UiDoc { get; set; }

        // Stocke les corrections regroupées par clé (ex. "Feuille : …" ou "Vue : …")
        private Dictionary<string, List<CorrectionItem>> _allResults = new Dictionary<string, List<CorrectionItem>>();

        // Filtre courant : "", "Erreur" ou "Mineur"
        private string _currentCategoryFilter = "";

        // Préférences utilisateur
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
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            LoadPreferences();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { this.DragMove(); } catch { }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Impossible d’ouvrir la page d’aide : {ex.Message}", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
                // Normaliser le texte de l'explication et des champs textuels
                string expl = c.Explanation?.ToLowerInvariant() ?? "";
                string origTrim = c.OriginalText?.Trim() ?? "";
                string corrTrim = c.CorrectedText?.Trim() ?? "";

                // Condition pour détecter un "pas de correction"
                bool isNoCorrection =
                    expl.Contains("aucune correction nécessaire") ||
                    expl.Contains("aucune erreur détectée") ||
                    expl.Contains("texte correct") ||
                    expl.Contains("pas d'erreur") ||
                    expl.Contains("pas de correction nécessaire") ||
                    string.Equals(origTrim, corrTrim, StringComparison.OrdinalIgnoreCase);

                if (isNoCorrection)
                    continue;

                // Déduplication habituelle
                string uniqueKey = $"{c.ElementId}||{origTrim}||{corrTrim}";
                if (!_allResults[key].Any(existing =>
                    $"{existing.ElementId}||{existing.OriginalText?.Trim()}||{existing.CorrectedText?.Trim()}"
                     == uniqueKey))
                {
                    c.ViewId = ExtractIdFromKey(key);
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
            if (_currentCategoryFilter == "Repetition")
            {
                var groups = new Dictionary<string, Dictionary<string, CorrectionItem>>();
                foreach (var kvp in _allResults)
                {
                    foreach (var item in kvp.Value)
                    {
                        string norm = (item.OriginalText ?? string.Empty).Trim().ToLowerInvariant();
                        if (!groups.ContainsKey(norm))
                            groups[norm] = new Dictionary<string, CorrectionItem>();
                        groups[norm][kvp.Key] = item;
                    }
                }
                foreach (var g in groups.Values)
                {
                    if (g.Count < 2)
                        continue;
                    var first = g.First();
                    AddHeader(first.Key);
                    AddCard(first.Value, g.Count, g);
                }
                return;
            }

            if (_preferences.HideDuplicates)
            {
                // Regrouper toutes les occurrences par texte pour connaître les répétitions
                var groupsByText = new Dictionary<string, Dictionary<string, CorrectionItem>>();
                foreach (var kvp in _allResults)
                {
                    var filtered = string.IsNullOrEmpty(_currentCategoryFilter)
                        ? kvp.Value
                        : kvp.Value.Where(c => c.Category.Equals(_currentCategoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                    foreach (var item in filtered)
                    {
                        string norm = (item.OriginalText ?? string.Empty).Trim().ToLowerInvariant();
                        if (!groupsByText.TryGetValue(norm, out var reps))
                        {
                            reps = new Dictionary<string, CorrectionItem>();
                            groupsByText[norm] = reps;
                        }
                        if (!reps.ContainsKey(kvp.Key))
                            reps[kvp.Key] = item;
                    }
                }

                // Affichage : seul le premier groupe pour un texte affiche la carte, mais le menu indique les autres vues/feuilles
                foreach (var kvp in _allResults)
                {
                    string groupKey = kvp.Key;
                    var filtered = string.IsNullOrEmpty(_currentCategoryFilter)
                        ? kvp.Value
                        : kvp.Value.Where(c => c.Category.Equals(_currentCategoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                    var toShow = new List<(CorrectionItem item, Dictionary<string, CorrectionItem> reps)>();
                    foreach (var item in filtered)
                    {
                        string norm = (item.OriginalText ?? string.Empty).Trim().ToLowerInvariant();
                        var reps = groupsByText[norm];
                        string firstGroup = reps.Keys.First();
                        if (firstGroup == groupKey)
                        {
                            toShow.Add((item, reps));
                        }
                    }

                    if (toShow.Count > 0)
                    {
                        AddHeader(groupKey);
                        foreach (var pair in toShow)
                        {
                            AddCard(pair.item, pair.reps.Count, pair.reps);
                        }
                    }
                }
                return;
            }

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

        private void AddCard(CorrectionItem item, int repetitionCount = 0, Dictionary<string, CorrectionItem> repetitions = null)
        {

            Border card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
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
                Foreground = new SolidColorBrush(Colors.Black)
            };
            Grid.SetRow(originalText, 0);
            grid.Children.Add(originalText);

            // Texte corrigé et boutons
            Grid correctedPanel = new Grid
            {
                Margin = new Thickness(0, 6, 0, 6)
            };
            correctedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            correctedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            correctedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Couleur selon la catégorie (Erreur = rouge, Mineur = orange)
            Color correctedColor = Color.FromRgb(198, 40, 40);
            if (!string.IsNullOrEmpty(item.Category) &&
                item.Category.Equals("Mineur", StringComparison.OrdinalIgnoreCase))
            {
                correctedColor = Color.FromRgb(199, 119, 0);
            }
            string correctedLabel = "Texte corrigé : " + item.CorrectedText;
            if (repetitionCount > 1)
            {
                correctedLabel += $" (x{repetitionCount})";
            }
            TextBlock correctedText = new TextBlock
            {
                Text = correctedLabel,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = new SolidColorBrush(correctedColor)
            };
            Grid.SetColumn(correctedText, 0);
            correctedPanel.Children.Add(correctedText);

            // Bouton "Copier"
            Button copyButton = new Button
            {
                Content = "Copier",
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Colors.Black),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Black)
            };
            copyButton.Click += (s, e) => Clipboard.SetText(item.CorrectedText);
            Grid.SetColumn(copyButton, 1);
            correctedPanel.Children.Add(copyButton);

            // Bouton "Afficher"
            Button showButton = new Button
            {
                Content = repetitions != null && repetitions.Count > 1 ? "Afficher ▼" : "Afficher",
                Margin = new Thickness(0),
                Padding = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Black)
            };
            if (repetitions != null && repetitions.Count > 1)
            {
                ContextMenu menu = new ContextMenu();
                foreach (var kvp in repetitions)
                {
                    MenuItem mi = new MenuItem { Header = kvp.Key };
                    var corr = kvp.Value;
                    mi.Click += (s, e) => ShowElementInView(corr);
                    menu.Items.Add(mi);
                }
                showButton.Click += (s, e) =>
                {
                    menu.PlacementTarget = showButton;
                    menu.IsOpen = true;
                };
                showButton.IsEnabled = true;
            }
            else if (int.TryParse(item.ElementId?.Trim(), out int dummy))
            {
                showButton.Click += (s, e) => ShowElement(item.ElementId);
                showButton.IsEnabled = true;
            }
            else
            {
                showButton.IsEnabled = false;
            }
            // Pour assurer une bonne lisibilité, définissons la couleur si le bouton est actif
            if (showButton.IsEnabled)
            {
                showButton.Foreground = new SolidColorBrush(Colors.Black);
            }
            Grid.SetColumn(showButton, 2);
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
                Foreground = new SolidColorBrush(Colors.Gray)
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

                    if (element is ViewSheet sheet)
                    {
                        UiDoc.RequestViewChange(sheet);
                    }
                    else
                    {
                        ElementId ownerViewId = element.OwnerViewId;
                        if (ownerViewId != null &&
                            ownerViewId != ElementId.InvalidElementId &&
                            !ownerViewId.Equals(UiDoc.ActiveView.Id))
                        {
                            View ownerView = UiDoc.Document.GetElement(ownerViewId) as View;
                            if (ownerView != null)
                            {
                                UiDoc.RequestViewChange(ownerView);
                            }
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
        private void ShowElementInView(CorrectionItem item)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.ViewId) && int.TryParse(item.ViewId, out int vId))
                {
                    View view = UiDoc.Document.GetElement(new ElementId(vId)) as View;
                    if (view != null && !view.Id.Equals(UiDoc.ActiveView.Id))
                    {
                        UiDoc.RequestViewChange(view);
                    }
                }
                ShowElement(item.ElementId);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur lors de l'affichage de l'élément : " + ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string ExtractIdFromKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            var m = Regex.Match(key, @"Id\s*(\d+)");
            if (m.Success) return m.Groups[1].Value;
            return "";
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
            HideDuplicatesCheckBox.IsChecked = _preferences.HideDuplicates;

        }

        private void SavePreferences()
        {
            string json = JsonConvert.SerializeObject(_preferences, Formatting.Indented);
            File.WriteAllText(PrefFilePath, json);
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
  private void RepetitionFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _currentCategoryFilter = "Repetition";
            RefreshDisplay();
        }

        private void ShowAllFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _currentCategoryFilter = "";
            RefreshDisplay();
        }
      
       

        private void HideDuplicatesCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _preferences.HideDuplicates = true;
            SavePreferences();
            RefreshDisplay();
        }

        private void HideDuplicatesCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _preferences.HideDuplicates = false;
            SavePreferences();
            RefreshDisplay();
        }
    }

    // Classe de préférences unique
    public class Preferences
    {
        public bool HideDuplicates { get; set; } = false;
    }
}

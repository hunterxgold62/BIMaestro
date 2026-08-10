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
using BIMaestro.Localization;

namespace ScanTextRevit
{
    public partial class CorrectionResultWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/ia?outil=audit-texte-ia";
        public UIDocument UiDoc { get; private set; }
        private readonly ShowCorrectionElementHandler _showElementHandler;
        private readonly ExternalEvent _showElementEvent;

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
            : this(null)
        {
        }

        public CorrectionResultWindow(UIDocument uiDoc)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            UiDoc = uiDoc;
            if (uiDoc != null)
            {
                _showElementHandler = new ShowCorrectionElementHandler(uiDoc.Document);
                _showElementEvent = ExternalEvent.Create(_showElementHandler);
            }
            Closed += CorrectionResultWindow_Closed;
            LoadPreferences();
        }

        private void CorrectionResultWindow_Closed(object sender, EventArgs e)
        {
            try { _showElementEvent?.Dispose(); } catch { }
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
                MessageBox.Show(UiLanguage.T($"Impossible d’ouvrir la page d’aide : {ex.Message}", $"Unable to open the help page: {ex.Message}"), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                Text = UiLanguage.T("Toutes les corrections sont terminées.", "All corrections are complete."),
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
            if (UiLanguage.IsEnglish && !string.IsNullOrWhiteSpace(text))
            {
                if (text.StartsWith("Feuille :", StringComparison.CurrentCultureIgnoreCase))
                    text = "Sheet:" + text.Substring("Feuille :".Length);
                else if (text.StartsWith("Vue :", StringComparison.CurrentCultureIgnoreCase))
                    text = "View:" + text.Substring("Vue :".Length);
                else if (text.StartsWith("Nomenclature :", StringComparison.CurrentCultureIgnoreCase))
                    text = "Schedule:" + text.Substring("Nomenclature :".Length);
            }

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
                Text = UiLanguage.T("Texte original : ", "Original Text: ") + item.OriginalText,
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
            string correctedLabel = UiLanguage.T("Texte corrigé : ", "Corrected Text: ") + item.CorrectedText;
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
                Content = UiLanguage.T("Copier", "Copy"),
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
                Content = repetitions != null && repetitions.Count > 1
                    ? UiLanguage.T("Afficher ▼", "Show ▼")
                    : UiLanguage.T("Afficher", "Show"),
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
                    mi.IsEnabled = TryGetElementId(corr, out _);
                    mi.Click += (s, e) => QueueShowElement(corr);
                    menu.Items.Add(mi);
                }
                showButton.Click += (s, e) =>
                {
                    menu.PlacementTarget = showButton;
                    menu.IsOpen = true;
                };
                showButton.IsEnabled = true;
            }
            else if (TryGetElementId(item, out _))
            {
                showButton.Click += (s, e) => QueueShowElement(item);
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
                Text = UiLanguage.T("Explication : ", "Explanation: ") + item.Explanation,
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


        private static bool TryGetElementId(CorrectionItem item, out long elementId)
        {
            elementId = -1;
            return item != null &&
                   long.TryParse(item.ElementId?.Trim(), out elementId) &&
                   elementId > 0;
        }

        private void QueueShowElement(CorrectionItem item)
        {
            if (!TryGetElementId(item, out long elementId) ||
                _showElementHandler == null ||
                _showElementEvent == null)
            {
                MessageBox.Show(
                    UiLanguage.T("L'élément ne peut pas être affiché.", "The element cannot be displayed."),
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            long? viewId = null;
            if (long.TryParse(item.ViewId?.Trim(), out long parsedViewId) && parsedViewId > 0)
                viewId = parsedViewId;

            _showElementHandler.SetRequest(elementId, viewId);
            ExternalEventRequest result = _showElementEvent.Raise();
            if (result == ExternalEventRequest.Denied)
            {
                MessageBox.Show(
                    UiLanguage.T("Revit ne peut pas traiter cette demande pour le moment.", "Revit cannot process this request right now."),
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private sealed class ShowCorrectionElementHandler : IExternalEventHandler
        {
            private readonly Document _document;
            private readonly object _requestLock = new object();
            private long? _elementId;
            private long? _viewId;

            public ShowCorrectionElementHandler(Document document)
            {
                _document = document;
            }

            public void SetRequest(long elementId, long? viewId)
            {
                lock (_requestLock)
                {
                    _elementId = elementId;
                    _viewId = viewId;
                }
            }

            public void Execute(UIApplication app)
            {
                long? elementId;
                long? requestedViewId;
                lock (_requestLock)
                {
                    elementId = _elementId;
                    requestedViewId = _viewId;
                    _elementId = null;
                    _viewId = null;
                }

                if (!elementId.HasValue)
                    return;

                try
                {
                    UIDocument uiDoc = app.ActiveUIDocument;
                    if (uiDoc == null || _document == null || !_document.IsValidObject ||
                        !ReferenceEquals(uiDoc.Document, _document))
                    {
                        TaskDialog.Show(
                            "BIMaestro",
                            UiLanguage.T("Le document analysé n'est plus le document actif. Revenez dans ce document puis réessayez.", "The analyzed document is no longer active. Return to that document and try again."));
                        return;
                    }

                    Element element = _document.GetElement(CreateElementId(elementId.Value));
                    if (element == null)
                    {
                        TaskDialog.Show("BIMaestro", UiLanguage.T("L'élément n'existe plus dans le document.", "The element no longer exists in the document."));
                        return;
                    }

                    View targetView = null;
                    if (requestedViewId.HasValue)
                        targetView = _document.GetElement(CreateElementId(requestedViewId.Value)) as View;

                    if (targetView == null && element is View elementView)
                        targetView = elementView;

                    if (targetView == null &&
                        element.OwnerViewId != null &&
                        element.OwnerViewId != ElementId.InvalidElementId)
                    {
                        targetView = _document.GetElement(element.OwnerViewId) as View;
                    }

                    if (targetView != null &&
                        !targetView.IsTemplate &&
                        !targetView.Id.Equals(uiDoc.ActiveView.Id))
                    {
                        // Changement synchrone dans le contexte ExternalEvent :
                        // le zoom n'est lancé qu'une fois la vue réellement active.
                        uiDoc.ActiveView = targetView;
                    }

                    // Les textes de paramètres de feuilles/nomenclatures sont rattachés
                    // à la vue elle-même : l'activer suffit, ShowElements(View) est évité.
                    if (element is View)
                        return;

                    var ids = new List<ElementId> { element.Id };
                    uiDoc.Selection.SetElementIds(ids);
                    uiDoc.ShowElements(ids);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(
                        "BIMaestro",
                        UiLanguage.T("Impossible d'afficher cet élément sans risque pour Revit.\n\n", "Unable to display this element safely in Revit.\n\n") + ex.Message);
                }
            }

            public string GetName()
            {
                return "BIMaestro - Afficher une correction de texte";
            }

            private static ElementId CreateElementId(long value)
            {
                return ElementIdExtensions.CreateElementId(value);
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

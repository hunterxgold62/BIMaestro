using System;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Grid = System.Windows.Controls.Grid;

namespace BIMaestro.Home
{
    public sealed class HomeAppearanceSettings
    {
        public string Style { get; set; } = "native";
        public bool IncludeHistory { get; set; }
    }
    public class HomeAppearanceAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }

    [Transaction(TransactionMode.Manual)]
    public class HomeAppearanceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                Couleur.CustomizeRibbonColorsCommand.ShowPreferences(data.Application, "home");
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    internal sealed class HomeAppearanceEditor : UserControl
    {
        private int _selectedIndex;
        private readonly CheckBox _history;
        private readonly System.Collections.Generic.List<RadioButton> _choices = new System.Collections.Generic.List<RadioButton>();
        private readonly System.Collections.Generic.List<Border> _choiceFrames = new System.Collections.Generic.List<Border>();
        private readonly TextBlock _description;
        private readonly string[] _keys = { "native", "2023", "2024", "2025", "2026" };
        private readonly string[] _descriptions = {
            "La présentation d’origine de Revit, avec vos habitudes et vos repères.",
            "Des cartes sobres et des projets séparés des familles, pour aller à l’essentiel.",
            "Un registre organisé avec les noms, dates et dossiers de vos fichiers.",
            "Des cartes horizontales et des aperçus pour reconnaître vos fichiers au premier regard.",
            "Une liste compacte et des colonnes alignées pour parcourir rapidement vos fichiers."
        };
        internal event Action ShowHomeRequested;
        internal bool HasChanges => _keys[_selectedIndex] != HomeAppearanceService.CurrentStyle || (_history.IsChecked == true) != HomeAppearanceService.HistoryEnabled;
        internal HomeAppearanceEditor()
        {
            var root = new StackPanel();
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8, 4, 4) };
            root.Children.Add(Text("Accueil Revit", "H2"));
            var intro = Text("Choisissez votre présentation et les fichiers à retrouver dès l’ouverture de Revit. Les couleurs suivent automatiquement son thème clair ou sombre.", "Hint");
            intro.Margin = new Thickness(0, 0, 0, 14); root.Children.Add(intro);
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) }); root.Children.Add(body);
            var styles = new StackPanel(); styles.Children.Add(Text("La présentation de votre accueil", "H2"));
            _selectedIndex = Math.Max(0, Array.IndexOf(_keys, HomeAppearanceService.CurrentStyle));
            _description = Text(_descriptions[_selectedIndex], "Hint");
            var labels = new[] { "Autodesk", "Atelier essentiel", "Bureau de projets", "Bibliothèque", "Index" };
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var details = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
                var name = Text(labels[i], "H2"); name.Margin = new Thickness(0, 0, 0, 4); details.Children.Add(name);
                details.Children.Add(Text(_descriptions[i], "Hint"));
                var choice = new RadioButton { Content = details, GroupName = "HomeStyle", FontSize = 13,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, IsChecked = i == _selectedIndex };
                choice.SetResourceReference(ForegroundProperty, "Text.Primary");
                var frame = new Border { Child = choice, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8) };
                frame.MouseLeftButtonUp += (_, __) => choice.IsChecked = true;
                choice.Checked += (_, __) => { _selectedIndex = index; _description.Text = _descriptions[index]; RefreshChoices(); };
                styles.Children.Add(frame); _choices.Add(choice); _choiceFrames.Add(frame);
            }
            var stylesCard = Card(styles); body.Children.Add(stylesCard);
            var side = new StackPanel(); Grid.SetColumn(side, 2); body.Children.Add(side);
            var journal = new StackPanel(); journal.Children.Add(Text("Vos fichiers récents", "H2"));
            _history = new CheckBox { Content = new TextBlock { Text = "Retrouver les projets et familles du journal Excel", TextWrapping = TextWrapping.Wrap }, IsChecked = HomeAppearanceService.HistoryEnabled, Margin = new Thickness(0, 6, 0, 12) };
            _history.SetResourceReference(ForegroundProperty, "Text.Primary"); journal.Children.Add(_history);
            journal.Children.Add(Text("Recherchez un fichier par son nom ou son dossier, puis ouvrez-le directement depuis l’accueil.", "Hint"));
            side.Children.Add(Card(journal));
            var actions = new StackPanel(); actions.Children.Add(Text("Votre accueil, à votre façon", "H2"));
            _description.Margin = new Thickness(0, 4, 0, 12); actions.Children.Add(_description);
            actions.Children.Add(Text("Enregistrez vos réglages avec le bouton en bas de la fenêtre, ou affichez votre accueil dès maintenant.", "Hint"));
            var show = new Button { Content = "Enregistrer et afficher l’accueil", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 14, 0, 0) };
            show.SetResourceReference(StyleProperty, "PrimaryButton"); show.Click += (_, __) => ShowHomeRequested?.Invoke(); actions.Children.Add(show);
            side.Children.Add(Card(actions)); RefreshChoices();
        }
        private void RefreshChoices()
        {
            for (int i = 0; i < _choiceFrames.Count; i++)
            {
                bool selected = i == _selectedIndex;
                _choiceFrames[i].SetResourceReference(Border.BackgroundProperty, selected ? "Surface" : "Surface.Subtle");
                _choiceFrames[i].SetResourceReference(Border.BorderBrushProperty, selected ? "Brand" : "Border");
                _choiceFrames[i].BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }
        internal HomeAppearanceSettings CaptureSettings() => new HomeAppearanceSettings { Style = _keys[_selectedIndex], IncludeHistory = _history.IsChecked == true };
        internal void LoadSettings(HomeAppearanceSettings settings)
        {
            int index = Array.IndexOf(_keys, settings.Style);
            if (index < 0) throw new ArgumentException("Accueil Revit inconnu.");
            _selectedIndex = index; _choices[index].IsChecked = true;
            _description.Text = _descriptions[index]; _history.IsChecked = settings.IncludeHistory;
            RefreshChoices();
        }
        internal void SaveChanges() => HomeAppearanceService.SetPreferences(_keys[_selectedIndex], _history.IsChecked == true);
        private static TextBlock Text(string value, string style)
        {
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(StyleProperty, style); return text;
        }
        private static Border Card(UIElement content)
        {
            var card = new Border { Child = content, Margin = new Thickness(0, 0, 0, 12) };
            card.SetResourceReference(StyleProperty, "Card"); return card;
        }
    }
}

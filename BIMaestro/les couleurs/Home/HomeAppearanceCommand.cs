using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIMaestro.Home
{
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
                var window = new HomeAppearanceWindow();
                new WindowInteropHelper(window).Owner = data.Application.MainWindowHandle;
                if (window.ShowDialog() != true) return Result.Cancelled;
                HomeAppearanceService.SetPreferences(window.SelectedStyle, window.IncludeHistory);
                // This ribbon command is reached from the workspace, including an empty one.
                var command = RevitCommandId.LookupPostableCommandId(PostableCommand.ToggleHome);
                if (data.Application.CanPostCommand(command)) data.Application.PostCommand(command);
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; TaskDialog.Show("Personnaliser l’accueil · Test", ex.Message); return Result.Failed; }
        }
    }

    internal sealed class HomeAppearanceWindow : Window
    {
        int selectedIndex;
        readonly CheckBox history;
        readonly TextBlock description;
        readonly string[] keys = { "native", "2023", "2024", "2025", "2026" };
        readonly string[] descriptions = {
            "Conserver la présentation Autodesk. Le catalogue du journal peut être activé séparément.",
            "Cartes sobres, compteurs simples et séparation des projets et familles. Aperçus de 88 px.",
            "Registre de fichiers : noms, dates, tailles disponibles et dossiers alignés. Aperçus de 56 px.",
            "Cartes horizontales de hauteur régulière : noms lisibles et aperçus compacts de 72 px.",
            "Liste compacte avec colonnes alignées et alternance discrète des fonds. Aperçus de 44 px."
        };
        internal string SelectedStyle => keys[selectedIndex];
        internal bool IncludeHistory => history.IsChecked == true;
        internal HomeAppearanceWindow()
        {
            Title = "Personnaliser l’accueil · Test"; Width = 580; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var dark = HomeAppearanceService.IsDarkTheme();
            var background = Brush(dark ? "#202624" : "#F5F7F5");
            var foreground = Brush(dark ? "#ECF2EE" : "#173D2C");
            var muted = Brush(dark ? "#BAC8C0" : "#52645A");
            Background = background; Foreground = foreground;
            var root = new StackPanel { Margin = new Thickness(26) }; Content = root;
            root.Children.Add(new TextBlock { Text = "Choisir votre accueil", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            root.Children.Add(new TextBlock { Text = "Les quatre propositions sont disponibles dans cette version de Revit.", TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 0, 0, 22) });
            description = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = muted, FontSize = 14, LineHeight = 20, Padding = new Thickness(0, 4, 0, 4), MinHeight = 60, Margin = new Thickness(0, 0, 0, 16) }; root.Children.Add(description);
            selectedIndex = Math.Max(0, Array.IndexOf(keys, HomeAppearanceService.CurrentStyle));
            var styles = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var labels = new[] { "Autodesk — Affichage d’origine", "Atelier essentiel — Proposition 2023", "Bureau de projets — Proposition 2024", "Bibliothèque — Proposition 2025", "Index — Proposition 2026" };
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var choice = new RadioButton { Content = labels[i], GroupName = "HomeStyle", Foreground = foreground, FontSize = 15, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 3, 0, 3), IsChecked = i == selectedIndex };
                choice.Checked += (_, __) => { selectedIndex = index; description.Text = descriptions[index]; };
                styles.Children.Add(choice);
            }
            root.Children.Insert(2, styles);
            description.Text = descriptions[selectedIndex];
            history = new CheckBox { Content = "Afficher le catalogue du journal Excel", IsChecked = HomeAppearanceService.HistoryEnabled, Foreground = foreground, FontSize = 15, Margin = new Thickness(0, 0, 0, 8) }; root.Children.Add(history);
            root.Children.Add(new TextBlock { Text = "Retrouver les projets et familles déjà journalisés, rechercher leur nom ou leur dossier, puis les ouvrir. Le journal est lu en arrière-plan et reste inchangé.", TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(22, 0, 0, 18) });
            root.Children.Add(new TextBlock { Text = "Thème : " + (dark ? "sombre" : "clair") + " · suit automatiquement Revit.\nLe choix est mémorisé. Les modèles ne sont pas modifiés.", TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 0, 0, 22) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Annuler", IsCancel = true, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 10, 0) };
            var apply = new Button { Content = "Appliquer et afficher l’accueil", IsDefault = true, Background = Brush("#0F5132"), Foreground = Brushes.White, Padding = new Thickness(18, 9, 18, 9) };
            apply.Click += (_, __) => DialogResult = true;
            buttons.Children.Add(cancel); buttons.Children.Add(apply); root.Children.Add(buttons);
        }
        static SolidColorBrush Brush(string color) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(color));
    }
}

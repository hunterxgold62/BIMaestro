using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using BIMaestro.Localization;

namespace Famille
{
    public partial class ParameterSelectionWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/famille?outil=purge-parametres";
        public ObservableCollection<ParameterSelection> Parameters { get; set; }

        public ParameterSelectionWindow(ObservableCollection<ParameterSelection> parameters)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            Parameters = parameters;
            DataContext = this;
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

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ToggleSelection_Click(object sender, RoutedEventArgs e)
        {
            // Vérifier si tous sont déjà cochés
            bool allSelected = Parameters.All(p => p.IsSelected);
            // Si tous sont cochés, décocher tout, sinon cocher tout
            foreach (var param in Parameters)
            {
                param.IsSelected = !allSelected;
            }

            // Mettre à jour l'affichage du DataGrid
            dataGridParameters.Items.Refresh();

            // Mettre à jour le texte du bouton en fonction de l'état
            ToggleButton.Content = allSelected ? UiLanguage.T("Tout cocher", "Select All") : UiLanguage.T("Tout décocher", "Clear All");
        }
    }
}

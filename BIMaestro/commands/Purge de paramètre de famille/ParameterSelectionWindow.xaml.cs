using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace MyRevitPlugin
{
    public partial class ParameterSelectionWindow : Window
    {
        public ObservableCollection<ParameterSelection> Parameters { get; set; }

        public ParameterSelectionWindow(ObservableCollection<ParameterSelection> parameters)
        {
            InitializeComponent();
            Parameters = parameters;
            DataContext = this;
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
            ToggleButton.Content = allSelected ? "Tout cocher" : "Tout décocher";
        }
    }
}

using System.Windows;
using System.Windows.Controls; // pour SelectionChangedEventArgs
using Microsoft.Win32;

namespace Modification
{
    public partial class ConfigureDynamoWindow : Window
    {
        public int SelectedButtonIndex { get; private set; }
        public string SelectedPath { get; private set; }

        public ConfigureDynamoWindow()
        {
            InitializeComponent();

            // On branche l'événement après avoir construit tous les contrôles
            ButtonComboBox.SelectionChanged += ButtonComboBox_SelectionChanged;

            // Initialise le TextBox avec le chemin du bouton 1
            PathTextBox.Text = DynamoSettings.GetPath(0);
        }

        // Met à jour le TextBox à chaque changement de sélection
        private void ButtonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = ButtonComboBox.SelectedIndex;
            PathTextBox.Text = DynamoSettings.GetPath(idx);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choisir un fichier Dynamo (.dyn)",
                Filter = "Fichiers Dynamo (*.dyn)|*.dyn",
                InitialDirectory = System.IO.Path.GetDirectoryName(
                    DynamoSettings.GetPath(ButtonComboBox.SelectedIndex))
            };
            if (dlg.ShowDialog() == true)
                PathTextBox.Text = dlg.FileName;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PathTextBox.Text))
            {
                MessageBox.Show("Veuillez sélectionner un fichier .dyn.",
                                "Attention",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            SelectedButtonIndex = ButtonComboBox.SelectedIndex;
            SelectedPath = PathTextBox.Text;
            DialogResult = true;
        }
    }
}

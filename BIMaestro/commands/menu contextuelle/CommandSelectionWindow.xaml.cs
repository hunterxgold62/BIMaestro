using System.Collections.Generic;
using System.Windows;

namespace TonNamespace
{
    public partial class CommandSelectionWindow : Window
    {
        public string SelectedCommand { get; private set; }
        public CommandSelectionWindow(List<string> availableCommands)
        {
            InitializeComponent();
            lstCommands.ItemsSource = availableCommands;
            // Aucune affectation du Owner n'est effectuée ici afin d'éviter des conflits
        }

        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            if (lstCommands.SelectedItem != null)
            {
                SelectedCommand = lstCommands.SelectedItem.ToString();
                this.DialogResult = true;
            }
            else
            {
                MessageBox.Show("Veuillez sélectionner une commande.");
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }
    }
}

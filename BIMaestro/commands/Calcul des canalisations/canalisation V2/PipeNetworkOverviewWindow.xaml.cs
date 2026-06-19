using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using Autodesk.Revit.DB;

namespace Analyse
{
    public partial class PipeNetworkInteractionWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/analyse?outil=calcul-canalisations";
        private readonly Action<HashSet<ElementId>> _selectInRevit;
        public ObservableCollection<PipeNetworkDisplayItem> Networks { get; }

        public PipeNetworkInteractionWindow(IEnumerable<PipeNetworkDisplayItem> networks, Action<HashSet<ElementId>> selectInRevit)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _selectInRevit = selectInRevit;
            Networks = new ObservableCollection<PipeNetworkDisplayItem>(networks);
            DataContext = this;
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d’ouvrir la page d’aide : {ex.Message}", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void NetworksList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (NetworksList.SelectedItem is PipeNetworkDisplayItem item)
            {
                _selectInRevit?.Invoke(item.ElementIds);
            }
        }
    }
}

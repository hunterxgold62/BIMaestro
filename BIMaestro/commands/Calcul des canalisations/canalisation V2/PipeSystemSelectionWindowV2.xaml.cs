using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MyRevitPluginV2
{
    /// <summary>
    /// Interaction logic for PipeSystemTypeSelectionWindowV2.xaml
    /// </summary>
    public partial class PipeSystemTypeSelectionWindowV2 : Window
    {
        public bool IncludeDucts { get; private set; }
        public bool FilterBySystemType { get; private set; }
        public List<string> SelectedSystemTypes { get; private set; }
        public bool ExportToExcel { get; private set; }

        public PipeSystemTypeSelectionWindowV2(List<string> systemTypes)
        {
            InitializeComponent();

            // Lier la liste des Types de système au ItemsControl
            SystemTypeList.ItemsSource = systemTypes.OrderBy(st => st).Select(st => new CheckBox { Content = st, IsChecked = true });

            // Par défaut, cacher la liste des Types de système et le bouton "Désélectionner tout"
            InstructionText.Visibility = Visibility.Collapsed;
            SystemTypeScrollViewer.Visibility = Visibility.Collapsed;
            DeselectAllButton.Visibility = Visibility.Collapsed;
        }

        private void EnableSystemTypeFilterCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            InstructionText.Visibility = Visibility.Visible;
            SystemTypeScrollViewer.Visibility = Visibility.Visible;
            DeselectAllButton.Visibility = Visibility.Visible;
            FilterBySystemType = true;
        }

        private void EnableSystemTypeFilterCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            InstructionText.Visibility = Visibility.Collapsed;
            SystemTypeScrollViewer.Visibility = Visibility.Collapsed;
            DeselectAllButton.Visibility = Visibility.Collapsed;
            FilterBySystemType = false;
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            // Parcours de tous les CheckBox de la liste et désélectionne chacun
            foreach (var item in SystemTypeList.Items)
            {
                if (item is CheckBox cb)
                {
                    cb.IsChecked = false;
                }
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilterBySystemType)
            {
                // Récupérer les Types de système sélectionnés
                SelectedSystemTypes = new List<string>();
                foreach (CheckBox cb in SystemTypeList.Items)
                {
                    if (cb.IsChecked == true)
                    {
                        SelectedSystemTypes.Add(cb.Content.ToString());
                    }
                }

                if (SelectedSystemTypes.Count == 0)
                {
                    MessageBox.Show("Veuillez sélectionner au moins un Type de système.", "Avertissement", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            IncludeDucts = IncludeDuctsCheckBox.IsChecked == true;
            ExportToExcel = ExportToExcelCheckBox.IsChecked == true;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

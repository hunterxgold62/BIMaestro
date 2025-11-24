using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Modification
{
    public partial class ConfigureDynamoWindow : Window
    {
        public int SelectedButtonIndex { get; private set; }
        public string SelectedPath { get; private set; }
        public string SelectedLabel { get; private set; }

        public ConfigureDynamoWindow()
        {
            InitializeComponent();

            ButtonComboBox.SelectionChanged += ButtonComboBox_SelectionChanged;
            UpdateSelectionFields(0);
        }

        private void ButtonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectionFields(ButtonComboBox.SelectedIndex);
        }

        private void UpdateSelectionFields(int idx)
        {
            if (idx < 0)
                return;

            PathTextBox.Text = DynamoSettings.GetPath(idx);
            LabelTextBox.Text = DynamoSettings.GetLabel(idx)
                .Replace("\n", Environment.NewLine);
            UpdatePreview();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choisir un fichier Dynamo (.dyn)",
                Filter = "Fichiers Dynamo (*.dyn)|*.dyn",
                InitialDirectory = GetInitialDirectory()
            };
            if (dlg.ShowDialog() == true)
                PathTextBox.Text = dlg.FileName;
        }

        private string GetInitialDirectory()
        {
            string currentPath = PathTextBox.Text;
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                var directory = Path.GetDirectoryName(currentPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    return directory;
            }

            var fallback = Path.GetDirectoryName(DynamoSettings.GetPath(ButtonComboBox.SelectedIndex));
            if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
                return fallback;

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
            SelectedPath = PathTextBox.Text.Trim();
            SelectedLabel = LabelTextBox.Text;
            DialogResult = true;
        }

        private void LabelTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            string previewText = string.IsNullOrWhiteSpace(LabelTextBox.Text)
                ? DynamoSettings.GetLabel(ButtonComboBox.SelectedIndex).Replace("\n", Environment.NewLine)
                : LabelTextBox.Text;
            PreviewTextBlock.Text = previewText;
        }
    }
}
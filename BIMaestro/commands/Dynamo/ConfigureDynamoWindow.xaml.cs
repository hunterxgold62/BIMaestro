using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Modification
{
    public partial class ConfigureDynamoWindow : Window
    {
        private const string HelpUrl = "https://bimaestro.net/";
        private class PathEntry : INotifyPropertyChanged
        {
            private string path;
            private string label;

            public string Path
            {
                get => path;
                set
                {
                    if (path == value)
                        return;
                    path = value;
                    OnPropertyChanged(nameof(Path));
                }
            }

            public string Label
            {
                get => label;
                set
                {
                    if (label == value)
                        return;
                    label = value;
                    OnPropertyChanged(nameof(Label));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private readonly ObservableCollection<PathEntry> _paths = new ObservableCollection<PathEntry>();
        private bool _isUpdatingPaths;

        public int SelectedButtonIndex { get; private set; }
        public ReadOnlyCollection<string> SelectedPaths { get; private set; }
        public string SelectedLabel { get; private set; }

        public ConfigureDynamoWindow()
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            PathsItemsControl.ItemsSource = _paths;
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

            LoadPaths(DynamoSettings.GetPaths(idx));
            LabelTextBox.Text = DynamoSettings.GetLabel(idx)
                .Replace("\n", Environment.NewLine);
            UpdatePreview();
        }

        private void LoadPaths(IReadOnlyCollection<string> paths)
        {
            _isUpdatingPaths = true;
            _paths.Clear();

            if (paths != null)
            {
                foreach (var path in paths)
                    _paths.Add(new PathEntry { Path = path });
            }

            if (_paths.Count == 0)
                _paths.Add(new PathEntry());

            _isUpdatingPaths = false;
            RefreshPathLabels();
        }

        private string GetInitialDirectory(string currentPath)
        {
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                var directory = Path.GetDirectoryName(currentPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    return directory;
            }

            var currentPaths = DynamoSettings.GetPaths(ButtonComboBox.SelectedIndex);
            var fallback = Path.GetDirectoryName(currentPaths.FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
                return fallback;

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedPaths = _paths
                .Select(p => p.Path?.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            if (selectedPaths.Count == 0)
            {
                MessageBox.Show("Veuillez sélectionner au moins un fichier .dyn.",
                                "Attention",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            SelectedButtonIndex = ButtonComboBox.SelectedIndex;
            SelectedPaths = selectedPaths.AsReadOnly();
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

        private void BrowsePathButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not PathEntry entry)
                return;

            var dlg = new OpenFileDialog
            {
                Title = "Choisir un fichier Dynamo (.dyn)",
                Filter = "Fichiers Dynamo (*.dyn)|*.dyn",
                InitialDirectory = GetInitialDirectory(entry.Path)
            };

            if (dlg.ShowDialog() == true)
                entry.Path = dlg.FileName;
        }

        private void AddPathButton_Click(object sender, RoutedEventArgs e)
        {
            _paths.Add(new PathEntry());
            RefreshPathLabels();
        }

        private void RemovePathButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not PathEntry entry)
                return;

            if (_paths.Count <= 1)
            {
                MessageBox.Show("Au moins un chemin est requis.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _paths.Remove(entry);
            RefreshPathLabels();
        }

        private void RefreshPathLabels()
        {
            if (_isUpdatingPaths)
                return;

            for (int i = 0; i < _paths.Count; i++)
            {
                _paths[i].Label = $"Chemin {i + 1}";
            }
        }
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d’ouvrir la page d’aide : {ex.Message}", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
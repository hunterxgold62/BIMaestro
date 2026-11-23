using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Modification;
using Color = System.Windows.Media.Color;
using Forms = System.Windows.Forms;
using BIMaestro.Localization;

namespace Analyse
{
    public partial class CollaborativeModelTrackerWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/analyse?outil=suivi-maquette";
        private static string AllUsersLabel => UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.Everyone");
        private static string AllVersionsLabel => UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.AllVersions");
        private static string AllFilesLabel => UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.AllFiles");
        private readonly Document _doc;
        private readonly UIApplication _uiapp;
        private List<CollaborativeModelRecord> _allRecords = new List<CollaborativeModelRecord>();
        private bool _hasPromptedForCommonPath;

        private sealed class ProjectCard
        {
            public string ProjectName { get; set; }
            public string ModelName { get; set; }
            public string ModelPath { get; set; }
            public string UserName { get; set; }
            public string CreatorName { get; set; }
            public string RevitVersion { get; set; }
            public Brush PastelBrush { get; set; }
        }

        public CollaborativeModelTrackerWindow(Document doc, UIApplication uiapp)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _doc = doc;
            _uiapp = uiapp;
            LoadRecords();
            ApplyFilters();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiLanguage.Format("CollaborativeModelTrackerWindow.xaml.UnableToOpenTheHelpPage0", (object)ex.Message), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadRecords()
        {
            _allRecords = CollaborativeModelTrackerStore.Load();

            var users = CollaborativeModelTrackerStore.GetKnownUsers();
            if (!users.Contains(AllUsersLabel, StringComparer.OrdinalIgnoreCase))
                users.Insert(0, AllUsersLabel);

            var previous = (UserComboBox.SelectedItem as string ?? UserComboBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(previous))
                previous = AllUsersLabel;

            UserComboBox.ItemsSource = users;
            var selectedUserItem = users.FirstOrDefault(item => string.Equals(item, previous, StringComparison.OrdinalIgnoreCase))
                                   ?? AllUsersLabel;
            UserComboBox.SelectedItem = selectedUserItem;
            UserComboBox.Text = selectedUserItem;

            var versions = _allRecords
                .Select(r => r.RevitVersion)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToList();
            versions.Insert(0, AllVersionsLabel);
            var previousVersion = (RevitVersionComboBox.SelectedItem as string ?? RevitVersionComboBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(previousVersion))
                previousVersion = AllVersionsLabel;

            RevitVersionComboBox.ItemsSource = versions;
            var selectedVersionItem = versions.FirstOrDefault(item => string.Equals(item, previousVersion, StringComparison.OrdinalIgnoreCase))
                                      ?? AllVersionsLabel;
            RevitVersionComboBox.SelectedItem = selectedVersionItem;
            RevitVersionComboBox.Text = selectedVersionItem;

            var fileTypes = _allRecords
                .Select(r => GetFileExtensionLabel(r.ModelPath, r.ModelName))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();
            fileTypes.Insert(0, AllFilesLabel);
            var previousType = (FileTypeComboBox.SelectedItem as string ?? FileTypeComboBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(previousType))
                previousType = AllFilesLabel;

            FileTypeComboBox.ItemsSource = fileTypes;
            var selectedFileTypeItem = fileTypes.FirstOrDefault(item => string.Equals(item, previousType, StringComparison.OrdinalIgnoreCase))
                                       ?? AllFilesLabel;
            FileTypeComboBox.SelectedItem = selectedFileTypeItem;
            FileTypeComboBox.Text = selectedFileTypeItem;

            InfoText.Text =
                UiLanguage.Format("CollaborativeModelTrackerWindow.xaml.StatusMessage1", (object)(_allRecords.Select(r => r.ModelName).Distinct(StringComparer.OrdinalIgnoreCase).Count()), (object)(CollaborativeModelTrackerStore.JsonPath)) +
                (string.IsNullOrWhiteSpace(CollaborativeModelTrackerStore.LastDirectoryResolutionMessage)
                    ? string.Empty
                    : $"\n{CollaborativeModelTrackerStore.LastDirectoryResolutionMessage}");

            PromptForCommonPathIfNeeded();
        }

        private void PromptForCommonPathIfNeeded()
        {
            if (_hasPromptedForCommonPath)
                return;

            _hasPromptedForCommonPath = true;

            if (!CollaborativeModelTrackerStore.IsUsingFallbackLocal)
                return;

            var result = MessageBox.Show(
                UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.NoSharedFolderIsConfiguredOrAvailableWould"),
                UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.SelectACommonPath"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.SelectTheCommonFolderUsedToStoreThe");
                dialog.SelectedPath = CollaborativeModelTrackerStore.ActiveDirectory;

                if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    return;

                if (!CollaborativeModelTrackerStore.TrySetSharedDirectory(dialog.SelectedPath, out var error))
                {
                    MessageBox.Show(UiLanguage.Format("CollaborativeModelTrackerWindow.xaml.TheSelectedPathCannotBeUsed0", (object)error),
                     UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.InvalidPath"), MessageBoxButton.OK, MessageBoxImage.Warning);
                   
                    
                    return;
                }

                LoadRecords();
                ApplyFilters();
            }
        }


        private void ApplyFilters()
        {
            var selectedUser = (UserComboBox.SelectedItem as string ?? UserComboBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(selectedUser))
                selectedUser = AllUsersLabel;

            var search = (SearchModelTextBox.Text ?? string.Empty).Trim();

            var selectedVersion = (RevitVersionComboBox.SelectedItem as string ?? RevitVersionComboBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(selectedVersion))
                selectedVersion = AllVersionsLabel;

            var selectedFileType = (FileTypeComboBox.SelectedItem as string ?? FileTypeComboBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(selectedFileType))
                selectedFileType = AllFilesLabel;

            IEnumerable<CollaborativeModelRecord> query = _allRecords;

            if (!string.IsNullOrWhiteSpace(selectedUser) &&
                !selectedUser.Equals(AllUsersLabel, StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(r => string.Equals(r.UserName ?? string.Empty, selectedUser, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(selectedVersion) &&
                !selectedVersion.Equals(AllVersionsLabel, StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(r => string.Equals(r.RevitVersion ?? string.Empty, selectedVersion, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(selectedFileType) &&
                !selectedFileType.Equals(AllFilesLabel, StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(r => string.Equals(GetFileExtensionLabel(r.ModelPath, r.ModelName), selectedFileType, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(r => !string.IsNullOrWhiteSpace(r.ModelName) &&
                                         r.ModelName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var models = query
                .GroupBy(r => string.IsNullOrWhiteSpace(r.ModelName) ? "Maquette inconnue" : r.ModelName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.TimestampDate).First())
                .OrderBy(m => m.ModelName)
                .ToList();

            var cards = new List<ProjectCard>();
            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                cards.Add(new ProjectCard
                {
                    ModelName = UiLanguage.T(m.ModelName),
                    ProjectName = UiLanguage.T(m.ProjectName),
                    ModelPath = UiLanguage.T(m.ModelPath),
                    UserName = m.UserName,
                    CreatorName = m.CreatorName,
                    RevitVersion = m.RevitVersion,
                    PastelBrush = BuildPastelBrush(i)
                });
            }

            ProjectsListBox.ItemsSource = cards;
            AutoAdjustWindowWidth(cards);
        }

        private void AutoAdjustWindowWidth(List<ProjectCard> cards)
        {
            try
            {
                int maxPathLen = cards.Count == 0
                    ? 0
                    : cards.Max(c => string.IsNullOrWhiteSpace(c.ModelPath) ? 0 : c.ModelPath.Length);

                double suggested = 1050 + Math.Min(700, Math.Max(0, maxPathLen - 80) * 4.0);
                double screenMax = Math.Max(1050, SystemParameters.WorkArea.Width - 30);
                Width = Math.Min(screenMax, suggested);
            }
            catch
            {
            }
        }

        private static Brush BuildPastelBrush(int index)
        {
            Color[] palette =
            {
                Color.FromRgb(244, 236, 255),
                Color.FromRgb(235, 247, 255),
                Color.FromRgb(232, 252, 241),
                Color.FromRgb(255, 244, 230),
                Color.FromRgb(255, 236, 242),
                Color.FromRgb(245, 245, 230),
                Color.FromRgb(232, 242, 255),
                Color.FromRgb(238, 255, 248)
            };

            return new SolidColorBrush(palette[index % palette.Length]);
        }


        private void UserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyFilters), DispatcherPriority.Background);
        }

        private void UserComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyFilters();
        }

        private void SearchModelTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private static string GetFileExtensionLabel(string modelPath, string modelName)
        {
            string ext = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(modelPath))
                    ext = Path.GetExtension(modelPath);
                if (string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(modelName))
                    ext = Path.GetExtension(modelName);
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(ext))
                return UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.NoExtension");

            return ext.Trim().TrimStart('.').ToUpperInvariant();
        }

        private void RevitVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyFilters), DispatcherPriority.Background);
        }

        private void FileTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyFilters), DispatcherPriority.Background);
        }


        private void ChangeSharedPath_Click(object sender, RoutedEventArgs e)
        {
            var explanation =
                UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.ForReliableCollaborativeTrackingSelectAFolderOn");

            var userChoice = MessageBox.Show(
                explanation + UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.WouldYouLikeToSelectOrChangeThis"),
                UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.CommonPathRecommended"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (userChoice != MessageBoxResult.Yes)
                return;

            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.SelectTheCommonServerFolderForModelTracking");
                dialog.SelectedPath = CollaborativeModelTrackerStore.ActiveDirectory;

                if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    return;

                if (!CollaborativeModelTrackerStore.TrySetSharedDirectory(dialog.SelectedPath, out var error))
                {
                    MessageBox.Show(UiLanguage.Format("CollaborativeModelTrackerWindow.xaml.TheSelectedPathCannotBeUsed0", (object)error),
                        UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.InvalidPath"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                LoadRecords();
                ApplyFilters();

                MessageBox.Show(
                    UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.TheCommonPathWasUpdatedSuccessfullyAllUsers"),
                    UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.PathUpdated"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenCurrentSelectedModelFolder();
        }

        private void ProjectsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenCurrentSelectedModelFolder();
        }

        private void OpenCurrentSelectedModelFolder()
        {
            try
            {
                if (!(ProjectsListBox.SelectedItem is ProjectCard project))
                {
                    OpenFolderPath(CollaborativeModelTrackerStore.ActiveDirectory);
                    return;
                }

                var target = ResolveFolderFromModelPath(project.ModelPath);
                OpenFolderPath(target);
            }
            catch
            {
                MessageBox.Show(UiLanguage.Get("CollaborativeModelTrackerWindow.xaml.UnableToOpenTheSelectedProjectFolder"), "Info", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string ResolveFolderFromModelPath(string modelPath)
        {
            if (!string.IsNullOrWhiteSpace(modelPath) &&
                !modelPath.Equals("Chemin non disponible", StringComparison.OrdinalIgnoreCase) &&
                !modelPath.Equals("Path unavailable", StringComparison.OrdinalIgnoreCase))
            {
                var candidates = modelPath
                    .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => (p ?? string.Empty).Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                if (candidates.Count == 0)
                    candidates.Add(modelPath);

                // Format attendu : local|partagé (comme le suivi temps par projet).
                // Si maquette partagée, on privilégie le chemin partagé.
                if (candidates.Count > 1)
                {
                    string sharedCandidate = candidates[candidates.Count - 1];
                    string sharedFolder = Directory.Exists(sharedCandidate) ? sharedCandidate : TryGetExistingDirectory(sharedCandidate);
                    if (!string.IsNullOrWhiteSpace(sharedFolder))
                        return sharedFolder;
                }

                foreach (var candidate in candidates)
                {
                    if (Directory.Exists(candidate))
                        return candidate;

                    var dir = TryGetExistingDirectory(candidate);
                    if (!string.IsNullOrWhiteSpace(dir))
                        return dir;
                }
            }

            return CollaborativeModelTrackerStore.ActiveDirectory;
        }

        private static string TryGetExistingDirectory(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return dir;
            }
            catch
            {
            }

            return null;
        }

        private static void OpenFolderPath(string folder)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
    }
}

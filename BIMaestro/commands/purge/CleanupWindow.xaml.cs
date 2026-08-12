// UI/CleanupWindow.xaml.cs
using System;
using System.Diagnostics;
using System.Windows;
using BIMaestro.Localization;

namespace Modification
{
    public partial class CleanupWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/modification?outil=purge";
        public bool DeleteViews => ViewsCheckbox.IsChecked == true;
        public bool DeleteFamilies => FamiliesCheckbox.IsChecked == true;
        public bool DeleteSchedules => SchedulesCheckbox.IsChecked == true;
        public bool DeleteHardFamilies => HardFamiliesCheckbox.IsChecked == true;

        public CleanupWindow()
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            UiLanguage.LocalizeWindow(this);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    UiLanguage.T($"Impossible d’ouvrir la page d’aide : {ex.Message}", $"Unable to open the help page: {ex.Message}"),
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}

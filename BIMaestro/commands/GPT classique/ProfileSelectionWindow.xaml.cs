using System.Windows;

namespace IA
{
    public partial class ProfileSelectionWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/ia?outil=chatbot-element";
        public string SelectedProfile { get; private set; }

        public ProfileSelectionWindow()
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(BIMaestro.Localization.UiLanguage.T($"Impossible d’ouvrir la page d’aide : {ex.Message}", $"Unable to open the help page: {ex.Message}"), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (BasiqueRadio.IsChecked == true)
                SelectedProfile = "Basique";
            else if (PersonnelleRevitRadio.IsChecked == true)
                SelectedProfile = "Personnelle Revit";
            else if (BIMManagerRadio.IsChecked == true)
                SelectedProfile = "BIM Manager";

            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}

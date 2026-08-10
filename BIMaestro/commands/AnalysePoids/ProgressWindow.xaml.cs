using System.Windows;

using BIMaestro.Localization;

namespace Analyse
{
    public partial class ProgressWindow : Window
    {
        public bool IsCancelled { get; private set; }

        public ProgressWindow()
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
        }

        public void UpdateProgress(int current, int total, string familyName)
        {
            ProgressBar.Value = (double)current / total * 100.0;
            StatusText.Text = UiLanguage.T($"Analyse de la famille {current}/{total} : {familyName}", $"Analyzing family {current}/{total}: {familyName}");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsCancelled = true;
        }
    }
}

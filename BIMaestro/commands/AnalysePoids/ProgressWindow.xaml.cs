using System.Windows;

namespace AnalysePoidsPlugin
{
    public partial class ProgressWindow : Window
    {
        public bool IsCancelled { get; private set; }

        public ProgressWindow()
        {
            InitializeComponent();
        }

        public void UpdateProgress(int current, int total, string familyName)
        {
            ProgressBar.Value = (double)current / total * 100.0;
            StatusText.Text = $"Analyse de la famille {current}/{total} : {familyName}";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsCancelled = true;
        }
    }
}

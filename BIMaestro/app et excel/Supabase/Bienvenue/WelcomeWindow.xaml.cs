using System.Text.RegularExpressions;
using System.Windows;

namespace BIMaestro.Welcome
{
    public partial class WelcomeWindow : Window
    {
        public WelcomeResultAction ResultAction { get; private set; } = WelcomeResultAction.None;

        public string Email => EmailBox?.Text?.Trim();
        public string FirstName => FirstNameBox?.Text?.Trim();
        public string LastName => LastNameBox?.Text?.Trim();

        public WelcomeWindow() => InitializeComponent();

        private void OpenGuide_Click(object sender, RoutedEventArgs e)
        {
            ResultAction = WelcomeResultAction.OpenGuide;
            DialogResult = false;
            Close();
        }

        private void OptIn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Email))
            {
                MessageBox.Show(this, "Indique un email (ou clique “Plus tard / Non merci”).",
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsValidEmail(Email))
            {
                MessageBox.Show(this, "Cet email ne semble pas valide.",
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultAction = WelcomeResultAction.OptIn;
            DialogResult = true;
            Close();
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            ResultAction = WelcomeResultAction.Snooze;
            DialogResult = false;
            Close();
        }

        private void NoThanks_Click(object sender, RoutedEventArgs e)
        {
            ResultAction = WelcomeResultAction.Dismiss;
            DialogResult = false;
            Close();
        }

        private static bool IsValidEmail(string email) =>
            Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
    }
}

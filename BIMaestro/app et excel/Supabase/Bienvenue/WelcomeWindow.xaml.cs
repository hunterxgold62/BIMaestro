using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BIMaestro.Welcome
{
    public partial class WelcomeWindow : Window
    {
        private const string GuideUrl = "https://www.bimaestro.fr";

        public WelcomeResultAction ResultAction { get; private set; } = WelcomeResultAction.None;

        public string Email => EmailBox?.Text?.Trim();
        public string FirstName => FirstNameBox?.Text?.Trim();
        public string LastName => LastNameBox?.Text?.Trim();

        public WelcomeWindow()
        {
            InitializeComponent();
            LogoImage.Source = LoadBitmapFromResource("BIMaestro.png");
        }

        private void OpenGuide_Click(object sender, RoutedEventArgs e)
        {
            // ✅ Bonus : ouvre le site, mais NE ferme PAS la fenêtre
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = GuideUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossible d’ouvrir l’exemple : " + ex.Message,
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // On ne change pas ResultAction, on ne Close() pas.
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

        private static BitmapImage LoadBitmapFromResource(string resourceFileName)
        {
            var asm = Assembly.GetExecutingAssembly();
            string resourcePath = $"BIMaestro.Resources.{resourceFileName}";

            using (var stream = asm.GetManifestResourceStream(resourcePath))
            {
                if (stream == null) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }
    }
}

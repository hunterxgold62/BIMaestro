using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using BIMaestro.Localization;

namespace BIMaestro.Welcome
{
    public partial class WelcomeWindow : Window
    {
        private const string GuideUrl = "https://www.bimaestro.fr";
        private const string LinkedInUrl = "https://www.linkedin.com/in/paul-lemert-b40921207";

        public WelcomeResultAction ResultAction { get; private set; } = WelcomeResultAction.None;

        public string Email => EmailBox?.Text?.Trim();
        public string FirstName => FirstNameBox?.Text?.Trim();
        public string LastName => LastNameBox?.Text?.Trim();

        public WelcomeWindow()
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            LogoImage.Source = LoadBitmapFromResource("BIMaestro.png");
        }

        private void OpenGuide_Click(object sender, RoutedEventArgs e)
        {
            // Ouvre le site sans fermer la fenêtre, pour laisser le choix de renseigner le contact.
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
                MessageBox.Show(this, UiLanguage.T("Impossible d’ouvrir l’exemple : ", "Unable to open the example: ") + ex.Message,
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // On ne change pas ResultAction, on ne Close() pas.
        }

        private void Contact_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = LinkedInUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, UiLanguage.T("Impossible d’ouvrir LinkedIn : ", "Unable to open LinkedIn: ") + ex.Message,
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OptIn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Email))
            {
                MessageBox.Show(this, UiLanguage.T("Indique un email, ou clique sur “Plus tard” si tu préfères passer.", "Enter an email, or click “Later” if you prefer to skip."),
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsValidEmail(Email))
            {
                MessageBox.Show(this, UiLanguage.T("Cet email ne semble pas valide.", "This email does not appear to be valid."),
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
            string[] resourcePaths =
            {
                $"BIMaestro.Resources.{resourceFileName}",
                $"BIMaestro.Resources.OLD.{resourceFileName}"
            };

            foreach (var resourcePath in resourcePaths)
            {
                using (var stream = asm.GetManifestResourceStream(resourcePath))
                {
                    if (stream == null) continue;

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }

            foreach (var resourcePath in asm.GetManifestResourceNames())
            {
                if (!resourcePath.EndsWith("." + resourceFileName, StringComparison.OrdinalIgnoreCase)) continue;

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

            return null;
        }
    }
}

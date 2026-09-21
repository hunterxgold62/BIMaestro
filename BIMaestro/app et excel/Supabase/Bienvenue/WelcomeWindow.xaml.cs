using System;
using System.ComponentModel;
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
        private const string LinkedInUrl = "https://www.linkedin.com/in/paul-lemert-b40921207";

        public WelcomeResultAction ResultAction { get; private set; } = WelcomeResultAction.None;

        public string Email => EmailBox?.Text?.Trim();
        public string FirstName => FirstNameBox?.Text?.Trim();
        public string LastName => LastNameBox?.Text?.Trim();
        private readonly bool communityProfileRequired;

        public WelcomeWindow() : this(false, null) { }

        internal WelcomeWindow(bool communityProfileRequired, WelcomeState existing)
        {
            this.communityProfileRequired = communityProfileRequired;
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            LogoImage.Source = LoadBitmapFromResource("BIMaestro.png");
            if (existing != null) { EmailBox.Text = existing.Email ?? ""; FirstNameBox.Text = existing.FirstName ?? ""; LastNameBox.Text = existing.LastName ?? ""; }
            if (communityProfileRequired)
            {
                Title = "BIMaestro — Profil de la bibliothèque commune";
                HeadingText.Text = "Bibliothèque commune"; HeadingSubtitleText.Text = "Identifiez votre profil avant un échange de famille.";
                IntroTitleText.Text = "Complétez votre profil";
                IntroText.Text = "Le nom, le prénom et l’e-mail sont demandés avant de publier ou télécharger une famille.";
                TrustTitleText.Text = "Pourquoi ces informations ?";
                TrustDetailsText.Text = "Elles permettent de reconnaître votre profil. Une identité technique protégée reste conservée sur cet ordinateur afin que vous puissiez ensuite modifier la couverture ou retirer vos propres familles.";
                EmailLabel.Text = "Votre e-mail (obligatoire)"; FirstNameLabel.Text = "Prénom (obligatoire)"; LastNameLabel.Text = "Nom (obligatoire)";
                PrivacyText.Text = "Ces informations sont enregistrées dans votre profil BIMaestro. Vous pourrez les modifier depuis Options.";
                NoThanksButton.Content = "Annuler"; LaterButton.Visibility = Visibility.Collapsed; PrimaryButton.Content = "Enregistrer et continuer";
            }
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
            if (communityProfileRequired && (string.IsNullOrWhiteSpace(FirstName) || string.IsNullOrWhiteSpace(LastName)))
            {
                MessageBox.Show(this, "Indiquez votre prénom et votre nom pour continuer avec la bibliothèque commune.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
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
            Close();
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            ResultAction = WelcomeResultAction.Snooze;
            Close();
        }

        private void NoThanks_Click(object sender, RoutedEventArgs e)
        {
            ResultAction = WelcomeResultAction.Dismiss;
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (ResultAction == WelcomeResultAction.None)
                ResultAction = WelcomeResultAction.Snooze;
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

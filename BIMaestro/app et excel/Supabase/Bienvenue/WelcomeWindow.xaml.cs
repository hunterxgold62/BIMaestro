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
        public string Company => CompanyBox?.Text?.Trim();
        private readonly bool communityProfileRequired;
        internal bool IsTutorialOffer { get; }

        public WelcomeWindow() : this(false, null, true, false) { }

        internal WelcomeWindow(bool communityProfileRequired, WelcomeState existing)
            : this(communityProfileRequired, existing, true, false) { }

        internal WelcomeWindow(bool showEmail, bool showTutorial)
            : this(false, null, showEmail, showTutorial) { }

        internal WelcomeWindow(bool showEmail, bool showTutorial, WelcomeState existing)
            : this(false, existing, showEmail, showTutorial) { }

        private WelcomeWindow(bool communityProfileRequired, WelcomeState existing, bool showEmail, bool showTutorial)
        {
            this.communityProfileRequired = communityProfileRequired;
            showTutorial = showTutorial && !showEmail;
            IsTutorialOffer = showTutorial;
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            LogoImage.Source = LoadBitmapFromResource("BIMaestro.png");
            TutorialSprite.Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage();
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(TutorialSprite,
                System.Windows.Media.BitmapScalingMode.NearestNeighbor);
            TutorialOffer.Visibility = showTutorial ? Visibility.Visible : Visibility.Collapsed;
            EmailContent.Visibility = showEmail ? Visibility.Visible : Visibility.Collapsed;
            if (!showEmail)
            {
                WelcomeFooter.Visibility = Visibility.Collapsed;
                TutorialOffer.Margin = new Thickness(0);
                WelcomeBody.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
                SizeToContent = SizeToContent.Height;
                Height = double.NaN;
                HeadingSubtitleText.Text = UiLanguage.T("Prenez vos marques dans une maquette d’essai.", "Get familiar with BIMaestro in a practice model.");
                Title = UiLanguage.T("BIMaestro — Maquette d’essai", "BIMaestro — Practice model");
                HeadingText.Text = UiLanguage.T("Essayez BIMaestro", "Try BIMaestro");
            }
            if (existing != null) { EmailBox.Text = existing.Email ?? ""; FirstNameBox.Text = existing.FirstName ?? ""; LastNameBox.Text = existing.LastName ?? ""; CompanyBox.Text = existing.Company ?? ""; }
            if (showEmail && !communityProfileRequired && !string.IsNullOrWhiteSpace(existing?.Email))
            {
                IntroTitleText.Text = UiLanguage.T("Complétez vos informations", "Complete your details");
                IntroText.Text = UiLanguage.T("Vous pouvez ajouter votre entreprise (facultatif). Vos informations déjà enregistrées sont préremplies.", "You can add your company (optional). Your saved details are pre-filled.");
                PrimaryButton.Content = UiLanguage.T("Enregistrer", "Save");
                NoThanksButton.Content = UiLanguage.T("Conserver", "Keep");
                NoThanksButton.ToolTip = UiLanguage.T("Garder mes informations actuelles sans compléter l’entreprise.", "Keep my current details without adding a company.");
            }
            if (communityProfileRequired)
            {
                Title = UiLanguage.T("BIMaestro — Profil de la bibliothèque commune", "BIMaestro — Shared library profile");
                HeadingText.Text = UiLanguage.T("Bibliothèque commune", "Shared library"); HeadingSubtitleText.Text = UiLanguage.T("Identifiez votre profil avant un échange de famille.", "Identify your profile before sharing families.");
                IntroTitleText.Text = UiLanguage.T("Complétez votre profil", "Complete your profile");
                IntroText.Text = UiLanguage.T("Le nom, le prénom et l’e-mail sont demandés avant de publier ou télécharger une famille.", "Your last name, first name and email are required before publishing or downloading a family.");
                TrustTitleText.Text = UiLanguage.T("Pourquoi ces informations ?", "Why do we need this?");
                TrustDetailsText.Text = UiLanguage.T("Elles permettent de reconnaître votre profil. Une identité technique protégée reste conservée sur cet ordinateur afin que vous puissiez ensuite modifier la couverture ou retirer vos propres familles.", "It lets us recognize your profile. A protected technical identity is stored on this computer so you can later change the cover image or remove your own families.");
                EmailLabel.Text = UiLanguage.T("Votre e-mail (obligatoire)", "Your email (required)"); FirstNameLabel.Text = UiLanguage.T("Prénom (obligatoire)", "First name (required)"); LastNameLabel.Text = UiLanguage.T("Nom (obligatoire)", "Last name (required)");
                PrivacyText.Text = UiLanguage.T("Ces informations sont enregistrées dans votre profil BIMaestro. Vous pourrez les modifier depuis Options.", "This information is saved in your BIMaestro profile. You can change it from Options.");
                NoThanksButton.Content = UiLanguage.T("Annuler", "Cancel"); LaterButton.Visibility = Visibility.Collapsed; PrimaryButton.Content = UiLanguage.T("Enregistrer et continuer", "Save and continue");
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
                MessageBox.Show(this, UiLanguage.T("Indiquez votre prénom et votre nom pour continuer avec la bibliothèque commune.", "Enter your first and last name to continue with the shared library."), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void OpenDemo_Click(object sender, RoutedEventArgs e)
        {
            ResultAction = WelcomeResultAction.OpenDemo;
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

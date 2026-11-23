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
                HeadingSubtitleText.Text = UiLanguage.Get("WelcomeWindow.xaml.GetFamiliarWithBIMaestroInAPracticeModel");
                Title = UiLanguage.Get("WelcomeWindow.xaml.BIMaestroPracticeModel");
                HeadingText.Text = UiLanguage.Get("WelcomeWindow.xaml.TryBIMaestro");
            }
            if (existing != null) { EmailBox.Text = existing.Email ?? ""; FirstNameBox.Text = existing.FirstName ?? ""; LastNameBox.Text = existing.LastName ?? ""; CompanyBox.Text = existing.Company ?? ""; }
            if (showEmail && !communityProfileRequired && !string.IsNullOrWhiteSpace(existing?.Email))
            {
                IntroTitleText.Text = UiLanguage.Get("WelcomeWindow.xaml.CompleteYourDetails");
                IntroText.Text = UiLanguage.Get("WelcomeWindow.xaml.YouCanAddYourCompanyOptionalYourSaved");
                PrimaryButton.Content = UiLanguage.Get("WelcomeWindow.xaml.Save");
                NoThanksButton.Content = UiLanguage.Get("WelcomeWindow.xaml.Keep");
                NoThanksButton.ToolTip = UiLanguage.Get("WelcomeWindow.xaml.KeepMyCurrentDetailsWithoutAddingACompany");
            }
            if (communityProfileRequired)
            {
                Title = UiLanguage.Get("WelcomeWindow.xaml.BIMaestroSharedLibraryProfile");
                HeadingText.Text = UiLanguage.Get("WelcomeWindow.xaml.SharedLibrary"); HeadingSubtitleText.Text = UiLanguage.Get("WelcomeWindow.xaml.IdentifyYourProfileBeforeSharingFamilies");
                IntroTitleText.Text = UiLanguage.Get("WelcomeWindow.xaml.CompleteYourProfile");
                IntroText.Text = UiLanguage.Get("WelcomeWindow.xaml.YourLastNameFirstNameAndEmailAre");
                TrustTitleText.Text = UiLanguage.Get("WelcomeWindow.xaml.WhyDoWeNeedThis");
                TrustDetailsText.Text = UiLanguage.Get("WelcomeWindow.xaml.ItLetsUsRecognizeYourProfileAProtected");
                EmailLabel.Text = UiLanguage.Get("WelcomeWindow.xaml.YourEmailRequired"); FirstNameLabel.Text = UiLanguage.Get("WelcomeWindow.xaml.FirstNameRequired"); LastNameLabel.Text = UiLanguage.Get("WelcomeWindow.xaml.LastNameRequired");
                PrivacyText.Text = UiLanguage.Get("WelcomeWindow.xaml.ThisInformationIsSavedInYourBIMaestroProfile");
                NoThanksButton.Content = UiLanguage.Get("WelcomeWindow.xaml.Cancel"); LaterButton.Visibility = Visibility.Collapsed; PrimaryButton.Content = UiLanguage.Get("WelcomeWindow.xaml.SaveAndContinue");
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
                MessageBox.Show(this, UiLanguage.Get("WelcomeWindow.xaml.UnableToOpenLinkedIn") + ex.Message,
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OptIn_Click(object sender, RoutedEventArgs e)
        {
            if (communityProfileRequired && (string.IsNullOrWhiteSpace(FirstName) || string.IsNullOrWhiteSpace(LastName)))
            {
                MessageBox.Show(this, UiLanguage.Get("WelcomeWindow.xaml.EnterYourFirstAndLastNameToContinue"), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(Email))
            {
                MessageBox.Show(this, UiLanguage.Get("WelcomeWindow.xaml.EnterAnEmailOrClickLaterIfYou"),
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsValidEmail(Email))
            {
                MessageBox.Show(this, UiLanguage.Get("WelcomeWindow.xaml.ThisEmailDoesNotAppearToBeValid"),
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

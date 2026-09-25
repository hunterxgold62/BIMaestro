using BIMaestro.Localization;
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Page
{
    internal enum SupportPromptKind
    {
        AfterUpdate,
        AfterUsage
    }

    public partial class SupportPromptWindow : Window
    {
        internal bool WantsToSupport { get; private set; }

        internal SupportPromptWindow(SupportPromptKind kind)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            LogoImage.Source = LoadBitmapFromResource("BIMaestro.png");
            ApplyContent(kind);
        }

        private void ApplyContent(SupportPromptKind kind)
        {
            if (kind == SupportPromptKind.AfterUpdate)
            {
                Height = 470;
                Title = UiLanguage.T("BIMaestro 1.0.6.4 est là", "BIMaestro 1.0.6.4 is here");
                TitleText.Text = Title;
                SubtitleText.Visibility = Visibility.Collapsed;
                IntroText.Text = UiLanguage.T(
                    "Merci d’utiliser BIMaestro. Je développe ce plugin seul, sur mon temps, et vos retours m’aident à le faire évoluer.",
                    "Thank you for using BIMaestro. I develop this plugin on my own, in my own time, and your feedback helps me improve it.");
                HighlightBorder.Visibility = Visibility.Collapsed;
                ClosingText.Text = UiLanguage.T(
                    "BIMaestro reste gratuit. Si vous l’utilisez régulièrement et souhaitez m’aider à poursuivre son développement et à couvrir ses frais, vous pouvez faire un don, même de 2 €. C’est entièrement libre.",
                    "BIMaestro remains free. If you use it regularly and would like to help me keep developing it and cover its costs, you can make a donation, even just €2. It is entirely up to you.");
                ClosingText.FontSize = 14;
                ClosingText.LineHeight = 22;
                LaterButton.Content = UiLanguage.T("Continuer", "Continue");
                SupportButton.Content = UiLanguage.T("♥  Soutenir BIMaestro", "♥  Support BIMaestro");
            }
            else
            {
                Title = UiLanguage.T("BIMaestro vous est utile ?", "Is BIMaestro useful to you?");
                TitleText.Text = UiLanguage.T("BIMaestro vous est utile ?", "Is BIMaestro useful to you?");
                SubtitleText.Text = UiLanguage.T(
                    "Vous connaissez maintenant ses outils et leur valeur au quotidien.",
                    "You now know its tools and their everyday value.");
                SetUsageIntroText();
                HighlightTitleText.Text = UiLanguage.T(
                    "Un petit soutien, un impact très concret",
                    "A little support, a very real impact");
                HighlightBodyText.Text = UiLanguage.T(
                    "Si BIMaestro vous fait gagner du temps, même 2 € participent réellement à sa pérennité et à ses prochaines innovations.",
                    "If BIMaestro saves you time, even €2 genuinely helps its future and next innovations.");
                ClosingText.Text = UiLanguage.T(
                    "Nous sommes déjà plus de 100 utilisateurs : les petits coûts de stockage et d’IA finissent donc par compter.",
                    "There are already more than 100 of us: small storage and AI costs therefore add up.");
                LaterButton.Content = UiLanguage.T("Pas maintenant", "Not now");
                SupportButton.Content = UiLanguage.T("♥  Soutenir dès 2 €", "♥  Support from €2");
            }

            VoluntaryText.Text = UiLanguage.T(
                "Soutien entièrement volontaire",
                "Entirely voluntary support");
        }

        private void SetUsageIntroText()
        {
            IntroText.Inlines.Clear();
            IntroText.Inlines.Add(new Run(UiLanguage.T(
                "BIMaestro est développé indépendamment depuis plus de ",
                "BIMaestro has been independently developed for more than ")));
            IntroText.Inlines.Add(new Run(UiLanguage.T("3 ans", "3 years"))
            {
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.DarkGreen
            });
            IntroText.Inlines.Add(new Run(UiLanguage.T(
                ", avec ",
                ", with ")));
            IntroText.Inlines.Add(new Run(UiLanguage.T("+ de 1 000 heures", "1,000+ hours"))
            {
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.DarkGreen
            });
            IntroText.Inlines.Add(new Run(UiLanguage.T(
                " consacrées à sa conception.",
                " devoted to its creation.")));
        }

        private void SupportButton_Click(object sender, RoutedEventArgs e)
        {
            WantsToSupport = true;
            Close();
        }

        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            WantsToSupport = false;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            WantsToSupport = false;
            Close();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private static BitmapImage LoadBitmapFromResource(string resourceFileName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                foreach (string resourceName in assembly.GetManifestResourceNames())
                {
                    if (!resourceName.EndsWith("." + resourceFileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null) return null;
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }
}

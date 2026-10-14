using BIMaestro.Localization;
using System;
using System.IO;
using System.Reflection;
using System.Windows;
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
            Height = 420;
            Title = UiLanguage.T("Pour me soutenir", "Support my work");
            TitleText.Text = Title;
            SubtitleText.Visibility = Visibility.Collapsed;
            HighlightBorder.Visibility = Visibility.Collapsed;
            IntroText.Text = UiLanguage.T(
                "Merci d’utiliser BIMaestro ! Si vous souhaitez me soutenir, vous pouvez le faire sur Ko-fi.",
                "Thank you for using BIMaestro! If you would like to support my work, you can do so on Ko-fi.");
            ClosingText.Text = UiLanguage.T(
                "Partager le plugin autour de vous et me mentionner sur LinkedIn m’aide déjà énormément. Merci pour votre soutien !",
                "Sharing the plugin with others and mentioning me on LinkedIn already helps a great deal. Thank you for your support!");
            ClosingText.Margin = new Thickness(0, 16, 0, 0);
            ClosingText.FontSize = 14;
            ClosingText.LineHeight = 22;
            LaterButton.Content = UiLanguage.T("Continuer", "Continue");
            SupportButton.Content = UiLanguage.T("♥  Ko-fi", "♥  Ko-fi");
            VoluntaryText.Text = UiLanguage.T("Merci !", "Thank you!");
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

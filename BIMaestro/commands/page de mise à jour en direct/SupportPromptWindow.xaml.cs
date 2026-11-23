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
            Title = UiLanguage.Get("SupportPromptWindow.xaml.SupportMyWork");
            TitleText.Text = Title;
            SubtitleText.Visibility = Visibility.Collapsed;
            HighlightBorder.Visibility = Visibility.Collapsed;
            IntroText.Text = UiLanguage.Get("SupportPromptWindow.xaml.ThankYouForUsingBIMaestroIfYouWould");
            ClosingText.Text = UiLanguage.Get("SupportPromptWindow.xaml.SharingThePluginWithOthersAndMentioningMe");
            ClosingText.Margin = new Thickness(0, 16, 0, 0);
            ClosingText.FontSize = 14;
            ClosingText.LineHeight = 22;
            LaterButton.Content = UiLanguage.Get("SupportPromptWindow.xaml.Continue");
            SupportButton.Content = UiLanguage.Get("SupportPromptWindow.xaml.KoFi");
            VoluntaryText.Text = UiLanguage.Get("SupportPromptWindow.xaml.ThankYou");
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

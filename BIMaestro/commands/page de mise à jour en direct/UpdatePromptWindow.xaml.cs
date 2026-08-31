using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BIMaestro.Localization;

namespace Page
{
    internal enum UpdatePromptResult
    {
        Close,
        Later,
        DownloadPageOpened
    }

    public partial class UpdatePromptWindow : Window
    {
        public bool MuteToday => MuteTodayCheck.IsChecked == true;
        internal UpdatePromptResult Result { get; private set; } = UpdatePromptResult.Close;
        internal UpdatePromptWindow(string latestVersion, string currentVersion)
        {
            InitializeComponent();

            TitleText.Text = UiLanguage.T("Nouvelle version BIMaestro : v", "New BIMaestro Version: v") + latestVersion;
            ContentText.Text = UiLanguage.T("Version installée : v", "Installed Version: v") + currentVersion +
                UiLanguage.T("\n\nTéléchargez la nouvelle version sur le site officiel, puis fermez toutes les fenêtres Revit et lancez l'installateur.",
                    "\n\nDownload the new version from the official website, then close all Revit windows and run the installer.");

            TryAttachEmbeddedGif(GifImage, "BIMaestro.Resources.OLD.Mickey.gif");
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateCheckService.OpenDownloadPage();
                Result = UpdatePromptResult.DownloadPageOpened;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    UiLanguage.T("Impossible d'ouvrir la page de téléchargement : ", "Unable to open the download page: ") + ex.Message +
                    "\n\n" + UpdateCheckService.DownloadPageUrl,
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            Result = UpdatePromptResult.Later;
            DialogResult = false;
            Close();
        }

        private static void TryAttachEmbeddedGif(Image image, string resourceName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return;
                    var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    AttachGifAnimation(image, decoder);
                }
            }
            catch
            {
                // no-op if GIF cannot be loaded
            }
        }

        private static void AttachGifAnimation(Image image, GifBitmapDecoder decoder)
        {
            try
            {
                if (decoder.Frames == null || decoder.Frames.Count == 0) return;

                var animation = new ObjectAnimationUsingKeyFrames();
                TimeSpan total = TimeSpan.Zero;

                foreach (var frame in decoder.Frames)
                {
                    var delay = GetFrameDelay(frame);
                    animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frame, KeyTime.FromTimeSpan(total)));
                    total += delay;
                }

                if (total <= TimeSpan.Zero) total = TimeSpan.FromSeconds(1);
                animation.Duration = total;
                animation.RepeatBehavior = RepeatBehavior.Forever;

                image.BeginAnimation(Image.SourceProperty, animation);
            }
            catch
            {
                // no-op if GIF animation fails
            }
        }

        private static TimeSpan GetFrameDelay(BitmapFrame frame)
        {
            try
            {
                BitmapMetadata meta = frame.Metadata as BitmapMetadata;
                object rawDelay = meta?.GetQuery("/grctlext/Delay");
                if (rawDelay is ushort d && d > 0)
                    return TimeSpan.FromMilliseconds(d * 10);
            }
            catch
            {
                // ignore metadata issues
            }

            return TimeSpan.FromMilliseconds(100);
        }
    }
}

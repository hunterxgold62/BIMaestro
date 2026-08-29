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
        UpdateNow
    }

    public partial class UpdatePromptWindow : Window
    {
        public bool MuteToday => MuteTodayCheck.IsChecked == true;
        internal UpdatePromptResult Result { get; private set; } = UpdatePromptResult.Close;
        private readonly UpdateManifest _manifest;

        internal UpdatePromptWindow(string latestVersion, string currentVersion, UpdateManifest manifest)
        {
            InitializeComponent();
            _manifest = manifest;

            TitleText.Text = UiLanguage.T("Nouvelle version BIMaestro : v", "New BIMaestro Version: v") + latestVersion;
            ContentText.Text = UiLanguage.T("Version installée : v", "Installed Version: v") + currentVersion +
                UiLanguage.T("\n\nVoulez-vous télécharger la mise à jour maintenant ? Elle sera installée automatiquement après la fermeture de Revit.",
                    "\n\nWould you like to download the update now? It will install automatically after Revit closes.");

            TryAttachEmbeddedGif(GifImage, "BIMaestro.Resources.OLD.Mickey.gif");
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_manifest == null) return;

            UpdateButton.IsEnabled = false;
            LaterButton.IsEnabled = false;
            MuteTodayCheck.IsEnabled = false;
            DownloadPanel.Visibility = Visibility.Visible;

            try
            {
                var progress = new Progress<int>(value =>
                {
                    DownloadProgress.Value = value;
                    DownloadStatus.Text = UiLanguage.T($"Téléchargement… {value}%", $"Downloading… {value}%");
                });
                await DirectUpdateService.DownloadAndScheduleAsync(_manifest, progress);
                Result = UpdatePromptResult.UpdateNow;
                MessageBox.Show(
                    UiLanguage.T(
                        "La mise à jour est téléchargée. Elle s'installera automatiquement après la fermeture de toutes les fenêtres Revit.",
                        "The update has been downloaded. It will install automatically after all Revit windows are closed."),
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                DownloadStatus.Text = UiLanguage.T("Échec du téléchargement.", "Download failed.");
                MessageBox.Show(
                    UiLanguage.T("Impossible de télécharger la mise à jour : ", "Unable to download the update: ") + ex.Message,
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UpdateButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                MuteTodayCheck.IsEnabled = true;
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

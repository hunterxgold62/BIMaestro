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

        public UpdatePromptWindow(string latestVersion, string currentVersion)
        {
            InitializeComponent();

            TitleText.Text = UiLanguage.T("Nouvelle version BIMaestro : v", "New BIMaestro Version: v") + latestVersion;
            ContentText.Text = UiLanguage.T("Version installée : v", "Installed Version: v") + currentVersion +
                UiLanguage.T("\n\nVoulez-vous ouvrir la page de mise à jour ?", "\n\nWould You Like to Open the Update Page?");

            TryAttachEmbeddedGif(GifImage, "BIMaestro.Resources.OLD.Mickey.gif");
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            Result = UpdatePromptResult.UpdateNow;
            DialogResult = true;
            Close();
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

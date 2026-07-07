using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Page
{
    public partial class SecretGifWindow : Window
    {
        private const string GifResourceName = "BIMaestro.Resources.OLD.Mickey.gif";
        private GifBitmapDecoder _decoder;

        public SecretGifWindow()
        {
            InitializeComponent();
            LoadGif();
        }

        public void RestartAnimation()
        {
            if (_decoder == null)
                LoadGif();
            else
                AttachGifAnimation(GifImage, _decoder);
        }

        private void LoadGif()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(GifResourceName))
                {
                    if (stream == null) return;

                    _decoder = new GifBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    AttachGifAnimation(GifImage, _decoder);
                }
            }
            catch
            {
                _decoder = null;
            }
        }

        private static void AttachGifAnimation(Image image, GifBitmapDecoder decoder)
        {
            try
            {
                if (decoder.Frames == null || decoder.Frames.Count == 0) return;

                image.BeginAnimation(Image.SourceProperty, null);

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
            }

            return TimeSpan.FromMilliseconds(100);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

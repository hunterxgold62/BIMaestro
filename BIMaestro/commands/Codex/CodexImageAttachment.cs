using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BIMaestro.Codex
{
    internal sealed class CodexImageAttachment
    {
        internal string Name, DataUrl;
        internal BitmapSource Thumbnail;
        internal static CodexImageAttachment FromFile(string path)
        {
            if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidOperationException("Image trop volumineuse : limite de 20 Mo par fichier.");
            using (var stream = File.OpenRead(path))
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1600; image.StreamSource = stream; image.EndInit(); image.Freeze();
                return FromBitmap(image, Path.GetFileName(path));
            }
        }
        internal static CodexImageAttachment FromPngBytes(byte[] png, string name)
        {
            using (var stream = new MemoryStream(png, false))
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); image.Freeze();
                return FromBitmap(image, name);
            }
        }
        internal static CodexImageAttachment FromBitmap(BitmapSource source, string name)
        {
            if (source == null || source.PixelWidth < 1 || source.PixelHeight < 1) throw new InvalidOperationException("Image vide.");
            double scale = Math.Min(1, 1600.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource normalized = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            // Re-encode pixels only: omit EXIF paths, metadata and source file handles.
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(normalized));
            using (var memory = new MemoryStream())
            {
                encoder.Save(memory);
                if (memory.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Image trop détaillée après réduction (8 Mo maximum).");
                var thumb = new TransformedBitmap(normalized, new ScaleTransform(72.0 / Math.Max(normalized.PixelWidth, normalized.PixelHeight), 72.0 / Math.Max(normalized.PixelWidth, normalized.PixelHeight)));
                thumb.Freeze();
                return new CodexImageAttachment { Name = name, DataUrl = "data:image/png;base64," + Convert.ToBase64String(memory.ToArray()), Thumbnail = thumb };
            }
        }
    }
}

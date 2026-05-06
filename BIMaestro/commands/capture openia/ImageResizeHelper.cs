using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace IA
{
    internal static class ImageResizeHelper
    {
        public static void ResizeToMax1024(string sourcePath, string targetPath)
        {
            using var src = Image.FromFile(sourcePath);
            var scale = Math.Min(1024.0 / src.Width, 1024.0 / src.Height);
            if (scale >= 1.0)
            {
                src.Save(targetPath, ImageFormat.Png);
                return;
            }

            int w = (int)Math.Round(src.Width * scale);
            int h = (int)Math.Round(src.Height * scale);
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            bmp.Save(targetPath, ImageFormat.Png);
        }
    }
}

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Famille
{
    public static class ThumbnailCache
    {
        public static string GetCachePath(string cacheFolder, string familyPath, int size)
        {
            Directory.CreateDirectory(cacheFolder);
            using var sha1 = SHA1.Create();
            var key = $"{familyPath}|{size}";
            var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key));
            var name = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            return Path.Combine(cacheFolder, name + ".png");
        }

        public static bool TryGet(string cacheFolder, string familyPath, int size, out string pngPath)
        {
            pngPath = GetCachePath(cacheFolder, familyPath, size);
            return File.Exists(pngPath);
        }

        public static string Save(string cacheFolder, string familyPath, int size, BitmapSource bmp)
        {
            var path = GetCachePath(cacheFolder, familyPath, size);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
            return path;
        }
    }
}

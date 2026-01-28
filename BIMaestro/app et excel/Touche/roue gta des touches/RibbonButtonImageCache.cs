using System;
using System.IO;
using System.Reflection;

namespace BIMaestro.UI
{
    internal static class RibbonButtonImageCache
    {
        private static readonly string CacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "CacheVignettes", "RibbonButtons");

        public static string GetOrCreate(string resourceFileName)
        {
            if (string.IsNullOrWhiteSpace(resourceFileName)) return null;

            try
            {
                Directory.CreateDirectory(CacheFolder);
                var targetPath = Path.Combine(CacheFolder, resourceFileName);
                if (File.Exists(targetPath)) return targetPath;

                var asm = Assembly.GetExecutingAssembly();
                string resourcePath = $"BIMaestro.Resources.{resourceFileName}";
                using (var stream = asm.GetManifestResourceStream(resourcePath))
                {
                    if (stream == null) return null;
                    using (var file = File.Create(targetPath))
                    {
                        stream.CopyTo(file);
                    }
                }

                return File.Exists(targetPath) ? targetPath : null;
            }
            catch
            {
                return null;
            }
        }
    }
}

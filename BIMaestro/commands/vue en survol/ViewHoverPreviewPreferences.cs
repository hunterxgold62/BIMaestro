using System;
using System.IO;

namespace BIMaestro.ViewHover
{
    internal static class ViewHoverPreviewPreferences
    {
        private static readonly string FilePath = Path.Combine(
            global::BIMaestro.Storage.UserDataStorage.Root, "SauvegardePréférence", "ViewHoverPreview.txt");

        internal static bool Enabled { get; private set; } = Load();
        internal static event Action Changed;

        private static readonly string SizesFilePath = Path.ChangeExtension(FilePath, ".sizes.txt");
        private static readonly int[] Sizes = LoadSizes();
        internal static int BrowserSizePercent => Sizes[0];
        internal static int ViewTabsSizePercent => Sizes[1];

        private static int[] LoadSizes()
        {
            var sizes = new[] { 100, 100 };
            try
            {
                string[] lines = File.ReadAllLines(SizesFilePath);
                for (int i = 0; i < sizes.Length && i < lines.Length; i++)
                    if (int.TryParse(lines[i], out int size) && IsValidSize(size))
                        sizes[i] = size;
            }
            catch { }
            return sizes;
        }

        private static bool IsValidSize(int size) =>
            size == 50 || size == 75 || size == 100 || size == 125 || size == 150 || size == 200;

        internal static bool SetSizes(int browserPercent, int viewTabsPercent)
        {
            if (!IsValidSize(browserPercent) || !IsValidSize(viewTabsPercent)) return false;
            if (BrowserSizePercent == browserPercent && ViewTabsSizePercent == viewTabsPercent) return true;
            if (!Save(SizesFilePath, browserPercent + Environment.NewLine + viewTabsPercent)) return false;
            Sizes[0] = browserPercent;
            Sizes[1] = viewTabsPercent;
            Changed?.Invoke();
            return true;
        }

        private static bool Load()
        {
            try
            {
                return !bool.TryParse(File.ReadAllText(FilePath), out bool enabled) || enabled;
            }
            catch { return true; }
        }

        internal static bool SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return true;
            if (!Save(FilePath, enabled.ToString())) return false;
            Enabled = enabled;
            Changed?.Invoke();
            return true;
        }

        private static bool Save(string path, string content)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporaryPath, content);
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
            }
            catch { return false; }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
            return true;
        }
    }
}

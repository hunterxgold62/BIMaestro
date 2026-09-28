using System;
using System.IO;

namespace BIMaestro.ViewHover
{
    internal static class ViewHoverPreviewPreferences
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "ViewHoverPreview.txt");

        internal static bool Enabled { get; private set; } = Load();
        internal static event Action Changed;

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
            string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(temporaryPath, enabled.ToString());
                if (File.Exists(FilePath)) File.Replace(temporaryPath, FilePath, null);
                else File.Move(temporaryPath, FilePath);
            }
            catch { return false; }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
            Enabled = enabled;
            Changed?.Invoke();
            return true;
        }
    }
}

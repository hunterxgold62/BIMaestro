using System;
using System.IO;

namespace Licensing
{
    public static class Paths
    {
        /// <summary>Mes Documents\RevitLogs</summary>
        public static string RevitLogsDir
            => EnsureDir(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs"));

        /// <summary>Mes Documents\RevitLogs\License</summary>
        public static string LicenseDir
            => EnsureDir(Path.Combine(RevitLogsDir, "License"));

        private static string EnsureDir(string dir)
        {
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { /* ignore */ }
            return dir;
        }
    }
}

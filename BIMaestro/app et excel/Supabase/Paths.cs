using System;
using System.IO;

namespace Licensing
{
    public static class Paths
    {
        /// <summary>Mes Documents\BIMaestro_Revit</summary>
        public static string BIMaestroDir
            => EnsureDir(global::BIMaestro.Storage.UserDataStorage.Root);

        // Compatibility for integrations compiled against the former property name.
        public static string RevitLogsDir => BIMaestroDir;

        /// <summary>Mes Documents\BIMaestro_Revit\License</summary>
        public static string LicenseDir
            => EnsureDir(Path.Combine(BIMaestroDir, "License"));

        private static string EnsureDir(string dir)
        {
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { /* ignore */ }
            return dir;
        }
    }
}

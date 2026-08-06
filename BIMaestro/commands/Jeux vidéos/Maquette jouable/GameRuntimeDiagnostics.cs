using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace BIMaestro.VideoGames
{
    internal static class GameRuntimeDiagnostics
    {
        private static readonly object SyncRoot = new object();
        private static int _revitMajorVersion = 2023;

        public static void ConfigureRevitVersion(int majorVersion)
        {
            if (majorVersion >= 2000 && majorVersion <= 9999)
                _revitMajorVersion = majorVersion;
        }

        public static void Write(string phase, Exception exception = null)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIMaestro",
                    "Logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(
                    directory,
                    "MaquetteBIM-Revit" + _revitMajorVersion + ".log");
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" | PID ")
                    .Append(Process.GetCurrentProcess().Id)
                    .Append(" | thread ")
                    .Append(Thread.CurrentThread.ManagedThreadId)
                    .Append(" | ")
                    .Append(phase ?? string.Empty);
                if (exception != null)
                {
                    line.AppendLine()
                        .Append(exception);
                }
                line.AppendLine();

                lock (SyncRoot)
                    File.AppendAllText(path, line.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Le diagnostic ne doit jamais devenir une cause de panne.
            }
        }
    }
}

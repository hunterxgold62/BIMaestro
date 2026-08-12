using System;
using System.Diagnostics;

namespace BIMaestro.VideoGames
{
    internal static class GameRuntimeDiagnostics
    {
        public static void ConfigureRevitVersion(int majorVersion)
        {
            // Conservé pour éviter de modifier les appelants. Les diagnostics
            // ne sont plus persistés dans AppData\Local\BIMaestro\Logs.
        }

        public static void Write(string phase, Exception exception = null)
        {
            string message = "[Maquette jouable] " + (phase ?? string.Empty);
            if (exception != null)
                message += Environment.NewLine + exception;
            Debug.WriteLine(message);
        }
    }
}

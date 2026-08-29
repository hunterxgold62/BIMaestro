using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BIMaestro.Updater
{
    internal static class Program
    {
        private const int MaximumWaitMinutes = 24 * 60;

        private static int Main(string[] args)
        {
            string ownPath = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                if (args.Length < 2 || !File.Exists(args[0]) || !int.TryParse(args[1], out int parentPid))
                    return 2;

                WaitForProcess(parentPid);
                if (!WaitForAllRevitProcesses()) return 4;

                var installer = Process.Start(new ProcessStartInfo
                {
                    FileName = args[0],
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(args[0])
                });
                installer?.WaitForExit();
                return installer?.ExitCode ?? 3;
            }
            catch
            {
                return 1;
            }
            finally
            {
                TryDeleteSelfLater(ownPath);
            }
        }

        private static void WaitForProcess(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                    process.WaitForExit();
            }
            catch (ArgumentException)
            {
                // Revit est deja ferme.
            }
        }

        private static bool WaitForAllRevitProcesses()
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(MaximumWaitMinutes);
            while (DateTime.UtcNow < deadline)
            {
                Process[] processes = Process.GetProcessesByName("Revit");
                if (processes.Length == 0) return true;

                foreach (var process in processes)
                {
                    using (process)
                    {
                        try { process.WaitForExit(1000); }
                        catch { }
                    }
                }
                Thread.Sleep(500);
            }
            return false;
        }

        private static void TryDeleteSelfLater(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/d /c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + path.Replace("\"", "") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }
    }
}

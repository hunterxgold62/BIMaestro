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
            string installerPath = args.Length > 0 ? args[0] : null;
            int exitCode = 1;
            string reason = "unexpected_error";
            try
            {
                if (args.Length < 2 || !File.Exists(args[0]) || !int.TryParse(args[1], out int parentPid))
                {
                    exitCode = 2;
                    reason = "invalid_arguments";
                    return exitCode;
                }

                WaitForProcess(parentPid);
                if (!WaitForAllRevitProcesses())
                {
                    exitCode = 4;
                    reason = "revit_wait_timeout";
                    TryDeleteScheduledMarker(installerPath);
                    return exitCode;
                }

                var installer = Process.Start(new ProcessStartInfo
                {
                    FileName = args[0],
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(args[0])
                });
                installer?.WaitForExit();
                exitCode = installer?.ExitCode ?? 3;
                if (exitCode == 0)
                {
                    reason = "success";
                    CleanupInstalledAndOlderUpdates(args[0]);
                }
                else
                {
                    reason = "installer_exit_code";
                    TryDeleteScheduledMarker(installerPath);
                }
                return exitCode;
            }
            catch
            {
                exitCode = 1;
                reason = "unexpected_error";
                TryDeleteScheduledMarker(installerPath);
                return exitCode;
            }
            finally
            {
                WriteInstallResult(installerPath, exitCode, reason);
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

        private static void CleanupInstalledAndOlderUpdates(string installerPath)
        {
            try
            {
                string fullInstallerPath = Path.GetFullPath(installerPath);
                string installedVersionDirectory = Path.GetDirectoryName(fullInstallerPath);
                string updatesDirectory = Directory.GetParent(installedVersionDirectory)?.FullName;
                string expectedUpdatesDirectory = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIMaestro",
                    "Updates"));

                if (string.IsNullOrWhiteSpace(updatesDirectory)
                    || !string.Equals(
                        Path.GetFullPath(updatesDirectory).TrimEnd(Path.DirectorySeparatorChar),
                        expectedUpdatesDirectory.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                    return;

                string installedVersionText = Path.GetFileName(installedVersionDirectory);
                if (!Version.TryParse(installedVersionText, out Version installedVersion))
                    return;

                foreach (string directory in Directory.GetDirectories(expectedUpdatesDirectory))
                {
                    string directoryName = Path.GetFileName(directory);
                    if (Version.TryParse(directoryName, out Version cachedVersion)
                        && cachedVersion <= installedVersion)
                    {
                        try { Directory.Delete(directory, true); }
                        catch { }
                    }
                }

                try
                {
                    if (Directory.Exists(expectedUpdatesDirectory)
                        && Directory.GetFileSystemEntries(expectedUpdatesDirectory).Length == 0)
                        Directory.Delete(expectedUpdatesDirectory, false);
                }
                catch { }
            }
            catch { }
        }

        private static void TryDeleteScheduledMarker(string installerPath)
        {
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(installerPath));
                string marker = Path.Combine(directory, "install.scheduled");
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch { }
        }

        private static void WriteInstallResult(string installerPath, int exitCode, string reason)
        {
            try
            {
                string version = "unknown";
                if (!string.IsNullOrWhiteSpace(installerPath))
                {
                    string directory = Path.GetDirectoryName(Path.GetFullPath(installerPath));
                    string candidate = Path.GetFileName(directory);
                    if (Version.TryParse(candidate, out Version parsedVersion))
                        version = parsedVersion.ToString();
                }

                string updatesDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIMaestro",
                    "Updates");
                Directory.CreateDirectory(updatesDirectory);

                string resultPath = Path.Combine(updatesDirectory, "last-result.json");
                string temporaryPath = resultPath + ".tmp";
                string json = "{" +
                    "\"version\":\"" + JsonEscape(version) + "\"," +
                    "\"success\":" + (exitCode == 0 ? "true" : "false") + "," +
                    "\"exitCode\":" + exitCode + "," +
                    "\"reason\":\"" + JsonEscape(reason) + "\"," +
                    "\"timestampUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"" +
                    "}";

                File.WriteAllText(temporaryPath, json);
                if (File.Exists(resultPath)) File.Delete(resultPath);
                File.Move(temporaryPath, resultPath);
            }
            catch { }
        }

        private static string JsonEscape(string value) =>
            (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

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

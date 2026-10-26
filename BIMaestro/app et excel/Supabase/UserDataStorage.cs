using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BIMaestro.Storage
{
    /// <summary>One process-wide decision, before any preferences or licence are read.</summary>
    public static class UserDataStorage
    {
        private const string Marker = ".bimaestro-migration-v1";
        private static readonly Lazy<string> ActiveRoot = new Lazy<string>(() => ResolveRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), true));
        public static string Root => ActiveRoot.Value;
        // Select Similar historically preferred OneDrive Documents even when Windows Documents
        // pointed elsewhere. Keep that exact location rather than silently losing its preferences.
        private static readonly Lazy<string> SelectionRoot = new Lazy<string>(() =>
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            foreach (string variable in new[] { "OneDriveCommercial", "OneDrive", "OneDriveConsumer" })
            {
                string drive = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrWhiteSpace(drive)) continue;
                string preferred = Path.Combine(drive, "Documents");
                if (string.Equals(Path.GetFullPath(preferred), Path.GetFullPath(documents), StringComparison.OrdinalIgnoreCase)) return Root;
                if (Directory.Exists(Path.Combine(preferred, "RevitLogs"))
                    || File.Exists(Path.Combine(preferred, "BIMaestro_Revit", Marker)))
                    return ResolveRoot(preferred, true);
                break;
            }
            return Root;
        });
        public static string SelectionPreferencesRoot => SelectionRoot.Value;
        public static string MigrationWarning { get; private set; }

        // Parameterized for isolated filesystem tests. Never searches outside this exact parent.
        internal static string ResolveRoot(string documents, bool checkRevitSessions = false)
        {
            if (string.IsNullOrWhiteSpace(documents)) throw new IOException("Documents is unavailable.");
            documents = Path.GetFullPath(documents);
            string oldRoot = Path.Combine(documents, "RevitLogs");
            string newRoot = Path.Combine(documents, "BIMaestro_Revit");
            string stage = null;
            string mutexId;
            using (var sha = SHA256.Create())
                mutexId = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(documents.ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, @"Local\BIMaestroDataMigration-" + mutexId))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Another BIMaestro migration is in progress.");
                    if (File.Exists(Path.Combine(newRoot, Marker))) return newRoot;
                    if (!Directory.Exists(oldRoot))
                    {
                        Directory.CreateDirectory(newRoot);
                        return newRoot;
                    }
                    RejectLink(oldRoot);
                    // A pre-existing destination belongs to the user: never merge or overwrite it.
                    if (Directory.Exists(newRoot) || File.Exists(newRoot))
                        throw new IOException("Documents\\BIMaestro_Revit already exists; no data was overwritten.");
                    // Old versions may still write to the legacy folder. Retry after closing other hosts.
                    if (checkRevitSessions && System.Diagnostics.Process.GetProcessesByName("Revit").Length > 1)
                        throw new IOException("Close the other Revit sessions before migrating.");
                    stage = Path.Combine(documents, ".BIMaestro-migration-" + Guid.NewGuid().ToString("N"));
                    CopyVerified(oldRoot, stage);
                    // Check again after the complete copy: changing sources must not be committed.
                    VerifyTree(oldRoot, stage);
                    RewritePreferencePaths(stage, oldRoot, newRoot);
                    File.WriteAllText(Path.Combine(stage, Marker), "Verified copy of " + oldRoot + "\r\n" + DateTime.UtcNow.ToString("O"));
                    Publish(stage, newRoot);
                    return newRoot;
                }
                catch (Exception ex)
                {
                    if (!held) throw;
                    MigrationWarning = "La migration des données vers Documents\\BIMaestro_Revit a été différée. " +
                        "BIMaestro conserve le dossier RevitLogs pour cette session. Aucun fichier existant n’a été supprimé ou remplacé.\n\n" +
                        ex.Message + (stage == null ? "" : "\nCopie de migration à contrôler : " + stage);
                    if (!Directory.Exists(oldRoot)) throw;
                    return oldRoot;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                // OneDrive placeholders are safe to hydrate/read; junctions and symlinks are not.
                FindData data;
                IntPtr handle = FindFirstFile(path, out data);
                if (handle == new IntPtr(-1)) throw new IOException("Cannot inspect entry: " + path);
                FindClose(handle);
                if ((data.Tag & 0xFFFF0FFFu) != 0x9000001Au)
                    throw new IOException("Linked entry requires review: " + path);
            }
        }

        internal static void Publish(string stage, string destination)
        {
            // Antivirus/OneDrive can briefly retain a handle after verification.
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Move(stage, destination); return; }
                catch (IOException) when (attempt < 7 && !Directory.Exists(destination) && !File.Exists(destination))
                { Thread.Sleep(250); }
                catch (UnauthorizedAccessException) when (attempt < 7 && !Directory.Exists(destination) && !File.Exists(destination))
                { Thread.Sleep(250); }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint SizeHigh, SizeLow, Tag, Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string path, out FindData data);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr handle);

        private static void RewritePreferencePaths(string stage, string oldRoot, string newRoot)
        {
            string prefs = Path.Combine(stage, "SauvegardePréférence");
            if (!Directory.Exists(prefs)) return;
            foreach (var file in Directory.GetFiles(prefs, "*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(file);
                if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                string text = File.ReadAllText(file);
                // Boundary prevents changing paths like RevitLogsOther. JSON stores doubled slashes.
                string escapedOld = oldRoot.Replace("\\", "\\\\");
                string escapedNew = newRoot.Replace("\\", "\\\\");
                string updated = Regex.Replace(text, Regex.Escape(escapedOld) + @"(?=\\\\|""|$)",
                    _ => escapedNew, RegexOptions.IgnoreCase);
                updated = Regex.Replace(updated, Regex.Escape(oldRoot) + @"(?=[\\/""\r\n]|$)",
                    _ => newRoot, RegexOptions.IgnoreCase);
                updated = Regex.Replace(updated, Regex.Escape(oldRoot.Replace('\\', '/')) + @"(?=[/""\r\n]|$)",
                    _ => newRoot.Replace('\\', '/'), RegexOptions.IgnoreCase);
                if (updated != text) File.WriteAllText(file, updated, new UTF8Encoding(false));
            }
        }

        internal static void CopyVerified(string source, string target)
        {
            RejectLink(source);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                RejectLink(file);
                string dest = Path.Combine(target, Path.GetFileName(file));
                // Deny writers/deletion while copying. Never write to the source.
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
            }
            foreach (string dir in Directory.GetDirectories(source))
                CopyVerified(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        internal static void VerifyTree(string source, string target)
        {
            RejectLink(source);
            var files = Directory.GetFiles(source);
            var dirs = Directory.GetDirectories(source);
            if (files.Length != Directory.GetFiles(target).Length || dirs.Length != Directory.GetDirectories(target).Length)
                throw new IOException("Source changed during migration: " + source);
            foreach (string file in files)
            {
                RejectLink(file);
                using (var sha = SHA256.Create())
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var copy = File.OpenRead(Path.Combine(target, Path.GetFileName(file))))
                    if (!sha.ComputeHash(input).SequenceEqual(sha.ComputeHash(copy)))
                        throw new IOException("Copy verification failed: " + file);
            }
            foreach (string dir in dirs) VerifyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}

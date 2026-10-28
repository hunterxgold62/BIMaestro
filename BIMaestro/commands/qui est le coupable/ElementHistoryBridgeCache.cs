using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Analyse
{
    // This policy only owns GUID-named opening copies in HistoryBridge. It never
    // evicts historical payloads, source projects, or useful copies to make room.
    internal static class ElementHistoryBridgeCache
    {
        internal const int RetentionDays = 15;
        internal const int WarningDays = 3;
        internal sealed class Snapshot
        {
            internal long Bytes, LimitBytes, FreeBytes;
            internal int Copies, Expiring;
            internal DateTime? EarliestExpiryUtc;
            internal string Issue;
        }
        private static readonly object Sync = new object();
        private static Task Worker;
        private static DateTime NextSweepUtc;
        private static Snapshot Current = new Snapshot();
        private static string LastIssue;
        internal static Snapshot Status { get { lock (Sync) return Current; } }
        internal static long Limit(long total) => Math.Min(10L * 1024 * 1024 * 1024, total / 20);
        internal static long Reserve(long total) => Math.Min(total / 5, Math.Max(2L * 1024 * 1024 * 1024, total / 20));
        internal static bool Fits(long total, long free, long used, long reservedGrowth, long length)
            => length > 0 && length <= Limit(total) - used && length <= free - reservedGrowth - Reserve(total);
        internal static bool NeedsWarning(Snapshot snapshot) => snapshot.Expiring > 0
            || snapshot.Issue?.StartsWith("Nouvelle copie non créée : plafond", StringComparison.Ordinal) == true;

        internal static void Tick(string root)
        {
            lock (Sync)
            {
                if (ElementHistoryClock.UtcNow < NextSweepUtc || Worker != null && !Worker.IsCompleted) return;
                NextSweepUtc = ElementHistoryClock.UtcNow.AddSeconds(15);
                Worker = Task.Run(() => {
                    try { var status = Sweep(root); lock (Sync) Current = status; }
                    catch (IOException ex) when (IsSharingConflict(ex)) { /* Another Revit instance is reserving space. */ }
                    catch (Exception ex) { lock (Sync) Current = new Snapshot { Issue = ex.Message }; }
                });
            }
        }
        internal static bool IsCopy(string root, string path)
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileNameWithoutExtension(full);
            return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(full), ".rvt", StringComparison.OrdinalIgnoreCase)
                && name.StartsWith("snapshot-opening-", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(name.Substring("snapshot-opening-".Length), "N", out _);
        }
        private static void RequireRoot(string root)
        {
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Le cache historique ne peut pas être un lien de dossier.");
        }
        internal static FileStream AcquireLease(string root, string path)
        {
            RequireRoot(root);
            if (!IsCopy(root, path)) throw new IOException("Copie hors du cache historique.");
            return new FileStream(path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        private static bool IsSharingConflict(IOException ex) => (ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33;
        private static FileStream ReserveLock(string root)
        {
            // Only called by the copier worker; never wait on Revit's UI thread.
            for (int attempt = 0; ; attempt++)
            {
                try { return new FileStream(Path.Combine(root, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) when (attempt < 80 && IsSharingConflict(ex)) { System.Threading.Thread.Sleep(25); }
            }
        }
        private static List<string> Copies(string root) => Directory.GetFiles(root, "snapshot-opening-*")
            .Select(p => p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 8)
                : p.EndsWith(".reservation", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 12) : p)
            .Where(p => IsCopy(root, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        internal static DateTime CreatedUtc(string path)
        {
            if (File.Exists(path + ".age") && long.TryParse(File.ReadAllText(path + ".age"), out var ticks)
                && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
                return new DateTime(ticks, DateTimeKind.Utc);
            return File.GetCreationTimeUtc(File.Exists(path) ? path : path + ".partial");
        }
        private static long Bytes(string path)
        {
            long size = (File.Exists(path) ? new FileInfo(path).Length : 0)
                + (File.Exists(path + ".partial") ? new FileInfo(path + ".partial").Length : 0);
            if (File.Exists(path + ".reservation") && long.TryParse(File.ReadAllText(path + ".reservation"), out var reserved))
                size = Math.Max(size, reserved);
            return size;
        }
        internal static Snapshot Sweep(string root)
        {
            RequireRoot(root);
            using (new FileStream(Path.Combine(root, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var now = ElementHistoryClock.UtcNow;
                foreach (var path in Copies(root))
                {
                    if (now - CreatedUtc(path) < TimeSpan.FromDays(RetentionDays)) continue;
                    try { Delete(root, path); }
                    catch (IOException) { /* A copier or a Revit reader still owns the lease. Retry next pass. */ }
                }
                var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)));
                var paths = Copies(root);
                var expiries = paths.Select(p => CreatedUtc(p).AddDays(RetentionDays)).ToList();
                return new Snapshot { Bytes = paths.Sum(Bytes), LimitBytes = Limit(disk.TotalSize), FreeBytes = disk.AvailableFreeSpace,
                    Copies = paths.Count, Expiring = expiries.Count(d => d <= now.AddDays(WarningDays)),
                    EarliestExpiryUtc = expiries.Count == 0 ? (DateTime?)null : expiries.Min(), Issue = LastIssue };
            }
        }
        internal static void Delete(string root, string path)
        {
            using (AcquireLease(root, path))
            {
                foreach (var file in new[] { path, path + ".partial", path + ".reservation", path + ".age" })
                {
                    if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Lien de fichier refusé dans le cache historique.");
                }
                foreach (var file in new[] { path, path + ".partial", path + ".reservation", path + ".age" }) File.Delete(file);
            }
        }
        internal static IDisposable BeginCopy(string root, string path, long length)
        {
            RequireRoot(root);
            using (ReserveLock(root))
            {
                var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)));
                var copies = Copies(root);
                var reservedGrowth = copies.Sum(p => Math.Max(0, Bytes(p)
                    - (File.Exists(p) ? new FileInfo(p).Length : 0)
                    - (File.Exists(p + ".partial") ? new FileInfo(p + ".partial").Length : 0)));
                if (!Fits(disk.TotalSize, disk.AvailableFreeSpace, copies.Sum(Bytes), reservedGrowth, length))
                {
                    LastIssue = "Nouvelle copie non créée : plafond du cache ou réserve d’espace disque atteints. Certaines restaurations pourront être indisponibles.";
                    throw new IOException(LastIssue);
                }
                var lease = AcquireLease(root, path);
                try
                {
                    File.WriteAllText(path + ".age", ElementHistoryClock.UtcNow.Ticks.ToString());
                    File.WriteAllText(path + ".reservation", length.ToString());
                    LastIssue = null;
                    return new CopyLease(lease, path);
                }
                catch { lease.Dispose(); throw; }
            }
        }
        private sealed class CopyLease : IDisposable
        {
            private readonly FileStream Lease;
            private readonly string Path;
            internal CopyLease(FileStream lease, string path) { Lease = lease; Path = path; }
            public void Dispose() { try { File.Delete(Path + ".reservation"); } finally { Lease.Dispose(); } }
        }
    }
}

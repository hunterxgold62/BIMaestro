using System;
using System.IO;
using System.Threading.Tasks;
using Analyse;

internal static class Program
{
    private static int Count;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Count++; Console.WriteLine("PASS " + label); }
    private static string Copy(string root) => Path.Combine(root, "snapshot-opening-" + Guid.NewGuid().ToString("N") + ".rvt");
    private static int Main()
    {
        var root = Path.GetFullPath(Path.Combine("tmp", "history-cache-tests", "BIMaestro-CacheTests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST", "1");
        var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            ElementHistoryClock.SetTestUtcNow(now);
            var path = Copy(root);
            using (ElementHistoryBridgeCache.BeginCopy(root, path, 1024))
            {
                Check(ElementHistoryBridgeCache.Sweep(root).Bytes == 1024, "in-flight reservation counted before bytes exist");
                File.WriteAllBytes(path + ".partial", new byte[12]);
                Check(ElementHistoryBridgeCache.Sweep(root).Bytes == 1024, "partial bytes do not double-count reservation");
                File.Move(path + ".partial", path);
            }
            Check(ElementHistoryBridgeCache.Sweep(root).Bytes == 12, "completed reservation released");
            Check(!ElementHistoryBridgeCache.NeedsWarning(ElementHistoryBridgeCache.Sweep(root)), "fresh copy at J-15 does not interrupt the user");
            ElementHistoryClock.SetTestUtcNow(now.AddDays(12).AddSeconds(-1));
            Check(ElementHistoryBridgeCache.Sweep(root).Expiring == 0, "warning not shown before J-3");
            ElementHistoryClock.SetTestUtcNow(now.AddDays(12));
            Check(ElementHistoryBridgeCache.Sweep(root).Expiring == 1, "warning at J-3");
            Check(ElementHistoryBridgeCache.NeedsWarning(ElementHistoryBridgeCache.Sweep(root)), "expiration warning begins at J-3");
            ElementHistoryClock.SetTestUtcNow(now.AddDays(15).AddTicks(-1));
            ElementHistoryBridgeCache.Sweep(root);
            Check(File.Exists(path), "copy kept until exact 15-day deadline");
            ElementHistoryClock.SetTestUtcNow(now.AddDays(15));
            using (ElementHistoryBridgeCache.AcquireLease(root, path))
            {
                ElementHistoryBridgeCache.Sweep(root);
                Check(File.Exists(path), "active extraction protected at expiry");
            }
            ElementHistoryBridgeCache.Sweep(root);
            Check(!File.Exists(path) && !File.Exists(path + ".age"), "expired copy removed after reader releases");

            ElementHistoryClock.SetTestUtcNow(now);
            var empty = Copy(root);
            using (ElementHistoryBridgeCache.BeginCopy(root, empty, 32)) File.WriteAllText(empty, "copy");
            ElementHistoryBridgeCache.Delete(root, empty);
            Check(!File.Exists(empty), "normal cleanup allowed before expiry");

            var partial = Copy(root);
            File.WriteAllText(partial + ".partial", "interrupted");
            File.WriteAllText(partial + ".age", now.Ticks.ToString());
            var original = Path.Combine(root, "original.rvt");
            File.WriteAllText(original, "user source");
            ElementHistoryClock.SetTestUtcNow(now.AddDays(16));
            ElementHistoryBridgeCache.Sweep(root);
            Check(!File.Exists(partial + ".partial"), "orphaned partial cleaned after expiry");
            Check(File.Exists(original), "non-owned RVT never deleted");

            var useful = Copy(root);
            using (ElementHistoryBridgeCache.BeginCopy(root, useful, 16)) File.WriteAllText(useful, "useful");
            var disk = new DriveInfo(Path.GetPathRoot(root));
            bool refused = false;
            try { using (ElementHistoryBridgeCache.BeginCopy(root, Copy(root), ElementHistoryBridgeCache.Limit(disk.TotalSize) + 1)) { } }
            catch (IOException) { refused = true; }
            Check(refused && File.Exists(useful), "quota rejects new copy without evicting useful data");
            Check(ElementHistoryBridgeCache.NeedsWarning(ElementHistoryBridgeCache.Sweep(root)), "quota refusal remains actionable before expiry");
            Check(ElementHistoryBridgeCache.Limit(1000) == 50, "quota adapts to disk capacity");
            Check(ElementHistoryBridgeCache.Limit(1000L * 1024 * 1024 * 1024) == 10L * 1024 * 1024 * 1024, "quota capped at 10 GiB");
            Check(ElementHistoryBridgeCache.Reserve(1000) == 200, "free-space reserve adapts to small disks");
            Check(!ElementHistoryBridgeCache.IsCopy(root, Path.Combine(root, "..", Path.GetFileName(useful))), "parent traversal refused");
            bool outsideRefused = false;
            try { ElementHistoryBridgeCache.Delete(root, original); } catch (IOException) { outsideRefused = true; }
            Check(outsideRefused && File.Exists(original), "normal cleanup also rejects original project");
            Check(!ElementHistoryBridgeCache.Fits(1000, 201, 0, 0, 2), "low free space rejects copy even below quota");
            Check(!ElementHistoryBridgeCache.Fits(1000, 250, 0, 49, 2), "other in-flight reservations protect free space");
            Check(ElementHistoryBridgeCache.Fits(1000, 250, 0, 0, 50), "exact quota and reserve boundary accepted");
            Check(!ElementHistoryBridgeCache.Fits(1000, 1000, 49, 0, 2), "existing cache usage counts against quota");
            Parallel.For(0, 8, i => {
                var concurrent = Copy(root);
                using (ElementHistoryBridgeCache.BeginCopy(root, concurrent, 1000)) File.WriteAllText(concurrent, "parallel");
            });
            Check(ElementHistoryBridgeCache.Sweep(root).Copies == 9, "concurrent copiers reserve safely across workers");
            Console.WriteLine(Count + " assertions passed (production policy, isolated temporary cache; no Revit invoked).");
            return 0;
        }
        finally
        {
            ElementHistoryClock.SetTestUtcNow(null);
            // Only this explicitly created, GUID-named test directory is removed.
            Directory.Delete(root, true);
        }
    }
}

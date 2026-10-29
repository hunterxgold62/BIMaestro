using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Analyse
{
    // Periodic disk hygiene on the IO worker. Durable restore data (journal,
    // conversions, payloads) is never touched here: only files whose purpose is over.
    internal static partial class ElementHistoryDurableBridge
    {
        private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan LogRetention = TimeSpan.FromDays(30);
        // A wave may write its snapshot before its journal events: never sweep fresh files.
        private static readonly TimeSpan NativeSnapshotGrace = TimeSpan.FromDays(1);
        private const int DiagnosticLimit = 1000;
        private static DateTime NextHousekeepingUtc;

        private static void Housekeeping()
        {
            Run("native-history", CleanupNativeHistory);
            var store = CollaborativeModelTrackerStore.ActiveDirectory;
            var reports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "HistoryReports");
            Run("reports", () => DeleteOlderThan(reports, "*.json", LogRetention, int.MaxValue));
            Run("diagnostics", () => DeleteOlderThan(Path.Combine(store, "history-data", "diagnostics"), "failure-*.json", LogRetention, DiagnosticLimit));
            Run("performance", () => DeleteOlderThan(Path.Combine(store, "history-performance"), "performance-*.jsonl", LogRetention, int.MaxValue));
        }
        private static void Run(string name, Action step)
        {
            try { step(); }
            catch (Exception ex) { Trace.WriteLine("History housekeeping " + name + ": " + ex.Message); }
        }

        // Keeps at most `limit` newest files and none older than `retention`.
        private static void DeleteOlderThan(string directory, string pattern, TimeSpan retention, int limit)
        {
            if (!Directory.Exists(directory)) return;
            var cutoff = DateTime.UtcNow - retention;
            var files = new DirectoryInfo(directory).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var file in files.Where((f, index) => index >= limit || f.LastWriteTimeUtc < cutoff))
            {
                try { file.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // native-history holds small native snapshots and, from older versions,
        // whole opening copies. Once no unfinished deletion and no stored
        // conversion still names a file (its data now lives in history-data), it is
        // removed; a damaged record keeps the files it may name.
        private static void CleanupNativeHistory()
        {
            var directory = Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history");
            if (!Directory.Exists(directory)) return;
            if (ElementHistoryLocalSpool.Default.HasPending(CollaborativeModelTrackerStore.ActiveDirectory)) return;
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Keep(HistoryRecipe recipe)
            {
                foreach (var artifact in Artifacts(recipe).Where(a => string.IsNullOrEmpty(a.PayloadId) && !string.IsNullOrWhiteSpace(a.File)))
                    referenced.Add(Path.GetFileName(artifact.File));
            }
            foreach (var ev in ReadJournal().Where(e => e.Action == "delete"))
            {
                HistoryRestoreRequest original, resolved;
                // Unreadable event: its files are unknown, so delete nothing this pass.
                try { original = ToRequest(ev, false); }
                catch (Exception ex) { Trace.WriteLine("History native-history kept: " + ex.Message); return; }
                try { resolved = ResolvePreparedRequest(EventId(ev)); }
                catch (InvalidDataException) { Keep(original.Recipe); continue; }
                if (resolved != null) Keep(resolved.Recipe);
                if (!IsCompletedBridgeRequest(resolved)) Keep(original.Recipe);
            }
            var cutoff = DateTime.UtcNow - NativeSnapshotGrace;
            foreach (var file in new DirectoryInfo(directory).GetFiles("snapshot-*.rvt"))
            {
                // SaveAs backups (name.0001.rvt) belong to their snapshot.
                var name = System.Text.RegularExpressions.Regex.Replace(file.Name, @"\.\d{4}\.rvt$", ".rvt");
                if (referenced.Contains(name) || file.LastWriteTimeUtc > cutoff) continue;
                try { file.Delete(); }
                catch (IOException) { /* Open in Revit: retry at the next pass. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

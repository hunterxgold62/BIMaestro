using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;

namespace Analyse
{
    // Plain session measurements; sorting never re-reads geometry or walks the model.
    internal static class HistoryCaptureCosts
    {
        private static readonly Dictionary<string, double> Costs = new Dictionary<string, double>();
        internal static string Key(Element element)
            => element.Document.GetHashCode() + "|" + element.GetType().Name + "|" + element.GetTypeId().GetIdLongValue();
        internal static double Initial(string elementClass)
        {
            switch (elementClass)
            {
                case "Toposolid": case "TopographySurface": return 5000;
                case "ImportInstance": return 3000;
                case "DirectShape": return 1500;
                case "Stairs": return 1000;
                case "Railing": return 500;
                case "Group": return 200;
                default: return 10;
            }
        }
        internal static double Estimate(string key, string phase, string elementClass)
            => key != null && Costs.TryGetValue(phase + "|" + key, out var ms) ? ms
                : phase == "element_capture_basic" ? 5 : Initial(elementClass);
        internal static bool KnownSlowBasic(Element element)
            => Estimate(Key(element), "element_capture_basic", element.GetType().Name) > 100;
        internal static void Observe(string key, string phase, double ms)
        {
            if (key == null) return;
            var index = phase + "|" + key;
            if (Costs.Count >= 10000 && !Costs.ContainsKey(index)) return;
            Costs[index] = Costs.TryGetValue(index, out var previous) ? previous * 0.75 + ms * 0.25 : ms;
        }
    }

    // Capture plain values on the Revit thread; serialize and write on the existing logger worker.
    internal static class HistoryPerformanceLog
    {
        private static readonly string Session = Guid.NewGuid().ToString("N");
        private static readonly ConcurrentQueue<Tuple<string, object>> Pending = new ConcurrentQueue<Tuple<string, object>>();
        private static readonly object WriteSync = new object();

        internal static void Record(Document doc, string phase, double elapsedMs, int elements,
            int remaining, bool complete, Element element = null, bool? success = null)
        {
            if (doc == null || Pending.Count >= 10000) return;
            var path = Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "history-performance",
                "performance-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + "-" + Session + ".jsonl");
            Pending.Enqueue(Tuple.Create(path, (object)new
            {
                SchemaVersion = 2, Session, AtUtc = DateTime.UtcNow,
                ModelKey = ElementHistoryTracker.GetDocumentKeyForHistory(doc),
                RuntimeDocumentId = doc.GetHashCode(), ModelName = doc.Title,
                ViewId = doc.ActiveView?.Id.GetIdValue(), Phase = phase,
                ElapsedMilliseconds = elapsedMs, Elements = elements, Remaining = remaining, Complete = complete,
                ElementId = element?.Id.GetIdLongValue(), ElementUniqueId = element?.UniqueId,
                ElementClass = element?.GetType().Name, Category = element?.Category?.Name,
                ElementName = element?.Name,
                ElementFamily = element is FamilyInstance family ? family.Symbol?.FamilyName
                    : element is FamilySymbol symbol ? symbol.FamilyName : null,
                ElementTypeId = element?.GetTypeId().GetIdLongValue(),
                ElementType = element == null ? null : element.Document.GetElement(element.GetTypeId())?.Name,
                Success = success
            }));
        }

        internal static void RecordElement(Element element, string phase, double elapsedMs, bool success)
        {
            if (element == null) return;
            try
            {
                HistoryCaptureCosts.Observe(HistoryCaptureCosts.Key(element), phase, elapsedMs);
                Record(element.Document, phase, elapsedMs, 1, 0, false, element, success);
            }
            catch (Exception ex) { Trace.WriteLine("History element performance: " + ex.Message); }
        }

        // Callers detach all Revit objects before passing details. Serialization
        // and disk writes stay on the existing logger worker.
        internal static void RecordDetails(Document doc, string phase, double elapsedMs, int elements,
            int remaining, bool success, object details)
        {
            if (doc == null || !doc.IsValidObject || Pending.Count >= 10000) return;
            try
            {
                var path = Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "history-performance",
                    "performance-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + "-" + Session + ".jsonl");
                Pending.Enqueue(Tuple.Create(path, (object)new
                {
                    SchemaVersion = 3, Session, AtUtc = DateTime.UtcNow,
                    ModelKey = ElementHistoryTracker.GetDocumentKeyForHistory(doc),
                    RuntimeDocumentId = doc.GetHashCode(), ModelName = doc.Title,
                    Phase = phase, ElapsedMilliseconds = elapsedMs, Elements = elements,
                    Remaining = remaining, Success = success, Details = details
                }));
            }
            catch (Exception ex) { Trace.WriteLine("History detail performance: " + ex.Message); }
        }

        internal static void Flush()
        {
            lock (WriteSync)
            {
                var batch = new List<Tuple<string, object>>();
                while (batch.Count < 500 && Pending.TryDequeue(out var record)) batch.Add(record);
                foreach (var group in batch.GroupBy(record => record.Item1))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(group.Key));
                        var text = new StringBuilder();
                        foreach (var item in group) text.AppendLine(JsonConvert.SerializeObject(item.Item2));
                        File.AppendAllText(group.Key, text.ToString(), Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("History performance write: " + ex.Message);
                        foreach (var item in group) Pending.Enqueue(item);
                    }
                }
            }
        }
    }

    internal sealed class HistoryPreloadCategoryAudit
    {
        public string Category { get; set; }
        public int Visited { get; set; }
        public int Captured { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
    }

    // Plain diagnostic data. No Revit objects are retained or serialized.
    internal sealed class HistoryPreloadAudit
    {
        public DateTime ScheduledUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public int TotalCandidates { get; set; }
        public int Visited { get; set; }
        public int Captured { get; set; }
        public int BasicRecipesCaptured { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int Batches { get; set; }
        public double ActiveMilliseconds { get; set; }
        public double PriorityCatalogMilliseconds { get; set; }
        public int PriorityCandidates { get; set; }
        public int PriorityVisited { get; set; }
        public string PriorityCatalogError { get; set; }
        public List<HistoryPreloadCategoryAudit> Categories { get; set; } = new List<HistoryPreloadCategoryAudit>();
        public double? EstimatedRemainingSecondsAtObservedWallRate { get; set; }
        public string Error { get; set; }
        public bool Complete { get; set; }

        public HistoryPreloadAudit Copy(DateTime atUtc)
        {
            double? remaining = null;
            // The priority categories are much more expensive than the later IDs
            // that are mostly skipped. Do not extrapolate the first small wave to
            // the entire model (it can yield multi-hour estimates for minutes of work).
            var minimumSample = Math.Min(TotalCandidates, Math.Max(250, TotalCandidates / 10));
            if (!Complete && TotalCandidates > 0 && Visited >= minimumSample
                && PriorityVisited >= PriorityCandidates && StartedUtc.HasValue)
                remaining = Math.Max(0, (atUtc - StartedUtc.Value).TotalSeconds *
                    (TotalCandidates - Visited) / Visited);
            return new HistoryPreloadAudit
            {
                ScheduledUtc = ScheduledUtc, StartedUtc = StartedUtc, CompletedUtc = CompletedUtc,
                TotalCandidates = TotalCandidates, Visited = Visited, Captured = Captured,
                BasicRecipesCaptured = BasicRecipesCaptured,
                Skipped = Skipped, Failed = Failed, Batches = Batches,
                ActiveMilliseconds = ActiveMilliseconds,
                PriorityCatalogMilliseconds = PriorityCatalogMilliseconds,
                PriorityCandidates = PriorityCandidates, PriorityVisited = PriorityVisited,
                PriorityCatalogError = PriorityCatalogError,
                Categories = Categories.ConvertAll(c => new HistoryPreloadCategoryAudit
                {
                    Category = c.Category, Visited = c.Visited, Captured = c.Captured,
                    Skipped = c.Skipped, Failed = c.Failed
                }),
                EstimatedRemainingSecondsAtObservedWallRate = remaining,
                Error = Error, Complete = Complete
            };
        }
    }

    internal sealed class HistorySelectionAudit
    {
        public DateTime AtUtc { get; set; }
        public int SelectedCount { get; set; }
        public int CacheHits { get; set; }
        public int SimpleCaptured { get; set; }
        public int DetailedCaptured { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public double TotalPluginSelectionMilliseconds { get; set; }
        public double SelectionExtractionMilliseconds { get; set; }
        public double ViewDeckMilliseconds { get; set; }
        public double HoverInfoMilliseconds { get; set; }
        public double ProjectBrowserMilliseconds { get; set; }
        public string Error { get; set; }
    }

    internal sealed class HistoryNativeBackgroundAudit
    {
        public DateTime? LastCheckUtc { get; set; }
        public string LastBlockReason { get; set; }
        public DateTime? NextEligibleUtc { get; set; }
        public DateTime? LastWaveStartedUtc { get; set; }
        public DateTime? LastWaveCompletedUtc { get; set; }
        public double LastWaveMilliseconds { get; set; }
        public DateTime? PrioritySelectionUtc { get; set; }
    }

    internal sealed class HistoryDeletionElementAudit
    {
        public string BatchId { get; set; }
        public int BatchDeletedElementCount { get; set; }
        public int BatchWithSnapshotCount { get; set; }
        public string BatchReportPath { get; set; }
        public int ElementId { get; set; }
        public string OriginalUniqueId { get; set; }
        public string Category { get; set; }
        public string Name { get; set; }
        public bool SnapshotFound { get; set; }
        public bool? PreloadStillQueuedAtDeletion { get; set; }
        public DateTime? SnapshotCapturedUtc { get; set; }
        public string SnapshotSource { get; set; }
        public string RecipeKind { get; set; }
        public string NativeFallbackReason { get; set; }
        public string CaptureFailure { get; set; }
        public bool? WasInLastSelection { get; set; }
        public string SuperComponentUniqueId { get; set; }
        public string FamilyTypeUniqueId { get; set; }
        public string HostUniqueId { get; set; }
        public string SketchOwnerSourceUniqueId { get; set; }
        public List<string> SketchDependentUniqueIds { get; set; }
        public string NativeRootSourceUniqueId { get; set; }
        public bool? NativeArchiveReadyAtDeletion { get; set; }
        public string NativeArchiveFailureAtDeletion { get; set; }
        public string NativeArchiveFile { get; set; }
        public DateTime? NativeArchiveRequestedUtc { get; set; }
        public DateTime? NativeArchiveStartedUtc { get; set; }
        public DateTime? NativeArchiveReadyUtc { get; set; }
        public List<string> RelatedSourceUniqueIds { get; set; }
    }

    internal sealed class HistoryDeletionBatchAudit
    {
        public string BatchId { get; set; }
        public string ReportPath { get; set; }
        public DateTime DeletedUtc { get; set; }
        public string ModelKey { get; set; }
        public string TransactionName { get; set; }
        public int DeletedElementCount { get; set; }
        public int WithSnapshot { get; set; }
        public int WithoutSnapshot { get; set; }
        public int WithRecipe { get; set; }
        public int NativeReady { get; set; }
        public int NativeNotReady { get; set; }
        public int NativePendingRootsAtDeletion { get; set; }
        public int RestorationCapturesPendingAtDeletion { get; set; }
        public int SelectionCapturesPendingAtDeletion { get; set; }
        public int NativeReadyRecipesAtDeletion { get; set; }
        public int NativePriorityPendingRootsAtDeletion { get; set; }
        public double NativePriorityClassificationMilliseconds { get; set; }
        public HistoryNativeBackgroundAudit NativeBackgroundAtDeletion { get; set; }
        public HistoryPreloadAudit PreloadAtDeletion { get; set; }
        public HistorySelectionAudit LastSelection { get; set; }
        public List<HistoryDeletionElementAudit> Elements { get; set; } = new List<HistoryDeletionElementAudit>();
    }

    internal sealed class HistoryRelationAudit
    {
        public string Kind { get; set; }
        public int End { get; set; }
        public int? SecondEnd { get; set; }
        public int? JoinOrder { get; set; }
        public int? ActualJoinOrder { get; set; }
        public int? ActualParticipantCount { get; set; }
        public bool? JoinOrderMatches { get; set; }
        public string FirstSourceUniqueId { get; set; }
        public string SecondSourceUniqueId { get; set; }
        public string FirstResolvedUniqueId { get; set; }
        public string SecondResolvedUniqueId { get; set; }
        public string Outcome { get; set; }
        public string Detail { get; set; }
    }
}

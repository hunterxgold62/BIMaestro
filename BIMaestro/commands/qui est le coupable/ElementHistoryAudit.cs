using System;
using System.Collections.Generic;

namespace Analyse
{
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

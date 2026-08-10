using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BIMaestro.ViewHover
{
    internal enum ViewPreviewBatchMode
    {
        MissingAndStale,
        MissingOnly,
        All
    }

    internal sealed class ViewPreviewBatchProgress
    {
        public string DocumentTitle { get; set; }
        public string CurrentViewName { get; set; }
        public string Status { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public int Failed { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? EstimatedRemaining { get; set; }
        public bool IsRunning { get; set; }
        public bool IsPaused { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsCanceled { get; set; }
    }

    internal static partial class ViewHoverPreviewService
    {
        private sealed class BatchItem
        {
            public ElementId ViewId { get; set; }
            public string ViewUniqueId { get; set; }
            public string ViewName { get; set; }
        }

        private sealed class BatchJob
        {
            public Document Document { get; set; }
            public string DocumentKey { get; set; }
            public string DocumentTitle { get; set; }
            public Queue<BatchItem> Items { get; set; }
            public int Total { get; set; }
            public int Completed { get; set; }
            public int Failed { get; set; }
            public string CurrentViewName { get; set; }
            public string Status { get; set; }
            public DateTime StartedUtc { get; set; }
            public DateTime? FinishedUtc { get; set; }
            public DateTime NextItemNotBeforeUtc { get; set; }
            public TimeSpan ProcessingDuration { get; set; }
            public bool IsPaused { get; set; }
            public bool StopRequested { get; set; }
            public bool IsCompleted { get; set; }
            public bool IsCanceled { get; set; }
            public bool WaitingForDocumentReported { get; set; }
        }

        private static BatchJob _batchJob;

        internal static event Action<ViewPreviewBatchProgress>
            BatchProgressChanged;

        internal static bool StartBatch(
            UIApplication uiApplication,
            ViewPreviewBatchMode mode,
            out string error)
        {
            error = string.Empty;
            Document document = uiApplication?.ActiveUIDocument?.Document;
            if (document == null)
            {
                error = "Aucun document Revit actif.";
                return false;
            }

            lock (Sync)
            {
                if (_batchJob != null &&
                    !_batchJob.IsCompleted &&
                    !_batchJob.IsCanceled)
                {
                    error = "Une génération de miniatures est déjà en cours.";
                    return false;
                }
            }

            string documentKey = Analyse.ElementHistoryTracker
                .GetDocumentKeyForHistory(document);
            string documentVersion = GetDocumentVersionSignature(document);
            var candidates = new List<BatchItem>();
            List<View> views = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => CanCapture(document, view))
                .OrderBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            lock (Sync)
            {
                DocumentPreviewState state = GetOrCreateState(documentKey);
                foreach (View view in views)
                {
                    state.Views[view.UniqueId] = CreateIdentity(view);
                    string targetPath = GetPreviewPath(
                        documentKey,
                        view.UniqueId);
                    bool exists = File.Exists(targetPath);
                    long capturedRevision = GetRevision(
                        state.CapturedRevisions,
                        view.UniqueId);
                    long dirtyRevision = Math.Max(
                        state.GlobalDirtyRevision,
                        GetRevision(
                            state.ViewDirtyRevisions,
                            view.UniqueId));
                    bool stale = exists &&
                        (dirtyRevision > capturedRevision ||
                         !HasMatchingDocumentVersion(
                             targetPath,
                             documentVersion));
                    bool include = mode == ViewPreviewBatchMode.All ||
                        (mode == ViewPreviewBatchMode.MissingOnly && !exists) ||
                        (mode == ViewPreviewBatchMode.MissingAndStale &&
                         (!exists || stale));
                    if (!include) continue;

                    candidates.Add(new BatchItem
                    {
                        ViewId = view.Id,
                        ViewUniqueId = view.UniqueId,
                        ViewName = view.Name ?? string.Empty
                    });
                }

                _pending = null;
                _batchJob = new BatchJob
                {
                    Document = document,
                    DocumentKey = documentKey,
                    DocumentTitle = document.Title ?? string.Empty,
                    Items = new Queue<BatchItem>(candidates),
                    Total = candidates.Count,
                    StartedUtc = DateTime.UtcNow,
                    NextItemNotBeforeUtc = DateTime.UtcNow,
                    Status = candidates.Count == 0
                        ? "Aucune miniature à générer."
                        : "Préparation de la file…",
                    IsCompleted = candidates.Count == 0,
                    FinishedUtc = candidates.Count == 0
                        ? DateTime.UtcNow
                        : (DateTime?)null
                };
            }

            PublishBatchProgress();
            return true;
        }

        internal static void PauseBatch()
        {
            lock (Sync)
            {
                if (_batchJob == null || _batchJob.IsCompleted ||
                    _batchJob.IsCanceled)
                {
                    return;
                }
                _batchJob.IsPaused = true;
                _batchJob.Status = "Traitement en pause.";
            }
            PublishBatchProgress();
        }

        internal static void ResumeBatch()
        {
            lock (Sync)
            {
                if (_batchJob == null || !_batchJob.IsPaused ||
                    _batchJob.IsCompleted || _batchJob.IsCanceled)
                {
                    return;
                }
                _batchJob.IsPaused = false;
                _batchJob.NextItemNotBeforeUtc = DateTime.UtcNow;
                _batchJob.Status = "Reprise du traitement…";
            }
            PublishBatchProgress();
        }

        internal static void StopBatch()
        {
            lock (Sync)
            {
                if (_batchJob == null || _batchJob.IsCompleted ||
                    _batchJob.IsCanceled)
                {
                    return;
                }
                _batchJob.StopRequested = true;
                _batchJob.Status = "Arrêt demandé…";
            }
            PublishBatchProgress();
        }

        internal static ViewPreviewBatchProgress GetBatchProgress()
        {
            lock (Sync)
            {
                return CreateBatchProgress(_batchJob);
            }
        }

        private static bool ProcessBatch(UIApplication uiApplication)
        {
            BatchJob job;
            lock (Sync)
            {
                job = _batchJob;
                if (job == null || job.IsCompleted || job.IsCanceled)
                    return false;
                if (job.StopRequested)
                {
                    job.IsCanceled = true;
                    job.IsPaused = false;
                    job.Status = "Traitement arrêté.";
                    job.FinishedUtc = DateTime.UtcNow;
                }
                if (job.IsCanceled)
                {
                    // Publish outside the lock below.
                }
                else if (job.IsPaused)
                {
                    return false;
                }
                else if (DateTime.UtcNow < job.NextItemNotBeforeUtc)
                {
                    return true;
                }
            }

            if (job.IsCanceled)
            {
                EnforceCacheSizeLimit();
                PublishBatchProgress();
                return false;
            }

            Document activeDocument =
                uiApplication?.ActiveUIDocument?.Document;
            string activeDocumentKey = activeDocument == null
                ? string.Empty
                : Analyse.ElementHistoryTracker
                    .GetDocumentKeyForHistory(activeDocument);
            if (!string.Equals(
                    activeDocumentKey,
                    job.DocumentKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                bool publishWaiting = false;
                lock (Sync)
                {
                    if (!job.WaitingForDocumentReported)
                    {
                        job.WaitingForDocumentReported = true;
                        job.Status = "En attente du retour au projet « " +
                                     job.DocumentTitle + " »…";
                        publishWaiting = true;
                    }
                }
                if (publishWaiting) PublishBatchProgress();
                return true;
            }

            lock (Sync)
            {
                job.WaitingForDocumentReported = false;
                DocumentPreviewState state = GetOrCreateState(job.DocumentKey);
                if (DateTime.UtcNow <
                    state.LastDocumentChangeUtc.AddSeconds(1))
                {
                    return true;
                }
            }

            BatchItem item;
            lock (Sync)
            {
                if (job.Items.Count == 0)
                {
                    job.IsCompleted = true;
                    job.FinishedUtc = DateTime.UtcNow;
                    job.Status = job.Failed > 0
                        ? "Terminé avec " + job.Failed + " échec(s)."
                        : "Toutes les miniatures sont à jour.";
                    item = null;
                }
                else
                {
                    item = job.Items.Dequeue();
                    job.CurrentViewName = item.ViewName;
                    job.Status = "Génération de « " + item.ViewName + " »…";
                }
            }

            if (item == null)
            {
                EnforceCacheSizeLimit();
                PublishBatchProgress();
                return false;
            }

            PublishBatchProgress();
            var stopwatch = Stopwatch.StartNew();
            bool refreshed = false;
            View view = null;
            try { view = job.Document.GetElement(item.ViewId) as View; }
            catch { }
            string targetPath = GetPreviewPath(
                job.DocumentKey,
                item.ViewUniqueId);
            bool hadPreviousPreview = File.Exists(targetPath);

            if (view != null && HasActiveTemporaryViewMode(view))
            {
                lock (Sync)
                {
                    job.Items.Enqueue(item);
                    job.Status = "En attente de la fin du mode temporaire de « " +
                                 (view.Name ?? item.ViewName) + " »…";
                    job.NextItemNotBeforeUtc =
                        DateTime.UtcNow.AddSeconds(1);
                }
                PublishBatchProgress();
                return true;
            }

            if (view != null && CanCapture(job.Document, view))
            {
                try
                {
                    refreshed = ExportViewPreview(
                        job.Document,
                        view,
                        targetPath);
                }
                catch
                {
                    refreshed = false;
                }
            }

            if (refreshed)
            {
                WriteDocumentVersion(
                    targetPath,
                    GetDocumentVersionSignature(job.Document));
                lock (Sync)
                {
                    DocumentPreviewState state = GetOrCreateState(
                        job.DocumentKey);
                    state.CapturedRevisions[item.ViewUniqueId] =
                        state.Revision;
                    state.Views[item.ViewUniqueId] = CreateIdentity(view);
                    string key = BuildPreviewKey(
                        job.DocumentKey,
                        item.ViewUniqueId);
                    Published.Add(key);
                    FailedThisSession.Remove(key);
                }
            }

            if (File.Exists(targetPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(targetPath);
                    string dataUri = "data:image/png;base64," +
                                     Convert.ToBase64String(bytes);
                    Couleur.ProjectBrowserColoring.SetViewHoverPreview(
                        GetElementIdText(item.ViewId),
                        view?.Name ?? item.ViewName,
                        dataUri,
                        File.GetLastWriteTime(targetPath),
                        !refreshed);
                }
                catch { }
            }

            stopwatch.Stop();
            bool completedNow;
            lock (Sync)
            {
                job.Completed++;
                if (!refreshed) job.Failed++;
                job.ProcessingDuration += stopwatch.Elapsed;
                job.CurrentViewName = view?.Name ?? item.ViewName;
                completedNow = job.Items.Count == 0;
                job.IsCompleted = completedNow;
                if (completedNow) job.FinishedUtc = DateTime.UtcNow;
                job.Status = completedNow
                    ? (job.Failed > 0
                        ? "Terminé avec " + job.Failed + " échec(s)."
                        : "Toutes les miniatures sont à jour.")
                    : (refreshed
                        ? "Miniature créée."
                        : (hadPreviousPreview
                            ? "Capture impossible, ancienne image conservée."
                            : "Capture impossible pour cette vue."));
                job.NextItemNotBeforeUtc =
                    DateTime.UtcNow.AddMilliseconds(150);
            }

            if (completedNow || job.Completed % 10 == 0)
                EnforceCacheSizeLimit();
            PublishBatchProgress();
            return !completedNow;
        }

        private static void CancelBatchForDocument(string documentKey)
        {
            bool changed = false;
            lock (Sync)
            {
                if (_batchJob != null &&
                    !_batchJob.IsCompleted &&
                    !_batchJob.IsCanceled &&
                    string.Equals(
                        _batchJob.DocumentKey,
                        documentKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _batchJob.IsCanceled = true;
                    _batchJob.Status = "Traitement arrêté : projet fermé.";
                    _batchJob.FinishedUtc = DateTime.UtcNow;
                    changed = true;
                }
            }
            if (changed) PublishBatchProgress();
        }

        private static void PublishBatchProgress()
        {
            ViewPreviewBatchProgress progress;
            lock (Sync)
            {
                progress = CreateBatchProgress(_batchJob);
            }
            try { BatchProgressChanged?.Invoke(progress); }
            catch { }
        }

        private static ViewPreviewBatchProgress CreateBatchProgress(
            BatchJob job)
        {
            if (job == null) return null;
            TimeSpan? remaining = null;
            if (job.Completed > 0 && job.Total > job.Completed)
            {
                double averageTicks =
                    job.ProcessingDuration.Ticks / (double)job.Completed;
                remaining = TimeSpan.FromTicks((long)(
                    averageTicks * (job.Total - job.Completed)));
            }

            return new ViewPreviewBatchProgress
            {
                DocumentTitle = job.DocumentTitle,
                CurrentViewName = job.CurrentViewName,
                Status = job.Status,
                Completed = job.Completed,
                Total = job.Total,
                Failed = job.Failed,
                Elapsed = (job.FinishedUtc ?? DateTime.UtcNow) -
                          job.StartedUtc,
                EstimatedRemaining = remaining,
                IsRunning = !job.IsPaused && !job.IsCompleted &&
                            !job.IsCanceled,
                IsPaused = job.IsPaused,
                IsCompleted = job.IsCompleted,
                IsCanceled = job.IsCanceled
            };
        }
    }
}

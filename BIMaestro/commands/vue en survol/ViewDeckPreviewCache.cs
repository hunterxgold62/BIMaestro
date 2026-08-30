using Autodesk.Revit.DB;
using System;
using System.IO;

namespace BIMaestro.ViewHover
{
    internal static partial class ViewHoverPreviewService
    {
        internal static string GetDeckPreviewPath(Document document, View view)
        {
            return GetPreviewPath(
                Analyse.ElementHistoryTracker.GetDocumentKeyForHistory(document), view.UniqueId);
        }

        internal static bool IsDeckPreviewUnavailable(Document document, View view)
        {
            if (!CanCapture(document, view)) return true;
            string key = BuildPreviewKey(
                Analyse.ElementHistoryTracker.GetDocumentKeyForHistory(document), view.UniqueId);
            lock (Sync) { return FailedThisSession.Contains(key); }
        }

        // Called only from Revit Idling, at most once per deck refresh. Existing
        // previews keep their normal revision/activation-based refresh policy.
        internal static bool TryCreateMissingDeckPreview(Document document, View view)
        {
            if (!CanCapture(document, view) || document.IsModifiable || document.IsReadOnly ||
                HasActiveTemporaryViewMode(view)) return false;
            string documentKey = Analyse.ElementHistoryTracker.GetDocumentKeyForHistory(document);
            string key = BuildPreviewKey(documentKey, view.UniqueId);
            string path = GetPreviewPath(documentKey, view.UniqueId);
            if (File.Exists(path)) return false;

            long revision;
            lock (Sync)
            {
                if (_isProcessing || _pending != null ||
                    (_batchJob != null && !_batchJob.IsCompleted) ||
                    FailedThisSession.Contains(key)) return false;
                DocumentPreviewState state = GetOrCreateState(documentKey);
                if (DateTime.UtcNow < state.LastDocumentChangeUtc.AddSeconds(2)) return false;
                revision = state.Revision;
                _isProcessing = true;
            }
            try
            {
                if (!ExportViewPreview(document, view, path))
                {
                    lock (Sync) { FailedThisSession.Add(key); }
                    return true;
                }
                WriteDocumentVersion(path, GetDocumentVersionSignature(document));
                lock (Sync)
                {
                    DocumentPreviewState state = GetOrCreateState(documentKey);
                    state.Views[view.UniqueId] = CreateIdentity(view);
                    state.CapturedRevisions[view.UniqueId] = revision;
                }
                EnforceCacheSizeLimit();
            }
            catch
            {
                lock (Sync) { FailedThisSession.Add(key); }
            }
            finally
            {
                lock (Sync) { _isProcessing = false; }
            }
            return true;
        }
    }
}

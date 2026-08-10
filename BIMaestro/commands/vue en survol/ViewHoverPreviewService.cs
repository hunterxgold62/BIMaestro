using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BIMaestro.ViewHover
{
    /// <summary>
    /// Construit une miniature uniquement après qu'une vue a réellement été
    /// activée. Un simple survol dans l'arborescence ne déclenche jamais
    /// d'export Revit.
    /// </summary>
    internal static partial class ViewHoverPreviewService
    {
        private sealed class PendingPreview
        {
            public string DocumentKey { get; set; }
            public string ViewUniqueId { get; set; }
            public ElementId ViewId { get; set; }
            public string ViewName { get; set; }
            public DateTime NotBeforeUtc { get; set; }
            public bool MustRefresh { get; set; }
        }

        private sealed class DocumentPreviewState
        {
            public long Revision { get; set; }
            public long GlobalDirtyRevision { get; set; }
            public Dictionary<string, long> ViewDirtyRevisions { get; } =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, long> CapturedRevisions { get; } =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, ViewPreviewIdentity> Views { get; } =
                new Dictionary<string, ViewPreviewIdentity>(
                    StringComparer.OrdinalIgnoreCase);
            public DateTime CacheCleanupNotBeforeUtc { get; set; } =
                DateTime.MaxValue;
            public DateTime LastDocumentChangeUtc { get; set; } =
                DateTime.MinValue;
        }

        private sealed class ViewPreviewIdentity
        {
            public string ViewId { get; set; }
            public string ViewName { get; set; }
        }

        private static readonly object Sync = new object();
        private static readonly HashSet<string> Published =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> FailedThisSession =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DocumentPreviewState>
            DocumentStates = new Dictionary<string, DocumentPreviewState>(
                StringComparer.OrdinalIgnoreCase);
        private static PendingPreview _pending;
        private static bool _isProcessing;

        private static readonly string CacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "CacheVignettes",
            "VueEnSurvol");
        private const long MaximumCacheBytes = 100L * 1024L * 1024L;

        internal static void TrackActivatedView(Document document, View view)
        {
            if (!CanCapture(document, view))
            {
                lock (Sync) { _pending = null; }
                return;
            }

            string documentKey = Analyse.ElementHistoryTracker
                .GetDocumentKeyForHistory(document);
            string previewKey = BuildPreviewKey(documentKey, view.UniqueId);
            string targetPath = GetPreviewPath(documentKey, view.UniqueId);
            string documentVersion = GetDocumentVersionSignature(document);
            bool markExistingPreviewStale = false;
            lock (Sync)
            {
                DocumentPreviewState state = GetOrCreateState(documentKey);
                state.Views[view.UniqueId] = new ViewPreviewIdentity
                {
                    ViewId = GetElementIdText(view.Id),
                    ViewName = view.Name ?? string.Empty
                };
                long capturedRevision = GetRevision(
                    state.CapturedRevisions,
                    view.UniqueId);
                long dirtyRevision = Math.Max(
                    state.GlobalDirtyRevision,
                    GetRevision(state.ViewDirtyRevisions, view.UniqueId));
                bool mustRefresh = !File.Exists(targetPath) ||
                                   dirtyRevision > capturedRevision ||
                                   !HasMatchingDocumentVersion(
                                       targetPath,
                                       documentVersion);
                markExistingPreviewStale =
                    mustRefresh && File.Exists(targetPath);
                DateTime stableAfterUtc =
                    state.LastDocumentChangeUtc.AddSeconds(1);

                if ((!mustRefresh && Published.Contains(previewKey)) ||
                    FailedThisSession.Contains(previewKey))
                {
                    _pending = null;
                    return;
                }

                _pending = new PendingPreview
                {
                    DocumentKey = documentKey,
                    ViewUniqueId = view.UniqueId,
                    ViewId = view.Id,
                    ViewName = view.Name ?? string.Empty,
                    // Une vue traversée rapidement ne doit jamais être exportée.
                    NotBeforeUtc = MaxDateTime(
                        DateTime.UtcNow.AddSeconds(2),
                        stableAfterUtc),
                    MustRefresh = mustRefresh
                };
            }

            if (markExistingPreviewStale)
            {
                Couleur.ProjectBrowserColoring.SetViewHoverPreviewStale(
                    GetElementIdText(view.Id),
                    view.Name ?? string.Empty,
                    true);
            }
        }

        internal static void TrackDocumentChanges(
            Document document,
            DocumentChangedEventArgs args)
        {
            if (document == null || args == null) return;

            string documentKey = Analyse.ElementHistoryTracker
                .GetDocumentKeyForHistory(document);
            var affectedViews = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var affectedViewIdentities =
                new Dictionary<string, ViewPreviewIdentity>(
                    StringComparer.OrdinalIgnoreCase);
            bool hasDeletedElements = args.GetDeletedElementIds().Any();
            bool affectsAllViews = hasDeletedElements;

            IEnumerable<ElementId> changedIds = args.GetAddedElementIds()
                .Concat(args.GetModifiedElementIds());
            foreach (ElementId elementId in changedIds)
            {
                Element element = null;
                try { element = document.GetElement(elementId); }
                catch { }

                if (element == null)
                {
                    affectsAllViews = true;
                    continue;
                }

                if (element is View changedView && !changedView.IsTemplate)
                {
                    affectedViews.Add(changedView.UniqueId);
                    affectedViewIdentities[changedView.UniqueId] =
                        CreateIdentity(changedView);
                    continue;
                }

                ElementId ownerViewId = ElementId.InvalidElementId;
                try { ownerViewId = element.OwnerViewId; }
                catch { }
                if (ownerViewId != null &&
                    ownerViewId != ElementId.InvalidElementId)
                {
                    try
                    {
                        if (document.GetElement(ownerViewId) is View ownerView &&
                            !ownerView.IsTemplate)
                        {
                            affectedViews.Add(ownerView.UniqueId);
                            affectedViewIdentities[ownerView.UniqueId] =
                                CreateIdentity(ownerView);
                            continue;
                        }
                    }
                    catch { }
                }

                // Model elements, types, materials and view templates can
                // influence several views, so invalidate conservatively.
                affectsAllViews = true;
            }

            if (!affectsAllViews && affectedViews.Count == 0) return;

            var stalePreviews = new Dictionary<string, ViewPreviewIdentity>(
                StringComparer.OrdinalIgnoreCase);
            lock (Sync)
            {
                DocumentPreviewState state = GetOrCreateState(documentKey);
                DateTime changedAtUtc = DateTime.UtcNow;
                state.LastDocumentChangeUtc = changedAtUtc;
                long revision = ++state.Revision;
                string documentPrefix = documentKey + "|view-preview|";
                foreach (KeyValuePair<string, ViewPreviewIdentity> item
                         in affectedViewIdentities)
                {
                    state.Views[item.Key] = item.Value;
                }
                if (_pending != null && string.Equals(
                        _pending.DocumentKey,
                        documentKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _pending.NotBeforeUtc = MaxDateTime(
                        _pending.NotBeforeUtc,
                        changedAtUtc.AddSeconds(1));
                    if (affectsAllViews ||
                        affectedViews.Contains(_pending.ViewUniqueId))
                    {
                        _pending.MustRefresh = true;
                    }
                    if (affectedViewIdentities.TryGetValue(
                            _pending.ViewUniqueId,
                            out ViewPreviewIdentity pendingIdentity))
                    {
                        _pending.ViewName = pendingIdentity.ViewName;
                    }
                }
                if (hasDeletedElements)
                {
                    state.CacheCleanupNotBeforeUtc =
                        changedAtUtc.AddSeconds(3);
                }

                if (affectsAllViews)
                {
                    state.GlobalDirtyRevision = revision;
                    Published.RemoveWhere(key => key.StartsWith(
                        documentPrefix,
                        StringComparison.OrdinalIgnoreCase));
                    FailedThisSession.RemoveWhere(key => key.StartsWith(
                        documentPrefix,
                        StringComparison.OrdinalIgnoreCase));
                    foreach (KeyValuePair<string, ViewPreviewIdentity> item
                             in state.Views)
                    {
                        stalePreviews[item.Key] = item.Value;
                    }
                }

                foreach (string viewUniqueId in affectedViews)
                {
                    state.ViewDirtyRevisions[viewUniqueId] = revision;
                    string key = BuildPreviewKey(documentKey, viewUniqueId);
                    Published.Remove(key);
                    FailedThisSession.Remove(key);
                    if (state.Views.TryGetValue(
                            viewUniqueId,
                            out ViewPreviewIdentity identity))
                    {
                        stalePreviews[viewUniqueId] = identity;
                    }
                }
            }

            foreach (ViewPreviewIdentity preview in stalePreviews.Values)
            {
                Couleur.ProjectBrowserColoring.SetViewHoverPreviewStale(
                    preview.ViewId,
                    preview.ViewName,
                    true);
            }
        }

        internal static void ScheduleCacheMaintenance(Document document)
        {
            if (document == null) return;
            string documentKey = Analyse.ElementHistoryTracker
                .GetDocumentKeyForHistory(document);
            lock (Sync)
            {
                GetOrCreateState(documentKey).CacheCleanupNotBeforeUtc =
                    DateTime.UtcNow.AddSeconds(2);
            }
        }

        internal static void ProcessPending(UIApplication uiApplication)
        {
            ProcessScheduledCacheMaintenance(uiApplication);
            if (ProcessBatch(uiApplication)) return;

            PendingPreview pending;
            lock (Sync)
            {
                if (_isProcessing || _pending == null ||
                    DateTime.UtcNow < _pending.NotBeforeUtc)
                {
                    return;
                }

                pending = _pending;
                _pending = null;
                _isProcessing = true;
            }

            try
            {
                UIDocument uiDocument = uiApplication?.ActiveUIDocument;
                Document document = uiDocument?.Document;
                View activeView = uiDocument?.ActiveView;
                if (document == null || activeView == null ||
                    activeView.Id != pending.ViewId ||
                    !string.Equals(
                        Analyse.ElementHistoryTracker.GetDocumentKeyForHistory(document),
                        pending.DocumentKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (HasActiveTemporaryViewMode(activeView))
                {
                    pending.NotBeforeUtc = DateTime.UtcNow.AddSeconds(1);
                    lock (Sync)
                    {
                        _pending = pending;
                    }
                    return;
                }

                string previewKey = BuildPreviewKey(
                    pending.DocumentKey,
                    pending.ViewUniqueId);
                string targetPath = GetPreviewPath(
                    pending.DocumentKey,
                    pending.ViewUniqueId);

                bool mustRefresh = pending.MustRefresh;
                long capturedAtRevision;
                lock (Sync)
                {
                    DocumentPreviewState state = GetOrCreateState(
                        pending.DocumentKey);
                    long previousCapture = GetRevision(
                        state.CapturedRevisions,
                        pending.ViewUniqueId);
                    long dirtyRevision = Math.Max(
                        state.GlobalDirtyRevision,
                        GetRevision(
                            state.ViewDirtyRevisions,
                            pending.ViewUniqueId));
                    mustRefresh |= dirtyRevision > previousCapture;
                    capturedAtRevision = state.Revision;
                }

                bool refreshed = false;
                if (mustRefresh || !File.Exists(targetPath))
                {
                    try
                    {
                        refreshed = ExportViewPreview(
                            document,
                            activeView,
                            targetPath);
                    }
                    catch
                    {
                        refreshed = false;
                    }
                }

                if (!File.Exists(targetPath))
                {
                    lock (Sync) { FailedThisSession.Add(previewKey); }
                    return;
                }

                byte[] bytes = File.ReadAllBytes(targetPath);
                string dataUri = "data:image/png;base64," +
                                 Convert.ToBase64String(bytes);
                Couleur.ProjectBrowserColoring.SetViewHoverPreview(
                    GetElementIdText(activeView.Id),
                    pending.ViewName,
                    dataUri,
                    File.GetLastWriteTime(targetPath),
                    mustRefresh && !refreshed);

                if (refreshed)
                {
                    WriteDocumentVersion(
                        targetPath,
                        GetDocumentVersionSignature(document));
                    EnforceCacheSizeLimit();
                }

                lock (Sync)
                {
                    DocumentPreviewState state = GetOrCreateState(
                        pending.DocumentKey);
                    if (!mustRefresh || refreshed)
                    {
                        state.CapturedRevisions[pending.ViewUniqueId] =
                            capturedAtRevision;
                        Published.Add(previewKey);
                    }
                    else
                    {
                        // Keep showing the previous image, but do not retry the
                        // failed export continuously during the same session.
                        FailedThisSession.Add(previewKey);
                    }
                }
            }
            catch
            {
                string key = BuildPreviewKey(
                    pending?.DocumentKey,
                    pending?.ViewUniqueId);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    lock (Sync) { FailedThisSession.Add(key); }
                }
            }
            finally
            {
                lock (Sync) { _isProcessing = false; }
            }
        }

        internal static void ForgetDocument(Document document)
        {
            if (document == null) return;
            string documentKey = Analyse.ElementHistoryTracker
                .GetDocumentKeyForHistory(document);
            lock (Sync)
            {
                if (_pending != null && string.Equals(
                        _pending.DocumentKey,
                        documentKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _pending = null;
                }

                DocumentStates.Remove(documentKey);
                string documentPrefix = documentKey + "|view-preview|";
                Published.RemoveWhere(key => key.StartsWith(
                    documentPrefix,
                    StringComparison.OrdinalIgnoreCase));
                FailedThisSession.RemoveWhere(key => key.StartsWith(
                    documentPrefix,
                    StringComparison.OrdinalIgnoreCase));
            }
            CancelBatchForDocument(documentKey);
        }

        private static bool CanCapture(Document document, View view)
        {
            if (document == null || view == null || view.IsTemplate)
                return false;

            try
            {
                return view.CanBePrinted &&
                       !(view is ViewSchedule);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasActiveTemporaryViewMode(View view)
        {
            if (view == null) return false;
            foreach (TemporaryViewMode mode in Enum.GetValues(
                         typeof(TemporaryViewMode)))
            {
                try
                {
                    if (view.IsInTemporaryViewMode(mode)) return true;
                }
                catch { }
            }

            return false;
        }

        private static ViewPreviewIdentity CreateIdentity(View view)
        {
            return new ViewPreviewIdentity
            {
                ViewId = GetElementIdText(view?.Id),
                ViewName = view?.Name ?? string.Empty
            };
        }

        private static DateTime MaxDateTime(DateTime first, DateTime second)
        {
            return first >= second ? first : second;
        }

        private static bool ExportViewPreview(
            Document document,
            View view,
            string targetPath)
        {
            string directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory)) return false;
            Directory.CreateDirectory(directory);

            string baseName = "capture-" +
                              GetElementIdText(view.Id) + "-" +
                              DateTime.UtcNow.Ticks.ToString(
                                  CultureInfo.InvariantCulture);
            string basePath = Path.Combine(directory, baseName);
            var before = new HashSet<string>(
                Directory.EnumerateFiles(directory, "*.png"),
                StringComparer.OrdinalIgnoreCase);

            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = basePath,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 360,
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_72
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            document.ExportImage(options);

            string generated = Directory
                .EnumerateFiles(directory, "*.png")
                .Where(path =>
                    !before.Contains(path) ||
                    Path.GetFileName(path).StartsWith(
                        baseName,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(generated) ||
                !File.Exists(generated))
            {
                return false;
            }

            File.Copy(generated, targetPath, true);
            if (!string.Equals(
                    generated,
                    targetPath,
                    StringComparison.OrdinalIgnoreCase) &&
                generated.StartsWith(
                    directory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(generated); }
                catch { }
            }

            return File.Exists(targetPath);
        }

        private static void ProcessScheduledCacheMaintenance(
            UIApplication uiApplication)
        {
            Document document = uiApplication?.ActiveUIDocument?.Document;
            if (document == null) return;

            string documentKey = Analyse.ElementHistoryTracker
                .GetDocumentKeyForHistory(document);
            bool shouldRun = false;
            lock (Sync)
            {
                if (DocumentStates.TryGetValue(
                        documentKey,
                        out DocumentPreviewState state) &&
                    DateTime.UtcNow >= state.CacheCleanupNotBeforeUtc)
                {
                    state.CacheCleanupNotBeforeUtc = DateTime.MaxValue;
                    shouldRun = true;
                }
            }

            if (!shouldRun) return;
            CleanupDeletedViewPreviews(document, documentKey);
            EnforceCacheSizeLimit();
        }

        private static void CleanupDeletedViewPreviews(
            Document document,
            string documentKey)
        {
            try
            {
                string documentFolder = Path.Combine(
                    CacheRoot,
                    Hash(documentKey).Substring(0, 20));
                if (!Directory.Exists(documentFolder)) return;

                var validFileNames = new HashSet<string>(
                    new FilteredElementCollector(document)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(view => !string.IsNullOrWhiteSpace(
                            view.UniqueId))
                        .Select(view =>
                            Hash(view.UniqueId).Substring(0, 24) + ".png"),
                    StringComparer.OrdinalIgnoreCase);

                foreach (string previewPath in Directory.EnumerateFiles(
                             documentFolder,
                             "*.png",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!validFileNames.Contains(
                            Path.GetFileName(previewPath)))
                    {
                        DeletePreviewFiles(previewPath);
                    }
                }
            }
            catch
            {
                // Cache maintenance must never interrupt Revit.
            }
        }

        private static void EnforceCacheSizeLimit()
        {
            try
            {
                if (!Directory.Exists(CacheRoot)) return;
                foreach (string versionPath in Directory.EnumerateFiles(
                             CacheRoot,
                             "*.png.version",
                             SearchOption.AllDirectories))
                {
                    string previewPath = versionPath.Substring(
                        0,
                        versionPath.Length - ".version".Length);
                    if (!File.Exists(previewPath))
                    {
                        try { File.Delete(versionPath); }
                        catch { }
                    }
                }

                List<FileInfo> previews = Directory
                    .EnumerateFiles(
                        CacheRoot,
                        "*.png",
                        SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .Where(file => file.Exists)
                    .OrderBy(file => file.LastWriteTimeUtc)
                    .ToList();
                long totalBytes = Directory
                    .EnumerateFiles(CacheRoot, "*", SearchOption.AllDirectories)
                    .Select(path =>
                    {
                        try { return new FileInfo(path).Length; }
                        catch { return 0L; }
                    })
                    .Sum();
                foreach (FileInfo preview in previews)
                {
                    if (totalBytes <= MaximumCacheBytes) break;
                    long length = preview.Length;
                    string versionPath = preview.FullName + ".version";
                    if (File.Exists(versionPath))
                    {
                        try { length += new FileInfo(versionPath).Length; }
                        catch { }
                    }
                    DeletePreviewFiles(preview.FullName);
                    if (!File.Exists(preview.FullName))
                        totalBytes -= length;
                }
            }
            catch
            {
                // A locked file is simply retained until the next cleanup.
            }
        }

        private static void DeletePreviewFiles(string previewPath)
        {
            try
            {
                if (File.Exists(previewPath)) File.Delete(previewPath);
            }
            catch { }
            try
            {
                string versionPath = previewPath + ".version";
                if (File.Exists(versionPath)) File.Delete(versionPath);
            }
            catch { }
        }

        private static DocumentPreviewState GetOrCreateState(
            string documentKey)
        {
            if (!DocumentStates.TryGetValue(documentKey, out var state))
            {
                state = new DocumentPreviewState();
                DocumentStates[documentKey] = state;
            }

            return state;
        }

        private static long GetRevision(
            Dictionary<string, long> revisions,
            string viewUniqueId)
        {
            return revisions.TryGetValue(viewUniqueId, out long revision)
                ? revision
                : 0L;
        }

        private static string GetDocumentVersionSignature(Document document)
        {
            try
            {
                DocumentVersion version = Document.GetDocumentVersion(document);
                return version.VersionGUID.ToString("D") + "|" +
                       version.NumberOfSaves.ToString(
                           CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool HasMatchingDocumentVersion(
            string targetPath,
            string expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(expectedVersion)) return true;
            string versionPath = targetPath + ".version";
            try
            {
                return File.Exists(versionPath) &&
                       string.Equals(
                           File.ReadAllText(versionPath).Trim(),
                           expectedVersion,
                           StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteDocumentVersion(
            string targetPath,
            string documentVersion)
        {
            if (string.IsNullOrWhiteSpace(documentVersion)) return;
            try { File.WriteAllText(targetPath + ".version", documentVersion); }
            catch { }
        }

        private static string GetPreviewPath(
            string documentKey,
            string viewUniqueId)
        {
            string documentFolder = Hash(documentKey).Substring(0, 20);
            string fileName = Hash(viewUniqueId).Substring(0, 24) + ".png";
            return Path.Combine(CacheRoot, documentFolder, fileName);
        }

        private static string BuildPreviewKey(
            string documentKey,
            string viewUniqueId)
        {
            if (string.IsNullOrWhiteSpace(documentKey) ||
                string.IsNullOrWhiteSpace(viewUniqueId))
            {
                return string.Empty;
            }

            return documentKey + "|view-preview|" + viewUniqueId;
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(value ?? string.Empty));
                return string.Concat(bytes.Select(
                    item => item.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static string GetElementIdText(ElementId elementId)
        {
            if (elementId == null) return string.Empty;
            foreach (string propertyName in new[]
                     {
                         "Value",
                         "IntegerValue"
                     })
            {
                try
                {
                    var valueProperty = elementId
                        .GetType()
                        .GetProperty(propertyName);
                    object value = valueProperty?.GetValue(elementId);
                    if (value != null)
                    {
                        return Convert.ToString(
                            value,
                            CultureInfo.InvariantCulture);
                    }
                }
                catch { }
            }

            return elementId.ToString() ?? string.Empty;
        }
    }
}

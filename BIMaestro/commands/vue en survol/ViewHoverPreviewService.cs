using Autodesk.Revit.DB;
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
    internal static class ViewHoverPreviewService
    {
        private sealed class PendingPreview
        {
            public string DocumentKey { get; set; }
            public string ViewUniqueId { get; set; }
            public ElementId ViewId { get; set; }
            public string ViewName { get; set; }
            public DateTime NotBeforeUtc { get; set; }
        }

        private static readonly object Sync = new object();
        private static readonly HashSet<string> Published =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> FailedThisSession =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static PendingPreview _pending;
        private static bool _isProcessing;

        private static readonly string CacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "CacheVignettes",
            "VueEnSurvol");

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
            lock (Sync)
            {
                if (Published.Contains(previewKey) ||
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
                    NotBeforeUtc = DateTime.UtcNow.AddSeconds(2)
                };
            }
        }

        internal static void ProcessPending(UIApplication uiApplication)
        {
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

                string previewKey = BuildPreviewKey(
                    pending.DocumentKey,
                    pending.ViewUniqueId);
                string targetPath = GetPreviewPath(
                    pending.DocumentKey,
                    pending.ViewUniqueId);

                if (!File.Exists(targetPath))
                    ExportViewPreview(document, activeView, targetPath);

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
                    dataUri);

                lock (Sync) { Published.Add(previewKey); }
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
            }
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

        private static void ExportViewPreview(
            Document document,
            View view,
            string targetPath)
        {
            string directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory)) return;
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
                return;
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
            try
            {
                var valueProperty = elementId
                    .GetType()
                    .GetProperty("Value");
                object value = valueProperty?.GetValue(elementId);
                if (value != null)
                {
                    return Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture);
                }
            }
            catch { }

#pragma warning disable CS0618
            return elementId.IntegerValue.ToString(
                CultureInfo.InvariantCulture);
#pragma warning restore CS0618
        }
    }
}

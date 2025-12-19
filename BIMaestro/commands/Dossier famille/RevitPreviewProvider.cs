// FamilyThumbnailProvider.cs
// Revit 2023+ — Miniature officielle des familles/types (Type Image prioritaire, sinon preview du type)
// - Pas de génération 3D
// - Bloque l'ouverture des RFA si une mise à niveau serait nécessaire (guard WouldUpgrade)
// - Cross-version: pas d'accès direct à API instables; réflexions + parsing

using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Famille
{
    internal static class FamilyThumbnailProvider
    {
        // ---------- Queue + ExternalEvent ----------
        private sealed class PreviewRequest
        {
            public string FamilyPath;
            public ElementId LoadedTypeId;
            public int Size;
            public TaskCompletionSource<BitmapSource> Tcs;
        }

        private sealed class Handler : IExternalEventHandler
        {
            private readonly Queue<PreviewRequest> _q;
            public Handler(Queue<PreviewRequest> q) => _q = q;
            public string GetName() => nameof(FamilyThumbnailProvider);

            public void Execute(UIApplication app)
            {
                PreviewRequest r;
                while ((r = Dequeue()) != null)
                {
                    BitmapSource result = null;
                    try
                    {
                        if (!string.IsNullOrEmpty(r.FamilyPath))
                            result = ExtractFromFamilyFile(app, r.FamilyPath, r.Size);
                        else if (r.LoadedTypeId != null && r.LoadedTypeId != ElementId.InvalidElementId)
                            result = ExtractFromLoadedType(app.ActiveUIDocument?.Document, r.LoadedTypeId, r.Size);
                    }
                    catch { }
                    r.Tcs.TrySetResult(result);
                }
            }

            private PreviewRequest Dequeue()
            {
                lock (_q) { return _q.Count == 0 ? null : _q.Dequeue(); }
            }
        }

        private static readonly Queue<PreviewRequest> _queue = new();
        private static ExternalEvent _eventRef;
        private static Handler _handler;

        public static void Initialize(UIApplication uiapp)
        {
            if (uiapp == null || _handler != null) return;
            _handler = new Handler(_queue);
            try { _eventRef = ExternalEvent.Create(_handler); } catch { _eventRef = null; }
        }

        public static Task<BitmapSource> RequestFromFamilyFileAsync(string familyPath, int size)
        {
            var req = new PreviewRequest
            {
                FamilyPath = familyPath,
                Size = Math.Max(16, size),
                Tcs = new TaskCompletionSource<BitmapSource>()
            };
            Enqueue(req);
            return req.Tcs.Task;
        }

        public static Task<BitmapSource> RequestFromLoadedTypeAsync(ElementId typeId, int size)
        {
            var req = new PreviewRequest
            {
                LoadedTypeId = typeId,
                Size = Math.Max(16, size),
                Tcs = new TaskCompletionSource<BitmapSource>()
            };
            Enqueue(req);
            return req.Tcs.Task;
        }

        private static void Enqueue(PreviewRequest r)
        {
            if (_eventRef == null) { r.Tcs.TrySetResult(null); return; }
            lock (_queue) { _queue.Enqueue(r); }
            try { _eventRef.Raise(); } catch { r.Tcs.TrySetResult(null); }
        }

        // ---------- Implémentations ----------
        private static BitmapSource ExtractFromFamilyFile(UIApplication uiapp, string path, int size)
        {
            if (uiapp == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            // ⛔ N’ouvre pas si l’ouverture provoquerait une mise à niveau
            if (WouldUpgrade(uiapp.Application, path))
                return null;

            Document famDoc = null;
            try
            {
                famDoc = uiapp.Application.OpenDocumentFile(path);
                if (famDoc == null || !famDoc.IsFamilyDocument) return null;

                // 1) on récupère un symbol (type) de la famille
                var symbol = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();

                if (symbol != null)
                {
                    // 1a) Type Image si présent (identique à la vignette de la palette Propriétés)
                    var fromTypeImage = TryGetTypeImageBitmapSource(famDoc, symbol, size);
                    if (fromTypeImage != null) return fromTypeImage;

                    // 1b) Preview stockée sur le type (peut être 3D si la famille a été enregistrée ainsi)
                    using (var bmp = symbol.GetPreviewImage(new System.Drawing.Size(size, size)))
                    {
                        if (bmp != null)
                        {
                            using (var scaled = DownscaleIfNeeded(bmp, size))
                                return ToSource(scaled ?? bmp);
                        }
                    }
                }

                return null;
            }
            catch { return null; }
            finally
            {
                if (famDoc != null) { try { famDoc.Close(false); } catch { } }
            }
        }

        private static BitmapSource ExtractFromLoadedType(Document doc, ElementId typeId, int size)
        {
            if (doc == null || typeId == null || typeId == ElementId.InvalidElementId) return null;

            try
            {
                if (doc.GetElement(typeId) is FamilySymbol fs)
                {
                    var fromTypeImage = TryGetTypeImageBitmapSource(doc, fs, size);
                    if (fromTypeImage != null) return fromTypeImage;

                    using (var bmp = fs.GetPreviewImage(new System.Drawing.Size(size, size)))
                    {
                        if (bmp != null)
                        {
                            using (var scaled = DownscaleIfNeeded(bmp, size))
                                return ToSource(scaled ?? bmp);
                        }
                    }
                }
                else if (doc.GetElement(typeId) is ElementType et)
                {
                    using (var bmp = et.GetPreviewImage(new System.Drawing.Size(size, size)))
                    {
                        if (bmp != null)
                        {
                            using (var scaled = DownscaleIfNeeded(bmp, size))
                                return ToSource(scaled ?? bmp);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Renvoie l'image du paramètre "Type Image" si disponible (fichier externe ou image embarquée).
        /// Cross-version : ExternalFileUtils (2023+) + réflexion sur ImageType.GetImage() si présent.
        /// </summary>
        private static BitmapSource TryGetTypeImageBitmapSource(Document doc, FamilySymbol fs, int target)
        {
            try
            {
                var p = fs.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_IMAGE);
                if (p == null || p.StorageType != StorageType.ElementId) return null;

                var imgId = p.AsElementId();
                if (imgId == ElementId.InvalidElementId) return null;

                var img = doc.GetElement(imgId) as ImageType;
                if (img == null) return null;

                // Cas A : image EXTERNE référencée (chemin disque)
                try
                {
                    var extRef = ExternalFileUtils.GetExternalFileReference(doc, img.Id);
                    if (extRef != null)
                    {
                        var modelPath = extRef.GetAbsolutePath(); // ModelPath
                        var abs = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                        if (!string.IsNullOrEmpty(abs) && File.Exists(abs))
                            return LoadImageFileAsBitmapSource(abs, target);
                    }
                }
                catch { /* pas d'external ref → on tente embarqué */ }

                // Cas B : image EMBARQUÉE dans le RFA/RVT — via réflexion (API non stable selon versions)
                try
                {
                    var m = typeof(ImageType).GetMethod("GetImage",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null)
                    {
                        using (var sysImg = m.Invoke(img, null) as System.Drawing.Image)
                        {
                            if (sysImg != null)
                            {
                                using (var bmp = new Bitmap(sysImg))
                                using (var scaled = DownscaleIfNeeded(bmp, target))
                                    return ToSource(scaled ?? bmp);
                            }
                        }
                    }
                }
                catch { }

                return null;
            }
            catch { return null; }
        }

        // ---------- Garde-fou "no upgrade" cross-version ----------
        internal static bool WouldUpgrade(Application app, string rfaPath)
        {
            try
            {
                var info = BasicFileInfo.Extract(rfaPath);
                if (info == null) return true; // prudence

                int current = ParseYear(app?.VersionNumber);

                if (TryGetSavedMajorVersion(info, out int saved))
                    return saved != current;

                if (TryGetYearFromProp(info, "RevitBuild", out saved) ||
                    TryGetYearFromProp(info, "RevitProduct", out saved) ||
                    TryGetYearFromProp(info, "Format", out saved))
                    return saved != current;

                return true; // inconnu → on n'ouvre pas
            }
            catch
            {
                return true;
            }
        }

        private static bool TryGetSavedMajorVersion(object info, out int year)
        {
            return
                TryGetYearFromProp(info, "SavedInVersion", out year) ||
                TryGetYearFromProp(info, "SavedInVersionNumber", out year) ||
                TryGetYearFromProp(info, "SavedInVersionMajor", out year) ||
                TryGetYearFromProp(info, "SavedIn", out year) ||
                TryGetYearFromProp(info, "FileVersion", out year);
        }

        private static bool TryGetYearFromProp(object obj, string propName, out int year)
        {
            year = 0;
            var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return false;

            var val = p.GetValue(obj);
            if (val == null) return false;

            if (val is int i)
            {
                year = NormalizeToYear(i);
                return year >= 2008;
            }

            string s = val.ToString();
            var m = Regex.Match(s, @"\b(20\d{2})\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out year))
                return true;

            if (int.TryParse(s, out i))
            {
                year = NormalizeToYear(i);
                return year >= 2008;
            }

            return false;
        }

        private static int NormalizeToYear(int v)
        {
            if (v < 100 && v >= 8) return 2000 + v; // 23 -> 2023
            return v;
        }

        private static int ParseYear(string s)
        {
            if (int.TryParse(s, out int v)) return NormalizeToYear(v);
            var m = Regex.Match(s ?? "", @"\b(20\d{2})\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int y)) return y;
            return 9999; // inconnu
        }

        // ---------- Utils images ----------
        private static BitmapSource LoadImageFileAsBitmapSource(string path, int decodeWidth)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        private static Bitmap DownscaleIfNeeded(Bitmap original, int box)
        {
            if (original == null || box <= 0) return null;
            int max = Math.Max(original.Width, original.Height);
            if (max <= box) return null;

            double k = (double)box / max;
            int w = Math.Max(1, (int)Math.Round(original.Width * k));
            int h = Math.Max(1, (int)Math.Round(original.Height * k));

            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(original, 0, 0, w, h);
            }
            return bmp;
        }

        private static BitmapSource ToSource(Bitmap bitmap)
        {
            if (bitmap == null) return null;
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = dec.Frames.Count > 0 ? dec.Frames[0] : null;
                frame?.Freeze();
                return frame;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// ✅ WPF imaging, mais SANS RenderTargetBitmap/DrawingVisual (cause NotImplemented)
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Famille
{
    public sealed class PreviewGenerationRequest
    {
        public IReadOnlyList<PreviewEntry> Entries { get; set; } = Array.Empty<PreviewEntry>();
        public string TargetRoot { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Action<PreviewGenerationProgress> ProgressCallback { get; set; }
        public Action<string> LogCallback { get; set; }

        // Export Revit
        public int RevitExportPixelSize { get; set; } = 1600;
        public ImageResolution RevitImageResolution { get; set; } = ImageResolution.DPI_150;

        // Nettoyage vue
        public bool HideAllAnnotationCategories { get; set; } = true;
        public bool HideNoisyCategoriesByNameHeuristic { get; set; } = true;
        public bool TryImproveViewIfPossible { get; set; } = true;

        // ✅ Post-process demandé : centrer + pad carré sans scaling
        public bool PostProcessToSquareNoScale { get; set; } = true;
        public bool TransparentBackground { get; set; } = true;

        // Détection fond/contenu (robuste)
        public int BackgroundSampleSize { get; set; } = 18;     // coins
        public byte BackgroundTolerance { get; set; } = 22;     // tolérance couleur
        public double CropMarginFactor { get; set; } = 0.04;    // marge autour contenu
    }

    public sealed class PreviewEntry
    {
        public string FamilyPath { get; set; }
        public string RelativePath { get; set; }
    }

    public sealed class PreviewGenerationProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public string CurrentFile { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsCanceled { get; set; }
    }

    public class GeneratePreviewImagesHandler : IExternalEventHandler
    {
        public PreviewGenerationRequest Request { get; set; }

        public void Execute(UIApplication app)
        {
            var req = Request;
            if (app == null || req == null || req.Entries == null || req.Entries.Count == 0)
                return;

            int total = req.Entries.Count;
            int done = 0;

            foreach (var entry in req.Entries)
            {
                if (req.CancellationToken.IsCancellationRequested)
                {
                    PublishProgress(req, done, total, entry?.FamilyPath, isCanceled: true);
                    break;
                }

                PublishProgress(req, done, total, entry?.FamilyPath);

                if (!TryExportFamilyPreview(app, entry, req, out var error))
                {
                    if (!string.IsNullOrWhiteSpace(error))
                        SafeLog(req.LogCallback, $"⚠️ {Path.GetFileName(entry?.FamilyPath ?? "")} : {error}");
                }

                done++;
                PublishProgress(req, done, total, entry?.FamilyPath);
            }

            PublishProgress(req, done, total, null, isCompleted: true, isCanceled: req.CancellationToken.IsCancellationRequested);
        }

        public string GetName() => nameof(GeneratePreviewImagesHandler);

        private static void PublishProgress(PreviewGenerationRequest req, int completed, int total, string current, bool isCompleted = false, bool isCanceled = false)
        {
            try
            {
                req.ProgressCallback?.Invoke(new PreviewGenerationProgress
                {
                    Completed = completed,
                    Total = total,
                    CurrentFile = current,
                    IsCompleted = isCompleted,
                    IsCanceled = isCanceled
                });
            }
            catch { }
        }

        private static void SafeLog(Action<string> logger, string message)
        {
            try { logger?.Invoke(message); } catch { }
        }

        private static bool TryExportFamilyPreview(UIApplication uiapp, PreviewEntry entry, PreviewGenerationRequest req, out string error)
        {
            error = null;

            if (entry == null || string.IsNullOrWhiteSpace(entry.FamilyPath))
            {
                error = "Entrée invalide.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(req.TargetRoot))
            {
                error = "Dossier miroir non défini.";
                return false;
            }

            if (!File.Exists(entry.FamilyPath))
            {
                error = "Fichier introuvable.";
                return false;
            }

            // Bloque uniquement si famille plus récente
            try
            {
                if (IsSavedInNewerRevitVersion(uiapp.Application, entry.FamilyPath))
                {
                    error = "Famille enregistrée dans une version Revit plus récente (impossible à ouvrir).";
                    return false;
                }
            }
            catch (Exception ex)
            {
                SafeLog(req.LogCallback, $"ℹ️ Info version ignorée : {ex.Message}");
            }

            Document famDoc = null;

            try
            {
                famDoc = uiapp.Application.OpenDocumentFile(entry.FamilyPath);
                if (famDoc == null || !famDoc.IsFamilyDocument)
                {
                    error = "Fichier non reconnu comme famille.";
                    return false;
                }

                var view = GetExisting3DView(famDoc, out var viewError);
                if (view == null)
                {
                    error = viewError ?? "Aucune vue 3D exportable.";
                    return false;
                }

                if (req.TryImproveViewIfPossible)
                {
                    TryPrepareViewForExport(famDoc, view, req.LogCallback);
                    CleanViewForThumbnailIfPossible(famDoc, view, req, req.LogCallback);
                    ConfigurePreviewOrientationIfPossible(famDoc, view, req.LogCallback);
                    try { famDoc.Regenerate(); } catch { }
                }

                var targetPng = GetTargetPath(entry, req.TargetRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPng));

                if (!ExportViewToPngRobust(famDoc, view, targetPng, req.RevitExportPixelSize, req.RevitImageResolution))
                {
                    error = "Export PNG impossible (aucun fichier créé).";
                    return false;
                }

                if (req.PostProcessToSquareNoScale)
                {
                    if (!CenterContentAndPadToSquare_NoScale_Pixels(
                            targetPng,
                            req.TransparentBackground,
                            req.BackgroundSampleSize,
                            req.BackgroundTolerance,
                            req.CropMarginFactor,
                            req.LogCallback))
                    {
                        SafeLog(req.LogCallback, $"⚠️ Post-process 1:1 non appliqué : {Path.GetFileName(targetPng)}");
                    }
                }

                return true;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                error = $"API Revit : {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { famDoc?.Close(false); } catch { }
            }
        }

        private static string GetTargetPath(PreviewEntry entry, string targetRoot)
        {
            var relative = string.IsNullOrWhiteSpace(entry.RelativePath)
                ? Path.GetFileName(entry.FamilyPath) ?? "preview.png"
                : entry.RelativePath;

            var mirrorPath = Path.Combine(targetRoot, relative);
            return Path.ChangeExtension(mirrorPath, ".png");
        }

        private static View3D GetExisting3DView(Document famDoc, out string error)
        {
            error = null;

            var views = new FilteredElementCollector(famDoc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate)
                .ToList();

            if (views.Count == 0)
            {
                error = "Aucune vue 3D existante dans la famille.";
                return null;
            }

            var default3d = views.FirstOrDefault(v =>
            {
                try { return string.Equals(v.Name, "{3D}", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });

            return default3d ?? views[0];
        }

        private static bool TryPrepareViewForExport(Document famDoc, View3D view, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(famDoc, "Préparer vue export"))
                {
                    var st = tx.Start();
                    if (st != TransactionStatus.Started)
                    {
                        SafeLog(log, $"ℹ️ Préparer vue export : transaction refusée ({st}).");
                        return false;
                    }

                    try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                    try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }

                    tx.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Préparer vue export : {ex.Message}");
                return false;
            }
        }

        private static void CleanViewForThumbnailIfPossible(Document doc, View3D view, PreviewGenerationRequest req, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(doc, "Nettoyage vue thumbnail"))
                {
                    var st = tx.Start();
                    if (st != TransactionStatus.Started)
                    {
                        SafeLog(log, $"ℹ️ Nettoyage : transaction refusée ({st}).");
                        return;
                    }

                    int hidden = 0;

                    if (req.HideAllAnnotationCategories)
                    {
                        foreach (Category cat in doc.Settings.Categories)
                        {
                            if (cat == null) continue;
                            if (cat.CategoryType != CategoryType.Annotation) continue;

                            try
                            {
                                if (!view.CanCategoryBeHidden(cat.Id)) continue;
                                if (!view.GetCategoryHidden(cat.Id))
                                {
                                    view.SetCategoryHidden(cat.Id, true);
                                    hidden++;
                                }
                            }
                            catch { }
                        }
                    }

                    if (req.HideNoisyCategoriesByNameHeuristic)
                    {
                        string[] tokens =
                        {
                            "reference", "référence", "ref", "plane", "plan",
                            "level", "niveau",
                            "grid", "quadrillage",
                            "dimension", "cote", "côtes",
                            "text", "texte",
                            "annotation", "étiquette", "tag",
                            "symbol", "symbole"
                        };

                        foreach (Category cat in doc.Settings.Categories)
                        {
                            if (cat == null) continue;

                            string name;
                            try { name = cat.Name ?? ""; } catch { name = ""; }
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            if (!tokens.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                                continue;

                            try
                            {
                                if (!view.CanCategoryBeHidden(cat.Id)) continue;
                                if (!view.GetCategoryHidden(cat.Id))
                                {
                                    view.SetCategoryHidden(cat.Id, true);
                                    hidden++;
                                }
                            }
                            catch { }
                        }
                    }

                    tx.Commit();
                    SafeLog(log, $"✅ Nettoyage : {hidden} catégories masquées.");
                }
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Nettoyage : {ex.Message}");
            }
        }

        private static void ConfigurePreviewOrientationIfPossible(Document famDoc, View3D view, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(famDoc, "Orientation preview"))
                {
                    var st = tx.Start();
                    if (st != TransactionStatus.Started)
                    {
                        SafeLog(log, $"ℹ️ Orientation : transaction refusée ({st}).");
                        return;
                    }

                    var bbox = GetModelBoundingBox(famDoc);
                    if (bbox == null)
                    {
                        tx.RollBack();
                        return;
                    }

                    var center = (bbox.Min + bbox.Max) * 0.5;
                    var extents = (bbox.Max - bbox.Min);
                    double radius = Math.Max(Math.Max(extents.X, extents.Y), extents.Z);
                    if (radius < 1e-6) radius = 10;

                    var offsetDir = new XYZ(-1, -1, 1).Normalize();
                    var eye = center + offsetDir.Multiply(radius * 2.5);
                    var forward = (center - eye);
                    if (forward.GetLength() < 1e-6) forward = offsetDir.Multiply(-1);
                    forward = forward.Normalize();

                    try { view.SetOrientation(new ViewOrientation3D(eye, XYZ.BasisZ, forward)); } catch { }
                    try { view.SaveOrientationAndLock(); } catch { }

                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Orientation : {ex.Message}");
            }
        }

        private static BoundingBoxXYZ GetModelBoundingBox(Document doc)
        {
            BoundingBoxXYZ acc = null;

            var elems = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var e in elems)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { }

                if (bb == null || bb.Min == null || bb.Max == null) continue;

                if (acc == null)
                {
                    acc = new BoundingBoxXYZ { Min = bb.Min, Max = bb.Max };
                }
                else
                {
                    acc.Min = new XYZ(
                        Math.Min(acc.Min.X, bb.Min.X),
                        Math.Min(acc.Min.Y, bb.Min.Y),
                        Math.Min(acc.Min.Z, bb.Min.Z));

                    acc.Max = new XYZ(
                        Math.Max(acc.Max.X, bb.Max.X),
                        Math.Max(acc.Max.Y, bb.Max.Y),
                        Math.Max(acc.Max.Z, bb.Max.Z));
                }
            }

            return acc;
        }

        private static bool ExportViewToPngRobust(Document famDoc, View3D view, string targetPng, int pixelSize, ImageResolution resolution)
        {
            var outDir = Path.GetDirectoryName(targetPng) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outDir))
                return false;

            Directory.CreateDirectory(outDir);

            var baseName = Path.GetFileNameWithoutExtension(targetPng);
            var basePath = Path.Combine(outDir, baseName);

            var before = new HashSet<string>(Directory.EnumerateFiles(outDir, "*.png"), StringComparer.OrdinalIgnoreCase);

            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = basePath,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = resolution,
                FitDirection = FitDirectionType.Horizontal,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = Math.Max(256, pixelSize)
            };

            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            try { famDoc.ExportImage(options); }
            catch { return false; }

            var after = Directory.EnumerateFiles(outDir, "*.png").ToList();
            var created = after.Where(f => !before.Contains(f)).ToList();

            string picked = created.Count > 0
                ? created.OrderByDescending(File.GetCreationTimeUtc).FirstOrDefault()
                : after.Where(f => Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(File.GetCreationTimeUtc)
                       .FirstOrDefault();

            if (picked == null || !File.Exists(picked))
                return false;

            MoveOrReplace(picked, targetPng);
            return File.Exists(targetPng);
        }

        private static void MoveOrReplace(string source, string target)
        {
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(source, target);
            }
            catch
            {
                try { File.Copy(source, target, overwrite: true); } catch { }
            }
        }

        // ==========================================================
        // ✅ Pixel-only post process: centre contenu + pad au carré (NO SCALE)
        // ==========================================================

        private struct Rgba { public byte R, G, B, A; }

        private static bool CenterContentAndPadToSquare_NoScale_Pixels(
            string pngPath,
            bool transparentBackground,
            int sampleSize,
            byte tolerance,
            double marginFactor,
            Action<string> log)
        {
            try
            {
                var src = LoadPngAsBgra32(pngPath);
                if (src == null) return false;

                int w = src.PixelWidth;
                int h = src.PixelHeight;
                int stride = w * 4;

                var pixels = new byte[h * stride];
                src.CopyPixels(pixels, stride, 0);

                var bg = EstimateBackgroundFromCorners(pixels, w, h, stride, Math.Max(2, sampleSize));

                if (!TryFindContentBounds(pixels, w, h, stride, bg, tolerance, out int minX, out int minY, out int maxX, out int maxY))
                {
                    SafeLog(log, $"ℹ️ PostProcess: contenu introuvable (image uniforme ?) {Path.GetFileName(pngPath)}");
                    return false;
                }

                int contentW = maxX - minX + 1;
                int contentH = maxY - minY + 1;

                int margin = (int)(Math.Max(contentW, contentH) * Clamp(marginFactor, 0, 0.30));

                int cropX = Math.Max(0, minX - margin);
                int cropY = Math.Max(0, minY - margin);
                int cropW = Math.Min(w - cropX, contentW + 2 * margin);
                int cropH = Math.Min(h - cropY, contentH + 2 * margin);

                // 1) extraire pixels crop (NO SCALE)
                byte[] cropPixels = new byte[cropH * cropW * 4];
                for (int y = 0; y < cropH; y++)
                {
                    Buffer.BlockCopy(
                        pixels,
                        ((cropY + y) * stride) + (cropX * 4),
                        cropPixels,
                        y * (cropW * 4),
                        cropW * 4);
                }

                // 2) créer canvas carré (size = côté le plus long du crop)
                int size = Math.Max(cropW, cropH);
                byte[] outPixels = new byte[size * size * 4];

                // Remplissage fond
                if (!transparentBackground)
                {
                    for (int i = 0; i < outPixels.Length; i += 4)
                    {
                        outPixels[i + 0] = 255; // B
                        outPixels[i + 1] = 255; // G
                        outPixels[i + 2] = 255; // R
                        outPixels[i + 3] = 255; // A
                    }
                }
                // sinon transparent => déjà 0

                // 3) coller le crop centré dans le carré
                int offX = (size - cropW) / 2;
                int offY = (size - cropH) / 2;
                int outStride = size * 4;
                int cropStride = cropW * 4;

                for (int y = 0; y < cropH; y++)
                {
                    Buffer.BlockCopy(
                        cropPixels,
                        y * cropStride,
                        outPixels,
                        ((offY + y) * outStride) + (offX * 4),
                        cropStride);
                }

                // 4) écrire PNG (sans Render)
                var wb = new WriteableBitmap(size, size, src.DpiX > 0 ? src.DpiX : 96, src.DpiY > 0 ? src.DpiY : 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, size, size), outPixels, outStride, 0);

                SaveBitmapSourceAsPng(wb, pngPath);

                SafeLog(log, $"✅ PostProcess 1:1 (no-scale) : {Path.GetFileName(pngPath)} ({w}x{h} -> crop {cropW}x{cropH} -> {size}x{size})");
                return true;
            }
            catch (Exception ex)
            {
                SafeLog(log, $"⚠️ PostProcess 1:1 (no-scale) échec {Path.GetFileName(pngPath)} : {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        private static BitmapSource LoadPngAsBgra32(string path)
        {
            if (!File.Exists(path)) return null;

            BitmapSource frame;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var decoder = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame = decoder.Frames[0];
            }

            return new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        }

        private static void SaveBitmapSourceAsPng(BitmapSource source, string path)
        {
            var tmp = path + ".tmp";
            using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(outFs);
            }

            try { File.Delete(path); } catch { }
            File.Move(tmp, path);
        }

        private static Rgba EstimateBackgroundFromCorners(byte[] px, int w, int h, int stride, int sample)
        {
            long r = 0, g = 0, b = 0, a = 0;
            long count = 0;

            void Accum(int startX, int startY)
            {
                int endX = Math.Min(w, startX + sample);
                int endY = Math.Min(h, startY + sample);

                for (int y = startY; y < endY; y++)
                {
                    int row = y * stride;
                    for (int x = startX; x < endX; x++)
                    {
                        int i = row + x * 4;
                        b += px[i + 0];
                        g += px[i + 1];
                        r += px[i + 2];
                        a += px[i + 3];
                        count++;
                    }
                }
            }

            Accum(0, 0);
            Accum(Math.Max(0, w - sample), 0);
            Accum(0, Math.Max(0, h - sample));
            Accum(Math.Max(0, w - sample), Math.Max(0, h - sample));

            if (count <= 0) return new Rgba { R = 255, G = 255, B = 255, A = 255 };

            return new Rgba
            {
                R = (byte)(r / count),
                G = (byte)(g / count),
                B = (byte)(b / count),
                A = (byte)(a / count)
            };
        }

        private static bool TryFindContentBounds(byte[] px, int w, int h, int stride, Rgba bg, byte tol,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = w; minY = h; maxX = -1; maxY = -1;

            const byte alphaBgThreshold = 8;

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    byte b = px[i + 0];
                    byte g = px[i + 1];
                    byte r = px[i + 2];
                    byte a = px[i + 3];

                    bool isBg;
                    if (a <= alphaBgThreshold)
                    {
                        isBg = true;
                    }
                    else
                    {
                        isBg =
                            Math.Abs(r - bg.R) <= tol &&
                            Math.Abs(g - bg.G) <= tol &&
                            Math.Abs(b - bg.B) <= tol;
                    }

                    if (!isBg)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            return maxX >= 0 && maxY >= 0;
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        private static bool IsSavedInNewerRevitVersion(Autodesk.Revit.ApplicationServices.Application app, string filePath)
        {
            var bfi = BasicFileInfo.Extract(filePath);
            if (bfi == null) return false;

            if (!int.TryParse(app.VersionNumber, out int appYear)) return false;

            int fileYear = 0;

            try
            {
                var prop = bfi.GetType().GetProperty("SavedInVersion");
                if (prop != null)
                {
                    var v = prop.GetValue(bfi, null);
                    if (v is int vi) fileYear = vi;
                    else if (v is string vs)
                    {
                        var digits = new string(vs.Where(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out int parsed)) fileYear = parsed;
                    }
                }
            }
            catch { }

            if (fileYear <= 0) return false;
            return fileYear > appYear;
        }
    }
}

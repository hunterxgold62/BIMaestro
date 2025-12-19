using System;
using System.Collections.Generic;
using System.Drawing;                       // ✅ post-traitement (carré sans crop)
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Famille
{
    public sealed class PreviewGenerationRequest
    {
        public IReadOnlyList<PreviewEntry> Entries { get; set; } = Array.Empty<PreviewEntry>();
        public string TargetRoot { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Action<PreviewGenerationProgress> ProgressCallback { get; set; }
        public Action<string> LogCallback { get; set; }
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

                PublishProgress(req, done, total, entry?.FamilyPath, isCanceled: false);

                if (!TryExportFamilyPreview(app, entry, req.TargetRoot, req.LogCallback, out var error))
                {
                    if (!string.IsNullOrWhiteSpace(error))
                        SafeLog(req.LogCallback, $"⚠️ {Path.GetFileName(entry?.FamilyPath ?? "")} : {error}");
                }

                done++;
                PublishProgress(req, done, total, entry?.FamilyPath, isCanceled: false);
            }

            PublishProgress(req, done, total, null, isCompleted: true, isCanceled: req.CancellationToken.IsCancellationRequested);
        }

        public string GetName() => nameof(GeneratePreviewImagesHandler);

        private static void PublishProgress(
            PreviewGenerationRequest req,
            int completed,
            int total,
            string current,
            bool isCompleted = false,
            bool isCanceled = false)
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
            catch
            {
                // best-effort
            }
        }

        private static void SafeLog(Action<string> logger, string message)
        {
            try { logger?.Invoke(message); } catch { }
        }

        private static bool TryExportFamilyPreview(
            UIApplication uiapp,
            PreviewEntry entry,
            string targetRoot,
            Action<string> log,
            out string error)
        {
            error = null;

            if (entry == null || string.IsNullOrWhiteSpace(entry.FamilyPath))
            {
                error = "Entrée invalide.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                error = "Dossier miroir non défini.";
                return false;
            }

            if (!File.Exists(entry.FamilyPath))
            {
                error = "Fichier introuvable.";
                return false;
            }

            // ✅ Bloque uniquement si famille enregistrée dans une version plus récente que Revit courant
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
                SafeLog(log, $"ℹ️ Info version ignorée : {ex.Message}");
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

                // ✅ IMPORTANT : on NE CRÉE PAS de vue (évite 'Modification forbidden')
                var view = GetExisting3DView(famDoc, out var viewError);
                if (view == null)
                {
                    error = viewError ?? "Aucune vue 3D exportable.";
                    return false;
                }

                // Best-effort : on nettoie/ prépare/oriente seulement si Revit autorise les modifs
                TryPrepareViewForExport(famDoc, view);
                CleanViewForThumbnailIfPossible(famDoc, view);
                ConfigurePreviewOrientationIfPossible(famDoc, view);
                try { famDoc.Regenerate(); } catch { }

                var targetPng = GetTargetPath(entry, targetRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPng));

                if (!ExportViewToPngRobust(famDoc, view, targetPng))
                {
                    error = "Export PNG impossible (aucun fichier créé).";
                    return false;
                }

                // ✅ 1:1 SANS COUPER : padding carré (fond transparent)
                PadPngToSquare(targetPng);

                return true;
            }
            catch (IOException io)
            {
                error = $"Fichier verrouillé ou inaccessible : {io.Message}";
                return false;
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

        // ✅ Récupère une vue 3D existante (pas template). On privilégie "{3D}" si elle existe.
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

        private static bool TryPrepareViewForExport(Document famDoc, View3D view)
        {
            if (famDoc == null || view == null) return false;
            if (!famDoc.IsModifiable) return false;

            try
            {
                using (var tx = new Transaction(famDoc, "Préparer vue export"))
                {
                    if (tx.Start() != TransactionStatus.Started) return false;

                    try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                    try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }

                    tx.Commit();
                    return true;
                }
            }
            catch { return false; }
        }

        // ✅ "Aperçu de la Visibilité" côté API : on reproduit l'effet en masquant annotations/catégories parasites
        private static void CleanViewForThumbnailIfPossible(Document doc, View3D view)
        {
            if (doc == null || view == null) return;
            if (!doc.IsModifiable) return;

            try
            {
                using (var tx = new Transaction(doc, "Nettoyage vue thumbnail"))
                {
                    if (tx.Start() != TransactionStatus.Started) return;

                    // 1) Certaines versions exposent un bool global
                    TrySetBoolProperty(view, "AreAnnotationCategoriesHidden", true);

                    // 2) En complément : cache des catégories courantes "bruyantes"
                    HideCategoryIfExists(doc, view, BuiltInCategory.OST_ReferenceLines);
                    HideCategoryIfExists(doc, view, BuiltInCategory.OST_Levels);
                    HideCategoryIfExists(doc, view, BuiltInCategory.OST_Grids);
                    HideCategoryIfExists(doc, view, BuiltInCategory.OST_Dimensions);
                    HideCategoryIfExists(doc, view, BuiltInCategory.OST_TextNotes);
                    HideCategoryIfExists(doc, view, BuiltInCategory.OST_GenericAnnotation);

                    // Imports (DWG etc) si jamais
                    HideCategoryIfExists(doc, view, BuiltInCategory.OST_ImportObjectStyles);

                    tx.Commit();
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static void HideCategoryIfExists(Document doc, View view, BuiltInCategory bic)
        {
            try
            {
                var cat = Category.GetCategory(doc, bic);
                if (cat == null) return;
                if (!view.CanCategoryBeHidden(cat.Id)) return;

                view.SetCategoryHidden(cat.Id, true);
            }
            catch { }
        }

        private static void TrySetBoolProperty(object obj, string propertyName, bool value)
        {
            try
            {
                var pi = obj.GetType().GetProperty(propertyName);
                if (pi == null) return;
                if (pi.PropertyType != typeof(bool)) return;
                if (!pi.CanWrite) return;
                pi.SetValue(obj, value, null);
            }
            catch { }
        }

        private static void ConfigurePreviewOrientationIfPossible(Document famDoc, View3D view)
        {
            if (famDoc == null || view == null) return;
            if (!famDoc.IsModifiable) return; // ✅ évite "Modification forbidden"

            try
            {
                using (var tx = new Transaction(famDoc, "Orientation preview"))
                {
                    if (tx.Start() != TransactionStatus.Started) return;

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

                    var offsetDir = new XYZ(-1, -1, 1);
                    if (offsetDir.GetLength() < 1e-6) offsetDir = new XYZ(-1, -1, 1);
                    offsetDir = offsetDir.Normalize();

                    var eye = center + offsetDir.Multiply(radius * 2.5);
                    var forward = center - eye;
                    if (forward.GetLength() < 1e-6) forward = offsetDir.Multiply(-1);
                    forward = forward.Normalize();

                    try { view.SetOrientation(new ViewOrientation3D(eye, XYZ.BasisZ, forward)); } catch { }
                    try { view.SaveOrientationAndLock(); } catch { }

                    // Section box best-effort
                    try
                    {
                        double margin = radius * 0.2;
                        var section = new BoundingBoxXYZ
                        {
                            Min = new XYZ(bbox.Min.X - margin, bbox.Min.Y - margin, bbox.Min.Z - margin),
                            Max = new XYZ(bbox.Max.X + margin, bbox.Max.Y + margin, bbox.Max.Z + margin)
                        };

                        try { view.SetSectionBox(section); } catch { }
                        try { view.IsSectionBoxActive = true; } catch { }
                    }
                    catch { }

                    tx.Commit();
                }
            }
            catch
            {
                // best-effort
            }
        }

        // ✅ Document n'a pas de BoundingBox : union des bounding boxes d'éléments
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

        // ✅ Export blindé : on récupère le PNG réellement généré (nom variable selon versions/langue)
        private static bool ExportViewToPngRobust(Document famDoc, View3D view, string targetPng)
        {
            var outDir = Path.GetDirectoryName(targetPng) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outDir))
                return false;

            Directory.CreateDirectory(outDir);

            var baseName = Path.GetFileNameWithoutExtension(targetPng);
            var basePath = Path.Combine(outDir, baseName);

            // Snapshot avant export
            var before = new HashSet<string>(Directory.EnumerateFiles(outDir, "*.png"), StringComparer.OrdinalIgnoreCase);

            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = basePath,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                FitDirection = FitDirectionType.Horizontal,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 1024
            };

            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            try
            {
                famDoc.ExportImage(options);
            }
            catch
            {
                return false;
            }

            var after = Directory.EnumerateFiles(outDir, "*.png").ToList();
            var created = after.Where(f => !before.Contains(f)).ToList();

            string picked = null;

            if (created.Count > 0)
            {
                picked = created
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();
            }
            else
            {
                picked = after
                    .Where(f => Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();
            }

            if (picked == null || !File.Exists(picked))
                return false;

            MoveOrReplace(picked, targetPng);
            return File.Exists(targetPng);
        }

        private static void MoveOrReplace(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return;

            try
            {
                if (File.Exists(target))
                    File.Delete(target);

                File.Move(source, target);
            }
            catch
            {
                try { File.Copy(source, target, overwrite: true); } catch { }
            }
        }

        // ✅ 1:1 sans couper : on crée un carré et on centre l'image dedans (fond transparent)
        private static void PadPngToSquare(string pngPath)
        {
            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
                return;

            try
            {
                using (var src = Image.FromFile(pngPath))
                {
                    int size = Math.Max(src.Width, src.Height);

                    using (var square = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                    using (var g = Graphics.FromImage(square))
                    {
                        g.Clear(System.Drawing.Color.Transparent);

                        int x = (size - src.Width) / 2;
                        int y = (size - src.Height) / 2;

                        g.DrawImage(src, x, y, src.Width, src.Height);

                        var tmp = pngPath + ".tmp";
                        square.Save(tmp, ImageFormat.Png);

                        // Remplacement safe
                        try { File.Delete(pngPath); } catch { }
                        try { File.Move(tmp, pngPath); } catch { }
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }
                }
            }
            catch
            {
                // best-effort
            }
        }

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

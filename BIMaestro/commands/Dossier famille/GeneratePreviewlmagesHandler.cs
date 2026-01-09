using System;
using System.Collections.Generic;
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
                    PublishProgress(req, done, total, entry.FamilyPath, isCanceled: true);
                    break;
                }

                PublishProgress(req, done, total, entry.FamilyPath, isCanceled: false);

                if (!TryExportFamilyPreview(app, entry, req.TargetRoot, req.LogCallback, out var error))
                {
                    if (!string.IsNullOrWhiteSpace(error))
                        SafeLog(req.LogCallback, $"⚠️ {Path.GetFileName(entry.FamilyPath)} : {error}");
                }

                done++;
                PublishProgress(req, done, total, entry.FamilyPath, isCanceled: false);
            }

            PublishProgress(req, done, total, null, isCompleted: true, isCanceled: req.CancellationToken.IsCancellationRequested);
        }

        public string GetName() => nameof(GeneratePreviewImagesHandler);

        private static void PublishProgress(PreviewGenerationRequest req, int completed, int total, string current,
            bool isCompleted = false, bool isCanceled = false)
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

        private static bool TryExportFamilyPreview(UIApplication uiapp, PreviewEntry entry, string targetRoot, Action<string> log, out string error)
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

            // ✅ Remplace RevitPreviewProvider : on bloque uniquement si le fichier est plus récent que Revit courant.
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
                // Si BasicFileInfo échoue, on n'empêche pas : on laissera OpenDocumentFile décider.
                SafeLog(log, $"ℹ️ Info version ignorée : {ex.Message}");
            }

            Document famDoc = null;
            View3D view = null;

            try
            {
                famDoc = uiapp.Application.OpenDocumentFile(entry.FamilyPath);
                if (famDoc == null || !famDoc.IsFamilyDocument)
                {
                    error = "Fichier non reconnu comme famille.";
                    return false;
                }

                view = CreatePreviewView(famDoc, out var viewError);
                if (view == null)
                {
                    error = viewError ?? "Impossible de créer une vue 3D.";
                    return false;
                }

                ConfigurePreviewOrientation(famDoc, view);
                famDoc.Regenerate();

                var targetPng = GetTargetPath(entry, targetRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPng));

                if (!ExportViewToPng(famDoc, view, targetPng))
                {
                    error = "Export PNG impossible.";
                    return false;
                }

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
                // Nettoyage vue (best effort)
                try
                {
                    if (view != null && famDoc != null && view.Id != ElementId.InvalidElementId)
                    {
                        using (var tx = new Transaction(famDoc, "Nettoyage vue preview"))
                        {
                            if (tx.Start() == TransactionStatus.Started)
                            {
                                try { famDoc.Delete(view.Id); } catch { }
                                tx.Commit();
                            }
                        }
                    }
                }
                catch { }

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

        private static View3D CreatePreviewView(Document famDoc, out string error)
        {
            error = null;

            try
            {
                var viewTypeId = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional)?.Id;

                if (viewTypeId == null || viewTypeId == ElementId.InvalidElementId)
                {
                    error = "Aucun ViewFamilyType 3D disponible.";
                    return null;
                }

                View3D view;

                using (var tx = new Transaction(famDoc, "Préparer la vue 3D"))
                {
                    if (tx.Start() != TransactionStatus.Started)
                    {
                        error = "Transaction refusée.";
                        return null;
                    }

                    view = View3D.CreateIsometric(famDoc, viewTypeId);
                    if (view == null)
                    {
                        error = "Création de vue impossible.";
                        tx.RollBack();
                        return null;
                    }

                    try { view.Name = $"_BIMaestroPreview_{Guid.NewGuid():N}"; } catch { }
                    try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                    try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }

                    tx.Commit();
                }

                return view;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static void ConfigurePreviewOrientation(Document famDoc, View3D view)
        {
            using (var tx = new Transaction(famDoc, "Orientation preview"))
            {
                if (tx.Start() != TransactionStatus.Started)
                    return;

                // ✅ Document n'a pas de bounding box -> on calcule une bbox globale en union d'éléments
                var bbox = GetModelBoundingBox(famDoc);
                if (bbox == null)
                {
                    // fallback propre
                    bbox = new BoundingBoxXYZ
                    {
                        Min = new XYZ(-5, -5, -5),
                        Max = new XYZ(5, 5, 5)
                    };
                }

                var center = (bbox.Min + bbox.Max) * 0.5;
                var extents = (bbox.Max - bbox.Min);
                double radius = Math.Max(Math.Max(extents.X, extents.Y), extents.Z);
                if (radius < 1e-6) radius = 10;

                var offsetDir = new XYZ(-1, -1, 1);
                if (offsetDir.GetLength() < 1e-6) offsetDir = new XYZ(-1, -1, 1);
                offsetDir = offsetDir.Normalize();

                var eye = center + offsetDir.Multiply(radius * 2.5);
                var forward = (center - eye);
                if (forward.GetLength() < 1e-6) forward = offsetDir.Multiply(-1);
                forward = forward.Normalize();

                var orientation = new ViewOrientation3D(eye, XYZ.BasisZ, forward);
                try { view.SetOrientation(orientation); } catch { }
                try { view.SaveOrientationAndLock(); } catch { }

                // ✅ Section box robuste (sans BuiltInParameter douteux)
                double margin = radius * 0.2;
                var section = new BoundingBoxXYZ
                {
                    Min = new XYZ(bbox.Min.X - margin, bbox.Min.Y - margin, bbox.Min.Z - margin),
                    Max = new XYZ(bbox.Max.X + margin, bbox.Max.Y + margin, bbox.Max.Z + margin)
                };

                try { view.SetSectionBox(section); } catch { }

                try
                {
                    // Certaines versions exposent la propriété directement
                    view.IsSectionBoxActive = true;
                }
                catch
                {
                    // Si pas dispo, tant pis : le SetSectionBox suffit souvent
                }

                tx.Commit();
            }
        }

        private static BoundingBoxXYZ GetModelBoundingBox(Document doc)
        {
            BoundingBoxXYZ acc = null;

            // On prend les éléments "réels" (pas les types) et on union leurs bounding boxes 3D.
            var elems = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var e in elems)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { }

                if (bb == null) continue;

                // Ignore les bbox "nulles"
                if (bb.Min == null || bb.Max == null) continue;

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

        private static bool ExportViewToPng(Document famDoc, View3D view, string targetPng)
        {
            var basePath = Path.Combine(Path.GetDirectoryName(targetPng) ?? string.Empty,
                                        Path.GetFileNameWithoutExtension(targetPng));

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

            famDoc.ExportImage(options);

            var expected = $"{basePath} - {view.Name}.png";
            if (File.Exists(expected))
            {
                MoveOrReplace(expected, targetPng);
                return File.Exists(targetPng);
            }

            var dir = Path.GetDirectoryName(basePath) ?? string.Empty;
            if (Directory.Exists(dir))
            {
                var fallback = Directory.EnumerateFiles(dir, Path.GetFileName(basePath) + "*.png")
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();

                if (fallback != null)
                    MoveOrReplace(fallback, targetPng);
            }

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

        private static bool IsSavedInNewerRevitVersion(Autodesk.Revit.ApplicationServices.Application app, string filePath)
        {
            // BasicFileInfo est le moyen "safe" de lire des métadonnées sans ouvrir le fichier.
            var bfi = BasicFileInfo.Extract(filePath);
            if (bfi == null) return false;

            // App.VersionNumber est typiquement "2023", "2024", etc.
            if (!int.TryParse(app.VersionNumber, out int appYear)) return false;

            // Selon versions API, SavedInVersion peut être int ou string -> on gère large.
            int fileYear = 0;

            try
            {
                // souvent: bfi.SavedInVersion (int)
                var prop = bfi.GetType().GetProperty("SavedInVersion");
                if (prop != null)
                {
                    var v = prop.GetValue(bfi, null);
                    if (v is int vi) fileYear = vi;
                    else if (v is string vs && int.TryParse(new string(vs.Where(char.IsDigit).ToArray()), out int parsed)) fileYear = parsed;
                }
            }
            catch { }

            if (fileYear <= 0) return false;

            return fileYear > appYear;
        }
    }
}
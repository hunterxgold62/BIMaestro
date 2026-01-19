using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;

namespace Famille
{
    public enum PreviewOverwriteMode
    {
        AskUser = 0,
        OverwriteAll = 1,
        SkipExisting = 2
    }

    public sealed class PreviewGenerationRequest
    {
        public IReadOnlyList<PreviewEntry> Entries { get; set; } = Array.Empty<PreviewEntry>();
        public string TargetRoot { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Action<PreviewGenerationProgress> ProgressCallback { get; set; }
        public Action<string> LogCallback { get; set; }

        // Overwrite final.png
        public PreviewOverwriteMode OverwriteMode { get; set; } = PreviewOverwriteMode.AskUser;

        // Export Revit "grand"
        public int RevitExportPixelSize { get; set; } = 2000;
        public ImageResolution RevitImageResolution { get; set; } = ImageResolution.DPI_150;

        // Downscale final (traits visuellement plus fins)
        public int FinalSquarePixelSize { get; set; } = 512;

        // Vue
        public bool TryImproveViewIfPossible { get; set; } = true;

        // Nettoyage
        public bool HideAllAnnotationCategories { get; set; } = true;
        public bool HideNoisyCategoriesByNameHeuristic { get; set; } = true;
        public bool HideConnectorsByElement { get; set; } = true;

        // Style
        public bool UseEdges { get; set; } = true;

        // ThinLines (global toggle) : pas toujours respecté par ExportImage
        public bool TryForceThinLinesToggle { get; set; } = false;

        // Post-process : centrer + pad carré (NO SCALE)
        public bool PostProcessToSquareNoScale { get; set; } = true;

        // Fond blanc puis transparent après
        public bool TransparentBackground { get; set; } = false;

        // Crop
        public int BackgroundSampleSize { get; set; } = 24;
        public byte BackgroundTolerance { get; set; } = 22;
        public double CropMarginFactor { get; set; } = 0.04;

        // Fond -> transparent
        public bool MakeBackgroundTransparent { get; set; } = true;
        public byte BackgroundTransparencyTolerance { get; set; } = 5;
        public bool PreserveSemiTransparentPixels { get; set; } = true;

        // =========================
        // NEW : Export A + B + Review
        // =========================

        // Si true : exporte 2 candidats "__A" et "__B" pour chaque famille, puis ouvre la review WPF.
        public bool EnableInteractiveChoice { get; set; } = true;

        // Taille d’un lot (après 20 choix => fenêtre propose Continuer / Arrêter).
        public int ReviewBatchSize { get; set; } = 20;

        // Suffix des candidats dans le même dossier que le final.png
        public string CandidateSuffixA { get; set; } = "__A";
        public string CandidateSuffixB { get; set; } = "__B";

        // Captions UI
        public string CandidateCaptionA { get; set; } = "A – Vue existante (ex: {3D})";
        public string CandidateCaptionB { get; set; } = "B – Vue preview perso (orientation stable)";

        // Orientation : on garde A proche de l’existant, B stable.
        public bool OrientExistingViewA { get; set; } = false;
        public bool OrientCustomViewB { get; set; } = true;
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

        private PreviewOverwriteMode? _resolvedOverwriteMode = null;
        private bool _stopRequested = false;

        public void Execute(UIApplication app)
        {
            var req = Request;
            if (app == null || req == null || req.Entries == null || req.Entries.Count == 0)
                return;

            _stopRequested = false;
            _resolvedOverwriteMode = null;

            if (req.TryForceThinLinesToggle)
                TryToggleThinLines(app, req.LogCallback);

            int total = req.Entries.Count;
            int done = 0;

            int batchSize = Math.Max(1, req.ReviewBatchSize);
            var batchItems = new List<Famille.PreviewReview.ReviewItem>(capacity: batchSize);

            for (int i = 0; i < req.Entries.Count; i++)
            {
                var entry = req.Entries[i];

                if (_stopRequested || req.CancellationToken.IsCancellationRequested)
                {
                    PublishProgress(req, done, total, entry?.FamilyPath, isCanceled: true);
                    break;
                }

                PublishProgress(req, done, total, entry?.FamilyPath);

                if (req.EnableInteractiveChoice)
                {
                    if (TryExportFamilyCandidatesAB(app, entry, req, out var reviewItem, out var error))
                    {
                        if (reviewItem != null)
                            batchItems.Add(reviewItem);
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                            SafeLog(req.LogCallback, $"⚠️ {Path.GetFileName(entry?.FamilyPath ?? "")} : {error}");
                    }
                }
                else
                {
                    // Mode "classique" : seulement final.png via vue existante.
                    if (!TryExportFamilySingle(app, entry, req, out var error))
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                            SafeLog(req.LogCallback, $"⚠️ {Path.GetFileName(entry?.FamilyPath ?? "")} : {error}");
                    }
                }

                done++;
                PublishProgress(req, done, total, entry?.FamilyPath);

                // Si mode review : dès qu’on atteint batchSize, on ouvre la fenêtre, puis on continue ou on stop.
                if (req.EnableInteractiveChoice && batchItems.Count >= batchSize)
                {
                    bool continueNext = Famille.PreviewReview.PreviewReviewSession.RunBatch(app.MainWindowHandle, batchItems);

                    batchItems.Clear();

                    if (!continueNext)
                    {
                        _stopRequested = true;
                        PublishProgress(req, done, total, entry?.FamilyPath, isCanceled: true);
                        break;
                    }
                }
            }

            // Dernier batch restant
            if (req.EnableInteractiveChoice && !_stopRequested && !req.CancellationToken.IsCancellationRequested && batchItems.Count > 0)
            {
                bool continueNext = Famille.PreviewReview.PreviewReviewSession.RunBatch(app.MainWindowHandle, batchItems);
                batchItems.Clear();

                if (!continueNext)
                    _stopRequested = true;
            }

            PublishProgress(req, done, total, null, isCompleted: true, isCanceled: _stopRequested || req.CancellationToken.IsCancellationRequested);
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

        // ==========================================================
        // MODE 1 : export A+B candidats + review
        // ==========================================================

        private bool TryExportFamilyCandidatesAB(
            UIApplication uiapp,
            PreviewEntry entry,
            PreviewGenerationRequest req,
            out Famille.PreviewReview.ReviewItem reviewItem,
            out string error)
        {
            reviewItem = null;
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

            string finalPng = GetTargetPath(entry, req.TargetRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPng) ?? req.TargetRoot);

            // Overwrite / Skip géré sur le final.png
            if (!ShouldWriteTarget(finalPng, entry, req, uiapp))
                return true;

            // check version
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

            string candA = GetCandidatePath(finalPng, req.CandidateSuffixA);
            string candB = GetCandidatePath(finalPng, req.CandidateSuffixB);

            // On réécrit les candidats si existants (intermédiaires)
            TryDeleteFile(candA);
            TryDeleteFile(candB);

            Document famDoc = null;

            try
            {
                famDoc = uiapp.Application.OpenDocumentFile(entry.FamilyPath);
                if (famDoc == null || !famDoc.IsFamilyDocument)
                {
                    error = "Fichier non reconnu comme famille.";
                    return false;
                }

                View3D viewA = GetExisting3DView(famDoc, out var viewErrA);
                if (viewA == null)
                {
                    error = viewErrA ?? "Aucune vue 3D existante (A).";
                    return false;
                }

                View3D viewB = GetOrCreatePreview3DView(famDoc, req.LogCallback, out var viewErrB);
                if (viewB == null)
                {
                    error = viewErrB ?? "Impossible de créer une vue 3D (B).";
                    return false;
                }

                if (req.TryImproveViewIfPossible)
                {
                    // Prépare A
                    PrepareViewForExport(famDoc, viewA, req, req.LogCallback);
                    CleanViewForThumbnailIfPossible(famDoc, viewA, req, req.LogCallback);
                    if (req.OrientExistingViewA)
                        ConfigurePreviewOrientationIfPossible(famDoc, viewA, withSectionBox: false, req.LogCallback);

                    // Prépare B
                    PrepareViewForExport(famDoc, viewB, req, req.LogCallback);
                    CleanViewForThumbnailIfPossible(famDoc, viewB, req, req.LogCallback);
                    if (req.OrientCustomViewB)
                        ConfigurePreviewOrientationIfPossible(famDoc, viewB, withSectionBox: true, req.LogCallback);

                    try { famDoc.Regenerate(); } catch { }
                }

                // Export A
                if (!ExportViewToPngRobust(famDoc, viewA, candA, req.RevitExportPixelSize, req.RevitImageResolution))
                {
                    error = "Export A impossible (aucun fichier créé).";
                    return false;
                }

                PostProcessCandidate(candA, req);

                // Export B
                if (!ExportViewToPngRobust(famDoc, viewB, candB, req.RevitExportPixelSize, req.RevitImageResolution))
                {
                    error = "Export B impossible (aucun fichier créé).";
                    return false;
                }

                PostProcessCandidate(candB, req);

                // Build review item
                reviewItem = new Famille.PreviewReview.ReviewItem
                {
                    FamilyDisplayName = Path.GetFileNameWithoutExtension(finalPng) ?? Path.GetFileName(entry.FamilyPath) ?? "Famille",
                    FinalPath = finalPng,
                    LeftCandidatePath = candA,
                    RightCandidatePath = candB,
                    LeftCaption = req.CandidateCaptionA,
                    RightCaption = req.CandidateCaptionB
                };

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

        private void PostProcessCandidate(string pngPath, PreviewGenerationRequest req)
        {
            // 1) crop/pad square sans scale
            if (req.PostProcessToSquareNoScale)
            {
                CenterContentAndPadToSquare_NoScale_Pixels(
                    pngPath,
                    req.TransparentBackground,
                    req.BackgroundSampleSize,
                    req.BackgroundTolerance,
                    req.CropMarginFactor,
                    req.LogCallback);
            }

            // 2) downscale (image opaque) -> mieux que "pâté"
            if (req.FinalSquarePixelSize > 0)
            {
                DownscaleSquarePng_HighQuality(pngPath, req.FinalSquarePixelSize, req.LogCallback);
            }

            // 3) puis rendre transparent (évite artefacts)
            if (req.MakeBackgroundTransparent)
            {
                ApplyBackgroundTransparency_Pixels(
                    pngPath,
                    req.BackgroundTransparencyTolerance,
                    req.PreserveSemiTransparentPixels,
                    req.LogCallback);
            }
        }

        // ==========================================================
        // MODE 2 : export classique final.png via vue existante
        // ==========================================================

        private bool TryExportFamilySingle(UIApplication uiapp, PreviewEntry entry, PreviewGenerationRequest req, out string error)
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

            string targetPng = GetTargetPath(entry, req.TargetRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPng) ?? req.TargetRoot);

            if (!ShouldWriteTarget(targetPng, entry, req, uiapp))
                return true;

            // check version
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
                    PrepareViewForExport(famDoc, view, req, req.LogCallback);
                    CleanViewForThumbnailIfPossible(famDoc, view, req, req.LogCallback);
                    ConfigurePreviewOrientationIfPossible(famDoc, view, withSectionBox: true, req.LogCallback);
                    try { famDoc.Regenerate(); } catch { }
                }

                if (!ExportViewToPngRobust(famDoc, view, targetPng, req.RevitExportPixelSize, req.RevitImageResolution))
                {
                    error = "Export PNG impossible (aucun fichier créé).";
                    return false;
                }

                // post-process
                if (req.PostProcessToSquareNoScale)
                {
                    CenterContentAndPadToSquare_NoScale_Pixels(
                        targetPng,
                        req.TransparentBackground,
                        req.BackgroundSampleSize,
                        req.BackgroundTolerance,
                        req.CropMarginFactor,
                        req.LogCallback);
                }

                if (req.FinalSquarePixelSize > 0)
                    DownscaleSquarePng_HighQuality(targetPng, req.FinalSquarePixelSize, req.LogCallback);

                if (req.MakeBackgroundTransparent)
                {
                    ApplyBackgroundTransparency_Pixels(
                        targetPng,
                        req.BackgroundTransparencyTolerance,
                        req.PreserveSemiTransparentPixels,
                        req.LogCallback);
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

        // ==========================================================
        // Overwrite logic
        // ==========================================================

        private bool ShouldWriteTarget(string targetPng, PreviewEntry entry, PreviewGenerationRequest req, UIApplication uiapp)
        {
            if (!File.Exists(targetPng))
                return true;

            var mode = _resolvedOverwriteMode ?? req.OverwriteMode;

            if (mode == PreviewOverwriteMode.OverwriteAll)
                return true;

            if (mode == PreviewOverwriteMode.SkipExisting)
            {
                SafeLog(req.LogCallback, $"⏭️ Existe déjà (skip) : {Path.GetFileName(targetPng)}");
                return false;
            }

            if (_resolvedOverwriteMode.HasValue)
                return _resolvedOverwriteMode.Value == PreviewOverwriteMode.OverwriteAll;

            var decision = AskUserOverwriteDecision(uiapp, targetPng, req);
            if (decision == null)
            {
                _stopRequested = true;
                return false;
            }

            _resolvedOverwriteMode = decision.Value;

            if (_resolvedOverwriteMode.Value == PreviewOverwriteMode.SkipExisting)
            {
                SafeLog(req.LogCallback, $"⏭️ Existe déjà (skip) : {Path.GetFileName(targetPng)}");
                return false;
            }

            return _resolvedOverwriteMode.Value == PreviewOverwriteMode.OverwriteAll;
        }

        private PreviewOverwriteMode? AskUserOverwriteDecision(UIApplication uiapp, string targetPng, PreviewGenerationRequest req)
        {
            try
            {
                var td = new TaskDialog("BIMaestro – Export des aperçus")
                {
                    MainInstruction = "Des images d’aperçu existent déjà.",
                    MainContent =
                        $"Exemple : {Path.GetFileName(targetPng)}\n\n" +
                        "Que veux-tu faire pour les fichiers déjà présents ?\n" +
                        "• Écraser : recrée toutes les images.\n" +
                        "• Ignorer : ne touche pas aux images existantes, mais génère celles manquantes.\n" +
                        "• Annuler : stoppe l’export.",
                    CommonButtons = TaskDialogCommonButtons.None,
                    AllowCancellation = true
                };

                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Écraser toutes les images existantes");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Ignorer les images existantes (générer seulement celles manquantes)");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Annuler");

                var res = td.Show();

                if (res == TaskDialogResult.CommandLink1)
                    return PreviewOverwriteMode.OverwriteAll;

                if (res == TaskDialogResult.CommandLink2)
                    return PreviewOverwriteMode.SkipExisting;

                return null;
            }
            catch
            {
                SafeLog(req.LogCallback, "ℹ️ Impossible d’afficher la boîte de dialogue : images existantes ignorées.");
                return PreviewOverwriteMode.SkipExisting;
            }
        }

        // ==========================================================
        // Paths
        // ==========================================================

        private static string GetTargetPath(PreviewEntry entry, string targetRoot)
        {
            var relative = string.IsNullOrWhiteSpace(entry.RelativePath)
                ? (Path.GetFileName(entry.FamilyPath) ?? "preview.rfa")
                : entry.RelativePath;

            var mirrorPath = Path.Combine(targetRoot, relative);
            return Path.ChangeExtension(mirrorPath, ".png");
        }

        private static string GetCandidatePath(string finalPngPath, string suffix)
        {
            var dir = Path.GetDirectoryName(finalPngPath) ?? "";
            var name = Path.GetFileNameWithoutExtension(finalPngPath) ?? "preview";
            return Path.Combine(dir, name + suffix + ".png");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        // ==========================================================
        // Views
        // ==========================================================

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

        private static View3D GetOrCreatePreview3DView(Document famDoc, Action<string> log, out string error)
        {
            error = null;

            // Reuse si existe déjà dans le doc ouvert
            var existing = new FilteredElementCollector(famDoc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v =>
                {
                    try { return !v.IsTemplate && string.Equals(v.Name, "__BIMaestroPreview3D", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });

            if (existing != null)
                return existing;

            View3D created = null;

            try
            {
                using (var tx = new Transaction(famDoc, "Créer vue 3D preview"))
                {
                    if (tx.Start() == TransactionStatus.Started)
                    {
                        var vft = new FilteredElementCollector(famDoc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

                        if (vft != null)
                        {
                            created = View3D.CreateIsometric(famDoc, vft.Id);
                            if (created != null)
                            {
                                try { created.Name = "__BIMaestroPreview3D"; } catch { }
                            }
                        }

                        tx.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Création vue 3D : {ex.Message}");
            }

            if (created != null)
                return created;

            // fallback sur une 3D existante
            return GetExisting3DView(famDoc, out error);
        }

        private static void PrepareViewForExport(Document famDoc, View3D view, PreviewGenerationRequest req, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(famDoc, "Préparer vue export"))
                {
                    if (tx.Start() != TransactionStatus.Started)
                        return;

                    try
                    {
                        view.DisplayStyle = req.UseEdges ? DisplayStyle.ShadingWithEdges : DisplayStyle.Shading;
                    }
                    catch { }

                    try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }

                    // Option “stabilise rendu” (silhouettes/ombres)
                    TryForceGraphicDisplayOptionsForThumbnail(view, log);

                    tx.Commit();
                }
            }
            catch { }
        }

        private static void TryForceGraphicDisplayOptionsForThumbnail(View view, Action<string> log)
        {
            try
            {
                var dm = view.GetViewDisplayModel();
                if (dm == null || !dm.IsValidObject) return;

                // le gros contour “cartoon”
                try { dm.EnableSilhouettes = false; } catch { }

                // anti-aliasing
                try { dm.SmoothEdges = true; } catch { }

                // Revit 2023 : ShowHiddenLines est une enum
                try { dm.ShowHiddenLines = ShowHiddenLinesValues.None; } catch { }

                view.SetViewDisplayModel(dm);
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ ViewDisplayModel : {ex.Message}");
            }
        }

        private static void CleanViewForThumbnailIfPossible(Document doc, View3D view, PreviewGenerationRequest req, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(doc, "Nettoyage vue thumbnail"))
                {
                    if (tx.Start() != TransactionStatus.Started)
                        return;

                    int hiddenCats = 0;

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
                                    hiddenCats++;
                                }
                            }
                            catch { }
                        }
                    }

                    if (req.HideNoisyCategoriesByNameHeuristic)
                    {
                        var tokens = new List<string>
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
                                    hiddenCats++;
                                }
                            }
                            catch { }
                        }
                    }

                    int hiddenElems = 0;
                    if (req.HideConnectorsByElement)
                    {
                        hiddenElems = TryHideElementsByCategoryNameTokens(doc, view, new[]
                        {
                            "connector", "connecteur", "connexion",
                            "élément de connecteur", "element de connecteur"
                        });
                    }

                    tx.Commit();

                    if (hiddenCats > 0)
                        SafeLog(log, $"✅ Nettoyage : {hiddenCats} catégories masquées.");
                    if (hiddenElems > 0)
                        SafeLog(log, $"✅ Connecteurs : {hiddenElems} éléments masqués.");
                }
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Nettoyage : {ex.Message}");
            }
        }

        private static int TryHideElementsByCategoryNameTokens(Document doc, View view, string[] tokens)
        {
            var ids = new List<ElementId>();

            try
            {
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    if (e == null) continue;

                    string catName = null;
                    try { catName = e.Category?.Name; } catch { }

                    if (string.IsNullOrWhiteSpace(catName)) continue;

                    if (!tokens.Any(t => catName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    if (e.Id == view.Id) continue;

                    ids.Add(e.Id);
                }

                if (ids.Count == 0) return 0;

                try
                {
                    view.HideElements(ids);
                    return ids.Count;
                }
                catch
                {
                    int ok = 0;
                    foreach (var id in ids)
                    {
                        try { view.HideElements(new List<ElementId> { id }); ok++; } catch { }
                    }
                    return ok;
                }
            }
            catch
            {
                return 0;
            }
        }

        private static void ConfigurePreviewOrientationIfPossible(Document famDoc, View3D view, bool withSectionBox, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(famDoc, "Orientation preview"))
                {
                    if (tx.Start() != TransactionStatus.Started)
                        return;

                    var bbox = GetModelBoundingBox(famDoc);
                    if (bbox == null)
                    {
                        tx.RollBack();
                        return;
                    }

                    var center = (bbox.Min + bbox.Max) * 0.5;
                    var extents = (bbox.Max - bbox.Min);
                    double radius = Math.Max(Math.Max(Math.Abs(extents.X), Math.Abs(extents.Y)), Math.Abs(extents.Z));
                    if (radius < 1e-6) radius = 10;

                    var offsetDir = new XYZ(-1, -1, 1).Normalize();
                    var eye = center + offsetDir.Multiply(radius * 2.5);
                    var forward = (center - eye);
                    if (forward.GetLength() < 1e-6) forward = offsetDir.Multiply(-1);
                    forward = forward.Normalize();

                    try { view.SetOrientation(new ViewOrientation3D(eye, XYZ.BasisZ, forward)); } catch { }

                    try { view.SaveOrientationAndLock(); } catch { }

                    if (withSectionBox)
                    {
                        try
                        {
                            var padded = ExpandBoundingBox(bbox, 0.35);
                            view.SetSectionBox(padded);
                            view.IsSectionBoxActive = true;
                        }
                        catch { }
                    }

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

        private static BoundingBoxXYZ ExpandBoundingBox(BoundingBoxXYZ bbox, double padFactor)
        {
            if (bbox == null) return null;

            var min = bbox.Min;
            var max = bbox.Max;
            var extents = max - min;

            double padX = Math.Max(Math.Abs(extents.X) * padFactor, 0.1);
            double padY = Math.Max(Math.Abs(extents.Y) * padFactor, 0.1);
            double padZ = Math.Max(Math.Abs(extents.Z) * padFactor, 0.1);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(min.X - padX, min.Y - padY, min.Z - padZ),
                Max = new XYZ(max.X + padX, max.Y + padY, max.Z + padZ)
            };
        }

        // ==========================================================
        // Export
        // ==========================================================

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
        // Pixel-only: centre contenu + pad carré (NO SCALE)
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
                    return false;

                int contentW = maxX - minX + 1;
                int contentH = maxY - minY + 1;
                int margin = (int)(Math.Max(contentW, contentH) * Clamp(marginFactor, 0, 0.30));

                int cropX = Math.Max(0, minX - margin);
                int cropY = Math.Max(0, minY - margin);
                int cropW = Math.Min(w - cropX, contentW + 2 * margin);
                int cropH = Math.Min(h - cropY, contentH + 2 * margin);

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

                int size = Math.Max(cropW, cropH);
                byte[] outPixels = new byte[size * size * 4];

                if (!transparentBackground)
                {
                    for (int i = 0; i < outPixels.Length; i += 4)
                    {
                        outPixels[i + 0] = 255;
                        outPixels[i + 1] = 255;
                        outPixels[i + 2] = 255;
                        outPixels[i + 3] = 255;
                    }
                }

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

                var wb = new WriteableBitmap(size, size,
                    src.DpiX > 0 ? src.DpiX : 96,
                    src.DpiY > 0 ? src.DpiY : 96,
                    PixelFormats.Bgra32, null);

                wb.WritePixels(new Int32Rect(0, 0, size, size), outPixels, outStride, 0);
                SaveBitmapSourceAsPng(wb, pngPath);

                return true;
            }
            catch (Exception ex)
            {
                SafeLog(log, $"⚠️ PostProcess 1:1 échec {Path.GetFileName(pngPath)} : {ex.Message}");
                return false;
            }
        }

        // ==========================================================
        // Downscale "net" (WPF HighQuality)
        // ==========================================================

        private static void DownscaleSquarePng_HighQuality(string pngPath, int targetSize, Action<string> log)
        {
            try
            {
                var src = LoadPngAsBgra32(pngPath);
                if (src == null) return;

                int w = src.PixelWidth;
                int h = src.PixelHeight;

                if (w != h) return;
                if (targetSize <= 0) return;
                if (w <= targetSize) return;

                double dpiX = src.DpiX > 0 ? src.DpiX : 96;
                double dpiY = src.DpiY > 0 ? src.DpiY : 96;

                var dv = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);

                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(src, new Rect(0, 0, targetSize, targetSize));
                }

                var rtb = new RenderTargetBitmap(targetSize, targetSize, dpiX, dpiY, PixelFormats.Pbgra32);
                rtb.Render(dv);

                // Reconvert en BGRA32 (cohérent avec le reste)
                var converted = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
                SaveBitmapSourceAsPng(converted, pngPath);

                SafeLog(log, $"✅ Downscale HQ : {Path.GetFileName(pngPath)} ({w} -> {targetSize})");
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Downscale HQ : {Path.GetFileName(pngPath)} : {ex.Message}");
            }
        }

        // ==========================================================
        // Fond -> transparent (couleur dominante)
        // ==========================================================

        private static void ApplyBackgroundTransparency_Pixels(
            string pngPath,
            byte tolerance,
            bool preserveSemiTransparent,
            Action<string> log)
        {
            try
            {
                var src = LoadPngAsBgra32(pngPath);
                if (src == null) return;

                int w = src.PixelWidth;
                int h = src.PixelHeight;
                int stride = w * 4;

                byte[] px = new byte[h * stride];
                src.CopyPixels(px, stride, 0);

                var bg = EstimateBackgroundByDominantColor(px, w, h, stride);

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

                        if (preserveSemiTransparent && a < 255 && a > alphaBgThreshold)
                            continue;

                        bool nearBg =
                            a <= alphaBgThreshold ||
                            (Math.Abs(r - bg.R) <= tolerance &&
                             Math.Abs(g - bg.G) <= tolerance &&
                             Math.Abs(b - bg.B) <= tolerance);

                        if (nearBg)
                            px[i + 3] = 0;
                    }
                }

                var wb = new WriteableBitmap(w, h,
                    src.DpiX > 0 ? src.DpiX : 96,
                    src.DpiY > 0 ? src.DpiY : 96,
                    PixelFormats.Bgra32, null);

                wb.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
                SaveBitmapSourceAsPng(wb, pngPath);
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Fond transparent : {Path.GetFileName(pngPath)} : {ex.Message}");
            }
        }

        private static Rgba EstimateBackgroundByDominantColor(byte[] px, int w, int h, int stride)
        {
            var dict = new Dictionary<int, int>(capacity: 4096);
            int step = 2;

            for (int y = 0; y < h; y += step)
            {
                int row = y * stride;
                for (int x = 0; x < w; x += step)
                {
                    int i = row + x * 4;
                    byte b = px[i + 0];
                    byte g = px[i + 1];
                    byte r = px[i + 2];
                    byte a = px[i + 3];

                    if (a < 10) continue;

                    int qb = b >> 3;
                    int qg = g >> 3;
                    int qr = r >> 3;

                    int key = (qr << 10) | (qg << 5) | qb;

                    dict.TryGetValue(key, out int c);
                    dict[key] = c + 1;
                }
            }

            if (dict.Count == 0)
                return new Rgba { R = 255, G = 255, B = 255, A = 255 };

            int bestKey = dict.OrderByDescending(kv => kv.Value).First().Key;

            byte R = (byte)(((bestKey >> 10) & 31) * 8 + 4);
            byte G = (byte)(((bestKey >> 5) & 31) * 8 + 4);
            byte B = (byte)((bestKey & 31) * 8 + 4);

            return new Rgba { R = R, G = G, B = B, A = 255 };
        }

        // ==========================================================
        // Helpers pixels
        // ==========================================================

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

                    bool isBg =
                        a <= alphaBgThreshold ||
                        (Math.Abs(r - bg.R) <= tol &&
                         Math.Abs(g - bg.G) <= tol &&
                         Math.Abs(b - bg.B) <= tol);

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

        // ==========================================================
        // Revit version check
        // ==========================================================

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

        private static void TryToggleThinLines(UIApplication uiapp, Action<string> log)
        {
            try
            {
                var cmdId = RevitCommandId.LookupPostableCommandId(PostableCommand.ThinLines);
                if (cmdId != null && uiapp.CanPostCommand(cmdId))
                {
                    uiapp.PostCommand(cmdId);
                    SafeLog(log, "ℹ️ ThinLines: commande envoyée (peut ne pas impacter ExportImage).");
                }
            }
            catch { }
        }
    }
}

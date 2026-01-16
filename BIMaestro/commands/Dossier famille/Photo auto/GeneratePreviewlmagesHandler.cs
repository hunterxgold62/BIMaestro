using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace Famille
{
    // ==========================================================
    // Models
    // ==========================================================

    public enum PreviewOverwriteMode { AskUser = 0, OverwriteAll = 1, SkipExisting = 2 }

    public sealed class PreviewGenerationRequest
    {
        public IReadOnlyList<PreviewEntry> Entries { get; set; } = Array.Empty<PreviewEntry>();
        public string TargetRoot { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Action<PreviewGenerationProgress> ProgressCallback { get; set; }
        public Action<string> LogCallback { get; set; }

        public PreviewOverwriteMode OverwriteMode { get; set; } = PreviewOverwriteMode.AskUser;

        public int RevitExportPixelSize { get; set; } = 2000;
        public ImageResolution RevitImageResolution { get; set; } = ImageResolution.DPI_150;
        public int FinalSquarePixelSize { get; set; } = 512;

        public bool TryImproveViewIfPossible { get; set; } = true;
        public bool HideAllAnnotationCategories { get; set; } = true;
        public bool HideNoisyCategoriesByNameHeuristic { get; set; } = true;
        public bool HideConnectorsByElement { get; set; } = true;

        public bool UseEdges { get; set; } = true;
        public bool TryForceThinLinesToggle { get; set; } = false;

        public bool PostProcessToSquareNoScale { get; set; } = true;
        public bool TransparentBackground { get; set; } = false;
        public bool MakeBackgroundTransparent { get; set; } = true;

        public int BackgroundSampleSize { get; set; } = 24;
        public byte BackgroundTolerance { get; set; } = 22;
        public double CropMarginFactor { get; set; } = 0.04;

        public byte BackgroundTransparencyTolerance { get; set; } = 5;
        public bool PreserveSemiTransparentPixels { get; set; } = true;

        // Review mode
        public bool EnableABReview { get; set; } = true;
        public int ReviewBatchSize { get; set; } = 20;

        public bool OrientExisting3DView { get; set; } = true;
        public double SectionBoxPaddingFactor { get; set; } = 0.35;
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

    public enum ReviewChoice { LeftA, RightB }

    public sealed class ReviewItem
    {
        public string Title { get; set; }
        public string FinalPath { get; set; }
        public string CandidateAPath { get; set; }
        public string CandidateBPath { get; set; }
    }

    public sealed class UndoRecord
    {
        public ReviewItem Item { get; set; }
        public string UndoFolder { get; set; }
        public bool HadFinalBefore { get; set; }
        public string OldFinalBackup { get; set; }
        public string OtherMovedBackup { get; set; }
        public ReviewChoice Choice { get; set; }
    }

    public static class ReviewFileOps
    {
        public static UndoRecord CommitChoice(ReviewItem item, ReviewChoice choice, string undoRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.FinalPath) ?? undoRoot);
            Directory.CreateDirectory(undoRoot);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string baseName = Path.GetFileNameWithoutExtension(item.FinalPath);
            string folder = Path.Combine(undoRoot, baseName + "__" + stamp);
            Directory.CreateDirectory(folder);

            var rec = new UndoRecord
            {
                Item = item,
                UndoFolder = folder,
                HadFinalBefore = File.Exists(item.FinalPath),
                Choice = choice
            };

            if (rec.HadFinalBefore)
            {
                rec.OldFinalBackup = Path.Combine(folder, baseName + "__OLD.png");
                MoveOrReplace(item.FinalPath, rec.OldFinalBackup);
            }

            string chosen = (choice == ReviewChoice.LeftA) ? item.CandidateAPath : item.CandidateBPath;
            string other = (choice == ReviewChoice.LeftA) ? item.CandidateBPath : item.CandidateAPath;

            if (File.Exists(chosen))
                MoveOrReplace(chosen, item.FinalPath);

            if (File.Exists(other))
            {
                rec.OtherMovedBackup = Path.Combine(folder, Path.GetFileName(other));
                MoveOrReplace(other, rec.OtherMovedBackup);
            }

            return rec;
        }

        public static void UndoLast(UndoRecord rec)
        {
            var item = rec.Item;

            if (File.Exists(item.FinalPath))
            {
                string dest = (rec.Choice == ReviewChoice.LeftA) ? item.CandidateAPath : item.CandidateBPath;
                EnsureDir(Path.GetDirectoryName(dest));
                MoveOrReplace(item.FinalPath, dest);
            }

            if (!string.IsNullOrWhiteSpace(rec.OtherMovedBackup) && File.Exists(rec.OtherMovedBackup))
            {
                string otherDest = (rec.Choice == ReviewChoice.LeftA) ? item.CandidateBPath : item.CandidateAPath;
                EnsureDir(Path.GetDirectoryName(otherDest));
                MoveOrReplace(rec.OtherMovedBackup, otherDest);
            }

            if (rec.HadFinalBefore && !string.IsNullOrWhiteSpace(rec.OldFinalBackup) && File.Exists(rec.OldFinalBackup))
            {
                EnsureDir(Path.GetDirectoryName(item.FinalPath));
                MoveOrReplace(rec.OldFinalBackup, item.FinalPath);
            }
            else
            {
                try { if (File.Exists(item.FinalPath)) File.Delete(item.FinalPath); } catch { }
            }

            try { Directory.Delete(rec.UndoFolder, true); } catch { }
        }

        private static void EnsureDir(string dir)
        {
            try { if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
        }

        private static void MoveOrReplace(string source, string target)
        {
            EnsureDir(Path.GetDirectoryName(target));
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(source, target);
            }
            catch
            {
                try { File.Copy(source, target, true); } catch { }
                try { File.Delete(source); } catch { }
            }
        }
    }

    // ==========================================================
    // Handler
    // ==========================================================

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

            int batchSize = Math.Max(1, req.ReviewBatchSize <= 0 ? 20 : req.ReviewBatchSize);

            for (int batchStart = 0; batchStart < req.Entries.Count; batchStart += batchSize)
            {
                if (_stopRequested || req.CancellationToken.IsCancellationRequested)
                    break;

                var batch = req.Entries.Skip(batchStart).Take(batchSize).ToList();

                var reviewItems = new List<ReviewItem>();
                bool batchDidWork = false;

                foreach (var entry in batch)
                {
                    if (_stopRequested || req.CancellationToken.IsCancellationRequested)
                        break;

                    PublishProgress(req, done, total, entry?.FamilyPath);

                    if (!TryExportFamilyCandidates_AB(app, entry, req, out var itemOrNull, out var error))
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                            SafeLog(req.LogCallback, $"⚠️ {Path.GetFileName(entry?.FamilyPath ?? "")} : {error}");
                    }
                    else
                    {
                        if (itemOrNull != null)
                        {
                            bool aOk = File.Exists(itemOrNull.CandidateAPath);
                            bool bOk = File.Exists(itemOrNull.CandidateBPath);

                            if (aOk || bOk)
                            {
                                reviewItems.Add(itemOrNull);
                                batchDidWork = true;
                            }
                        }
                    }

                    done++;
                    PublishProgress(req, done, total, entry?.FamilyPath);
                }

                // Review
                if (req.EnableABReview && reviewItems.Count > 0 && !_stopRequested && !req.CancellationToken.IsCancellationRequested)
                {
                    EnsureWpfApplication();

                    string undoRoot = Path.Combine(req.TargetRoot, "__undo");

                    var win = new ReviewWindow(reviewItems, undoRoot, req.LogCallback);
                    try { new WindowInteropHelper(win).Owner = app.MainWindowHandle; } catch { }

                    bool closedEarly = false;
                    try
                    {
                        win.ShowDialog();
                        closedEarly = win.ClosedEarly;
                    }
                    catch (Exception ex)
                    {
                        SafeLog(req.LogCallback, "❌ ShowDialog review failed: " + ex.Message);
                        // fallback: commit A si dispo sinon B
                        foreach (var it in reviewItems)
                        {
                            try
                            {
                                if (File.Exists(it.CandidateAPath))
                                    ReviewFileOps.CommitChoice(it, ReviewChoice.LeftA, undoRoot);
                                else if (File.Exists(it.CandidateBPath))
                                    ReviewFileOps.CommitChoice(it, ReviewChoice.RightB, undoRoot);
                            }
                            catch { }
                        }
                    }

                    // ✅ Nettoyage demandé : après que le choix est fini, on supprime __undo et __candidates
                    CleanupReviewFolders(req.TargetRoot);

                    if (closedEarly)
                        _stopRequested = true;
                }

                if (_stopRequested || req.CancellationToken.IsCancellationRequested)
                    break;

                // Continuer / Arrêter : seulement si le batch a réellement fait quelque chose
                bool hasMore = (batchStart + batch.Count) < req.Entries.Count;
                if (req.EnableABReview && hasMore && batchDidWork)
                {
                    var td = new TaskDialog("BIMaestro – Export des aperçus")
                    {
                        MainInstruction = "Batch terminé",
                        MainContent = "Souhaites-tu continuer sur le batch suivant ?",
                        CommonButtons = TaskDialogCommonButtons.None,
                        AllowCancellation = true
                    };
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Continuer");
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Arrêter");

                    var r = td.Show();
                    if (r == TaskDialogResult.CommandLink2 || r == TaskDialogResult.Cancel)
                    {
                        _stopRequested = true;
                        break;
                    }
                }
            }

            // cleanup final au cas où
            CleanupReviewFolders(req.TargetRoot);

            PublishProgress(req, done, total, null, isCompleted: true, isCanceled: _stopRequested || req.CancellationToken.IsCancellationRequested);
        }

        public string GetName() => nameof(GeneratePreviewImagesHandler);

        // ==========================================================
        // Export A/B
        // ==========================================================

        private bool TryExportFamilyCandidates_AB(UIApplication uiapp, PreviewEntry entry, PreviewGenerationRequest req,
            out ReviewItem reviewItem, out string error)
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
            EnsureDir(Path.GetDirectoryName(finalPng));

            // Skip si final existe et choix skip
            if (!ShouldWriteTarget(finalPng, entry, req, uiapp))
                return true;

            string candA = GetCandidatePath(entry, req.TargetRoot, "A");
            string candB = GetCandidatePath(entry, req.TargetRoot, "B");
            EnsureDir(Path.GetDirectoryName(candA));
            EnsureDir(Path.GetDirectoryName(candB));

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

                View3D viewA = GetExisting3DView(famDoc);
                View3D viewB = GetOrCreatePreview3DView(famDoc, req.LogCallback);

                bool aOk = false, bOk = false;

                // A
                if (viewA != null)
                {
                    if (req.TryImproveViewIfPossible)
                    {
                        PrepareViewForExport(famDoc, viewA, req, req.LogCallback);
                        CleanViewForThumbnailIfPossible(famDoc, viewA, req, req.LogCallback);
                        if (req.OrientExisting3DView)
                            ConfigurePreviewOrientationIfPossible(famDoc, viewA, req, req.LogCallback);
                        try { famDoc.Regenerate(); } catch { }
                    }

                    if (ExportViewToPngRobust(famDoc, viewA, candA, req.RevitExportPixelSize, req.RevitImageResolution))
                    {
                        PostProcessPipeline(candA, req);
                        aOk = File.Exists(candA);
                    }
                }

                // B
                if (viewB != null)
                {
                    if (req.TryImproveViewIfPossible)
                    {
                        PrepareViewForExport(famDoc, viewB, req, req.LogCallback);
                        CleanViewForThumbnailIfPossible(famDoc, viewB, req, req.LogCallback);
                        ConfigurePreviewOrientationIfPossible(famDoc, viewB, req, req.LogCallback);
                        try { famDoc.Regenerate(); } catch { }
                    }

                    if (ExportViewToPngRobust(famDoc, viewB, candB, req.RevitExportPixelSize, req.RevitImageResolution))
                    {
                        PostProcessPipeline(candB, req);
                        bOk = File.Exists(candB);
                    }
                }

                if (!aOk && !bOk)
                {
                    error = "Export PNG impossible (A et B ont échoué).";
                    return false;
                }

                reviewItem = new ReviewItem
                {
                    Title = Path.GetFileNameWithoutExtension(finalPng),
                    FinalPath = finalPng,
                    CandidateAPath = candA,
                    CandidateBPath = candB
                };

                return true;
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

        private void PostProcessPipeline(string pngPath, PreviewGenerationRequest req)
        {
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

            // downscale AVANT transparence (évite halo)
            if (req.FinalSquarePixelSize > 0)
                DownscaleSquarePng_Lanczos3(pngPath, req.FinalSquarePixelSize, req.LogCallback);

            if (req.MakeBackgroundTransparent)
                ApplyBackgroundTransparency_Pixels(pngPath, req.BackgroundTransparencyTolerance, req.PreserveSemiTransparentPixels, req.LogCallback);
        }

        // ==========================================================
        // Overwrite policy
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

                if (res == TaskDialogResult.CommandLink1) return PreviewOverwriteMode.OverwriteAll;
                if (res == TaskDialogResult.CommandLink2) return PreviewOverwriteMode.SkipExisting;
                return null;
            }
            catch
            {
                SafeLog(req.LogCallback, "ℹ️ Impossible d’afficher la boîte de dialogue : images existantes ignorées.");
                return PreviewOverwriteMode.SkipExisting;
            }
        }

        // ==========================================================
        // Paths (NO Path.GetRelativePath)
        // ==========================================================

        private static string GetTargetPath(PreviewEntry entry, string targetRoot)
        {
            string rel = GetSafeRelative(entry);
            string mirrorPath = Path.Combine(targetRoot, rel);
            return Path.ChangeExtension(mirrorPath, ".png");
        }

        private static string GetCandidatePath(PreviewEntry entry, string targetRoot, string variant)
        {
            string rel = Path.ChangeExtension(GetSafeRelative(entry), ".png");
            string candRoot = Path.Combine(targetRoot, "__candidates", variant);
            return Path.Combine(candRoot, rel);
        }

        private static string GetSafeRelative(PreviewEntry entry)
        {
            string rel = string.IsNullOrWhiteSpace(entry.RelativePath)
                ? (Path.GetFileName(entry.FamilyPath) ?? "preview.rfa")
                : entry.RelativePath;

            rel = rel.Replace('/', '\\').Trim();
            while (rel.StartsWith("\\")) rel = rel.Substring(1);

            if (Path.IsPathRooted(rel) || rel.Contains(":"))
                rel = Path.GetFileName(entry.FamilyPath) ?? "preview.rfa";

            return rel;
        }

        private static void EnsureDir(string dir)
        {
            try { if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
        }

        // ==========================================================
        // Views
        // ==========================================================

        private static View3D GetExisting3DView(Document famDoc)
        {
            var views = new FilteredElementCollector(famDoc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate)
                .ToList();

            if (views.Count == 0) return null;

            var default3d = views.FirstOrDefault(v =>
            {
                try { return string.Equals(v.Name, "{3D}", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });

            return default3d ?? views[0];
        }

        private static View3D GetOrCreatePreview3DView(Document famDoc, Action<string> log)
        {
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

            return created;
        }

        private static void PrepareViewForExport(Document famDoc, View3D view, PreviewGenerationRequest req, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(famDoc, "Préparer vue export"))
                {
                    if (tx.Start() != TransactionStatus.Started) return;

                    try { view.DisplayStyle = req.UseEdges ? DisplayStyle.ShadingWithEdges : DisplayStyle.Shading; } catch { }
                    try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }

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

                try { dm.EnableSilhouettes = false; } catch { }
                try { dm.SmoothEdges = true; } catch { }
                try { dm.ShowHiddenLines = ShowHiddenLinesValues.None; } catch { }

                TrySetProperty(dm, "CastShadows", false);
                TrySetProperty(dm, "AmbientShadows", false);
                TrySetProperty(dm, "ShowShadows", false);
                TrySetProperty(dm, "EnableDepthCueing", false);
                TrySetProperty(dm, "UseDepthCueing", false);

                view.SetViewDisplayModel(dm);
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Options d'affichage : {ex.Message}");
            }
        }

        private static void TrySetProperty(object obj, string propName, object value)
        {
            try
            {
                var p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
                if (p == null || !p.CanWrite) return;

                var t = p.PropertyType;
                if (t == typeof(bool) && value is bool)
                    p.SetValue(obj, value, null);
            }
            catch { }
        }

        private static void CleanViewForThumbnailIfPossible(Document doc, View3D view, PreviewGenerationRequest req, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(doc, "Nettoyage vue thumbnail"))
                {
                    if (tx.Start() != TransactionStatus.Started) return;

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
                                    view.SetCategoryHidden(cat.Id, true);
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
                                    view.SetCategoryHidden(cat.Id, true);
                            }
                            catch { }
                        }
                    }

                    if (req.HideConnectorsByElement)
                    {
                        TryHideElementsByCategoryNameTokens(doc, view, new[]
                        {
                            "connector", "connecteur", "connexion",
                            "élément de connecteur", "element de connecteur"
                        });
                    }

                    tx.Commit();
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

                try { view.HideElements(ids); return ids.Count; }
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
            catch { return 0; }
        }

        private static void ConfigurePreviewOrientationIfPossible(Document famDoc, View3D view, PreviewGenerationRequest req, Action<string> log)
        {
            try
            {
                using (var tx = new Transaction(famDoc, "Orientation preview"))
                {
                    if (tx.Start() != TransactionStatus.Started) return;

                    var bbox = GetModelBoundingBox(famDoc);
                    if (bbox == null) { tx.RollBack(); return; }

                    TryUnlock3DView(view);

                    var center = (bbox.Min + bbox.Max) * 0.5;
                    var ext = (bbox.Max - bbox.Min);

                    double ax = Math.Abs(ext.X), ay = Math.Abs(ext.Y), az = Math.Abs(ext.Z);
                    double radius = Math.Max(Math.Max(ax, ay), az);
                    if (radius < 1e-6) radius = 10;

                    XYZ dir;
                    if (ax >= ay && ax >= az) dir = new XYZ(-0.3, -1.0, 0.9);
                    else if (ay >= ax && ay >= az) dir = new XYZ(-1.0, -0.3, 0.9);
                    else dir = new XYZ(-1.0, -1.0, 0.6);

                    dir = dir.Normalize();

                    var eye = center + dir.Multiply(radius * 2.6);
                    var forward = (center - eye);
                    if (forward.GetLength() < 1e-6) forward = dir.Negate();
                    forward = forward.Normalize();

                    try { view.SetOrientation(new ViewOrientation3D(eye, XYZ.BasisZ, forward)); } catch { }

                    try
                    {
                        var padded = ExpandBoundingBox(bbox, Clamp(req.SectionBoxPaddingFactor, 0.05, 0.80));
                        view.SetSectionBox(padded);
                        view.IsSectionBoxActive = true;
                    }
                    catch { }

                    try { view.SaveOrientationAndLock(); } catch { }

                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Orientation : {ex.Message}");
            }
        }

        private static void TryUnlock3DView(View3D view)
        {
            try
            {
                var isLockedProp = view.GetType().GetProperty("IsLocked", BindingFlags.Instance | BindingFlags.Public);
                if (isLockedProp != null && isLockedProp.PropertyType == typeof(bool))
                {
                    bool locked = (bool)isLockedProp.GetValue(view, null);
                    if (!locked) return;

                    var unlockMethod = view.GetType().GetMethod("Unlock", BindingFlags.Instance | BindingFlags.Public);
                    if (unlockMethod != null) unlockMethod.Invoke(view, null);
                }
            }
            catch { }
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
                    acc = new BoundingBoxXYZ { Min = bb.Min, Max = bb.Max };
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

        private static bool ExportViewToPngRobust(Document famDoc, View3D view, string targetPng, int pixelSize, ImageResolution resolution)
        {
            var outDir = Path.GetDirectoryName(targetPng) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(outDir)) return false;

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
                       .OrderByDescending(File.GetLastWriteTimeUtc)
                       .FirstOrDefault();

            if (picked == null || !File.Exists(picked)) return false;

            MoveOrReplace(picked, targetPng);
            return File.Exists(targetPng);
        }

        private static void MoveOrReplace(string source, string target)
        {
            try
            {
                EnsureDir(Path.GetDirectoryName(target));
                if (File.Exists(target)) File.Delete(target);
                File.Move(source, target);
            }
            catch
            {
                try { File.Copy(source, target, true); } catch { }
            }
        }

        // ==========================================================
        // Pixel post-process (Crop + Lanczos3 + Transparency)
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

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(size, size,
                    src.DpiX > 0 ? src.DpiX : 96,
                    src.DpiY > 0 ? src.DpiY : 96,
                    System.Windows.Media.PixelFormats.Bgra32, null);

                wb.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), outPixels, outStride, 0);
                SaveBitmapSourceAsPng(wb, pngPath);

                return true;
            }
            catch (Exception ex)
            {
                SafeLog(log, $"⚠️ PostProcess échec {Path.GetFileName(pngPath)} : {ex.Message}");
                return false;
            }
        }

        private static void ApplyBackgroundTransparency_Pixels(string pngPath, byte tolerance, bool preserveSemiTransparent, Action<string> log)
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

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(w, h,
                    src.DpiX > 0 ? src.DpiX : 96,
                    src.DpiY > 0 ? src.DpiY : 96,
                    System.Windows.Media.PixelFormats.Bgra32, null);

                wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), px, stride, 0);
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

        // Lanczos3 downscale (net)
        private sealed class Kernel1D { public int[] Idx; public double[] W; }

        private static void DownscaleSquarePng_Lanczos3(string pngPath, int targetSize, Action<string> log)
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

                int srcStride = w * 4;
                byte[] spx = new byte[h * srcStride];
                src.CopyPixels(spx, srcStride, 0);

                int tw = targetSize;
                int th = targetSize;
                int dstStride = tw * 4;
                byte[] dpx = new byte[th * dstStride];

                const double a = 3.0;
                double scale = (double)w / tw;

                var kx = BuildLanczosKernels(tw, w, scale, a);
                var ky = BuildLanczosKernels(th, h, scale, a);

                for (int y = 0; y < th; y++)
                {
                    var kyY = ky[y];
                    int outRow = y * dstStride;

                    for (int x = 0; x < tw; x++)
                    {
                        var kxX = kx[x];

                        double sumA = 0.0;
                        double sumPr = 0.0, sumPg = 0.0, sumPb = 0.0;

                        for (int iy = 0; iy < kyY.Idx.Length; iy++)
                        {
                            int sy = kyY.Idx[iy];
                            double wy = kyY.W[iy];
                            int srcRow = sy * srcStride;

                            for (int ix = 0; ix < kxX.Idx.Length; ix++)
                            {
                                int sx = kxX.Idx[ix];
                                double wxy = wy * kxX.W[ix];
                                int si = srcRow + sx * 4;

                                byte b = spx[si + 0];
                                byte g = spx[si + 1];
                                byte r = spx[si + 2];
                                byte A = spx[si + 3];

                                double a01 = A / 255.0;

                                sumA += a01 * wxy;
                                sumPr += (r * a01) * wxy;
                                sumPg += (g * a01) * wxy;
                                sumPb += (b * a01) * wxy;
                            }
                        }

                        int di = outRow + x * 4;

                        if (sumA > 1e-9)
                        {
                            double invA = 1.0 / sumA;

                            dpx[di + 2] = (byte)ClampByte(sumPr * invA);
                            dpx[di + 1] = (byte)ClampByte(sumPg * invA);
                            dpx[di + 0] = (byte)ClampByte(sumPb * invA);
                            dpx[di + 3] = (byte)ClampByte(sumA * 255.0);
                        }
                        else
                        {
                            dpx[di + 0] = 0;
                            dpx[di + 1] = 0;
                            dpx[di + 2] = 0;
                            dpx[di + 3] = 0;
                        }
                    }
                }

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(tw, th,
                    src.DpiX > 0 ? src.DpiX : 96,
                    src.DpiY > 0 ? src.DpiY : 96,
                    System.Windows.Media.PixelFormats.Bgra32, null);

                wb.WritePixels(new System.Windows.Int32Rect(0, 0, tw, th), dpx, dstStride, 0);
                SaveBitmapSourceAsPng(wb, pngPath);
            }
            catch (Exception ex)
            {
                SafeLog(log, $"ℹ️ Downscale Lanczos3 : {Path.GetFileName(pngPath)} : {ex.Message}");
            }
        }

        private static Kernel1D[] BuildLanczosKernels(int dstSize, int srcSize, double scale, double a)
        {
            var kernels = new Kernel1D[dstSize];
            for (int i = 0; i < dstSize; i++)
            {
                double center = (i + 0.5) * scale - 0.5;

                int left = (int)Math.Floor(center - a + 1);
                int right = (int)Math.Floor(center + a);

                int len = right - left + 1;
                var idx = new int[len];
                var w = new double[len];

                double sum = 0.0;

                for (int k = 0; k < len; k++)
                {
                    int s = left + k;
                    int sc = s < 0 ? 0 : (s >= srcSize ? srcSize - 1 : s);
                    idx[k] = sc;

                    double x = center - s;
                    double wk = Lanczos(x, a);
                    w[k] = wk;
                    sum += wk;
                }

                if (Math.Abs(sum) > 1e-12)
                    for (int k = 0; k < len; k++) w[k] /= sum;

                kernels[i] = new Kernel1D { Idx = idx, W = w };
            }
            return kernels;
        }

        private static double Lanczos(double x, double a)
        {
            double ax = Math.Abs(x);
            if (ax < 1e-12) return 1.0;
            if (ax >= a) return 0.0;
            return Sinc(x) * Sinc(x / a);
        }

        private static double Sinc(double x)
        {
            double pix = Math.PI * x;
            return Math.Sin(pix) / pix;
        }

        private static int ClampByte(double v)
        {
            int iv = (int)Math.Round(v);
            if (iv < 0) return 0;
            if (iv > 255) return 255;
            return iv;
        }

        private static System.Windows.Media.Imaging.BitmapSource LoadPngAsBgra32(string path)
        {
            if (!File.Exists(path)) return null;

            System.Windows.Media.Imaging.BitmapSource frame;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(fs,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                frame = decoder.Frames[0];
            }

            return new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        }

        private static void SaveBitmapSourceAsPng(System.Windows.Media.Imaging.BitmapSource source, string path)
        {
            var tmp = path + ".tmp";
            using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
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
        // Progress / logging
        // ==========================================================

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
        // WPF + Cleanup folders
        // ==========================================================

        private static void EnsureWpfApplication()
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var wpf = new System.Windows.Application();
                    wpf.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }
            }
            catch { }
        }

        private static void CleanupReviewFolders(string targetRoot)
        {
            if (string.IsNullOrWhiteSpace(targetRoot)) return;

            TryDeleteDirectory(Path.Combine(targetRoot, "__undo"));
            TryDeleteDirectory(Path.Combine(targetRoot, "__candidates"));
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { }
        }

        // ==========================================================
        // Revit Version + ThinLines
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

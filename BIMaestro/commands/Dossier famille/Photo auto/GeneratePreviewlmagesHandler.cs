using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;

namespace Famille
{
    public enum PreviewOverwriteMode
    {
        AskUser = 0,
        OverwriteAll = 1,
        SkipExisting = 2
    }

    internal enum VariantChoice
    {
        A = 0, // Vue existante
        B = 1  // Vue perso normalisée
    }

    public sealed class PreviewGenerationRequest
    {
        public IReadOnlyList<PreviewEntry> Entries { get; set; } = Array.Empty<PreviewEntry>();
        public string TargetRoot { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Action<PreviewGenerationProgress> ProgressCallback { get; set; }
        public Action<string> LogCallback { get; set; }

        public PreviewOverwriteMode OverwriteMode { get; set; } = PreviewOverwriteMode.AskUser;

        public int RevitExportPixelSize { get; set; } = 2400;
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

        public int BackgroundSampleSize { get; set; } = 24;
        public byte BackgroundTolerance { get; set; } = 22;
        public double CropMarginFactor { get; set; } = 0.04;

        public bool MakeBackgroundTransparent { get; set; } = true;
        public byte BackgroundTransparencyTolerance { get; set; } = 5;
        public bool PreserveSemiTransparentPixels { get; set; } = true;

        // Compare A/B
        public bool EnableABCompareChoice { get; set; } = true;
        public int ABCompareBatchSize { get; set; } = 10;
        public string ABTempFolderName { get; set; } = "__BIMaestro_TMP_AB";
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

    internal sealed class CompareItem
    {
        public PreviewEntry Entry;
        public string FinalTargetPng;
        public string TempA;
        public string TempB;
        public VariantChoice Choice = VariantChoice.A;
    }

    // ==========================================================
    // WPF : fenêtre de choix A/B (batch)
    // ==========================================================
    internal sealed class CompareChoiceWindow : Window
    {
        private readonly List<CompareItem> _items;
        private readonly Action<string> _log;
        public bool WasCanceled { get; private set; } = false;

        public CompareChoiceWindow(List<CompareItem> items, IntPtr ownerHwnd, Action<string> log)
        {
            _items = items ?? new List<CompareItem>();
            _log = log;

            Title = UiLanguage.T("BIMaestro – Choix des aperçus (A / B)", "BIMaestro – Preview Selection (A / B)");
            Width = 980;
            Height = 720;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));

            Loaded += (_, __) =>
            {
                try
                {
                    var helper = new WindowInteropHelper(this);
                    helper.Owner = ownerHwnd;
                }
                catch { }
            };

            Content = BuildUi();
        }

        private UIElement BuildUi()
        {
            var root = new DockPanel();

            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                Padding = new Thickness(14),
                Child = new TextBlock
                {
                    Text = UiLanguage.T("Pour chaque famille, choisis l’image à conserver : A (vue existante) ou B (vue normalisée).", "For each family, choose the image to keep: A (existing view) or B (normalized view)."),
                    Foreground = Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold
                }
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(12)
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            scroll.Content = stack;

            for (int i = 0; i < _items.Count; i++)
            {
                stack.Children.Add(BuildRow(_items[i], i));
            }

            root.Children.Add(scroll);
            return root;
        }

        private UIElement BuildFooter()
        {
            var panel = new DockPanel
            {
                LastChildFill = false,
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 250)),
            };

            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            DockPanel.SetDock(left, Dock.Left);

            var btnAllA = new Button { Content = UiLanguage.T("Tout en A", "All A"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
            btnAllA.Click += (_, __) => SetAllChoices(VariantChoice.A);

            var btnAllB = new Button { Content = UiLanguage.T("Tout en B", "All B"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
            btnAllB.Click += (_, __) => SetAllChoices(VariantChoice.B);

            left.Children.Add(btnAllA);
            left.Children.Add(btnAllB);
            panel.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(right, Dock.Right);

            var btnCancel = new Button { Content = UiLanguage.T("Annuler", "Cancel"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
            btnCancel.Click += (_, __) =>
            {
                WasCanceled = true;
                try { DialogResult = false; } catch { Close(); }
            };

            var btnOk = new Button
            {
                Content = UiLanguage.T("Valider (garder les choix)", "Confirm (Keep Selections)"),
                Padding = new Thickness(16, 6, 16, 6),
                Background = new SolidColorBrush(Color.FromRgb(40, 120, 255)),
                Foreground = Brushes.White
            };
            btnOk.Click += (_, __) =>
            {
                WasCanceled = false;
                try { DialogResult = true; } catch { Close(); }
            };

            right.Children.Add(btnCancel);
            right.Children.Add(btnOk);
            panel.Children.Add(right);

            // ✅ Padding déplacé ici
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 250)),
                Padding = new Thickness(12),
                Child = panel
            };
        }

        private UIElement BuildRow(CompareItem item, int index)
        {
            string famName = "";
            try { famName = Path.GetFileNameWithoutExtension(item?.Entry?.FamilyPath ?? "") ?? ""; } catch { famName = ""; }
            if (string.IsNullOrWhiteSpace(famName)) famName = item?.Entry?.FamilyPath ?? UiLanguage.T("Famille", "Family");

            var border = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var outer = new StackPanel { Orientation = Orientation.Vertical };
            border.Child = outer;

            outer.Children.Add(new TextBlock
            {
                Text = famName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var panelA = BuildVariantPanel(item, index, VariantChoice.A, UiLanguage.T("A – Vue existante", "A – Existing View"), item.TempA);
            var panelB = BuildVariantPanel(item, index, VariantChoice.B, UiLanguage.T("B – Vue normalisée", "B – Normalized View"), item.TempB);

            Grid.SetColumn(panelA, 0);
            Grid.SetColumn(panelB, 1);
            grid.Children.Add(panelA);
            grid.Children.Add(panelB);

            outer.Children.Add(grid);

            return border;
        }

        private UIElement BuildVariantPanel(CompareItem item, int index, VariantChoice v, string title, string imagePath)
        {
            var wrap = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(235, 235, 235)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(v == VariantChoice.A ? 0 : 8, 0, v == VariantChoice.A ? 8 : 0, 0),
                Padding = new Thickness(8)
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            wrap.Child = stack;

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 60))
            });

            // ✅ DIAG : existence + taille + chemin visible
            string diag = BuildFileDiag(imagePath);
            var diagTxt = new TextBlock
            {
                Text = diag,
                FontSize = 11,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 6),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            diagTxt.ToolTip = imagePath ?? "";
            stack.Children.Add(diagTxt);

            var img = new Image
            {
                Width = 420,
                Height = 320,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };

            string failReason;
            var bmp = TryLoadBitmapRobust(imagePath, decodePixelWidth: 700, _log, out failReason);

            if (bmp != null)
            {
                img.Source = bmp;
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = UiLanguage.T("(Image non chargée)", "(Image not loaded)"),
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = (failReason ?? "") + "\n" + (imagePath ?? "")
                });

                try { _log?.Invoke($"ℹ️ WPF load fail: {failReason} | {imagePath}"); } catch { }
            }

            stack.Children.Add(img);

            var rb = new RadioButton
            {
                Content = (v == VariantChoice.A) ? UiLanguage.T("Choisir A", "Choose A") : UiLanguage.T("Choisir B", "Choose B"),
                GroupName = "choice_" + index,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 13
            };

            rb.IsChecked = (item.Choice == v);
            rb.Checked += (_, __) => item.Choice = v;

            stack.Children.Add(rb);

            return wrap;
        }

        private void SetAllChoices(VariantChoice choice)
        {
            foreach (var it in _items)
                it.Choice = choice;

            try { Content = BuildUi(); } catch { }
        }

        private static string BuildFileDiag(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return "Path: (null)";

                bool exists = File.Exists(path);
                if (!exists)
                    return "EXISTE: NON";

                var fi = new FileInfo(path);
                return $"EXISTE: OUI | Taille: {fi.Length} octets";
            }
            catch
            {
                return "DIAG: erreur";
            }
        }

        // ✅ La méthode importante : on arrête de “cacher” l’erreur.
        private static BitmapSource TryLoadBitmapRobust(string path, int decodePixelWidth, Action<string> log, out string failReason)
        {
            failReason = null;

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    failReason = "Path null/empty";
                    return null;
                }

                if (!File.Exists(path))
                {
                    failReason = "File.Exists = false";
                    return null;
                }

                Exception last = null;

                for (int attempt = 0; attempt < 15; attempt++)
                {
                    try
                    {
                        // IMPORTANT: ReadWrite pour tolérer un lock “soft”
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (fs.Length <= 0)
                                throw new IOException("File length = 0");

                            var decoder = BitmapDecoder.Create(
                                fs,
                                BitmapCreateOptions.PreservePixelFormat,
                                BitmapCacheOption.OnLoad);

                            var frame = decoder.Frames[0];

                            BitmapSource src = frame;

                            // downscale affichage (pas obligatoire, mais évite de charger trop gros)
                            if (decodePixelWidth > 0 && frame.PixelWidth > decodePixelWidth)
                            {
                                double s = (double)decodePixelWidth / frame.PixelWidth;
                                var tb = new TransformedBitmap(frame, new ScaleTransform(s, s));
                                tb.Freeze();
                                src = tb;
                            }

                            // conversion sûre
                            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                            conv.Freeze();
                            return conv;
                        }
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Thread.Sleep(80);
                    }
                }

                // Après retries : on expose l’erreur
                try
                {
                    var fi = new FileInfo(path);
                    failReason = $"{last?.GetType().Name}: {last?.Message} | size={fi.Length}";
                }
                catch
                {
                    failReason = $"{last?.GetType().Name}: {last?.Message}";
                }

                return null;
            }
            catch (Exception ex)
            {
                failReason = $"{ex.GetType().Name}: {ex.Message}";
                try { log?.Invoke($"ℹ️ TryLoadBitmapRobust fatal: {failReason} | {path}"); } catch { }
                return null;
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

        public void Execute(UIApplication uiapp)
        {
            var req = Request;
            if (uiapp == null || req == null || req.Entries == null || req.Entries.Count == 0)
                return;

            _stopRequested = false;
            _resolvedOverwriteMode = null;

            if (req.TryForceThinLinesToggle)
                TryToggleThinLines(uiapp, req.LogCallback);

            int total = req.Entries.Count;
            int done = 0;

            string tempRoot = null;
            var pending = new List<CompareItem>();

            try
            {
                if (req.EnableABCompareChoice)
                {
                    if (string.IsNullOrWhiteSpace(req.TargetRoot))
                        throw new InvalidOperationException("TargetRoot non défini.");

                    tempRoot = Path.Combine(req.TargetRoot, req.ABTempFolderName ?? "__BIMaestro_TMP_AB");
                    TryDeleteDirectory(tempRoot);
                    Directory.CreateDirectory(tempRoot);
                    Directory.CreateDirectory(Path.Combine(tempRoot, "A"));
                    Directory.CreateDirectory(Path.Combine(tempRoot, "B"));
                }

                foreach (var entry in req.Entries)
                {
                    if (_stopRequested || req.CancellationToken.IsCancellationRequested)
                    {
                        PublishProgress(req, done, total, entry?.FamilyPath, isCanceled: true);
                        break;
                    }

                    PublishProgress(req, done, total, entry?.FamilyPath);

                    if (!TryProcessEntry(uiapp, entry, req, tempRoot, out CompareItem compareItem, out string error))
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                            SafeLog(req.LogCallback, $"⚠️ {Path.GetFileName(entry?.FamilyPath ?? "")} : {error}");
                    }
                    else
                    {
                        if (req.EnableABCompareChoice && compareItem != null)
                        {
                            pending.Add(compareItem);

                            int batchSize = Math.Max(1, req.ABCompareBatchSize);
                            if (pending.Count >= batchSize)
                            {
                                if (!ResolvePendingChoices(uiapp, req, pending))
                                {
                                    _stopRequested = true;
                                    PublishProgress(req, done, total, entry?.FamilyPath, isCanceled: true);
                                    break;
                                }
                                pending.Clear();
                            }
                        }
                    }

                    done++;
                    PublishProgress(req, done, total, entry?.FamilyPath);
                }

                if (!_stopRequested && !req.CancellationToken.IsCancellationRequested)
                {
                    if (req.EnableABCompareChoice && pending.Count > 0)
                    {
                        ResolvePendingChoices(uiapp, req, pending);
                        pending.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog(req.LogCallback, $"⚠️ Export interrompu : {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempRoot))
                    TryDeleteDirectory(tempRoot);

                PublishProgress(req, done, total, null, isCompleted: true, isCanceled: _stopRequested || req.CancellationToken.IsCancellationRequested);
            }
        }

        public string GetName() => nameof(GeneratePreviewImagesHandler);

        // ==========================================================
        // Entry processing
        // ==========================================================
        private bool TryProcessEntry(UIApplication uiapp, PreviewEntry entry, PreviewGenerationRequest req, string tempRoot, out CompareItem compareItem, out string error)
        {
            compareItem = null;
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

            var finalTarget = GetTargetPath(entry, req.TargetRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(finalTarget) ?? req.TargetRoot);

            if (!ShouldWriteTarget(finalTarget, entry, req, uiapp))
                return true;

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

                if (!req.EnableABCompareChoice)
                {
                    var viewA = GetExisting3DView(famDoc, out var vErrA);
                    if (viewA == null)
                    {
                        error = vErrA ?? "Aucune vue 3D exportable.";
                        return false;
                    }

                    if (req.TryImproveViewIfPossible)
                    {
                        PrepareViewForExport(famDoc, viewA, req, forceDisplayOptions: false, req.LogCallback);
                        CleanViewForThumbnailIfPossible(famDoc, viewA, req, req.LogCallback);
                        ConfigurePreviewOrientation(famDoc, viewA, useSectionBox: false, sectionPadFactor: 0.0, req.LogCallback);
                        try { famDoc.Regenerate(); } catch { }
                    }

                    if (!ExportViewToPngRobust(famDoc, viewA, finalTarget, req.RevitExportPixelSize, req.RevitImageResolution, req.LogCallback))
                    {
                        error = "Export PNG impossible (aucun fichier créé).";
                        return false;
                    }

                    PostProcessPipeline(finalTarget, req);
                    WaitForFileReady(finalTarget, 4000);
                    return true;
                }

                string rel = GetRelativePathPortable(req.TargetRoot, finalTarget);
                string outA = Path.Combine(tempRoot, "A", rel);
                string outB = Path.Combine(tempRoot, "B", rel);

                Directory.CreateDirectory(Path.GetDirectoryName(outA) ?? Path.Combine(tempRoot, "A"));
                Directory.CreateDirectory(Path.GetDirectoryName(outB) ?? Path.Combine(tempRoot, "B"));

                var viewExisting = GetExisting3DView(famDoc, out var viewErrA2);
                if (viewExisting == null)
                {
                    error = viewErrA2 ?? "Aucune vue 3D existante exportable (A).";
                    return false;
                }

                var viewCustom = GetOrCreatePreview3DView(famDoc, req.LogCallback, out var viewErrB2);
                if (viewCustom == null)
                {
                    error = viewErrB2 ?? "Impossible de créer/obtenir une vue 3D (B).";
                    return false;
                }

                if (req.TryImproveViewIfPossible)
                {
                    PrepareViewForExport(famDoc, viewExisting, req, forceDisplayOptions: false, req.LogCallback);
                    CleanViewForThumbnailIfPossible(famDoc, viewExisting, req, req.LogCallback);
                    ConfigurePreviewOrientation(famDoc, viewExisting, useSectionBox: false, sectionPadFactor: 0.0, req.LogCallback);

                    PrepareViewForExport(famDoc, viewCustom, req, forceDisplayOptions: true, req.LogCallback);
                    CleanViewForThumbnailIfPossible(famDoc, viewCustom, req, req.LogCallback);
                    ConfigurePreviewOrientation(famDoc, viewCustom, useSectionBox: true, sectionPadFactor: 0.35, req.LogCallback);

                    try { famDoc.Regenerate(); } catch { }
                }

                if (!ExportViewToPngRobust(famDoc, viewExisting, outA, req.RevitExportPixelSize, req.RevitImageResolution, req.LogCallback))
                {
                    error = "Export A impossible.";
                    return false;
                }
                PostProcessPipeline(outA, req);
                WaitForFileReady(outA, 5000);

                if (!ExportViewToPngRobust(famDoc, viewCustom, outB, req.RevitExportPixelSize, req.RevitImageResolution, req.LogCallback))
                {
                    error = "Export B impossible.";
                    return false;
                }
                PostProcessPipeline(outB, req);
                WaitForFileReady(outB, 5000);

                compareItem = new CompareItem
                {
                    Entry = entry,
                    FinalTargetPng = finalTarget,
                    TempA = outA,
                    TempB = outB,
                    Choice = VariantChoice.A
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

        // ==========================================================
        // Resolve choices
        // ==========================================================
        private bool ResolvePendingChoices(UIApplication uiapp, PreviewGenerationRequest req, List<CompareItem> pending)
        {
            try
            {
                if (pending == null || pending.Count == 0) return true;

                // ✅ double sécurité : s'assurer que les fichiers existent et sont lisibles AVANT d'afficher
                foreach (var it in pending)
                {
                    WaitForFileReady(it.TempA, 4000);
                    WaitForFileReady(it.TempB, 4000);
                }

                var wnd = new CompareChoiceWindow(pending, uiapp.MainWindowHandle, req.LogCallback);
                bool? ok = null;
                try { ok = wnd.ShowDialog(); } catch { ok = false; }

                if (ok != true || wnd.WasCanceled)
                {
                    SafeLog(req.LogCallback, "⛔ Choix annulé : arrêt de l’export.");
                    return false;
                }

                foreach (var it in pending)
                {
                    try
                    {
                        string chosen = (it.Choice == VariantChoice.A) ? it.TempA : it.TempB;

                        Directory.CreateDirectory(Path.GetDirectoryName(it.FinalTargetPng) ?? req.TargetRoot);
                        MoveOrReplace(chosen, it.FinalTargetPng);
                        WaitForFileReady(it.FinalTargetPng, 5000);

                        TryDeleteFile(it.TempA);
                        TryDeleteFile(it.TempB);

                        SafeLog(req.LogCallback, $"✅ Choix {(it.Choice == VariantChoice.A ? "A" : "B")} : {Path.GetFileName(it.FinalTargetPng)}");
                    }
                    catch (Exception ex)
                    {
                        SafeLog(req.LogCallback, $"⚠️ Choix non appliqué : {ex.Message}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                SafeLog(req.LogCallback, $"⚠️ Fenêtre de choix : {ex.Message}");
                return false;
            }
        }

        // ==========================================================
        // Post-process pipeline
        // ==========================================================
        private static void PostProcessPipeline(string pngPath, PreviewGenerationRequest req)
        {
            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath)) return;

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

            if (req.FinalSquarePixelSize > 0)
            {
                DownscaleSquarePng_Lanczos3(pngPath, req.FinalSquarePixelSize, req.LogCallback);
            }

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

            var existing = new FilteredElementCollector(famDoc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v =>
                {
                    try { return !v.IsTemplate && string.Equals(v.Name, "__BIMaestroPreview3D", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });

            if (existing != null) return existing;

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

            if (created != null) return created;

            var any = new FilteredElementCollector(famDoc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);

            if (any != null) return any;

            error = "Aucune vue 3D disponible.";
            return null;
        }

        // ==========================================================
        // Prepare view
        // ==========================================================
        private static void PrepareViewForExport(Document famDoc, View3D view, PreviewGenerationRequest req, bool forceDisplayOptions, Action<string> log)
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

                    if (forceDisplayOptions)
                    {
                        TryForceGraphicDisplayOptionsForThumbnail(view, log);
                    }

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

                TrySetProperty(dm, "EnableSilhouettes", false);
                TrySetProperty(dm, "SmoothEdges", true);

                // ✅ Revit 2023 : enum (plus bool)
                TrySetProperty(dm, "ShowHiddenLines", ShowHiddenLinesValues.None);

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

                if (t.IsEnum)
                {
                    object vEnum = value;
                    if (value != null && value.GetType() != t)
                        vEnum = Enum.Parse(t, value.ToString(), ignoreCase: true);

                    p.SetValue(obj, vEnum, null);
                    return;
                }

                if (t == typeof(bool) && value is bool)
                {
                    p.SetValue(obj, value, null);
                    return;
                }

                if (t == typeof(int) && value is int)
                {
                    p.SetValue(obj, value, null);
                    return;
                }

                if (t == typeof(double) && value is double)
                {
                    p.SetValue(obj, value, null);
                    return;
                }
            }
            catch { }
        }

        // ==========================================================
        // Clean view
        // ==========================================================
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

        // ==========================================================
        // Orientation
        // ==========================================================
        private static void ConfigurePreviewOrientation(Document famDoc, View3D view, bool useSectionBox, double sectionPadFactor, Action<string> log)
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

                    if (useSectionBox)
                    {
                        try
                        {
                            var padded = ExpandBoundingBox(bbox, Math.Max(0.10, sectionPadFactor));
                            view.SetSectionBox(padded);
                            view.IsSectionBoxActive = true;
                        }
                        catch { }
                    }

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
                var isLockedProp = view.GetType().GetProperty("IsLocked");
                if (isLockedProp == null || isLockedProp.PropertyType != typeof(bool)) return;

                bool locked = (bool)isLockedProp.GetValue(view, null);
                if (!locked) return;

                var unlock = view.GetType().GetMethod("Unlock", BindingFlags.Instance | BindingFlags.Public);
                if (unlock != null) unlock.Invoke(view, null);
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
            var ext = max - min;

            double padX = Math.Max(Math.Abs(ext.X) * padFactor, 0.1);
            double padY = Math.Max(Math.Abs(ext.Y) * padFactor, 0.1);
            double padZ = Math.Max(Math.Abs(ext.Z) * padFactor, 0.1);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(min.X - padX, min.Y - padY, min.Z - padZ),
                Max = new XYZ(max.X + padX, max.Y + padY, max.Z + padZ)
            };
        }

        // ==========================================================
        // Export image (✅ attente de création + attente de lisibilité)
        // ==========================================================
        private static bool ExportViewToPngRobust(Document famDoc, View3D view, string targetPng, int pixelSize, ImageResolution resolution, Action<string> log)
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

            // ✅ Revit peut écrire avec un léger délai -> on attend que quelque chose apparaisse
            string picked = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(3000);

            while (DateTime.UtcNow < deadline)
            {
                var after = Directory.EnumerateFiles(outDir, "*.png").ToList();
                var created = after.Where(f => !before.Contains(f)).ToList();

                picked = created.Count > 0
                    ? created.OrderByDescending(File.GetCreationTimeUtc).FirstOrDefault()
                    : after.Where(f => Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(File.GetCreationTimeUtc)
                           .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(picked) && File.Exists(picked))
                    break;

                Thread.Sleep(80);
            }

            if (picked == null || !File.Exists(picked))
                return false;

            MoveOrReplace(picked, targetPng);

            // ✅ attendre que le fichier final soit lisible (évite "Image manquante")
            WaitForFileReady(targetPng, 4000);

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
        // Wait helpers (file ready)
        // ==========================================================
        private static void WaitForFileReady(string path, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        Thread.Sleep(60);
                        continue;
                    }

                    var fi = new FileInfo(path);
                    if (fi.Length <= 0)
                    {
                        Thread.Sleep(60);
                        continue;
                    }

                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length > 0) return;
                    }
                }
                catch
                {
                    Thread.Sleep(80);
                }
            }
        }

        // ==========================================================
        // Overwrite decision
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
                var td = new TaskDialog(UiLanguage.T("BIMaestro – Export des aperçus", "BIMaestro – Preview Export"))
                {
                    MainInstruction = UiLanguage.T("Des images d’aperçu existent déjà.", "Preview images already exist."),
                    MainContent = UiLanguage.T(
                        $"Exemple : {Path.GetFileName(targetPng)}\n\nQue veux-tu faire pour les fichiers déjà présents ?\n• Écraser : recrée toutes les images.\n• Ignorer : ne touche pas aux images existantes, mais génère celles manquantes.\n• Annuler : stoppe l’export.",
                        $"Example: {Path.GetFileName(targetPng)}\n\nWhat would you like to do with existing files?\n• Overwrite: recreate all images.\n• Skip: keep existing images and generate only missing ones.\n• Cancel: stop the export."),
                    CommonButtons = TaskDialogCommonButtons.None,
                    AllowCancellation = true
                };

                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, UiLanguage.T("Écraser toutes les images existantes", "Overwrite All Existing Images"));
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, UiLanguage.T("Ignorer les images existantes (générer seulement celles manquantes)", "Skip Existing Images (Generate Missing Images Only)"));
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, UiLanguage.T("Annuler", "Cancel"));

                var res = td.Show();

                if (res == TaskDialogResult.CommandLink1)
                    return PreviewOverwriteMode.OverwriteAll;

                if (res == TaskDialogResult.CommandLink2)
                    return PreviewOverwriteMode.SkipExisting;

                return null;
            }
            catch
            {
                SafeLog(req.LogCallback, UiLanguage.T("ℹ️ Impossible d’afficher la boîte de dialogue : images existantes ignorées.", "ℹ️ Unable to display the dialog: existing images were skipped."));
                return PreviewOverwriteMode.SkipExisting;
            }
        }

        // ==========================================================
        // Paths
        // ==========================================================
        private static string GetTargetPath(PreviewEntry entry, string targetRoot)
        {
            var relative = string.IsNullOrWhiteSpace(entry.RelativePath)
                ? Path.GetFileName(entry.FamilyPath) ?? "preview.png"
                : entry.RelativePath;

            var mirrorPath = Path.Combine(targetRoot, relative);
            return Path.ChangeExtension(mirrorPath, ".png");
        }

        private static string GetRelativePathPortable(string baseDir, string fullPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(fullPath))
                    return Path.GetFileName(fullPath);

                if (!baseDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    baseDir += Path.DirectorySeparatorChar;

                var baseUri = new Uri(baseDir);
                var fullUri = new Uri(fullPath);

                var relUri = baseUri.MakeRelativeUri(fullUri);
                var rel = Uri.UnescapeDataString(relUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
                return rel;
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }

        // ==========================================================
        // Progress & logs
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
        // Pixels / processing
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
        // Downscale Lanczos3 (net)
        // ==========================================================
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

                Kernel1D[] kx = BuildLanczosKernels(tw, w, scale, a);
                Kernel1D[] ky = BuildLanczosKernels(th, h, scale, a);

                for (int y = 0; y < th; y++)
                {
                    var kyY = ky[y];
                    int outRow = y * dstStride;

                    for (int x = 0; x < tw; x++)
                    {
                        var kxX = kx[x];

                        double sumA = 0.0;
                        double sumPr = 0.0;
                        double sumPg = 0.0;
                        double sumPb = 0.0;

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

                var wb = new WriteableBitmap(tw, th,
                    src.DpiX > 0 ? src.DpiX : 96,
                    src.DpiY > 0 ? src.DpiY : 96,
                    PixelFormats.Bgra32, null);

                wb.WritePixels(new Int32Rect(0, 0, tw, th), dpx, dstStride, 0);
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
                {
                    for (int k = 0; k < len; k++)
                        w[k] /= sum;
                }

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

        // ==========================================================
        // Bitmap IO
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
            using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(outFs);
            }

            try { File.Delete(path); } catch { }
            try { File.Move(tmp, path); } catch { }
        }

        // ==========================================================
        // Version check
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

        // ==========================================================
        // ThinLines
        // ==========================================================
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

        // ==========================================================
        // FS helpers
        // ==========================================================
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        Directory.Delete(path, recursive: true);
                        return;
                    }
                    catch
                    {
                        Thread.Sleep(80);
                    }
                }
            }
            catch { }
        }
    }
}

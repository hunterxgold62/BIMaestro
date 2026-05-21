using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;

namespace IA
{
    [Transaction(TransactionMode.Manual)]
    public class RealisticViewImageCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "RealisticViewImageCommand";

        private const int InputMaxEdge = 1536;
        private const int OutputMaxEdge = 1536;
        private const int OutputMultiple = 16;
        private const int OutputMinPixels = 655360;
        private const string ImageQuality = "low";

        private enum RenderMode
        {
            Auto,
            Fidele,
            Presentation,
            Ambiance
        }

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;

            if (doc == null)
                return Result.Cancelled;

            var candidateViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(IsEligibleView)
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name)
                .ToList();

            if (!candidateViews.Any())
            {
                TaskDialog.Show("IA Image", "Aucune vue Plan/Coupe/3D exploitable trouvée.");
                return Result.Cancelled;
            }

            var picker = new ViewPickerWindow(candidateViews);
            var handle = data.Application.MainWindowHandle;

            if (handle != IntPtr.Zero)
                new WindowInteropHelper(picker).Owner = handle;

            if (picker.ShowDialog() != true || picker.SelectedView == null)
                return Result.Cancelled;

            string jwt = BIMaestroApp.LicenseJwt;

            if (string.IsNullOrWhiteSpace(jwt))
            {
                TaskDialog.Show("IA Image", "Licence/JWT introuvable.");
                return Result.Failed;
            }

            string exportedPath = ExportViewAsPng(doc, picker.SelectedView.Id);

            if (string.IsNullOrWhiteSpace(exportedPath) || !File.Exists(exportedPath))
            {
                TaskDialog.Show("IA Image", "Impossible d'exporter la vue sélectionnée.");
                return Result.Failed;
            }

            try
            {
                byte[] normalizedBytes = NormalizeImageToMaxEdge(exportedPath, InputMaxEdge);
                string b64Input = Convert.ToBase64String(normalizedBytes);

                string outputSize = GetCostAwareOutputSizeFromImageBytes(normalizedBytes);

                RenderMode effectiveMode = ResolveEffectiveRenderMode(
                    picker.SelectedRenderMode,
                    picker.SelectedView
                );

                string prompt = BuildPrompt(effectiveMode);

                HistoryContext history = CreateHistoryContext(doc, picker.SelectedView);

                TryWriteAllBytes(history.SourcePath, normalizedBytes);

                TryWriteAllText(
                    history.PromptPath,
                    BuildPromptDebugText(
                        prompt,
                        picker.SelectedRenderMode,
                        effectiveMode,
                        outputSize,
                        exportedPath,
                        history.FolderPath,
                        picker.SelectedView
                    )
                );

                WriteDebugFile(
                    "last_image_size.txt",
                    "Taille demandée à OpenAI : " + outputSize +
                    Environment.NewLine +
                    "Vue Revit exportée : " + exportedPath +
                    Environment.NewLine +
                    "Entrée max edge : " + InputMaxEdge +
                    Environment.NewLine +
                    "Sortie max edge : " + OutputMaxEdge +
                    Environment.NewLine +
                    "Qualité : " + ImageQuality +
                    Environment.NewLine +
                    "Mode demandé : " + GetModeLabel(picker.SelectedRenderMode) +
                    Environment.NewLine +
                    "Mode appliqué : " + GetModeLabel(effectiveMode)
                );

                JObject response = SendImageRequestWithFallback(jwt, b64Input, outputSize, prompt);

                TrackImageTokenUsage(response, outputSize, effectiveMode, picker.SelectedView);

                string resultB64 = ExtractImageBase64(response);

                if (string.IsNullOrWhiteSpace(resultB64))
                    throw new InvalidOperationException("Réponse IA sans image exploitable.");

                byte[] outBytes = Convert.FromBase64String(resultB64);

                TryWriteAllBytes(history.ResultPath, outBytes);
                TryWriteAllText(history.ResponsePath, response.ToString());

                string outputPixelInfo = GetPixelSizeTextFromImageBytes(outBytes);
                string inputPixelInfo = GetPixelSizeTextFromImageBytes(normalizedBytes);

                string suggestedFileName = $"{SanitizeFileName(picker.SelectedView.Name)}_realiste.png";

                var preview = new ImageResultPreviewWindow(
                    normalizedBytes,
                    outBytes,
                    suggestedFileName,
                    inputPixelInfo,
                    outputPixelInfo,
                    history.FolderPath
                );

                if (handle != IntPtr.Zero)
                    new WindowInteropHelper(preview).Owner = handle;

                preview.ShowDialog();
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    "IA Image",
                    "Erreur lors de la génération IA : " + ex.Message +
                    "\n\nConseil : réessayez dans quelques secondes. " +
                    "Si l'erreur 500 persiste, le service OpenAI/proxy est temporairement indisponible."
                );

                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private static bool IsEligibleView(View v)
        {
            if (v == null || v.IsTemplate)
                return false;

            return v.ViewType == ViewType.FloorPlan
                || v.ViewType == ViewType.CeilingPlan
                || v.ViewType == ViewType.EngineeringPlan
                || v.ViewType == ViewType.Section
                || v.ViewType == ViewType.ThreeD;
        }

        private static RenderMode ResolveEffectiveRenderMode(RenderMode requestedMode, View view)
        {
            if (requestedMode != RenderMode.Auto)
                return requestedMode;

            if (view != null && view.ViewType == ViewType.ThreeD)
                return RenderMode.Presentation;

            return RenderMode.Fidele;
        }

        private static string ExportViewAsPng(Document doc, ElementId viewId)
        {
            string folder = Path.Combine(Path.GetTempPath(), "BIMaestro", "IAImage");
            Directory.CreateDirectory(folder);

            foreach (string oldFile in Directory.GetFiles(folder, "*.png"))
                TryDeleteFile(oldFile);

            string baseName = "revit_view_export";

            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(folder, baseName),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = InputMaxEdge,
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_150
            };

            opts.SetViewsAndSheets(new List<ElementId> { viewId });
            doc.ExportImage(opts);

            return Directory.GetFiles(folder, "*.png")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static byte[] NormalizeImageToMaxEdge(string imagePath, int maxEdge)
        {
            var src = new BitmapImage();

            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.UriSource = new Uri(imagePath);
            src.EndInit();
            src.Freeze();

            int width = src.PixelWidth;
            int height = src.PixelHeight;

            if (width <= 0 || height <= 0)
                return File.ReadAllBytes(imagePath);

            double ratio = Math.Min((double)maxEdge / width, (double)maxEdge / height);

            if (ratio >= 1.0)
                return File.ReadAllBytes(imagePath);

            int targetW = Math.Max(1, (int)Math.Round(width * ratio));
            int targetH = Math.Max(1, (int)Math.Round(height * ratio));

            var visual = new DrawingVisual();

            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(src, new Rect(0, 0, targetW, targetH));
            }

            var bmp = new RenderTargetBitmap(
                targetW,
                targetH,
                96,
                96,
                PixelFormats.Pbgra32
            );

            bmp.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        private static string GetCostAwareOutputSizeFromImageBytes(byte[] imageBytes)
        {
            int sourceW;
            int sourceH;

            if (!TryReadPixelSize(imageBytes, out sourceW, out sourceH))
                return "1536x1536";

            if (sourceW <= 0 || sourceH <= 0)
                return "1536x1536";

            double scale = 1.0;
            int sourceMaxEdge = Math.Max(sourceW, sourceH);

            if (sourceMaxEdge > OutputMaxEdge)
                scale = (double)OutputMaxEdge / sourceMaxEdge;

            double scaledW = sourceW * scale;
            double scaledH = sourceH * scale;

            double currentPixels = scaledW * scaledH;

            if (currentPixels < OutputMinPixels)
            {
                double scaleUp = Math.Sqrt((double)OutputMinPixels / currentPixels);

                scaledW *= scaleUp;
                scaledH *= scaleUp;

                double newMax = Math.Max(scaledW, scaledH);

                if (newMax > OutputMaxEdge)
                {
                    double capScale = (double)OutputMaxEdge / newMax;
                    scaledW *= capScale;
                    scaledH *= capScale;
                }
            }

            int targetW = RoundToMultiple(scaledW, OutputMultiple);
            int targetH = RoundToMultiple(scaledH, OutputMultiple);

            targetW = ClampToMultiple(targetW, OutputMultiple, OutputMaxEdge);
            targetH = ClampToMultiple(targetH, OutputMultiple, OutputMaxEdge);

            EnsureImageApiConstraints(ref targetW, ref targetH);

            return targetW + "x" + targetH;
        }

        private static void EnsureImageApiConstraints(ref int width, ref int height)
        {
            width = ClampToMultiple(width, OutputMultiple, OutputMaxEdge);
            height = ClampToMultiple(height, OutputMultiple, OutputMaxEdge);

            int guard = 0;

            while (guard < 300)
            {
                guard++;

                int longEdge = Math.Max(width, height);
                int shortEdge = Math.Min(width, height);

                bool ratioTooHigh = shortEdge > 0 && ((double)longEdge / shortEdge) > 3.0;
                bool notEnoughPixels = width * height < OutputMinPixels;

                if (!ratioTooHigh && !notEnoughPixels)
                    break;

                if (ratioTooHigh)
                {
                    if (width >= height)
                        height = Math.Min(OutputMaxEdge, height + OutputMultiple);
                    else
                        width = Math.Min(OutputMaxEdge, width + OutputMultiple);
                }
                else if (notEnoughPixels)
                {
                    if (width <= height && width < OutputMaxEdge)
                        width = Math.Min(OutputMaxEdge, width + OutputMultiple);
                    else if (height < OutputMaxEdge)
                        height = Math.Min(OutputMaxEdge, height + OutputMultiple);
                    else
                        break;
                }
            }

            if (width * height < OutputMinPixels)
            {
                width = 1536;
                height = 1536;
            }
        }

        private static int RoundToMultiple(double value, int multiple)
        {
            if (multiple <= 0)
                return (int)Math.Round(value);

            int rounded = (int)Math.Round(value / multiple) * multiple;

            if (rounded < multiple)
                rounded = multiple;

            return rounded;
        }

        private static int ClampToMultiple(int value, int multiple, int max)
        {
            if (value < multiple)
                value = multiple;

            if (value > max)
                value = max;

            int remainder = value % multiple;

            if (remainder != 0)
                value = value - remainder;

            if (value < multiple)
                value = multiple;

            return value;
        }

        private static bool TryReadPixelSize(byte[] imageBytes, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                using (var ms = new MemoryStream(imageBytes))
                {
                    var decoder = BitmapDecoder.Create(
                        ms,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad
                    );

                    var frame = decoder.Frames.FirstOrDefault();

                    if (frame == null)
                        return false;

                    width = frame.PixelWidth;
                    height = frame.PixelHeight;

                    return width > 0 && height > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetPixelSizeTextFromImageBytes(byte[] imageBytes)
        {
            int width;
            int height;

            if (TryReadPixelSize(imageBytes, out width, out height))
                return width + " x " + height + " px";

            return "Dimensions inconnues";
        }

        private static string BuildPrompt(RenderMode mode)
        {
            string basePrompt =
                "Transforme cette vue Revit en rendu réaliste. " +
                "Respecte strictement la géométrie, le cadrage, l'angle de caméra, les proportions, les volumes et tous les éléments présents. " +
                "Ne rajoute aucun objet, aucun bâtiment, aucune personne, aucun véhicule et ne déplace aucun élément. " +
                "Ne supprime rien et ne modifie pas la conception. " +
                "Améliore uniquement le rendu visuel, les matériaux, la lumière, les ombres, les reflets, les couleurs et l'ambiance générale. ";

            string modePrompt;

            if (mode == RenderMode.Fidele)
            {
                modePrompt =
                    "Style recherché : rendu fidèle, sobre, propre et technique. " +
                    "Le résultat doit rester très proche de la vue Revit d'origine, avec un réalisme amélioré mais sans effet trop artistique. " +
                    "Conserve une lecture claire des contours, des volumes, des réseaux, des plans, des coupes et des éléments techniques. " +
                    "Évite fortement toute interprétation créative, tout embellissement excessif et toute transformation en image trop architecturale. " +
                    "L'image doit être lisible, crédible et adaptée à un usage bureau d'études ou validation interne. ";
            }
            else if (mode == RenderMode.Ambiance)
            {
                modePrompt =
                    "Style recherché : rendu plus charmant, esthétique et valorisant pour une présentation d'avant-projet. " +
                    "L'ambiance doit être plus agréable, avec une lumière naturelle douce, des couleurs harmonieuses, des matériaux plus séduisants et un rendu global plus chaleureux. " +
                    "Le résultat doit rester réaliste et professionnel, sans devenir artificiel ou trop décoratif. " +
                    "Même avec une ambiance plus travaillée, ne change pas la conception et n'invente aucun élément absent de la vue. ";
            }
            else
            {
                modePrompt =
                    "Style recherché : rendu architectural réaliste, propre et valorisant pour une présentation d'avant-projet. " +
                    "Le résultat doit être professionnel, agréable à regarder, crédible et suffisamment séduisant pour montrer rapidement l'intention du projet. " +
                    "Utilise une lumière naturelle équilibrée, des textures réalistes, des ombres cohérentes et des couleurs plus agréables. " +
                    "Le rendu doit être esthétique sans être excessif, et doit rester fidèle à la maquette Revit. ";
            }

            string finalPrompt =
                "Important : le rendu final doit rester une amélioration visuelle de la vue fournie, pas une nouvelle conception. " +
                "Conserve la composition globale et ne change pas l'organisation de la scène.";

            return basePrompt + modePrompt + finalPrompt;
        }

        private static string GetModeLabel(RenderMode mode)
        {
            if (mode == RenderMode.Auto)
                return "Auto";

            if (mode == RenderMode.Fidele)
                return "Fidèle";

            if (mode == RenderMode.Ambiance)
                return "Ambiance";

            return "Présentation";
        }

        private static string GetAutoModeDescription(View view)
        {
            if (view != null && view.ViewType == ViewType.ThreeD)
                return "Auto : vue 3D détectée → mode Présentation.";

            return "Auto : plan/coupe détecté → mode Fidèle.";
        }

        private static JObject SendImageRequestWithFallback(
            string jwt,
            string b64Input,
            string outputSize,
            string prompt
        )
        {
            string dataUrl = "data:image/png;base64," + b64Input;

            var payloads = new object[]
            {
                new
                {
                    model = "gpt-image-2",
                    quality = ImageQuality,
                    size = outputSize,
                    prompt,
                    image = dataUrl
                },

                new
                {
                    model = "gpt-image-2",
                    quality = ImageQuality,
                    size = outputSize,
                    prompt,
                    input_image = dataUrl
                },

                new
                {
                    model = "gpt-image-2",
                    quality = ImageQuality,
                    size = outputSize,
                    prompt,
                    images = new[] { dataUrl }
                }
            };

            Exception lastError = null;

            foreach (var payload in payloads)
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        return AiClient.SendOpenAI(jwt, payload);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;

                        bool isBadPromptError =
                            ex.Message?.IndexOf("Missing model/prompt", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isBadPromptError)
                        {
                            throw new InvalidOperationException(
                                "Le proxy IA attend un couple model/prompt valide pour ce type de requête. " +
                                "Vérifie la configuration de la fonction ai-proxy côté Supabase.",
                                ex
                            );
                        }

                        bool isServerError =
                            ex.Message?.IndexOf("500", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            ex.Message?.IndexOf("server_error", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (!isServerError || attempt == 3)
                            break;

                        Thread.Sleep(700 * attempt);
                    }
                }
            }

            throw lastError ?? new InvalidOperationException("Échec de l'appel IA image.");
        }

        private static string ExtractImageBase64(JObject json)
        {
            return json?["data"]?[0]?["b64_json"]?.ToString()
                ?? json?["image"]?.ToString()
                ?? json?["output"]?[0]?["b64_json"]?.ToString()
                ?? json?["result"]?["b64_json"]?.ToString();
        }

        private static void TrackImageTokenUsage(
         JObject response,
         string outputSize,
         RenderMode mode,
         View view)
        {
            try
            {
                var usage = response?["usage"];
                int inputTokens = usage?["input_tokens"]?.Value<int?>()
                    ?? usage?["prompt_tokens"]?.Value<int?>()
                    ?? 0;
                int outputTokens = usage?["output_tokens"]?.Value<int?>()
                    ?? usage?["completion_tokens"]?.Value<int?>()
                    ?? 0;
                int totalTokens = usage?["total_tokens"]?.Value<int?>()
                    ?? (inputTokens + outputTokens);

                Licensing.Telemetry.TrackButton(
                    "IA.RenduPlan.Tokens",
                    true,
                    new
                    {
                        feature = "RealisticViewImage",
                        model = "gpt-image-2",
                        view_type = view?.ViewType.ToString() ?? "",
                        output_size = outputSize,
                        mode = GetModeLabel(mode),
                        input_tokens = inputTokens,
                        output_tokens = outputTokens,
                        total_tokens = totalTokens,
                        has_usage = usage != null
                    }
                );
            }
            catch
            {
                // Ne jamais bloquer le rendu image si le tracking échoue.
            }
        }
        private static HistoryContext CreateHistoryContext(Document doc, View view)
        {
            string projectName = doc != null && !string.IsNullOrWhiteSpace(doc.Title)
                ? doc.Title
                : "ProjetRevit";

            string viewName = view != null && !string.IsNullOrWhiteSpace(view.Name)
                ? view.Name
                : "Vue";

            string baseName = SanitizeFileName(projectName + "_" + viewName);
            baseName = Truncate(baseName, 90);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs",
                "BIMaestro",
                "IAImage",
                "Historique",
                baseName + "_" + timestamp
            );

            Directory.CreateDirectory(folder);

            return new HistoryContext
            {
                FolderPath = folder,
                SourcePath = Path.Combine(folder, "01_source_revit.png"),
                ResultPath = Path.Combine(folder, "02_rendu_ia.png"),
                PromptPath = Path.Combine(folder, "prompt.txt"),
                ResponsePath = Path.Combine(folder, "response.json")
            };
        }

        private static string BuildPromptDebugText(
            string prompt,
            RenderMode requestedMode,
            RenderMode effectiveMode,
            string outputSize,
            string exportedPath,
            string historyFolder,
            View selectedView
        )
        {
            return
                "BIMaestro - IA Image" + Environment.NewLine +
                "Date : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                "Vue : " + (selectedView != null ? selectedView.Name : "Vue inconnue") + Environment.NewLine +
                "Type de vue : " + (selectedView != null ? selectedView.ViewType.ToString() : "Inconnu") + Environment.NewLine +
                "Mode demandé : " + GetModeLabel(requestedMode) + Environment.NewLine +
                "Mode appliqué : " + GetModeLabel(effectiveMode) + Environment.NewLine +
                "Qualité : " + ImageQuality + Environment.NewLine +
                "Entrée max edge : " + InputMaxEdge + Environment.NewLine +
                "Sortie demandée : " + outputSize + Environment.NewLine +
                "Export Revit temporaire : " + exportedPath + Environment.NewLine +
                "Dossier historique : " + historyFolder + Environment.NewLine +
                Environment.NewLine +
                "PROMPT :" + Environment.NewLine +
                prompt;
        }

        private static void WriteDebugFile(string fileName, string content)
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs",
                    "BIMaestro",
                    "IAImage"
                );

                Directory.CreateDirectory(folder);

                string path = Path.Combine(folder, fileName);
                File.WriteAllText(path, content ?? string.Empty);
            }
            catch
            {
                // Ne jamais bloquer la commande Revit pour un simple log.
            }
        }

        private static void TryWriteAllBytes(string path, byte[] bytes)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || bytes == null)
                    return;

                string folder = Path.GetDirectoryName(path);

                if (!string.IsNullOrWhiteSpace(folder))
                    Directory.CreateDirectory(folder);

                File.WriteAllBytes(path, bytes);
            }
            catch
            {
                // L'historique ne doit jamais bloquer la génération.
            }
        }

        private static void TryWriteAllText(string path, string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                string folder = Path.GetDirectoryName(path);

                if (!string.IsNullOrWhiteSpace(folder))
                    Directory.CreateDirectory(folder);

                File.WriteAllText(path, text ?? string.Empty);
            }
            catch
            {
                // L'historique ne doit jamais bloquer la génération.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Ignore volontairement les anciens fichiers verrouillés.
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();

            return new string((name ?? "vue")
                .Select(c => invalid.Contains(c) ? '_' : c)
                .ToArray());
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "SansNom";

            if (value.Length <= maxLength)
                return value;

            return value.Substring(0, maxLength);
        }

        private class HistoryContext
        {
            public string FolderPath { get; set; }
            public string SourcePath { get; set; }
            public string ResultPath { get; set; }
            public string PromptPath { get; set; }
            public string ResponsePath { get; set; }
        }

        private class ViewPickerWindow : Window
        {
            private readonly ListBox _list;
            private readonly RadioButton _rbAuto;
            private readonly RadioButton _rbFidele;
            private readonly RadioButton _rbPresentation;
            private readonly RadioButton _rbAmbiance;
            private readonly TextBlock _modeInfo;

            public View SelectedView { get; private set; }
            public RenderMode SelectedRenderMode { get; private set; }

            public ViewPickerWindow(List<View> views)
            {
                SelectedRenderMode = RenderMode.Auto;

                Title = "BIMaestro - Rendu IA d'avant-projet";
                Width = 620;
                Height = 600;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.CanResize;
                Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));

                var grid = new Grid { Margin = new Thickness(14) };

                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var header = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 0, 0, 12)
                };

                header.Children.Add(new TextBlock
                {
                    Text = "Créer un rendu IA depuis une vue Revit",
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(25, 35, 55)),
                    TextWrapping = TextWrapping.Wrap
                });

                header.Children.Add(new TextBlock
                {
                    Text = "Choisis une vue puis un style de rendu. Qualité : low — entrée/sortie max 1536 px.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(95, 103, 115)),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });

                Grid.SetRow(header, 0);
                grid.Children.Add(header);

                _list = new ListBox
                {
                    DisplayMemberPath = "Name",
                    BorderBrush = new SolidColorBrush(Color.FromRgb(210, 215, 225)),
                    BorderThickness = new Thickness(1),
                    Background = Brushes.White
                };

                foreach (var v in views)
                    _list.Items.Add(new ViewRow(v));

                if (_list.Items.Count > 0)
                    _list.SelectedIndex = 0;

                _list.SelectionChanged += (s, e) => UpdateModeInfo();
                _list.MouseDoubleClick += (s, e) => ValidateAndClose();

                Grid.SetRow(_list, 1);
                grid.Children.Add(_list);

                var optionsBorder = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(210, 215, 225)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 12, 0, 0)
                };

                var optionsPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical
                };

                optionsPanel.Children.Add(new TextBlock
                {
                    Text = "Style du rendu",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(25, 35, 55)),
                    Margin = new Thickness(0, 0, 0, 8)
                });

                _rbAuto = new RadioButton
                {
                    Content = "Auto recommandé — Fidèle pour plan/coupe, Présentation pour 3D",
                    GroupName = "RenderMode",
                    IsChecked = true,
                    Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = "Adapte automatiquement le prompt au type de vue sélectionné."
                };

                _rbFidele = new RadioButton
                {
                    Content = "Fidèle — rendu sobre, proche de la maquette",
                    GroupName = "RenderMode",
                    Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = "Priorise la conformité à la vue Revit."
                };

                _rbPresentation = new RadioButton
                {
                    Content = "Présentation — rendu propre et valorisant",
                    GroupName = "RenderMode",
                    Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = "Bon équilibre pour l'avant-projet."
                };

                _rbAmbiance = new RadioButton
                {
                    Content = "Ambiance — rendu plus charmeur",
                    GroupName = "RenderMode",
                    Margin = new Thickness(0, 0, 0, 10),
                    ToolTip = "Plus esthétique, avec un peu plus de risque d'interprétation."
                };

                _rbAuto.Checked += (s, e) => UpdateModeInfo();
                _rbFidele.Checked += (s, e) => UpdateModeInfo();
                _rbPresentation.Checked += (s, e) => UpdateModeInfo();
                _rbAmbiance.Checked += (s, e) => UpdateModeInfo();

                optionsPanel.Children.Add(_rbAuto);
                optionsPanel.Children.Add(_rbFidele);
                optionsPanel.Children.Add(_rbPresentation);
                optionsPanel.Children.Add(_rbAmbiance);

                _modeInfo = new TextBlock
                {
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(95, 103, 115)),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };

                optionsPanel.Children.Add(_modeInfo);

                optionsBorder.Child = optionsPanel;

                Grid.SetRow(optionsBorder, 2);
                grid.Children.Add(optionsBorder);

                var panelBtn = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };

                var ok = new Button
                {
                    Content = "Générer",
                    Width = 120,
                    Height = 32,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                ok.Click += (s, e) => ValidateAndClose();

                var cancel = new Button
                {
                    Content = "Annuler",
                    Width = 90,
                    Height = 32
                };

                cancel.Click += (s, e) =>
                {
                    DialogResult = false;
                    Close();
                };

                panelBtn.Children.Add(ok);
                panelBtn.Children.Add(cancel);

                Grid.SetRow(panelBtn, 3);
                grid.Children.Add(panelBtn);

                Content = grid;

                UpdateModeInfo();
            }

            private void UpdateModeInfo()
            {
                if (_modeInfo == null)
                    return;

                View selectedView = GetCurrentSelectedView();
                RenderMode selectedMode = GetSelectedRenderMode();
                RenderMode effectiveMode = ResolveEffectiveRenderMode(selectedMode, selectedView);

                if (selectedMode == RenderMode.Auto)
                {
                    _modeInfo.Text =
                        GetAutoModeDescription(selectedView) +
                        " Mode appliqué : " + GetModeLabel(effectiveMode) + ".";
                }
                else
                {
                    _modeInfo.Text =
                        "Mode appliqué : " + GetModeLabel(effectiveMode) + ".";
                }
            }

            private View GetCurrentSelectedView()
            {
                if (_list.SelectedItem is ViewRow row)
                    return row.View;

                return null;
            }

            private RenderMode GetSelectedRenderMode()
            {
                if (_rbAuto != null && _rbAuto.IsChecked == true)
                    return RenderMode.Auto;

                if (_rbFidele != null && _rbFidele.IsChecked == true)
                    return RenderMode.Fidele;

                if (_rbAmbiance != null && _rbAmbiance.IsChecked == true)
                    return RenderMode.Ambiance;

                return RenderMode.Presentation;
            }

            private void ValidateAndClose()
            {
                if (!(_list.SelectedItem is ViewRow row))
                    return;

                SelectedView = row.View;
                SelectedRenderMode = GetSelectedRenderMode();

                DialogResult = true;
                Close();
            }

            private class ViewRow
            {
                public View View { get; }

                public string Name => $"[{View.ViewType}] {View.Name}";

                public ViewRow(View v)
                {
                    View = v;
                }
            }
        }

        private class ImageResultPreviewWindow : Window
        {
            private readonly byte[] _sourceBytes;
            private readonly byte[] _resultBytes;
            private readonly string _suggestedFileName;
            private readonly string _sourceInfo;
            private readonly string _resultInfo;
            private readonly string _historyFolder;

            public ImageResultPreviewWindow(
                byte[] sourceBytes,
                byte[] resultBytes,
                string suggestedFileName,
                string sourceInfo,
                string resultInfo,
                string historyFolder
            )
            {
                _sourceBytes = sourceBytes ?? throw new ArgumentNullException(nameof(sourceBytes));
                _resultBytes = resultBytes ?? throw new ArgumentNullException(nameof(resultBytes));

                _suggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName)
                    ? "image_realiste.png"
                    : suggestedFileName;

                _sourceInfo = string.IsNullOrWhiteSpace(sourceInfo)
                    ? "Dimensions source inconnues"
                    : sourceInfo;

                _resultInfo = string.IsNullOrWhiteSpace(resultInfo)
                    ? "Dimensions résultat inconnues"
                    : resultInfo;

                _historyFolder = historyFolder ?? string.Empty;

                Title = "BIMaestro - Avant / Après rendu IA";
                Width = 1180;
                Height = 760;
                MinWidth = 900;
                MinHeight = 560;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.CanResize;
                Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));

                Content = BuildContent();
            }

            private UIElement BuildContent()
            {
                var root = new Grid { Margin = new Thickness(14) };

                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var header = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 0, 0, 10)
                };

                header.Children.Add(new TextBlock
                {
                    Text = "Comparaison avant / après",
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(25, 35, 55))
                });

                header.Children.Add(new TextBlock
                {
                    Text = "Source : " + _sourceInfo + " — Rendu IA : " + _resultInfo,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(95, 103, 115)),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });

                if (!string.IsNullOrWhiteSpace(_historyFolder))
                {
                    header.Children.Add(new TextBlock
                    {
                        Text = "Historique local : " + _historyFolder,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(120, 127, 138)),
                        Margin = new Thickness(0, 4, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                Grid.SetRow(header, 0);
                root.Children.Add(header);

                var comparisonGrid = new Grid();

                comparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                comparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                comparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var sourcePanel = BuildImagePanel(
                    "Vue Revit source",
                    _sourceBytes,
                    _sourceInfo
                );

                Grid.SetColumn(sourcePanel, 0);
                comparisonGrid.Children.Add(sourcePanel);

                var resultPanel = BuildImagePanel(
                    "Rendu IA",
                    _resultBytes,
                    _resultInfo
                );

                Grid.SetColumn(resultPanel, 2);
                comparisonGrid.Children.Add(resultPanel);

                Grid.SetRow(comparisonGrid, 1);
                root.Children.Add(comparisonGrid);

                var footer = new Grid
                {
                    Margin = new Thickness(0, 12, 0, 0)
                };

                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var note = new TextBlock
                {
                    Text = "Une copie source/résultat/prompt est conservée dans l’historique local. Le bouton Enregistrer permet de choisir un emplacement final.",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(95, 103, 115)),
                    TextWrapping = TextWrapping.Wrap
                };

                Grid.SetColumn(note, 0);
                footer.Children.Add(note);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };

                var saveButton = new Button
                {
                    Content = "Enregistrer...",
                    Width = 130,
                    Height = 32,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                saveButton.Click += (s, e) => SaveImage();

                var closeButton = new Button
                {
                    Content = "Fermer",
                    Width = 90,
                    Height = 32
                };

                closeButton.Click += (s, e) => Close();

                buttons.Children.Add(saveButton);
                buttons.Children.Add(closeButton);

                Grid.SetColumn(buttons, 1);
                footer.Children.Add(buttons);

                Grid.SetRow(footer, 2);
                root.Children.Add(footer);

                return root;
            }

            private UIElement BuildImagePanel(string title, byte[] imageBytes, string imageInfo)
            {
                var root = new Grid();

                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var titleBlock = new TextBlock
                {
                    Text = title + " — " + imageInfo,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(25, 35, 55)),
                    Margin = new Thickness(0, 0, 0, 6),
                    TextWrapping = TextWrapping.Wrap
                };

                Grid.SetRow(titleBlock, 0);
                root.Children.Add(titleBlock);

                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(210, 215, 225)),
                    BorderThickness = new Thickness(1),
                    Background = Brushes.White,
                    Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(8)
                };

                var imageControl = new System.Windows.Controls.Image
                {
                    Source = CreateBitmapFromBytes(imageBytes),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                RenderOptions.SetBitmapScalingMode(imageControl, BitmapScalingMode.HighQuality);

                border.Child = imageControl;

                Grid.SetRow(border, 1);
                root.Children.Add(border);

                return root;
            }

            private static BitmapImage CreateBitmapFromBytes(byte[] bytes)
            {
                var image = new BitmapImage();

                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze();
                }

                return image;
            }

            private void SaveImage()
            {
                var save = new SaveFileDialog
                {
                    Title = "Enregistrer l'image réaliste",
                    Filter = "Image PNG (*.png)|*.png",
                    FileName = _suggestedFileName
                };

                if (save.ShowDialog(this) == true)
                {
                    File.WriteAllBytes(save.FileName, _resultBytes);

                    MessageBox.Show(
                        this,
                        "Image enregistrée.",
                        "BIMaestro - IA Image",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );

                    Close();
                }
            }
        }
    }
}
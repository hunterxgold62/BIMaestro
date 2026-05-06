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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IA
{
    [Transaction(TransactionMode.Manual)]
    public class RealisticViewImageCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "RealisticViewImageCommand";

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
                var normalizedBytes = NormalizeImageToMax1024(exportedPath);
                string b64Input = Convert.ToBase64String(normalizedBytes);

                var response = AiClient.SendOpenAI(jwt, new
                {
                    model = "gpt-image-2",
                    quality = "low",
                    size = "1024x1024",
                    prompt = "Transforme cette vue Revit en rendu réaliste, sans modifier la géométrie, la composition, l'angle, ni les éléments présents. Respect absolu du cadrage et des proportions.",
                    image = b64Input
                });

                string resultB64 = ExtractImageBase64(response);
                if (string.IsNullOrWhiteSpace(resultB64))
                    throw new InvalidOperationException("Réponse IA sans image exploitable.");

                byte[] outBytes = Convert.FromBase64String(resultB64);
                var save = new SaveFileDialog
                {
                    Title = "Enregistrer l'image réaliste",
                    Filter = "Image PNG (*.png)|*.png",
                    FileName = $"{SanitizeFileName(picker.SelectedView.Name)}_realiste.png"
                };

                if (save.ShowDialog() == true)
                {
                    File.WriteAllBytes(save.FileName, outBytes);
                    TaskDialog.Show("IA Image", "Image générée et enregistrée sur le PC.");
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("IA Image", $"Erreur lors de la génération IA : {ex.Message}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private static bool IsEligibleView(View v)
        {
            if (v == null || v.IsTemplate) return false;
            return v.ViewType == ViewType.FloorPlan
                || v.ViewType == ViewType.CeilingPlan
                || v.ViewType == ViewType.EngineeringPlan
                || v.ViewType == ViewType.Section
                || v.ViewType == ViewType.ThreeD;
        }

        private static string ExportViewAsPng(Document doc, ElementId viewId)
        {
            string folder = Path.Combine(Path.GetTempPath(), "BIMaestro", "IAImage");
            Directory.CreateDirectory(folder);
            string baseName = "revit_view_export";

            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(folder, baseName),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 1024,
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_150
            };
            opts.SetViewsAndSheets(new List<ElementId> { viewId });
            doc.ExportImage(opts);

            return Directory.GetFiles(folder, "*.png")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static byte[] NormalizeImageToMax1024(string imagePath)
        {
            var src = new BitmapImage();
            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.UriSource = new Uri(imagePath);
            src.EndInit();
            src.Freeze();

            int width = src.PixelWidth;
            int height = src.PixelHeight;
            double ratio = Math.Min(1024.0 / width, 1024.0 / height);
            if (ratio >= 1.0)
                return File.ReadAllBytes(imagePath);

            int targetW = Math.Max(1, (int)Math.Round(width * ratio));
            int targetH = Math.Max(1, (int)Math.Round(height * ratio));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(src, new Rect(0, 0, targetW, targetH));
            }

            var bmp = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static string ExtractImageBase64(JObject json)
        {
            return json?["data"]?[0]?["b64_json"]?.ToString()
                ?? json?["image"]?.ToString()
                ?? json?["output"]?[0]?["b64_json"]?.ToString()
                ?? json?["result"]?["b64_json"]?.ToString();
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string((name ?? "vue").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private class ViewPickerWindow : Window
        {
            private readonly ListBox _list;
            public View SelectedView { get; private set; }

            public ViewPickerWindow(List<View> views)
            {
                Title = "IA Image - Choisir une vue";
                Width = 520;
                Height = 480;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.CanResize;
                Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));

                var grid = new Grid { Margin = new Thickness(12) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                grid.Children.Add(new TextBlock
                {
                    Text = "Choisis la vue à transformer en rendu réaliste (plan, coupe ou 3D).",
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 10),
                    TextWrapping = TextWrapping.Wrap
                });

                _list = new ListBox { DisplayMemberPath = "Name" };
                foreach (var v in views)
                    _list.Items.Add(new ViewRow(v));
                _list.MouseDoubleClick += (_, __) => ValidateAndClose();
                Grid.SetRow(_list, 1);
                grid.Children.Add(_list);

                var panelBtn = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var ok = new Button { Content = "Générer", Width = 110, Margin = new Thickness(0, 0, 8, 0) };
                ok.Click += (_, __) => ValidateAndClose();
                var cancel = new Button { Content = "Annuler", Width = 90 };
                cancel.Click += (_, __) => { DialogResult = false; Close(); };
                panelBtn.Children.Add(ok);
                panelBtn.Children.Add(cancel);
                Grid.SetRow(panelBtn, 2);
                grid.Children.Add(panelBtn);

                Content = grid;
            }

            private void ValidateAndClose()
            {
                if (_list.SelectedItem is ViewRow row)
                {
                    SelectedView = row.View;
                    DialogResult = true;
                    Close();
                }
            }

            private class ViewRow
            {
                public View View { get; }
                public string Name => $"[{View.ViewType}] {View.Name}";
                public ViewRow(View v) => View = v;
            }
        }
    }
}

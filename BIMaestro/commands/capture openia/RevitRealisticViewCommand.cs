using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace IA
{
    [Transaction(TransactionMode.Manual)]
    public class RevitRealisticViewCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "RevitRealisticViewButton";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null) return Result.Failed;

            string jwt = BIMaestroApp.LicenseJwt;
            if (string.IsNullOrWhiteSpace(jwt))
            {
                TaskDialog.Show("IA", "Licence non valide : impossible d'appeler l'IA.");
                return Result.Cancelled;
            }

            var candidateViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.Section || v.ViewType == ViewType.ThreeD))
                .OrderBy(v => v.ViewType)
                .ThenBy(v => v.Name)
                .ToList();

            if (candidateViews.Count == 0)
            {
                TaskDialog.Show("IA", "Aucune vue plan/coupe/3D disponible.");
                return Result.Cancelled;
            }

            var picker = new RevitRealisticViewWindow(candidateViews);
            var helper = new WindowInteropHelper(picker) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            if (picker.ShowDialog() != true || picker.SelectedViewId == null)
                return Result.Cancelled;

            var selectedView = doc.GetElement(picker.SelectedViewId) as View;
            if (selectedView == null) return Result.Cancelled;

            string tempDir = Path.Combine(Path.GetTempPath(), "BIMaestro_IA_Views");
            Directory.CreateDirectory(tempDir);
            string inputPath = Path.Combine(tempDir, $"revit_view_{selectedView.Id.IntegerValue}_{DateTime.Now:yyyyMMddHHmmss}.png");
            ExportViewAsPng(doc, selectedView, inputPath);

            string optimizedPath = Path.Combine(tempDir, $"revit_view_opt_{selectedView.Id.IntegerValue}_{DateTime.Now:yyyyMMddHHmmss}.png");
            ImageResizeHelper.ResizeToMax1024(inputPath, optimizedPath);

            string base64 = Convert.ToBase64String(File.ReadAllBytes(optimizedPath));
            var request = new
            {
                model = "gpt-image-1-mini",
                prompt = "Transforme cette vue Revit (plan, coupe ou vue 3D) en rendu réaliste sans modifier la composition, le cadrage, les proportions, ni les éléments visibles. Respect strict de la géométrie et des annotations visibles.",
                size = "1024x1024",
                quality = "medium",
                image = base64
            };

            JObject json = AiClient.SendOpenAI(jwt, request);
            string outputB64 = json["data"]?[0]?["b64_json"]?.ToString();
            if (string.IsNullOrWhiteSpace(outputB64))
                throw new InvalidOperationException("Aucune image retournée par l'API.");

            byte[] imageBytes = Convert.FromBase64String(outputB64);
            string defaultName = $"{SanitizeFileName(selectedView.Name)}_realiste.png";
            string suggestedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), defaultName);

            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Enregistrer l'image réaliste",
                Filter = "Image PNG (*.png)|*.png",
                FileName = defaultName,
                InitialDirectory = Path.GetDirectoryName(suggestedPath)
            };

            if (saveDialog.ShowDialog() == true)
            {
                File.WriteAllBytes(saveDialog.FileName, imageBytes);
                TaskDialog.Show("IA", $"Image enregistrée :\n{saveDialog.FileName}");
            }

            return Result.Succeeded;
        }

        private static void ExportViewAsPng(Document doc, View view, string targetFilePath)
        {
            string folder = Path.GetDirectoryName(targetFilePath);
            string file = Path.GetFileNameWithoutExtension(targetFilePath);
            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 2048,
                FilePath = Path.Combine(folder, file)
            };
            opts.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(opts);

            var produced = Directory.GetFiles(folder, file + "*.png").OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(produced) || !File.Exists(produced))
                throw new InvalidOperationException("Échec de l'export de la vue.");

            if (!string.Equals(produced, targetFilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(targetFilePath)) File.Delete(targetFilePath);
                File.Move(produced, targetFilePath);
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}

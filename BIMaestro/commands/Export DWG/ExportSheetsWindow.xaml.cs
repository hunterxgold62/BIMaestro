using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.WindowsAPICodePack.Dialogs;  // NuGet : Microsoft.WindowsAPICodePack.Dialogs
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MessageBox = System.Windows.MessageBox;

namespace Visualisation
{
    public partial class ExportWindow : Window
    {
        private readonly Document _doc;
        private readonly List<ViewSheetSet> _sheetSets;

        public ExportWindow(ExternalCommandData cmdData)
        {
            InitializeComponent();

            // 1) Charge le Document et tous les ViewSheetSet
            _doc = cmdData.Application.ActiveUIDocument.Document;
            _sheetSets = new FilteredElementCollector(_doc)
                             .OfClass(typeof(ViewSheetSet))
                             .Cast<ViewSheetSet>()
                             .ToList();

            SheetSetComboBox.ItemsSource = _sheetSets
                .Select(s => s.Name)
                .OrderBy(n => n);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            // Explorateur de dossiers moderne
            var dlg = new CommonOpenFileDialog
            {
                Title = "Choisissez le dossier d’export DWG",
                IsFolderPicker = true,
                AllowNonFileSystemItems = false,
                EnsurePathExists = true,
                EnsureValidNames = true
            };
            if (dlg.ShowDialog() == CommonFileDialogResult.Ok)
            {
                FolderTextBox.Text = dlg.FileName;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            // 2) Validation du nom du jeu
            string setName = SheetSetComboBox.Text?.Trim();
            if (string.IsNullOrEmpty(setName))
            {
                MessageBox.Show("Veuillez saisir un nom de jeu de feuilles.",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sheetSet = _sheetSets
                .FirstOrDefault(s => s.Name.Equals(setName,
                                                   StringComparison.OrdinalIgnoreCase));
            if (sheetSet == null)
            {
                MessageBox.Show($"Le jeu '{setName}' n'existe pas.",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 3) Validation du dossier
            string exportDir = FolderTextBox.Text;
            if (string.IsNullOrEmpty(exportDir) || !Directory.Exists(exportDir))
            {
                MessageBox.Show("Veuillez choisir un dossier d’export valide.",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 4) Désactive l’UI
            ExportButton.IsEnabled = false;
            CancelButton.IsEnabled = false;

            // 5) Récupère les ViewSheet du ViewSet
            var sheets = sheetSet.Views
                                 .OfType<ViewSheet>()
                                 .ToList();
            if (sheets.Count == 0)
            {
                MessageBox.Show("Aucune feuille à exporter dans ce jeu.",
                                "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }

            // 6) Prépare les options DWG
            var options = new DWGExportOptions
            {
                MergedViews = true,
                TargetUnit = ExportUnit.Meter,
                Colors = ExportColorMode.TrueColorPerView
            };

            // 7) Boucle par feuille
            var sw = Stopwatch.StartNew();
            foreach (var vs in sheets)
            {
                // Nom "élaboré", puis nom de secours (numéro de feuille)
                string elaborateName = BuildFileName(vs);
                elaborateName = SanitizeFileName(elaborateName);

                bool success = TryExport(vs, exportDir, elaborateName, options);

                if (!success)
                {
                    // Nom de secours : juste le numéro de la feuille
                    string fallback = SanitizeFileName(vs.SheetNumber);
                    TryExport(vs, exportDir, fallback, options);
                }
            }
            sw.Stop();

            // 8) Retour utilisateur
            MessageBox.Show(
                $"Export de {sheets.Count} feuilles réalisé en {sw.Elapsed:mm\\:ss}.",
                "Terminé", MessageBoxButton.OK, MessageBoxImage.Information);

            Close();
        }

        // Tente l'export et retourne true si OK
        private bool TryExport(ViewSheet vs, string dir, string name, DWGExportOptions options)
        {
            try
            {
                _doc.Export(dir, name, new List<ElementId> { vs.Id }, options);
                // suppression éventuelle du .pcp
                string pcp = Path.Combine(dir, name + ".pcp");
                if (File.Exists(pcp)) File.Delete(pcp);
                return true;
            }
            catch (Exception ex)
            {
                // On loggue le message (ou affiche selon besoin)
                Debug.WriteLine($"Export failed '{name}' : {ex.Message}");
                return false;
            }
        }

        // Retire les caractères invalides pour un nom de fichier
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var regex = new Regex($"[{Regex.Escape(new string(invalid))}]");
            var clean = regex.Replace(name, "_");
            // Limite à 120 caractères comme avant
            return clean.Length <= 120 ? clean : clean.Substring(0, 120);
        }

        /// <summary>
        /// Construit le nom DWG (script Dynamo)
        /// </summary>
        private static string BuildFileName(ViewSheet sheet)
        {
            string P(string n) =>
                sheet.LookupParameter(n) is Parameter p && p.HasValue
                    ? p.AsString().Trim()
                    : "";

            var parts = new[]
            {
                P("CML_Projet"),
                P("CML_Phase"),
                P("CML_Emetteur"),
                P("CML_Lot"),
                P("CML_Nature"),
                P("Numéro de la feuille"),
                !string.IsNullOrEmpty(P("Révisions sur feuille"))
                    ? P("Révisions sur feuille")
                    : P("Révision actuelle"),
                sheet.Name
            }
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();

            var name = string.Join("-", parts);
            return name;
        }
    }
}

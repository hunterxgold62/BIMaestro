using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MessageBox = System.Windows.MessageBox;

namespace MonPluginRevit
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
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Choisissez le dossier d’export DWG",
                ShowNewFolderButton = true
            })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    FolderTextBox.Text = dlg.SelectedPath;
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

            // 5) Récupère **les seules** ViewSheet du ViewSet
            var sheets = sheetSet.Views
                                 .OfType<ViewSheet>()   // filtre pour ne garder que les feuilles
                                 .ToList();
            if (sheets.Count == 0)
            {
                MessageBox.Show("Aucune feuille à exporter dans ce jeu.",
                                "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }

            // 6) Prépare les options DWG : 
            //    • MergedViews = true → un seul DWG complet par appel
            //    • Autres options inchangées
            var options = new DWGExportOptions
            {
                MergedViews = true,
                TargetUnit = ExportUnit.Meter,
                Colors = ExportColorMode.TrueColorPerView
            };

            // 7) Boucle **par feuille** (méthode basique)
            var sw = Stopwatch.StartNew();
            foreach (var vs in sheets)
            {
                string fileName = BuildFileName(vs);
                try
                {
                    // Export à chaque fois UNE feuille
                    _doc.Export(exportDir,
                                fileName,
                                new List<ElementId> { vs.Id },
                                options);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Erreur pour la feuille {vs.SheetNumber} : {ex.Message}",
                        "Erreur Export", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                // Supprime le .pcp généré si besoin
                string pcp = Path.Combine(exportDir, fileName + ".pcp");
                if (File.Exists(pcp))
                {
                    try { File.Delete(pcp); }
                    catch { /* ignore */ }
                }
            }
            sw.Stop();

            // 8) Retour utilisateur
            MessageBox.Show(
                $"Export de {sheets.Count} feuilles réalisé en {sw.Elapsed:mm\\:ss}.",
                "Terminé", MessageBoxButton.OK, MessageBoxImage.Information);

            Close();
        }

        /// <summary>
        /// Construit le nom du DWG identique à ton script Dynamo
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
            return name.Length <= 120
                ? name
                : name.Substring(0, 120) + "...";
        }
    }
}

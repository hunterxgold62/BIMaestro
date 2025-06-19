using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using MessageBox = System.Windows.MessageBox;

namespace MonPluginRevit
{
    public partial class ExportWindow : Window
    {
        private readonly UIDocument _uiDoc;
        private readonly Document _doc;
        private readonly string _rvtPath;
        private readonly List<ViewSheetSet> _sheetSets;

        // **Dossier fixe pour les .rvt tampons et les JSON**
        private const string TempFolder =
            @"C:\Users\plemert\OneDrive - SAS H.C.M. HOLDING CABINET MERLIN\Documents\RevitLogs\test";

        public ExportWindow(ExternalCommandData cmdData)
        {
            InitializeComponent();

            _uiDoc = cmdData.Application.ActiveUIDocument;
            _doc = _uiDoc.Document;
            _rvtPath = _doc.PathName;

            if (string.IsNullOrEmpty(_rvtPath))
            {
                MessageBox.Show(
                    "Le document doit être enregistré avant export parallèle.",
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            // Création du dossier tampon
            Directory.CreateDirectory(TempFolder);

            // Chargement des jeux de feuilles
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
            using var dlg = new FolderBrowserDialog
            {
                Description = "Choisissez le dossier d’export DWG",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                FolderTextBox.Text = dlg.SelectedPath;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            // 1) Validation des entrées
            string setName = SheetSetComboBox.Text?.Trim();
            if (string.IsNullOrEmpty(setName))
            {
                MessageBox.Show(
                    "Veuillez sélectionner un jeu de feuilles.",
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sheetSet = _sheetSets
                .FirstOrDefault(s => s.Name.Equals(setName, StringComparison.OrdinalIgnoreCase));
            if (sheetSet == null)
            {
                MessageBox.Show(
                    $"Le jeu '{setName}' n'existe pas.",
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string exportDir = FolderTextBox.Text;
            if (string.IsNullOrEmpty(exportDir) || !Directory.Exists(exportDir))
            {
                MessageBox.Show(
                    "Veuillez choisir un dossier d’export valide.",
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(GroupSizeTextBox.Text, out int groupSize) || groupSize <= 0)
            {
                MessageBox.Show(
                    "Le nombre de vues par groupe doit être un entier > 0.",
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2) Extraction des feuilles
            var sheets = sheetSet.Views
                                 .OfType<ViewSheet>()
                                 .ToList();
            if (sheets.Count == 0)
            {
                MessageBox.Show(
                    "Aucune feuille à exporter dans ce jeu.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 3) Découpage en groupes
            var groups = sheets
                .Select((s, i) => new { Index = i, Sheet = s })
                .GroupBy(x => x.Index / groupSize)
                .Select(g => g.Select(x => x.Sheet).ToList())
                .ToList();

            // 4) Création des processus Revit parallèles
            var processes = new List<Process>();
            for (int i = 0; i < groups.Count; i++)
            {
                string copyName = $"{Path.GetFileNameWithoutExtension(_rvtPath)}_Copy_{i + 1}.rvt";
                string copyPath = Path.Combine(TempFolder, copyName);
                File.Copy(_rvtPath, copyPath, overwrite: true);

                // Génération du JSON de tâche
                var taskDef = new TaskDefinition
                {
                    Views = groups[i].Select(v => v.Id.IntegerValue).ToList(),
                    ExportDir = exportDir,
                    Options = new ExportOptionsDefinition
                    {
                        MergedViews = true,
                        TargetUnit = "Meter",
                        Colors = "TrueColorPerView"
                    }
                };
                string jsonName = $"task_{i + 1}.json";
                string jsonPath = Path.Combine(TempFolder, jsonName);
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(taskDef, Formatting.Indented));

                // Lancement de Revit avec journal (journal en 1er paramètre)
                string journal = JournalHelper.CreateJournalForTask(copyPath, jsonPath);
                var psi = new ProcessStartInfo
                {
                    FileName = JournalHelper.RevitExePath,
                    Arguments = $"/nosplash /language fr-FR /journal \"{journal}\" \"{copyPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                processes.Add(Process.Start(psi)!);
            }

            // 5) Attente en tâche de fond, nettoyage et fermeture UI
            Task.Run(() =>
            {
                // Attend la fin de tous les Revit
                foreach (var p in processes) p.WaitForExit();

                // Nettoyage des tampons
                for (int i = 1; i <= groups.Count; i++)
                {
                    TryDelete(Path.Combine(TempFolder,
                        $"{Path.GetFileNameWithoutExtension(_rvtPath)}_Copy_{i}.rvt"));
                    TryDelete(Path.Combine(TempFolder, $"task_{i}.json"));
                }

                // Message de fin et fermeture de la fenêtre sur le thread UI
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"Export parallèle de {sheets.Count} feuilles terminé.",
                        "Terminé",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Close();
                });
            });
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { /* ignore */ }
        }
    }
}

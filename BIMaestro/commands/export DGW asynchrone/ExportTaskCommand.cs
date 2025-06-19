using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Newtonsoft.Json;

// on conserve juste l'alias MessageBox
using MessageBox = System.Windows.MessageBox;

namespace MonPluginRevit
{
    [Transaction(TransactionMode.Manual)]
    public class ExportTaskCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            // 1) Repère le dossier de travail via le chemin du .rvt ouvert
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument.Document;
            string folder = Path.GetDirectoryName(doc.PathName)
                            ?? throw new InvalidOperationException("Chemin du .rvt introuvable");

            // 2) Prépare le log pour debug
            string logPath = Path.Combine(folder, "export_task.log");
            void Log(string line)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
                catch { /* ignore */ }
            }

            Log("=== Début ExportTaskCommand ===");

            // 3) Localise le fichier de journal (.txt) le plus récent dans ce dossier
            string journalFile = Directory
                .GetFiles(folder, "*.txt")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .FirstOrDefault();
            if (journalFile == null)
            {
                Log("Aucun .txt de journal trouvé dans le dossier.");
                return Result.Failed;
            }
            Log($"Journal trouvé : {Path.GetFileName(journalFile)}");

            // 4) Extrait la ligne ExternalCommandArgument pour récupérer le chemin du JSON
            string taskJson = null;
            try
            {
                var lines = File.ReadAllLines(journalFile);
                var argLine = lines
                    .FirstOrDefault(l => l.StartsWith("ExternalCommandArgument", StringComparison.OrdinalIgnoreCase));
                if (argLine != null)
                {
                    // split par " pour récupérer le chemin entre guillemets
                    var parts = argLine.Split('"');
                    if (parts.Length >= 2)
                        taskJson = parts[1];
                }
            }
            catch (Exception ex)
            {
                Log($"Erreur lecture journal : {ex.Message}");
                return Result.Failed;
            }

            if (string.IsNullOrEmpty(taskJson) || !File.Exists(taskJson))
            {
                Log($"task.json introuvable ou inexistant : '{taskJson}'");
                return Result.Failed;
            }
            Log($"JSON de tâche : {Path.GetFileName(taskJson)}");

            // 5) Désérialise le JSON
            TaskDefinition task;
            try
            {
                task = JsonConvert
                       .DeserializeObject<TaskDefinition>(
                           File.ReadAllText(taskJson))
                       ?? throw new Exception("Tâche vide après désérialisation");
                Log($"JSON chargé : {task.Views.Count} vues, exportDir = {task.ExportDir}");
            }
            catch (Exception ex)
            {
                Log($"Erreur désérialisation JSON : {ex.Message}");
                return Result.Failed;
            }

            // 6) Prépare la liste des ElementId et DWGExportOptions
            IList<ElementId> ids = task.Views.Select(i => new ElementId(i)).ToList();
            var opts = new DWGExportOptions
            {
                MergedViews = task.Options.MergedViews,
                TargetUnit = (ExportUnit)Enum.Parse(
                                  typeof(ExportUnit),
                                  task.Options.TargetUnit),
                Colors = (ExportColorMode)Enum.Parse(
                                  typeof(ExportColorMode),
                                  task.Options.Colors)
            };

            // 7) Lance l’export
            try
            {
                string baseName = Path.GetFileNameWithoutExtension(doc.PathName);
                doc.Export(
                    task.ExportDir,
                    $"Group_{baseName}",
                    ids,
                    opts);
                Log($"Export réussi : {ids.Count} vues vers {task.ExportDir}");
            }
            catch (Exception ex)
            {
                Log($"Erreur pendant Export : {ex.Message}");
                return Result.Failed;
            }

            Log("=== Fin ExportTaskCommand ===");
            return Result.Succeeded;
        }
    }
}

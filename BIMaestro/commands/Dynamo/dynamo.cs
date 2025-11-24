using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Dynamo.Applications;
using Dynamo.Applications.Properties;
using Newtonsoft.Json;

namespace Modification
{
    public static class DynamoSettings
    {
        private const int ButtonCount = 5;
        private static readonly string ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "SauvegardePréférence");
        private static readonly string ConfigFile = Path.Combine(ConfigFolder, "DynamoButtons.json");
        private static readonly string LegacyConfigFile = Path.Combine(ConfigFolder, "DynamoPaths.txt");
        private static readonly string DefaultPath =
            @"P:\0-Boîte à outils Revit\1-Dynamo\CML_LOD_200.dyn";

        private static readonly string[] DefaultLabels = new[]
        {
            "Auto\nDynamo 1",
            "Auto\nDynamo 2",
            "Auto\nDynamo 3",
            "Auto\nDynamo 4",
            "Auto\nDynamo 5"
        };

        private class ButtonConfig
        {
            public string Path { get; set; }
            public string Label { get; set; }
        }

        private static readonly ButtonConfig[] buttons = new ButtonConfig[ButtonCount];

        static DynamoSettings()
        {
            for (int i = 0; i < ButtonCount; i++)
                buttons[i] = new ButtonConfig();

            try
            {
                // Crée le dossier si besoin
                if (!Directory.Exists(ConfigFolder))
                    Directory.CreateDirectory(ConfigFolder);

                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile);
                    var data = JsonConvert.DeserializeObject<List<ButtonConfig>>(json);
                    if (data != null)
                    {
                        for (int i = 0; i < Math.Min(data.Count, ButtonCount); i++)
                        {
                            var cfg = data[i];
                            if (cfg == null) continue;
                            buttons[i].Path = cfg.Path;
                            buttons[i].Label = NormalizeLabel(cfg.Label);
                        }
                    }
                }
                else if (File.Exists(LegacyConfigFile))
                {
                    var lines = File.ReadAllLines(LegacyConfigFile);
                    for (int i = 0; i < Math.Min(lines.Length, ButtonCount); i++)
                    {
                        if (!string.IsNullOrWhiteSpace(lines[i]))
                            buttons[i].Path = lines[i];
                    }
                    Save();
                }
            }
            catch
            {
                // En cas d’erreur, on reste sur les valeurs par défaut
            }
        }

        private static void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(buttons, Formatting.Indented);
                File.WriteAllText(ConfigFile, json);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Erreur", "Impossible d'enregistrer la configuration :\n" + ex.Message);
            }
        }

        private static string NormalizeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return null;

            string normalized = label.Replace("\r\n", "\n").Replace('\r', '\n');
            normalized = normalized.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static void ValidateIndex(int index)
        {
            if (index < 0 || index >= ButtonCount)
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        public static string GetPath(int index)
        {
            ValidateIndex(index);
            return !string.IsNullOrWhiteSpace(buttons[index].Path) ? buttons[index].Path : DefaultPath;
        }

        public static string GetLabel(int index)
        {
            ValidateIndex(index);
            string label = buttons[index].Label;
            return !string.IsNullOrWhiteSpace(label) ? label : DefaultLabels[index];
        }

        public static void SetConfiguration(int index, string path, string label)
        {
            ValidateIndex(index);
            buttons[index].Path = path;
            string normalized = NormalizeLabel(label);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                string.Equals(normalized, DefaultLabels[index], StringComparison.Ordinal))
            {
                normalized = null;
            }
            buttons[index].Label = normalized;
            Save();
        }

        public static void SetPath(int index, string path)
        {
            ValidateIndex(index);
            SetConfiguration(index, path, buttons[index].Label);
        }
    }

    public static class DynamoExecutor
    {
        public static Result RunDynamo(int buttonIndex, ExternalCommandData commandData)
        {
            string dynPath = DynamoSettings.GetPath(buttonIndex);
            if (!File.Exists(dynPath))
            {
                TaskDialog.Show("Erreur", $"Le fichier Dynamo n'existe pas :\n{dynPath}");
                return Result.Failed;
            }

            try
            {
                var dynamoRevit = new DynamoRevit();
                var cmdData = new DynamoRevitCommandData(commandData);
                var journal = new Dictionary<string, string>
                {
                    { JournalKeys.ShowUiKey,          false.ToString() },
                    { JournalKeys.AutomationModeKey,  false.ToString() },
                    { JournalKeys.DynPathKey,         dynPath },
                    { JournalKeys.DynPathExecuteKey,  true.ToString() },
                    { JournalKeys.ForceManualRunKey,  true.ToString() },
                    { JournalKeys.ModelShutDownKey,   true.ToString() },
                    { JournalKeys.ModelNodesInfo,     false.ToString() }
                };
                cmdData.JournalData = journal;
                return dynamoRevit.ExecuteCommand(cmdData);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Exception", ex.Message);
                return Result.Failed;
            }
        }
    }

    // 5 commandes de lancement
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class RunDynamo1Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
        => DynamoExecutor.RunDynamo(0, c);
    }
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class RunDynamo2Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
        => DynamoExecutor.RunDynamo(1, c);
    }
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class RunDynamo3Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
        => DynamoExecutor.RunDynamo(2, c);
    }
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class RunDynamo4Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
        => DynamoExecutor.RunDynamo(3, c);
    }
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class RunDynamo5Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData c, ref string m, ElementSet e)
        => DynamoExecutor.RunDynamo(4, c);
    }

    // Commande qui ouvre la fenêtre WPF
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.ReadOnly)]
    public class ConfigureDynamoButtonCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // 1. Création de la fenêtre WPF
                var wnd = new ConfigureDynamoWindow();

                // 2. Rattache la fenêtre WPF au parent Revit, sans passer par Process.GetCurrentProcess()
                var helper = new System.Windows.Interop.WindowInteropHelper(wnd)
                {
                    Owner = commandData.Application.MainWindowHandle
                };

                // 3. Affichage modal
                bool? result = wnd.ShowDialog();
                if (result != true)
                    return Result.Cancelled;

                // 4. Sauvegarde du choix (chemin + libellé)
                DynamoSettings.SetConfiguration(wnd.SelectedButtonIndex, wnd.SelectedPath, wnd.SelectedLabel);

                string labelPreview = DynamoSettings.GetLabel(wnd.SelectedButtonIndex).Replace("\n", " / ");
                TaskDialog.Show("Fait",
                    $"Le bouton \"{labelPreview}\" utilisera :\n{wnd.SelectedPath}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Affiche le détail de l'exception pour diagnostiquer
                TaskDialog.Show("Erreur inattendue", ex.ToString());
                return Result.Failed;
            }
        }
    }
}

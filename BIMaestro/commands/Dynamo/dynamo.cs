using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Dynamo.Applications;
using Dynamo.Applications.Properties;

namespace Modification
{
    public static class DynamoSettings
    {
        // Chemin vers Documents\RevitLogs\SauvegardePréférence\DynamoPaths.txt
        private static readonly string ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "SauvegardePréférence");
        private static readonly string ConfigFile = Path.Combine(ConfigFolder, "DynamoPaths.txt");
        private static readonly string DefaultPath =
            @"P:\0-Boîte à outils Revit\1-Dynamo\CML_Arases réservations_V24.dyn";

        // Tableau mémoire pour 5 chemins
        private static readonly string[] userPaths = new string[5];

        static DynamoSettings()
        {
            try
            {
                // Crée le dossier si besoin
                if (!Directory.Exists(ConfigFolder))
                    Directory.CreateDirectory(ConfigFolder);

                // Charge le fichier si présent
                if (File.Exists(ConfigFile))
                {
                    var lines = File.ReadAllLines(ConfigFile);
                    for (int i = 0; i < Math.Min(lines.Length, 5); i++)
                        if (!string.IsNullOrWhiteSpace(lines[i]))
                            userPaths[i] = lines[i];
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
                var lines = new string[5];
                for (int i = 0; i < 5; i++)
                    lines[i] = userPaths[i] ?? string.Empty;
                File.WriteAllLines(ConfigFile, lines);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Erreur", "Impossible d'enregistrer la configuration :\n" + ex.Message);
            }
        }

        public static string GetPath(int index)
            => !string.IsNullOrWhiteSpace(userPaths[index]) ? userPaths[index] : DefaultPath;

        public static void SetPath(int index, string path)
        {
            userPaths[index] = path;
            Save();
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

                // 4. Sauvegarde du choix
                DynamoSettings.SetPath(wnd.SelectedButtonIndex, wnd.SelectedPath);
                TaskDialog.Show("Fait",
                    $"Le bouton {wnd.SelectedButtonIndex + 1} utilisera :\n{wnd.SelectedPath}");
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

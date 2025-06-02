using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;

namespace MyRevitPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class PurgeFamilyParametersCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Obtenir le document actif
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Vérifier si le document est une famille
            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show("Erreur", "Ce plugin doit être exécuté dans l'éditeur de familles.");
                return Result.Cancelled;
            }

            // Récupérer uniquement les paramètres personnalisés supprimables
            FamilyManager familyManager = doc.FamilyManager;
            IList<FamilyParameter> allParams = familyManager.GetParameters();
            ObservableCollection<ParameterSelection> candidateParameters = new ObservableCollection<ParameterSelection>();

            foreach (FamilyParameter param in allParams)
            {
                if (param.Definition == null)
                    continue;

                // Vérifier s'il s'agit d'un paramètre interne (BuiltInParameter) de Revit
                bool isBuiltInParam = false;
                string groupName = "Autre";
                if (param.Definition is InternalDefinition internalDef)
                {
                    if (internalDef.BuiltInParameter != BuiltInParameter.INVALID)
                    {
                        isBuiltInParam = true;
                    }
                    // Récupérer le groupe sous forme conviviale
                    groupName = GetFriendlyGroupName(internalDef.ParameterGroup);
                }

                // Déterminer si le paramètre peut être supprimé
                bool canBeDeleted = (!param.IsReadOnly &&
                                     !isBuiltInParam &&
                                     param.Definition.GetParameterTypeName() != "YesNo" &&
                                     !IsParameterUsed(doc, param));

                if (canBeDeleted)
                {
                    candidateParameters.Add(new ParameterSelection
                    {
                        Name = param.Definition.Name,
                        Parameter = param,
                        IsSelected = true, // pré-coché par défaut
                        CanBeDeleted = true,
                        Group = groupName
                    });
                }
            }

            // S'il n'y a aucun paramètre supprimable, informer l'utilisateur et arrêter
            if (candidateParameters.Count == 0)
            {
                TaskDialog.Show("Information", "Aucun paramètre supprimable n'a été trouvé.");
                return Result.Succeeded;
            }

            // Afficher la fenêtre WPF pour la sélection des paramètres
            ParameterSelectionWindow selectionWindow = new ParameterSelectionWindow(candidateParameters);
            System.Windows.Interop.WindowInteropHelper helper = new System.Windows.Interop.WindowInteropHelper(selectionWindow);
            helper.Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

            bool? dialogResult = selectionWindow.ShowDialog();
            if (dialogResult != true)
            {
                // L'utilisateur a annulé la sélection : on ne fait rien
                return Result.Cancelled;
            }

            // Filtrer les paramètres sélectionnés pour suppression
            List<FamilyParameter> parametersToRemove = candidateParameters
                .Where(ps => ps.IsSelected)
                .Select(ps => ps.Parameter)
                .ToList();

            if (parametersToRemove.Count == 0)
            {
                TaskDialog.Show("Information", "Aucun paramètre sélectionné pour la suppression.");
                return Result.Succeeded;
            }

            // Sauvegarder la famille (backup) après confirmation
            string backupPath = GetBackupFilePath(doc);
            try
            {
                SaveAsOptions saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
                doc.SaveAs(backupPath, saveOptions);
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("Erreur", $"Impossible de sauvegarder la famille : {ex.Message}");
                return Result.Cancelled;
            }

            // Purger les paramètres sélectionnés dans une transaction
            int totalParametersRemoved = 0;
            List<string> removedParameterNames = new List<string>();

            using (Transaction trans = new Transaction(doc, "Purger les paramètres"))
            {
                trans.Start();

                foreach (FamilyParameter param in parametersToRemove)
                {
                    try
                    {
                        string paramName = param.Definition.Name;
                        familyManager.RemoveParameter(param);
                        totalParametersRemoved++;
                        removedParameterNames.Add(paramName);
                    }
                    catch (System.Exception)
                    {
                        continue;
                    }
                }

                trans.Commit();
            }

            removedParameterNames.Sort();

            string resultMessage = $"Nombre total de paramètres supprimés : {totalParametersRemoved}";
            if (removedParameterNames.Count > 0)
            {
                resultMessage += "\n\nParamètres supprimés :\n" + string.Join("\n", removedParameterNames);
            }

            TaskDialog.Show("Résultat", resultMessage);
            return Result.Succeeded;
        }

        private string GetBackupFilePath(Document doc)
        {
            string docFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
            string backupFolder = Path.Combine(docFolder, "RevitLogs", "FamilleRevit");

            if (!Directory.Exists(backupFolder))
                Directory.CreateDirectory(backupFolder);

            string familyName;
            string extension;

            if (string.IsNullOrEmpty(doc.PathName))
            {
                familyName = doc.Title;
                if (familyName.EndsWith(".rfa", System.StringComparison.OrdinalIgnoreCase))
                    familyName = familyName.Substring(0, familyName.Length - 4);
                extension = ".rfa";
            }
            else
            {
                familyName = Path.GetFileNameWithoutExtension(doc.PathName);
                extension = Path.GetExtension(doc.PathName);
                if (string.IsNullOrEmpty(extension))
                    extension = ".rfa";
            }

            string backupFilename = $"{familyName}_purger{extension}";
            string backupPath = Path.Combine(backupFolder, backupFilename);
            int counter = 1;
            while (File.Exists(backupPath))
            {
                backupFilename = $"{familyName}_purger_{counter}{extension}";
                backupPath = Path.Combine(backupFolder, backupFilename);
                counter++;
            }
            return backupPath;
        }

        private bool IsParameterUsed(Document familyDoc, FamilyParameter param)
        {
            FilteredElementCollector dimensions = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(Dimension));
            foreach (Dimension dim in dimensions)
            {
                try
                {
                    if (dim.FamilyLabel != null && dim.FamilyLabel.Id == param.Id)
                        return true;

                    if (dim.IsLocked && dim.FamilyLabel != null && dim.FamilyLabel.Id == param.Id)
                        return true;
                }
                catch
                {
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(param.Formula))
                return true;

            FamilyManager familyManager = familyDoc.FamilyManager;
            IList<FamilyParameter> parameters = familyManager.GetParameters();
            foreach (FamilyParameter otherParam in parameters)
            {
                if (otherParam.Id != param.Id && !string.IsNullOrEmpty(otherParam.Formula))
                {
                    if (IsParameterReferencedInFormula(param, otherParam.Formula))
                        return true;
                }
            }
            return false;
        }

        private bool IsParameterReferencedInFormula(FamilyParameter param, string formula)
        {
            string paramName = param.Definition.Name;
            string pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(paramName)}\b";
            return System.Text.RegularExpressions.Regex.IsMatch(formula, pattern);
        }

        // Mapping pour obtenir un intitulé convivial à partir du BuiltInParameterGroup
        private string GetFriendlyGroupName(BuiltInParameterGroup group)
        {
            switch (group)
            {
                case BuiltInParameterGroup.PG_TEXT: return "Texte";
                case BuiltInParameterGroup.PG_CONSTRAINTS: return "Contraintes";
                case BuiltInParameterGroup.PG_GEOMETRY: return "Cotes";
                case BuiltInParameterGroup.PG_DATA: return "Données";
                case BuiltInParameterGroup.PG_IDENTITY_DATA: return "Identité";
                default: return group.ToString();
            }
        }
    }

    // Méthode d'extension pour obtenir le nom du ParameterType sous forme de chaîne via réflexion
    public static class DefinitionExtensions
    {
        public static string GetParameterTypeName(this Definition def)
        {
            PropertyInfo prop = def.GetType().GetProperty("ParameterType", BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop != null)
            {
                object val = prop.GetValue(def, null);
                return val?.ToString() ?? "Invalid";
            }
            return "Invalid";
        }
    }
}

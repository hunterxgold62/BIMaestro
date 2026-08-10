// PurgeFamilyParametersCommand.cs
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using Licensing;
using BIMaestro.Localization;

namespace Famille
{
    [Transaction(TransactionMode.Manual)]
    public class PurgeFamilyParametersCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "PurgeFamilyParametersCommand";


        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T("Ce plugin doit être exécuté dans l'éditeur de familles.", "This plugin must be run in the Family Editor."));
                return Result.Cancelled;
            }

            FamilyManager familyManager = doc.FamilyManager;
            IList<FamilyParameter> allParams = familyManager.GetParameters();
            var candidates = new ObservableCollection<ParameterSelection>();

            foreach (FamilyParameter param in allParams)
            {
                if (param.Definition == null) continue;

                // 1) DÉTECTION EXACTE DES BUILT‑IN PARAMS (comme à l'origine)
                bool isBuiltInParam = false;
                if (param.Definition is InternalDefinition internalDef)
                {
                    if (internalDef.BuiltInParameter != BuiltInParameter.INVALID)
                        isBuiltInParam = true;
                }

                // 2) LECTURE RÉFLEXIVE DU GROUPE (uniquement pour l'affichage)
                string groupName = "Autre";
                var defType = param.Definition.GetType();
                var pgProp = defType.GetProperty("ParameterGroup", BindingFlags.Public | BindingFlags.Instance);
                if (pgProp != null)
                {
                    object raw = pgProp.GetValue(param.Definition, null);
                    groupName = raw?.ToString() ?? "Autre";
                }

                // 3) TYPE DE PARAMÈTRE (exclure les YesNo)
                string typeName = GetParameterTypeName(param.Definition);

                // 4) PARAMÈTRE UTILISÉ ?
                bool used = IsParameterUsed(doc, param);

                // 5) CRITÈRES D'ÉFFAÇABILITÉ (identiques à l'origine)
                bool canBeDeleted = !param.IsReadOnly
                                    && !isBuiltInParam
                                    && typeName != "YesNo"
                                    && !used;

                if (canBeDeleted)
                {
                    candidates.Add(new ParameterSelection
                    {
                        Name = param.Definition.Name,
                        Parameter = param,
                        IsSelected = true,
                        CanBeDeleted = true,
                        Group = groupName
                    });
                }
            }

            if (candidates.Count == 0)
            {
                TaskDialog.Show(UiLanguage.T("Information", "Information"), UiLanguage.T("Aucun paramètre supprimable n'a été trouvé.", "No removable parameter was found."));
                return Result.Succeeded;
            }

            // 6) AFFICHAGE DE LA FENÊTRE DE SÉLECTION
            var selectionWindow = new ParameterSelectionWindow(candidates);
            var helper = new System.Windows.Interop.WindowInteropHelper(selectionWindow)
            {
                Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle
            };
            if (selectionWindow.ShowDialog() != true)
                return Result.Cancelled;

            // 7) RÉCUPÉRATION & PURGE
            var toRemove = candidates.Where(ps => ps.IsSelected)
                                     .Select(ps => ps.Parameter)
                                     .ToList();

            if (toRemove.Count == 0)
            {
                TaskDialog.Show(UiLanguage.T("Information", "Information"), UiLanguage.T("Aucun paramètre sélectionné pour la suppression.", "No parameter was selected for deletion."));
                return Result.Succeeded;
            }

            // 8) BACKUP
            string backupPath = GetBackupFilePath(doc);
            try
            {
                doc.SaveAs(backupPath, new SaveAsOptions { OverwriteExistingFile = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T($"Impossible de sauvegarder la famille : {ex.Message}", $"Unable to save the family: {ex.Message}"));
                return Result.Cancelled;
            }

            // 9) TRANSACTION DE PURGE
            var removedNames = new List<string>();
            using (var t = new Transaction(doc, "Purger les paramètres"))
            {
                t.Start();
                foreach (var p in toRemove)
                {
                    try
                    {
                        removedNames.Add(p.Definition.Name);
                        familyManager.RemoveParameter(p);
                    }
                    catch { }
                }
                t.Commit();
            }

            removedNames.Sort();
            string resultMsg = UiLanguage.T($"Nombre total de paramètres supprimés : {removedNames.Count}", $"Total parameters deleted: {removedNames.Count}");
            if (removedNames.Count > 0)
                resultMsg += UiLanguage.T("\n\nParamètres supprimés :\n", "\n\nDeleted parameters:\n") + string.Join("\n", removedNames);

            TaskDialog.Show(UiLanguage.T("Résultat", "Result"), resultMsg);
            return Result.Succeeded;
        }

        #region Helpers

        private string GetBackupFilePath(Document doc)
        {
            string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string backupFolder = Path.Combine(myDocs, "RevitLogs", "FamilleRevit");
            if (!Directory.Exists(backupFolder))
                Directory.CreateDirectory(backupFolder);

            string baseName = string.IsNullOrEmpty(doc.PathName)
                ? Path.GetFileNameWithoutExtension(doc.Title)
                : Path.GetFileNameWithoutExtension(doc.PathName);

            string ext = ".rfa";
            string path = Path.Combine(backupFolder, baseName + "_purger" + ext);
            int i = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(backupFolder, $"{baseName}_purger_{i}{ext}");
                i++;
            }
            return path;
        }

        private bool IsParameterUsed(Document doc, FamilyParameter param)
        {
            // Vérifier dans les cotes
            foreach (Dimension d in new FilteredElementCollector(doc).OfClass(typeof(Dimension)))
            {
                try
                {
                    if (d.FamilyLabel?.Id == param.Id)
                        return true;
                }
                catch { }
            }

            // Vérifier formule sur le paramètre
            if (!string.IsNullOrEmpty(param.Formula))
                return true;

            // Vérifier références dans d'autres formules
            var fm = doc.FamilyManager;
            foreach (var other in fm.GetParameters())
            {
                if (other.Id != param.Id && !string.IsNullOrEmpty(other.Formula))
                {
                    if (IsReferencedInFormula(param, other.Formula))
                        return true;
                }
            }
            return false;
        }

        private bool IsReferencedInFormula(FamilyParameter param, string formula)
        {
            string name = param.Definition.Name;
            string pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b";
            return System.Text.RegularExpressions.Regex.IsMatch(formula, pattern);
        }

        private string GetParameterTypeName(Definition def)
        {
            // Lecture réflexive de "ParameterType"
            PropertyInfo prop = def.GetType()
                                   .GetProperty("ParameterType",
                                                BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop != null)
            {
                object val = prop.GetValue(def, null);
                return val?.ToString() ?? "Invalid";
            }
            return "Invalid";
        }

        #endregion
    }
}

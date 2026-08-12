// DeleteUnusedFamiliesHardCommand.cs
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

namespace Modification
{
    /// <summary>
    /// Préprocesseur d'échecs qui supprime tous les messages de sévérité Warning.
    /// </summary>
    class WarningSuppressor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var msg in accessor.GetFailureMessages()
                                       .Where(f => f.GetSeverity() == FailureSeverity.Warning))
            {
                accessor.DeleteWarning(msg);
            }
            return FailureProcessingResult.Continue;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class DeleteUnusedFamiliesHardCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 1) Répertoire des symboles instanciés (FamilyInstance)
            var usedSymbolIds = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Select(fi => fi.Symbol.Id)
                .ToHashSet();

            // 2) Détection des system-families (Railings, Stairs)
            bool hasRailings = new FilteredElementCollector(doc).OfClass(typeof(Railing)).Any();
            bool hasStairs = new FilteredElementCollector(doc).OfClass(typeof(Stairs)).Any();

            // 3) Liste des catégories système à exclure
            var excludedCats = new HashSet<ElementId>();
            if (hasRailings) excludedCats.Add(new ElementId(BuiltInCategory.OST_Railings));
            if (hasStairs) excludedCats.Add(new ElementId(BuiltInCategory.OST_Stairs));
            excludedCats.Add(new ElementId(BuiltInCategory.OST_GenericAnnotation));
            excludedCats.Add(new ElementId(BuiltInCategory.OST_Dimensions));

            // 4) Collecte des familles chargées (.rfa), éditables, non in-place, Model, hors exclusions
            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f =>
                    f.IsEditable &&
                    !f.IsInPlace &&
                    f.FamilyCategory != null &&
                    f.FamilyCategory.CategoryType == CategoryType.Model &&
                    !excludedCats.Contains(f.FamilyCategory.Id)
                )
                .ToList();

            // 5) Filtrage des familles vraiment non utilisées
            var toDelete = candidates
                .Where(f => !f.GetFamilySymbolIds().Any(id => usedSymbolIds.Contains(id)))
                .OrderBy(f => f.Name, StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            if (!toDelete.Any())
            {
                TaskDialog.Show(UiLanguage.T("Familles inutilisées", "Unused Families"),
                                UiLanguage.T("Aucune famille chargée inutilisée trouvée.", "No unused loaded family was found."));
                return Result.Succeeded;
            }

            // 6) Affichage complet des noms à l'utilisateur
            string allNames = string.Join(Environment.NewLine,
                toDelete.Select(f => f.Name));
            var confirm = new TaskDialog(UiLanguage.T("Familles inutilisées détectées", "Unused Families Detected"))
            {
                MainInstruction = UiLanguage.T($"Vous allez supprimer {toDelete.Count} famille(s) inutilisée(s):", $"You are about to delete {toDelete.Count} unused family(ies):"),
                MainContent = allNames,
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel,
                AllowCancellation = true,
                MainIcon = TaskDialogIcon.TaskDialogIconWarning
            };
            if (confirm.Show() != TaskDialogResult.Yes)
                return Result.Succeeded;

            // 7) Transaction avec suppression des warnings
            var tx = new Transaction(doc, "Supprimer familles inutilisées");
            var opts = tx.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(new WarningSuppressor());
            opts.SetClearAfterRollback(true);
            opts.SetForcedModalHandling(false);
            tx.SetFailureHandlingOptions(opts);

            tx.Start();
            foreach (var fam in toDelete)
            {
                try { doc.Delete(fam.Id); }
                catch { /* ignorer */ }
            }
            tx.Commit();

            TaskDialog.Show(UiLanguage.T("Suppression terminée", "Deletion Completed"),
                            UiLanguage.T($"{toDelete.Count} famille(s) supprimée(s).", $"{toDelete.Count} family(ies) deleted."));
            return Result.Succeeded;
        }
    }
}

// DeleteUnusedSchedulesCommand.cs
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class DeleteUnusedSchedulesCommand : IExternalCommand
    {

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 0) Simule l'entrée booléenne IN[0] du Python
            bool runCode = true;
            if (!runCode)
                return Result.Succeeded;

            // 1) Collecte de toutes les vues de nomenclature (éléments, pas types)
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .WhereElementIsNotElementType()    // <-- comme en Python
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate)
                .ToList();

            // 2) Instances placées sur feuille
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .ToList();

            // 3) IDs des nomenclatures réellement utilisées
            var usedIds = instances
                .Select(inst => inst.ScheduleId)
                .ToHashSet();

            // 4) Candidats de suppression : non placées + supprimables en sécurité.
            var unplacedCandidates = schedules
                .Where(s => !usedIds.Contains(s.Id))
                .ToList();

            var finalList = unplacedCandidates
                .Where(CanBeDeletedSafely)
                .Where(s => !HasAnySheetInstance(doc, s.Id))
                .ToList();

            

            if (finalList.Count == 0)
            {
                TaskDialog.Show(UiLanguage.T("Supprimer nomenclatures inutilisées", "Delete Unused Schedules"),
                    UiLanguage.T("Aucune nomenclature non placée à supprimer.", "No unplaced schedule was found to delete."));
                return Result.Succeeded;
            }

            // 6) Confirmation utilisateur
            var dlg = new TaskDialog(UiLanguage.T("Supprimer nomenclatures inutilisées", "Delete Unused Schedules"))
            {
                MainInstruction = UiLanguage.T($"Vous allez supprimer {finalList.Count} nomenclature(s) non placée(s).", $"You are about to delete {finalList.Count} unplaced schedule(s)."),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
            };
            if (dlg.Show() != TaskDialogResult.Yes)
                return Result.Succeeded;

            // 7) Suppression en transaction
            int deletedCount = 0;
            int skippedCount = 0;

            using (var tx = new Transaction(doc, "Supprimer nomenclatures non utilisées"))
            {
                tx.Start();
                foreach (var s in finalList)
                {
                    try
                    {
                        // Double-vérification de sécurité juste avant suppression
                        // pour éviter de toucher une nomenclature finalement placée.
                        if (HasAnySheetInstance(doc, s.Id))
                        {
                            skippedCount++;
                            continue;
                        }

                        // Garde-fou fort :
                        // on annule la suppression si elle impacte des instances de nomenclatures
                        // qui ne dépendent pas de la nomenclature cible.
                        if (TryDeleteWithoutAffectingOtherPlacedSchedules(doc, s.Id))
                        {
                            deletedCount++;
                        }
                        else
                        {
                            skippedCount++;
                        }
                    }
                    catch
                    {
                        // En cas d'échec, on n'insiste pas.
                        skippedCount++;
                    }
                }
                tx.Commit();
            }

            TaskDialog.Show(UiLanguage.T("Supprimer nomenclatures inutilisées", "Delete Unused Schedules"),
                UiLanguage.T($"{deletedCount} nomenclature(s) supprimée(s)." +
                             (skippedCount > 0 ? $"\n{skippedCount} nomenclature(s) ignorée(s) par sécurité." : string.Empty),
                             $"{deletedCount} schedule(s) deleted." +
                             (skippedCount > 0 ? $"\n{skippedCount} schedule(s) skipped for safety." : string.Empty)));
            return Result.Succeeded;
        }

        private static bool CanBeDeletedSafely(ViewSchedule schedule)
        {
            // Ne jamais supprimer les nomenclatures systèmes sensibles.
            if (schedule.IsTitleblockRevisionSchedule)
                return false;

            // Sur certaines versions d'API Revit, ViewSchedule n'expose pas CanBeDeleted.
            // On se limite donc à des règles robustes compatibles :
            // - non nomenclature système de révision (ci-dessus)
            // - filtrage amont : non placée
            return true;
        }

        private static bool HasAnySheetInstance(Document doc, ElementId scheduleId)
        {
            // Vérification 1 : toutes les instances de nomenclature en feuille.
            bool hasDirectInstance = new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Any(i => i.ScheduleId == scheduleId);

            if (hasDirectInstance)
                return true;

            // Vérification 2 : dépendances Revit de la nomenclature.
            // Cela couvre certains cas où la relation n'est pas reflétée comme attendu dans ScheduleId.
            Element schedule = doc.GetElement(scheduleId);
            if (schedule == null)
                return false;

            var dependentIds = schedule.GetDependentElements(new ElementClassFilter(typeof(ScheduleSheetInstance)));
            return dependentIds != null && dependentIds.Count > 0;
        }

        private static bool TryDeleteWithoutAffectingOtherPlacedSchedules(Document doc, ElementId scheduleId)
        {
            var before = GetPlacedInstanceMap(doc);

            using (var st = new SubTransaction(doc))
            {
                st.Start();
                doc.Delete(scheduleId);

                var after = GetPlacedInstanceMap(doc);

                // Toute instance placée qui disparaît doit appartenir à la nomenclature supprimée.
                foreach (var kvp in before)
                {
                    ElementId instanceId = kvp.Key;
                    ElementId ownerScheduleId = kvp.Value;

                    bool stillExists = after.ContainsKey(instanceId);
                    if (!stillExists && ownerScheduleId != scheduleId)
                    {
                        st.RollBack();
                        return false;
                    }
                }

                st.Commit();
                return true;
            }
        }

        private static Dictionary<ElementId, ElementId> GetPlacedInstanceMap(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .ToDictionary(i => i.Id, i => i.ScheduleId);
        }
    }
}

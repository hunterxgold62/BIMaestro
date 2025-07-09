// DeleteUnusedSchedulesCommand.cs
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MyRevitAddin.Commands
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

            // 4) Filtrer celles **jamais** placées
            var unused = schedules
                .Where(s => !usedIds.Contains(s.Id))
                .ToList();

            // 5) Filtrer par nom (méthode String.Contains du Dynamo)
            //    = ne conserver que celles dont le nom contient "Interne"
            string searchFor = "Interne";
            var finalList = unused
                .Where(s => s.Name.IndexOf(searchFor, StringComparison.InvariantCultureIgnoreCase) >= 0)
                .ToList();

            if (finalList.Count == 0)
            {
                TaskDialog.Show("Supprimer nomenclatures inutilisées",
                    "Aucune nomenclature non utilisée répondant au filtre à supprimer.");
                return Result.Succeeded;
            }

            // 6) Confirmation utilisateur
            var dlg = new TaskDialog("Supprimer nomenclatures inutilisées")
            {
                MainInstruction = $"Vous allez supprimer {finalList.Count} nomenclature(s) non utilisée(s).",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
            };
            if (dlg.Show() != TaskDialogResult.Yes)
                return Result.Succeeded;

            // 7) Suppression en transaction
            using (var tx = new Transaction(doc, "Supprimer nomenclatures non utilisées"))
            {
                tx.Start();
                foreach (var s in finalList)
                {
                    try { doc.Delete(s.Id); }
                    catch { /* ignore si impossible */ }
                }
                tx.Commit();
            }

            TaskDialog.Show("Supprimer nomenclatures inutilisées",
                $"{finalList.Count} nomenclature(s) supprimée(s).");
            return Result.Succeeded;
        }
    }
}

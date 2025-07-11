// DeleteUnplacedViewsCommand.cs
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Modification
{ 
    [Transaction(TransactionMode.Manual)]
    public class DeleteUnplacedViewsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            // 1) Toutes les vues placées sur une feuille (via Viewport)
            var placedIds = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .Select(vp => vp.ViewId)
                .ToHashSet();

            // 2) Toutes les vues FloorPlan, CeilingPlan, EngineeringPlan, Section,
            //    ThreeD, Detail (callouts), Rendering (hors templates)
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Where(v =>
                    v.ViewType == ViewType.FloorPlan ||
                    v.ViewType == ViewType.CeilingPlan ||
                    v.ViewType == ViewType.EngineeringPlan ||
                    v.ViewType == ViewType.Section ||
                    v.ViewType == ViewType.ThreeD ||
                    v.ViewType == ViewType.Detail ||
                    v.ViewType == ViewType.Rendering
                )
                .ToList(); // <-- nécessaire pour que .Count soit une propriété

            // 3) Filtrer celles qui ne sont pas placées ET ne pas toucher à la vue active
            var activeId = doc.ActiveView.Id;
            var toDelete = allViews
                .Where(v => !placedIds.Contains(v.Id) && v.Id != activeId)
                .ToList(); // <-- idem

            if (toDelete.Count == 0)
            {
                TaskDialog.Show("Supprimer vues non implantées",
                    "Aucune vue plan/coupe/3D/detail/rendering non implantée à supprimer.");
                return Result.Succeeded;
            }

            // 4) Confirmation avant suppression
            var dlg = new TaskDialog("Supprimer vues non implantées")
            {
                MainInstruction = $"Vous allez supprimer {toDelete.Count} vue(s) non implantée(s).",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
            };
            if (dlg.Show() != TaskDialogResult.Yes)
                return Result.Succeeded;

            // 5) Transaction & suppression
            using (var tx = new Transaction(doc, "Supprimer vues non implantées"))
            {
                tx.Start();
                foreach (var v in toDelete)
                {
                    try { doc.Delete(v.Id); }
                    catch { /* ignore si impossible */ }
                }
                tx.Commit();
            }

            TaskDialog.Show("Supprimer vues non implantées",
                $"{toDelete.Count} vue(s) supprimée(s).");
            return Result.Succeeded;
        }
    }
}

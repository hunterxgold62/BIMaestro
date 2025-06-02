using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;
using System.Collections.Generic;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class OverrideColorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            // 1) Vérifier la sélection d’éléments
            var selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show("Erreur", "Veuillez sélectionner au moins un élément avant d’appliquer une opération.");
                return Result.Failed;
            }

            // 2) Ouvrir la fenêtre de réglages
            var picker = new ColorPickerWindow(uiapp);
            bool? result = picker.ShowDialog();
            if (result != true)
                return Result.Cancelled;

            // 3) Si le reset a été demandé, on termine ici
            if (picker.IsResetRequested)
                return Result.Succeeded;

            // 4) Sinon, on applique les overrides classiques
            var views = picker.SelectedViews;
            if (views == null || views.Count == 0)
            {
                TaskDialog.Show("Erreur", "Aucune vue sélectionnée.");
                return Result.Cancelled;
            }

            var ogs = picker.GetOverrideGraphicSettings();

            using (var tx = new Transaction(doc, "Override Color and Patterns"))
            {
                tx.Start();
                foreach (var view in views)
                {
                    foreach (var id in selectedIds)
                    {
                        if (id == ElementId.InvalidElementId) continue;
                        try
                        {
                            if (picker.HideInView)
                                view.HideElements(new List<ElementId> { id });
                            else
                                view.SetElementOverrides(id, ogs);
                        }
                        catch { /* on ignore les vues non supportées */ }
                    }
                }
                tx.Commit();
            }

            return Result.Succeeded;
        }
    }
}

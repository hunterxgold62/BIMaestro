using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;
using BIMaestro.Localization;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class ResetOverridesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            var selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T("Aucun élément sélectionné. Sélectionnez d’abord les éléments à réinitialiser.", "No Element Selected. First Select the Elements to Reset."));
                return Result.Failed;
            }

            // Correction : Récupérer toutes les vues non-template sans filtrer par type
            var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate);

            using (Transaction tx = new Transaction(doc, "Reset Graphic Overrides"))
            {
                tx.Start();

                foreach (var view in views)
                {
                    foreach (var selectedId in selectedIds)
                    {
                        if (view != null && selectedId != ElementId.InvalidElementId)
                        {
                            try
                            {
                                view.SetElementOverrides(selectedId, new OverrideGraphicSettings());
                            }
                            catch (Exception)
                            {
                                // Ignorer les exceptions
                            }
                        }
                    }
                }

                tx.Commit();
            }

            return Result.Succeeded;
        }
    }
}

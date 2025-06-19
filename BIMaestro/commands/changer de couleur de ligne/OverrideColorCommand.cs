using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

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
            var selIds = uidoc.Selection.GetElementIds();

            if (selIds.Count == 0)
            {
                TaskDialog.Show("Erreur", "Veuillez sélectionner au moins un élément avant d’appliquer une opération.");
                return Result.Failed;
            }

            var picker = new ColorPickerWindow(uiapp);
            bool? ok = picker.ShowDialog();
            if (ok != true) return Result.Cancelled;
            if (picker.IsResetRequested) return Result.Succeeded;

            var views = picker.SelectedViews;
            if (views.Count == 0)
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
                    foreach (var id in selIds)
                    {
                        try
                        {
                            if (picker.HideInView)
                                view.HideElements(new List<ElementId> { id });
                            else
                                view.SetElementOverrides(id, ogs);
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                        {
                            // ignore vues non supportées
                        }
                        catch
                        {
                            // ignore autres erreurs
                        }
                    }
                }
                tx.Commit();
            }

            return Result.Succeeded;
        }
    }
}

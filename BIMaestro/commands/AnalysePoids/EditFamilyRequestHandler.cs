using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using BIMaestro.Localization;

namespace Analyse
{
    public class SelectionRequestHandler : IExternalEventHandler
    {
        // Nouvelle propriété pour plusieurs IDs
        public IList<ElementId> ElementIds { get; set; }

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (ElementIds != null && ElementIds.Any())
            {
                uidoc.Selection.SetElementIds(ElementIds);
                uidoc.ShowElements(ElementIds);
            }
        }

        public string GetName() => "SelectionRequestHandler";
    }

    public class DeleteElementRequestHandler : IExternalEventHandler
    {
        public IList<ElementId> ElementIds { get; set; }
        public Action<bool> OnCompleted { get; set; }

        public void Execute(UIApplication app)
        {
            bool success = false;

            try
            {
                UIDocument uidoc = app.ActiveUIDocument;
                Document doc = uidoc?.Document;
                if (doc == null)
                {
                    return;
                }

                var idsToDelete = ElementIds?
                    .Where(id => id != null)
                    .ToList();
                if (idsToDelete == null || idsToDelete.Count == 0)
                {
                    TaskDialog.Show(UiLanguage.T("Suppression", "Deletion"), UiLanguage.T("Aucun élément à supprimer n'a été fourni.", "No element was provided for deletion."));
                    return;
                }

                using (var tx = new Transaction(doc, "Supprimer élément"))
                {
                    tx.Start();
                    doc.Delete(idsToDelete);
                    tx.Commit();
                    success = true;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.T("Suppression", "Deletion"), UiLanguage.T($"Erreur lors de la suppression : {ex.Message}", $"Error while deleting: {ex.Message}"));
            }
            finally
            {
                OnCompleted?.Invoke(success);
                ElementIds = null;
                OnCompleted = null;
            }
        }

        public string GetName() => "DeleteElementRequestHandler";
    }
}

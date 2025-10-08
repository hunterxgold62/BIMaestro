using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

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

    public class DeleteFamilyRequestHandler : IExternalEventHandler
    {
        public ElementId FamilyId { get; set; }
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

                if (FamilyId == null)
                {
                    TaskDialog.Show("Suppression famille", "Aucune famille à supprimer n'a été fournie.");
                    return;
                }

                using (var tx = new Transaction(doc, "Supprimer famille"))
                {
                    tx.Start();
                    doc.Delete(FamilyId);
                    tx.Commit();
                    success = true;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Suppression famille", $"Erreur lors de la suppression : {ex.Message}");
            }
            finally
            {
                OnCompleted?.Invoke(success);
                FamilyId = null;
                OnCompleted = null;
            }
        }

        public string GetName() => "DeleteFamilyRequestHandler";
    }
}
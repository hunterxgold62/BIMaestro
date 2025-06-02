using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.UI.Selection;

namespace MyRevitPlugin
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class MainCommand : IExternalCommand
    {
        public Result Execute(
          ExternalCommandData commandData,
          ref string message,
          ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Créer le menu d'options
            TaskDialog mainDialog = new TaskDialog("Select Command");
            mainDialog.MainInstruction = "Que voulez-vous faire ?";
            mainDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Qui a créé la vue active ?");
            mainDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Qui a créé l'élément sélectionné ?");
            mainDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Afficher la dernière synchronisation du modèle");

            TaskDialogResult result = mainDialog.Show();

            switch (result)
            {
                case TaskDialogResult.CommandLink1:
                    WhoCreatedActiveView(commandData);
                    break;
                case TaskDialogResult.CommandLink2:
                    WhoCreatedSelection(commandData);
                    break;
                case TaskDialogResult.CommandLink3:
                    WhoDidLastSync(commandData);
                    break;
                default:
                    return Result.Cancelled;
            }

            return Result.Succeeded;
        }

        private void WhoCreatedActiveView(ExternalCommandData commandData)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = uidoc.ActiveView;

            // Vérifier si le document est partagé
            if (!IsWorkshared(doc)) return;

            // Récupérer les informations de worksharing
            WorksharingTooltipInfo worksharingInfo = WorksharingUtils.GetWorksharingTooltipInfo(doc, activeView.Id);

            // Afficher les informations du créateur et du dernier utilisateur
            if (worksharingInfo != null)
            {
                string creator = !string.IsNullOrEmpty(worksharingInfo.Creator) ? worksharingInfo.Creator : "Non disponible";
                string lastChangedBy = !string.IsNullOrEmpty(worksharingInfo.LastChangedBy) ? worksharingInfo.LastChangedBy : "Non disponible";

                TaskDialog.Show("Active View Info",
                    $"Créateur de la vue active : {creator}\n" +
                    $"Dernière modification par : {lastChangedBy}");
            }
            else
            {
                TaskDialog.Show("Active View Info", "Impossible de récupérer les informations de worksharing pour la vue active.");
            }
        }



        private void WhoCreatedSelection(ExternalCommandData commandData)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            Selection selection = uidoc.Selection;

            if (!IsWorkshared(doc)) return;

            if (selection.GetElementIds().Count == 1)
            {
                ElementId selectedId = selection.GetElementIds().First();
                Element selectedElement = doc.GetElement(selectedId);
                WorksharingTooltipInfo worksharingInfo = WorksharingUtils.GetWorksharingTooltipInfo(doc, selectedId);

                TaskDialog.Show("Selection Info", $"Créateur : {worksharingInfo.Creator}\nPropriétaire actuel : {worksharingInfo.Owner}\nDernière modification par : {worksharingInfo.LastChangedBy}");
            }
            else
            {
                TaskDialog.Show("Erreur", "Un seul élément doit être sélectionné.");
            }
        }

        private void WhoDidLastSync(ExternalCommandData commandData)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            if (!IsWorkshared(doc)) return;

            WorksharingTooltipInfo worksharingInfo = WorksharingUtils.GetWorksharingTooltipInfo(doc, doc.ActiveView.Id);
            TaskDialog.Show("Dernière Synchronisation", $"Dernière synchronisation effectuée par : {worksharingInfo.LastChangedBy}");
        }

        // Méthode utilitaire pour vérifier si un document est partagé
        private bool IsWorkshared(Document doc)
        {
            if (!doc.IsWorkshared)
            {
                TaskDialog.Show("Erreur", "Le modèle n'est pas partagé.");
                return false;
            }
            return true;
        }
    }
}

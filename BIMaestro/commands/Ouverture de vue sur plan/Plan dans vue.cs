using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using System.Linq;

namespace Visualisation
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class OpenSheetFromView : IExternalCommand
    {
        public Result Execute(
          ExternalCommandData commandData,
          ref string message,
          ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Obtenir la vue active (celle sur laquelle vous travaillez actuellement)
                View activeView = uidoc.ActiveView;

                // Liste des types de vues supportées
                var supportedViewTypes = new[] { typeof(ViewPlan), typeof(ViewSection), typeof(View3D) };

                // Vérification de la vue active
                if (!supportedViewTypes.Contains(activeView.GetType()) && !(activeView is ViewSheet))
                {
                    ShowError("Cette commande ne fonctionne que sur des vues de plan, coupe, 3D ou des feuilles.");
                    return Result.Failed;
                }

                // Si la vue active est une feuille
                if (activeView is ViewSheet)
                {
                    // Si on est sur une feuille, essayer de sélectionner un viewport
                    Selection sel = uidoc.Selection;
                    Reference pickedRef = sel.PickObject(ObjectType.Element, "Sélectionnez un viewport dans la feuille.");

                    if (pickedRef != null)
                    {
                        // Obtenir l'élément sélectionné
                        Element elem = doc.GetElement(pickedRef);
                        if (elem is Viewport viewport)
                        {
                            // Obtenir la vue associée à ce viewport
                            ElementId viewId = viewport.ViewId;
                            View view = doc.GetElement(viewId) as View;

                            // Ouvrir la vue associée
                            if (view != null)
                            {
                                uidoc.ActiveView = view;
                            }
                            else
                            {
                                ShowError("Impossible d'ouvrir la vue associée.");
                                return Result.Failed;
                            }
                        }
                        else
                        {
                            ShowError("Veuillez sélectionner un viewport valide.");
                            return Result.Failed;
                        }
                    }
                    else
                    {
                        ShowError("Aucun viewport sélectionné.");
                        return Result.Failed;
                    }
                }
                else
                {
                    // Si on est sur une vue de plan/coupe/3D, rechercher les feuilles qui contiennent cette vue
                    var sheetsWithViewports = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(sheet => sheet.GetAllViewports().Count > 0);

                    // Puis rechercher les feuilles qui contiennent la vue active
                    var sheetsWithView = sheetsWithViewports.Where(sheet => sheet.GetAllViewports().Any(vpId =>
                    {
                        Viewport vp = doc.GetElement(vpId) as Viewport;
                        return vp != null && vp.ViewId == activeView.Id;
                    }));

                    // Vérifier si la vue est présente dans une feuille
                    if (sheetsWithView.Any())
                    {
                        // Si la feuille contient un seul viewport, l'ouvrir directement
                        if (sheetsWithView.Count() == 1)
                        {
                            ViewSheet sheet = sheetsWithView.First();
                            uidoc.ActiveView = sheet;
                        }
                        else
                        {
                            // Ouvrir la première feuille trouvée
                            ViewSheet sheet = sheetsWithView.First();
                            uidoc.ActiveView = sheet;
                        }
                    }
                    else
                    {
                        ShowError("Cette vue n'est pas placée sur une feuille.");
                        return Result.Failed;
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Gestion des erreurs imprévues
                ShowError("Une erreur inattendue s'est produite : " + ex.Message);
                return Result.Failed;
            }
        }

        // Méthode pour afficher des messages d'erreur
        private void ShowError(string message)
        {
            TaskDialog.Show("Erreur", message);
        }
    }
}

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;

namespace Visualisation
{
    [Transaction(TransactionMode.Manual)]
    public class ReorientViewCommand : IExternalCommand
    {
        public Result Execute(
          ExternalCommandData commandData,
          ref string message,
          ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            // Vérifie si la vue active est une vue 3D
            if (activeView is View3D view3D)
            {
                try
                {
                    // Sélectionner une face
                    Reference pickedRef = uidoc.Selection.PickObject(ObjectType.Face, "Sélectionne une face à orienter");
                    Element selectedElement = doc.GetElement(pickedRef.ElementId);
                    GeometryObject geomObj = selectedElement?.GetGeometryObjectFromReference(pickedRef);

                    // Valider que l'objet sélectionné est bien une face
                    if (geomObj is Face face)
                    {
                        using (Transaction trans = new Transaction(doc, "Orienter sur la face sélectionnée"))
                        {
                            trans.Start();
                            OrientViewToFace(view3D, face, selectedElement); // Passer l'élément sélectionné
                            trans.Commit();
                        }
                    }
                    else
                    {
                        TaskDialog.Show("Erreur", "L'objet sélectionné n'est pas une face. Réessaie avec une surface valide.");
                        return Result.Failed;
                    }

                    uidoc.RefreshActiveView();
                    return Result.Succeeded;
                }
                catch (OperationCanceledException)
                {
                    // Gestion de l'annulation par l'utilisateur
                    TaskDialog.Show("Annulé", "Opération annulée. Pas de souci, tu peux réessayer à tout moment !");
                    return Result.Cancelled;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    // Gestion des erreurs spécifiques à Revit
                    message = "Erreur lors de la sélection de la face : " + ex.Message;
                    return Result.Failed;
                }
                catch (Exception ex)
                {
                    // Gestion des erreurs génériques
                    message = "Une erreur inattendue est survenue : " + ex.Message;
                    return Result.Failed;
                }
            }
            else
            {
                // Message d'erreur si la vue n'est pas 3D
                TaskDialog.Show("Oups !", "Heeey, tu dois être sur une vue 3D pour que ça marche !");
                return Result.Failed;
            }
        }

        /// <summary>
        /// Oriente la vue 3D vers la face sélectionnée.
        /// </summary>
        /// <param name="view3D">Vue 3D active</param>
        /// <param name="face">Face sélectionnée</param>
        /// <param name="selectedElement">Élément sélectionné</param>
        private void OrientViewToFace(View3D view3D, Face face, Element selectedElement)
        {
            // Calcul de la normale de la face au centre pour déterminer la direction de la caméra
            BoundingBoxUV boundingBoxUV = face.GetBoundingBox();
            UV centerUV = (boundingBoxUV.Min + boundingBoxUV.Max) / 2;
            XYZ normalVec = face.ComputeNormal(centerUV);

            // Récupère le centre de la face comme origine pour la vue
            XYZ origin = face.Evaluate(centerUV);

            // Récupère la taille de l'élément pour ajuster la distance de la caméra
            BoundingBoxXYZ elementBoundingBox = selectedElement.get_BoundingBox(null);
            double elementSize = elementBoundingBox != null ? elementBoundingBox.Max.DistanceTo(elementBoundingBox.Min) : 10.0;

            // Définit la distance en fonction de la taille de l'élément
            double distance = elementSize;

            // Positionne la caméra à une distance appropriée de la face sélectionnée
            XYZ eyePosition = origin + distance * normalVec; // Changement de signe

            // Détermine la direction "forward" et "up" pour la caméra
            XYZ viewForward = (origin - eyePosition).Normalize(); // La caméra regarde vers l'origine
            XYZ viewUp;

            // Si la normale est presque parallèle à l'axe Z, on choisit un vecteur horizontal pour "viewUp"
            if (Math.Abs(normalVec.DotProduct(XYZ.BasisZ)) > 0.99)
            {
                viewUp = XYZ.BasisX;
            }
            else
            {
                viewUp = XYZ.BasisZ;
            }

            // Correction du vecteur "up" pour qu'il soit perpendiculaire au vecteur "forward"
            viewUp = viewForward.CrossProduct(viewUp).Normalize().CrossProduct(viewForward).Normalize();

            // Applique l'orientation calculée à la vue 3D
            ViewOrientation3D orientation = new ViewOrientation3D(eyePosition, viewUp, viewForward);
            view3D.SetOrientation(orientation);
        }
    }
}
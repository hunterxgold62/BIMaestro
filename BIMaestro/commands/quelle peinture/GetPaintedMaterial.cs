using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Visualisation
{
    [Transaction(TransactionMode.Manual)]
    public class GetPaintedMaterialsCommand : IExternalCommand
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
                // 1) Sélection de l'élément
                Reference pickedRef = uidoc.Selection.PickObject(ObjectType.Element, "Sélectionnez un élément");
                Element elem = doc.GetElement(pickedRef.ElementId);

                // 2) Récupération des matériaux de base et de peinture
                var baseMaterialsIds = elem.GetMaterialIds(false);
                var allMaterialsIds = elem.GetMaterialIds(true);
                var paintedMaterialIds = allMaterialsIds.Except(baseMaterialsIds);

                // 3) Création des listes de Material
                List<Material> objectMaterials = baseMaterialsIds
                    .Select(id => doc.GetElement(id) as Material)
                    .Where(m => m != null)
                    .Distinct()
                    .ToList();

                List<Material> paintedMaterials = paintedMaterialIds
                    .Select(id => doc.GetElement(id) as Material)
                    .Where(m => m != null)
                    .Distinct()
                    .ToList();

                // 4) Affichage
                var sb = new StringBuilder();

                if (objectMaterials.Count > 0)
                {
                    sb.AppendLine("Matériaux présents sur l'objet :");
                    foreach (var m in objectMaterials)
                        sb.AppendLine($" - {m.Name}");
                }
                else
                {
                    sb.AppendLine("Aucun matériau présent directement sur l'objet.");
                }

                if (paintedMaterials.Count > 0)
                {
                    sb.AppendLine("\nMatériaux de peinture trouvés :");
                    foreach (var m in paintedMaterials)
                        sb.AppendLine($" - {m.Name}");
                }
                else
                {
                    sb.AppendLine("\nAucun matériau de peinture trouvé.");
                }

                TaskDialog.Show("Matériaux de l'élément", sb.ToString());
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            return Result.Succeeded;
        }
    }
}

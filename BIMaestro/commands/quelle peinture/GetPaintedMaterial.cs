using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Visualisation
{
    [Transaction(TransactionMode.Manual)]
    public class GetPaintedMaterialsCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "GetPaintedMaterialsCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1) Sélection de l'élément
                Reference pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    UiLanguage.T("Sélectionnez un élément", "Select an element"));
                Element elem = doc.GetElement(pickedRef.ElementId);

                // 2) Récupération des matériaux de base et de peinture
                var baseMaterialsIds = elem.GetMaterialIds(false);
                var paintedMaterialIds = elem.GetMaterialIds(true);

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
                    sb.AppendLine(UiLanguage.T(
                        "Matériaux présents sur l'objet :",
                        "Materials present on the element:"));
                    foreach (var m in objectMaterials)
                        sb.AppendLine($" - {m.Name}");
                }
                else
                {
                    sb.AppendLine(UiLanguage.T(
                        "Aucun matériau présent directement sur l'objet.",
                        "No material is directly present on the element."));
                }

                if (paintedMaterials.Count > 0)
                {
                    sb.AppendLine("\n" + UiLanguage.T(
                        "Matériaux de peinture trouvés :",
                        "Paint materials found:"));
                    foreach (var m in paintedMaterials)
                        sb.AppendLine($" - {m.Name}");
                }
                else
                {
                    sb.AppendLine("\n" + UiLanguage.T(
                        "Aucun matériau de peinture trouvé.",
                        "No paint material was found."));
                }

                TaskDialog.Show(
                    UiLanguage.T("Matériaux de l'élément", "Element Materials"),
                    sb.ToString());
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

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMaestro.Localization;
using Licensing;
using System;
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
                // La sélection d'une face conserve le point exact cliqué. C'est
                // indispensable pour distinguer les régions créées par Scinder la face.
                Reference pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Face,
                    UiLanguage.T(
                        "Cliquez sur la zone de peinture à identifier",
                        "Click the paint area to identify"));
                Element elem = doc.GetElement(pickedRef.ElementId);

                Face pickedFace = elem?.GetGeometryObjectFromReference(pickedRef) as Face;
                if (elem == null || pickedFace == null)
                {
                    TaskDialog.Show(
                        UiLanguage.T("Peinture", "Paint"),
                        UiLanguage.T(
                            "La face sélectionnée n'a pas pu être analysée.",
                            "The selected face could not be analyzed."));
                    return Result.Cancelled;
                }

                Face clickedRegion = ResolveClickedRegion(pickedFace, pickedRef.GlobalPoint);
                ElementId paintedMaterialId = doc.GetPaintedMaterial(elem.Id, clickedRegion);
                Material paintedMaterial = IsValid(paintedMaterialId)
                    ? doc.GetElement(paintedMaterialId) as Material
                    : null;

                ElementId baseMaterialId = clickedRegion.MaterialElementId;
                Material baseMaterial = IsValid(baseMaterialId)
                    ? doc.GetElement(baseMaterialId) as Material
                    : null;

                var sb = new StringBuilder();
                sb.AppendLine(UiLanguage.T(
                    "Zone sélectionnée :",
                    "Selected area:"));

                if (paintedMaterial != null)
                {
                    sb.AppendLine(UiLanguage.T(
                        $" • Peinture appliquée : {paintedMaterial.Name}",
                        $" • Applied paint: {paintedMaterial.Name}"));
                }
                else
                {
                    sb.AppendLine(UiLanguage.T(
                        " • Aucune peinture appliquée",
                        " • No paint applied"));
                }

                if (elem is Wall wall)
                {
                    AppendWallComposition(sb, doc, wall);
                }
                else if (baseMaterial != null)
                {
                    sb.AppendLine(UiLanguage.T(
                        $" • Matériau support : {baseMaterial.Name}",
                        $" • Base material: {baseMaterial.Name}"));
                }

                TaskDialog.Show(
                    UiLanguage.T("Peinture de la zone", "Area Paint"),
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

        private static void AppendWallComposition(StringBuilder sb, Document doc, Wall wall)
        {
            WallType wallType = wall.WallType;
            CompoundStructure structure = wallType?.GetCompoundStructure();

            sb.AppendLine();
            sb.AppendLine(UiLanguage.T(
                $"Composition du mur « {wallType?.Name} » (extérieur → intérieur) :",
                $"Wall “{wallType?.Name}” composition (exterior → interior):"));

            if (structure == null || structure.GetLayers().Count == 0)
            {
                sb.AppendLine(UiLanguage.T(
                    " • Aucune couche composée disponible",
                    " • No compound layers available"));
                return;
            }

            int index = 1;
            foreach (CompoundStructureLayer layer in structure.GetLayers())
            {
                Material material = IsValid(layer.MaterialId)
                    ? doc.GetElement(layer.MaterialId) as Material
                    : null;
                string materialName = material?.Name ?? UiLanguage.T("Par catégorie", "By Category");
                string functionName = GetLayerFunctionName(layer.Function);
                double widthMillimeters = UnitUtils.ConvertFromInternalUnits(
                    layer.Width,
                    UnitTypeId.Millimeters);
                string width = layer.Width > 1e-9
                    ? UiLanguage.T(
                        $" — {widthMillimeters:0.##} mm",
                        $" — {widthMillimeters:0.##} mm")
                    : string.Empty;

                sb.AppendLine($" {index}. {functionName} — {materialName}{width}");
                index++;
            }
        }

        private static string GetLayerFunctionName(MaterialFunctionAssignment function)
        {
            switch (function)
            {
                case MaterialFunctionAssignment.Structure:
                    return UiLanguage.T("Structure", "Structure");
                case MaterialFunctionAssignment.Substrate:
                    return UiLanguage.T("Support", "Substrate");
                case MaterialFunctionAssignment.Insulation:
                    return UiLanguage.T("Isolation", "Insulation");
                case MaterialFunctionAssignment.Finish1:
                    return UiLanguage.T("Finition extérieure", "Exterior finish");
                case MaterialFunctionAssignment.Finish2:
                    return UiLanguage.T("Finition intérieure", "Interior finish");
                case MaterialFunctionAssignment.Membrane:
                    return UiLanguage.T("Membrane", "Membrane");
                default:
                    return function.ToString();
            }
        }

        private static Face ResolveClickedRegion(Face face, XYZ clickPoint)
        {
            if (!face.HasRegions || clickPoint == null)
                return face;

            Face bestRegion = null;
            double bestDistance = double.MaxValue;
            double bestArea = double.MaxValue;

            foreach (Face region in face.GetRegions())
            {
                IntersectionResult projection = region.Project(clickPoint);
                if (projection == null || !region.IsInside(projection.UVPoint))
                    continue;

                // Les régions sont normalement coplanaires. La distance protège les
                // surfaces courbes ; l'aire départage un clic exactement sur une limite.
                double distance = projection.Distance;
                if (distance < bestDistance - 1e-9 ||
                    (Math.Abs(distance - bestDistance) <= 1e-9 && region.Area < bestArea))
                {
                    bestRegion = region;
                    bestDistance = distance;
                    bestArea = region.Area;
                }
            }

            return bestRegion ?? face;
        }

        private static bool IsValid(ElementId id)
        {
            return id != null && id != ElementId.InvalidElementId;
        }
    }
}

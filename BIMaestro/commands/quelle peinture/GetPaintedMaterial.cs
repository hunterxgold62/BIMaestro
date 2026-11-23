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
                    UiLanguage.Get("GetPaintedMaterial.ClickThePaintAreaToIdentify"));
                Element elem = doc.GetElement(pickedRef.ElementId);

                Face pickedFace = elem?.GetGeometryObjectFromReference(pickedRef) as Face;
                if (elem == null || pickedFace == null)
                {
                    TaskDialog.Show(
                        UiLanguage.Get("GetPaintedMaterial.Paint"),
                        UiLanguage.Get("GetPaintedMaterial.TheSelectedFaceCouldNotBeAnalyzed"));
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
                sb.AppendLine(UiLanguage.Get("GetPaintedMaterial.SelectedArea"));

                if (paintedMaterial != null)
                {
                    sb.AppendLine(UiLanguage.Format("GetPaintedMaterial.AppliedPaint0", (object)paintedMaterial.Name));
                }
                else
                {
                    sb.AppendLine(UiLanguage.Get("GetPaintedMaterial.NoPaintApplied"));
                }

                if (elem is Wall wall)
                {
                    AppendWallComposition(sb, doc, wall);
                }
                else if (baseMaterial != null)
                {
                    sb.AppendLine(UiLanguage.Format("GetPaintedMaterial.BaseMaterial0", (object)baseMaterial.Name));
                }

                TaskDialog.Show(
                    UiLanguage.Get("GetPaintedMaterial.AreaPaint"),
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
            sb.AppendLine(UiLanguage.Format("GetPaintedMaterial.StatusMessage1", (object)(wallType?.Name)));

            if (structure == null || structure.GetLayers().Count == 0)
            {
                sb.AppendLine(UiLanguage.Get("GetPaintedMaterial.NoCompoundLayersAvailable"));
                return;
            }

            int index = 1;
            foreach (CompoundStructureLayer layer in structure.GetLayers())
            {
                Material material = IsValid(layer.MaterialId)
                    ? doc.GetElement(layer.MaterialId) as Material
                    : null;
                string materialName = material?.Name ?? UiLanguage.Get("GetPaintedMaterial.ByCategory");
                string functionName = GetLayerFunctionName(layer.Function);
                double widthMillimeters = UnitUtils.ConvertFromInternalUnits(
                    layer.Width,
                    UnitTypeId.Millimeters);
                string width = layer.Width > 1e-9
                    ? UiLanguage.Format("GetPaintedMaterial.00Mm", (object)widthMillimeters)
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
                    return UiLanguage.Get("GetPaintedMaterial.Structure");
                case MaterialFunctionAssignment.Substrate:
                    return UiLanguage.Get("GetPaintedMaterial.Substrate");
                case MaterialFunctionAssignment.Insulation:
                    return UiLanguage.Get("GetPaintedMaterial.Insulation");
                case MaterialFunctionAssignment.Finish1:
                    return UiLanguage.Get("GetPaintedMaterial.ExteriorFinish");
                case MaterialFunctionAssignment.Finish2:
                    return UiLanguage.Get("GetPaintedMaterial.InteriorFinish");
                case MaterialFunctionAssignment.Membrane:
                    return UiLanguage.Get("GetPaintedMaterial.Membrane");
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

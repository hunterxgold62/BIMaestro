using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
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
                // Sélectionner un élément dans Revit
                Reference pickedRef = uidoc.Selection.PickObject(ObjectType.Element, "Sélectionnez un élément");
                Element elem = doc.GetElement(pickedRef.ElementId);

                // Obtenir tous les matériaux de peinture appliqués aux faces de l'élément
                List<Material> paintedMaterials = GetPaintedMaterials(doc, elem);

                // Obtenir les matériaux directement appliqués à l'élément
                List<Material> objectMaterials = GetElementMaterials(doc, elem);

                StringBuilder materialsList = new StringBuilder();

                if (objectMaterials.Count > 0)
                {
                    materialsList.AppendLine("Matériaux présents sur l'objet :");
                    foreach (Material material in objectMaterials)
                    {
                        materialsList.AppendLine(material.Name);
                    }
                }
                else
                {
                    materialsList.AppendLine("Aucun matériau présent directement sur l'objet.");
                }

                if (paintedMaterials.Count > 0)
                {
                    materialsList.AppendLine("\nMatériaux de peinture trouvés :");
                    foreach (Material material in paintedMaterials)
                    {
                        materialsList.AppendLine(material.Name);
                    }
                }
                else
                {
                    materialsList.AppendLine("\nAucun matériau de peinture trouvé.");
                }

                TaskDialog.Show("Matériaux de l'élément", materialsList.ToString());
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // L'utilisateur a annulé la sélection
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private List<Material> GetPaintedMaterials(Document doc, Element element)
        {
            List<Material> materials = new List<Material>();
            Options geomOptions = new Options();
            geomOptions.ComputeReferences = true;
            geomOptions.IncludeNonVisibleObjects = true;
            geomOptions.DetailLevel = ViewDetailLevel.Fine;

            GeometryElement geomElement = element.get_Geometry(geomOptions);
            if (geomElement != null)
            {
                foreach (GeometryObject geomObj in geomElement)
                {
                    ProcessGeometryObject(geomObj, doc, element.Id, materials, true);
                }
            }

            return materials;
        }

        private List<Material> GetElementMaterials(Document doc, Element element)
        {
            List<Material> materials = new List<Material>();
            ICollection<ElementId> materialIds = element.GetMaterialIds(false);
            foreach (ElementId materialId in materialIds)
            {
                Material material = doc.GetElement(materialId) as Material;
                if (material != null && !materials.Contains(material))
                {
                    materials.Add(material);
                }
            }
            return materials;
        }

        private void ProcessGeometryObject(GeometryObject geomObj, Document doc, ElementId elementId, List<Material> materials, bool isPainted)
        {
            if (geomObj is Solid solid)
            {
                foreach (Face face in solid.Faces)
                {
                    if (isPainted)
                    {
                        AddPaintedMaterial(doc, elementId, face, materials);
                    }
                }
            }
            else if (geomObj is GeometryInstance geomInstance)
            {
                GeometryElement instanceGeometry = geomInstance.GetInstanceGeometry();
                foreach (GeometryObject instanceGeomObj in instanceGeometry)
                {
                    ProcessGeometryObject(instanceGeomObj, doc, elementId, materials, isPainted);
                }
            }
            else if (geomObj is GeometryElement geomElem)
            {
                foreach (GeometryObject subGeomObj in geomElem)
                {
                    ProcessGeometryObject(subGeomObj, doc, elementId, materials, isPainted);
                }
            }
        }

        private void AddPaintedMaterial(Document doc, ElementId elementId, Face face, List<Material> materials)
        {
            ElementId materialId = doc.GetPaintedMaterial(elementId, face);
            if (materialId != ElementId.InvalidElementId)
            {
                Material material = doc.GetElement(materialId) as Material;
                if (material != null && !materials.Contains(material))
                {
                    materials.Add(material);
                }
            }
        }
    }
}

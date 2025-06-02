using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace IA
{
    [Transaction(TransactionMode.Manual)]
    public class SelectElementsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Initialiser ElementUtilities avec UIApplication
            ElementUtilities.Initialize(commandData.Application);

            try
            {
                // Sélection des éléments
                ICollection<ElementId> selectedElementIds = uidoc.Selection.GetElementIds();

                if (selectedElementIds.Count == 0)
                {
                    TaskDialog.Show("Sélection", "Aucun élément sélectionné. Veuillez sélectionner des éléments.");
                    return Result.Cancelled;
                }

                List<ElementInfo> elementInfos = new List<ElementInfo>();

                foreach (ElementId elementId in selectedElementIds)
                {
                    Element element = doc.GetElement(elementId);
                    Level level = element.Document.GetElement(element.LevelId) as Level;
                    string levelName = level != null ? level.Name : "Niveau inconnu";

                    string categoryName = element.Category?.Name ?? "Catégorie inconnue";

                    ElementInfo elementInfo = new ElementInfo
                    {
                        Id = element.Id.ToString(),
                        Name = element.Name,
                        Category = categoryName,
                        Material = ElementUtilities.GetElementMaterials(element),
                        CustomParameters = ElementUtilities.GetCustomParameters(element), // Pas besoin de passer UIApplication
                        Level = levelName,
                        SurfaceAndVolume = ElementUtilities.GetElementFloorAreaAndVolume(element, categoryName) // Pass category
                    };
                    elementInfos.Add(elementInfo);
                }

                string elementsInfoLog = string.Join(Environment.NewLine + Environment.NewLine, elementInfos.Select(e => e.ToString()));
                TaskDialog.Show("Informations des éléments", elementsInfoLog);

                return Result.Succeeded;
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
        }
    }
}

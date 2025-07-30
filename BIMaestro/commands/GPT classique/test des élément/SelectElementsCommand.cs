// SelectElementsCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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

            try
            {
                // Récupération des éléments sélectionnés
                ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
                if (selectedIds == null || !selectedIds.Any())
                {
                    TaskDialog.Show("Sélection", "Aucun élément sélectionné. Veuillez sélectionner des éléments.");
                    return Result.Cancelled;
                }

                List<ElementInfo> elementInfos = new List<ElementInfo>();

                foreach (ElementId elementId in selectedIds)
                {
                    Element element = doc.GetElement(elementId);

                    // Récupération sûre du niveau via le paramètre LEVEL_PARAM
                    ElementId lvlId = ElementId.InvalidElementId;
                    Parameter lvlParam = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                    if (lvlParam != null && lvlParam.StorageType == StorageType.ElementId)
                    {
                        lvlId = lvlParam.AsElementId();
                    }
                    Level level = lvlId != ElementId.InvalidElementId
                                  ? doc.GetElement(lvlId) as Level
                                  : null;
                    string levelName = level?.Name ?? "Niveau inconnu";

                    // Catégorie et autres propriétés
                    string categoryName = element.Category?.Name ?? "Catégorie inconnue";

                    ElementInfo info = new ElementInfo
                    {
                        Id = element.Id.ToString(),
                        Name = element.Name,
                        Category = categoryName,
                        Material = ElementUtilities.GetElementMaterials(element),
                        CustomParameters = ElementUtilities.GetCustomParameters(element),
                        Level = levelName
                    };

                    elementInfos.Add(info);
                }

                // Construction du texte à afficher
                string elementsInfoLog = string.Join(
                    Environment.NewLine + Environment.NewLine,
                    elementInfos.Select(ei => ei.ToString())
                );

                TaskDialog.Show("Informations des éléments", elementsInfoLog);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Linq;
using Autodesk.Revit.UI.Selection;
using System.Collections.Generic;

[Transaction(TransactionMode.ReadOnly)]
public class CountElementsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        // Obtenir l'application et le document courants
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        // Obtenir la sélection courante
        ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();

        // Vérifier si un élément est sélectionné
        if (selectedIds.Count == 0)
        {
            TaskDialog.Show("Sélectionner un élément", "Sélectionne un élément dans le projet.");
            return Result.Cancelled;
        }

        // Obtenir le premier élément sélectionné
        Element selectedElement = doc.GetElement(selectedIds.First());

        // Obtenir la catégorie de l'élément sélectionné
        Category category = selectedElement.Category;

        if (category == null)
        {
            TaskDialog.Show("Erreur", "L'élément sélectionné n'a pas de catégorie valide.");
            return Result.Failed;
        }

        // Filtrer tous les éléments de la même catégorie dans le projet
        FilteredElementCollector collector = new FilteredElementCollector(doc)
            .OfCategoryId(category.Id)
            .WhereElementIsNotElementType();

        int elementCount = collector.Count();

        // Afficher le nombre d'éléments dans une boîte de dialogue
        TaskDialog.Show("Nombre d'Éléments", $"Il y a {elementCount} éléments dans la catégorie '{category.Name}'.");

        return Result.Succeeded;
    }
}

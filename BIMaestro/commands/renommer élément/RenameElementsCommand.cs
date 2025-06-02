using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class RenameElementsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Récupérer les éléments sélectionnés
            ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                message = "Aucun élément sélectionné.";
                return Result.Failed;
            }

            // Récupérer les paramètres texte modifiables du premier élément sélectionné
            Element firstElement = doc.GetElement(selectedIds.First());
            List<string> textParameters = GetWritableTextParameters(firstElement);

            if (!textParameters.Any())
            {
                TaskDialog.Show("Erreur", "Aucun paramètre texte modifiable trouvé sur les éléments sélectionnés.");
                return Result.Failed;
            }

            // Afficher la fenêtre de renommage avec les paramètres disponibles
            ElementRenamerWindow renamerWindow = new ElementRenamerWindow(textParameters);
            if (renamerWindow.ShowDialog() == true)
            {
                string selectedParameter = renamerWindow.SelectedParameter;

                using (Transaction tx = new Transaction(doc, "Mettre à jour ou réinitialiser les éléments"))
                {
                    try
                    {
                        tx.Start();

                        if (renamerWindow.IsReset)
                        {
                            // Réinitialiser le paramètre sélectionné
                            foreach (ElementId id in selectedIds)
                            {
                                Element element = doc.GetElement(id);
                                Parameter param = element.LookupParameter(selectedParameter);
                                if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                                {
                                    param.Set(""); // Réinitialiser le paramètre à une chaîne vide
                                }
                            }
                        }
                        else
                        {
                            // Renommer les éléments
                            string prefix = renamerWindow.Prefix ?? "";
                            string suffix = renamerWindow.Suffix ?? "";

                            int currentNumber = 1;
                            int totalElements = selectedIds.Count;

                            bool isNumeric = renamerWindow.SelectedNumberFormat == "1,2,3..." || renamerWindow.SelectedNumberFormat == "001,002,003...";
                            bool isAlphabetic = renamerWindow.SelectedNumberFormat == "A,B,C...";

                            if (isNumeric)
                            {
                                if (!int.TryParse(renamerWindow.StartNumber, out currentNumber))
                                {
                                    TaskDialog.Show("Erreur", "Le numéro de départ doit être un nombre entier pour le format sélectionné.");
                                    tx.RollBack();
                                    return Result.Failed;
                                }
                            }
                            else if (isAlphabetic)
                            {
                                currentNumber = LettersToNumber(renamerWindow.StartNumber.ToUpper());
                                if (currentNumber == -1)
                                {
                                    TaskDialog.Show("Erreur", "Le numéro de départ doit être une lettre (A-Z) ou une séquence alphabétique valide pour le format alphabétique.");
                                    tx.RollBack();
                                    return Result.Failed;
                                }
                            }

                            // Conversion de la hauteur de bande avec gestion des erreurs
                            if (!double.TryParse(renamerWindow.BandHeight, out double bandHeight))
                            {
                                bandHeight = 1.0; // Valeur par défaut si la conversion échoue
                            }

                            // Obtenir les éléments avec leurs positions transformées selon la vue active
                            var elementLocations = GetElementsWithLocations(doc, selectedIds, uiDoc.ActiveView);

                            List<ElementLocation> sortedElements;

                            // Vérifier si le tri par niveau est activé
                            if (renamerWindow.IsSortByLevelEnabled)
                            {
                                // Vérifier si tous les éléments ont un paramètre de niveau
                                if (!AllElementsHaveLevel(elementLocations))
                                {
                                    TaskDialog.Show("Erreur", "Tous les éléments n'ont pas de paramètre 'Niveau'. Le tri par niveau n'est pas possible pour ces éléments. Veuillez trier niveau par niveau.");
                                    tx.RollBack();
                                    return Result.Failed;
                                }

                                // Utiliser le tri par niveau
                                sortedElements = SortElementsByLevelAndLocation(elementLocations, bandHeight, doc);
                            }
                            else
                            {
                                // Utiliser le tri standard
                                sortedElements = SortElementsByGridLocation(elementLocations, bandHeight);
                            }

                            foreach (var elemLoc in sortedElements)
                            {
                                Element element = elemLoc.Element;
                                Parameter param = element.LookupParameter(selectedParameter);
                                if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                                {
                                    string numberString = "";

                                    if (renamerWindow.IsNumberingEnabled)
                                    {
                                        if (isNumeric)
                                        {
                                            if (renamerWindow.SelectedNumberFormat == "001,002,003...")
                                            {
                                                numberString = currentNumber.ToString("D3");
                                            }
                                            else
                                            {
                                                numberString = currentNumber.ToString();
                                            }
                                            currentNumber++;
                                        }
                                        else if (isAlphabetic)
                                        {
                                            numberString = NumberToLetters(currentNumber);
                                            currentNumber++;
                                        }
                                    }

                                    string newValue = prefix + numberString + suffix;
                                    param.Set(newValue);
                                }
                            }
                        }

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        TaskDialog.Show("Erreur", $"Une erreur est survenue : {ex.Message}");
                        return Result.Failed;
                    }
                }

                return Result.Succeeded;
            }

            return Result.Cancelled;
        }

        private List<string> GetWritableTextParameters(Element element)
        {
            List<string> textParameters = new List<string>();

            foreach (Parameter param in element.Parameters)
            {
                if (param.StorageType == StorageType.String && !param.IsReadOnly)
                {
                    textParameters.Add(param.Definition.Name);
                }
            }

            return textParameters;
        }

        // Classe pour associer un élément avec sa position transformée selon la vue active
        private class ElementLocation
        {
            public Element Element { get; set; }
            public XYZ Location { get; set; }
        }

        // Récupérer les éléments avec leurs positions transformées selon la vue active
        private List<ElementLocation> GetElementsWithLocations(Document doc, ICollection<ElementId> elementIds, View activeView)
        {
            List<ElementLocation> elementLocations = new List<ElementLocation>();

            // Créer une transformation pour passer des coordonnées du monde aux coordonnées de la vue
            Transform viewTransform = Transform.Identity;
            viewTransform.BasisX = activeView.RightDirection;
            viewTransform.BasisY = activeView.UpDirection;
            viewTransform.BasisZ = activeView.ViewDirection;

            Transform worldToViewTransform = viewTransform.Inverse;

            foreach (ElementId id in elementIds)
            {
                Element element = doc.GetElement(id);
                LocationPoint locationPoint = element.Location as LocationPoint;
                if (locationPoint != null)
                {
                    XYZ transformedLocation = worldToViewTransform.OfPoint(locationPoint.Point);
                    elementLocations.Add(new ElementLocation
                    {
                        Element = element,
                        Location = transformedLocation
                    });
                }
                else
                {
                    LocationCurve locationCurve = element.Location as LocationCurve;
                    if (locationCurve != null)
                    {
                        XYZ midpoint = (locationCurve.Curve.GetEndPoint(0) + locationCurve.Curve.GetEndPoint(1)) / 2;
                        XYZ transformedLocation = worldToViewTransform.OfPoint(midpoint);
                        elementLocations.Add(new ElementLocation
                        {
                            Element = element,
                            Location = transformedLocation
                        });
                    }
                    else
                    {
                        // Ignorer les éléments sans position géométrique
                        continue;
                    }
                }
            }

            return elementLocations;
        }

        // Vérifier si tous les éléments ont un paramètre de niveau
        private bool AllElementsHaveLevel(List<ElementLocation> elements)
        {
            foreach (var elemLoc in elements)
            {
                ElementId levelId = GetElementLevelId(elemLoc.Element);
                if (levelId == ElementId.InvalidElementId)
                {
                    return false;
                }
            }
            return true;
        }

        // Trier les éléments en utilisant une grille de taille définie
        private List<ElementLocation> SortElementsByGridLocation(List<ElementLocation> elements, double gridSize = 1.0)
        {
            // Grouper les éléments par cellule de grille en Y
            var groupedElements = elements
                .GroupBy(e => (int)Math.Floor(e.Location.Y / gridSize)) // Regrouper par cellule de grille en Y
                .OrderByDescending(g => g.Key) // Trier les bandes de haut en bas
                .ToList();

            // Trier les éléments dans chaque bande de gauche à droite (X)
            var sortedElements = new List<ElementLocation>();
            foreach (var group in groupedElements)
            {
                var sortedGroup = group.OrderBy(e => e.Location.X).ToList(); // Trier de gauche à droite
                sortedElements.AddRange(sortedGroup);
            }

            return sortedElements;
        }

        // Tri par niveau puis par position
        private List<ElementLocation> SortElementsByLevelAndLocation(List<ElementLocation> elements, double gridSize, Document doc)
        {
            // Regrouper les éléments par niveau
            var groupedByLevel = elements
                .GroupBy(e => GetElementLevelId(e.Element))
                .OrderBy(g => GetLevelElevation(g.Key, doc)) // Trier du niveau le plus bas au plus haut
                .ToList();

            var sortedElements = new List<ElementLocation>();

            foreach (var levelGroup in groupedByLevel)
            {
                // Au sein de chaque niveau, trier les éléments par position en utilisant la grille
                var elementsInLevel = levelGroup.ToList();
                var sortedInLevel = SortElementsByGridLocation(elementsInLevel, gridSize);
                sortedElements.AddRange(sortedInLevel);
            }

            return sortedElements;
        }

        // Méthode pour obtenir l'Id du niveau de l'élément
        private ElementId GetElementLevelId(Element element)
        {
            ElementId levelId = ElementId.InvalidElementId;

            // Essayer d'utiliser la propriété LevelId si disponible
            if (element is FamilyInstance familyInstance && familyInstance.LevelId != ElementId.InvalidElementId)
            {
                levelId = familyInstance.LevelId;
            }
            else if (element is Wall wall)
            {
                levelId = wall.LevelId;
            }
            else if (element is Floor floor)
            {
                levelId = floor.LevelId;
            }
            else if (element is Ceiling ceiling)
            {
                levelId = ceiling.LevelId;
            }
            else if (element is RoofBase roof)
            {
                levelId = roof.LevelId;
            }
            else
            {
                // Parcourir les paramètres pour trouver 'Niveau'
                foreach (Parameter param in element.Parameters)
                {
                    if (param.Definition.Name == "Niveau" && param.HasValue)
                    {
                        levelId = param.AsElementId();
                        break;
                    }
                }
            }

            return levelId;
        }

        // Méthode pour obtenir l'élévation du niveau
        private double GetLevelElevation(ElementId levelId, Document doc)
        {
            if (levelId != ElementId.InvalidElementId)
            {
                Level level = doc.GetElement(levelId) as Level;
                if (level != null)
                {
                    return level.Elevation;
                }
            }
            return double.MinValue; // Si pas de niveau, on met l'élévation minimale
        }

        // Convertir un nombre en séquence de lettres (A, B, ..., AA, AB, ...)
        private string NumberToLetters(int number)
        {
            string result = string.Empty;
            while (number > 0)
            {
                number--;
                result = (char)('A' + (number % 26)) + result;
                number /= 26;
            }
            return result;
        }

        // Convertir une séquence de lettres en nombre (A=1, B=2, ..., AA=27, AB=28, ...)
        private int LettersToNumber(string letters)
        {
            int number = 0;
            foreach (char c in letters)
            {
                if (c < 'A' || c > 'Z')
                {
                    return -1; // Erreur si caractère non valide
                }
                number = number * 26 + (c - 'A' + 1);
            }
            return number;
        }
    }
}

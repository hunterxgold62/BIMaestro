using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BIMaestro.Localization;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class RenameElementsCommand : BaseTrackedCommand
    {
        private const string ViewNameTarget = "Nom de la vue — navigateur";
        private const string ViewTitleTarget = "Titre sur la feuille — texte affiché";
        private const string ViewportDetailNumberTarget = "Numéro du détail — pastille sur la feuille";

        protected override string ButtonId => "RenameElementsCommand";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Récupérer les éléments sélectionnés
            ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show(UiLanguage.T("Sélection", "Selection"), UiLanguage.T("Aucun élément sélectionné. Veuillez sélectionner des éléments dans Revit.", "No Element Selected. Select Elements in Revit."));
                return Result.Cancelled;
            }

            var selectedElements = selectedIds
                .Select(doc.GetElement)
                .Where(element => element != null)
                .ToList();
            int viewportCount = selectedElements.Count(element => element is Viewport);

            if (viewportCount > 0)
            {
                if (viewportCount != selectedElements.Count)
                {
                    TaskDialog.Show(
                        UiLanguage.T("Sélection incompatible", "Incompatible Selection"),
                        UiLanguage.T("La sélection mélange des fenêtres de vue et d'autres éléments.\n\nSélectionnez uniquement les fenêtres de vue à numéroter.", "The Selection Mixes Viewports and Other Elements.\n\nSelect Only the Viewports to Number."));
                    return Result.Cancelled;
                }

                return RenameSelectedViewports(doc, uiDoc, selectedElements.Cast<Viewport>().ToList());
            }

            // Récupérer les paramètres texte modifiables du premier élément sélectionné
            Element firstElement = doc.GetElement(selectedIds.First());
            List<string> textParameters = GetWritableTextParameters(firstElement);

            if (!textParameters.Any())
            {
                TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T("Aucun paramètre texte modifiable trouvé sur les éléments sélectionnés.", "No Editable Text Parameter Found on the Selected Elements."));
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

                            bool isNumeric = renamerWindow.SelectedNumberFormat == "1,2,3..."
                                || renamerWindow.SelectedNumberFormat == "001,002,003..."
                                || renamerWindow.SelectedNumberFormat == "0001,0002,0003...";
                            bool isAlphabetic = renamerWindow.SelectedNumberFormat == "A,B,C...";

                            if (isNumeric)
                            {
                                if (!int.TryParse(renamerWindow.StartNumber, out currentNumber))
                                {
                                    TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T("Le numéro de départ doit être un nombre entier pour le format sélectionné.", "The Starting Number Must Be an Integer for the Selected Format."));
                                    tx.RollBack();
                                    return Result.Failed;
                                }
                            }
                            else if (isAlphabetic)
                            {
                                currentNumber = LettersToNumber(renamerWindow.StartNumber.ToUpper());
                                if (currentNumber == -1)
                                {
                                    TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T("Le numéro de départ doit être une lettre (A-Z) ou une séquence alphabétique valide pour le format alphabétique.", "The Starting Number Must Be a Letter (A-Z) or a Valid Alphabetic Sequence."));
                                    tx.RollBack();
                                    return Result.Failed;
                                }
                            }

                            // Conversion de la hauteur de bande avec gestion des erreurs
                            if (!TryParseUserDouble(renamerWindow.BandHeight, out double bandHeightMeters)
                                || bandHeightMeters <= 0)
                            {
                                bandHeightMeters = 1.0;
                            }
                            double bandHeight = UnitUtils.ConvertToInternalUnits(
                                bandHeightMeters,
                                UnitTypeId.Meters);

                            // Obtenir les éléments avec leurs positions transformées selon la vue active
                            var elementLocations = GetElementsWithLocations(doc, selectedIds, uiDoc.ActiveView);

                            List<ElementLocation> sortedElements;

                            // Vérifier si le tri par niveau est activé
                            if (renamerWindow.IsSortByLevelEnabled)
                            {
                                // Vérifier si tous les éléments ont un paramètre de niveau
                                if (!AllElementsHaveLevel(elementLocations))
                                {
                                    TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T("Tous les éléments n'ont pas de paramètre 'Niveau'. Le tri par niveau n'est pas possible pour ces éléments. Veuillez trier niveau par niveau.", "Not All Elements Have a 'Level' Parameter. These Elements Cannot Be Sorted by Level; Process One Level at a Time."));
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

                                    if (isNumeric)
                                    {
                                        if (renamerWindow.SelectedNumberFormat == "0001,0002,0003...")
                                        {
                                            numberString = currentNumber.ToString("D4");
                                        }
                                        else if (renamerWindow.SelectedNumberFormat == "001,002,003...")
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
                        TaskDialog.Show(UiLanguage.T("Erreur", "Error"), UiLanguage.T("Une erreur est survenue : ", "An Error Occurred: ") + ex.Message);
                        return Result.Failed;
                    }
                }

                return Result.Succeeded;
            }

            return Result.Cancelled;
        }

        private Result RenameSelectedViewports(
            Document doc,
            UIDocument uiDoc,
            List<Viewport> selectedViewports)
        {
            ViewSheet activeSheet = uiDoc.ActiveView as ViewSheet;
            if (activeSheet == null)
            {
                TaskDialog.Show(
                    "Feuille requise",
                    UiLanguage.T("Pour numéroter des fenêtres de vue, ouvrez leur feuille puis relancez Organisateur.", "To Number Viewports, Open Their Sheet and Run Organizer Again."));
                return Result.Cancelled;
            }

            var viewportIdsOnSheet = new HashSet<int>(
                activeSheet.GetAllViewports().Select(id => id.IntegerValue));
            if (selectedViewports.Any(viewport => !viewportIdsOnSheet.Contains(viewport.Id.IntegerValue)))
            {
                TaskDialog.Show(
                    UiLanguage.T("Sélection incompatible", "Incompatible Selection"),
                    UiLanguage.T("Toutes les fenêtres de vue sélectionnées doivent appartenir à la feuille active.", "All Selected Viewports Must Belong to the Active Sheet."));
                return Result.Cancelled;
            }

            var viewportTargets = new List<string>
            {
                ViewportDetailNumberTarget,
                ViewNameTarget,
                ViewTitleTarget
            };

            var renamerWindow = new ElementRenamerWindow(viewportTargets, isViewportMode: true);
            if (renamerWindow.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            if (!TryParseUserDouble(renamerWindow.BandHeight, out double toleranceMillimeters)
                || toleranceMillimeters <= 0)
            {
                toleranceMillimeters = 20.0;
            }

            double rowTolerance = UnitUtils.ConvertToInternalUnits(
                toleranceMillimeters,
                UnitTypeId.Millimeters);
            List<Viewport> sortedViewports = SortViewportsByReadingOrder(
                selectedViewports,
                rowTolerance);

            if (!TryBuildNumberedValues(
                renamerWindow,
                sortedViewports.Count,
                out List<string> desiredValues,
                out string validationError))
            {
                TaskDialog.Show("Erreur", validationError);
                return Result.Failed;
            }

            using (var transaction = new Transaction(doc, "Organiser les fenêtres de vue"))
            {
                try
                {
                    transaction.Start();

                    if (renamerWindow.SelectedParameter == ViewportDetailNumberTarget)
                    {
                        SetViewportDetailNumbers(
                            doc,
                            activeSheet,
                            sortedViewports,
                            desiredValues);
                    }
                    else if (renamerWindow.SelectedParameter == ViewTitleTarget)
                    {
                        SetViewTitles(doc, sortedViewports, desiredValues);
                    }
                    else
                    {
                        SetViewNames(doc, sortedViewports, desiredValues);
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                    {
                        transaction.RollBack();
                    }

                    TaskDialog.Show(
                        "Organisateur",
                        "La numérotation des fenêtres de vue n'a pas pu être appliquée.\n\n" +
                        ex.Message);
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }

        private List<Viewport> SortViewportsByReadingOrder(
            IEnumerable<Viewport> viewports,
            double rowTolerance)
        {
            var remaining = viewports
                .Select(viewport => new ElementLocation
                {
                    Element = viewport,
                    Location = GetViewportReadingPosition(viewport)
                })
                .OrderByDescending(item => item.Location.Y)
                .ThenBy(item => item.Location.X)
                .ToList();

            var sorted = new List<Viewport>();
            while (remaining.Count > 0)
            {
                double rowY = remaining[0].Location.Y;
                var row = remaining
                    .Where(item => Math.Abs(item.Location.Y - rowY) <= rowTolerance)
                    .OrderBy(item => item.Location.X)
                    .ToList();

                sorted.AddRange(row.Select(item => (Viewport)item.Element));
                foreach (ElementLocation item in row)
                {
                    remaining.Remove(item);
                }
            }

            return sorted;
        }

        private XYZ GetViewportReadingPosition(Viewport viewport)
        {
            XYZ boxCenter = viewport.GetBoxCenter();
            double rowY = boxCenter.Y;

            try
            {
                Outline labelOutline = viewport.GetLabelOutline();
                if (labelOutline != null && !labelOutline.IsEmpty)
                {
                    // Les titres sont généralement alignés sur une même ligne même lorsque
                    // les cadrages des coupes ont des hauteurs différentes.
                    rowY = labelOutline.MinimumPoint.Y;
                }
                else
                {
                    Outline boxOutline = viewport.GetBoxOutline();
                    if (boxOutline != null && !boxOutline.IsEmpty)
                    {
                        rowY = boxOutline.MinimumPoint.Y;
                    }
                }
            }
            catch
            {
                // Le centre reste un repli fiable si un type de viewport n'expose pas son titre.
            }

            return new XYZ(boxCenter.X, rowY, 0);
        }

        private bool TryBuildNumberedValues(
            ElementRenamerWindow renamerWindow,
            int count,
            out List<string> values,
            out string error)
        {
            values = new List<string>();
            error = null;

            string format = renamerWindow.SelectedNumberFormat;
            bool isAlphabetic = format == "A,B,C...";
            int currentNumber;

            if (isAlphabetic)
            {
                currentNumber = LettersToNumber((renamerWindow.StartNumber ?? string.Empty).ToUpperInvariant());
                if (currentNumber < 1)
                {
                    error = UiLanguage.T("Le numéro de départ doit être une lettre ou une séquence alphabétique valide.", "The Starting Number Must Be a Letter or a Valid Alphabetic Sequence.");
                    return false;
                }
            }
            else if (!int.TryParse(renamerWindow.StartNumber, out currentNumber))
            {
                error = UiLanguage.T("Le numéro de départ doit être un nombre entier.", "The Starting Number Must Be an Integer.");
                return false;
            }

            string prefix = renamerWindow.Prefix ?? string.Empty;
            string suffix = renamerWindow.Suffix ?? string.Empty;

            for (int index = 0; index < count; index++)
            {
                string number;
                if (isAlphabetic)
                {
                    number = NumberToLetters(currentNumber);
                }
                else if (format == "0001,0002,0003...")
                {
                    number = currentNumber.ToString("D4");
                }
                else if (format == "001,002,003...")
                {
                    number = currentNumber.ToString("D3");
                }
                else
                {
                    number = currentNumber.ToString(CultureInfo.InvariantCulture);
                }

                values.Add(prefix + number + suffix);
                currentNumber++;
            }

            return true;
        }

        private void SetViewportDetailNumbers(
            Document doc,
            ViewSheet sheet,
            IList<Viewport> viewports,
            IList<string> desiredValues)
        {
            var selectedIds = new HashSet<int>(viewports.Select(viewport => viewport.Id.IntegerValue));
            var numbersUsedByOtherViewports = new HashSet<string>(
                sheet.GetAllViewports()
                    .Where(id => !selectedIds.Contains(id.IntegerValue))
                    .Select(id => doc.GetElement(id) as Viewport)
                    .Where(viewport => viewport != null)
                    .Select(GetViewportDetailNumber)
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);

            List<string> conflicts = desiredValues
                .Where(value => numbersUsedByOtherViewports.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (conflicts.Count > 0)
            {
                throw new InvalidOperationException(
                    UiLanguage.T("Ces numéros sont déjà utilisés par d'autres fenêtres de la feuille : ", "These Numbers Are Already Used by Other Viewports on the Sheet: ") +
                    string.Join(", ", conflicts) + ".");
            }

            string temporaryPrefix = "BM" + Guid.NewGuid().ToString("N").Substring(0, 8);
            for (int index = 0; index < viewports.Count; index++)
            {
                SetViewportDetailNumber(viewports[index], temporaryPrefix + index);
            }

            doc.Regenerate();

            for (int index = 0; index < viewports.Count; index++)
            {
                SetViewportDetailNumber(viewports[index], desiredValues[index]);
            }
        }

        private string GetViewportDetailNumber(Viewport viewport)
        {
            Parameter parameter = viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
            return parameter?.AsString() ?? string.Empty;
        }

        private void SetViewportDetailNumber(Viewport viewport, string value)
        {
            Parameter parameter = viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
            {
                throw new InvalidOperationException(
                    UiLanguage.T("Le numéro de détail d'une fenêtre de vue sélectionnée n'est pas modifiable.", "The Detail Number of a Selected Viewport Cannot Be Modified."));
            }

            parameter.Set(value);
        }

        private void SetViewNames(
            Document doc,
            IList<Viewport> viewports,
            IList<string> desiredValues)
        {
            List<View> views = GetDistinctSelectedViews(doc, viewports);
            if (views.Count != viewports.Count)
            {
                throw new InvalidOperationException(
                    UiLanguage.T("Une même vue est présente plusieurs fois dans la sélection et ne peut pas recevoir plusieurs noms.", "The Same View Appears Multiple Times in the Selection and Cannot Receive Multiple Names."));
            }

            var selectedViewIds = new HashSet<int>(views.Select(view => view.Id.IntegerValue));
            var namesUsedByOtherViews = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(view => !selectedViewIds.Contains(view.Id.IntegerValue))
                    .Select(view => view.Name),
                StringComparer.OrdinalIgnoreCase);

            List<string> conflicts = desiredValues
                .Where(value => namesUsedByOtherViews.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (conflicts.Count > 0)
            {
                throw new InvalidOperationException(
                    UiLanguage.T("Ces noms de vue existent déjà dans le projet : ", "These View Names Already Exist in the Project: ") +
                    string.Join(", ", conflicts) + ".");
            }

            string temporaryPrefix = "BIMaestro temporaire " +
                Guid.NewGuid().ToString("N").Substring(0, 8) + " ";
            for (int index = 0; index < views.Count; index++)
            {
                views[index].Name = temporaryPrefix + index;
            }

            doc.Regenerate();

            for (int index = 0; index < views.Count; index++)
            {
                views[index].Name = desiredValues[index];
            }
        }

        private void SetViewTitles(
            Document doc,
            IList<Viewport> viewports,
            IList<string> desiredValues)
        {
            for (int index = 0; index < viewports.Count; index++)
            {
                View view = doc.GetElement(viewports[index].ViewId) as View;
                Parameter parameter = view?.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
                {
                    throw new InvalidOperationException(
                        UiLanguage.T("Le titre sur la feuille d'une vue sélectionnée n'est pas modifiable.", "The Title on Sheet of a Selected View Cannot Be Modified."));
                }

                parameter.Set(desiredValues[index]);
            }
        }

        private List<View> GetDistinctSelectedViews(Document doc, IEnumerable<Viewport> viewports)
        {
            return viewports
                .Select(viewport => doc.GetElement(viewport.ViewId) as View)
                .Where(view => view != null)
                .GroupBy(view => view.Id.IntegerValue)
                .Select(group => group.First())
                .ToList();
        }

        private static bool TryParseUserDouble(string value, out double result)
        {
            return double.TryParse(
                       value,
                       NumberStyles.Float,
                       CultureInfo.CurrentCulture,
                       out result)
                || double.TryParse(
                       value,
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out result);
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
                        // Utiliser le centre de la BoundingBox lorsque Location est indisponible (ex. portes de mur rideau)
                        BoundingBoxXYZ bb = element.get_BoundingBox(null);
                        if (bb != null)
                        {
                            XYZ center = (bb.Min + bb.Max) / 2;
                            XYZ transformedLocation = worldToViewTransform.OfPoint(center);
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
                // Utiliser un paramètre intégré pour récupérer le niveau
                Parameter levelParam = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                if (levelParam == null)
                {
                    levelParam = element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                }
                if (levelParam == null)
                {
                    levelParam = element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                }

                if (levelParam != null && levelParam.HasValue)
                {
                    levelId = levelParam.AsElementId();
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

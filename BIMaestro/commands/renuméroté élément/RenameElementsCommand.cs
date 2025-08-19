using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class RenameElementsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Récupérer la sélection
            ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show("Sélection", "Aucun élément sélectionné. Veuillez sélectionner des éléments dans Revit.");
                return Result.Cancelled;
            }

            // Paramètres texte modifiables (basé sur le 1er élément)
            Element firstElement = doc.GetElement(selectedIds.First());
            List<string> textParameters = GetWritableTextParameters(firstElement);

            if (!textParameters.Any())
            {
                TaskDialog.Show("Erreur", "Aucun paramètre texte modifiable trouvé sur les éléments sélectionnés.");
                return Result.Failed;
            }

            // Fenêtre
            ElementRenamerWindow renamerWindow = new ElementRenamerWindow(textParameters);
            if (renamerWindow.ShowDialog() != true)
                return Result.Cancelled;

            string selectedParameter = renamerWindow.SelectedParameter ?? string.Empty;

            using (Transaction tx = new Transaction(doc, "Mettre à jour ou réinitialiser les éléments"))
            {
                try
                {
                    tx.Start();

                    // Prétraitements communs
                    bool hasGrids = selectedIds
                        .Select(id => doc.GetElement(id))
                        .Any(el => el != null && el.Category != null &&
                                   el.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Grids);

                    // Désactiver le tri par niveau si des grilles sont présentes
                    if (renamerWindow.IsSortByLevelEnabled && hasGrids)
                    {
                        TaskDialog.Show("Info",
                            "Le tri par niveau ne s'applique pas aux lignes de quadrillage. " +
                            "Tri standard gauche→droite, haut→bas utilisé.");
                    }

                    // Conversion robuste de BandHeight -> unités internes Revit
                    double bandHeightInternal = 1.0;
                    {
                        double userVal;
                        // Essaye culture courante (utile en FR avec virgule)
                        if (!double.TryParse(renamerWindow.BandHeight, NumberStyles.Any, CultureInfo.CurrentCulture, out userVal))
                        {
                            // Backup en invariant
                            if (!double.TryParse(renamerWindow.BandHeight, NumberStyles.Any, CultureInfo.InvariantCulture, out userVal))
                                userVal = 1.0;
                        }

#if REVIT_2023_OR_NEWER
                        var fo = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
                        ForgeTypeId displayUnit = fo.GetUnitTypeId();
                        bandHeightInternal = UnitUtils.ConvertToInternalUnits(userVal, displayUnit);
#else
                        // Compat si besoin (anciennes API)
                        bandHeightInternal = userVal; // à adapter si tu vises des versions plus anciennes
#endif
                        if (bandHeightInternal <= 0) bandHeightInternal = 1.0;
                    }

                    // Localisations (dans le repère de la vue) pour le tri
                    var elementLocations = GetElementsWithLocations(doc, selectedIds, uiDoc.ActiveView);

                    List<ElementLocation> sortedElements;

                    // Tri au niveau si demandé ET pas de grille dans la sélection
                    if (renamerWindow.IsSortByLevelEnabled && !hasGrids)
                    {
                        if (!AllElementsHaveLevel(elementLocations))
                        {
                            TaskDialog.Show("Erreur", "Tous les éléments n'ont pas de 'Niveau'. " +
                                "Le tri par niveau n'est pas possible. Veuillez trier niveau par niveau.");
                            tx.RollBack();
                            return Result.Failed;
                        }
                        sortedElements = SortElementsByLevelAndLocation(elementLocations, bandHeightInternal, doc);
                    }
                    else
                    {
                        // Tri standard par bandes horizontales (vue courante), haut→bas puis gauche→droite
                        sortedElements = SortElementsByGridLocation(elementLocations, bandHeightInternal);
                    }

                    // RESET ?
                    if (renamerWindow.IsReset)
                    {
                        foreach (ElementLocation elemLoc in sortedElements)
                        {
                            Element element = elemLoc.Element;
                            bool isDatum = (element is Grid) || (element is Level);

                            // Interdire reset à vide pour datums
                            if (isDatum) continue;

                            Parameter param = element.LookupParameter(selectedParameter);
                            if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
                            {
                                using (SubTransaction st = new SubTransaction(doc))
                                {
                                    st.Start();
                                    try
                                    {
                                        param.Set(string.Empty);
                                        st.Commit();
                                    }
                                    catch
                                    {
                                        st.RollBack();
                                    }
                                }
                            }
                            else
                            {
                                // Fallback DATUM_TEXT pour sécurité si user a choisi un param fantôme
                                if (TrySetStringParameter(element, selectedParameter, string.Empty))
                                {
                                    // ok via fallback
                                }
                            }
                        }

                        tx.Commit();
                        return Result.Succeeded;
                    }

                    // Numérotation
                    string prefix = renamerWindow.Prefix ?? "";
                    string suffix = renamerWindow.Suffix ?? "";

                    int currentNumber = 1;
                    bool isNumeric = renamerWindow.SelectedNumberFormat == "1,2,3..." ||
                                     renamerWindow.SelectedNumberFormat == "001,002,003..." ||
                                     renamerWindow.SelectedNumberFormat == "0001,0002,0003...";
                    bool isAlphabetic = renamerWindow.SelectedNumberFormat == "A,B,C...";

                    if (isNumeric)
                    {
                        if (!int.TryParse(renamerWindow.StartNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out currentNumber))
                        {
                            TaskDialog.Show("Erreur", "Le numéro de départ doit être un entier pour le format sélectionné.");
                            tx.RollBack();
                            return Result.Failed;
                        }
                    }
                    else if (isAlphabetic)
                    {
                        currentNumber = LettersToNumber((renamerWindow.StartNumber ?? "A").ToUpperInvariant());
                        if (currentNumber <= 0)
                        {
                            TaskDialog.Show("Erreur", "Le départ pour le format alphabétique doit être une lettre (A-Z) ou une séquence valide (AA...).");
                            tx.RollBack();
                            return Result.Failed;
                        }
                    }

                    var failures = new List<string>();

                    foreach (var elemLoc in sortedElements)
                    {
                        Element element = elemLoc.Element;
                        bool isDatum = (element is Grid) || (element is Level);

                        // Construire le numéro / lettres
                        string numberString = "";
                        if (isNumeric)
                        {
                            if (renamerWindow.SelectedNumberFormat == "0001,0002,0003...")
                                numberString = currentNumber.ToString("D4", CultureInfo.InvariantCulture);
                            else if (renamerWindow.SelectedNumberFormat == "001,002,003...")
                                numberString = currentNumber.ToString("D3", CultureInfo.InvariantCulture);
                            else
                                numberString = currentNumber.ToString(CultureInfo.InvariantCulture);
                        }
                        else if (isAlphabetic)
                        {
                            numberString = NumberToLetters(currentNumber);
                        }

                        string proposed = prefix + numberString + suffix;

                        // DATUM: interdire vide
                        if (isDatum && string.IsNullOrWhiteSpace(proposed))
                        {
                            proposed = isAlphabetic ? "A" : "1";
                        }

                        // Unicité pour les Grids (optionnel: idem Levels si besoin)
                        if (element is Grid)
                        {
                            string baseName = proposed;
                            int bump = 0;
                            while (!IsGridNameAvailable(doc, proposed) && bump < 10000)
                            {
                                bump++;
                                proposed = $"{baseName}-{bump}";
                            }
                            if (bump == 10000)
                            {
                                failures.Add($"Grid {element.Id.IntegerValue} : impossible de trouver un nom unique.");
                                if (isNumeric || isAlphabetic) currentNumber++;
                                continue;
                            }
                        }

                        using (SubTransaction st = new SubTransaction(doc))
                        {
                            try
                            {
                                st.Start();

                                bool ok = TrySetStringParameter(element, selectedParameter, proposed);
                                if (!ok)
                                {
                                    failures.Add($"Élément {element.Id.IntegerValue} : paramètre introuvable ou verrouillé.");
                                    st.RollBack();
                                }
                                else
                                {
                                    st.Commit();
                                    if (isNumeric || isAlphabetic) currentNumber++;
                                }
                            }
                            catch (Exception ex)
                            {
                                failures.Add($"Élément {element.Id.IntegerValue} : {ex.Message}");
                                st.RollBack();
                                if (isNumeric || isAlphabetic) currentNumber++;
                            }
                        }
                    }

                    // Avertissements non bloquants
                    if (failures.Count > 0)
                    {
                        TaskDialog.Show("Terminé avec avertissements",
                            string.Join(Environment.NewLine, failures.Take(20)) +
                            (failures.Count > 20 ? $"\n... (+{failures.Count - 20} autres)" : ""));
                    }

                    tx.Commit();
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("Erreur", $"Une erreur est survenue : {ex.Message}");
                    return Result.Failed;
                }
            }
        }

        // ----- Utilitaires -----

        private List<string> GetWritableTextParameters(Element element)
        {
            var textParameters = new List<string>();

            // paramètres texte modifiables par nom
            foreach (Parameter param in element.Parameters)
            {
                if (param.StorageType == StorageType.String && !param.IsReadOnly)
                {
                    var name = param.Definition?.Name;
                    if (!string.IsNullOrEmpty(name) && !textParameters.Contains(name))
                        textParameters.Add(name);
                }
            }

            // S'assurer que les datums soient couverts même si le paramètre n'apparaît pas dans la collection
            if ((element is Grid || element is Level))
            {
                // On ajoute une étiquette "Nom (Datum)" pour guider l'utilisateur (mappée dans TrySetStringParameter)
                if (!textParameters.Contains("Nom (Datum)"))
                    textParameters.Add("Nom (Datum)");
            }

            // Tri alpha pour UX
            textParameters.Sort(StringComparer.CurrentCultureIgnoreCase);
            return textParameters;
        }

        private class ElementLocation
        {
            public Element Element { get; set; }
            public XYZ Location { get; set; }
        }

        // Milieu paramétrique + fallback BBox, transformé dans le repère de la vue
        private List<ElementLocation> GetElementsWithLocations(Document doc, ICollection<ElementId> elementIds, View activeView)
        {
            var elementLocations = new List<ElementLocation>();

            Transform viewT = Transform.Identity;
            viewT.BasisX = activeView.RightDirection;
            viewT.BasisY = activeView.UpDirection;
            viewT.BasisZ = activeView.ViewDirection;
            Transform worldToView = viewT.Inverse;

            foreach (ElementId id in elementIds)
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;

                if (e.Location is LocationPoint lp)
                {
                    XYZ p = worldToView.OfPoint(lp.Point);
                    elementLocations.Add(new ElementLocation { Element = e, Location = p });
                    continue;
                }

                if (e.Location is LocationCurve lc && lc.Curve != null)
                {
                    XYZ mid = lc.Curve.Evaluate(0.5, true);
                    XYZ p = worldToView.OfPoint(mid);
                    elementLocations.Add(new ElementLocation { Element = e, Location = p });
                    continue;
                }

                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb != null)
                {
                    XYZ center = (bb.Min + bb.Max) / 2.0;
                    XYZ p = worldToView.OfPoint(center);
                    elementLocations.Add(new ElementLocation { Element = e, Location = p });
                }
            }

            return elementLocations;
        }

        private bool AllElementsHaveLevel(List<ElementLocation> elements)
        {
            foreach (var elemLoc in elements)
            {
                ElementId levelId = GetElementLevelId(elemLoc.Element);
                if (levelId == ElementId.InvalidElementId)
                    return false;
            }
            return true;
        }

        private List<ElementLocation> SortElementsByGridLocation(List<ElementLocation> elements, double gridSize = 1.0)
        {
            // Regrouper par "bandes" en Y (dans le repère de la vue), de haut -> bas, puis dans chaque bande X croissant
            var grouped = elements
                .GroupBy(e => (int)Math.Floor(e.Location.Y / gridSize))
                .OrderByDescending(g => g.Key);

            var sorted = new List<ElementLocation>();
            foreach (var group in grouped)
                sorted.AddRange(group.OrderBy(e => e.Location.X));

            return sorted;
        }

        private List<ElementLocation> SortElementsByLevelAndLocation(List<ElementLocation> elements, double gridSize, Document doc)
        {
            var groupedByLevel = elements
                .GroupBy(e => GetElementLevelId(e.Element))
                .OrderBy(g => GetLevelElevation(g.Key, doc));

            var sorted = new List<ElementLocation>();
            foreach (var levelGroup in groupedByLevel)
            {
                var inLevel = levelGroup.ToList();
                var sortedInLevel = SortElementsByGridLocation(inLevel, gridSize);
                sorted.AddRange(sortedInLevel);
            }
            return sorted;
        }

        private ElementId GetElementLevelId(Element element)
        {
            if (element is FamilyInstance fi && fi.LevelId != ElementId.InvalidElementId)
                return fi.LevelId;
            if (element is Wall wall)
                return wall.LevelId;
            if (element is Floor floor)
                return floor.LevelId;
            if (element is Ceiling ceiling)
                return ceiling.LevelId;
            if (element is RoofBase roof)
                return roof.LevelId;

            Parameter levelParam = element.get_Parameter(BuiltInParameter.LEVEL_PARAM)
                ?? element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                ?? element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);

            if (levelParam != null && levelParam.HasValue)
                return levelParam.AsElementId();

            return ElementId.InvalidElementId;
        }

        private double GetLevelElevation(ElementId levelId, Document doc)
        {
            if (levelId == ElementId.InvalidElementId) return double.MinValue;
            Level level = doc.GetElement(levelId) as Level;
            return level != null ? level.Elevation : double.MinValue;
        }

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

        private int LettersToNumber(string letters)
        {
            int number = 0;
            foreach (char c in letters)
            {
                if (c < 'A' || c > 'Z') return -1;
                number = number * 26 + (c - 'A' + 1);
            }
            return number;
        }

        // Setter robuste: tente LookupParameter, sinon DATUM_TEXT pour Grids/Levels,
        // et mappe "Nom (Datum)" explicitement
        private bool TrySetStringParameter(Element element, string selectedParameterName, string value)
        {
            // Mapping explicite pour l'entrée synthétique
            bool wantsDatum = string.Equals(selectedParameterName, "Nom (Datum)", StringComparison.InvariantCultureIgnoreCase);

            // 1) par nom (si pas "Nom (Datum)")
            if (!wantsDatum)
            {
                Parameter p = element.LookupParameter(selectedParameterName);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                {
                    return p.Set(value);
                }
            }

            // 2) fallback DATUM_TEXT pour datums ou si l'utilisateur a choisi "Nom (Datum)"
            if (element is Grid || element is Level)
            {
                Parameter datumText = element.get_Parameter(BuiltInParameter.DATUM_TEXT);
                if (datumText != null && !datumText.IsReadOnly)
                    return datumText.Set(value);
            }

            return false;
        }

        private bool IsGridNameAvailable(Document doc, string name)
        {
            return !new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Any(g =>
                {
                    // Essaye DATUM_TEXT sinon Name
                    var p = g.get_Parameter(BuiltInParameter.DATUM_TEXT);
                    string current = p != null ? p.AsString() : g.Name;
                    return string.Equals(current, name, StringComparison.InvariantCultureIgnoreCase);
                });
        }
    }
}

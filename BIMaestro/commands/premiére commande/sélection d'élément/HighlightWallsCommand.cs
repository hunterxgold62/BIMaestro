using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Visualisation
{
    [Transaction(TransactionMode.ReadOnly)]
    public class HighlightElementsByCategoriesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Vérifier la sélection initiale
            var selIds = uidoc.Selection.GetElementIds();
            if (!selIds.Any())
            {
                TaskDialog.Show("Sélection", "Veuillez sélectionner au moins un élément.");
                return Result.Cancelled;
            }

            // Vérifier si la sélection contient au moins une étiquette
            bool selectionContainsTag = false;
            foreach (var eid in selIds)
            {
                var e = doc.GetElement(eid);
                // Vérifie si c'est une étiquette (IndependentTag)
                if (e is IndependentTag)
                {
                    selectionContainsTag = true;
                    break;
                }
            }

            // Préparer un dictionnaire : CategoryId => Liste d'ElementId
            var catDict = new Dictionary<ElementId, List<ElementId>>();

            // Construction du dictionnaire pour tous les éléments sélectionnés
            foreach (var eid in selIds)
            {
                var e = doc.GetElement(eid);
                if (e?.Category != null)
                {
                    var catId = e.Category.Id;

                    // Si on n'a pas encore cette catégorie, on collecte toutes les occurrences
                    if (!catDict.ContainsKey(catId))
                    {
                        var collector = new FilteredElementCollector(doc, uidoc.ActiveView.Id)
                            .WhereElementIsNotElementType()
                            .OfCategoryId(catId);

                        catDict[catId] = collector.ToElementIds().ToList();
                    }
                }
            }

            // Si la sélection contient AU MOINS une étiquette, on récupère TOUTES les étiquettes de la vue
            if (selectionContainsTag)
            {
                // Récupérer toutes les étiquettes dans la vue active
                var allTagsInView = new FilteredElementCollector(doc, uidoc.ActiveView.Id)
                    .OfClass(typeof(IndependentTag))
                    .WhereElementIsNotElementType()
                    .ToElements();

                // On groupe par catégorie (car plusieurs catégories possibles : OST_StructuralFramingTags, etc.)
                var groupedByCategory = allTagsInView
                    .GroupBy(tag => tag.Category.Id)
                    .ToDictionary(g => g.Key, g => g.Select(elem => elem.Id).ToList());

                // On fusionne avec notre dictionnaire existant
                foreach (var kvp in groupedByCategory)
                {
                    if (!catDict.ContainsKey(kvp.Key))
                        catDict[kvp.Key] = new List<ElementId>();

                    catDict[kvp.Key].AddRange(kvp.Value);
                }
            }

            // Construire la liste "Famille : Type" pour la fenêtre
            var allFamilies = new HashSet<string>();
            foreach (var kvp in catDict)
            {
                foreach (var id in kvp.Value)
                {
                    var elem = doc.GetElement(id);
                    if (elem != null)
                    {
                        // Récupération du type (ElementType) pour la famille
                        ElementType et = doc.GetElement(elem.GetTypeId()) as ElementType;
                        if (et != null)
                        {
                            // .Trim() pour éviter les espaces parasites
                            string famName = et.FamilyName.Trim();
                            string typeName = et.Name.Trim();

                            string fullName = famName + " : " + typeName;
                            allFamilies.Add(fullName);
                        }
                    }
                }
            }

            // Triage alphabétique
            var familyList = allFamilies.ToList();
            familyList.Sort();

            // Ouvrir la fenêtre
            var win = new FamilySelectionWindow(familyList);
            bool? dialogResult = win.ShowDialog();
            if (dialogResult != true)
            {
                return Result.Cancelled;
            }

            // Récupérer les sélections de l'utilisateur
            var parents = win.SelectedParentFamilies;   // Parent coché
            var subs = win.SelectedSubFamilies;         // Sous-familles cochées (parent décoché)
            var excluded = win.ExcludedSubFamilies;     // Sous-familles décochées (parent coché)

            // Filtrer la sélection finale
            var finalSel = new List<ElementId>();

            foreach (var kvp in catDict)
            {
                foreach (var eId in kvp.Value)
                {
                    var el = doc.GetElement(eId);
                    if (el != null)
                    {
                        ElementType et = doc.GetElement(el.GetTypeId()) as ElementType;
                        if (et != null)
                        {
                            string famName = et.FamilyName.Trim();
                            string typeName = et.Name.Trim();
                            string fullName = famName + " : " + typeName;

                            bool parentIsSelected = parents.Contains(famName);
                            bool isExcluded = excluded.Contains(fullName);
                            bool isSubSelected = subs.Contains(fullName);

                            // 1) Si le parent est coché => inclure tout
                            //    sauf les sous-types explicitement exclus
                            if (parentIsSelected)
                            {
                                if (!isExcluded)
                                {
                                    finalSel.Add(eId);
                                }
                            }
                            else
                            {
                                // 2) Parent non coché => inclure seulement si sous-type coché
                                if (isSubSelected)
                                {
                                    finalSel.Add(eId);
                                }
                            }
                        }
                    }
                }
            }

            // Supprimer les doublons
            finalSel = finalSel.Distinct().ToList();

            // Mettre à jour la sélection Revit
            uidoc.Selection.SetElementIds(finalSel);

            // Construire un message de confirmation
            int totalElements = finalSel.Count;
            string msg = $"Nombre total d'éléments sélectionnés : {totalElements}\n\n";

            var famCount = new Dictionary<string, int>();
            foreach (var id in finalSel)
            {
                var e = doc.GetElement(id);
                if (e != null)
                {
                    var et = doc.GetElement(e.GetTypeId()) as ElementType;
                    if (et != null)
                    {
                        string fName = et.FamilyName;
                        if (!famCount.ContainsKey(fName))
                            famCount[fName] = 0;
                        famCount[fName]++;
                    }
                }
            }

            msg += "Nombre d'éléments par famille :\n";
            foreach (var kv in famCount)
            {
                msg += $"- {kv.Value} × {kv.Key}\n";
            }

            TaskDialog.Show("Mon Plugin - Sélection", msg);

            return Result.Succeeded;
        }
    }
}

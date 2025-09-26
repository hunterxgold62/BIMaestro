using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Licensing;

namespace Visualisation
{
    [Transaction(TransactionMode.ReadOnly)]
    public class HighlightElementsByCategoriesCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "HighlightElementsByCategoriesCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 1) Vérifier la sélection initiale (sert à déterminer les catégories de travail)
            var selIds = uidoc.Selection.GetElementIds();
            if (!selIds.Any())
            {
                TaskDialog.Show("Sélection", "Veuillez sélectionner au moins un élément.");
                return Result.Cancelled;
            }

            // 2) La sélection contient-elle au moins une étiquette ?
            bool selectionContainsTag = false;
            foreach (var eid in selIds)
            {
                var e = doc.GetElement(eid);
                if (e is IndependentTag)
                {
                    selectionContainsTag = true;
                    break;
                }
            }

            // 3) Préparer un dictionnaire : CategoryId => Liste d'ElementId
            var catDict = new Dictionary<ElementId, List<ElementId>>();

            // ----- Fonction locale : collecter toutes les occurrences d'une catégorie
            IList<ElementId> CollectAllOfCategory(ElementId catId, bool entireModel)
            {
                var bic = (BuiltInCategory)catId.IntegerValue;

                FilteredElementCollector collector = entireModel
                    ? new FilteredElementCollector(doc)
                    : new FilteredElementCollector(doc, uidoc.ActiveView.Id);

                collector = collector.WhereElementIsNotElementType();

                // OfCategory(bic) existe 2023+ ; ElementCategoryFilter(bic) en secours
                try
                {
                    collector = collector.OfCategory(bic);
                }
                catch
                {
                    collector = collector.WherePasses(new ElementCategoryFilter(bic));
                }

                return collector.ToElementIds().ToList();
            }

            // 4) Construire la liste "catégories à couvrir" d'après la sélection
            var selectedCategories = new HashSet<ElementId>(new ElementIdComparer());
            foreach (var eid in selIds)
            {
                var e = doc.GetElement(eid);
                if (e?.Category != null)
                    selectedCategories.Add(e.Category.Id);
            }

            // 5) Ouvrir la fenêtre pour choix familles/types + portée (vue / maquette)
            //    D’abord, on précollecte (par défaut vue active) pour construire l’arbre familles/types.
            var preFamilies = new HashSet<string>();
            foreach (var catId in selectedCategories)
            {
                foreach (var id in CollectAllOfCategory(catId, entireModel: false)) // préview basée sur la vue active
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;

                    ElementType et = doc.GetElement(elem.GetTypeId()) as ElementType;
                    if (et == null) continue;

                    string famName = et.FamilyName?.Trim() ?? "";
                    string typeName = et.Name?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(famName))
                        preFamilies.Add(string.IsNullOrEmpty(typeName) ? famName : (famName + " : " + typeName));
                }
            }

            var familyList = preFamilies.ToList();
            familyList.Sort(StringComparer.CurrentCultureIgnoreCase);

            var win = new FamilySelectionWindow(familyList);
            bool? dialogResult = win.ShowDialog();
            if (dialogResult != true)
                return Result.Cancelled;

            bool entireModel = win.ScopeEntireModel;

            // 6) Construire catDict selon la portée choisie
            foreach (var catId in selectedCategories)
            {
                if (!catDict.ContainsKey(catId))
                    catDict[catId] = CollectAllOfCategory(catId, entireModel).ToList();
            }

            // 7) Si la sélection contient AU MOINS une étiquette, récupérer toutes les étiquettes (selon portée)
            if (selectionContainsTag)
            {
                IEnumerable<Element> allTags = (entireModel
                        ? new FilteredElementCollector(doc)
                        : new FilteredElementCollector(doc, uidoc.ActiveView.Id))
                    .OfClass(typeof(IndependentTag))
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Grouper par catégorie (ex: OST_StructuralFramingTags, etc.)
                var groupedByCategory = allTags
                    .GroupBy(tag => tag.Category.Id)
                    .ToDictionary(g => g.Key, g => g.Select(elem => elem.Id).ToList(), new ElementIdComparer());

                foreach (var kvp in groupedByCategory)
                {
                    if (!catDict.ContainsKey(kvp.Key))
                        catDict[kvp.Key] = new List<ElementId>();

                    catDict[kvp.Key].AddRange(kvp.Value);
                }
            }

            // 8) Construire la liste "Famille : Type" ré-issue de catDict (sur la portée finale)
            var allFamilies = new HashSet<string>();
            foreach (var kvp in catDict)
            {
                foreach (var id in kvp.Value)
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;

                    ElementType et = doc.GetElement(elem.GetTypeId()) as ElementType;
                    if (et == null) continue;

                    string famName = et.FamilyName?.Trim() ?? "";
                    string typeName = et.Name?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(famName))
                        allFamilies.Add(string.IsNullOrEmpty(typeName) ? famName : (famName + " : " + typeName));
                }
            }

            // 9) Récupérer les sélections de l'utilisateur depuis la fenêtre
            var parents = win.SelectedParentFamilies;   // Parent coché
            var subs = win.SelectedSubFamilies;         // Sous-familles cochées (parent décoché)
            var excluded = win.ExcludedSubFamilies;     // Sous-familles décochées (parent coché)

            // 10) Filtrer la sélection finale
            var finalSel = new List<ElementId>();

            foreach (var kvp in catDict)
            {
                foreach (var eId in kvp.Value)
                {
                    var el = doc.GetElement(eId);
                    if (el == null) continue;

                    var et = doc.GetElement(el.GetTypeId()) as ElementType;
                    if (et == null) continue;

                    string famName = et.FamilyName?.Trim() ?? "";
                    string typeName = et.Name?.Trim() ?? "";
                    string fullName = string.IsNullOrEmpty(typeName) ? famName : (famName + " : " + typeName);

                    bool parentIsSelected = parents.Contains(famName);
                    bool isExcluded = excluded.Contains(fullName);
                    bool isSubSelected = subs.Contains(fullName);

                    if (parentIsSelected)
                    {
                        if (!isExcluded)
                            finalSel.Add(eId);
                    }
                    else
                    {
                        if (isSubSelected)
                            finalSel.Add(eId);
                    }
                }
            }

            // 11) Supprimer les doublons & appliquer la sélection
            finalSel = finalSel.Distinct(new ElementIdEqualityComparer()).ToList();
            uidoc.Selection.SetElementIds(finalSel);

            // 12) Feedback utilisateur
            int totalElements = finalSel.Count;
            string msg = $"Portée : {(entireModel ? "maquette entière" : "vue active")}\n" +
                         $"Nombre total d'éléments sélectionnés : {totalElements}\n\n";

            var famCount = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var id in finalSel)
            {
                var e = doc.GetElement(id);
                if (e == null) continue;

                var et = doc.GetElement(e.GetTypeId()) as ElementType;
                if (et == null) continue;

                string fName = et.FamilyName ?? "(Famille inconnue)";
                if (!famCount.ContainsKey(fName)) famCount[fName] = 0;
                famCount[fName]++;
            }

            msg += "Nombre d'éléments par famille :\n";
            foreach (var kv in famCount.OrderBy(k => k.Key, StringComparer.CurrentCultureIgnoreCase))
                msg += $"- {kv.Value} × {kv.Key}\n";

            TaskDialog.Show("Mon Plugin - Sélection", msg);

            return Result.Succeeded;
        }

        // Comparateurs utilitaires pour ElementId
        private class ElementIdComparer : IEqualityComparer<ElementId>
        {
            public bool Equals(ElementId x, ElementId y) => x?.IntegerValue == y?.IntegerValue;
            public int GetHashCode(ElementId obj) => obj?.IntegerValue.GetHashCode() ?? 0;
        }
        private class ElementIdEqualityComparer : IEqualityComparer<ElementId>
        {
            public bool Equals(ElementId x, ElementId y) => x?.IntegerValue == y?.IntegerValue;
            public int GetHashCode(ElementId obj) => obj?.IntegerValue.GetHashCode() ?? 0;
        }
    }
}

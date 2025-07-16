using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.Exceptions;

namespace Visualisation
{
    /// <summary>
    /// Commande principale pour la sélection d'éléments similaires (usage unique)
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SelectSimilarCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // 1. Sélection initiale (un ou plusieurs éléments de référence)
            IList<Reference> initialRefs;
            try
            {
                initialRefs = uiDoc.Selection.PickObjects(
                    ObjectType.Element,
                    "Sélectionnez un ou plusieurs éléments de référence");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // L'utilisateur a annulé l'opération, on quitte silencieusement
                return Result.Cancelled;
            }

            if (initialRefs == null || initialRefs.Count == 0)
            {
                TaskDialog.Show("Select Similar", "Aucun élément sélectionné.");
                return Result.Cancelled;
            }

            // 2. Choix du critère (Type ou Famille)
            bool byType = TaskDialog.Show(
                "Filtre de similarité",
                "Oui = Par Type (même TypeId)\nNon = Par Famille (même FamilyName)",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
            ) == TaskDialogResult.Yes;

            // 3. Construire les ensembles de comparaison
            var typeIds = new HashSet<ElementId>();
            var familyNames = new HashSet<string>();

            foreach (var r in initialRefs)
            {
                Element el = doc.GetElement(r.ElementId);
                if (el == null) continue;

                if (byType)
                {
                    typeIds.Add(el.GetTypeId());
                }
                else
                {
                    // Récupère le nom de famille depuis l'ElementType (système ou chargé)
                    ElementType et = doc.GetElement(el.GetTypeId()) as ElementType;
                    if (et != null && !string.IsNullOrEmpty(et.FamilyName))
                        familyNames.Add(et.FamilyName);
                }
            }

            // 4. Créer et appliquer le filtre personnalisé
            var filter = new SimilarElementFilter(doc, typeIds, familyNames, byType);

            IList<Reference> resultRefs;
            try
            {
                resultRefs = uiDoc.Selection.PickObjects(
                    ObjectType.Element,
                    filter,
                    "Tracez un rectangle pour sélectionner les objets similaires");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // L'utilisateur a annulé l'opération, on quitte silencieusement
                return Result.Cancelled;
            }

            // 5. Mettre à jour la sélection
            var resultIds = new List<ElementId>();
            foreach (var r in resultRefs)
                resultIds.Add(r.ElementId);

            uiDoc.Selection.SetElementIds(resultIds);
            TaskDialog.Show(
                "Select Similar",
                $"{resultIds.Count} éléments similaires sélectionnés.");

            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Filtre de sélection pour ne garder que les éléments similaires
    /// </summary>
    public class SimilarElementFilter : ISelectionFilter
    {
        private readonly Document _doc;
        private readonly HashSet<ElementId> _typeIds;
        private readonly HashSet<string> _familyNames;
        private readonly bool _byType;

        public SimilarElementFilter(
            Document doc,
            HashSet<ElementId> typeIds,
            HashSet<string> familyNames,
            bool byType)
        {
            _doc = doc;
            _typeIds = typeIds;
            _familyNames = familyNames;
            _byType = byType;
        }

        public bool AllowElement(Element elem)
        {
            if (_byType)
            {
                return _typeIds.Contains(elem.GetTypeId());
            }
            else
            {
                // Récupère le nom de famille depuis l'ElementType
                ElementType et = _doc.GetElement(elem.GetTypeId()) as ElementType;
                if (et != null && _familyNames.Contains(et.FamilyName))
                    return true;
                return false;
            }
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            // Autorise tout type de référence; la filtration se fait dans AllowElement
            return true;
        }
    }
}

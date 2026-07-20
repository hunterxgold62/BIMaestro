using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class SafeMoveCommand : BaseTrackedCommand
    {
        private const double VectorTolerance = 1e-9;

        protected override string ButtonId => "SafeMoveButton";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Déplacement protégé", "Aucun document Revit actif.");
                return Result.Failed;
            }

            Document doc = uidoc.Document;
            IList<ElementId> selectedIds;
            try
            {
                selectedIds = GetElementsToMove(uidoc, doc);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            if (selectedIds.Count == 0)
                return Result.Cancelled;

            string validationError = ValidateSelection(doc, selectedIds);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                TaskDialog.Show("Déplacement protégé", validationError);
                return Result.Cancelled;
            }

            XYZ startPoint;
            XYZ endPoint;
            try
            {
                ObjectSnapTypes snaps =
                    ObjectSnapTypes.Endpoints |
                    ObjectSnapTypes.Midpoints |
                    ObjectSnapTypes.Intersections |
                    ObjectSnapTypes.Centers |
                    ObjectSnapTypes.Perpendicular |
                    ObjectSnapTypes.Tangents |
                    ObjectSnapTypes.Quadrants |
                    ObjectSnapTypes.Points |
                    ObjectSnapTypes.Nearest |
                    ObjectSnapTypes.WorkPlaneGrid;

                startPoint = uidoc.Selection.PickPoint(snaps, "Déplacement protégé : choisissez le point de départ");
                endPoint = uidoc.Selection.PickPoint(snaps, "Déplacement protégé : choisissez le point d'arrivée");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            XYZ translation = GetTranslationForView(uidoc.ActiveView, endPoint - startPoint);
            if (translation.GetLength() <= VectorTolerance)
                return Result.Cancelled;

            IList<IdentitySnapshot> identities = CaptureIdentities(doc, selectedIds);
            HashSet<ElementId> protectedIds = new HashSet<ElementId>(selectedIds);
            IList<TagSnapshot> tags = CaptureTags(doc, protectedIds);
            IList<DimensionSnapshot> dimensions = CaptureDimensions(doc, protectedIds);
            var failureGuard = new RollBackOnAnyFailure();

            using (TransactionGroup group = new TransactionGroup(doc, "BIMaestro - Déplacement protégé"))
            {
                group.Start();

                try
                {
                    TransactionStatus moveStatus;
                    using (Transaction transaction = new Transaction(doc, "Déplacer sans dissocier"))
                    {
                        transaction.Start();
                        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(failureGuard);
                        options.SetClearAfterRollback(true);
                        transaction.SetFailureHandlingOptions(options);

                        ElementTransformUtils.MoveElements(doc, selectedIds, translation);
                        doc.Regenerate();
                        moveStatus = transaction.Commit();
                    }

                    if (moveStatus != TransactionStatus.Committed)
                    {
                        group.RollBack();
                        ShowProtectedFailure(failureGuard.Message);
                        return Result.Cancelled;
                    }

                    doc.Regenerate();
                    string protectionError = ValidateProtectedState(doc, identities, tags, dimensions);
                    if (!string.IsNullOrWhiteSpace(protectionError))
                    {
                        group.RollBack();
                        ShowProtectedFailure(protectionError);
                        return Result.Cancelled;
                    }

                    if (group.Assimilate() != TransactionStatus.Committed)
                    {
                        if (group.GetStatus() == TransactionStatus.Started)
                            group.RollBack();
                        ShowProtectedFailure("Revit n'a pas pu finaliser le déplacement.");
                        return Result.Cancelled;
                    }
                }
                catch (Exception ex)
                {
                    if (group.GetStatus() == TransactionStatus.Started)
                        group.RollBack();

                    string detail = !string.IsNullOrWhiteSpace(failureGuard.Message)
                        ? failureGuard.Message
                        : ex.Message;
                    ShowProtectedFailure(detail);
                    return Result.Cancelled;
                }
            }

            uidoc.Selection.SetElementIds(selectedIds);
            return Result.Succeeded;
        }

        private static IList<ElementId> GetElementsToMove(UIDocument uidoc, Document doc)
        {
            ICollection<ElementId> preselection = uidoc.Selection.GetElementIds();
            if (preselection != null && preselection.Count > 0)
                return preselection.Distinct().ToList();

            IList<Reference> references = uidoc.Selection.PickObjects(
                ObjectType.Element,
                new ModelElementSelectionFilter(doc),
                "Déplacement protégé : sélectionnez les objets, puis cliquez sur Terminer");

            return references
                .Select(reference => reference.ElementId)
                .Distinct()
                .ToList();
        }

        private static string ValidateSelection(Document doc, IEnumerable<ElementId> ids)
        {
            foreach (ElementId id in ids)
            {
                Element element = doc.GetElement(id);
                string label = GetElementLabel(element, id);

                if (element == null)
                    return $"L'objet {label} n'existe plus. Aucun déplacement n'a été effectué.";
                if (element is ElementType || element.ViewSpecific || element.Category == null || element.Category.CategoryType != CategoryType.Model)
                    return $"{label} n'est pas un objet de modèle déplaçable. Aucun déplacement n'a été effectué.";
                if (!element.IsModifiable)
                    return $"{label} n'est pas modifiable, probablement à cause des droits de sous-projet. Aucun déplacement n'a été effectué.";
                if (element.Pinned)
                    return $"{label} est épinglé. Désépinglez-le avant d'utiliser Déplacement protégé.";
            }

            return null;
        }

        private static XYZ GetTranslationForView(View view, XYZ rawTranslation)
        {
            if (view is View3D)
                return rawTranslation;

            XYZ normal = view?.ViewDirection;
            if (normal == null || normal.GetLength() <= VectorTolerance)
                return rawTranslation;

            XYZ normalizedNormal = normal.Normalize();
            return rawTranslation - normalizedNormal.Multiply(rawTranslation.DotProduct(normalizedNormal));
        }

        private static IList<IdentitySnapshot> CaptureIdentities(Document doc, IEnumerable<ElementId> ids)
        {
            return ids
                .Select(id => doc.GetElement(id))
                .Where(element => element != null)
                .Select(element => new IdentitySnapshot(element.Id, element.UniqueId))
                .ToList();
        }

        private static IList<TagSnapshot> CaptureTags(Document doc, HashSet<ElementId> protectedIds)
        {
            var snapshots = new List<TagSnapshot>();
            IEnumerable<IndependentTag> tags = new FilteredElementCollector(doc)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>();

            foreach (IndependentTag tag in tags)
            {
                HashSet<ElementId> taggedIds = GetTaggedLocalElementIds(tag);
                HashSet<ElementId> protectedReferences = new HashSet<ElementId>(taggedIds.Where(protectedIds.Contains));
                if (protectedReferences.Count == 0)
                    continue;

                snapshots.Add(new TagSnapshot(tag.Id, protectedReferences, tag.IsOrphaned));
            }

            return snapshots;
        }

        private static IList<DimensionSnapshot> CaptureDimensions(Document doc, HashSet<ElementId> protectedIds)
        {
            var snapshots = new List<DimensionSnapshot>();
            IEnumerable<Dimension> dimensions = new FilteredElementCollector(doc)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>();

            foreach (Dimension dimension in dimensions)
            {
                HashSet<ElementId> referencedIds = GetDimensionElementIds(dimension);
                HashSet<ElementId> protectedReferences = new HashSet<ElementId>(referencedIds.Where(protectedIds.Contains));
                if (protectedReferences.Count > 0)
                    snapshots.Add(new DimensionSnapshot(dimension.Id, protectedReferences));
            }

            return snapshots;
        }

        private static string ValidateProtectedState(
            Document doc,
            IEnumerable<IdentitySnapshot> identities,
            IEnumerable<TagSnapshot> tags,
            IEnumerable<DimensionSnapshot> dimensions)
        {
            foreach (IdentitySnapshot snapshot in identities)
            {
                Element element = doc.GetElement(snapshot.Id);
                if (element == null || !string.Equals(element.UniqueId, snapshot.UniqueId, StringComparison.Ordinal))
                    return $"L'identité de l'objet {snapshot.Id.IntegerValue} aurait été remplacée.";
            }

            foreach (TagSnapshot snapshot in tags)
            {
                IndependentTag tag = doc.GetElement(snapshot.Id) as IndependentTag;
                if (tag == null)
                    return $"L'étiquette {snapshot.Id.IntegerValue} aurait été supprimée.";
                if (!snapshot.WasOrphaned && tag.IsOrphaned)
                    return $"L'étiquette {snapshot.Id.IntegerValue} serait devenue orpheline.";

                HashSet<ElementId> currentReferences = GetTaggedLocalElementIds(tag);
                if (!snapshot.ProtectedReferences.IsSubsetOf(currentReferences))
                    return $"L'étiquette {snapshot.Id.IntegerValue} aurait perdu sa référence.";
            }

            foreach (DimensionSnapshot snapshot in dimensions)
            {
                Dimension dimension = doc.GetElement(snapshot.Id) as Dimension;
                if (dimension == null)
                    return $"La cotation {snapshot.Id.IntegerValue} aurait été supprimée.";

                HashSet<ElementId> currentReferences = GetDimensionElementIds(dimension);
                if (!snapshot.ProtectedReferences.IsSubsetOf(currentReferences))
                    return $"La cotation {snapshot.Id.IntegerValue} aurait perdu sa référence.";
            }

            return null;
        }

        private static HashSet<ElementId> GetTaggedLocalElementIds(IndependentTag tag)
        {
            try
            {
                return new HashSet<ElementId>(tag.GetTaggedLocalElementIds());
            }
            catch
            {
                return new HashSet<ElementId>();
            }
        }

        private static HashSet<ElementId> GetDimensionElementIds(Dimension dimension)
        {
            var result = new HashSet<ElementId>();
            ReferenceArray references = dimension?.References;
            if (references == null)
                return result;

            foreach (Reference reference in references)
            {
                if (reference?.ElementId != null && reference.ElementId != ElementId.InvalidElementId)
                    result.Add(reference.ElementId);
            }

            return result;
        }

        private static string GetElementLabel(Element element, ElementId id)
        {
            string name = element?.Name;
            return string.IsNullOrWhiteSpace(name)
                ? $"ID {id.IntegerValue}"
                : $"« {name} » (ID {id.IntegerValue})";
        }

        private static void ShowProtectedFailure(string detail)
        {
            string reason = string.IsNullOrWhiteSpace(detail)
                ? "Revit a signalé une contrainte ou un risque de suppression."
                : detail;

            TaskDialog.Show(
                "Déplacement protégé",
                "Le déplacement a été annulé intégralement.\n\n" +
                reason + "\n\nAucun objet, paramètre, étiquette ou cotation n'a été modifié.");
        }

        private sealed class ModelElementSelectionFilter : ISelectionFilter
        {
            private readonly Document _document;

            public ModelElementSelectionFilter(Document document)
            {
                _document = document;
            }

            public bool AllowElement(Element element)
            {
                return element != null &&
                       element.Document.Equals(_document) &&
                       !(element is ElementType) &&
                       !element.ViewSpecific &&
                       element.Category != null &&
                       element.Category.CategoryType == CategoryType.Model;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return true;
            }
        }

        private sealed class RollBackOnAnyFailure : IFailuresPreprocessor
        {
            public string Message { get; private set; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                if (failures == null || failures.Count == 0)
                    return FailureProcessingResult.Continue;

                Message = string.Join(
                    "\n",
                    failures
                        .Select(failure => failure.GetDescriptionText())
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .Distinct());

                return FailureProcessingResult.ProceedWithRollBack;
            }
        }

        private sealed class IdentitySnapshot
        {
            public IdentitySnapshot(ElementId id, string uniqueId)
            {
                Id = id;
                UniqueId = uniqueId;
            }

            public ElementId Id { get; }
            public string UniqueId { get; }
        }

        private sealed class TagSnapshot
        {
            public TagSnapshot(ElementId id, HashSet<ElementId> protectedReferences, bool wasOrphaned)
            {
                Id = id;
                ProtectedReferences = protectedReferences;
                WasOrphaned = wasOrphaned;
            }

            public ElementId Id { get; }
            public HashSet<ElementId> ProtectedReferences { get; }
            public bool WasOrphaned { get; }
        }

        private sealed class DimensionSnapshot
        {
            public DimensionSnapshot(ElementId id, HashSet<ElementId> protectedReferences)
            {
                Id = id;
                ProtectedReferences = protectedReferences;
            }

            public ElementId Id { get; }
            public HashSet<ElementId> ProtectedReferences { get; }
        }
    }
}

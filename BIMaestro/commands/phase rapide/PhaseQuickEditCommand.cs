using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class PhaseQuickEditCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "PhaseQuickEditCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Phases rapides", "Aucun document Revit actif.");
                return Result.Failed;
            }

            Document doc = uidoc.Document;
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds == null || selectedIds.Count == 0)
            {
                TaskDialog.Show("Phases rapides", "Selectionne d'abord les objets a modifier.");
                return Result.Cancelled;
            }

            List<Phase> phases = doc.Phases.Cast<Phase>().ToList();
            if (phases.Count == 0)
            {
                TaskDialog.Show("Phases rapides", "Aucune phase n'a ete trouvee dans ce projet.");
                return Result.Failed;
            }

            var window = new PhaseQuickEditWindow(phases, selectedIds.Count);
            bool? dialogResult = window.ShowDialog();
            if (dialogResult != true)
                return Result.Cancelled;

            if (!window.ChangeCreatedPhase && !window.ChangeDemolishedPhase)
            {
                TaskDialog.Show("Phases rapides", "Choisis au moins une phase a modifier.");
                return Result.Cancelled;
            }

            Dictionary<int, int> phaseOrder = BuildPhaseOrder(phases);
            int updated = 0;
            int skipped = 0;
            int failed = 0;

            using (Transaction tx = new Transaction(doc, "BIMaestro - Modifier les phases"))
            {
                tx.Start();

                foreach (ElementId id in selectedIds)
                {
                    Element element = doc.GetElement(id);
                    if (element == null || element is ElementType)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        bool changed = ApplyPhaseChanges(
                            element,
                            window.ChangeCreatedPhase,
                            window.SelectedCreatedPhaseId,
                            window.ChangeDemolishedPhase,
                            window.SelectedDemolishedPhaseId,
                            phaseOrder);

                        if (changed)
                            updated++;
                        else
                            skipped++;
                    }
                    catch
                    {
                        failed++;
                    }
                }

                tx.Commit();
            }

            TaskDialog.Show(
                "Phases rapides",
                "Modification terminee.\n\n" +
                $"Objets modifies : {updated}\n" +
                $"Objets ignores : {skipped}\n" +
                $"Objets en echec : {failed}");

            return Result.Succeeded;
        }

        private static bool ApplyPhaseChanges(
            Element element,
            bool changeCreatedPhase,
            ElementId createdPhaseId,
            bool changeDemolishedPhase,
            ElementId demolishedPhaseId,
            Dictionary<int, int> phaseOrder)
        {
            Parameter createdParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
            Parameter demolishedParam = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);

            ElementId finalCreatedPhaseId = changeCreatedPhase
                ? createdPhaseId
                : GetParameterElementId(createdParam);

            ElementId finalDemolishedPhaseId = changeDemolishedPhase
                ? demolishedPhaseId
                : GetParameterElementId(demolishedParam);

            if (!IsPhaseOrderValid(finalCreatedPhaseId, finalDemolishedPhaseId, phaseOrder))
                return false;

            bool changed = false;

            if (changeCreatedPhase && CanSetElementIdParameter(createdParam))
            {
                if (!AreSameElementId(GetParameterElementId(createdParam), createdPhaseId))
                {
                    createdParam.Set(createdPhaseId);
                    changed = true;
                }
            }

            if (changeDemolishedPhase && CanSetElementIdParameter(demolishedParam))
            {
                if (!AreSameElementId(GetParameterElementId(demolishedParam), demolishedPhaseId))
                {
                    demolishedParam.Set(demolishedPhaseId);
                    changed = true;
                }
            }

            return changed;
        }

        private static Dictionary<int, int> BuildPhaseOrder(IList<Phase> phases)
        {
            var result = new Dictionary<int, int>();
            for (int i = 0; i < phases.Count; i++)
            {
                result[phases[i].Id.IntegerValue] = i;
            }

            return result;
        }

        private static bool IsPhaseOrderValid(
            ElementId createdPhaseId,
            ElementId demolishedPhaseId,
            Dictionary<int, int> phaseOrder)
        {
            if (AreSameElementId(demolishedPhaseId, ElementId.InvalidElementId))
                return true;

            if (AreSameElementId(createdPhaseId, ElementId.InvalidElementId))
                return true;

            int createdOrder;
            int demolishedOrder;
            if (!phaseOrder.TryGetValue(createdPhaseId.IntegerValue, out createdOrder) ||
                !phaseOrder.TryGetValue(demolishedPhaseId.IntegerValue, out demolishedOrder))
            {
                return true;
            }

            return demolishedOrder >= createdOrder;
        }

        private static bool CanSetElementIdParameter(Parameter parameter)
        {
            return parameter != null &&
                   !parameter.IsReadOnly &&
                   parameter.StorageType == StorageType.ElementId;
        }

        private static ElementId GetParameterElementId(Parameter parameter)
        {
            if (parameter == null || parameter.StorageType != StorageType.ElementId)
                return ElementId.InvalidElementId;

            return parameter.AsElementId();
        }

        private static bool AreSameElementId(ElementId first, ElementId second)
        {
            int firstValue = first == null ? ElementId.InvalidElementId.IntegerValue : first.IntegerValue;
            int secondValue = second == null ? ElementId.InvalidElementId.IntegerValue : second.IntegerValue;
            return firstValue == secondValue;
        }
    }
}

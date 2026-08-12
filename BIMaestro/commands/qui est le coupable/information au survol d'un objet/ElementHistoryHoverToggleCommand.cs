using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;

namespace Analyse
{
    [Transaction(TransactionMode.Manual)]
    public class ElementHistoryHoverToggleCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ElementHistoryHoverToggle";

        protected override Result OnExecute(
            ExternalCommandData data,
            ref string message,
            ElementSet elements)
        {
            Document document = data.Application?.ActiveUIDocument?.Document;
            bool enabled = ElementHistoryHoverInfoService.Toggle(document);

            AppUI.UpdatePushButtonPresentation(
                "ElementHistoryHoverToggle",
                enabled ? "Infos objet : ON" : "Infos objet : OFF",
                enabled
                    ? "Informations sur le dernier changement activées. Cliquez pour les désactiver."
                    : "Informations sur le dernier changement désactivées. Cliquez pour les activer.");

            return Result.Succeeded;
        }
    }
}

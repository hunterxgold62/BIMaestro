using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;

namespace BIMaestro.UI
{
    [Transaction(TransactionMode.Manual)]
    public class RadialMenuButtonsCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "RadialMenuButtonsCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            RadialButtonsService.Show(data.Application);
            return Result.Succeeded;
        }
    }
}

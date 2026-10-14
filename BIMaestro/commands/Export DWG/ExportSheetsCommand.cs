using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using Licensing;
using Autodesk.Revit.DB;

namespace Visualisation
{
    [Transaction(TransactionMode.Manual)]
    public class ExportSheetsCommand : BaseTrackedCommand
    {

        protected override string ButtonId => "ExportSheetsCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            var wnd = new ExportWindow(commandData);
            wnd.ShowDialog();  // Fenêtre modale pour rester dans le contexte Revit
            if (wnd.OperationCounts != null)
                RecordOperationResult(ButtonId, "export_dwg", false, wnd.OperationCounts);
            return Result.Succeeded;
        }

       
    }
}

using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;

namespace MonPluginRevit
{
    [Transaction(TransactionMode.Manual)]
    public class ExportSheetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              Autodesk.Revit.DB.ElementSet elements)
        {
            // Lance la fenêtre maître
            var wnd = new ExportWindow(commandData);
            wnd.ShowDialog();
            return Result.Succeeded;
        }
    }
}

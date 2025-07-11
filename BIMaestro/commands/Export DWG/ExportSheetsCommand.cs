using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;

namespace Visualisation
{
    [Transaction(TransactionMode.Manual)]
    public class ExportSheetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              Autodesk.Revit.DB.ElementSet elements)
        {
            var wnd = new ExportWindow(commandData);
            wnd.ShowDialog();  // Fenêtre modale pour rester dans le contexte Revit
            return Result.Succeeded;
        }
    }
}

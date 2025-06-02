using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace IA
{
    [Transaction(TransactionMode.Manual)]
    public class ScreenCaptureCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var mainWindow = new MainWindow();
                mainWindow.Show(); // Utilisez Show() au lieu de ShowDialog() pour rendre la fenêtre non modale
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
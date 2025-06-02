using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows;

namespace MyRevitTroll
{
    [Transaction(TransactionMode.Manual)]
    public class CommandStickmanAnimation : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            // Afficher une fenêtre d'animation
            SimpleGangnamStickmanWindow window = new SimpleGangnamStickmanWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();

            return Result.Succeeded;
        }
    }
}

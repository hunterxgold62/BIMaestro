using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;

namespace MyRevitPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class ResetTabItemRandomColorsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Réinitialise le dictionnaire des couleurs (les couleurs aléatoires)
                CombinedColoringApplication.ResetRandomColors();

                // Récupère le handle de la fenêtre principale et réapplique la coloration sur les TabItems
                IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;
                CombinedColoringApplication.ApplyTabItemColoring(mainWindowHandle);

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

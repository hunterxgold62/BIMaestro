using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;

namespace Couleur
{
    [Transaction(TransactionMode.Manual)]
    public class ResetTabItemRandomColorsCommand : BaseTrackedCommand
    {

        protected override string ButtonId => "ResetTabItemRandomColorsCommand";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
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

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;

using System;
using System.Windows.Interop;

namespace BIMaestro.Bonus
{
    [Transaction(TransactionMode.Manual)]
    public class FlappyBirdCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "FlappyBirdCommand";

        protected override Result OnExecute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiapp = commandData.Application;

                var win = new FlappyBirdWindow();

                var helper = new WindowInteropHelper(win)
                {
                    Owner = uiapp.MainWindowHandle
                };

                win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
                win.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }
    }
}

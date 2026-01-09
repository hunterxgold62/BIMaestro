using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.RibbonLayout;
using Licensing;

namespace BIMaestro.RibbonLayout
{
    [Transaction(TransactionMode.Manual)]
    public class RibbonLayoutCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "RibbonLayoutCommand";

        protected override Result OnExecute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var definitions = AppUI.BuildDefaultRibbonDefinitions(assemblyPath);
            var layout = RibbonLayoutConfigManager.LoadLayout(definitions);

            var window = new RibbonLayoutWindow(definitions, layout)
            {
                Owner = commandData.Application?.MainWindowHandle != null
                    ? System.Windows.Interop.HwndSource.FromHwnd(commandData.Application.MainWindowHandle)?.RootVisual as System.Windows.Window
                    : null
            };

            var result = window.ShowDialog();
            if (result == true)
            {
                RibbonLayoutConfigManager.SaveLayout(window.GetUpdatedLayout());
            }

            return Result.Succeeded;
        }
    }
}
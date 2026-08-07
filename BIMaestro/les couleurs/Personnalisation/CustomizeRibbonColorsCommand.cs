using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Windows.Interop;

namespace Couleur
{
    [Transaction(TransactionMode.Manual)]
    public class CustomizeRibbonColorsCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "CustomizeRibbonColorsCommand";

        protected override Result OnExecute(
            ExternalCommandData data,
            ref string message,
            ElementSet elements)
        {
            try
            {
                ColoringStateManager.LoadState();

                var window = new ColorPreferencesWindow(
                    data.Application.MainWindowHandle,
                    data.Application.ActiveUIDocument?.Document);
                new WindowInteropHelper(window)
                {
                    Owner = data.Application.MainWindowHandle
                };

                if (window.ShowDialog() == true)
                    ReapplyColors(data.Application.MainWindowHandle);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static void ReapplyColors(IntPtr mainWindowHandle)
        {
            CombinedColoringApplication.ResetColorings(mainWindowHandle);
            PartialColoringHelper.ResetPartialColoring(mainWindowHandle);

            if (!ColoringStateManager.IsColoringActive)
                return;

            CombinedColoringApplication.ApplyTabItemColoring(mainWindowHandle);

            if (ColoringStateManager.IsFullMode)
                CombinedColoringApplication.ApplyPapanoelColoring(mainWindowHandle);
            else
                PartialColoringHelper.ApplyPartialColoring(mainWindowHandle);

            RevitRibbonGlobalColoring.Apply(mainWindowHandle);
        }
    }
}

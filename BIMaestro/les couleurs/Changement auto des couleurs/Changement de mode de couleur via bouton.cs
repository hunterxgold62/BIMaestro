using System;
using System.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;

namespace MyRevitPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleCombinedColoringCommand : IExternalCommand
    {
        private const int DoubleClickThresholdMs = 300;
        private static bool _waitingForDoubleClick = false;
        private static Timer _singleClickTimer = null;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ColoringStateManager.LoadState();

                if (!_waitingForDoubleClick)
                {
                    // 1er clic => on attend un possible second clic
                    _waitingForDoubleClick = true;

                    _singleClickTimer = new Timer(SingleClickAction, commandData, DoubleClickThresholdMs, Timeout.Infinite);
                }
                else
                {
                    // 2e clic => double clic => switch mode
                    _waitingForDoubleClick = false;

                    if (_singleClickTimer != null)
                    {
                        _singleClickTimer.Dispose();
                        _singleClickTimer = null;
                    }

                    DoDoubleClick(commandData);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Se déclenche après DoubleClickThresholdMs si aucun second clic n'a eu lieu
        /// => on fait l'action simple clic (toggle on/off).
        /// </summary>
        private void SingleClickAction(object state)
        {
            _waitingForDoubleClick = false;

            if (state is ExternalCommandData cdata)
            {
                DoSingleClick(cdata);
            }
        }

        private void DoSingleClick(ExternalCommandData commandData)
        {
            try
            {
                // Toggle on/off
                ColoringStateManager.ToggleColoring();

                IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;
                CombinedColoringApplication.ResetColorings(mainWindowHandle);
                PartialColoringHelper.ResetPartialColoring(mainWindowHandle);

                if (ColoringStateManager.IsColoringActive)
                {
                    CombinedColoringApplication.ApplyTabItemColoring(mainWindowHandle);
                    if (ColoringStateManager.IsFullMode)
                    {
                        CombinedColoringApplication.ApplyPapanoelColoring(mainWindowHandle);
                    }
                    else
                    {
                        PartialColoringHelper.ApplyPartialColoring(mainWindowHandle);
                    }
                }
            }
            catch
            {
                // Eviter de planter Revit
            }
        }

        private void DoDoubleClick(ExternalCommandData commandData)
        {
            try
            {
                IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;
                // Switch de mode
                ColoringStateManager.SwitchMode();

                CombinedColoringApplication.ResetColorings(mainWindowHandle);
                PartialColoringHelper.ResetPartialColoring(mainWindowHandle);

                if (ColoringStateManager.IsColoringActive)
                {
                    CombinedColoringApplication.ApplyTabItemColoring(mainWindowHandle);
                    if (ColoringStateManager.IsFullMode)
                    {
                        CombinedColoringApplication.ApplyPapanoelColoring(mainWindowHandle);
                    }
                    else
                    {
                        PartialColoringHelper.ApplyPartialColoring(mainWindowHandle);
                    }
                }
            }
            catch
            {
                // Eviter de planter Revit
            }
        }
    }
}

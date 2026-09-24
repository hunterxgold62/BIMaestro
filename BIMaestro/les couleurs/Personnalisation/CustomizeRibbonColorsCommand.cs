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
        private static ColorPreferencesWindow _openWindow;
        private static ExternalEvent _openPreferencesEvent;
        private static readonly OpenPreferencesHandler _openPreferencesHandler = new OpenPreferencesHandler();

        private sealed class OpenPreferencesHandler : IExternalEventHandler
        {
            public string GetName() => "BIMaestro - Ouvrir les couleurs";

            public void Execute(UIApplication app)
            {
                try { ShowPreferences(app); }
                catch (Exception ex)
                {
                    TaskDialog.Show("BIMaestro", "Impossible d’ouvrir la personnalisation des couleurs : " + ex.Message);
                }
            }
        }

        internal static void EnsureOpenEvent()
        {
            if (_openPreferencesEvent == null)
                _openPreferencesEvent = ExternalEvent.Create(_openPreferencesHandler);
        }

        internal static void RequestOpenPreferences()
        {
            if (_openWindow != null && _openWindow.IsVisible)
            {
                _openWindow.Activate();
                return;
            }
            if (_openPreferencesEvent == null)
                throw new InvalidOperationException("La commande Revit doit être relancée pour ouvrir les couleurs.");
            ExternalEventRequest result = _openPreferencesEvent.Raise();
            if (result != ExternalEventRequest.Accepted && result != ExternalEventRequest.Pending)
                throw new InvalidOperationException("Revit est occupé. Réessayez dans un instant.");
        }
        protected override string ButtonId => "CustomizeRibbonColorsCommand";

        protected override Result OnExecute(
            ExternalCommandData data,
            ref string message,
            ElementSet elements)
        {
            try
            {
                ShowPreferences(data);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        internal static void ShowPreferences(ExternalCommandData data)
        {
            if (data == null) return;
            ShowPreferences(data.Application);
        }

        private static void ShowPreferences(UIApplication app)
        {
            if (app == null) return;

            if (_openWindow != null && _openWindow.IsVisible)
            {
                _openWindow.Activate();
                return;
            }

            ColoringStateManager.LoadState();

            var window = new ColorPreferencesWindow(
                app.MainWindowHandle,
                app.ActiveUIDocument?.Document);
            new WindowInteropHelper(window)
            {
                Owner = app.MainWindowHandle
            };

            _openWindow = window;
            window.Closed += (_, __) =>
            {
                if (ReferenceEquals(_openWindow, window))
                    _openWindow = null;
            };
            window.Show();
            window.Activate();
        }

        internal static void ReapplyColors(IntPtr mainWindowHandle)
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

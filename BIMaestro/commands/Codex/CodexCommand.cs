using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows.Interop;

namespace BIMaestro.Codex
{
    [Transaction(TransactionMode.Manual)]
    public class CodexCommand : Licensing.BaseTrackedCommand
    {
        protected override string ButtonId => "CodexChat";
        private static CodexWindow window;
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (window != null) { window.Activate(); return Result.Succeeded; }
            var bridge = new CodexRevitBridge(data.Application.ActiveUIDocument?.Document, data.Application.Application.VersionNumber);
            bridge.AttachEvent(ExternalEvent.Create(bridge));
            window = new CodexWindow(bridge);
            new WindowInteropHelper(window).Owner = data.Application.MainWindowHandle;
            window.Closed += (_, __) => window = null;
            window.Show();
            return Result.Succeeded;
        }

        internal static void OpenDedicatedSession(UIApplication app)
        {
            if (window != null) { window.Activate(); return; }
            var bridge = new CodexRevitBridge(null, app.Application.VersionNumber) { DedicatedSession = true };
            bridge.AttachEvent(ExternalEvent.Create(bridge));
            window = new CodexWindow(bridge);
            new WindowInteropHelper(window).Owner = app.MainWindowHandle;
            window.Closed += (_, __) => window = null;
            window.Show();
        }

        internal static void Shutdown() { window?.Close(); }
    }
}

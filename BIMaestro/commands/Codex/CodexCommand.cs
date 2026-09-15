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
            var bridge = new CodexRevitBridge(data.Application.ActiveUIDocument?.Document);
            bridge.AttachEvent(ExternalEvent.Create(bridge));
            window = new CodexWindow(bridge);
            new WindowInteropHelper(window).Owner = data.Application.MainWindowHandle;
            window.Closed += (_, __) => window = null;
            window.Show();
            return Result.Succeeded;
        }

        internal static void Shutdown() { window?.Close(); }
    }
}

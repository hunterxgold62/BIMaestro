using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows.Interop;

namespace BIMaestro.Codex
{
    [Transaction(TransactionMode.Manual)]
    public class CodexMepCommand : Licensing.BaseTrackedCommand
    {
        protected override string ButtonId => "MepAssistantAi";
        private static CodexWindow window;

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (window != null) { window.Activate(); return Result.Succeeded; }
            var document = data.Application.ActiveUIDocument?.Document;
            if (document == null || document.IsFamilyDocument)
            {
                TaskDialog.Show("BIMaestro — Assistant MEP", "Ouvrez un projet Revit contenant les équipements à raccorder.");
                return Result.Cancelled;
            }

            var bridge = new CodexRevitBridge(document, data.Application.Application.VersionNumber) { MepMode = true };
            bridge.AttachEvent(ExternalEvent.Create(bridge));
            window = new CodexWindow(bridge, mepMode: true);
            new WindowInteropHelper(window).Owner = data.Application.MainWindowHandle;
            window.Closed += (_, __) => window = null;
            window.Show();
            return Result.Succeeded;
        }

        internal static void Shutdown() { window?.Close(); }
    }
}

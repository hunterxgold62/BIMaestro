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
        private static CodexDedicatedRevitLink dedicatedLink;
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

        internal static void StartDedicatedLink(UIApplication app)
        {
            if (dedicatedLink != null) return;
            string pipe = System.Environment.GetEnvironmentVariable("BIMAESTRO_FAMILY_PIPE");
            string secret = System.Environment.GetEnvironmentVariable("BIMAESTRO_FAMILY_SECRET");
            if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(secret)) return;
            dedicatedLink = new CodexDedicatedRevitLink(pipe, secret, app);
            System.Environment.SetEnvironmentVariable("BIMAESTRO_FAMILY_PIPE", null, System.EnvironmentVariableTarget.Process);
            System.Environment.SetEnvironmentVariable("BIMAESTRO_FAMILY_SECRET", null, System.EnvironmentVariableTarget.Process);
        }

        internal static void Shutdown() { window?.Close(); dedicatedLink?.Dispose(); dedicatedLink = null; }
    }
}

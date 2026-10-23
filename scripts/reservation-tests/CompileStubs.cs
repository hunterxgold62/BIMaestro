// External application services only. Revit geometry, filtering and the entire
// Autoréservation implementation are compiled from their unmodified sources.
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

#if REVIT2025_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
#endif

namespace Licensing
{
    public abstract class BaseTrackedCommand : IExternalCommand
    {
        protected abstract string ButtonId { get; }
        protected abstract Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements);
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => OnExecute(data, ref message, elements);
        protected void RecordOperationResult(string tool, string mode, bool tutorial, IDictionary<string, int> counts) { }
    }
}
namespace BIMaestro.Localization
{
    internal static class UiLanguage
    {
        internal static bool IsEnglish => false;
        internal static string T(string french, string english = null) => french;
    }
}
namespace Modification
{
    internal static class ThemeManager { internal static void EnsureThemeLoaded() { } }
}
namespace BIMaestro.Tutorials
{
    internal static class DemoTourService
    {
        internal static bool AttachIfRequested(string name, object window) => false;
        internal static void StartInWindow(string name, object window) { }
    }
    internal static class DemoProjectBuilder { internal const string DemoPrefix = "TEST_"; }
    internal static class DemoTourMessage { internal static void Show(IntPtr owner, string title, string body, string button = null) { } }
    internal static class DemoTourCompletion { internal static void Show(IntPtr owner, string name, string body) { } }
}
namespace Dynamo.Applications
{
    internal sealed class DynamoRevit { internal void ExecuteCommand(DynamoRevitCommandData command) { } }
    internal sealed class DynamoRevitCommandData
    {
        internal DynamoRevitCommandData(ExternalCommandData command) { }
        internal IDictionary<string, string> JournalData { get; set; }
    }
}
namespace Dynamo.Applications.Properties
{
    internal static class JournalKeys
    {
        internal const string ShowUiKey = "ShowUi", AutomationModeKey = "Automation", DynPathKey = "Path",
            DynPathExecuteKey = "Execute", ForceManualRunKey = "Manual", ModelShutDownKey = "Shutdown", ModelNodesInfo = "Nodes";
    }
}

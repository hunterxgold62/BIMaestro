namespace BIMaestro.Localization
{
    public static class UiLanguage
    {
        public static string T(string value) => value;
        public static string T(string fr, string en) => fr;
    }
}
namespace Modification { public static class ThemeManager { public static void EnsureThemeLoaded() { } } }
namespace Licensing
{
    internal static class OperationTelemetry
    {
        internal static void Track(string toolId, string label, string mode, bool tutorial,
            System.Collections.Generic.IDictionary<string, int> counts) { }
        internal static void Observe(Autodesk.Revit.UI.UIApplication app, string toolId,
            string label, string mode, bool tutorial, System.Action action) => action();
    }
}
// Production SmartCheckState now uses PreferenceStorage. Keep test preferences isolated.
namespace BIMaestro.Storage
{
    public static class PreferenceStorage
    {
        public static string Folder(string area)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(
                typeof(Analyse.Tests.NativeValidation).Assembly.Location), "test-documents", area);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
        public static string FilePath(string area, string name) => System.IO.Path.Combine(Folder(area), name);
    }
}
namespace Analyse { public static class SmartClashCommand { public const string Smart3DName = "BIMaestro – SmartCheck 3D"; } }
// Tutorial orchestration is outside this bench; the actual Clash tutorial partial and fixture are compiled.
namespace BIMaestro.Tutorials
{
    internal static class DemoTourService
    {
        internal static bool AttachIfRequested(string id, System.Windows.Window window) => false;
        internal static bool IsActive(System.Windows.Window window) => false;
        internal static void StartInWindow(string id, System.Windows.Window window) { }
        internal static void ReportAction(System.Windows.Window window, string action) { }
        internal static System.Windows.Window ObserveHistoryResult(System.Windows.Window window, System.IntPtr owner, string title, string text, System.Action done) => null;
    }
}

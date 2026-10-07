namespace BIMaestro.Localization
{
    public static class UiLanguage
    {
        public static string T(string value) => value;
        public static string T(string fr, string en) => fr;
    }
}
namespace Modification { public static class ThemeManager { public static void EnsureThemeLoaded() { } } }
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

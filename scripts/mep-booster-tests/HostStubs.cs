// Only the unrelated licensing/application host is replaced for standalone lifecycle tests.
// Revit API types and all three production MEP Booster files are compiled unchanged.
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace Licensing
{
    public abstract class BaseTrackedCommand : IExternalCommand
    {
        protected abstract string ButtonId { get; }
        protected abstract Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements);
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => OnExecute(data, ref message, elements);
    }
}
internal static class BIMaestroApp
{
    internal static UIControlledApplication UIControlledApp { get; set; }
}

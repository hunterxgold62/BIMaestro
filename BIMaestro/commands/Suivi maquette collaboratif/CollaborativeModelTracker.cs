using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System.Windows.Interop;
using BIMaestro.Localization;

namespace Analyse
{
    [Transaction(TransactionMode.Manual)]
    public class CollaborativeModelTrackerCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "CollaborativeModelTracker";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                TaskDialog.Show(UiLanguage.Get("CollaborativeModelTracker.ModelTracking"), UiLanguage.Get("CollaborativeModelTracker.NoActiveDocument"));
                return Result.Cancelled;
            }

            var window = new CollaborativeModelTrackerWindow(uidoc.Document, uiapp);
            new WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System.Linq;
using System.Windows.Interop;

namespace Analyse
{
    [Transaction(TransactionMode.Manual)]
    public class MainCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "MainCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application?.ActiveUIDocument;
            if (uidoc?.Document == null) return Result.Cancelled;

            var selectedId = uidoc.Selection.GetElementIds().FirstOrDefault();
            Element selected = null;
            if (selectedId != null && selectedId != ElementId.InvalidElementId)
                selected = uidoc.Document.GetElement(selectedId);

            var win = new ElementHistoryWindow(uidoc, selected);
            new WindowInteropHelper(win) { Owner = data.Application.MainWindowHandle };
            win.Show();

            return Result.Succeeded;
        }
    }
}

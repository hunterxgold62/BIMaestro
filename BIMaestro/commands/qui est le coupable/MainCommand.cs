using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System.Collections.Generic;
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

            var defaultAction = selected == null ? "delete" : null;
            List<ElementHistoryEvent> initialEvents = null;
            try
            {
                ElementHistoryTracker.PrimeDocument(uidoc.Document);
                initialEvents = ElementHistoryWindow.LoadInitialHistory(uidoc.Document, selected, out defaultAction);
            }
            catch
            {
            }

            var win = new ElementHistoryWindow(uidoc, selected, initialEvents, defaultAction);
            new WindowInteropHelper(win) { Owner = data.Application.MainWindowHandle };
            win.Show();

            return Result.Succeeded;
        }
    }
}

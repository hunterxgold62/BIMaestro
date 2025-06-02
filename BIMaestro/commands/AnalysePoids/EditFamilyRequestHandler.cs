using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace AnalysePoidsPlugin
{
    public class SelectionRequestHandler : IExternalEventHandler
    {
        // Nouvelle propriété pour plusieurs IDs
        public IList<ElementId> ElementIds { get; set; }

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (ElementIds != null && ElementIds.Any())
            {
                uidoc.Selection.SetElementIds(ElementIds);
                uidoc.ShowElements(ElementIds);
            }
        }

        public string GetName() => "SelectionRequestHandler";
    }
}

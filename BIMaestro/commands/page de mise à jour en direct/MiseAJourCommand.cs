// MiseAJourCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;

namespace Page
{
    [Transaction(TransactionMode.Manual)]
    public class MiseAJourCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "MiseAJourCommand";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            try
            {
                var win = new UpdateWindow();
                win.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMaestro - Erreur", $"Une erreur s'est produite : {ex.Message}");
                return Result.Failed;
            }
        }
    }
}

// MiseAJourCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace Page
{
    [Transaction(TransactionMode.Manual)]
    public class MiseAJourCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
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

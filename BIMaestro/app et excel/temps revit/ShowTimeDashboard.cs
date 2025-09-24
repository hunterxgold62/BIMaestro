using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace BIMaestro.Dashboard
{
    [Transaction(TransactionMode.Manual)]
    public class ShowTimeDashboard : IExternalCommand
    {
        public Result Execute(ExternalCommandData cdata, ref string message, ElementSet elements)
        {
            try
            {
                Environment.SetEnvironmentVariable("EPPlusLicenseContext", "NonCommercial", EnvironmentVariableTarget.Process);
                new TimeSeriesDashboardWindow().Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Dashboard", ex.ToString());
                return Result.Failed;
            }
        }
    }
}

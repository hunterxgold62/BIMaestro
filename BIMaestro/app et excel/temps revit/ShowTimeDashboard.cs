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
                string activePath = cdata.Application?.ActiveUIDocument?.Document?.PathName;
                new TimeSeriesDashboardWindow(activePath).Show();
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
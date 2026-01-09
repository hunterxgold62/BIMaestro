using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;

namespace BIMaestro.Dashboard
{
    [Transaction(TransactionMode.Manual)]
    public class ShowTimeDashboard : BaseTrackedCommand
    {
        protected override string ButtonId => "ShowTimeDashboard";

        protected override Result OnExecute(ExternalCommandData cdata, ref string message, ElementSet elements)
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
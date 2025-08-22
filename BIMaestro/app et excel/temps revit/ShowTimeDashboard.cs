using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Windows;

namespace BIMaestro.Dashboard
{
    [Transaction(TransactionMode.Manual)]
    public class ShowTimeDashboard : IExternalCommand
    {
        public Result Execute(ExternalCommandData cdata, ref string message, ElementSet elements)
        {
            try
            {
                // EPPlus : contexte non commercial par variable d'env (compatible v5→v8)
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

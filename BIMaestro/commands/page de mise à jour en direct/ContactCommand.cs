using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Diagnostics;

namespace Page
{
    [Transaction(TransactionMode.Manual)]
    public class ContactCommand : BaseTrackedCommand
    {
        private const string LinkedInUrl = "https://www.linkedin.com/in/paul-lemert-b40921207";

        protected override string ButtonId => "ContactCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = LinkedInUrl,
                    UseShellExecute = true
                });

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMaestro - Contact", $"Impossible d'ouvrir LinkedIn : {ex.Message}");
                return Result.Failed;
            }
        }
    }
}

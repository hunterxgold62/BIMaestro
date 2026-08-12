using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
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
                TaskDialog.Show(UiLanguage.T("BIMaestro - Contact", "BIMaestro - Contact"), UiLanguage.T($"Impossible d'ouvrir LinkedIn : {ex.Message}", $"Unable to open LinkedIn: {ex.Message}"));
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class SupportCommand : BaseTrackedCommand
    {
        private const string SupportUrl = "https://ko-fi.com/bimaestro";

        protected override string ButtonId => "SupportCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SupportUrl,
                    UseShellExecute = true
                });

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.T("BIMaestro - Soutenir", "BIMaestro - Support"), UiLanguage.T($"Impossible d'ouvrir la page Ko-fi : {ex.Message}", $"Unable to open the Ko-fi page: {ex.Message}"));
                return Result.Failed;
            }
        }
    }
}

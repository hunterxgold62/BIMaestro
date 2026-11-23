// GuideCommand.cs
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
    public class GuideCommand : BaseTrackedCommand
    {
        private const string GuideUrl = "https://www.bimaestro.fr";
        protected override string ButtonId => "GuideCommand";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GuideUrl,
                    UseShellExecute = true
                };

                Process.Start(psi);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.Get("GuideCommand.BIMaestroError"), UiLanguage.Format("GuideCommand.UnableToOpenTheGuideInTheBrowser", (object)ex.Message));
                return Result.Failed;
            }
        }
    }
}

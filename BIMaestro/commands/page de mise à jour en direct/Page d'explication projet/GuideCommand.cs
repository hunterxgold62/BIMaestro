// GuideCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Diagnostics;

namespace Page
{
    [Transaction(TransactionMode.Manual)]
    public class GuideCommand : BaseTrackedCommand
    {
        private const string GuideUrl = "https://sites.google.com/view/guide-bimaestro";
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
                TaskDialog.Show("BIMaestro - Erreur", $"Impossible d'ouvrir le guide dans le navigateur : {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
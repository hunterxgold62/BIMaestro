// GuideCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Diagnostics;

namespace Page
{
    [Transaction(TransactionMode.Manual)]
    public class GuideCommand : IExternalCommand
    {
        private const string GuideUrl = "https://sites.google.com/view/guide-bimaestro";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
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
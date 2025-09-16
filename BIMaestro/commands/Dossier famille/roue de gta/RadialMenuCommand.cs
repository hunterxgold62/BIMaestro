using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using System;
using System.Linq;

namespace BIMaestro.UI
{
    [Transaction(TransactionMode.Manual)]
    public class RadialMenuCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                var uiapp = commandData.Application;

                // Position curseur (px écran)
                var (screenX, screenY) = OwnerWindowHelper.GetCursorPosPx();

                // Images “Mes Documents” (png/jpg), max 200 mélangées
                var allImages = ImageDiscovery.FindInDocuments(maxCount: 200).ToList();

                // Fallback si aucune image trouvée : 8× une ressource intégrée
                if (allImages.Count == 0)
                {
                    string res = "BIMaestro.Resources.dynamo 1.png";
                    allImages = Enumerable.Repeat(res, 8).ToList();
                }

                var win = new RadialMenuWindow(allImages, screenX, screenY);

                // Pas d'Owner (plus robuste avec AllowsTransparency=True)
                win.Completed += (accepted, index) =>
                {
                    RevitIdleRunner.Run(uiapp, () =>
                    {
                        if (!accepted) return;
                        TaskDialog.Show("Wheel", $"Index sélectionné : {index}");
                        // TODO: ta logique (ouvrir famille, PostCommand, etc.)
                    });
                };

                win.Show();
                win.Activate();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }
    }
}

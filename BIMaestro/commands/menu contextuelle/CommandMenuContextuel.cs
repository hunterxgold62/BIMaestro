using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Windows.Forms;
using System.Windows.Controls.Primitives;
using System.Windows;
using Autodesk.Revit.Attributes;

namespace TonNamespace
{
    [Transaction(TransactionMode.Manual)]
    public class CommandMenuContextuel : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Récupérer la position de la souris (Windows Forms)
            System.Drawing.Point mousePos = System.Windows.Forms.Control.MousePosition;

            // Création d'un Popup servant de menu contextuel
            Popup popup = new Popup
            {
                StaysOpen = false, // Se ferme automatiquement en cas de clic extérieur
                Placement = PlacementMode.MousePoint,
                AllowsTransparency = true,
                Focusable = false
            };

            // Création du contrôle de menu en lui passant l'objet UIApplication
            MenuControl menuControl = new MenuControl(commandData.Application);
            // Lorsqu'une action demande la fermeture, on ferme le popup.
            menuControl.RequestClose += () => { popup.IsOpen = false; };

            popup.Child = menuControl;
            popup.IsOpen = true;

            return Result.Succeeded;
        }
    }
}

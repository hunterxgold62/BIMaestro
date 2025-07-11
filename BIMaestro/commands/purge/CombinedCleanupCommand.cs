// Commands/CombinedCleanupCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class CombinedCleanupCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            // 1) Affiche la fenêtre WPF
            var window = new CleanupWindow();
            var helper = new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = commandData.Application.MainWindowHandle
            };
            if (window.ShowDialog() != true)
                return Result.Cancelled;

            // 2) Exécute chaque commande selon la sélection
            if (window.DeleteViews)
            {
                var cmd = new DeleteUnplacedViewsCommand();
                cmd.Execute(commandData, ref message, elements);
            }

            if (window.DeleteFamilies)
            {
                var cmd = new DeleteUnusedFamiliesCommand();
                cmd.Execute(commandData, ref message, elements);
            }

            if (window.DeleteSchedules)
            {
                var cmd = new DeleteUnusedSchedulesCommand();
                cmd.Execute(commandData, ref message, elements);
            }

            // 3) Méthode forte : à faire en dernier
            if (window.DeleteHardFamilies)
            {
                var cmd = new DeleteUnusedFamiliesHardCommand();
                cmd.Execute(commandData, ref message, elements);
            }

            return Result.Succeeded;
        }
    }
}

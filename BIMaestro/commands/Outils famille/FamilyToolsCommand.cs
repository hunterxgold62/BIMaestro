using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;

namespace Famille
{
    /// <summary>
    /// « Outils famille » : une seule fenêtre qui regroupe Convertir paramètres, Purge,
    /// Trad.IA (paramètres et vues) et Unités (export / import). Chaque outil se lance séparément.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class FamilyToolsCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "FamilyTools";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsFamilyDocument)
            {
                TaskDialog.Show("BIMaestro", UiLanguage.T(
                    "Ouvrez une famille dans l'éditeur de familles avant de lancer « Outils famille ».",
                    "Open a family in the Family Editor before running “Family tools”."));
                return Result.Cancelled;
            }

            var window = new FamilyToolsWindow(doc);
            new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle
            };
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}

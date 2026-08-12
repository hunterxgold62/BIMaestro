using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

namespace Modification
{ 
    [Transaction(TransactionMode.Manual)]
    public class DeleteUnusedFamiliesCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 1) Collecte de tous les symboles instanciés (FamilyInstance)
            var usedSymbolIds = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Select(fi => fi.Symbol.Id)
                .ToHashSet();

            // 2) Toutes les familles chargées (.rfa), éditables, non in-place
            var allFamilies = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => f.IsEditable && !f.IsInPlace)
                .ToList();

            // 3) Filtrer celles dont aucun symbole n’est instancié
            var unused = allFamilies
                .Where(f =>
                    !f.GetFamilySymbolIds()
                      .Any(symId => usedSymbolIds.Contains(symId))
                )
                .OrderBy(f => f.Name, StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            if (!unused.Any())
            {
                TaskDialog.Show(
                    UiLanguage.T("Aucune famille inutilisée", "No Unused Families"),
                    UiLanguage.T("Aucune famille chargée inutilisée trouvée.", "No unused loaded family was found.")
                );
                return Result.Succeeded;
            }

            // 4) Afficher la liste complète des noms avant purge
            string listNames = string.Join(
                Environment.NewLine,
                unused.Select(f => f.Name)
            );

            var confirm = new TaskDialog(UiLanguage.T("Familles inutilisées détectées", "Unused Families Detected"))
            {
                MainInstruction = UiLanguage.T($"Vous allez purger {unused.Count} famille(s) inutilisée(s) :", $"You are about to purge {unused.Count} unused family(ies):"),
                MainContent = listNames,
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel,
                AllowCancellation = true,
                MainIcon = TaskDialogIcon.TaskDialogIconWarning
            };
            if (confirm.Show() != TaskDialogResult.Yes)
                return Result.Succeeded;

            // 5) Lancer la commande native Purge Unused
            var purgeCmdId = RevitCommandId.LookupCommandId("ID_PURGE_UNUSED");
            uiapp.PostCommand(purgeCmdId);

            return Result.Succeeded;
        }
    }
}

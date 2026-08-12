using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Famille
{
    [Transaction(TransactionMode.Manual)]
    public class ImportProjectUnitsCommand : BaseTrackedCommand
    {

        protected override string ButtonId => "ImportProjectUnitsCommand";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            // 1) Chemin du JSON
            string docDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string prefDir = Path.Combine(docDir, "RevitLogs", "SauvegardePréférence");
            string filePath = Path.Combine(prefDir, "RevitUnits.json");

            if (!File.Exists(filePath))
            {
                TaskDialog.Show(UiLanguage.T("Importer unités", "Import Units"), UiLanguage.T($"⚠️ Aucun fichier trouvé :\n{filePath}", $"⚠️ No file was found:\n{filePath}"));
                return Result.Failed;
            }

            // 2) Désérialisation
            List<UnitInfo> list;
            try
            {
                list = JsonConvert.DeserializeObject<List<UnitInfo>>(
                    File.ReadAllText(filePath)
                );
                if (list == null) { list = new List<UnitInfo>(); }
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.T("Erreur JSON", "JSON Error"), ex.Message);
                return Result.Failed;
            }

            // 3) Application des unités
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            using (var tx = new Transaction(doc, "Importer unités"))
            {
                tx.Start();
                Units projectUnits = doc.GetUnits();
                var modifiableSpecs = new HashSet<string>(
                    Units.GetModifiableSpecs().Select(s => s.TypeId)
                );

                int appliedCount = 0;
                int skippedUnmodifiableCount = 0;
                foreach (var u in list.Where(x => x != null))
                {
                    if (string.IsNullOrWhiteSpace(u.SpecType) || string.IsNullOrWhiteSpace(u.UnitType)) { continue; }
                    if (!modifiableSpecs.Contains(u.SpecType))
                    {
                        skippedUnmodifiableCount++;
                        continue;
                    }

                    var specId = new ForgeTypeId(u.SpecType);
                    var unitId = new ForgeTypeId(u.UnitType);
                    if (UnitUtils.IsValidUnit(specId, unitId))
                    {
                        var fo = new FormatOptions(unitId) { Accuracy = u.Accuracy };
                        projectUnits.SetFormatOptions(specId, fo);
                        appliedCount++;
                    }
                }

                doc.SetUnits(projectUnits);
                tx.Commit();

                var disciplineCount = UnitUtils.GetAllDisciplines().Count();
                TaskDialog.Show(UiLanguage.T("Importer unités", "Import Units"), UiLanguage.T($"✅ {appliedCount} unité(s) importée(s) (toutes disciplines, {disciplineCount} discipline(s)) depuis :\n{filePath}\n\n⚠️ {skippedUnmodifiableCount} unité(s) ignorée(s) car non modifiables dans ce projet.", $"✅ {appliedCount} unit(s) imported (all disciplines, {disciplineCount} discipline(s)) from:\n{filePath}\n\n⚠️ {skippedUnmodifiableCount} unit(s) skipped because they cannot be modified in this project."));
            }

            return Result.Succeeded;
        }
    }
}

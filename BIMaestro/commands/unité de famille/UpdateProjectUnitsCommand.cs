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
            string filePath = UnitsFilePath;

            if (!File.Exists(filePath))
            {
                TaskDialog.Show(UiLanguage.Get("UpdateProjectUnitsCommand.ImportUnits"), UiLanguage.Format("UpdateProjectUnitsCommand.NoFileWasFound0", (object)filePath));
                return Result.Failed;
            }

            // 2) Désérialisation
            List<UnitInfo> list;
            try
            {
                list = LoadUnits(filePath);
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.Get("UpdateProjectUnitsCommand.JSONError"), ex.Message);
                return Result.Failed;
            }

            // 3) Application des unités
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            var (appliedCount, skippedUnmodifiableCount) = ApplyUnits(doc, list);

            var disciplineCount = UnitUtils.GetAllDisciplines().Count();
            TaskDialog.Show(UiLanguage.Get("UpdateProjectUnitsCommand.ImportUnits"), UiLanguage.Format("UpdateProjectUnitsCommand.0UnitSImportedAllDisciplines1Discipline", (object)appliedCount, (object)disciplineCount, (object)filePath, (object)skippedUnmodifiableCount));

            return Result.Succeeded;
        }

        /// <summary>Fichier JSON écrit par « Export d'unité ».</summary>
        internal static string UnitsFilePath =>
            Path.Combine(global::BIMaestro.Storage.PreferenceStorage.Folder("Unités"), "RevitUnits.json");

        internal static List<UnitInfo> LoadUnits(string filePath)
        {
            return JsonConvert.DeserializeObject<List<UnitInfo>>(File.ReadAllText(filePath)) ?? new List<UnitInfo>();
        }

        /// <summary>Applique les unités dans une transaction, sans fenêtre (utilisé aussi par « Préparer la famille »).</summary>
        internal static (int Applied, int SkippedUnmodifiable) ApplyUnits(Document doc, List<UnitInfo> list)
        {
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
                return (appliedCount, skippedUnmodifiableCount);
            }
        }
    }
}

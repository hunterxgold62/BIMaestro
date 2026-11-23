using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using Newtonsoft.Json;
using System.Linq;

namespace Famille
{
    public class UnitInfo
    {
        public string SpecType { get; set; }
        public string UnitType { get; set; }
        public double Accuracy { get; set; }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class ExportProjectUnitsCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ExportProjectUnitsCommand";

        protected override Result OnExecute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Prépare le dossier et le fichier JSON
            string docDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string prefDir = global::BIMaestro.Storage.PreferenceStorage.Folder("Unités");
            Directory.CreateDirectory(prefDir);
            string filePath = Path.Combine(prefDir, "RevitUnits.json");

            Document doc = commandData.Application.ActiveUIDocument.Document;
            Units units = doc.GetUnits();
            var list = new List<UnitInfo>();
            var allSpecs = UnitUtils.GetAllMeasurableSpecs();

            foreach (var specId in allSpecs)
            {
                FormatOptions fo = units.GetFormatOptions(specId);
                if (fo == null) { continue; }
                ForgeTypeId unitId = fo.GetUnitTypeId();
                if (unitId == null || string.IsNullOrWhiteSpace(unitId.TypeId)) { continue; }

                list.Add(new UnitInfo
                {
                    SpecType = specId.TypeId,
                    UnitType = unitId.TypeId,
                    Accuracy = fo.Accuracy
                });
            }

            File.WriteAllText(filePath, JsonConvert.SerializeObject(list, Formatting.Indented));
            RecordOperationResult(ButtonId, "export_units", false,
                new Dictionary<string, int> { ["files_exported"] = 1, ["units_exported"] = list.Count });

            var disciplineCount = UnitUtils.GetAllDisciplines().Count();
            TaskDialog.Show(UiLanguage.Get("ExportProjectUnitsCommand.ExportUnits"), UiLanguage.Format("ExportProjectUnitsCommand.0UnitSExportedAllDisciplines1Discipline", (object)list.Count, (object)disciplineCount, (object)filePath));
            return Result.Succeeded;
        }
    }
}

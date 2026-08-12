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
            string prefDir = Path.Combine(docDir, "RevitLogs", "SauvegardePréférence");
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

            var disciplineCount = UnitUtils.GetAllDisciplines().Count();
            TaskDialog.Show(UiLanguage.T("Export unités", "Export Units"), UiLanguage.T($"✅ {list.Count} unité(s) exportée(s) (toutes disciplines, {disciplineCount} discipline(s)) vers :\n{filePath}", $"✅ {list.Count} unit(s) exported (all disciplines, {disciplineCount} discipline(s)) to:\n{filePath}"));
            return Result.Succeeded;
        }
    }
}

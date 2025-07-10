using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;

namespace YourNamespace
{
    public class UnitInfo
    {
        public string SpecType { get; set; }
        public string UnitType { get; set; }
        public double Accuracy { get; set; }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class ExportProjectUnitsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Prépare le dossier et le fichier JSON
            string docDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string prefDir = Path.Combine(docDir, "RevitLogs", "SauvegardePréférence");
            Directory.CreateDirectory(prefDir);
            string filePath = Path.Combine(prefDir, "RevitUnits.json");

            // Liste mise à jour : on exporte maintenant aussi la DISTANCE
            var specTypes = new[]
            {
                SpecTypeId.Angle,
                SpecTypeId.Distance,    // ← Ajouté
                SpecTypeId.Length,
                SpecTypeId.Area,
                SpecTypeId.CostPerArea,
                SpecTypeId.MassDensity,
                SpecTypeId.RotationAngle,
                SpecTypeId.Slope,
                SpecTypeId.Speed,
                SpecTypeId.Time,
                SpecTypeId.Volume,
                SpecTypeId.Currency
            };

            Document doc = commandData.Application.ActiveUIDocument.Document;
            Units units = doc.GetUnits();
            var list = new List<UnitInfo>();

            foreach (var specId in specTypes)
            {
                FormatOptions fo = units.GetFormatOptions(specId);
                ForgeTypeId unitId = fo.GetUnitTypeId();
                list.Add(new UnitInfo
                {
                    SpecType = specId.TypeId,
                    UnitType = unitId.TypeId,
                    Accuracy = fo.Accuracy
                });
            }

            File.WriteAllText(filePath, JsonConvert.SerializeObject(list, Formatting.Indented));

            TaskDialog.Show("Export unités", $"✅ Unités exportées vers :\n{filePath}");
            return Result.Succeeded;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;

namespace YourNamespace
{
    [Transaction(TransactionMode.Manual)]
    public class ImportProjectUnitsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // 1) Chemin du JSON
            string docDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string prefDir = Path.Combine(docDir, "RevitLogs", "SauvegardePréférence");
            string filePath = Path.Combine(prefDir, "RevitUnits.json");

            if (!File.Exists(filePath))
            {
                TaskDialog.Show("Importer unités", $"⚠️ Aucun fichier trouvé :\n{filePath}");
                return Result.Failed;
            }

            // 2) Désérialisation
            List<UnitInfo> list;
            try
            {
                list = JsonConvert.DeserializeObject<List<UnitInfo>>(
                    File.ReadAllText(filePath)
                );
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Erreur JSON", ex.Message);
                return Result.Failed;
            }

            // 3) Application des unités
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            using (var tx = new Transaction(doc, "Importer unités"))
            {
                tx.Start();
                Units projectUnits = doc.GetUnits();

                foreach (var u in list)
                {
                    var specId = new ForgeTypeId(u.SpecType);
                    var unitId = new ForgeTypeId(u.UnitType);
                    if (UnitUtils.IsValidUnit(specId, unitId))
                    {
                        var fo = new FormatOptions(unitId) { Accuracy = u.Accuracy };
                        projectUnits.SetFormatOptions(specId, fo);
                    }
                }

                doc.SetUnits(projectUnits);
                tx.Commit();
            }

            TaskDialog.Show("Importer unités", $"✅ Unités importées depuis :\n{filePath}");
            return Result.Succeeded;
        }
    }
}

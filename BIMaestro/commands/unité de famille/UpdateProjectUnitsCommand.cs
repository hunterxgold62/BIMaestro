// File: UpdateProjectUnitsCommand.cs
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
    public class UpdateProjectUnitsCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            // 1) Chemin vers le JSON dans Mes Documents\RevitLogs\SauvegardePréférence
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string folder = Path.Combine(docs, "RevitLogs", "SauvegardePréférence");
            string file = Path.Combine(folder, "preferenceunité.json");

            if (!File.Exists(file))
            {
                TaskDialog.Show("Unités",
                                $"Fichier introuvable :\n{file}");
                return Result.Cancelled;
            }

            // 2) Chargement des préférences
            List<PreferenceViewModel> prefs;
            try
            {
                var json = File.ReadAllText(file);
                prefs = JsonConvert.DeserializeObject<List<PreferenceViewModel>>(json);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Erreur",
                                $"Lecture JSON impossible :\n{ex.Message}");
                return Result.Failed;
            }

            // 3) Symbole → ForgeTypeId
            var symbolToUnit = new Dictionary<string, ForgeTypeId>
            {
                ["m"] = UnitTypeId.Meters,
                ["cm"] = UnitTypeId.Centimeters,
                ["ft"] = UnitTypeId.Feet,
                ["ft-in"] = UnitTypeId.FeetFractionalInches,
                ["m²"] = UnitTypeId.SquareMeters,
                ["ft²"] = UnitTypeId.SquareFeet,
                ["m³"] = UnitTypeId.CubicMeters,
                ["ft³"] = UnitTypeId.CubicFeet,
                ["kg/m³"] = UnitTypeId.KilogramsPerCubicMeter,
                ["lb/ft³"] = UnitTypeId.PoundsMassPerCubicFoot,
                ["km/h"] = UnitTypeId.KilometersPerHour,
                ["mph"] = UnitTypeId.MilesPerHour,
                ["h"] = UnitTypeId.Hours,
                ["s"] = UnitTypeId.Seconds,
                ["°"] = UnitTypeId.Degrees,
                ["$/m²"] = UnitTypeId.CurrencyPerSquareMeter,
                ["€/m²"] = UnitTypeId.CurrencyPerSquareMeter,
                ["$"] = UnitTypeId.Currency,
                ["€"] = UnitTypeId.Currency
            };

            // 4) Catégorie → ForgeTypeId
            var specNameToId = new Dictionary<string, ForgeTypeId>
            {
                ["Angle"] = SpecTypeId.Angle,
                ["Angle de rotation"] = SpecTypeId.RotationAngle,
                ["Inclinaison"] = SpecTypeId.Slope,
                ["Distance"] = SpecTypeId.Distance,
                ["Longueur"] = SpecTypeId.Length,
                ["Surface"] = SpecTypeId.Area,
                ["Volume"] = SpecTypeId.Volume,
                ["Densité de la masse"] = SpecTypeId.MassDensity,
                ["Vitesse"] = SpecTypeId.Speed,
                ["Temps"] = SpecTypeId.Time,
                ["Coût par surface"] = SpecTypeId.CostPerArea,
                ["Devise"] = SpecTypeId.Currency
            };

            // 5) Application
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            using (var tx = new Transaction(doc, "Appliquer préférences d'unités"))
            {
                tx.Start();

                Units units = doc.GetUnits();
                foreach (var p in prefs)
                {
                    if (!specNameToId.TryGetValue(p.SpecType, out var specId)) continue;
                    if (!symbolToUnit.TryGetValue(p.SelectedUnit, out var unitId)) continue;

                    if (UnitUtils.IsValidUnit(specId, unitId))
                    {
                        var fo = new FormatOptions(unitId)
                        {
                            Accuracy = p.Accuracy
                        };
                        units.SetFormatOptions(specId, fo);
                    }
                }

                doc.SetUnits(units);
                tx.Commit();
            }

            TaskDialog.Show("Unités", "✅ Préférences appliquées !");
            return Result.Succeeded;
        }
    }
}

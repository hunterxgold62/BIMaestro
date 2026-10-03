using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

namespace BIMaestro.UI
{
    internal sealed class RadialPlaceFamilyHandler : IExternalEventHandler
    {
        public string FamilyPath { get; set; }
        public bool SuppressUsageTracking { get; set; }

        public void Execute(UIApplication uiapp)
        {
            try
            {
                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null || string.IsNullOrWhiteSpace(FamilyPath) || !File.Exists(FamilyPath))
                    return;

                var doc = uidoc.Document;
                var famName = Path.GetFileNameWithoutExtension(FamilyPath);

                var fam = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));

                if (fam == null)
                {
                    using (var tx = new Transaction(doc, "Charger famille"))
                    {
                        tx.Start();
                        doc.LoadFamily(FamilyPath, new Famille.FamilyLoadOptionKeep(), out fam);
                        tx.Commit();
                    }
                    if (fam == null)
                    {
                        fam = new FilteredElementCollector(doc)
                            .OfClass(typeof(Family))
                            .Cast<Family>()
                            .FirstOrDefault(f => f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));
                        if (fam == null) return;
                    }
                }

                var symId = fam.GetFamilySymbolIds().FirstOrDefault();
                if (symId == ElementId.InvalidElementId) return;

                var symbol = doc.GetElement(symId) as FamilySymbol;
                if (symbol == null) return;

                if (!symbol.IsActive)
                {
                    using (var tx2 = new Transaction(doc, "Activer type"))
                    {
                        tx2.Start();
                        symbol.Activate();
                        tx2.Commit();
                    }
                }

                uidoc.Selection.SetElementIds(new List<ElementId> { symbol.Id });
                bool tutorialFamily = SuppressUsageTracking || BIMaestro.Tutorials.DemoTourService.IsTutorialFamilyForRosace(FamilyPath);
                if (BIMaestro.Tutorials.DemoTourService.IsTutorialChaiseForRosace(FamilyPath))
                    BIMaestro.Tutorials.DemoTourService.WatchTutorialFamilyPlacement(doc, symbol);
                uidoc.PostRequestForElementTypePlacement(symbol);

                if (!tutorialFamily)
                {
                    Famille.FamilyUsageManager.RegisterUse(FamilyPath);
                    Famille.FamilyRecentManager.RegisterUse(FamilyPath);
                }
                if (BIMaestro.Tutorials.DemoTourService.IsTutorialChaiseForRosace(FamilyPath))
                    BIMaestro.Tutorials.DemoTourService.ReportExternalAction("radial-tutorial-chaise-used");
                BIMaestro.Tutorials.DemoTourService.ReportExternalAction("radial-family-used");
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.T("BIMaestro - Rosace (erreur)", "BIMaestro - Radial Menu Error"), ex.Message);
            }
        }

        public string GetName() => "RadialPlaceFamilyHandler";
    }
}

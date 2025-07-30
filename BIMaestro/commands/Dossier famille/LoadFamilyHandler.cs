// LoadFamilyHandler.cs
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Famille
{
    public class LoadFamilyHandler : IExternalEventHandler
    {
        public string FamilyPath { get; set; }

        public void Execute(UIApplication uiapp)
        {
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
            Family famLoaded = null;

            // 1) Charger la famille (ou tenter de charger)
            using (var tx = new Transaction(doc, "Charger Famille"))
            {
                tx.Start();
                doc.LoadFamily(FamilyPath, new FamilyLoadOption(), out famLoaded);
                tx.Commit();
            }

            // 2) Si elle était déjà présente, la retrouver dans le document
            if (famLoaded == null)
            {
                var famName = System.IO.Path.GetFileNameWithoutExtension(FamilyPath);
                famLoaded = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f =>
                        f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));
            }

            // 3) Si on n’a toujours pas la famille, on quitte
            if (famLoaded == null)
                return;

            // 4) Récupérer et activer le premier FamilySymbol
            var symId = famLoaded.GetFamilySymbolIds().FirstOrDefault();
            if (symId == ElementId.InvalidElementId)
                return;
            var symbol = doc.GetElement(symId) as FamilySymbol;
            if (symbol == null)
                return;

            if (!symbol.IsActive)
            {
                using (var tx2 = new Transaction(doc, "Activer symbole"))
                {
                    tx2.Start();
                    symbol.Activate();
                    tx2.Commit();
                }
            }

            // 5) Sélectionner puis lancer le mode placement du symbole
            uidoc.Selection.SetElementIds(new List<ElementId> { symbol.Id });
            uidoc.PostRequestForElementTypePlacement(symbol);
        }

        public string GetName() => "LoadFamilyHandler";
    }
}

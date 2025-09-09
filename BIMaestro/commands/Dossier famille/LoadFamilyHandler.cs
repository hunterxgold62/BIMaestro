// LoadFamilyHandler.cs
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
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
            Family fam = null;

            // Nom de la famille sans extension
            var famName = Path.GetFileNameWithoutExtension(FamilyPath);

            // 1) On cherche d'abord la famille dans le document
            fam = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f =>
                    f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));

            // 2) Si elle n'existe pas, on la charge une seule fois depuis le fichier
            if (fam == null)
            {
                using (var tx = new Transaction(doc, "Charger Famille"))
                {
                    tx.Start();
                    doc.LoadFamily(FamilyPath, new FamilyLoadOptionKeep(), out fam);
                    tx.Commit();
                }

                // Si jamais LoadFamily n'a pas trouvé de nouvelle famille,  
                // on retente de récupérer l'existante
                if (fam == null)
                {
                    fam = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f =>
                            f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));
                }
            }

            // 3) Si on n'a toujours pas la famille, on arrête
            if (fam == null)
                return;

            // 4) Récupérer et activer le premier symbole
            var symId = fam.GetFamilySymbolIds().FirstOrDefault();
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

            // 5) Sélectionner puis lancer le mode placement
            uidoc.Selection.SetElementIds(new List<ElementId> { symbol.Id });
            uidoc.PostRequestForElementTypePlacement(symbol);
            FamilyUsageManager.RegisterUse(FamilyPath);
        }

        public string GetName() => "LoadFamilyHandler";
    }
}

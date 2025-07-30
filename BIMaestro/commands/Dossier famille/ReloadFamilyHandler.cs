// ReloadFamilyHandler.cs
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Famille
{
    public class ReloadFamilyHandler : IExternalEventHandler
    {
        public string FamilyPath { get; set; }

        // Mémorisation des timestamps pour détecter si le fichier a vraiment changé
        private static readonly Dictionary<string, DateTime> LastWriteTimes =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public void Execute(UIApplication uiapp)
        {
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
            var famName = Path.GetFileNameWithoutExtension(FamilyPath);

            // 1) Vérifier la dernière écriture sur disque
            DateTime current = File.GetLastWriteTimeUtc(FamilyPath);
            bool isSame = LastWriteTimes.TryGetValue(FamilyPath, out DateTime prev)
                          && prev == current;

            // 2) Recharger la famille sans confirmation
            Family reloaded = null;
            using (var tx = new Transaction(doc, "Recharger Famille"))
            {
                tx.Start();
                doc.LoadFamily(FamilyPath, new FamilyLoadOption(), out reloaded);
                tx.Commit();
            }

            // 3) Mettre à jour le timestamp
            LastWriteTimes[FamilyPath] = current;

            // 4) Si LoadFamily n'a rien retourné, récupérer l'existante
            if (reloaded == null)
            {
                reloaded = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f =>
                        f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));
            }

            if (reloaded == null)
                return;

            // 5) Récupérer et activer le FamilySymbol
            var symId = reloaded.GetFamilySymbolIds().FirstOrDefault();
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

            // 6) Sélectionner puis relancer le mode placement
            uidoc.Selection.SetElementIds(new List<ElementId> { symbol.Id });
            uidoc.PostRequestForElementTypePlacement(symbol);

            // 7) Message utilisateur
            if (isSame)
                TaskDialog.Show("Rechargement", $"La famille « {famName} » est déjà à jour.");
            else
                TaskDialog.Show("Rechargement", $"La famille « {famName} » a été rechargée avec succès.");
        }

        public string GetName() => "ReloadFamilyHandler";
    }
}

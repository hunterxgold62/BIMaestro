// ReloadFamilyHandler.cs
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Famille
{
    /// <summary>
    /// Recharge TOUJOURS la famille depuis le .RFA en écrasant les valeurs des paramètres de TYPE.
    /// Affiche un message clair, puis propose le placement du premier type.
    /// </summary>
    public class ReloadFamilyHandler : IExternalEventHandler
    {
        public string FamilyPath { get; set; }

        // Cache UX pour indiquer "déjà à jour" vs "nouvelle version"
        private static readonly Dictionary<string, DateTime> LastWriteTimes =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public void Execute(UIApplication uiapp)
        {
            try
            {
                if (uiapp?.ActiveUIDocument == null) return;

                UIDocument uidoc = uiapp.ActiveUIDocument;
                Document doc = uidoc.Document;

                if (string.IsNullOrWhiteSpace(FamilyPath) || !File.Exists(FamilyPath))
                {
                    TaskDialog.Show("BIMaestro - Rechargement",
                        "Chemin de famille invalide ou fichier introuvable.");
                    return;
                }

                string famName = Path.GetFileNameWithoutExtension(FamilyPath);

                // 1) UX : statut du fichier (n'empêche JAMAIS le rechargement)
                DateTime currentWriteUtc = File.GetLastWriteTimeUtc(FamilyPath);
                bool fileUnchanged = LastWriteTimes.TryGetValue(FamilyPath, out DateTime prevUtc)
                                     && prevUtc == currentWriteUtc;

                // 2) Recharger TOUJOURS en ECRASANT les valeurs de TYPE
                Family reloaded = null;
                using (var tx = new Transaction(doc, "Recharger famille (écraser valeurs de type)"))
                {
                    tx.Start();
                    // >>> Nécessite la classe FamilyLoadOptionOverwrite (overwriteParameterValues = true)
                    doc.LoadFamily(FamilyPath, new FamilyLoadOptionOverwrite(), out reloaded);
                    tx.Commit();
                }

                // Met à jour le cache UX
                LastWriteTimes[FamilyPath] = currentWriteUtc;

                // Fallback si LoadFamily n'a rien retourné
                if (reloaded == null)
                {
                    reloaded = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));
                    if (reloaded == null)
                    {
                        TaskDialog.Show("BIMaestro - Rechargement",
                            $"Impossible de retrouver « {famName} » après rechargement.");
                        return;
                    }
                }

                // 3) Régénération SÉCURISÉE (évite l'InvalidOperationException)
                SafeRegenerate(doc);

                // 4) Message clair (toujours "rechargée")
                string suffix = fileUnchanged ? " (déjà à jour)." : " (nouvelle version détectée).";
                TaskDialog.Show("BIMaestro - Rechargement",
                    $"La famille « {famName} » a été rechargée avec succès{suffix}");

                // 5) Activer le premier type et proposer le placement
                ElementId symId = reloaded.GetFamilySymbolIds().FirstOrDefault();
                if (symId == ElementId.InvalidElementId) return;

                FamilySymbol symbol = doc.GetElement(symId) as FamilySymbol;
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
                uidoc.PostRequestForElementTypePlacement(symbol);

                // Analytics facultatif
                FamilyUsageManager.RegisterUse(FamilyPath);
            }
            catch (Exception ex)
            {
                // Si quelque chose d'autre plante, on l'affiche.
                TaskDialog.Show("BIMaestro - Rechargement (erreur)", ex.Message);
            }
        }

        /// <summary>
        /// Appelle Document.Regenerate() sans lever d'exception si le document n'est pas modifiable.
        /// </summary>
        private static void SafeRegenerate(Document doc)
        {
            try
            {
                if (doc.IsModifiable)
                {
                    doc.Regenerate();
                }
                else
                {
                    using (var tx = new Transaction(doc, "Regenerate (safe)"))
                    {
                        tx.Start();
                        doc.Regenerate();
                        tx.Commit();
                    }
                }
            }
            catch
            {
                // Non bloquant : on ignore si Revit refuse la régénération ici.
            }
        }

        public string GetName() => "ReloadFamilyHandler";
    }
}

// ReloadFamilyHandler.cs
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

namespace Famille
{
    /// <summary>
    /// Recharge TOUJOURS la famille depuis le .RFA en écrasant les valeurs des paramètres de TYPE.
    /// Affiche un message clair, puis propose le placement du premier type.
    /// </summary>
    public class ReloadFamilyHandler : IExternalEventHandler
    {
        public string FamilyPath { get; set; }
        public List<string> FamilyPaths { get; set; } = new List<string>();

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

                var targetPaths = (FamilyPaths ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (targetPaths.Count == 0 && !string.IsNullOrWhiteSpace(FamilyPath))
                    targetPaths.Add(FamilyPath);

                targetPaths = targetPaths.Where(File.Exists).ToList();
                if (targetPaths.Count == 0)
                {
                    TaskDialog.Show(UiLanguage.T("BIMaestro - Rechargement", "BIMaestro - Reload"),
                        UiLanguage.T("Chemin de famille invalide ou fichier introuvable.", "Invalid family path or file not found."));
                    return;
                }

                bool singleMode = targetPaths.Count == 1;
                int ok = 0;
                int fail = 0;
                string firstError = null;

                foreach (var path in targetPaths)
                {
                    try
                    {
                        string famName = Path.GetFileNameWithoutExtension(path);
                        DateTime currentWriteUtc = File.GetLastWriteTimeUtc(path);
                        bool fileUnchanged = LastWriteTimes.TryGetValue(path, out DateTime prevUtc)
                                             && prevUtc == currentWriteUtc;

                        Family reloaded = null;
                        using (var tx = new Transaction(doc, "Recharger famille (écraser valeurs de type)"))
                        {
                            tx.Start();
                            doc.LoadFamily(path, new FamilyLoadOptionOverwrite(), out reloaded);
                            tx.Commit();
                        }

                        LastWriteTimes[path] = currentWriteUtc;

                        if (reloaded == null)
                        {
                            reloaded = new FilteredElementCollector(doc)
                                .OfClass(typeof(Family))
                                .Cast<Family>()
                                .FirstOrDefault(f => f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));
                            if (reloaded == null)
                                throw new InvalidOperationException(UiLanguage.T(
                                    $"Impossible de retrouver « {famName} » après rechargement.",
                                    $"Unable to find “{famName}” after reloading."));
                        }

                        SafeRegenerate(doc);

                        if (singleMode)
                        {
                            string suffix = fileUnchanged
                                ? UiLanguage.T(" (déjà à jour).", " (already up to date).")
                                : UiLanguage.T(" (nouvelle version détectée).", " (new version detected).");
                            TaskDialog.Show(UiLanguage.T("BIMaestro - Rechargement", "BIMaestro - Reload"),
                                UiLanguage.T($"La famille « {famName} » a été rechargée avec succès{suffix}", $"The family “{famName}” was reloaded successfully{suffix}"));

                            ElementId symId = reloaded.GetFamilySymbolIds().FirstOrDefault();
                            if (symId != ElementId.InvalidElementId)
                            {
                                FamilySymbol symbol = doc.GetElement(symId) as FamilySymbol;
                                if (symbol != null)
                                {
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
                                }
                            }
                        }

                        FamilyUsageManager.RegisterUse(path);
                        FamilyRecentManager.RegisterUse(path);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        if (firstError == null)
                            firstError = ex.Message;
                    }
                }

                if (!singleMode)
                {
                    string message = UiLanguage.T($"Familles rechargées : {ok}\nÉchecs : {fail}", $"Families reloaded: {ok}\nFailures: {fail}");
                    if (!string.IsNullOrWhiteSpace(firstError))
                        message += UiLanguage.T($"\n\nPremier détail d'erreur : {firstError}", $"\n\nFirst error detail: {firstError}");
                    TaskDialog.Show(UiLanguage.T("BIMaestro - Rechargement", "BIMaestro - Reload"), message);
                }
            }
            catch (Exception ex)
            {
                // Si quelque chose d'autre plante, on l'affiche.
                TaskDialog.Show(UiLanguage.T("BIMaestro - Rechargement (erreur)", "BIMaestro - Reload Error"), ex.Message);
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

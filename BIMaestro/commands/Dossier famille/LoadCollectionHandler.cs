using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

namespace Famille
{
    /// <summary>
    /// Charge toutes les familles d'une collection dans le document actif.
    /// Écrase toujours les valeurs de TYPE.
    /// </summary>
    public class LoadCollectionHandler : IExternalEventHandler
    {
        public List<string> FamilyPaths { get; set; } = new List<string>();

        public void Execute(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;
            var doc = uidoc.Document;

            int ok = 0, fail = 0, unchanged = 0;

            foreach (var p in FamilyPaths)
            {
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) { fail++; continue; }

                try
                {
                    using (var tx = new Transaction(doc, $"Charger « {Path.GetFileNameWithoutExtension(p)} »"))
                    {
                        tx.Start();
                        Family fam;
                        bool loaded = doc.LoadFamily(p, new FamilyLoadOptionOverwrite(), out fam);
                        if (tx.Commit() != TransactionStatus.Committed) { fail++; continue; }
                        if (!loaded) { unchanged++; continue; }
                    }
                    FamilyUsageManager.RegisterUse(p);
                    ok++;
                }
                catch { fail++; }
            }

            Licensing.OperationTelemetry.Track("FamilyBrowserCommand", "Navigateur de familles", "load_collection", false,
                new Dictionary<string, int> { ["families_loaded"] = ok,
                    ["families_unchanged"] = unchanged, ["items_failed"] = fail });
            TaskDialog.Show("Collections", UiLanguage.T(
                $"Familles chargées : {ok}\nFamilles inchangées : {unchanged}\nÉchecs : {fail}",
                $"Families loaded: {ok}\nUnchanged families: {unchanged}\nFailures: {fail}"));
        }

        public string GetName() => "LoadCollectionHandler";
    }
}

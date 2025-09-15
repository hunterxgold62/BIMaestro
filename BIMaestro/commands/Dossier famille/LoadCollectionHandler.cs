using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

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

            int ok = 0, fail = 0;

            foreach (var p in FamilyPaths)
            {
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) { fail++; continue; }

                try
                {
                    using (var tx = new Transaction(doc, $"Charger « {Path.GetFileNameWithoutExtension(p)} »"))
                    {
                        tx.Start();
                        Family fam;
                        doc.LoadFamily(p, new FamilyLoadOptionOverwrite(), out fam);
                        tx.Commit();
                    }
                    FamilyUsageManager.RegisterUse(p);
                    ok++;
                }
                catch { fail++; }
            }

            TaskDialog.Show("Collections", $"Familles chargées : {ok}\nÉchecs : {fail}");
        }

        public string GetName() => "LoadCollectionHandler";
    }
}

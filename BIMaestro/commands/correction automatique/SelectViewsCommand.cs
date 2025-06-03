using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ScanTextRevit
{
    [Transaction(TransactionMode.Manual)]
    public class SelectViewsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            Document doc = uiApp.ActiveUIDocument.Document;

            // 1) Récupération de toutes les vues (hors feuilles) et toutes les feuilles
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet))
                .OrderBy(v => v.ViewType)
                .ThenBy(v => v.Name)
                .ToList();

            var allSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber)
                .ToList();

            // 2) Affichage de la fenêtre de sélection
            SelectViewsWindow wpf = new SelectViewsWindow(allViews, allSheets, doc);
            var helper = new System.Windows.Interop.WindowInteropHelper(wpf);
            helper.Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            bool? dialogResult = wpf.ShowDialog();
            if (dialogResult != true)
                return Result.Cancelled;

            // 3) Récupération de la sélection
            List<ElementId> selectedIds = wpf.GetSelectedElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show("Info", "Aucune vue/feuille sélectionnée.");
                return Result.Cancelled;
            }

            // ------------------------------
            //    ÉTAPE-CLÉ : DÉDUPLICATION
            // ------------------------------
            // Si l'utilisateur a sélectionné une feuille + la vue indépendante correspondante,
            // on retire la vue indépendante pour éviter un double scan.
            var placedViewIdsOnSelectedSheets = new HashSet<ElementId>();

            // 3.1) Parcourir chaque feuille sélectionnée pour récupérer les vues placées
            foreach (ElementId sheetId in selectedIds.ToList())
            {
                var sheet = doc.GetElement(sheetId) as ViewSheet;
                if (sheet != null)
                {
                    // Récupère les vues placées sur cette feuille
                    var vports = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .ToList();

                    foreach (var vp in vports)
                    {
                        placedViewIdsOnSelectedSheets.Add(vp.ViewId);
                    }
                }
            }

            // 3.2) Retirer de la sélection toutes les vues indépendantes qui se trouvent déjà sur une feuille sélectionnée
            selectedIds.RemoveAll(id => placedViewIdsOnSelectedSheets.Contains(id));

            // S'il ne reste plus rien après la déduplication
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show("Info", "Après déduplication, aucune vue/feuille n'est sélectionnée.");
                return Result.Cancelled;
            }

            // 4) Lancement du scan
            ScanService service = new ScanService();
            var scanResults = service.ScanSelectedViewsAndSheets(doc, selectedIds);
            if (scanResults.Count == 0)
            {
                TaskDialog.Show("Info", "Aucun texte trouvé.");
                return Result.Cancelled;
            }

            // 5) Affichage de la fenêtre de résultat
            CorrectionResultWindow resultWindow = new CorrectionResultWindow();
            resultWindow.UiDoc = uiApp.ActiveUIDocument;
            var helper2 = new System.Windows.Interop.WindowInteropHelper(resultWindow);
            helper2.Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            resultWindow.Show();

            // 6) Configuration du AiGrammarChecker et abonnements
            AiGrammarChecker grammarChecker = new AiGrammarChecker();
            grammarChecker.ChunkProcessed += (key, partialCorrections) =>
            {
                resultWindow.Dispatcher.Invoke(() =>
                {
                    resultWindow.AddPartialResults(key, partialCorrections);
                });
            };
            grammarChecker.ProgressUpdated += (percent) =>
            {
                resultWindow.Dispatcher.Invoke(() =>
                {
                    resultWindow.UpdateProgressBar(percent);
                });
            };
            grammarChecker.OnAllChunksCompleted += () =>
            {
                resultWindow.Dispatcher.Invoke(() =>
                {
                    resultWindow.OnAllChunksCompleted();
                });
            };

            // 7) Traitement asynchrone
            Task.Run(async () =>
            {
                var finalResults = await grammarChecker.CheckGrammarInChunksAsync(scanResults);
            });

            return Result.Succeeded;
        }
    }
}
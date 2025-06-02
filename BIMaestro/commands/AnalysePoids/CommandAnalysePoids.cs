using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Windows.Threading;
using System.Windows.Interop;

namespace AnalysePoidsPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class CommandAnalysePoids : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // 1. Confirmation rapide
            var td = new TaskDialog("Analyse Poids")
            {
                MainInstruction = "Lancement de l'analyse des familles et imports",
                MainContent = "Cela peut prendre quelques instants.\nContinuer ?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (td.Show() != TaskDialogResult.Yes)
                return Result.Cancelled;

            // 2. Préparer cache familles
            string logsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs", "TailleFamilleRevit");
            Directory.CreateDirectory(logsFolder);
            string cacheFile = Path.Combine(logsFolder, "CacheTailleFamille.json");
            var cache = LoadCache(cacheFile);

            // 3. Lancer l'indexation disque en tâche de fond
            var indexTask = Task.Run(() => BuildFileIndex(doc));

            // 4. Analyser les familles
            var famInfos = AnalyseFamilles(doc, commandData, cache, cacheFile);

            // 5. Attendre l'index puis analyser les imports & PDF
            var fileIndex = indexTask.Result;
            var impInfos = AnalyseImports(doc, fileIndex);

            // 6. Fusionner et calculer total
            var elems = new List<ElementInfo>();
            elems.AddRange(famInfos);
            elems.AddRange(impInfos);
            double totalMo = elems.Sum(e => e.TailleEnMo);

            // 7. Afficher les résultats
            var win = new ResultWindow(elems, totalMo, commandData);
            win.Show();

            return Result.Succeeded;
        }

        // ==== Indexation disque ====
        private Dictionary<string, string> BuildFileIndex(Document doc)
        {
            var roots = new List<string>();
            string rvtPath = doc.IsWorkshared
                ? ModelPathUtils.ConvertModelPathToUserVisiblePath(
                    doc.GetWorksharingCentralModelPath())
                : doc.PathName;
            if (!string.IsNullOrEmpty(rvtPath))
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(rvtPath));
                for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                    roots.Add(dir.FullName);
            }
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(profile, "Downloads"));
            roots.Add(Path.Combine(profile, "Téléchargements"));

            var idx = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(roots.Distinct().Where(Directory.Exists), root =>
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        idx.TryAdd(Path.GetFileName(f), f);
                }
                catch { }
            });
            return idx.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        }

        // ==== Analyse des familles ====
        private List<ElementInfo> AnalyseFamilles(Document doc,
                                                 ExternalCommandData cmdData,
                                                 Dictionary<string, FamilyCacheEntry> cache,
                                                 string cacheFile)
        {
            var result = new List<ElementInfo>();
            var fams = new FilteredElementCollector(doc)
                       .OfClass(typeof(Family))
                       .WhereElementIsNotElementType()
                       .Cast<Family>();
            var instCounts = new FilteredElementCollector(doc)
                             .OfClass(typeof(FamilyInstance))
                             .WhereElementIsNotElementType()
                             .Cast<FamilyInstance>()
                             .GroupBy(fi => fi.Symbol.Family.Id.IntegerValue)
                             .ToDictionary(g => g.Key, g => g.Count());

            var prog = new ProgressWindow();
            new WindowInteropHelper(prog).Owner = cmdData.Application.MainWindowHandle;
            prog.Show();

            int total = fams.Count(), i = 0;
            foreach (var fam in fams)
            {
                if (prog.IsCancelled) { prog.Close(); throw new OperationCanceledException(); }
                i++;
                prog.UpdateProgress(i, total, fam.Name);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

                double mo = 0;
                int cnt = instCounts.TryGetValue(fam.Id.IntegerValue, out var c) ? c : 0;
                if (fam.IsEditable)
                {
                    string key = fam.Id.IntegerValue.ToString();
                    if (cache.TryGetValue(key, out var entry)
                        && entry.FamilyName == fam.Name)
                    {
                        mo = entry.TailleEnMo;
                    }
                    else
                    {
                        mo = GetFamilySizeMo(fam, doc);
                        cache[key] = new FamilyCacheEntry
                        {
                            FamilyId = fam.Id.IntegerValue,
                            FamilyName = fam.Name,
                            TailleEnMo = mo
                        };
                    }
                }

                // **Correction ici : on caste en FamilyInstance avant de filtrer**
                var instanceIds = new FilteredElementCollector(doc)
                                  .OfClass(typeof(FamilyInstance))
                                  .Cast<FamilyInstance>()
                                  .Where(fi => fi.Symbol.Family.Id == fam.Id)
                                  .Select(fi => fi.Id)
                                  .ToList();

                result.Add(new ElementInfo
                {
                    Nom = fam.Name,
                    Type = "Famille",
                    TailleEnMo = mo,
                    Count = cnt,
                    ElementIds = instanceIds
                });
            }

            prog.Close();
            SaveCache(cacheFile, cache);
            return result.OrderByDescending(f => f.TailleEnMo).ToList();
        }

        private double GetFamilySizeMo(Family fam, Document doc)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), fam.Name + ".rfa");
                var famDoc = doc.EditFamily(fam);
                famDoc.SaveAs(tmp, new SaveAsOptions { OverwriteExistingFile = true });
                double mo = new FileInfo(tmp).Length / 1024.0 / 1024.0;
                famDoc.Close(false);
                File.Delete(tmp);
                return mo;
            }
            catch { return 0; }
        }

        // ==== Analyse des imports, PDF, liens, nuages de points ====
        private List<ElementInfo> AnalyseImports(Document doc,
                                                 Dictionary<string, string> index)
        {
            var infos = new List<ElementInfo>();

            // 1) Imports CAO (DWG) — on distingue lié vs importé
            var allImps = new FilteredElementCollector(doc)
                          .OfClass(typeof(ImportInstance))
                          .Cast<ImportInstance>()
                          .ToList();

            // Groupe par nom ET par mode (lié ou importé)
            var groupedImps = allImps.GroupBy(imp =>
            {
                string name = imp.get_Parameter(BuiltInParameter.IMPORT_SYMBOL_NAME)
                                 ?.AsString() ?? "<Import DWG>";
                bool isLinked = imp.IsLinked;
                return (name, isLinked);
            });

            foreach (var grp in groupedImps)
            {
                string name = grp.Key.name;
                bool isLinked = grp.Key.isLinked;
                string kind = isLinked ? "Lien CAO" : "Import CAO";

                // Récupère le vrai chemin via param ou index disque
                string path = GetImportPath(grp.First(), doc);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    index.TryGetValue(name, out path);

                double mo = (!string.IsNullOrEmpty(path) && File.Exists(path))
                            ? new FileInfo(path).Length / 1024.0 / 1024.0
                            : 0;

                infos.Add(new ElementInfo
                {
                    Nom = name,
                    Type = kind,
                    TailleEnMo = mo,
                    Count = grp.Count(),
                    ElementIds = grp.Select(i => i.Id).ToList()
                });
            }

            // 2) PDF / Image raster
            var allImgs = new FilteredElementCollector(doc)
                          .OfClass(typeof(ImageInstance))
                          .Cast<ImageInstance>()
                          .ToList();
            var groupedImgs = allImgs.GroupBy(img =>
            {
                var type = doc.GetElement(img.GetTypeId());
                string path = type?.get_Parameter(BuiltInParameter.RASTER_SYMBOL_FILENAME)
                                  ?.AsString();
                return !string.IsNullOrEmpty(path)
                       ? Path.GetFileName(path)
                       : "<PDF/Image>";
            });
            foreach (var grp in groupedImgs)
            {
                string name = grp.Key;
                string path = doc.GetElement(grp.First().GetTypeId())
                                 .get_Parameter(BuiltInParameter.RASTER_SYMBOL_FILENAME)
                                 ?.AsString();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    index.TryGetValue(name, out path);
                double mo = (!string.IsNullOrEmpty(path) && File.Exists(path))
                            ? new FileInfo(path).Length / 1024.0 / 1024.0 : 0;

                infos.Add(new ElementInfo
                {
                    Nom = name,
                    Type = "PDF/Image",
                    TailleEnMo = mo,
                    Count = grp.Count(),
                    ElementIds = grp.Select(i => i.Id).ToList()
                });
            }

            // 3) Liens Revit/IFC
            var allLinks = new FilteredElementCollector(doc)
                           .OfClass(typeof(RevitLinkInstance))
                           .Cast<RevitLinkInstance>()
                           .ToList();
            var groupedLinks = allLinks.GroupBy(lk => lk.Name);
            foreach (var grp in groupedLinks)
            {
                string name = grp.Key;
                var ext = grp.First().GetType()
                              .GetMethod("GetExternalFileReference")
                              .Invoke(grp.First(), null) as ExternalFileReference;
                string path = ext != null
                    ? ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath())
                    : "";
                double mo = (!string.IsNullOrEmpty(path) && File.Exists(path))
                            ? new FileInfo(path).Length / 1024.0 / 1024.0 : 0;

                infos.Add(new ElementInfo
                {
                    Nom = name,
                    Type = "Lien Revit/IFC",
                    TailleEnMo = mo,
                    Count = grp.Count(),
                    ElementIds = grp.Select(i => i.Id).ToList()
                });
            }

            // 4) Nuages de points
            var allPC = new FilteredElementCollector(doc)
                        .OfClass(typeof(PointCloudInstance))
                        .Cast<PointCloudInstance>()
                        .ToList();
            var groupedPC = allPC.GroupBy(pc => pc.Name);
            foreach (var grp in groupedPC)
            {
                string name = grp.Key;
                var pt = doc.GetElement(grp.First().GetTypeId());
                var p = pt.LookupParameter("Point Cloud File Path")
                         ?? pt.LookupParameter("Source File Path")
                         ?? pt.LookupParameter("File Path");
                string path = p?.AsString();
                double mo = (!string.IsNullOrEmpty(path) && File.Exists(path))
                            ? new FileInfo(path).Length / 1024.0 / 1024.0 : 0;

                infos.Add(new ElementInfo
                {
                    Nom = name,
                    Type = "Nuage de points",
                    TailleEnMo = mo,
                    Count = grp.Count(),
                    ElementIds = grp.Select(i => i.Id).ToList()
                });
            }

            return infos;
        }

        private string GetImportPath(ImportInstance imp, Document doc)
        {
            var sym = doc.GetElement(imp.GetTypeId());
            if (sym == null) return null;
            string[] keys = {
                "Source File Path","Chemin du fichier source",
                "DWG File Path","Chemin du fichier DWG",
                "Linked File Path","Chemin du lien",
                "File Path","Chemin du fichier"
            };
            foreach (var k in keys)
            {
                var p = sym.LookupParameter(k);
                if (p != null && !string.IsNullOrEmpty(p.AsString()))
                    return p.AsString();
            }
            return null;
        }

        // ==== Cache JSON pour familles ====
        private Dictionary<string, FamilyCacheEntry> LoadCache(string path)
        {
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<Dictionary<string, FamilyCacheEntry>>(
                           File.ReadAllText(path));
            return new Dictionary<string, FamilyCacheEntry>();
        }
        private void SaveCache(string path, Dictionary<string, FamilyCacheEntry> cache)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(cache, Formatting.Indented));
        }
    }

    // ==== DTO pour tous les éléments ====
    public class ElementInfo
    {
        public string Nom { get; set; }
        public string Type { get; set; }
        public double TailleEnMo { get; set; }
        public int Count { get; set; }
        public IList<ElementId> ElementIds { get; set; }
    }

    public class FamilyCacheEntry
    {
        public int FamilyId { get; set; }
        public string FamilyName { get; set; }
        public double TailleEnMo { get; set; }
    }
}

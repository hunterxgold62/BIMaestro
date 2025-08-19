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

namespace Analyse
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
                "RevitLogs", "SauvegardePréférence");
            Directory.CreateDirectory(logsFolder);
            string cacheFile = Path.Combine(logsFolder, "CacheTailleFamille.json");
            var cache = LoadCache(cacheFile);

            // 3. Lancer l'indexation disque en tâche de fond
            var importNames = GetImportNames(doc);
            var roots = GetSearchRoots(doc);
            var indexTask = Task.Run(() => BuildFileIndex(importNames, roots));

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

      /// <summary>
/// Construit la liste des noms de fichiers (DWG, PDF, RCP, IFC, RVT, etc.)
/// à rechercher dans l’index.
/// </summary>
private List<string> GetImportNames(Document doc)
{
    var names = new List<string>();

    // 1) DWG / CAO
    var allImps = new FilteredElementCollector(doc)
                  .OfClass(typeof(ImportInstance))
                  .Cast<ImportInstance>();
    foreach (var imp in allImps)
    {
        string dwgName = imp
            .get_Parameter(BuiltInParameter.IMPORT_SYMBOL_NAME)
            ?.AsString()
            ?? "<Import DWG>";
        names.Add(dwgName);
    }

    // 2) PDF / Image raster
    var allImgs = new FilteredElementCollector(doc)
                  .OfClass(typeof(ImageInstance))
                  .Cast<ImageInstance>();
    foreach (var img in allImgs)
    {
        var type = doc.GetElement(img.GetTypeId());
        string fullPath = type
            ?.get_Parameter(BuiltInParameter.RASTER_SYMBOL_FILENAME)
            ?.AsString();
        string fileName = !string.IsNullOrEmpty(fullPath)
            ? Path.GetFileName(fullPath)
            : "<PDF/Image>";
        names.Add(fileName);
    }

    // 3) Liens Revit/IFC/RVT
    var allLinks = new FilteredElementCollector(doc)
                   .OfClass(typeof(RevitLinkInstance))
                   .Cast<RevitLinkInstance>();
    foreach (var lk in allLinks)
    {
        string extPath = null;
        try
        {
            var method = lk.GetType().GetMethod("GetExternalFileReference");
            if (method != null)
            {
                var ext = method.Invoke(lk, null) as ExternalFileReference;
                if (ext != null)
                    extPath = ModelPathUtils
                        .ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath());
            }
        }
        catch { /* ignore */ }

        string fileName = !string.IsNullOrEmpty(extPath)
            ? Path.GetFileName(extPath)
            : lk.Name + ".rvt"; // ou ".ifc" si tes liens sont IFC
        names.Add(fileName);
    }

    // 4) Nuages de points (RCP)
    // clés de paramètre en anglais et en français
    var pcParamKeys = new[]
    {
        "Point Cloud File Path",
        "Source File Path",
        "File Path",
        "Chemin du fichier nuage de points",
        "Chemin du fichier source",
        "Chemin du fichier"
    };

    var allPC = new FilteredElementCollector(doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>();
    foreach (var pc in allPC)
    {
        var pt = doc.GetElement(pc.GetTypeId());
        string fullPath = null;

        // on cherche l’un des paramètres valides
        foreach (var key in pcParamKeys)
        {
            var param = pt.LookupParameter(key);
            if (param != null && !string.IsNullOrEmpty(param.AsString()))
            {
                fullPath = param.AsString();
                break;
            }
        }

        string fileName = !string.IsNullOrEmpty(fullPath)
            ? Path.GetFileName(fullPath)
            : pc.Name + ".rcp";
        names.Add(fileName);
    }

    // Élimine les doublons et renvoie
    return names
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}



        /// <summary>
        /// Construit la liste de dossiers racine à parcourir.
        /// </summary>
        private List<string> GetSearchRoots(Document doc)
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

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.AddRange(new[] {
        docs,
        Path.Combine(profile, "Downloads"),
        Path.Combine(profile, "Téléchargements")
    });

            return roots.Distinct().Where(Directory.Exists).ToList();
        }

        // ==== Indexation disque ====
        /// <summary>
        /// Ne parcourt que les dossiers racine, et s’arrête quand tous les importNames ont un chemin.
        /// </summary>
        private Dictionary<string, string> BuildFileIndex(
            IEnumerable<string> importNames,
            IEnumerable<string> roots)
        {
            // Dico thread-safe : clé = nom de fichier, valeur = chemin (null tant que non trouvé)
            var remaining = new ConcurrentDictionary<string, string>(
                importNames.ToDictionary(n => n, n => (string)null),
                StringComparer.OrdinalIgnoreCase);

            Parallel.ForEach(roots, (root, state) =>
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (remaining.ContainsKey(name) && remaining[name] == null)
                    {
                        remaining[name] = file;
                        // Si tout a été trouvé, on stoppe le Parallel.ForEach
                        if (remaining.All(kv => kv.Value != null))
                        {
                            state.Break();
                            break;
                        }
                    }
                }
            });

            // Renvoie seulement les entrées où on a un chemin
            return remaining
                .Where(kv => kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
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

            // 3) Liens Revit/IFC/RVT (on groupe par NOM DE FICHIER)
            var allLinks = new FilteredElementCollector(doc)
                           .OfClass(typeof(RevitLinkInstance))
                           .Cast<RevitLinkInstance>()
                           .ToList();

            var groupedLinks = allLinks.GroupBy(lk =>
            {
                // 1) Tenter la réflexion pour récupérer le chemin complet
                string fullPath = null;
                try
                {
                    var method = lk.GetType().GetMethod("GetExternalFileReference");
                    if (method != null)
                    {
                        var ext = method.Invoke(lk, null) as ExternalFileReference;
                        if (ext != null)
                            fullPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath());
                    }
                }
                catch { }

                // 2) Nom de fichier final (avec extension .rvt ou .ifc)
                return !string.IsNullOrEmpty(fullPath)
                    ? Path.GetFileName(fullPath)
                    : lk.Name + ".rvt";
            });

            foreach (var grp in groupedLinks)
            {
                string fileName = grp.Key;
                string path = null;

                // 3) Retenter la réflexion si besoin
                try
                {
                    var method = grp.First().GetType().GetMethod("GetExternalFileReference");
                    if (method != null)
                    {
                        var ext = method.Invoke(grp.First(), null) as ExternalFileReference;
                        if (ext != null)
                            path = ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath());
                    }
                }
                catch { }

                // 4) Fallback sur l'index si toujours pas de chemin ou fichier inexistant
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    index.TryGetValue(fileName, out path);
                }

                double mo = (!string.IsNullOrEmpty(path) && File.Exists(path))
                            ? new FileInfo(path).Length / 1024.0 / 1024.0
                            : 0.0;

                infos.Add(new ElementInfo
                {
                    Nom = fileName,
                    Type = "Lien Revit/IFC",
                    TailleEnMo = mo,
                    Count = grp.Count(),
                    ElementIds = grp.Select(lk => lk.Id).ToList()
                });
            }




            /// 4) Nuages de points — on traite chaque instance séparément
            var allPC = new FilteredElementCollector(doc)
                        .OfClass(typeof(PointCloudInstance))
                        .Cast<PointCloudInstance>()
                        .ToList();

            foreach (var pc in allPC)
            {
                // 1) On essaye d'abord de lire le chemin natif via PointCloudType.GetPath()
                string fullPath = null;
                var pcType = doc.GetElement(pc.GetTypeId()) as PointCloudType;
                if (pcType != null)
                {
                    ModelPath mp = pcType.GetPath();
                    if (mp != null)
                        fullPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                }

                // 2) Si échec ou fichier introuvable, on fouille dans l'index
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                {
                    // clé = nom de la ressource sans chemin, avec extension si possible
                    // essayons d'abord avec pc.Name + ".rcp"
                    string key = pc.Name.EndsWith(".rcp", StringComparison.OrdinalIgnoreCase)
                                 ? pc.Name
                                 : pc.Name + ".rcp";

                    index.TryGetValue(key, out fullPath);
                }

                // 3) Si toujours rien, on scan le dossier du RVT (et parents) pour ce seul fichier
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                {
                    string rvtFolder = Path.GetDirectoryName(doc.PathName);
                    try
                    {
                        fullPath = Directory
                            .EnumerateFiles(rvtFolder, pc.Name + ".*", SearchOption.AllDirectories)
                            .FirstOrDefault(f => f.EndsWith(".rcp", StringComparison.OrdinalIgnoreCase));
                    }
                    catch { /* accès refusé ou autre, on ignore */ }
                }

                // 4) Calcul de la taille
                double mo = (fullPath != null && File.Exists(fullPath))
                            ? new FileInfo(fullPath).Length / 1024.0 / 1024.0
                            : 0.0;

                // 5) On ajoute le résultat
                infos.Add(new ElementInfo
                {
                    Nom = pc.Name,           // ou Path.GetFileName(fullPath)
                    Type = "Nuage de points",
                    TailleEnMo = mo,
                    Count = 1,
                    ElementIds = new List<ElementId> { pc.Id }
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
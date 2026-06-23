using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using System.Windows.Threading;
using System.Windows.Interop;
using Licensing;

namespace Analyse
{
    [Transaction(TransactionMode.Manual)]
    public class CommandAnalysePoids : BaseTrackedCommand


    {

        protected override string ButtonId => "CommandAnalysePoids";
        private static readonly TimeSpan FileSearchTimeLimit = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan PointCloudFallbackSearchTimeLimit = TimeSpan.FromSeconds(4);
        private const int FileSearchMaxVisitedFiles = 60000;
        private const int PointCloudFallbackMaxVisitedFiles = 12000;

        private static readonly HashSet<string> SearchableImportExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".dwg", ".dxf", ".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff",
                ".rvt", ".ifc", ".rcp", ".rcs"
            };

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
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

            // 3. Préparer les fichiers à retrouver sur disque. La recherche est bornée plus bas.
            var importNames = GetImportNames(doc);
            var roots = GetSearchRoots(doc);

            // 4. Analyser les familles
            List<ElementInfo> famInfos;
            try
            {
                famInfos = AnalyseFamilles(doc, commandData, cache, cacheFile);
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }

            // 5. Recherche disque limitée, puis analyse des imports & PDF
            var fileIndex = BuildFileIndex(importNames, roots);
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
        private List<SearchRoot> GetSearchRoots(Document doc)
        {
            var roots = new List<SearchRoot>();

            string rvtPath = doc.IsWorkshared
                ? ModelPathUtils.ConvertModelPathToUserVisiblePath(
                    doc.GetWorksharingCentralModelPath())
                : doc.PathName;
            // Le fichier peut être une famille non enregistrée : dans ce cas PathName est vide
            // et DirectoryInfo ne doit pas être construit avec une chaîne vide/null.
            var dirPath = string.IsNullOrEmpty(rvtPath)
                ? null
                : Path.GetDirectoryName(rvtPath);

            if (!string.IsNullOrEmpty(dirPath))
            {
                var dir = new DirectoryInfo(dirPath);
                AddSearchRoot(roots, dir.FullName, 6);

                string[] nearbyFolders =
                {
                    "Liens", "Links", "Imports", "Xrefs", "DWG", "PDF",
                    "Nuages de points", "PointClouds", "References"
                };

                foreach (var folder in nearbyFolders)
                    AddSearchRoot(roots, Path.Combine(dir.FullName, folder), 5);

                for (int i = 0; i < 2 && dir.Parent != null; i++)
                {
                    dir = dir.Parent;
                    AddSearchRoot(roots, dir.FullName, 3);
                }
            }

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddSearchRoot(roots, docs, 2);
            AddSearchRoot(roots, Path.Combine(profile, "Downloads"), 2);
            AddSearchRoot(roots, Path.Combine(profile, "Téléchargements"), 2);

            return roots
                .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => r.MaxDepth).First())
                .Where(r => Directory.Exists(r.Path))
                .ToList();
        }

        // ==== Indexation disque ====
        /// <summary>
        /// Recherche seulement les fichiers utiles, avec limite de temps et de volume parcouru.
        /// </summary>
        private Dictionary<string, string> BuildFileIndex(
            IEnumerable<string> importNames,
            IEnumerable<SearchRoot> roots)
        {
            var exactTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var baseNameTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var importName in importNames ?? Enumerable.Empty<string>())
            {
                var name = NormalizeFileName(importName);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var ext = Path.GetExtension(name);
                if (!string.IsNullOrWhiteSpace(ext))
                {
                    if (SearchableImportExtensions.Contains(ext))
                        exactTargets[name] = name;
                }
                else
                {
                    baseNameTargets[name] = name;
                }
            }

            if (exactTargets.Count == 0 && baseNameTargets.Count == 0)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var watch = Stopwatch.StartNew();
            int visited = 0;

            bool CanContinue() =>
                watch.Elapsed < FileSearchTimeLimit &&
                visited < FileSearchMaxVisitedFiles &&
                (exactTargets.Count > 0 || baseNameTargets.Count > 0);

            foreach (var root in roots ?? Enumerable.Empty<SearchRoot>())
            {
                if (!CanContinue()) break;

                foreach (var file in SafeEnumerateFiles(root.Path, root.MaxDepth, CanContinue))
                {
                    visited++;
                    if (!CanContinue()) break;

                    var ext = Path.GetExtension(file);
                    if (!SearchableImportExtensions.Contains(ext)) continue;

                    var fileName = Path.GetFileName(file);
                    if (fileName != null && exactTargets.TryGetValue(fileName, out var exactKey))
                    {
                        found[exactKey] = file;
                        exactTargets.Remove(fileName);
                        continue;
                    }

                    var baseName = Path.GetFileNameWithoutExtension(file);
                    if (baseName != null && baseNameTargets.TryGetValue(baseName, out var baseKey))
                    {
                        found[baseKey] = file;
                        baseNameTargets.Remove(baseName);
                    }
                }
            }

            return found;
        }

        private IEnumerable<string> SafeEnumerateFiles(string root, int maxDepth, Func<bool> shouldContinue)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                yield break;

            var stack = new Stack<SearchQueueItem>();
            stack.Push(new SearchQueueItem(root, 0));

            while (stack.Count > 0 && shouldContinue())
            {
                var item = stack.Pop();
                var current = item.Path;
                IEnumerable<string> files;

                try
                {
                    var attrs = File.GetAttributes(current);
                    if ((attrs & FileAttributes.ReparsePoint) != 0 ||
                        (attrs & FileAttributes.System) != 0)
                        continue;

                    files = Directory.EnumerateFiles(current);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (!shouldContinue()) yield break;
                    yield return file;
                }

                if (item.Depth >= maxDepth)
                    continue;

                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(current);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var dir in subDirs)
                {
                    try
                    {
                        var attrs = File.GetAttributes(dir);
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                            continue;

                        stack.Push(new SearchQueueItem(dir, item.Depth + 1));
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Dossier protégé : on ignore et on continue l'indexation.
                    }
                    catch (IOException)
                    {
                        // Dossier invalide/indisponible : on ignore et on continue.
                    }
                }
            }
        }

        private static void AddSearchRoot(ICollection<SearchRoot> roots, string path, int maxDepth)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath)) return;

                var root = Path.GetPathRoot(fullPath);
                if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    return;

                roots.Add(new SearchRoot(fullPath, maxDepth));
            }
            catch
            {
                // Chemin invalide ou inaccessible : on l'ignore.
            }
        }

        private static string NormalizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var name = value.Trim();
            if (name.StartsWith("<") && name.EndsWith(">")) return null;

            try
            {
                name = Path.GetFileName(name);
            }
            catch
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(name) ? null : name;
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
                             .GroupBy(fi => fi.Symbol.Family.Id.GetIdValue())
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
                int cnt = instCounts.TryGetValue(fam.Id.GetIdValue(), out var c) ? c : 0;
                if (fam.IsEditable)
                {
                    string key = fam.Id.GetIdValue().ToString();
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
                            FamilyId = fam.Id.GetIdValue(),
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
                    ElementIds = instanceIds,
                    PrimaryId = fam.Id
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

                var elementIds = grp.Select(i => i.Id).ToList();
                infos.Add(new ElementInfo
                {
                    Nom = name,
                    Type = kind,
                    TailleEnMo = mo,
                    Count = elementIds.Count,
                    ElementIds = elementIds,
                    PrimaryId = elementIds.FirstOrDefault()
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

                var elementIds = grp.Select(i => i.Id).ToList();
                infos.Add(new ElementInfo
                {
                    Nom = name,
                    Type = "PDF/Image",
                    TailleEnMo = mo,
                    Count = elementIds.Count,
                    ElementIds = elementIds,
                    PrimaryId = elementIds.FirstOrDefault()
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

                var elementIds = grp.Select(lk => lk.Id).ToList();
                infos.Add(new ElementInfo
                {
                    Nom = fileName,
                    Type = "Lien Revit/IFC",
                    TailleEnMo = mo,
                    Count = elementIds.Count,
                    ElementIds = elementIds,
                    PrimaryId = elementIds.FirstOrDefault()
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
                    fullPath = FindPointCloudFileNearModel(doc, pc.Name);
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
                    ElementIds = new List<ElementId> { pc.Id },
                    PrimaryId = pc.Id
                });
            }




            return infos;
        }

        private string FindPointCloudFileNearModel(Document doc, string pointCloudName)
        {
            var rvtFolder = string.IsNullOrWhiteSpace(doc.PathName)
                ? null
                : Path.GetDirectoryName(doc.PathName);

            if (string.IsNullOrWhiteSpace(rvtFolder) || !Directory.Exists(rvtFolder))
                return null;

            var watch = Stopwatch.StartNew();
            int visited = 0;
            bool CanContinue() =>
                watch.Elapsed < PointCloudFallbackSearchTimeLimit &&
                visited < PointCloudFallbackMaxVisitedFiles;

            foreach (var file in SafeEnumerateFiles(rvtFolder, 3, CanContinue))
            {
                visited++;
                if (!CanContinue()) break;

                if (!string.Equals(Path.GetExtension(file), ".rcp", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(Path.GetFileNameWithoutExtension(file), pointCloudName, StringComparison.OrdinalIgnoreCase))
                    return file;
            }

            return null;
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

        private sealed class SearchRoot
        {
            public SearchRoot(string path, int maxDepth)
            {
                Path = path;
                MaxDepth = maxDepth;
            }

            public string Path { get; }
            public int MaxDepth { get; }
        }

        private sealed class SearchQueueItem
        {
            public SearchQueueItem(string path, int depth)
            {
                Path = path;
                Depth = depth;
            }

            public string Path { get; }
            public int Depth { get; }
        }
    }

    // ==== DTO pour tous les éléments ====
    public class ElementInfo
    {
        public string Nom { get; set; }
        public string Type { get; set; }
        public double TailleEnMo { get; set; }
        public string TailleAffiche => $"{TailleEnMo:N2} Mo";

        public int Count { get; set; }
        public IList<ElementId> ElementIds { get; set; }
        public ElementId PrimaryId { get; set; }
        public bool IsFamily => string.Equals(Type, "Famille", StringComparison.OrdinalIgnoreCase);
    }

    public class FamilyCacheEntry
    {
        public int FamilyId { get; set; }
        public string FamilyName { get; set; }
        public double TailleEnMo { get; set; }
    }
}

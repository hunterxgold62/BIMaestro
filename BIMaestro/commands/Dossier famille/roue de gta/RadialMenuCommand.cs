using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Famille; // Usage/Recent + CatalogImageResolver + ThumbnailCache + ShellThumbnailProvider + ReloadFamilyHandler
using Licensing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMaestro.UI
{
    [Transaction(TransactionMode.Manual)]
    public class RadialMenuCommand : BaseTrackedCommand
    {

        protected override string ButtonId => "RadialMenuCommand";
        private static RadialPlaceFamilyHandler s_placeHandler;
        private static ExternalEvent s_placeEvent;

        // >>> nouveau : handler "Recharger (overwrite type values)"
        private static ReloadFamilyHandler s_reloadHandler;
        private static ExternalEvent s_reloadEvent;

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            try
            {
                var uiapp = commandData.Application;
                EnsureHandlers();

                var (screenX, screenY) = OwnerWindowHelper.GetCursorPosPx();

                var state = RadialMenuCollectionStateStore.Load();
                var collections = CollectionStore.Load() ?? new List<Collection>();

                var standardData = BuildStandardData();
                RadialMenuData activeData = standardData;
                Collection activeCollection = null;

                if (state != null && state.UseCollection && !string.IsNullOrWhiteSpace(state.ActiveCollectionId))
                {
                    activeCollection = collections.FirstOrDefault(c => string.Equals(c.Id, state.ActiveCollectionId, StringComparison.OrdinalIgnoreCase));
                    if (activeCollection != null)
                    {
                        activeData = BuildCollectionData(activeCollection);
                    }
                    else
                    {
                        state.UseCollection = false;
                        state.ActiveCollectionId = null;
                        RadialMenuCollectionStateStore.Save(state);
                    }
                }

                var win = new RadialMenuWindow(activeData.Items, screenX, screenY);
                win.SetPageLabelFactory(activeData.PageLabelFactory);
                win.UpdateCollectionState(activeData.IsCollectionMode, activeData.CollectionName, activeCollection?.Id);

                win.ConfigureCollectionActions(
                    () =>
                    {
                        var fresh = CollectionStore.Load() ?? new List<Collection>();
                        return fresh.Select(c => (c.Id, c.Name)).ToList();
                    },
                    id =>
                    {
                        if (string.IsNullOrWhiteSpace(id)) return;
                        var freshCollections = CollectionStore.Load() ?? new List<Collection>();
                        var selected = freshCollections.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                        if (selected == null) return;

                        var updated = BuildCollectionData(selected);
                        state.UseCollection = true;
                        state.ActiveCollectionId = selected.Id;
                        state.LastCollectionId = selected.Id;
                        RadialMenuCollectionStateStore.Save(state);

                        win.ReplaceItems(updated.Items);
                        win.SetPageLabelFactory(updated.PageLabelFactory);
                        win.UpdateCollectionState(true, selected.Name, selected.Id);
                    },
                    () =>
                    {
                        state.UseCollection = false;
                        state.ActiveCollectionId = null;
                        RadialMenuCollectionStateStore.Save(state);

                        var updated = BuildStandardData();
                        win.ReplaceItems(updated.Items);
                        win.SetPageLabelFactory(updated.PageLabelFactory);
                        win.UpdateCollectionState(false, null, null);
                    });

                // Gauche = placer (load keep + placement)
                win.Completed += (accepted, _, item) =>
                {
                    if (!accepted || item == null || string.IsNullOrWhiteSpace(item.FamilyPath)) return;
                    s_placeHandler.FamilyPath = item.FamilyPath;
                    s_placeEvent.Raise();
                };

                // >>> Clic droit = recharger dernière version (overwrite)
                win.ReloadRequested += item =>
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.FamilyPath)) return;
                    s_reloadHandler.FamilyPath = item.FamilyPath;
                    s_reloadEvent.Raise();
                };

                win.Show();
                win.Activate();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }

        private static RadialMenuData BuildStandardData()
        {
            var usage = FamilyUsageManager.Load();
            var top8 = usage.OrderByDescending(kv => kv.Value)
                            .Select(kv => kv.Key)
                            .Where(File.Exists)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(8)
                            .ToList();

            var recent16 = FamilyRecentManager.LoadMostRecentDistinct(16, File.Exists);

            foreach (var p in recent16)
            {
                if (top8.Count >= 8) break;
                if (!top8.Contains(p, StringComparer.OrdinalIgnoreCase)) top8.Add(p);
            }

            var recentA = recent16.Take(8).ToList();
            var recentB = recent16.Skip(8).Take(8).ToList();

            var samplePaths = top8.Concat(recent16).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            InitRootsForPhotos(samplePaths);

            var items = new List<RadialItem>(24);
            items.AddRange(BuildItems(top8));
            items.AddRange(BuildItems(recentA));
            items.AddRange(BuildItems(recentB));
            while (items.Count < 24) items.Add(new RadialItem());

            return new RadialMenuData
            {
                Items = items,
                IsCollectionMode = false,
                CollectionName = null,
                PageLabelFactory = (index, count) => index switch
                {
                    0 => "Top-8",
                    1 => "Récents (1/2)",
                    2 => "Récents (2/2)",
                    _ => $"Page {index + 1}/{count}",
                }
            };
        }

        private static RadialMenuData BuildCollectionData(Collection collection)
        {
            var paths = collection?.Paths ?? new List<string>();
            var normalized = paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            InitRootsForPhotos(normalized);

            var items = BuildItems(normalized).ToList();

            return new RadialMenuData
            {
                Items = items,
                IsCollectionMode = true,
                CollectionName = collection?.Name,
                PageLabelFactory = CreateCollectionPageLabelFactory(collection?.Name)
            };
        }

        private static Func<int, int, string> CreateCollectionPageLabelFactory(string collectionName)
        {
            string displayName = string.IsNullOrWhiteSpace(collectionName) ? string.Empty : collectionName.Trim();
            return (index, count) =>
            {
                if (count <= 1)
                {
                    return string.IsNullOrEmpty(displayName) ? string.Empty : displayName;
                }

                if (string.IsNullOrEmpty(displayName))
                    return $"Page {index + 1}/{count}";

                return $"{displayName}({index + 1}/{count})";
            };
        }

        // ==== PHOTOS (même logique que le navigateur) ====

        private static IEnumerable<RadialItem> BuildItems(IEnumerable<string> familyPaths)
        {
            foreach (var p in familyPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;

                // 1) Photo "catalogue" (CatalogImageResolver connaît les racines)
                string img = CatalogImageResolver.Resolve(null, null, p);

                // 2) Secours : vignette Windows + cache disque
                if (string.IsNullOrEmpty(img))
                {
                    const int SIZE = 256;
                    if (ThumbnailCache.TryGet(CacheFolder, p, SIZE, out var cached) && File.Exists(cached))
                        img = cached;
                    else if (ShellThumbnailProvider.TryGetThumbnail(p, SIZE, out var bmp))
                    {
                        try { img = ThumbnailCache.Save(CacheFolder, p, SIZE, bmp); } catch { }
                    }
                }

                yield return new RadialItem
                {
                    FamilyPath = p,
                    ImagePath = img,
                    Label = Path.GetFileNameWithoutExtension(p)
                };
            }
        }

        private sealed class RadialMenuData
        {
            public List<RadialItem> Items { get; set; } = new();
            public bool IsCollectionMode { get; set; }
            public string CollectionName { get; set; }
            public Func<int, int, string> PageLabelFactory { get; set; }
        }

        private static void InitRootsForPhotos(List<string> sampleFamilyPaths)
        {
            // 1) JSON si présent
            var cfg = RadialPathsConfig.LoadOrNull();
            if (cfg != null && Directory.Exists(cfg.FamiliesFolder))
            {
                CatalogImageResolver.Initialize(cfg.FamiliesFolder, cfg.ImagesFolder);
                return;
            }

            // 2) Inférence sinon
            if (sampleFamilyPaths == null || sampleFamilyPaths.Count == 0) return;

            string famRoot = InferFamiliesRoot(sampleFamilyPaths);
            if (string.IsNullOrWhiteSpace(famRoot) || !Directory.Exists(famRoot)) return;

            string imgRoot = FindBestImagesRoot(famRoot, sampleFamilyPaths);
            if (string.IsNullOrWhiteSpace(imgRoot) || !Directory.Exists(imgRoot))
                imgRoot = famRoot;

            CatalogImageResolver.Initialize(famRoot, imgRoot);
        }

        private static string InferFamiliesRoot(List<string> familyPaths)
        {
            try
            {
                var parts = familyPaths
                    .Where(File.Exists)
                    .Select(p => Path.GetDirectoryName(p))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.Split(Path.DirectorySeparatorChar))
                    .ToList();

                if (parts.Count == 0) return null;

                int minLen = parts.Min(a => a.Length);
                var common = new List<string>();
                for (int i = 0; i < minLen; i++)
                {
                    string seg = parts[0][i];
                    if (parts.All(a => string.Equals(a[i], seg, StringComparison.OrdinalIgnoreCase)))
                        common.Add(seg);
                    else break;
                }
                if (common.Count == 0) return null;

                int idxA = common.FindIndex(s => s.StartsWith("A-", StringComparison.OrdinalIgnoreCase));
                if (idxA >= 0) common = common.Take(idxA + 1).ToList();

                return string.Join(Path.DirectorySeparatorChar.ToString(), common);
            }
            catch { return null; }
        }

        private static string FindBestImagesRoot(string familiesRoot, List<string> sampleFamilyPaths)
        {
            try
            {
                const int MAX = 30;
                var sample = sampleFamilyPaths.Where(File.Exists).Take(MAX).ToList();
                if (sample.Count == 0) return null;

                var candidates = EnumerateCandidateImageRoots(familiesRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (candidates.Count == 0) return null;

                string best = null;
                int bestScore = -1;
                foreach (var cand in candidates)
                {
                    int score = EvaluateImageRootScore(cand, familiesRoot, sample);
                    if (score > bestScore) { bestScore = score; best = cand; }
                }

                int threshold = Math.Max(3, sample.Count / 4);
                return (bestScore >= threshold) ? best : null;
            }
            catch { return null; }
        }

        private static int EvaluateImageRootScore(string candidateRoot, string familiesRoot, List<string> sample)
        {
            int hits = 0;
            foreach (var p in sample)
            {
                string rel = GetRelativePathSafe(familiesRoot, p);
                string name = Path.GetFileNameWithoutExtension(p);

                string[] mirror =
                {
                    Path.ChangeExtension(Path.Combine(candidateRoot, rel), ".png"),
                    Path.ChangeExtension(Path.Combine(candidateRoot, rel), ".jpg"),
                };
                string[] flat =
                {
                    Path.Combine(candidateRoot, name + ".png"),
                    Path.Combine(candidateRoot, name + ".jpg"),
                };

                if (mirror.Any(File.Exists) || flat.Any(File.Exists)) hits++;
            }
            return hits;
        }

        private static IEnumerable<string> EnumerateCandidateImageRoots(string familiesRoot)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(familiesRoot)) yield break;

            string parent = Path.GetDirectoryName(familiesRoot);
            string grand = string.IsNullOrEmpty(parent) ? null : Path.GetDirectoryName(parent);

            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                foreach (var d in SafeDirs(parent)) set.Add(d);

            if (!string.IsNullOrEmpty(grand) && Directory.Exists(grand))
                foreach (var d in SafeDirs(grand)) set.Add(d);

            var keywords = new[] { "image", "images", "img", "preview", "vignet", "png", "jpg" };

            foreach (var d in set.Where(p => keywords.Any(k => Path.GetFileName(p).ToLowerInvariant().Contains(k))))
                yield return d;
            foreach (var d in set.Where(p => Path.GetFileName(p).StartsWith("B-", StringComparison.OrdinalIgnoreCase)))
                yield return d;
            foreach (var d in set) yield return d;

            static IEnumerable<string> SafeDirs(string root)
            {
                try { return Directory.GetDirectories(root); }
                catch { return Array.Empty<string>(); }
            }
        }

        private static string GetRelativePathSafe(string root, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(root)) return Path.GetFileName(path);
                var fromUri = new Uri(root.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? root : root + Path.DirectorySeparatorChar);
                var toUri = new Uri(path);
                var relUri = fromUri.MakeRelativeUri(toUri);
                return Uri.UnescapeDataString(relUri.ToString())
                          .Replace('/', Path.DirectorySeparatorChar);
            }
            catch { return Path.GetFileName(path); }
        }

        private static string CacheFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "CacheVignettes");

        private static void EnsureHandlers()
        {
            if (s_placeHandler == null)
            {
                s_placeHandler = new RadialPlaceFamilyHandler();
                s_placeEvent = ExternalEvent.Create(s_placeHandler);
            }
            if (s_reloadHandler == null)
            {
                s_reloadHandler = new ReloadFamilyHandler();
                s_reloadEvent = ExternalEvent.Create(s_reloadHandler);
            }
            Directory.CreateDirectory(CacheFolder);
        }
    }
}

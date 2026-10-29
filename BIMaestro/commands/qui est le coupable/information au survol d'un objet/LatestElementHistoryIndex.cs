using Autodesk.Revit.DB;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Analyse
{
    /// <summary>
    /// Index minimal du dernier événement par élément. Les lectures de fichiers
    /// se font une seule fois, en arrière-plan, jamais pendant une sélection.
    /// </summary>
    internal static class LatestElementHistoryIndex
    {
        private sealed class PersistedIndex
        {
            public DateTime SavedUtc { get; set; }
            public List<PersistedEvent> Events { get; set; } =
                new List<PersistedEvent>();
        }

        private sealed class PersistedEvent
        {
            public DateTime Ts { get; set; }
            public string ModelKey { get; set; }
            public string UniqueId { get; set; }
            public string Action { get; set; }
            public string User { get; set; }

            public ElementHistoryEvent ToHistoryEvent()
            {
                return new ElementHistoryEvent
                {
                    Ts = Ts,
                    ModelKey = ModelKey,
                    UniqueId = UniqueId,
                    Action = Action,
                    User = User
                };
            }

            public static PersistedEvent FromHistoryEvent(
                ElementHistoryEvent historyEvent)
            {
                return new PersistedEvent
                {
                    Ts = historyEvent.Ts,
                    ModelKey = historyEvent.ModelKey,
                    UniqueId = historyEvent.UniqueId,
                    Action = historyEvent.Action,
                    User = historyEvent.User
                };
            }
        }

        private static readonly string IndexCacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "CacheIndex",
            "QuiAFaitCa");

        private static readonly ConcurrentDictionary<string, ElementHistoryEvent>
            LatestByElement =
                new ConcurrentDictionary<string, ElementHistoryEvent>(
                    StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, byte> ScheduledModels =
            new ConcurrentDictionary<string, byte>(
                StringComparer.OrdinalIgnoreCase);

        internal static void Observe(ElementHistoryEvent historyEvent)
        {
            if (historyEvent == null ||
                string.IsNullOrWhiteSpace(historyEvent.ModelKey) ||
                string.IsNullOrWhiteSpace(historyEvent.UniqueId))
            {
                return;
            }

            string key = BuildKey(
                historyEvent.ModelKey,
                historyEvent.UniqueId);
            LatestByElement.AddOrUpdate(
                key,
                historyEvent,
                (_, current) => current == null ||
                                historyEvent.Ts >= current.Ts
                    ? historyEvent
                    : current);
        }

        internal static void ScheduleBackgroundLoad(Document document)
        {
            if (document == null) return;

            List<string> modelKeys = ElementHistoryTracker
                .GetDocumentKeysForHistory(document);
            string primaryKey = ElementHistoryTracker
                .GetDocumentKeyForHistory(document);
            if (string.IsNullOrWhiteSpace(primaryKey) ||
                !ScheduledModels.TryAdd(primaryKey, 0))
            {
                return;
            }

            string historyDirectory =
                CollaborativeModelTrackerStore.ActiveDirectory;
            Task.Run(async () =>
            {
                string cachePath = GetCachePath(primaryKey);
                PersistedIndex persisted = LoadPersistedIndex(cachePath);
                IEnumerable<PersistedEvent> cachedEvents =
                    persisted?.Events ?? new List<PersistedEvent>();
                foreach (PersistedEvent cachedEvent in cachedEvents)
                {
                    Observe(cachedEvent?.ToHistoryEvent());
                }

                // Le chargement du document reste prioritaire.
                await Task.Delay(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                LoadHistory(
                    historyDirectory,
                    modelKeys,
                    persisted?.SavedUtc);
                SavePersistedIndex(cachePath, modelKeys);
            });
        }

        internal static bool TryGetLatest(
            Document document,
            Element element,
            out ElementHistoryEvent historyEvent)
        {
            historyEvent = null;
            if (document == null || element == null ||
                string.IsNullOrWhiteSpace(element.UniqueId))
            {
                return false;
            }

            foreach (string modelKey in ElementHistoryTracker
                         .GetDocumentKeysForHistory(document))
            {
                if (LatestByElement.TryGetValue(
                        BuildKey(modelKey, element.UniqueId),
                        out ElementHistoryEvent candidate) &&
                    candidate != null &&
                    (historyEvent == null || candidate.Ts > historyEvent.Ts))
                {
                    historyEvent = candidate;
                }
            }

            return historyEvent != null;
        }

        private static void LoadHistory(
            string directory,
            IReadOnlyCollection<string> modelKeys,
            DateTime? indexedThroughUtc)
        {
            if (string.IsNullOrWhiteSpace(directory) ||
                !Directory.Exists(directory) ||
                modelKeys == null || modelKeys.Count == 0)
            {
                return;
            }

            var accepted = new HashSet<string>(
                modelKeys.Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> files;
            try
            {
                files = Directory
                    .EnumerateFiles(directory, "element-history-*.jsonl*")
                    .Where(path =>
                        !indexedThroughUtc.HasValue ||
                        File.GetLastWriteTimeUtc(path) >=
                        indexedThroughUtc.Value.AddMinutes(-1))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray();
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                foreach (string line in ReadLines(file))
                {
                    try
                    {
                        var historyEvent =
                            JsonConvert.DeserializeObject<ElementHistoryEvent>(line);
                        if (historyEvent == null ||
                            !accepted.Contains(historyEvent.ModelKey ?? string.Empty) ||
                            !ElementHistoryTracker.IsDisplayableHistoryEvent(
                                historyEvent))
                        {
                            continue;
                        }

                        Observe(historyEvent);
                    }
                    catch
                    {
                        // Une ligne endommagée ne bloque pas le reste de l'index.
                    }
                }
            }
        }

        private static IEnumerable<string> ReadLines(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                yield break;

            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using (var stream = File.OpenRead(path))
                using (var gzip = new GZipStream(
                           stream,
                           CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        yield return line;
                }

                yield break;
            }

            foreach (string line in File.ReadLines(path, Encoding.UTF8))
                yield return line;
        }

        private static string BuildKey(string modelKey, string uniqueId)
        {
            return (modelKey ?? string.Empty).Trim() +
                   "|latest-history|" +
                   (uniqueId ?? string.Empty).Trim();
        }

        private static PersistedIndex LoadPersistedIndex(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<PersistedIndex>(
                    File.ReadAllText(path, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static void SavePersistedIndex(
            string path,
            IReadOnlyCollection<string> modelKeys)
        {
            try
            {
                var accepted = new HashSet<string>(
                    modelKeys.Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                List<PersistedEvent> events = LatestByElement.Values
                    .Where(historyEvent =>
                        historyEvent != null &&
                        accepted.Contains(
                            historyEvent.ModelKey ?? string.Empty))
                    .GroupBy(
                        historyEvent => historyEvent.UniqueId ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(item => item.Ts)
                        .First())
                    .Select(PersistedEvent.FromHistoryEvent)
                    .ToList();

                string directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory)) return;
                Directory.CreateDirectory(directory);
                string temporaryPath = path + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonConvert.SerializeObject(
                        new PersistedIndex
                        {
                            SavedUtc = DateTime.UtcNow,
                            Events = events
                        },
                        Formatting.None),
                    Encoding.UTF8);
                File.Copy(temporaryPath, path, true);
                File.Delete(temporaryPath);
            }
            catch
            {
                // Le cache est une optimisation ; les journaux restent la source.
            }
        }

        private static string GetCachePath(string modelKey)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(modelKey ?? string.Empty));
            }

            string fileName = string.Concat(
                hash.Take(16).Select(value =>
                    value.ToString("x2", CultureInfo.InvariantCulture))) +
                ".json";
            return Path.Combine(IndexCacheRoot, fileName);
        }
    }
}

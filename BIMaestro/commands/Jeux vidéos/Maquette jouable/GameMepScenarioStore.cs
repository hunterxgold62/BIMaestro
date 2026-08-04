using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BIMaestro.VideoGames
{
    internal sealed class GameMepScenarioRestoreResult
    {
        public int RestoredSources { get; set; }
        public int RestoredValves { get; set; }
        public int RestoredDirectionConstraints { get; set; }
        public int SkippedEntries { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    internal sealed class GameMepScenarioSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public string ModelKeyHash { get; set; } = string.Empty;
        public string DocumentTitle { get; set; } = string.Empty;
        public DateTime SavedUtc { get; set; }
        public IList<GameMepScenarioValveState> Valves { get; set; } =
            new List<GameMepScenarioValveState>();
        public IList<GameMepScenarioSourceState> Sources { get; set; } =
            new List<GameMepScenarioSourceState>();
        public IList<GameMepScenarioDirectionConstraintState> DirectionConstraints
            { get; set; } = new List<GameMepScenarioDirectionConstraintState>();

        [JsonIgnore]
        public string ModelKey { get; set; } = string.Empty;

        [JsonIgnore]
        public bool CanPersist { get; set; }

        [JsonIgnore]
        public long Revision { get; set; }

        [JsonIgnore]
        public bool HasUserState =>
            Valves.Count > 0 || Sources.Count > 0 || DirectionConstraints.Count > 0;
    }

    internal sealed class GameMepScenarioValveState
    {
        public string ElementPersistentId { get; set; } = string.Empty;
        public bool IsEnabledAsValve { get; set; }
        public bool IsClosed { get; set; }
    }

    internal sealed class GameMepScenarioSourceState
    {
        public string ElementPersistentId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool IsUserCreated { get; set; }
        public GameMepBoundaryKind BoundaryKind { get; set; } =
            GameMepBoundaryKind.Inlet;
        public string EntryConnectorPersistentKey { get; set; } = string.Empty;
        public string ExitConnectorPersistentKey { get; set; } = string.Empty;
    }

    internal sealed class GameMepScenarioDirectionConstraintState
    {
        public string ElementPersistentId { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string EntryConnectorPersistentKey { get; set; } = string.Empty;
        public string ExitConnectorPersistentKey { get; set; } = string.Empty;
    }

    internal static class GameMepScenarioStore
    {
        private const int CurrentSchemaVersion = 2;
        private static readonly object StorageLock = new object();
        private static readonly Dictionary<string, GameMepScenarioSnapshot>
            SessionScenarios =
                new Dictionary<string, GameMepScenarioSnapshot>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> LatestRevisionByModel =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static long _nextRevision;

        public static GameMepScenarioRestoreResult Restore(
            GameMepGraphData graph,
            string? storageDirectoryOverride = null)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var result = new GameMepScenarioRestoreResult();
            graph.RestoredSourceCount = 0;
            graph.RestoredValveCount = 0;
            graph.RestoredDirectionConstraintCount = 0;
            graph.SkippedScenarioEntryCount = 0;
            graph.ScenarioPersistenceError = string.Empty;

            if (string.IsNullOrWhiteSpace(graph.ScenarioModelKey))
                return result;

            try
            {
                GameMepScenarioSnapshot? snapshot = graph.ScenarioCanPersist
                    ? LoadFromDisk(graph, storageDirectoryOverride)
                    : LoadFromSession(graph.ScenarioModelKey);
                if (snapshot == null)
                    return result;

                ApplySnapshot(graph, snapshot, result);
                graph.RestoredSourceCount = result.RestoredSources;
                graph.RestoredValveCount = result.RestoredValves;
                graph.RestoredDirectionConstraintCount =
                    result.RestoredDirectionConstraints;
                graph.SkippedScenarioEntryCount = result.SkippedEntries;
                return result;
            }
            catch (Exception exception)
            {
                result.Error = exception.Message;
                graph.ScenarioPersistenceError = exception.Message;
                GameRuntimeDiagnostics.Write(
                    "Restauration du scénario MEP ignorée",
                    exception);
                return result;
            }
        }

        public static void QueueSave(GameMepGraphData graph)
        {
            if (graph == null || string.IsNullOrWhiteSpace(graph.ScenarioModelKey))
                return;

            GameMepScenarioSnapshot snapshot;
            try
            {
                snapshot = Capture(graph);
                RegisterLatestRevision(snapshot);
                graph.ScenarioPersistenceError = string.Empty;
                if (!snapshot.CanPersist)
                {
                    SaveSessionSnapshot(snapshot);
                    return;
                }
            }
            catch (Exception exception)
            {
                graph.ScenarioPersistenceError = exception.Message;
                GameRuntimeDiagnostics.Write(
                    "Préparation de la sauvegarde MEP impossible",
                    exception);
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    WriteIfLatest(snapshot, null);
                }
                catch (Exception exception)
                {
                    GameRuntimeDiagnostics.Write(
                        "Sauvegarde asynchrone du scénario MEP impossible",
                        exception);
                }
            });
        }

        public static bool SaveNow(
            GameMepGraphData graph,
            string? storageDirectoryOverride = null)
        {
            if (graph == null || string.IsNullOrWhiteSpace(graph.ScenarioModelKey))
                return false;

            try
            {
                GameMepScenarioSnapshot snapshot = Capture(graph);
                RegisterLatestRevision(snapshot);
                if (snapshot.CanPersist)
                    WriteIfLatest(snapshot, storageDirectoryOverride);
                else
                    SaveSessionSnapshot(snapshot);
                graph.ScenarioPersistenceError = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                graph.ScenarioPersistenceError = exception.Message;
                GameRuntimeDiagnostics.Write(
                    "Sauvegarde immédiate du scénario MEP impossible",
                    exception);
                return false;
            }
        }

        internal static GameMepScenarioSnapshot Capture(GameMepGraphData graph)
        {
            var snapshot = new GameMepScenarioSnapshot
            {
                SchemaVersion = CurrentSchemaVersion,
                ModelKey = graph.ScenarioModelKey ?? string.Empty,
                ModelKeyHash = ComputeModelKeyHash(graph.ScenarioModelKey),
                DocumentTitle = graph.DocumentTitle ?? string.Empty,
                SavedUtc = DateTime.UtcNow,
                CanPersist = graph.ScenarioCanPersist,
                Revision = Interlocked.Increment(ref _nextRevision)
            };

            var elementsByKey = graph.Elements
                .Where(element => !string.IsNullOrWhiteSpace(element.Key))
                .GroupBy(element => element.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (GameMepValveData valve in graph.Valves)
            {
                bool differsFromInitial =
                    valve.IsEnabledAsValve != valve.InitiallyEnabledAsValve;
                if (!valve.WasManuallyOverridden &&
                    !differsFromInitial &&
                    !valve.IsClosed)
                {
                    continue;
                }

                if (!elementsByKey.TryGetValue(
                        valve.ElementKey,
                        out GameMepElementData element) ||
                    string.IsNullOrWhiteSpace(element.PersistentId))
                {
                    continue;
                }

                snapshot.Valves.Add(new GameMepScenarioValveState
                {
                    ElementPersistentId = element.PersistentId,
                    IsEnabledAsValve = valve.IsEnabledAsValve,
                    IsClosed = valve.IsEnabledAsValve && valve.IsClosed
                });
            }

            foreach (GameMepSourceData source in graph.Sources)
            {
                bool differsFromInitial = source.IsActive != source.InitiallyActive;
                if (!source.WasManuallyOverridden &&
                    !source.IsUserCreated &&
                    !differsFromInitial &&
                    !source.HasExplicitDirection)
                {
                    continue;
                }

                if (!elementsByKey.TryGetValue(
                        source.ElementKey,
                        out GameMepElementData element) ||
                    string.IsNullOrWhiteSpace(element.PersistentId))
                {
                    continue;
                }

                snapshot.Sources.Add(new GameMepScenarioSourceState
                {
                    ElementPersistentId = element.PersistentId,
                    Name = source.Name ?? string.Empty,
                    IsActive = source.IsActive,
                    IsUserCreated = source.IsUserCreated,
                    BoundaryKind = source.BoundaryKind,
                    EntryConnectorPersistentKey = GetConnectorPersistentKey(
                        graph,
                        source.EntryConnectorIndex),
                    ExitConnectorPersistentKey = GetConnectorPersistentKey(
                        graph,
                        source.ExitConnectorIndex)
                });
            }

            foreach (GameMepDirectionConstraintData constraint in
                graph.DirectionConstraints)
            {
                if (!constraint.WasManuallyOverridden ||
                    !constraint.HasExplicitDirection ||
                    !elementsByKey.TryGetValue(
                        constraint.ElementKey,
                        out GameMepElementData element) ||
                    string.IsNullOrWhiteSpace(element.PersistentId))
                {
                    continue;
                }
                snapshot.DirectionConstraints.Add(
                    new GameMepScenarioDirectionConstraintState
                    {
                        ElementPersistentId = element.PersistentId,
                        IsActive = constraint.IsActive,
                        EntryConnectorPersistentKey = GetConnectorPersistentKey(
                            graph,
                            constraint.EntryConnectorIndex),
                        ExitConnectorPersistentKey = GetConnectorPersistentKey(
                            graph,
                            constraint.ExitConnectorIndex)
                    });
            }

            return snapshot;
        }

        private static void ApplySnapshot(
            GameMepGraphData graph,
            GameMepScenarioSnapshot snapshot,
            GameMepScenarioRestoreResult result)
        {
            if (snapshot.SchemaVersion < 1 ||
                snapshot.SchemaVersion > CurrentSchemaVersion)
                throw new InvalidDataException(
                    "Version de scénario MEP non prise en charge : " +
                    snapshot.SchemaVersion.ToString(CultureInfo.InvariantCulture));

            string expectedHash = ComputeModelKeyHash(graph.ScenarioModelKey);
            if (!string.Equals(
                    snapshot.ModelKeyHash,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Le scénario MEP appartient à une autre maquette.");
            }

            var elementsByPersistentId = graph.Elements
                .Where(element => !string.IsNullOrWhiteSpace(element.PersistentId))
                .GroupBy(element => element.PersistentId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (GameMepScenarioValveState state in snapshot.Valves ??
                new List<GameMepScenarioValveState>())
            {
                if (!elementsByPersistentId.TryGetValue(
                        state.ElementPersistentId ?? string.Empty,
                        out GameMepElementData element))
                {
                    result.SkippedEntries++;
                    continue;
                }

                GameMepValveData? valve = graph.FindValve(element.Key);
                if (valve == null)
                {
                    result.SkippedEntries++;
                    continue;
                }

                valve.IsEnabledAsValve = state.IsEnabledAsValve;
                valve.IsClosed = state.IsEnabledAsValve && state.IsClosed;
                valve.WasManuallyOverridden = true;
                result.RestoredValves++;
            }

            foreach (GameMepScenarioSourceState state in snapshot.Sources ??
                new List<GameMepScenarioSourceState>())
            {
                if (!elementsByPersistentId.TryGetValue(
                        state.ElementPersistentId ?? string.Empty,
                        out GameMepElementData element))
                {
                    result.SkippedEntries++;
                    continue;
                }

                bool hasStoredDirection =
                    !string.IsNullOrWhiteSpace(state.EntryConnectorPersistentKey) ||
                    !string.IsNullOrWhiteSpace(state.ExitConnectorPersistentKey);
                int entryIndex = FindConnectorIndex(
                    graph,
                    element.Key,
                    state.EntryConnectorPersistentKey);
                int exitIndex = FindConnectorIndex(
                    graph,
                    element.Key,
                    state.ExitConnectorPersistentKey);
                if (hasStoredDirection &&
                    (entryIndex < 0 || exitIndex < 0 || entryIndex == exitIndex))
                {
                    // Ne jamais transformer silencieusement une arrivée dirigée
                    // en source bidirectionnelle après une modification du réseau.
                    result.SkippedEntries++;
                    continue;
                }

                GameMepSourceData? source = graph.Sources.FirstOrDefault(candidate =>
                    string.Equals(candidate.ElementKey, element.Key, StringComparison.Ordinal) &&
                    candidate.BoundaryKind == state.BoundaryKind);
                if (source == null)
                {
                    source = new GameMepSourceData
                    {
                        ElementKey = element.Key,
                        SystemKey = element.SystemKey,
                        Name = string.IsNullOrWhiteSpace(state.Name)
                            ? element.Name
                            : state.Name,
                        Confidence = hasStoredDirection
                            ? GameMepConfidence.High
                            : GameMepConfidence.Low,
                        InitiallyActive = false,
                        IsUserCreated = state.IsUserCreated,
                        BoundaryKind = state.BoundaryKind
                    };
                    graph.Sources.Add(source);
                }

                source.IsActive = state.IsActive;
                source.IsUserCreated = state.IsUserCreated;
                source.BoundaryKind = state.BoundaryKind;
                source.WasManuallyOverridden = true;
                source.EntryConnectorIndex = hasStoredDirection ? entryIndex : -1;
                source.ExitConnectorIndex = hasStoredDirection ? exitIndex : -1;
                result.RestoredSources++;
            }

            foreach (GameMepScenarioDirectionConstraintState state in
                snapshot.DirectionConstraints ??
                new List<GameMepScenarioDirectionConstraintState>())
            {
                if (!elementsByPersistentId.TryGetValue(
                        state.ElementPersistentId ?? string.Empty,
                        out GameMepElementData element))
                {
                    result.SkippedEntries++;
                    continue;
                }
                int entryIndex = FindConnectorIndex(
                    graph,
                    element.Key,
                    state.EntryConnectorPersistentKey);
                int exitIndex = FindConnectorIndex(
                    graph,
                    element.Key,
                    state.ExitConnectorPersistentKey);
                if (entryIndex < 0 || exitIndex < 0 || entryIndex == exitIndex)
                {
                    result.SkippedEntries++;
                    continue;
                }
                graph.DirectionConstraints.Add(
                    new GameMepDirectionConstraintData
                    {
                        ElementKey = element.Key,
                        EntryConnectorIndex = entryIndex,
                        ExitConnectorIndex = exitIndex,
                        IsActive = state.IsActive,
                        WasManuallyOverridden = true
                    });
                result.RestoredDirectionConstraints++;
            }
        }

        private static GameMepScenarioSnapshot? LoadFromDisk(
            GameMepGraphData graph,
            string? storageDirectoryOverride)
        {
            string path = GetScenarioFilePath(graph, storageDirectoryOverride);
            if (!File.Exists(path))
                return null;

            try
            {
                return Deserialize(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception primaryException)
            {
                string backupPath = path + ".bak";
                if (File.Exists(backupPath))
                {
                    try
                    {
                        GameRuntimeDiagnostics.Write(
                            "Scénario MEP principal illisible, utilisation de la sauvegarde",
                            primaryException);
                        return Deserialize(File.ReadAllText(backupPath, Encoding.UTF8));
                    }
                    catch
                    {
                    }
                }
                throw;
            }
        }

        private static GameMepScenarioSnapshot? LoadFromSession(string modelKey)
        {
            lock (StorageLock)
            {
                return SessionScenarios.TryGetValue(
                    modelKey,
                    out GameMepScenarioSnapshot snapshot)
                        ? snapshot
                        : null;
            }
        }

        private static void SaveSessionSnapshot(GameMepScenarioSnapshot snapshot)
        {
            lock (StorageLock)
            {
                if (snapshot.HasUserState)
                    SessionScenarios[snapshot.ModelKey] = snapshot;
                else
                    SessionScenarios.Remove(snapshot.ModelKey);
            }
        }

        private static void RegisterLatestRevision(GameMepScenarioSnapshot snapshot)
        {
            lock (StorageLock)
                LatestRevisionByModel[snapshot.ModelKey] = snapshot.Revision;
        }

        private static void WriteIfLatest(
            GameMepScenarioSnapshot snapshot,
            string? storageDirectoryOverride)
        {
            lock (StorageLock)
            {
                if (LatestRevisionByModel.TryGetValue(
                        snapshot.ModelKey,
                        out long latestRevision) &&
                    snapshot.Revision < latestRevision)
                {
                    return;
                }

                WriteSnapshot(snapshot, storageDirectoryOverride);
            }
        }

        private static void WriteSnapshot(
            GameMepScenarioSnapshot snapshot,
            string? storageDirectoryOverride)
        {
            string directory = ResolveStorageDirectory(storageDirectoryOverride);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, snapshot.ModelKeyHash + ".json");
            if (!snapshot.HasUserState)
            {
                TryDelete(path);
                TryDelete(path + ".bak");
                return;
            }

            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupPath = path + ".bak";
            try
            {
                string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    TryDelete(backupPath);
                    File.Replace(temporaryPath, path, backupPath, true);
                }
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        internal static string GetScenarioFilePath(
            GameMepGraphData graph,
            string? storageDirectoryOverride = null)
        {
            return Path.Combine(
                ResolveStorageDirectory(storageDirectoryOverride),
                ComputeModelKeyHash(graph.ScenarioModelKey) + ".json");
        }

        private static string ResolveStorageDirectory(string? storageDirectoryOverride)
        {
            if (!string.IsNullOrWhiteSpace(storageDirectoryOverride))
                return storageDirectoryOverride;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMaestro",
                "MaquetteJouable",
                "FluidesMEP");
        }

        private static GameMepScenarioSnapshot Deserialize(string json)
        {
            GameMepScenarioSnapshot? snapshot =
                JsonConvert.DeserializeObject<GameMepScenarioSnapshot>(json);
            return snapshot ?? throw new InvalidDataException(
                "Le fichier de scénario MEP est vide ou invalide.");
        }

        private static string GetConnectorPersistentKey(
            GameMepGraphData graph,
            int connectorIndex)
        {
            return connectorIndex >= 0 && connectorIndex < graph.Connectors.Count
                ? graph.Connectors[connectorIndex].PersistentKey ?? string.Empty
                : string.Empty;
        }

        private static int FindConnectorIndex(
            GameMepGraphData graph,
            string elementKey,
            string persistentKey)
        {
            if (string.IsNullOrWhiteSpace(persistentKey))
                return -1;
            foreach (GameMepConnectorData connector in graph.Connectors)
            {
                if (string.Equals(
                        connector.ElementKey,
                        elementKey,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        connector.PersistentKey,
                        persistentKey,
                        StringComparison.Ordinal))
                {
                    return connector.Index;
                }
            }
            return -1;
        }

        private static string ComputeModelKeyHash(string modelKey)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(
                    Encoding.UTF8.GetBytes(modelKey ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes)
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.ApplicationServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Analyse
{
    // Revit work stays in its API callback. Journals, outbox, compression and
    // publication run on the IO worker. A bridge is never a historical payload.
    internal static partial class ElementHistoryDurableBridge
    {
        internal sealed class Status
        {
            public int PendingJobs { get; set; }
            public int PendingModels { get; set; }
            public int ConvertedEvents { get; set; }
            public int FailedJobs { get; set; }
            public List<string> Failures { get; set; }
            public List<string> BridgeFiles { get; set; }
            public List<string> DeletedBridgeFiles { get; set; }
            public bool Complete { get; set; }
            public bool SessionClosed { get; set; }
            public bool Working { get; set; }
            public string ProgressLabel { get; set; }
            public int ProgressCompleted { get; set; }
            public int ProgressTotal { get; set; }
            public double ProgressElapsedSeconds { get; set; }
            public double PreparationElapsedSeconds { get; set; }
        }
        private sealed class Session
        {
            public int Version { get; set; } = 1;
            public string Id { get; set; }
            public string BridgeFile { get; set; }
            public string ModelKey { get; set; }
            public int ProcessId { get; set; }
            public DateTime ProcessStartedUtc { get; set; }
            public bool Closed { get; set; }
            public bool Deleted { get; set; }
            public bool Referenced { get; set; }
            [JsonIgnore] internal Task CopyTask;
        }
        private sealed class Prepared
        {
            internal ElementHistoryEvent Event;
            internal HistoryRestoreRequest Request;
            internal string Root;
        }
        internal sealed class DocumentReferenceComparer : IEqualityComparer<Document>
        {
            internal static readonly DocumentReferenceComparer Instance = new DocumentReferenceComparer();
            public bool Equals(Document x, Document y) => ReferenceEquals(x, y);
            public int GetHashCode(Document doc) => RuntimeHelpers.GetHashCode(doc);
        }
        private static readonly object Sync = new object();
        private static readonly Dictionary<Document, Session> Live = new Dictionary<Document, Session>(DocumentReferenceComparer.Instance);
        private static readonly Dictionary<string, Session> Sessions = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ElementHistoryEvent> Pending = new Dictionary<string, ElementHistoryEvent>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Errors = new Dictionary<string, string>(StringComparer.Ordinal);
        private sealed class TransportRetry
        {
            internal int Failures;
            internal DateTime NextUtc;
        }
        private static readonly Dictionary<string, TransportRetry> TransportRetries = new Dictionary<string, TransportRetry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Prepared> PreparedJobs = new Dictionary<string, Prepared>(StringComparer.Ordinal);
        private static readonly Dictionary<string, HistoryNativeArtifact> OpeningBatchProofs = new Dictionary<string, HistoryNativeArtifact>(StringComparer.Ordinal);
        internal static HistoryRestoreRequest TryGetOpeningNativeRequest(ElementHistoryEvent ev)
        {
            if (ev?.Action != "delete") return null;
            var original = ToRequest(ev, false);
            var artifact = SourceArtifact(original.Recipe);
            if (artifact?.WholeModel != true)
            {
                var key = (ev.ModelKey ?? "") + "|" + ev.DeletionAudit?.BatchId;
                lock (Sync) if (!OpeningBatchProofs.TryGetValue(key, out artifact)) return null;
                // An unrooted opening receipt freezes the complete changed-ID set
                // for this deletion batch. Never use another batch or model.
                if (artifact.SourceRootElementId != 0) return null;
            }
            if (!File.Exists(artifact.File) || artifact.ExcludedElementIds?.Contains(ev.ElementId) == true) return null;
            var copy = JObject.FromObject(artifact).ToObject<HistoryNativeArtifact>();
            copy.SourceElementId = ev.ElementId;
            copy.MemberSourceUniqueId = string.IsNullOrWhiteSpace(original.SourceUniqueId) ? null : original.SourceUniqueId;
            original.Recipe = new HistoryRecipe { Kind = "native_archive", Native = copy };
            original.CaptureFailure = null;
            return original;
        }
        private static ElementHistoryNativeArchive.Sources Sources;
        private static string OpenBridgeFile;
        private static Task Io;
        private static bool Loaded;
        private static int Converted;
        private static DateTime NextPassUtc;
        private static DateTime NextClosedCleanupUtc;
        private static void ScheduleClosedCleanup()
        {
            if (DateTime.UtcNow < NextClosedCleanupUtc) return;
            NextClosedCleanupUtc = DateTime.UtcNow.AddMinutes(1);
            Io = Task.Run(() => { try { CleanupClosedSessions(); } catch (Exception ex) { RecordFailure("cleanup", ex); } });
        }
        private static string CurrentStore;
        private static bool IsolatedAutomaticPreparation;
        private static bool ManualPreparation;
        private static readonly HashSet<string> AutonomousMigrationAttempted = new HashSet<string>(StringComparer.Ordinal);
        private static DateTime ManualRequestedUtc;
        private static string PriorityModel;
        // Null is an explicit model-wide request; an empty set must stay empty.
        private static HashSet<string> PreparationEventIds;
        private static long PreparationGeneration;
        private static string PreparationLabel;
        private static int PreparationCompleted, PreparationTotal;
        private static readonly Stopwatch PreparationClock = new Stopwatch();
        private static readonly Stopwatch PreparationStepClock = new Stopwatch();
        private static void SetPreparationProgress(string label, int completed, int total, long? expectedGeneration = null)
        {
            lock (Sync)
            {
                if (!ManualPreparation || expectedGeneration.HasValue && expectedGeneration.Value != PreparationGeneration) return;
                if (!string.Equals(PreparationLabel, label, StringComparison.Ordinal)) PreparationStepClock.Restart();
                PreparationLabel = label; PreparationCompleted = completed; PreparationTotal = total;
            }
            SignalReadiness();
        }
        // Call while holding Sync; use event IDs rather than mutable UI objects.
        private static bool InPreparationScope(ElementHistoryEvent ev)
            => ev != null && (PriorityModel == null || string.Equals(ev.ModelKey, PriorityModel, StringComparison.OrdinalIgnoreCase))
                && (PreparationEventIds == null || PreparationEventIds.Contains(EventId(ev)));
        private static void FinishPreparationLocked()
        {
            ManualPreparation = false;
            PriorityModel = null; PreparationEventIds = null;
            PreparationLabel = null; PreparationCompleted = PreparationTotal = 0;
            PreparationClock.Stop(); PreparationStepClock.Stop();
        }
        internal static bool IsManualPreparing { get { lock (Sync) return ManualPreparation; } }
        internal static void RequestPreparation(string modelKey = null, IEnumerable<ElementHistoryEvent> selection = null)
        {
            var compatibilityIssue = ElementHistoryCompatibility.GetLoadedApiIssue();
            if (compatibilityIssue != null) throw new InvalidOperationException(compatibilityIssue);
            var selectedEvents = selection?.Where(ev => ev?.Action == "delete").ToList();
            var selectedIds = selectedEvents == null ? null : new HashSet<string>(selectedEvents.Select(EventId), StringComparer.Ordinal);
            List<string> retrying;
            lock (Sync)
            {
                ManualPreparation = true;
                PreparationGeneration++;
                ManualRequestedUtc = DateTime.UtcNow;
                PreparationClock.Restart(); PreparationStepClock.Restart();
                PreparationLabel = "Préparation demandée…"; PreparationCompleted = 0; PreparationTotal = 0;
                PriorityModel = modelKey; PreparationEventIds = selectedIds;
                NextPassUtc = DateTime.MinValue;
                retrying = Pending.Where(p => InPreparationScope(p.Value)
                    && (!Errors.ContainsKey(p.Key) || TransportRetries.ContainsKey(p.Key))).Select(p => p.Key).ToList();
                foreach (var id in retrying) { Errors.Remove(id); TransportRetries.Remove(id); }
            }
            foreach (var id in retrying) SetReadiness(id, ReadinessState.Preparing, "Préparation demandée par l’utilisateur…");
            QueueAutonomousPreparation(modelKey, selectedIds);
        }
        // Explicit preparation also upgrades older, already-loaded native records to
        // independent JSON. Journal IO never runs in the Revit API callback.
        private static void QueueAutonomousPreparation(string modelKey, HashSet<string> selectedIds)
        {
            lock (Sync)
            {
                var preceding = Io; var generation = PreparationGeneration;
                Io = Task.Run(async () =>
                {
                    try
                    {
                        if (preceding != null) await preceding.ConfigureAwait(false);
                        foreach (var ev in ReadJournal().Where(ev => ev.Action == "delete"
                            && (modelKey == null || string.Equals(ev.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase))
                            && (selectedIds == null || selectedIds.Contains(EventId(ev)))))
                        {
                            var id = EventId(ev);
                            try
                            {
                            lock (Sync) if (generation != PreparationGeneration) return;
                            if (ElementHistoryDurableStore.ResolveAutonomousConversion(id) != null) continue;
                            var captured = HasCapturedAutonomousRecipe(ev);
                            var prepared = captured ? null : ElementHistoryDurableStore.ResolveConversion(id);
                            lock (Sync) if (!captured && AutonomousMigrationAttempted.Contains(id)) continue;
                            if (!captured && !IsReadyNativeRequest(prepared)) continue;
                            ElementHistoryDurableStore.WriteJob(ev);
                            lock (Sync)
                            {
                                Pending[id] = ev;
                                if (captured) { Errors.Remove(id); TransportRetries.Remove(id); }
                            }
                            SetReadiness(id, ReadinessState.Preparing, "Centralisation des données dans le JSON…");
                            }
                            catch (Exception ex) { RecordFailure(id, ex); }
                        }
                    }
                    catch (Exception ex) { RecordFailure("autonomous-preparation", ex); }
                });
            }
        }

        internal static void CancelPreparation()
        {
            lock (Sync) { FinishPreparationLocked(); PreparationGeneration++; }
            SignalReadiness();
        }
        // Revit API context only: IO publication uses DTOs and never this source.
        internal static void ReleasePreparationSourcesForRestore()
        { CancelPreparation(); ResetNativePreparation(); }
        internal static void EnableIsolatedAutomaticPreparation()
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") != "1")
                throw new InvalidOperationException("Cette option est réservée au processus de test isolé.");
            IsolatedAutomaticPreparation = true;
        }
        private static string SessionDirectory => Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "history-data", "sessions");
        internal static string LocalBridgeDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "HistoryBridge");

        internal static string NewBridgePath() => Path.Combine(LocalBridgeDirectory, "snapshot-opening-" + Guid.NewGuid().ToString("N") + ".rvt");
        internal static void RegisterSession(Document doc, string path, Task copyTask)
        {
            var session = new Session { Id = Path.GetFileNameWithoutExtension(path).Substring("snapshot-opening-".Length),
                BridgeFile = Path.GetFullPath(path), ProcessId = Process.GetCurrentProcess().Id,
                ProcessStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(), CopyTask = copyTask,
                ModelKey = ElementHistoryTracker.GetDocumentKeyForHistory(doc) };
            lock (Sync) { Live[doc] = session; Sessions[session.Id] = session; }
            // Durable ownership is needed to recover after an abnormal shutdown.
            Task.Run(() => { try { SaveSession(session); } catch (Exception ex) { RecordFailure("session:" + session.Id, ex); } });
        }
        internal static string GetSessionId(Document doc)
        {
            lock (Sync)
            {
                if (Live.TryGetValue(doc, out var session)) return session.Id;
                return Live.FirstOrDefault(p => p.Key.IsValidObject && doc.IsValidObject && p.Key.Equals(doc)).Value?.Id;
            }
        }
        internal static void CloseSession(string sessionId)
        {
            Session session;
            lock (Sync)
            {
                if (sessionId == null || !Sessions.TryGetValue(sessionId, out session)) return;
                foreach (var doc in Live.Where(p => p.Value.Id == sessionId).Select(p => p.Key).ToList()) Live.Remove(doc);
            }
            Task.Run(async () =>
            {
                try
                {
                    if (session.CopyTask != null) await session.CopyTask.ConfigureAwait(false);
                    // Flush has no Revit calls and propagates failures. A failed
                    // append/outbox prevents Closed and therefore prevents cleanup.
                    ElementHistoryTracker.FlushPendingForDurability();
                    lock (Sync) session.Closed = true;
                    SaveSession(session);
                    // Do not wait for another idle API pass to remove an unused
                    // closed copy. Readers and copiers retain exclusive leases.
                    CleanupClosedSessions();
                }
                catch (Exception ex) { RecordFailure("session:" + session.Id, ex); }
            });
        }
        private static void SaveSession(Session session)
        {
            lock (session)
            {
                Directory.CreateDirectory(SessionDirectory);
                var path = Path.Combine(SessionDirectory, session.Id + ".json");
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(session));
                        output.Write(bytes, 0, bytes.Length); output.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        internal static string EventId(ElementHistoryEvent ev)
        {
            if (!string.IsNullOrWhiteSpace(ev.EventId)) return ev.EventId;
            // Stable identity for already persisted JSONL and GZ records.
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new object[] { ev.Ts.ToUniversalTime().Ticks,
                ev.ModelKey, ev.ModelGuid, ev.UniqueId, ev.ElementId, ev.Action, ev.Tx, ev.DeletionAudit?.BatchId }));
            using (var hash = SHA256.Create()) return "legacy-" + BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        // Autonomous semantic JSON remains usable independently of all RVT/blob files.
        internal static HistoryRestoreRequest ResolvePreparedRequest(string eventId)
            => ElementHistoryDurableStore.ResolveAutonomousConversion(eventId)
                ?? ElementHistoryDurableStore.ResolveConversion(eventId);

        internal static HistoryRestoreRequest ToRequest(ElementHistoryEvent ev, bool resolve = true)
        {
            if (resolve)
            {
                var converted = ResolvePreparedRequest(EventId(ev));
                if (converted != null) return converted;
            }
            object source = null, raw = null, failure = null, parent = null, familyType = null;
            ev.Delta?.TryGetValue("deletedUniqueId", out source); ev.Delta?.TryGetValue("recipe", out raw);
            ev.Delta?.TryGetValue("captureFailure", out failure); ev.Delta?.TryGetValue("superComponentUniqueId", out parent);
            ev.Delta?.TryGetValue("familyTypeUniqueId", out familyType);
            HistoryRecipe recipe = null;
            if (raw != null && !(raw is JToken absent && absent.Type == JTokenType.Null))
            {
                var strict = JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error,
                    TypeNameHandling = TypeNameHandling.None, MaxDepth = 64 });
                recipe = raw is HistoryRecipe typed ? typed : JToken.FromObject(raw).ToObject<HistoryRecipe>(strict);
                if (recipe == null) throw new InvalidDataException("Le contenu de la recette historique n’est pas un objet valide.");
                foreach (var node in Recipes(recipe))
                    if (ElementHistoryReconstruction.ReadRecipe(node) == null)
                        throw new InvalidDataException("Recette historique inconnue ou invalide ; la passerelle reste conservée.");
            }
            return new HistoryRestoreRequest { SourceUniqueId = Convert.ToString(source),
                Label = (ev.Family + " " + ev.TypeName).Trim() + " [" + ev.ElementId + "]", Category = ev.Category,
                Recipe = recipe, CaptureFailure = Convert.ToString(failure),
                SuperComponentUniqueId = Convert.ToString(parent), FamilyTypeUniqueId = Convert.ToString(familyType), DeletionAudit = ev.DeletionAudit };
        }
        internal static void ApplyConversion(ElementHistoryEvent ev)
        {
            if (ev?.Action != "delete") return;
            var converted = ResolvePreparedRequest(EventId(ev));
            if (converted == null) return;
            ev.Delta = ev.Delta == null ? new Dictionary<string, object>() : new Dictionary<string, object>(ev.Delta);
            ev.Delta["recipe"] = converted.Recipe; ev.Delta["deletedUniqueId"] = converted.SourceUniqueId;
            ev.Delta["captureFailure"] = converted.CaptureFailure;
            ev.Delta["superComponentUniqueId"] = converted.SuperComponentUniqueId;
            ev.Delta["familyTypeUniqueId"] = converted.FamilyTypeUniqueId;
            var classification = ElementHistoryDurableStore.ResolveOpeningClassification(EventId(ev));
            if (classification != null && classification.SourceElementId == ev.ElementId)
                ev.Category = classification.Category ?? ev.Category;
        }
        private static bool NeedsOpeningClassification(ElementHistoryEvent ev, HistoryRestoreRequest original, HistoryRestoreRequest converted)
            => !IsTerminalBridgeFailure(converted) && converted?.Recipe == null && original?.Recipe?.Native?.WholeModel == true
                && ElementHistoryDurableStore.ResolveOpeningClassification(EventId(ev)) == null
                && File.Exists(original.Recipe.Native.File);
        private static bool NeedsNativeUpgrade(ElementHistoryEvent ev, HistoryRestoreRequest converted)
        {
            if (IsTerminalBridgeFailure(converted) || IsReadyDirectRequest(converted)) return false;
            if (converted?.Recipe?.Native?.PayloadId != null) return false;
            if (ElementHistoryDurableStore.ResolveOpeningClassification(EventId(ev))?.IsExcluded() == true) return false;
            return TryGetOpeningNativeRequest(ev) != null;
        }
        private static bool NeedsConversion(HistoryRecipe recipe) => recipe?.Native != null || recipe?.OpeningFallback != null
            || (recipe?.ElectricalCircuits?.Any(c => NeedsConversion(c.Recipe)) ?? false);
        private static IEnumerable<HistoryRecipe> Recipes(HistoryRecipe recipe)
        {
            if (recipe == null) yield break;
            yield return recipe;
            foreach (var circuit in recipe.ElectricalCircuits ?? new List<HistoryCircuit>())
                foreach (var child in Recipes(circuit.Recipe)) yield return child;
        }
        private static IEnumerable<HistoryNativeArtifact> Artifacts(HistoryRecipe recipe) => Recipes(recipe)
            .SelectMany(r => new[] { r.Native, r.OpeningFallback }).Where(a => a != null);
        private static HistoryNativeArtifact SourceArtifact(HistoryRecipe recipe) => Artifacts(recipe).FirstOrDefault(a => a.WholeModel)
            ?? Artifacts(recipe).FirstOrDefault();
        internal static void EventsPersisted(IEnumerable<ElementHistoryEvent> events)
        {
            foreach (var ev in events.Where(e => e.Action == "delete"))
            {
                try
                {
                var request = ToRequest(ev, resolve: false);
                // New deletions must not disappear from the outbox simply
                // because capture failed or a direct recipe lacks frozen types.
                bool currentRecord = !string.IsNullOrWhiteSpace(ev.EventId) && !ev.EventId.StartsWith("legacy-", StringComparison.Ordinal);
                if (!NeedsConversion(request.Recipe) && !currentRecord) continue;
                ev.EventId = EventId(ev);
                var receipt = SourceArtifact(request.Recipe);
                if (receipt?.WholeModel == true && receipt.SourceRootElementId == 0 && !string.IsNullOrWhiteSpace(ev.DeletionAudit?.BatchId))
                    lock (Sync) OpeningBatchProofs[(ev.ModelKey ?? "") + "|" + ev.DeletionAudit.BatchId] = receipt;
                Session used = null;
                bool referencedChanged = false;
                if (receipt?.WholeModel == true)
                    lock (Sync)
                    {
                        used = Sessions.Values.FirstOrDefault(s => string.Equals(Path.GetFileName(s.BridgeFile), Path.GetFileName(receipt.File), StringComparison.OrdinalIgnoreCase));
                        if (used != null && !used.Referenced) { used.Referenced = true; referencedChanged = true; }
                    }
                if (referencedChanged) SaveSession(used);
                var conversion = ResolvePreparedRequest(ev.EventId);
                if (conversion != null && !NeedsOpeningClassification(ev, request, conversion) && !NeedsNativeUpgrade(ev, conversion)) continue;
                ElementHistoryDurableStore.WriteJob(ev);
                lock (Sync) Pending[ev.EventId] = ev;
                }
                catch (Exception ex) { RecordFailure(EventId(ev), ex); }
            }
        }
        private static IEnumerable<ElementHistoryEvent> ReadJournal()
        {
            var root = CollaborativeModelTrackerStore.ActiveDirectory;
            return ElementHistoryLocalSpool.Default.ReadWithTransportLock(root, () => ReadJournalUnderLease(root));
        }
        private static IEnumerable<ElementHistoryEvent> ReadJournalUnderLease(string root)
        {
            if (!Directory.Exists(root)) yield break;
            foreach (var file in Directory.GetFiles(root, "element-history-*").OrderBy(f => f, StringComparer.Ordinal))
            {
                bool gzip = file.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase);
                if (!gzip && !file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Format de journal historique inconnu : " + file);
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var decompressed = gzip ? new GZipStream(input, CompressionMode.Decompress, true) : null)
                using (var reader = new StreamReader(decompressed ?? (Stream)input, new UTF8Encoding(false, true)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var ev = JsonConvert.DeserializeObject<ElementHistoryEvent>(line);
                        if (ev == null || ev.Ts == default || string.IsNullOrEmpty(ev.Action))
                            throw new InvalidDataException("Événement historique illisible ; nettoyage de la passerelle refusé.");
                        yield return ev;
                    }
                }
            }
        }
        private static void Load()
        {
            // Recover the gap between durable journal append and outbox creation.
            // Gzip is decoded, not mistaken for a completed conversion.
            EventsPersisted(ReadJournal());
            foreach (var ev in ElementHistoryDurableStore.ReadPendingJobs(RecordFailure))
            {
                var id = EventId(ev);
                try
                {
                var converted = ResolvePreparedRequest(id);
                if (converted != null && !NeedsOpeningClassification(ev, ToRequest(ev, false), converted) && !NeedsNativeUpgrade(ev, converted))
                {
                    ElementHistoryDurableStore.DeleteJob(id);
                    lock (Sync) { Pending.Remove(id); Converted++; }
                }
                else lock (Sync) Pending[id] = ev;
                }
                catch (Exception ex) { RecordFailure(id, ex); }
            }
            if (Directory.Exists(SessionDirectory))
                foreach (var file in Directory.GetFiles(SessionDirectory, "*.json"))
                {
                    var session = JsonConvert.DeserializeObject<Session>(File.ReadAllText(file));
                    if (session?.Version != 1 || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.BridgeFile))
                        throw new InvalidDataException("Propriétaire de passerelle inconnu.");
                    if (!session.Closed && IsOwnerGone(session))
                    { session.Closed = true; SaveSession(session); }
                    lock (Sync) if (!Sessions.ContainsKey(session.Id)) Sessions[session.Id] = session;
                }
            // Sessions must also become relevant when recovery rebuilt the jobs
            // before their original Referenced marker reached durable storage.
            foreach (var ev in ReadJournal().Where(e => e.Action == "delete"))
            {
                // All batch proofs are now indexed, including those encountered
                // after an older direct event in the journal's first pass.
                EventsPersisted(new[] { ev });
                foreach (var artifact in Artifacts(ToRequest(ev, false).Recipe).Where(a => a.WholeModel))
                {
                    Session used;
                    lock (Sync) used = Sessions.Values.FirstOrDefault(s => string.Equals(Path.GetFileName(s.BridgeFile), Path.GetFileName(artifact.File), StringComparison.OrdinalIgnoreCase));
                    if (used != null && !used.Referenced) { used.Referenced = true; SaveSession(used); }
                }
            }
            lock (Sync) { Loaded = true; Errors.Remove("load"); }
        }
        private static bool IsOwnerGone(Session session)
        {
            try { using (var process = Process.GetProcessById(session.ProcessId)) return process.StartTime.ToUniversalTime() != session.ProcessStartedUtc; }
            catch (ArgumentException) { return true; }
            catch { return false; }
        }
        private static void RecordFailure(string id, Exception ex)
        {
            var concise = ex.Message.Split(new[] { '\r', '\n' }, 2)[0];
            if (concise.Length > 700) concise = concise.Substring(0, 700);
            lock (Sync)
            {
                Errors[id] = concise;
                if (ElementHistoryRetryPolicy.IsTransient(ex))
                {
                    if (!TransportRetries.TryGetValue(id, out var retry)) TransportRetries[id] = retry = new TransportRetry();
                    retry.Failures++;
                    retry.NextUtc = DateTime.UtcNow.Add(ElementHistoryRetryPolicy.Delay(retry.Failures));
                }
                else TransportRetries.Remove(id);
            }
            SetReadiness(id, ReadinessState.Unavailable, concise);
            Trace.WriteLine("History durable " + id + ": " + ex);
            Task.Run(() =>
            {
                try
                {
                    var path = Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "history-data", "diagnostics");
                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, "failure-" + Guid.NewGuid().ToString("N") + ".json"),
                        JsonConvert.SerializeObject(new { EventId = id, AtUtc = DateTime.UtcNow, Exception = ex.ToString() }));
                }
                catch { }
            });
        }
        private static void ReleaseDueTransportFailures(DateTime nowUtc)
        {
            List<string> due;
            lock (Sync)
            {
                due = TransportRetries.Where(pair => pair.Value.NextUtc <= nowUtc && Errors.ContainsKey(pair.Key)).Select(pair => pair.Key).ToList();
                foreach (var id in due) Errors.Remove(id);
            }
            foreach (var id in due) SetReadiness(id, ReadinessState.Preparing, "Nouvelle tentative après indisponibilité du stockage…");
        }
        internal static Status GetStatus()
        {
            lock (Sync)
            {
                var relevant = Sessions.Values.Where(s => s.Referenced).ToList();
                var runnable = Pending.Where(p => !Errors.ContainsKey(p.Key)).Select(p => p.Value).ToList();
                return new Status { PendingJobs = runnable.Count, PendingModels = runnable.Select(e => e.ModelKey).Distinct().Count(), ConvertedEvents = Converted,
                FailedJobs = Errors.Count, Failures = Errors.Select(e => e.Key + " : " + e.Value).ToList(),
                BridgeFiles = relevant.Select(s => s.BridgeFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                DeletedBridgeFiles = relevant.Where(s => s.Deleted).Select(s => s.BridgeFile).ToList(),
                Complete = Loaded && Pending.Count == 0 && Errors.Count == 0 && (Io == null || Io.IsCompleted),
                Working = Io != null && !Io.IsCompleted || NativePass != null,
                ProgressLabel=PreparationLabel,ProgressCompleted=PreparationCompleted,ProgressTotal=PreparationTotal,
                ProgressElapsedSeconds=PreparationStepClock.Elapsed.TotalSeconds, PreparationElapsedSeconds=PreparationClock.Elapsed.TotalSeconds,
                SessionClosed = (relevant.Count > 0 ? relevant : Sessions.Values.ToList()).All(s => s.Closed) };
            }
        }
        internal static bool IsKnownLiveExclusion(ElementHistoryEvent ev) => HasLiveExclusion(ev);
        private static bool HasLiveExclusion(ElementHistoryEvent ev)
        {
            if (ev?.Delta == null || !ev.Delta.TryGetValue("excluded3D", out var value) || value == null) return false;
            var metadata = JObject.FromObject(value);
            var reason = (string)metadata["Reason"];
            var kind = (string)metadata["Class"] ?? "";
            var uid = (string)metadata["SourceUniqueId"];
            ev.Delta.TryGetValue("deletedUniqueId", out var deletedUid);
            bool annotation = reason == "annotation" && ((bool?)metadata["ViewSpecific"] == true
                || kind == "Autodesk.Revit.DB.Dimension" || kind == "Autodesk.Revit.DB.LinearDimension"
                || kind == "Autodesk.Revit.DB.SpotDimension");
            bool insulation = reason == "insulation" && (kind == "Autodesk.Revit.DB.Plumbing.PipeInsulation"
                || kind == "Autodesk.Revit.DB.Mechanical.DuctInsulation");
            bool externalModel = reason == "external_model" && ElementHistoryExternalModels.IsExcludedClass(kind);
            // Exact built-in center-line categories; ordinary curves/instances
            // named "axis" are never acknowledged by a name heuristic.
            var category = (long?)metadata["CategoryId"] ?? 0;
            bool axis = reason == "axis" && kind == "Autodesk.Revit.DB.Element"
                && new long[] { -2008214, -2008210, -2008196, -2008141, -2008140, -2008139, -2008136,
                    -2008072, -2008066, -2008051, -2008045, -2008021, -2008001, -2000288 }.Contains(category);
            return (bool?)metadata["CapturedWhileAlive"] == true
                && (int?)metadata["SourceElementId"] == ev.ElementId
                && !string.IsNullOrWhiteSpace(uid) && string.Equals(uid, Convert.ToString(deletedUid), StringComparison.Ordinal)
                && kind.StartsWith("Autodesk.Revit.DB.", StringComparison.Ordinal)
                && (annotation || axis || insulation || externalModel);
        }

        private static bool QueueExcludedAnnotationAcknowledgements(List<ElementHistoryEvent> jobs)
        {
            const string ackFailurePrefix = "ACK_3D_EXCLUDED: ";
            var acknowledgements = new List<Prepared>();
            foreach (var ev in jobs)
            {
                if (acknowledgements.Count >= 8) break;
                // Only exact category evidence or metadata captured from a live
                // excluded object. A missing recipe never implies exclusion.
                var category = ev.Category ?? ev.DeletionAudit?.Category;
                if (!string.Equals(category, "Cotes automatiques de l'esquisse", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(category, "Automatic Sketch Dimensions", StringComparison.OrdinalIgnoreCase)
                    && !HasLiveExclusion(ev)) continue;
                var id = EventId(ev);
                lock (Sync)
                    if (Errors.TryGetValue(id, out var failure) && failure.StartsWith(ackFailurePrefix, StringComparison.Ordinal)) continue;
                try
                {
                    var request = ToRequest(ev, resolve: false);
                    request.Recipe = null;
                    request.CaptureFailure = HasLiveExclusion(ev)
                        ? "Hors périmètre de restauration : objet exclu identifié avant sa suppression (annotation, axe, isolant ou lien/import externe)."
                        : "Hors périmètre 3D : cotation identifiée par sa catégorie historique exacte.";
                    acknowledgements.Add(new Prepared { Event = ev, Request = request });
                }
                catch (Exception ex) { RecordFailure(id, new InvalidDataException(ackFailurePrefix + ex.Message, ex)); }
            }
            if (acknowledgements.Count == 0) return false;
            Io = Task.Run(() =>
            {
                foreach (var item in acknowledgements)
                {
                    try { Publish(item); }
                    catch (Exception ex) { RecordFailure(EventId(item.Event), new InvalidDataException(ackFailurePrefix + ex.Message, ex)); }
                }
            });
            return true;
        }

        internal static List<ElementHistoryEvent> OrderPendingJobs(IEnumerable<ElementHistoryEvent> jobs, IEnumerable<string> activeModelKeys)
        {
            var active = new HashSet<string>(activeModelKeys.Where(key => !string.IsNullOrWhiteSpace(key)), StringComparer.OrdinalIgnoreCase);
            return jobs.OrderByDescending(ev => active.Contains(ev.ModelKey ?? ""))
                .ThenBy(ev => string.IsNullOrWhiteSpace(ev.EventId) || ev.EventId.StartsWith("legacy-", StringComparison.Ordinal) ? 1 : 0)
                .ThenByDescending(ev => ev.Ts).ThenBy(ev => ev.EventId, StringComparer.Ordinal).ToList();
        }

        internal static void ProcessPending(Application application, bool force = false)
        {
            if (ElementHistoryMaintenance.IsActive) return;
            if (ElementHistoryCompatibility.GetLoadedApiIssue() != null) return;
            force |= IsManualPreparing;
            // Isolated regression processes explicitly drive API waves only after
            // redirecting the history store. Never touch the real store first.
            if (!force && Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") == "1" && !IsolatedAutomaticPreparation) return;
            if (application == null || HistoryBackgroundWork.Paused && !force) return;
            if(!force && (NativePass!=null || Sources!=null))ResetNativePreparation();
            bool liveSession;
            lock (Sync) liveSession = Live.Count > 0;
            // Unsaved/cloud projects may have no opening-copy session. They are
            // still user documents and require the same inactivity protection.
            liveSession |= application.Documents.Cast<Document>().Any(doc => !ElementHistoryNativeArchive.Owns(doc));
            if (DateTime.UtcNow < NextPassUtc) return;
            NextPassUtc = DateTime.UtcNow.AddMilliseconds(force ? 50 : 1000);
            ReleaseDueTransportFailures(DateTime.UtcNow);
            lock (Sync)
            {
                if (Io != null && !Io.IsCompleted) return;
                var store = CollaborativeModelTrackerStore.ActiveDirectory;
                if (CurrentStore != null && !string.Equals(CurrentStore, store, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Le dossier historique a changé pendant une conversion.");
                CurrentStore = store;
                if (!Loaded)
                {
                    if (Errors.ContainsKey("load")) { FinishPreparationLocked(); return; }
                    Io = Task.Run(() =>
                    {
                        try
                        {
                            // Recover locally staged work/torn appends before the
                            // strict journal scan can classify a crash fragment.
                            ElementHistoryLocalSpool.Default.Drain(store, EventsPersisted);
                            Load();
                        }
                        catch (Exception ex) { RecordFailure("load", ex); }
                    }); return;
                }
            }
            List<ElementHistoryEvent> jobs;
            lock (Sync) jobs = Pending.Values.Where(e => !ManualPreparation || InPreparationScope(e)).ToList();
            // A backlog from other projects must not delay today's deletion in
            // an open model. Native API preparation requires an explicit request.
            var activeKeys = application.Documents.Cast<Document>().Where(doc => !ElementHistoryNativeArchive.Owns(doc))
                .Select(ElementHistoryTracker.GetDocumentKeyForHistory).ToList();
            lock (Sync) activeKeys.AddRange(Live.Values.Select(session => session.ModelKey));
            jobs = OrderPendingJobs(jobs, activeKeys);
            bool noRunnableJobs;
            lock (Sync)
            {
                if (ManualPreparation && PriorityModel != null)
                    jobs = jobs.OrderByDescending(e => string.Equals(e.ModelKey, PriorityModel, StringComparison.OrdinalIgnoreCase)).ToList();
                noRunnableJobs = Loaded && !jobs.Any(e => !Errors.ContainsKey(EventId(e)));
                // A just-recorded deletion may still be travelling through the
                // local spool when the user clicks the button. Keep the request
                // alive briefly so the asynchronous journal can deliver it.
                if (noRunnableJobs && (jobs.Count > 0 || DateTime.UtcNow - ManualRequestedUtc > TimeSpan.FromSeconds(5)))
                { FinishPreparationLocked(); }
            }
            if (noRunnableJobs || !force && liveSession && !HistoryBackgroundWork.PreparationCanRun())
            { Sources?.Dispose(); Sources = null; OpenBridgeFile = null; }
            if (noRunnableJobs && jobs.Count > 0)
            {
                ScheduleClosedCleanup();
                return;
            }
            if (jobs.Count == 0)
            {
                if (!force && liveSession && !HistoryBackgroundWork.PreparationCanRun()) return;
                Sources?.Dispose(); Sources = null; PreparedJobs.Clear();
                lock (Sync) if (!Sessions.Values.Any(s => s.Closed && !s.Deleted)) return;
                ScheduleClosedCleanup();
                return;
            }
            if (QueueExcludedAnnotationAcknowledgements(jobs)) return;
            // A complete live-captured recipe needs only JSON publication, even while
            // an opening safety copy exists for other objects in the same deletion.
            var capturedDirect = jobs.FirstOrDefault(ev => {
                lock (Sync) if (Errors.ContainsKey(EventId(ev))) return false;
                return HasCapturedAutonomousRecipe(ev);
            });
            if (capturedDirect != null)
            {
                try { ProcessDirectDefinitions(application, jobs, capturedDirect); }
                catch (Exception ex) { RecordFailure(EventId(capturedDirect), ex); }
                return;
            }
            if (force)
            {
                var migration = jobs.FirstOrDefault(ev => {
                    var id = EventId(ev);
                    lock (Sync) if (Errors.ContainsKey(id) || AutonomousMigrationAttempted.Contains(id)) return false;
                    return ElementHistoryDurableStore.ResolveAutonomousConversion(id) == null
                        && IsReadyNativeRequest(ElementHistoryDurableStore.ResolveConversion(id));
                });
                if (migration != null)
                {
                    PrepareAutonomousNativeMigration(application, migration);
                    return;
                }
            }
            var nativeWork = jobs.FirstOrDefault(e => !Errors.ContainsKey(EventId(e)) && TryGetOpeningNativeRequest(e) != null);
            if (nativeWork != null && force)
            {
                PrepareNativeClipboard(application, jobs, nativeWork);
                return;
            }
            // Direct recipes only need JSON processing and background IO. Do not
            // hold them behind the idle gate for opening a full Revit model.
            var direct = jobs.FirstOrDefault(e => {
                lock (Sync) if (Errors.ContainsKey(EventId(e))) return false;
                var recipe = ToRequest(e, false).Recipe;
                return recipe?.Native == null && recipe?.Kind != "native_archive";
            });
            if (direct != null)
            {
                try { ProcessDirectDefinitions(application, jobs, direct); }
                catch (Exception ex) { RecordFailure(EventId(direct), ex); }
                return;
            }
            // A quiet/home/background Revit session is not permission to open and convert models.
            // Keep pending work intact until the user presses the preparation button.
            if (!force) { ScheduleClosedCleanup(); return; }
            string processingId = null;
            try
            {
                var first = jobs.FirstOrDefault(e => !Errors.ContainsKey(EventId(e)));
                if (first == null) return;
                processingId = EventId(first);
                var firstRequest = ToRequest(first, resolve: false);
                var receipt = SourceArtifact(firstRequest.Recipe);
                if (firstRequest.Recipe?.Native == null && firstRequest.Recipe?.Kind != "native_archive")
                { ProcessDirectDefinitions(application, jobs, first); return; }
                if (receipt.WholeModel && !File.Exists(receipt.File)
                    && !File.Exists(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history", Path.GetFileName(receipt.File)))
                    && DateTime.UtcNow - first.Ts < TimeSpan.FromMinutes(1)) return;
                if (receipt.WholeModel && ElementHistoryBridgeCache.IsCopy(LocalBridgeDirectory, receipt.File) && !File.Exists(receipt.File))
                    throw new InvalidDataException("La copie temporaire a expiré après 15 jours, a été supprimée ou n’a pas pu être créée. Cet élément ne peut plus être préparé depuis cette copie.");
                if (!receipt.WholeModel)
                {
                    Io = Task.Run(() => { try { ConvertSmallNative(first, firstRequest); } catch (Exception ex) { RecordFailure(EventId(first), ex); } });
                    return;
                }
                if (Sources != null && !string.Equals(OpenBridgeFile, receipt.File, StringComparison.OrdinalIgnoreCase))
                {
                    Sources.Dispose(); Sources = null; OpenBridgeFile = null;
                    // Release and clean the completed source even while another
                    // model still has work waiting in the global queue.
                    ScheduleClosedCleanup();
                    return;
                }
                OpenBridgeFile = receipt.File;
                Sources ??= new ElementHistoryNativeArchive.Sources();
                var source = Sources.Open(application, receipt);
                var sameSource = jobs.Where(e => {
                    var r = ToRequest(e, false).Recipe; var a = SourceArtifact(r);
                    return r?.Native?.WholeModel == true && a?.WholeModel == true
                        && string.Equals(Path.GetFileName(a.File), Path.GetFileName(receipt.File), StringComparison.OrdinalIgnoreCase);
                }).ToList();
                var prepared = new List<Prepared>();
                var preparation = Stopwatch.StartNew();
                int preparationLimit = force || !liveSession ? 32 : 2;
                int preparationBudgetMs = force || !liveSession ? 250 : 25;
                foreach (var ev in sameSource)
                {
                    if (prepared.Count > 0 && (preparation.ElapsedMilliseconds >= preparationBudgetMs || prepared.Count >= preparationLimit)) break;
                    var id = EventId(ev);
                    lock (Sync) if (Errors.ContainsKey(id)) continue;
                    var request = ToRequest(ev, false);
                    request.ClassificationOnly = ResolvePreparedRequest(id) != null;
                    try { PrepareCompactOpeningRequest(source, request); }
                    catch (InvalidDataException ex) { MakeUnavailable(request, ex.Message); }
                    prepared.Add(new Prepared { Event = ev, Request = request });
                }
                if (prepared.Count == 0) throw new InvalidDataException("Aucun objet directement reconstructible dans cette copie d’ouverture.");
                PublishPreparedAsync(prepared);
            }
            catch (Exception ex) { RecordFailure(processingId ?? "convert", ex); }
        }
        private static void BindPayload(HistoryRecipe recipe, string payload)
        {
            foreach (var artifact in new[] { recipe.Native, recipe.OpeningFallback }.Where(a => a != null))
            {
                if (artifact.WholeModel) throw new InvalidDataException("Une capsule durable ne peut pas contenir une maquette complète.");
                if (string.IsNullOrEmpty(payload)) throw new InvalidDataException("Données natives de la capsule absentes.");
                artifact.PayloadId = payload; artifact.File = null; artifact.Ready = true;
            }
            foreach (var circuit in recipe.ElectricalCircuits ?? new List<HistoryCircuit>()) BindPayload(circuit.Recipe, payload);
        }
        private static void PrepareAutonomousNativeMigration(Application application, ElementHistoryEvent ev)
        {
            var id = EventId(ev);
            try
            {
                var archived = ElementHistoryDurableStore.ResolveConversion(id);
                if (!IsReadyNativeRequest(archived)) return;
                var sourceKey = "payload:" + archived.Recipe.Native.PayloadId;
                if (Sources != null && !string.Equals(OpenBridgeFile, sourceKey, StringComparison.Ordinal)) ResetNativePreparation();
                Sources ??= new ElementHistoryNativeArchive.Sources();
                OpenBridgeFile = sourceKey;
                SetPreparationProgress("Lecture ciblée pour enregistrer le JSON…", 0, 1);
                var source = Sources.Open(application, archived.Recipe.Native);
                var autonomous = CaptureAutonomousNativeRequest(source, archived);
                if (autonomous == null)
                {
                    // Unsupported data keeps its native archive and remains restorable.
                    ElementHistoryDurableStore.DeleteJob(id);
                    lock (Sync) { Pending.Remove(id); Errors.Remove(id); AutonomousMigrationAttempted.Add(id); }
                    SetReadiness(id, ReadinessState.Ready, "Archive native conservée : recette JSON autonome indisponible pour cet objet.");
                    return;
                }
                var item = new Prepared { Event = ev, Request = autonomous };
                Io = Task.Run(() => { try { PublishAutonomousPrepared(item); } catch (Exception ex) { RecordFailure(id, ex); } });
            }
            catch (Exception ex) { RecordFailure(id, ex); }
        }

        private static void PublishAutonomousPrepared(Prepared item)
        {
            var id = EventId(item.Event);
            if (item.Request.OpeningClassification != null)
                ElementHistoryDurableStore.PublishOpeningClassification(id, item.Request.OpeningClassification);
            ElementHistoryDurableStore.PublishAutonomousConversion(id, item.Request);
            var effective = ElementHistoryDurableStore.ResolveAutonomousConversion(id);
            if (!IsReadyDirectRequest(effective)) throw new InvalidDataException("Recette JSON autonome publiée mais non relue.");
            ElementHistoryDurableStore.DeleteJob(id);
            lock (Sync) { Pending.Remove(id); Errors.Remove(id); TransportRetries.Remove(id); Converted++; }
            SetReadiness(id, ReadinessState.Ready, "Recette JSON autonome enregistrée ; indépendante des copies temporaires.");
        }

        private static void Publish(Prepared item)
        {
            var id = EventId(item.Event);
            if (item.Request.OpeningClassification != null)
                ElementHistoryDurableStore.PublishOpeningClassification(id, item.Request.OpeningClassification);
            if (item.Request.NativeGeneration) ElementHistoryDurableStore.PublishNativeConversion(id, item.Request);
            else if (!item.Request.ClassificationOnly) ElementHistoryDurableStore.PublishConversion(id, item.Request);
            var effective = ResolvePreparedRequest(id);
            if (effective == null) throw new InvalidDataException("Conversion publiée mais non relue.");
            ElementHistoryDurableStore.DeleteJob(id);
            lock (Sync) { Pending.Remove(id); Errors.Remove(id); TransportRetries.Remove(id); Converted++; }
            bool usable = IsReadyDirectRequest(effective) || IsReadyNativeRequest(effective);
            SetReadiness(id, usable ? ReadinessState.Ready : ReadinessState.Unavailable,
                usable ? ReadyReason(effective.Recipe) : effective.CaptureFailure
                    ?? "Les données enregistrées ne contiennent pas de recette ou de copie de restauration exploitable.");
        }
        private static void ConvertSmallNative(ElementHistoryEvent ev, HistoryRestoreRequest request)
        {
            foreach (var artifact in Artifacts(request.Recipe))
            {
                // Older mutable archives have no frozen definition version.
                // Wrapping their bytes alone would publish a success that the
                // durable restore correctly refuses (or substitute live types).
                if (string.IsNullOrWhiteSpace(artifact.TypeVersion))
                    throw new InvalidDataException("Cette archive native ne possède pas de définitions historiques versionnées ; sa conversion autonome reste en attente.");
                if (!string.IsNullOrEmpty(artifact.PayloadId)) continue;
                if (artifact.WholeModel) throw new InvalidDataException("Une copie complète exige une conversion Revit.");
                var path = Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history", Path.GetFileName(artifact.File));
                artifact.PayloadId = ElementHistoryDurableStore.PublishNative(path); artifact.File = null; artifact.Ready = true;
            }
            Publish(new Prepared { Event = ev, Request = request });
        }
        private const string TerminalBridgeFailurePrefix = "Préparation terminée avec échec définitif : ";
        private static bool IsTerminalBridgeFailure(HistoryRestoreRequest request)
            => request?.Recipe == null && request?.CaptureFailure?.StartsWith(TerminalBridgeFailurePrefix, StringComparison.Ordinal) == true;

        private static bool IsCompletedBridgeRequest(HistoryRestoreRequest request)
            => IsTerminalBridgeFailure(request) || IsReadyDirectRequest(request) || IsReadyNativeRequest(request);

        private static HistoryRestoreRequest FinalizeClosedFailure(ElementHistoryEvent ev, HistoryRestoreRequest effective, List<Session> closed)
        {
            if (IsCompletedBridgeRequest(effective)) return effective;
            var id = EventId(ev);
            string reason;
            bool modelStillLive;
            lock (Sync)
            {
                if (!Pending.ContainsKey(id) || !Errors.TryGetValue(id, out reason) || TransportRetries.ContainsKey(id)) return effective;
                modelStillLive = Live.Values.Any(session => !session.Closed && string.Equals(session.ModelKey, ev.ModelKey, StringComparison.OrdinalIgnoreCase));
            }
            var original = ToRequest(ev, false);
            var sources = Artifacts(original.Recipe).Where(artifact => artifact.WholeModel)
                .Select(artifact => Path.GetFileName(artifact.File)).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
            bool belongsToClosedSource = closed.Any(session => string.Equals(session.ModelKey, ev.ModelKey, StringComparison.OrdinalIgnoreCase)
                && (sources.Count > 0 ? sources.Contains(Path.GetFileName(session.BridgeFile), StringComparer.OrdinalIgnoreCase) : !modelStillLive));
            if (!belongsToClosedSource) return effective;
            // Preserve a durable final outcome before removing the source or job.
            // Failed writes/readback leave both the job and source protected.
            original.Recipe = null;
            original.CaptureFailure = TerminalBridgeFailurePrefix + reason;
            original.NativeGeneration = true;
            original.ClassificationOnly = false;
            Publish(new Prepared { Event = ev, Request = original });
            var completed = ResolvePreparedRequest(id);
            if (!IsTerminalBridgeFailure(completed)) throw new InvalidDataException("La clôture de l’échec historique n’a pas été relue.");
            return completed;
        }

        private static void CleanupClosedSessions()
        {
            // A deletion may be safely staged locally while the shared folder is
            // unavailable. It has not reached ReadJournal yet: keep every bridge.
            if (ElementHistoryLocalSpool.Default.HasPending(CollaborativeModelTrackerStore.ActiveDirectory)) return;
            List<Session> closed;
            lock (Sync) closed = Sessions.Values.Where(s => s.Closed && !s.Deleted).ToList();
            if (closed.Count == 0) return;
            // Complete scan, including compressed journals. Unknown or damaged
            // data aborts cleanup. Original journal references are superseded only
            // by an independently readable, validated immutable conversion.
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in ReadJournal().Where(e => e.Action == "delete"))
            {
                var request = FinalizeClosedFailure(ev, ToRequest(ev), closed);
                // Completed direct/native requests and durable final failures no
                // longer depend on the opening RVT. Native payloads remain stored.
                if (IsCompletedBridgeRequest(request)) continue;
                if (!HasLiveExclusion(ev))
                {
                    var classification = ElementHistoryDurableStore.ResolveOpeningClassification(EventId(ev));
                    if (classification?.IsExcluded() != true)
                        foreach (var artifact in Artifacts(ToRequest(ev, false).Recipe).Where(a => a.WholeModel))
                            references.Add(Path.GetFileName(artifact.File));
                }
                foreach (var a in Artifacts(request.Recipe).Where(a => a.WholeModel))
                    references.Add(Path.GetFileName(a.File));
                if (!string.IsNullOrWhiteSpace(ev.EventId) && !ev.EventId.StartsWith("legacy-", StringComparison.Ordinal)
                    && ResolvePreparedRequest(EventId(ev)) == null)
                    foreach (var session in closed.Where(s => string.Equals(s.ModelKey, ev.ModelKey, StringComparison.OrdinalIgnoreCase)))
                        references.Add(Path.GetFileName(session.BridgeFile));
            }
            foreach (var session in closed)
            {
                if (references.Contains(Path.GetFileName(session.BridgeFile))) continue;
                var path = Path.GetFullPath(session.BridgeFile);
                var root = Path.GetFullPath(LocalBridgeDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path) != "snapshot-opening-" + session.Id + ".rvt")
                    throw new InvalidDataException("Chemin de passerelle hors du cache local : suppression refusée.");
                try { ElementHistoryBridgeCache.Delete(LocalBridgeDirectory, path); }
                catch (IOException) { continue; }
                lock (Sync) session.Deleted = true;
                SaveSession(session);
            }
        }
        internal static void Shutdown()
        {
            // No API calls on a worker. Closing archive documents requires the
            // normal Revit shutdown callback. Persistent jobs survive this stop.
            Sources?.Dispose(); Sources = null; PreparedJobs.Clear();
            CancelPreparation();
            // No blocking wait at exit. Completed copies with durable journals
            // can be removed now; an interrupted copy is recovered on next start.
            try
            {
                ElementHistoryTracker.FlushPendingForDurability();
                List<Session> sessions;
                lock (Sync) sessions = Live.Values.Distinct().Where(s => s.CopyTask == null || s.CopyTask.IsCompleted).ToList();
                foreach (var session in sessions) { session.Closed = true; SaveSession(session); }
                CleanupClosedSessions();
            }
            catch (Exception ex) { Trace.WriteLine("History exit cleanup: " + ex.Message); }
        }
    }
}

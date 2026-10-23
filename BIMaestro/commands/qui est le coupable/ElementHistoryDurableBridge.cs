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
            public int ConvertedEvents { get; set; }
            public int FailedJobs { get; set; }
            public List<string> Failures { get; set; }
            public List<string> BridgeFiles { get; set; }
            public List<string> DeletedBridgeFiles { get; set; }
            public bool Complete { get; set; }
            public bool SessionClosed { get; set; }
            public bool Working { get; set; }
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
        private static ElementHistoryNativeArchive.Sources Sources;
        private static Task Io;
        private static bool Loaded;
        private static int Converted;
        private static DateTime NextPassUtc;
        private static string CurrentStore;
        private static bool IsolatedAutomaticPreparation;
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
        internal static HistoryRestoreRequest ToRequest(ElementHistoryEvent ev, bool resolve = true)
        {
            if (resolve)
            {
                var converted = ElementHistoryDurableStore.ResolveConversion(EventId(ev));
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
            var converted = ElementHistoryDurableStore.ResolveConversion(EventId(ev));
            if (converted == null) return;
            ev.Delta = ev.Delta == null ? new Dictionary<string, object>() : new Dictionary<string, object>(ev.Delta);
            ev.Delta["recipe"] = converted.Recipe; ev.Delta["deletedUniqueId"] = converted.SourceUniqueId;
            ev.Delta["captureFailure"] = converted.CaptureFailure;
            ev.Delta["superComponentUniqueId"] = converted.SuperComponentUniqueId;
            ev.Delta["familyTypeUniqueId"] = converted.FamilyTypeUniqueId;
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
                var request = ToRequest(ev, resolve: false);
                // New deletions must not disappear from the outbox simply
                // because capture failed or a direct recipe lacks frozen types.
                bool currentRecord = !string.IsNullOrWhiteSpace(ev.EventId) && !ev.EventId.StartsWith("legacy-", StringComparison.Ordinal);
                if (!NeedsConversion(request.Recipe) && !currentRecord) continue;
                ev.EventId = EventId(ev);
                var receipt = SourceArtifact(request.Recipe);
                Session used = null;
                bool referencedChanged = false;
                if (receipt?.WholeModel == true)
                    lock (Sync)
                    {
                        used = Sessions.Values.FirstOrDefault(s => string.Equals(Path.GetFileName(s.BridgeFile), Path.GetFileName(receipt.File), StringComparison.OrdinalIgnoreCase));
                        if (used != null && !used.Referenced) { used.Referenced = true; referencedChanged = true; }
                    }
                if (referencedChanged) SaveSession(used);
                if (ElementHistoryDurableStore.HasConversion(ev.EventId)) continue;
                ElementHistoryDurableStore.WriteJob(ev);
                lock (Sync) Pending[ev.EventId] = ev;
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
            foreach (var ev in ElementHistoryDurableStore.ReadPendingJobs())
            {
                var id = EventId(ev);
                if (ElementHistoryDurableStore.ResolveConversion(id) != null)
                {
                    ElementHistoryDurableStore.DeleteJob(id);
                    lock (Sync) { Pending.Remove(id); Converted++; }
                }
                else lock (Sync) Pending[id] = ev;
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
                foreach (var artifact in Artifacts(ToRequest(ev, false).Recipe).Where(a => a.WholeModel))
                {
                    Session used;
                    lock (Sync) used = Sessions.Values.FirstOrDefault(s => string.Equals(Path.GetFileName(s.BridgeFile), Path.GetFileName(artifact.File), StringComparison.OrdinalIgnoreCase));
                    if (used != null && !used.Referenced) { used.Referenced = true; SaveSession(used); }
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
                return new Status { PendingJobs = Pending.Count, ConvertedEvents = Converted,
                FailedJobs = Errors.Count, Failures = Errors.Select(e => e.Key + " : " + e.Value).ToList(),
                BridgeFiles = relevant.Select(s => s.BridgeFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                DeletedBridgeFiles = relevant.Where(s => s.Deleted).Select(s => s.BridgeFile).ToList(),
                Complete = Loaded && Pending.Count == 0 && Errors.Count == 0 && (Io == null || Io.IsCompleted),
                Working = Io != null && !Io.IsCompleted,
                SessionClosed = (relevant.Count > 0 ? relevant : Sessions.Values.ToList()).All(s => s.Closed) };
            }
        }
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

        internal static void ProcessPending(Application application, bool force = false)
        {
            // Isolated regression processes explicitly drive API waves only after
            // redirecting the history store. Never touch the real store first.
            if (!force && Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") == "1" && !IsolatedAutomaticPreparation) return;
            if (application == null || HistoryBackgroundWork.Paused && !force) return;
            bool liveSession;
            lock (Sync) liveSession = Live.Count > 0;
            // Unsaved/cloud projects may have no opening-copy session. They are
            // still user documents and require the same inactivity protection.
            liveSession |= application.Documents.Cast<Document>().Any(doc => !ElementHistoryNativeArchive.Owns(doc));
            if (!force && liveSession && !HistoryBackgroundWork.PreparationCanRun()) return;
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
                    if (Errors.ContainsKey("load")) return;
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
            lock (Sync) jobs = Pending.Values.ToList();
            if (jobs.Count == 0)
            {
                Sources?.Dispose(); Sources = null; PreparedJobs.Clear();
                lock (Sync) if (!Sessions.Values.Any(s => s.Closed && !s.Deleted)) return;
                Io = Task.Run(() => { try { CleanupClosedSessions(); } catch (Exception ex) { RecordFailure("cleanup", ex); } });
                return;
            }
            if (QueueExcludedAnnotationAcknowledgements(jobs)) return;
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
                if (!receipt.WholeModel)
                {
                    Io = Task.Run(() => { try { ConvertSmallNative(first, firstRequest); } catch (Exception ex) { RecordFailure(EventId(first), ex); } });
                    return;
                }
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
        private static void Publish(Prepared item)
        {
            var id = EventId(item.Event);
            ElementHistoryDurableStore.PublishConversion(id, item.Request);
            if (ElementHistoryDurableStore.ResolveConversion(id) == null) throw new InvalidDataException("Conversion publiée mais non relue.");
            ElementHistoryDurableStore.DeleteJob(id);
            lock (Sync) { Pending.Remove(id); Errors.Remove(id); TransportRetries.Remove(id); Converted++; }
            SetReadiness(id, item.Request.Recipe == null ? ReadinessState.Unavailable : ReadinessState.Ready,
                item.Request.Recipe == null ? item.Request.CaptureFailure : ReadyReason(item.Request.Recipe));
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
                var request = ToRequest(ev);
                foreach (var a in Artifacts(request.Recipe).Where(a => a.WholeModel))
                    references.Add(Path.GetFileName(a.File));
                if (!string.IsNullOrWhiteSpace(ev.EventId) && !ev.EventId.StartsWith("legacy-", StringComparison.Ordinal)
                    && ElementHistoryDurableStore.ResolveConversion(EventId(ev)) == null)
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
                using (var lease = new FileStream(path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (File.Exists(path)) File.Delete(path);
                    lock (Sync) session.Deleted = true;
                    SaveSession(session);
                }
                try { File.Delete(path + ".lease"); } catch { }
            }
        }
        internal static void Shutdown()
        {
            // No API calls on a worker. Closing archive documents requires the
            // normal Revit shutdown callback. Persistent jobs survive this stop.
            Sources?.Dispose(); Sources = null; PreparedJobs.Clear();
        }
    }
}

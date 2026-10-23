using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        bool partialCapturePaused, partialAwaitingSessionClose, automaticPreparationPaused;
        DateTime nextPartialUtc;
        object partialCloseEvidence;
        PartialSelection partialSelection;
        long[] fullDeletedPhysicalIds = Array.Empty<long>();
        readonly HashSet<string> partialRestoreEventIds = new(StringComparer.Ordinal);

        public sealed class PartialEvent
        {
            public string EventId { get; set; }
            public long ElementId { get; set; }
            public string SourceUid { get; set; }
            public bool Included { get; set; }
            public string RecipeKind { get; set; }
            public string[] PayloadIds { get; set; } = Array.Empty<string>();
            public string Reason { get; set; }
        }
        public sealed class PartialSelection
        {
            public DateTime CapturedUtc { get; set; }
            public int FullDeletionEventCount { get; set; }
            public long[] FullDeletedPhysicalIds { get; set; }
            public long[] SelectedPhysicalIds { get; set; }
            public string[] SelectedEventIds { get; set; }
            public List<PartialEvent> Records { get; set; } = new();
            public object ConversionStatus { get; set; }
            public bool FullConversionComplete { get; set; }
        }
        IEnumerable<object> ArtifactsOf(object recipe)
        {
            if (recipe == null) yield break;
            foreach (var artifact in new[] { Read(recipe, "Native"), Read(recipe, "OpeningFallback") }) if (artifact != null) yield return artifact;
            foreach (var circuit in (Read(recipe, "ElectricalCircuits") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>())
                foreach (var artifact in ArtifactsOf(Read(circuit, "Recipe"))) yield return artifact;
        }
        object ResolveConverted(object ev) => Call(product.GetType("Analyse.ElementHistoryDurableStore", true), "ResolveConversion", (string)Call(durableBridge, "EventId", ev));

        void PersistSubset(UIApplication application)
        {
            if (!IsDurableCapture || !deleted || !journalReady || Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") != "1")
                throw new InvalidOperationException("Partial capture is restricted to the isolated manual durable test after natural journal persistence.");
            // Stop scheduling new API waves before checking the publication task.
            // An in-flight worker may finish naturally; the caller can retry in a
            // later ExternalEvent without blocking the Revit message pump.
            durableConverting = false; durableAwaitingCloseCleanup = false;
            if (config.BusinessWorkflow) { var background = product.GetType("Analyse.HistoryBackgroundWork", true); background.GetField("Paused", Flags).SetValue(null, true); automaticPreparationPaused = (bool?)Read(background, "Paused") == true; if (!automaticPreparationPaused) throw new InvalidOperationException("Isolated automatic preparation did not pause."); Record("partial_automatic_preparation_paused", new { AutomaticPreparationPaused = true, IsolatedPid = Environment.ProcessId }); }
            var status = ReadDurableStatus();
            if ((bool?)Read(status, "Working") == true) throw new InvalidOperationException("A publication wave is still working; retry after it finishes.");
            var range = ((IEnumerable)Call(tracker, "LoadModelHistoryForLocalRange", new[] { docKey }, openedUtc.ToLocalTime().Date.AddDays(-1), DateTime.Today.AddDays(1))).Cast<object>();
            var physical = deletedPhysicalIds.ToHashSet(); var records = new List<PartialEvent>();
            foreach (var ev in range.Where(ev => (string)Read(ev, "Action") == "delete" && (string)Read(ev, "Tx") == transactionName))
            {
                long id = Convert.ToInt64(Read(ev, "ElementId")); if (!physical.Contains(id)) continue;
                var record = new PartialEvent { EventId = (string)Call(durableBridge, "EventId", ev), ElementId = id, SourceUid = specs[id].Uid };
                records.Add(record);
                try
                {
                    var request = ResolveConverted(ev); var recipe = Read(request, "Recipe"); record.RecipeKind = Read(recipe, "Kind")?.ToString();
                    if (request == null) { record.Reason = "No immutable validated conversion; raw recipe fallback is forbidden."; continue; }
                    if (recipe == null) { record.Reason = Read(request, "CaptureFailure")?.ToString() ?? "Converted diagnostic without a restorable recipe."; continue; }
                    if ((string)Read(request, "SourceUniqueId") != record.SourceUid) { record.Reason = "Converted identity differs from the physical baseline."; continue; }
                    var artifacts = ArtifactsOf(recipe).ToArray();
                    if (artifacts.Length == 0) { record.Reason = "UnknownFrozenDefinitions: no versioned physical payload receipt."; continue; }
                    if (artifacts.Any(artifact => (bool?)Read(artifact, "WholeModel") == true
                        || Read(artifact, "File") is string path && durableBridgePaths.Contains(path)
                        || Read(artifact, "PayloadId") is not string payload || payload.Length != 64 || !payload.All(Uri.IsHexDigit)
                        || Read(artifact, "TypeVersion") is not string version || version.Length is not (32 or 64) || !version.All(Uri.IsHexDigit)))
                    { record.Reason = "Converted receipt still needs a temporary file or has no validated payload identity."; continue; }
                    record.PayloadIds = artifacts.Select(artifact => (string)Read(artifact, "PayloadId")).Distinct().ToArray();
                    record.Included = true;
                }
                catch (Exception ex) { record.Reason = "Validated conversion could not be read: " + ex; }
            }
            var included = records.Where(record => record.Included).ToArray();
            if (included.Length == 0 || !included.SelectMany(record => record.PayloadIds).Any())
                throw new InvalidOperationException("No validated physical payload witness exists; the partial project was not saved or closed.");
            if (included.Select(record => record.ElementId).Distinct().Count() != included.Length)
                throw new InvalidOperationException("Partial selection has duplicate physical event identities.");
            foreach (var id in physical.Except(records.Select(record => record.ElementId)))
                records.Add(new PartialEvent { ElementId = id, SourceUid = specs[id].Uid, Reason = "Physical deletion absent from the actual range loader." });
            partialSelection = new PartialSelection { CapturedUtc = DateTime.UtcNow, FullDeletionEventCount = persisted.Count,
                FullDeletedPhysicalIds = deletedPhysicalIds, SelectedPhysicalIds = included.Select(record => record.ElementId).OrderBy(id => id).ToArray(),
                SelectedEventIds = included.Select(record => record.EventId).ToArray(), Records = records,
                ConversionStatus = status, FullConversionComplete = DurableConversionSucceeded(status) };
            Write("partial-subset.json", partialSelection);
            // No further converter or source-opening command can execute after
            // this point. The manual removal script does not coordinate readers.
            partialCapturePaused = true; durableConverting = false; durableAwaitingCloseCleanup = false;

            phase = "partial-save-deleted";
            doc.SaveAs(durableDeletedPath, new SaveAsOptions { OverwriteExistingFile = false });
            var closing = doc; int runtime = closing.GetHashCode(); ActivateBlank(application);
            bool closed = closing.Close(false), invalid = !closing.IsValidObject;
            bool absent = !application.Application.Documents.Cast<Document>().Any(candidate => candidate.GetHashCode() == runtime);
            if (!closed || !invalid || !absent) throw new InvalidOperationException("Partial deleted-state project did not actually close.");
            doc = null; Call(durableBridge, "Shutdown");
            var remaining = application.Application.Documents.Cast<Document>().Where(candidate => (bool)Call(native, "Owns", candidate)).Select(candidate => candidate.PathName).ToArray();
            bool disposed = Read(durableBridge, "Sources") == null && remaining.Length == 0;
            partialCloseEvidence = new { Closed = closed, OldDocumentInvalid = invalid, AbsentFromApplicationDocuments = absent, SourcesDisposed = disposed, RemainingHistoricalDocuments = remaining };
            Record("partial_sources_close_result", partialCloseEvidence);
            if (!disposed) throw new InvalidOperationException("Historical source documents remain open; manual bridge removal is forbidden.");
            partialAwaitingSessionClose = true; nextPartialUtc = DateTime.MinValue; phase = "partial-await-session-close";
        }

        void PartialIdle()
        {
            if (!partialAwaitingSessionClose || DateTime.UtcNow < nextPartialUtc) return;
            nextPartialUtc = DateTime.UtcNow.AddMilliseconds(500);
            if (!File.Exists(Path.Combine(folder, "partial-subset.json"))) return;
            if (config.BusinessWorkflow && (!automaticPreparationPaused || (bool?)Read(product.GetType("Analyse.HistoryBackgroundWork", true), "Paused") != true)) throw new InvalidOperationException("Automatic preparation resumed; frozen-source proof is refused.");
            if ((bool?)Read(ReadDurableStatus(), "Working") == true) return;
            var directory = Path.Combine(historyDirectory, "history-data", "sessions");
            if (!Directory.Exists(directory)) return;
            var sessions = Directory.GetFiles(directory, "*.json").Select(path => new { Path = path, Data = JsonFile(path) })
                .Where(item => item.Data.GetProperty("ProcessId").GetInt32() == Environment.ProcessId
                    && durableBridgePaths.Contains(item.Data.GetProperty("BridgeFile").GetString())).ToArray();
            if (sessions.Length != 1) throw new InvalidOperationException("Manual partial removal requires one exact owned bridge session.");
            var session = sessions[0]; if (!JsonTrue(session.Data, "Closed")) return;
            string bridge = session.Data.GetProperty("BridgeFile").GetString();
            if (!File.Exists(bridge)) throw new InvalidOperationException("Bridge vanished before manual removal proof; no attributable removal is asserted.");
            string initial = File.ReadAllText(Path.Combine(folder, "source-sha256.txt")).Trim();
            string source = File.ReadAllText(Path.Combine(folder, "source-path.txt")).Trim(); string copy = Path.Combine(folder, "Antibes-stress.rvt");
            string sourceHash = Sha(source), copyHash = Sha(copy);
            var proof = new { AtUtc = DateTime.UtcNow, CapturePid = Environment.ProcessId, ProjectClosed = true,
                OldDocumentInvalid = true, AbsentFromApplicationDocuments = true, SourcesDisposed = true, ConverterPaused = true, RequestsFrozen = true, AutomaticPreparationPaused = !config.BusinessWorkflow || automaticPreparationPaused,
                BridgeFile = bridge, BridgeSha256 = Sha(bridge), SessionId = session.Data.GetProperty("Id").GetString(),
                SessionManifestPath = session.Path, SessionManifestSha256 = Sha(session.Path),
                SourcePath = source, InitialCopyPath = copy, DeletedProject = durableDeletedPath, DeletedProjectSha256 = Sha(durableDeletedPath),
                SourceSha256Before = initial, SourceSha256After = sourceHash, InitialCopySha256 = copyHash,
                SourceAndInitialCopyUnchanged = initial == sourceHash && initial == copyHash,
                ObserverSha256 = Sha(GetType().Assembly.Location), ProductSha256 = Sha(product.Location),
                SubsetManifestSha256 = Sha(Path.Combine(folder, "partial-subset.json")), Status = ReadDurableStatus(),
                ManualTestBridgeRemoval = false, AutomaticCleanupVerified = false, FullConversionComplete = false, FullRestoreVerified = false };
            Write("partial-close-ready.json", proof); Record("partial_close_ready", proof);
            partialAwaitingSessionClose = false; phase = "partial-await-manual-bridge-removal";
        }

        void FinalizeSubset()
        {
            if (!partialCapturePaused || partialAwaitingSessionClose) throw new InvalidOperationException("Partial sources are not frozen and closed.");
            if (config.BusinessWorkflow && (!automaticPreparationPaused || (bool?)Read(product.GetType("Analyse.HistoryBackgroundWork", true), "Paused") != true)) throw new InvalidOperationException("Automatic preparation is not paused at finalization.");
            var proof = JsonFile(Path.Combine(folder, "partial-close-ready.json"));
            var removal = JsonFile(Path.Combine(folder, "partial-bridge-removal.json"));
            string bridge = proof.GetProperty("BridgeFile").GetString();
            if (!JsonTrue(removal, "Removed") || !JsonTrue(removal, "AbsentAfter") || !JsonTrue(removal, "ManualTestBridgeRemoval")
                || JsonTrue(removal, "AutomaticCleanupVerified") || File.Exists(bridge)
                || removal.GetProperty("BridgeFile").GetString() != bridge
                || removal.GetProperty("ProofSha256").GetString() != Sha(Path.Combine(folder, "partial-close-ready.json")))
                throw new InvalidOperationException("Guarded manual bridge removal has no matching immutable proof.");
            var result = new { AtUtc = DateTime.UtcNow, CapturePid = Environment.ProcessId, ProductSha256 = Sha(product.Location),
                DocumentKey = docKey, OpenedUtc = openedUtc, Transaction = transactionName,
                Closed = true, OldDocumentInvalid = true, AbsentFromApplicationDocuments = true, SessionClosed = true,
                ConversionComplete = false, ManualTestBridgeRemoval = true, AutomaticCleanupVerified = false, FullRestoreVerified = false,
                BridgeFiles = new[] { bridge }, DeletedBridgeFiles = new[] { bridge }, AllBridgeFilesAbsent = true,
                DeletedProject = durableDeletedPath, DeletedProjectSha256 = Sha(durableDeletedPath),
                SourceSha256Before = proof.GetProperty("SourceSha256Before").GetString(), SourceSha256After = proof.GetProperty("SourceSha256After").GetString(),
                InitialCopySha256 = proof.GetProperty("InitialCopySha256").GetString(), SourceAndInitialCopyUnchanged = JsonTrue(proof, "SourceAndInitialCopyUnchanged"),
                ExpectedDeletionEvents = partialSelection.FullDeletionEventCount, FullDeletedPhysicalCount = partialSelection.FullDeletedPhysicalIds.Length,
                SubsetPhysicalCount = partialSelection.SelectedPhysicalIds.Length, SubsetManifestSha256 = Sha(Path.Combine(folder, "partial-subset.json")),
                RemovalProofSha256 = Sha(Path.Combine(folder, "partial-bridge-removal.json")), Status = ReadDurableStatus() };
            Write("partial-session-closed.json", result); Write("finished.json", result);
            Record("partial_session_closed", result); finished = true; phase = "partial-capture-finished";
        }

        void LoadPartialSelection()
        {
            var closure = JsonFile(Path.Combine(evidenceDirectory, "partial-session-closed.json"));
            if (closure.GetProperty("SubsetManifestSha256").GetString() != Sha(Path.Combine(evidenceDirectory, "partial-subset.json")))
                throw new InvalidOperationException("Partial subset manifest differs from the frozen closure hash.");
            partialSelection = JsonSerializer.Deserialize<PartialSelection>(File.ReadAllText(Path.Combine(evidenceDirectory, "partial-subset.json")));
            var included = partialSelection.Records.Where(record => record.Included).ToArray();
            var physical = deletedPhysicalIds.ToHashSet();
            if (included.Length == 0 || included.Any(record => !physical.Contains(record.ElementId)
                || !specs.TryGetValue(record.ElementId, out var spec) || spec.Uid != record.SourceUid || string.IsNullOrWhiteSpace(record.EventId))
                || included.Select(record => record.ElementId).Distinct().Count() != included.Length
                || included.Select(record => record.EventId).Distinct().Count() != included.Length
                || !included.Select(record => record.ElementId).OrderBy(id => id).SequenceEqual(partialSelection.SelectedPhysicalIds.OrderBy(id => id))
                || !included.Select(record => record.EventId).OrderBy(id => id).SequenceEqual(partialSelection.SelectedEventIds.OrderBy(id => id)))
                throw new InvalidOperationException("Partial manifest identities are not an exact injective subset of the physical baseline.");
            fullDeletedPhysicalIds = deletedPhysicalIds.ToArray();
            if (!fullDeletedPhysicalIds.OrderBy(id => id).SequenceEqual(partialSelection.FullDeletedPhysicalIds.OrderBy(id => id)))
                throw new InvalidOperationException("Partial selection changed the original physical denominator.");
            deletedPhysicalIds = partialSelection.SelectedPhysicalIds;
            foreach (var id in partialSelection.SelectedEventIds) partialRestoreEventIds.Add(id);
            persisted.Clear(); foreach (var record in partialSelection.Records.Where(record => record.Included)) persisted.Add(record.ElementId, default);
            if (partialRestoreEventIds.Count == 0 || persisted.Count != partialRestoreEventIds.Count || deletedPhysicalIds.Length != persisted.Count)
                throw new InvalidOperationException("Partial manifest is empty or inconsistent.");
        }

        List<object> FilterPartialEvents(List<object> events)
        {
            if (!config.AllowPartialDurableRestore) return events;
            var selected = events.Where(ev => partialRestoreEventIds.Contains((string)Call(durableBridge, "EventId", ev))).ToList();
            if (selected.Count != partialRestoreEventIds.Count) throw new InvalidOperationException("Actual range loader did not return the exact partial event set.");
            foreach (var ev in selected)
            {
                var converted = ResolveConverted(ev);
                var record = partialSelection.Records.Single(item => item.Included && item.EventId == (string)Call(durableBridge, "EventId", ev));
                if (Read(converted, "Recipe") == null || Convert.ToInt64(Read(ev, "ElementId")) != record.ElementId
                    || (string)Read(converted, "SourceUniqueId") != record.SourceUid)
                    throw new InvalidOperationException("Selected conversion is unavailable or mismatched; raw fallback is forbidden.");
            }
            return selected;
        }
    }
}

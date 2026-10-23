using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        Type durableBridge;
        bool durableConverting, durableAwaitingCloseCleanup, durableConversionComplete, durableJournalRequiresBridge;
        int durableWaves;
        DateTime nextDurableUtc;
        object durableStatus, durableCloseEvidence;
        readonly HashSet<string> durableBridgePaths = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> durableDeletedBridgePaths = new(StringComparer.OrdinalIgnoreCase);
        string durableDeletedPath, historyDirectory, evidenceDirectory;
        bool IsDurable => config?.Scenario == "durable";
        bool IsDurableRestore => IsDurable && config.DurablePhase == "restore";
        bool IsDurableCapture => IsDurable && !IsDurableRestore;

        public sealed class DurableInventory
        {
            public List<Spec> Specs { get; set; } = new();
            public List<CatalogEntry> Catalog { get; set; } = new();
            public List<LogicalGroupRoot> LogicalGroupRoots { get; set; } = new();
            public long[] SelectedRootIds { get; set; } = Array.Empty<long>();
            public long[] PhysicalEligibleIds { get; set; } = Array.Empty<long>();
            public long[] ExplicitlyExcludedPhysicalIds { get; set; } = Array.Empty<long>();
            public List<TypeDefinition> TypeDefinitions { get; set; } = new();
        }

        static IEnumerable<string> Strings(object value) => value is IEnumerable values
            ? values.Cast<object>().Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item))
            : Enumerable.Empty<string>();
        static JsonElement JsonFile(string path) { using var parsed = JsonDocument.Parse(File.ReadAllText(path)); return parsed.RootElement.Clone(); }
        static bool JsonTrue(JsonElement value, string name) => value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.True;
        static long[] JsonIds(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().Select(id => id.GetInt64()).ToArray();

        void ConfigureDurablePaths()
        {
            evidenceDirectory = IsDurableRestore ? Path.GetFullPath(config.DurableCaptureDirectory) : folder;
            var runsRoot = Path.GetFullPath(Path.Combine(folder, "..")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!evidenceDirectory.StartsWith(runsRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Durable evidence must stay inside the stress-run workspace.");
            if (IsDurableRestore && string.Equals(evidenceDirectory, folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The restore phase requires a fresh run directory and process.");
            durableDeletedPath = Path.Combine(config.IsolatedRestoreWorkspace ? folder : evidenceDirectory, "Antibes-supprime.rvt");
            historyDirectory = Path.Combine(config.IsolatedRestoreWorkspace ? folder : evidenceDirectory, "history");
            if (IsDurableRestore) modelPath = durableDeletedPath;
        }

        void LoadDurableBaseline()
        {
            string closureName = config.AllowPartialDurableRestore ? "partial-session-closed.json" : "durable-session-closed.json";
            var closure = JsonFile(Path.Combine(evidenceDirectory, closureName));
            if (!JsonTrue(closure, "SessionClosed") || !JsonTrue(closure, "AllBridgeFilesAbsent")
                || !JsonTrue(closure, "Closed") || !JsonTrue(closure, "OldDocumentInvalid")
                || !JsonTrue(closure, "AbsentFromApplicationDocuments") || !JsonTrue(closure, "SourceAndInitialCopyUnchanged"))
                throw new InvalidOperationException("Capture phase has no complete conversion/close/bridge-removal proof.");
            if (config.AllowPartialDurableRestore)
            {
                if (!JsonTrue(closure, "ManualTestBridgeRemoval") || JsonTrue(closure, "AutomaticCleanupVerified")
                    || closure.GetProperty("SubsetManifestSha256").GetString() != Sha(Path.Combine(evidenceDirectory, "partial-subset.json")))
                    throw new InvalidOperationException("Partial closure has no matching explicit subset/manual-removal evidence.");
            }
            else if (!JsonTrue(closure, "ConversionComplete")) throw new InvalidOperationException("Full durable phase requires complete conversion.");
            int capturePid = closure.GetProperty("CapturePid").GetInt32();
            if (capturePid == Environment.ProcessId) throw new InvalidOperationException("Durable restoration must run in a distinct Revit process.");
            if (Safe(() => !Process.GetProcessById(capturePid).HasExited))
                throw new InvalidOperationException("The capture process is still alive; stop it before phase two.");
            foreach (var path in closure.GetProperty("BridgeFiles").EnumerateArray().Select(item => item.GetString()))
                if (!string.IsNullOrWhiteSpace(path)) durableBridgePaths.Add(path);
            foreach (var path in closure.GetProperty("DeletedBridgeFiles").EnumerateArray().Select(item => item.GetString()))
                if (!string.IsNullOrWhiteSpace(path)) durableDeletedBridgePaths.Add(path);
            if (durableBridgePaths.Any(File.Exists)) throw new InvalidOperationException("A temporary opening bridge still exists before phase two.");
            if (!File.Exists(modelPath) || Sha(modelPath) != closure.GetProperty("DeletedProjectSha256").GetString())
                throw new InvalidOperationException("Deleted-state project differs from the closed capture checkpoint.");
            var reference = JsonSerializer.Deserialize<DurableInventory>(File.ReadAllText(Path.Combine(evidenceDirectory, "inventory.json")));
            foreach (var spec in reference.Specs) specs.Add(spec.Id, spec);
            foreach (var entry in reference.Catalog) catalog.Add(entry.Id, entry);
            foreach (var definition in reference.TypeDefinitions) if (definition != null) sourceTypeDefinitions.Add(definition.Uid, definition);
            logicalGroupRoots.AddRange(reference.LogicalGroupRoots); selectedRoots = reference.SelectedRootIds;
            var deletion = JsonFile(Path.Combine(evidenceDirectory, "deletion.json"));
            selectedPhysicalIds = JsonIds(deletion, "SelectedPhysicalIds"); deletedPhysicalIds = JsonIds(deletion, "DeletedPhysicalIds");
            physicalCandidateIds = reference.PhysicalEligibleIds.Length > 0 ? reference.PhysicalEligibleIds : selectedPhysicalIds;
            explicitlyExcludedPhysicalIds = reference.ExplicitlyExcludedPhysicalIds;
            changedIds = JsonIds(deletion, "DocumentChangedDeletedIds"); returnedIds = JsonIds(deletion, "ReturnedIds");
            transactionName = deletion.GetProperty("Transaction").GetString();
            var journal = JsonFile(Path.Combine(evidenceDirectory, "journal-summary.json"));
            foreach (var id in JsonIds(journal, "EventIds")) persisted.Add(id, default);
            foreach (var batch in journal.GetProperty("DeletionBatchIds").EnumerateArray()) batchIds.Add(batch.GetString());
            if (JsonIds(journal, "MissingChangedIds").Length != 0 || changedIds.Except(persisted.Keys).Any())
                throw new InvalidOperationException("Capture journal was incomplete; phase two cannot hide missing deletion events.");
            docKey = closure.GetProperty("DocumentKey").GetString();
            openedUtc = closure.GetProperty("OpenedUtc").GetDateTime();
            if (config.AllowPartialDurableRestore) LoadPartialSelection();
            if (config.DiagnosticPhysicalIds.Length > 0) LoadDiagnosticSelection();
            deleted = true; journalReady = true;
            Write("durable-phase-two-baseline.json", new
            {
                AtUtc = DateTime.UtcNow, CaptureDirectory = evidenceDirectory, CapturePid = capturePid,
                RestorePid = Environment.ProcessId, DistinctProcess = true, CaptureProcessExited = true,
                InventorySha256 = Sha(Path.Combine(evidenceDirectory, "inventory.json")),
                DeletionSha256 = Sha(Path.Combine(evidenceDirectory, "deletion.json")),
                ClosureSha256 = Sha(Path.Combine(evidenceDirectory, closureName)),
                DeletedProject = modelPath, DeletedProjectSha256 = Sha(modelPath),
                HistoryDirectory = historyDirectory, BridgeFiles = durableBridgePaths.ToArray(),
                AllBridgeFilesAbsent = true, SelectedPhysical = selectedPhysicalIds.Length,
                DeletedPhysical = deletedPhysicalIds.Length, ExpectedDeletionEvents = persisted.Count,
                DiagnosticPhysicalIds = config.DiagnosticPhysicalIds, IsolatedRestoreWorkspace = config.IsolatedRestoreWorkspace, OriginalSubsetPhysicalIds = diagnosticOriginalSubsetIds, LaneOnly = config.DiagnosticPhysicalIds.Length > 0, PartialOnly = config.AllowPartialDurableRestore, FullDeletedPhysicalCount = config.AllowPartialDurableRestore ? fullDeletedPhysicalIds.Length : deletedPhysicalIds.Length,
                FullDeletionEventCount = config.AllowPartialDurableRestore ? partialSelection.FullDeletionEventCount : persisted.Count,
                ManualTestBridgeRemoval = config.AllowPartialDurableRestore, AutomaticCleanupVerified = !config.AllowPartialDurableRestore,
                FullRestorationVerified = false
            });
        }

        object ReadDurableStatus()
        {
            durableBridge ??= product.GetType("Analyse.ElementHistoryDurableBridge", true);
            var status = Call(durableBridge, "GetStatus");
            foreach (var name in new[] { "PendingJobs", "ConvertedEvents", "FailedJobs", "Failures", "BridgeFiles", "DeletedBridgeFiles", "Complete", "SessionClosed", "Working" })
                if (status == null || status.GetType().GetProperty(name, Flags) == null && status.GetType().GetField(name, Flags) == null)
                    throw new MissingFieldException("Durable status is missing required evidence: " + name);
            foreach (var path in Strings(Read(status, "BridgeFiles"))) durableBridgePaths.Add(Path.GetFullPath(path));
            foreach (var path in Strings(Read(status, "DeletedBridgeFiles"))) durableDeletedBridgePaths.Add(Path.GetFullPath(path));
            durableStatus = new
            {
                PendingJobs = Convert.ToInt32(Read(status, "PendingJobs")),
                ConvertedEvents = Convert.ToInt32(Read(status, "ConvertedEvents")),
                FailedJobs = Convert.ToInt32(Read(status, "FailedJobs")),
                Failures = Strings(Read(status, "Failures")).ToArray(),
                BridgeFiles = Strings(Read(status, "BridgeFiles")).ToArray(),
                DeletedBridgeFiles = Strings(Read(status, "DeletedBridgeFiles")).ToArray(),
                Complete = (bool?)Read(status, "Complete") == true,
                SessionClosed = (bool?)Read(status, "SessionClosed") == true,
                Working = (bool?)Read(status, "Working") == true
            };
            return durableStatus;
        }

        bool DurableConversionSucceeded(object status) => (bool?)Read(status, "Complete") == true
            && Convert.ToInt32(Read(status, "PendingJobs")) == 0 && Convert.ToInt32(Read(status, "FailedJobs")) == 0
            && !Strings(Read(status, "Failures")).Any();

        bool ObserveDurableCompletion(object status)
        {
            if (!DurableConversionSucceeded(status)) return false;
            // A service can be idle because it received no usable recipe at all.
            // That must never become a successful conversion of this deletion batch.
            if (persisted.Count == 0 || Convert.ToInt32(Read(status, "ConvertedEvents")) < persisted.Count
                || durableJournalRequiresBridge && durableBridgePaths.Count == 0) return false;
            if (!durableAwaitingCloseCleanup)
            {
                durableConverting = false; durableConversionComplete = true; phase = "durable-await-persist-deleted";
                Write("durable-conversion.json", new { AtUtc = DateTime.UtcNow, Pid = Environment.ProcessId, Complete = true, ExpectedEvents = persisted.Count, Status = status, Waves = durableWaves, BridgeFiles = durableBridgePaths.ToArray(), Note = "Conversion completed while the project session was open; bridge removal is checked only after actual closure." });
                Record("durable_conversion_complete", status); return true;
            }
            if ((bool?)Read(status, "SessionClosed") == true && durableBridgePaths.Count > 0
                && durableBridgePaths.All(path => !File.Exists(path)) && durableBridgePaths.All(durableDeletedBridgePaths.Contains))
            { CompleteDurableCapture(status); return true; }
            return false;
        }

        void CaptureRawBridgePaths()
        {
            foreach (var ev in persisted.Values)
            {
                if (ev.ValueKind != JsonValueKind.Object || !ev.TryGetProperty("Delta", out var delta)
                    || !delta.TryGetProperty("recipe", out var recipe) || recipe.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in new[] { "Native", "OpeningFallback" })
                    if (recipe.TryGetProperty(field, out var artifact) && artifact.ValueKind == JsonValueKind.Object
                        && JsonTrue(artifact, "WholeModel"))
                    {
                        durableJournalRequiresBridge = true;
                        if (artifact.TryGetProperty("File", out var file) && file.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(file.GetString()))
                            durableBridgePaths.Add(Path.GetFullPath(file.GetString()));
                    }
            }
        }

        void BeginDurableConversion()
        {
            if (config.BusinessWorkflow) throw new InvalidOperationException("Business workflow observes automatic preparation only; force-convert is forbidden.");
            if (!IsDurableCapture || !deleted || !journalReady || doc == null || !doc.IsValidObject)
                throw new InvalidOperationException("Durable conversion requires the deleted capture copy and a naturally persisted journal.");
            CaptureRawBridgePaths(); durableConverting = true; nextDurableUtc = DateTime.MinValue;
            phase = "durable-conversion";
            Record("durable_conversion_armed", new { ExpectedEvents = persisted.Count, KnownBridgeFiles = durableBridgePaths.ToArray(), Status = ReadDurableStatus() });
        }

        // One explicit production conversion wave per real Idling callback. Never
        // wait or repeat the batch loop inside a Revit callback.
        void DurableIdle(UIApplication application)
        {
            if (config.BusinessWorkflow) { BusinessDurableCompletionIdle(); return; }
            if (!IsDurableCapture || finished || partialCapturePaused || (!durableConverting && !durableAwaitingCloseCleanup)
                || DateTime.UtcNow < nextDurableUtc) return;
            nextDurableUtc = DateTime.UtcNow.AddMilliseconds(500);
            try
            {
                var before = ReadDurableStatus();
                // Observe a completed IO wave before starting another cleanup wave.
                if (ObserveDurableCompletion(before)) return;
                var watch = Stopwatch.StartNew();
                Call(durableBridge, "ProcessPending", application.Application, true);
                var after = ReadDurableStatus(); durableWaves++;
                Record("durable_conversion_wave", new { Wave = durableWaves, Milliseconds = watch.Elapsed.TotalMilliseconds, Before = before, After = after });
                Write("durable-status.json", new { AtUtc = DateTime.UtcNow, Pid = Environment.ProcessId, Waves = durableWaves, Status = after, ExpectedDeletionEvents = persisted.Count,
                    ConvertedCoverageComplete = persisted.Count > 0 && Convert.ToInt32(Read(after, "ConvertedEvents")) >= persisted.Count,
                    BridgeEvidenceRequired = durableJournalRequiresBridge, KnownBridgeFiles = durableBridgePaths.ToArray(), KnownDeletedBridgeFiles = durableDeletedBridgePaths.ToArray() });
                ObserveDurableCompletion(after);
            }
            catch (Exception ex)
            {
                durableConverting = false; durableAwaitingCloseCleanup = false; phase = "durable-error";
                Record("durable_conversion_error", ex.ToString());
                Write("durable-error.json", new { AtUtc = DateTime.UtcNow, Error = ex.ToString(), Waves = durableWaves, Status = durableStatus });
            }
        }

        void PersistDeleted(UIApplication application)
        {
            if (!IsDurableCapture || !durableConversionComplete || !DurableConversionSucceeded(ReadDurableStatus()))
                throw new InvalidOperationException("Complete conversion without failed jobs is required before saving the deleted-state project.");
            if (lastResult != null) throw new InvalidOperationException("The deleted-state checkpoint must precede any restoration.");
            phase = "durable-save-deleted"; var watch = Stopwatch.StartNew();
            doc.SaveAs(durableDeletedPath, new SaveAsOptions { OverwriteExistingFile = false });
            Record("deleted_copy_saved", new { Path = durableDeletedPath, Milliseconds = watch.Elapsed.TotalMilliseconds });
            var saved = doc; int runtime = saved.GetHashCode(); ActivateBlank(application);
            bool closed = saved.Close(false), invalid = !saved.IsValidObject;
            bool absent = !application.Application.Documents.Cast<Document>().Any(candidate => candidate.GetHashCode() == runtime);
            durableCloseEvidence = new { Closed = closed, OldDocumentInvalid = invalid, AbsentFromApplicationDocuments = absent, RuntimeDocumentId = runtime };
            Write("durable-deleted-close.json", durableCloseEvidence); Record("durable_deleted_close_result", durableCloseEvidence);
            if (!closed || !invalid || !absent) throw new InvalidOperationException("Deleted project did not actually close; bridge cleanup is unproven.");
            doc = null; durableConverting = false; durableAwaitingCloseCleanup = true; nextDurableUtc = DateTime.MinValue;
            phase = "durable-await-bridge-removal";
        }

        void CompleteDurableCapture(object status)
        {
            var initial = File.ReadAllText(Path.Combine(folder, "source-sha256.txt")).Trim();
            var sourcePath = File.ReadAllText(Path.Combine(folder, "source-path.txt")).Trim();
            var sourceHash = Sha(sourcePath); var initialCopyHash = Sha(Path.Combine(folder, "Antibes-stress.rvt"));
            var proof = new
            {
                AtUtc = DateTime.UtcNow, CapturePid = Environment.ProcessId, ProductSha256 = Sha(product.Location),
                DocumentKey = docKey, OpenedUtc = openedUtc, Transaction = transactionName,
                Closed = (bool?)Read(durableCloseEvidence, "Closed") == true,
                OldDocumentInvalid = (bool?)Read(durableCloseEvidence, "OldDocumentInvalid") == true,
                AbsentFromApplicationDocuments = (bool?)Read(durableCloseEvidence, "AbsentFromApplicationDocuments") == true,
                SessionClosed = true, ConversionComplete = true, Status = status,
                BridgeFiles = durableBridgePaths.ToArray(), DeletedBridgeFiles = durableDeletedBridgePaths.ToArray(),
                AllBridgeFilesAbsent = durableBridgePaths.All(path => !File.Exists(path)),
                DeletedProject = durableDeletedPath, DeletedProjectSha256 = Sha(durableDeletedPath),
                SourcePath = sourcePath, SourceSha256Before = initial, SourceSha256After = sourceHash,
                InitialCopy = Path.Combine(folder, "Antibes-stress.rvt"), InitialCopySha256 = initialCopyHash,
                SourceAndInitialCopyUnchanged = initial == sourceHash && initial == initialCopyHash,
                ExpectedDeletionEvents = persisted.Count, Waves = durableWaves,
                Note = "Capture phase only. Close this process before starting the distinct restore phase; no restoration or persistence fidelity is asserted here."
            };
            Write("durable-session-closed.json", proof); Record("durable_session_closed", proof);
            tracker.GetField("_captureDetailedDeletedMesh", Flags).SetValue(null, originalDetailed);
            durableAwaitingCloseCleanup = false; phase = "durable-capture-finished"; finished = true;
            Write("finished.json", proof);
        }

        void DurableDiagnostics()
        {
            if (!IsDurable) throw new InvalidOperationException("Durability diagnostics require Scenario=durable.");
            var files = Directory.Exists(Path.Combine(historyDirectory, "history-data"))
                ? Directory.GetFiles(Path.Combine(historyDirectory, "history-data"), "*", SearchOption.AllDirectories) : Array.Empty<string>();
            Write("durable-diagnostics.json", new { AtUtc = DateTime.UtcNow, Phase = config.DurablePhase, Pid = Environment.ProcessId,
                Status = ReadDurableStatus(), Files = files.Select(path => new { Path = path, Bytes = new FileInfo(path).Length, Sha256 = Sha(path) }).ToArray(),
                BridgeFiles = durableBridgePaths.Select(path => new { Path = path, Exists = File.Exists(path) }).ToArray(),
                Note = "Read-only file/status inventory. File presence or conversion counters alone do not establish restored-model fidelity." });
        }

        void ValidateDurableRestoreRequests(IList requestsToRestore, IEnumerable<object> events)
        {
            if (!IsDurableRestore) return;
            var eventIds = events.Select(ev => Convert.ToInt64(Read(ev, "ElementId"))).ToArray();
            var identities = requestsToRestore.Cast<object>().Select(request => new
            {
                SourceUid = Read(request, "SourceUniqueId"), Recipe = Read(request, "Recipe"),
                Native = Read(Read(request, "Recipe"), "Native"), Backup = Read(Read(request, "Recipe"), "OpeningFallback")
            }).ToArray();
            durableTypeReceipts.Clear();
            foreach (var identity in identities)
                foreach (var artifact in ArtifactsOf(identity.Recipe))
                    durableTypeReceipts.Add(new DurableTypeReceipt { SourceUid = identity.SourceUid?.ToString(),
                        RootSourceUid = Read(artifact, "RootSourceUniqueId")?.ToString(),
                        PayloadId = Read(artifact, "PayloadId")?.ToString(), TypeVersion = Read(artifact, "TypeVersion")?.ToString() });
            var artifacts = identities.SelectMany(item => ArtifactsOf(item.Recipe)).ToArray();
            var bridgeReferences = artifacts.Where(artifact => (bool?)Read(artifact, "WholeModel") == true
                || Read(artifact, "File") is string path && durableBridgePaths.Contains(path)).ToArray();
            bool coverage = eventIds.Length == persisted.Count && eventIds.Distinct().Count() == persisted.Count
                && !persisted.Keys.Except(eventIds).Any() && requestsToRestore.Count == eventIds.Length;
            bool absent = durableBridgePaths.Count > 0 && durableBridgePaths.All(path => !File.Exists(path));
            Write("durable-restoration-inputs.json", new
            {
                AtUtc = DateTime.UtcNow, CaptureDirectory = evidenceDirectory, RestorePid = Environment.ProcessId,
                ExpectedEvents = persisted.Count, LoadedEvents = eventIds.Length, Requests = requestsToRestore.Count,
                DiagnosticPhysicalIds = config.DiagnosticPhysicalIds, IsolatedRestoreWorkspace = config.IsolatedRestoreWorkspace, OriginalSubsetPhysicalIds = diagnosticOriginalSubsetIds, LaneOnly = config.DiagnosticPhysicalIds.Length > 0, PartialOnly = config.AllowPartialDurableRestore, FullDeletedPhysicalCount = config.AllowPartialDurableRestore ? fullDeletedPhysicalIds.Length : deletedPhysicalIds.Length,
                FullDeletionEventCount = config.AllowPartialDurableRestore ? partialSelection.FullDeletionEventCount : persisted.Count,
                ExactEventCoverage = coverage, AllBridgeFilesAbsent = absent, WholeModelOrBridgeReferences = bridgeReferences.Length,
                RecipeKinds = identities.GroupBy(item => (string)Read(item.Recipe, "Kind") ?? "(none)").ToDictionary(group => group.Key, group => group.Count()),
                Artifacts = artifacts.Select(artifact => new { File = Read(artifact, "File"), WholeModel = Read(artifact, "WholeModel"), PayloadId = Read(artifact, "PayloadId"), TypeVersion = Read(artifact, "TypeVersion"), RootSourceUniqueId = Read(artifact, "RootSourceUniqueId") }).ToArray(),
                RequestsWithoutRecipe = identities.Count(item => item.Recipe == null),
                Note = "Requests come from the real range loader and UI ToRestoreRequest after process restart. Null recipes remain visible as failures; temporary WholeModel receipts are forbidden."
            });
            if (!coverage || !absent || bridgeReferences.Length != 0)
                throw new InvalidOperationException("Durable inputs are incomplete or still depend on a temporary opening bridge; Restore was not called.");
        }
    }
}

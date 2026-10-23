using Autodesk.Revit.DB;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        readonly HashSet<long> rapidIds = new();
        readonly List<FileStream> rapidSourceLocks = new();
        readonly List<object> rapidEdits = new();
        readonly Dictionary<string, object> rapidRestoreRequests = new(StringComparer.Ordinal);
        readonly HashSet<string> rapidInitialArchiveFiles = new(StringComparer.OrdinalIgnoreCase);
        Type rapidBackground, rapidClock;
        bool rapidOriginalPaused, rapidEdited, rapidDeleteComplete, rapidConvertQueued, rapidPersistQueued, rapidFailed;
        int rapidTimeSetup, rapidPreloadCount, rapidCallbackSequence, rapidEditCallbackSequence;
        DateTime rapidEditsCompletedUtc, rapidEffectiveUtc;
        object[] rapidReadinessEvents;

        void InitializeRapidCycle()
        {
            if (!config.RapidModifiedDelete) return;
            if (!IsDurable || config.BusinessWorkflow || HasDelayedDeletion || config.AllowPartialDurableRestore)
                throw new InvalidOperationException("Rapid cycle requires an isolated full durable run without another workflow.");
            if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") != "1")
                throw new InvalidOperationException("Rapid cycle requires the isolated manual-test process guard.");
            rapidBackground = product.GetType("Analyse.HistoryBackgroundWork", true);
            rapidClock = product.GetType("Analyse.ElementHistoryClock", true);
            rapidOriginalPaused = (bool)Read(rapidBackground, "Paused");
            rapidBackground.GetField("Paused", Flags).SetValue(null, true);
            tracker.GetField("_captureDetailedDeletedMesh", Flags).SetValue(null, false);
            foreach (var path in RapidArchiveFiles()) rapidInitialArchiveFiles.Add(path);
            Write("rapid-storage-before.json", new { AtUtc = DateTime.UtcNow, UsesExistingModelDefinitions = true,
                Files = RapidArchiveFiles().Select(RapidFileEvidence).ToArray() });
            if (!IsDurableRestore) return;
            var selected = JsonFile(Path.Combine(evidenceDirectory, "rapid-edits.json"));
            foreach (var id in JsonIds(selected, "ModifiedIds")) rapidIds.Add(id);
            if (rapidIds.Count != 3 || !rapidIds.SetEquals(selectedPhysicalIds))
                throw new InvalidOperationException("Rapid restore requires the exact three edited witnesses from capture.");
            foreach (var path in new[] { File.ReadAllText(Path.Combine(evidenceDirectory, "source-path.txt")).Trim(),
                Path.Combine(evidenceDirectory, "Antibes-stress.rvt") }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // These are the explicitly prepared test source/copy, never the
                // user's active document. Read-only exclusive handles make any
                // accidental source fallback fail throughout restore + reopen.
                rapidSourceLocks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));
            }
            Write("rapid-source-denial.json", new { AtUtc = DateTime.UtcNow, CaptureProcessStopped = true,
                Sources = rapidSourceLocks.Select(stream => new { Path = stream.Name, ExclusiveReadHandleHeld = true,
                    IndependentReadDenied = ReadIsDenied(stream.Name) }).ToArray(),
                OldOpeningBridges = durableBridgePaths.Select(path => new { Path = path, Exists = File.Exists(path) }).ToArray(),
                Note = "Exclusive read-only locks remain held through real restoration, SaveAs and restored-file reopening; original source bytes are unchanged." });
            if (rapidSourceLocks.Any(stream => !ReadIsDenied(stream.Name)))
                throw new InvalidOperationException("A prepared source remains accessible despite the denial guard.");
        }

        static bool ReadIsDenied(string path)
        {
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return false; }
            catch (IOException) { return true; }
        }

        void ReleaseRapidSources()
        {
            foreach (var stream in rapidSourceLocks) stream.Dispose();
            rapidSourceLocks.Clear();
            rapidBackground?.GetField("Paused", Flags)?.SetValue(null, rapidOriginalPaused);
            if (rapidClock != null) Call(rapidClock, "SetTestUtcNow", (object)null);
        }

        int FreeMepEnd(MEPCurve candidate)
        {
            if (candidate == null || candidate.Pinned || candidate.GroupId != ElementId.InvalidElementId
                || !WritableComment(candidate) || candidate.Location is not LocationCurve location
                || location.Curve is not Line || location.Curve.Length < 1 || Protection(candidate) != null) return -1;
            for (int end = 0; end < 2; end++)
                if (Connectors(candidate).Any(port => port.ConnectorType == ConnectorType.End
                    && port.Origin.DistanceTo(location.Curve.GetEndPoint(end)) <= Tolerance
                    && Safe(() => !port.IsConnected, false))) return end;
            return -1;
        }

        int FreeRapidWallEnd(Wall wall)
        {
            if (wall == null || wall.Pinned || wall.GroupId != ElementId.InvalidElementId || !WritableComment(wall)
                || Protection(wall) != null || wall.Location is not LocationCurve location || location.Curve is not Line
                || location.Curve.Length < 1 || wall.FindInserts(true, true, true, true).Count != 0) return -1;
            for (int end = 0; end < 2; end++) if (location.get_ElementsAtJoin(end).Size == 0) return end;
            return -1;
        }

        bool RapidFamilyCandidate(FamilyInstance instance) => instance != null && !instance.Pinned
            && instance.Location is LocationPoint && instance.GroupId == ElementId.InvalidElementId
            && instance.SuperComponent == null && (instance.Host == null || instance.Host is Level)
            && Connectors(instance).Count == 0 && WritableComment(instance) && Protection(instance) == null;

        void EditRapidObjects(Document document)
        {
            if (!config.RapidModifiedDelete || !IsDurableCapture || rapidEdited || deleted
                || (bool?)Read(rapidBackground, "Paused") != true)
                throw new InvalidOperationException("Rapid edit is not armed with deferred preparation suspended.");
            AuditRapidExternalModels(document);
            var family = document.GetElement(new ElementId(474717L)) as FamilyInstance;
            if (!RapidFamilyCandidate(family)) family = new FilteredElementCollector(document).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().Where(RapidFamilyCandidate).OrderBy(e => e.Id.Value).FirstOrDefault();
            var wall = document.GetElement(new ElementId(556835L)) as Wall;
            if (FreeRapidWallEnd(wall) < 0) wall = new FilteredElementCollector(document).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(e => FreeRapidWallEnd(e) >= 0).OrderBy(e => e.Id.Value).FirstOrDefault();
            var mep = document.GetElement(new ElementId(913245L)) as MEPCurve;
            if (FreeMepEnd(mep) < 0) mep = new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements()
                .OfType<MEPCurve>().Where(e => e is Autodesk.Revit.DB.Plumbing.Pipe || e is Autodesk.Revit.DB.Mechanical.Duct)
                .Where(e => FreeMepEnd(e) >= 0).OrderBy(e => e.Id.Value).FirstOrDefault();
            if (family == null || wall == null || mep == null)
                throw new InvalidOperationException("Rapid cycle requires all three real witnesses: independent family, free straight wall and free-ended straight pipe/duct. Missing witnesses cannot be skipped.");
            foreach (var element in new Element[] { family, wall, mep }) rapidIds.Add(element.Id.Value);
            rapidPreloadCount = Convert.ToInt32(Read(Read(State(document), "Preload"), "Captured") ?? 0);
            // Independent baseline happens BEFORE any edit. It never invokes a
            // production prime/capture API and background preparation is paused.
            Inventory(document);
            double delta = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
            var wantedPoint = ((LocationPoint)family.Location).Point + new XYZ(delta, 0, 0);
            RapidEditTransaction(document, "family", family,
                () => ElementTransformUtils.MoveElement(document, family.Id, new XYZ(delta, 0, 0)),
                actual => actual.Location is LocationPoint point && point.Point.DistanceTo(wantedPoint) <= Tolerance);
            ShortenRapidCurve(document, "wall", wall, FreeRapidWallEnd(wall), delta);
            ShortenRapidCurve(document, "mep", mep, FreeMepEnd(mep), delta);
            rapidEdited = true; rapidEditsCompletedUtc = DateTime.UtcNow; rapidEditCallbackSequence = rapidCallbackSequence;
            CaptureSourceTypeDefinitions(document);
            Write("inventory.json", new { CapturedUtc = rapidEditsCompletedUtc, Catalog = catalog.Values.ToArray(),
                Specs = specs.Values.ToArray(), PhysicalEligibleIds = physicalCandidateIds,
                ExplicitlyExcludedPhysicalIds = explicitlyExcludedPhysicalIds, SelectedRootIds = selectedRoots,
                LogicalGroupRoots = logicalGroupRoots.ToArray(), TypeDefinitions = sourceTypeDefinitions.Values.ToArray(),
                Note = "Independent pre-edit full physical baseline, with the three actual modified states reread after committed edits; no production capture was requested by the observer." });
            Write("rapid-edits.json", new { AtUtc = rapidEditsCompletedUtc, ModifiedIds = rapidIds.OrderBy(id => id).ToArray(),
                EditCallbackSequence = rapidEditCallbackSequence, Edits = rapidEdits.ToArray(), State = State(document),
                BackgroundPreparationPaused = true, ForcedPreparation = false, RequiredWitnessCount = 3,
                UsesExistingModelDefinitions = true });
            deleteQueued = true; actions.Enqueue("delete"); phase = "rapid-delete-next-callback";
        }

        void AuditRapidExternalModels(Document document)
        {
            var exclusion = product.GetType("Analyse.ElementHistoryExternalModels", true);
            var opening = product.GetType("Analyse.ElementHistoryOpeningArchive", true);
            var records = new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements()
                .Concat(new FilteredElementCollector(document).WhereElementIsElementType().ToElements())
                .Where(element => element is ImportInstance || element is RevitLinkInstance
                    || element is CADLinkType || element is RevitLinkType)
                .Select(element => new { Id = element.Id.Value, Uid = element.UniqueId,
                    Class = element.GetType().FullName, Name = element.Name,
                    Excluded = (bool)Call(exclusion, "IsExcluded", element),
                    OpeningObjectEligible = (bool)Call(opening, "IsEligibleElement", element) }).ToArray();
            bool passed = records.Length > 0 && records.All(record => record.Excluded && !record.OpeningObjectEligible);
            Write("rapid-external-model-exclusions.json", new { AtUtc = DateTime.UtcNow, Passed = passed,
                Records = records, Note = "Native API reads of real external import/link instances and types. No external object is added to the deletion targets; the opening RVT file remains an allowed safety support." });
            if (!passed) throw new InvalidOperationException("External import/link objects must be excluded from the restoration perimeter.");
        }

        void ShortenRapidCurve(Document document, string kind, Element element, int freeEnd, double delta)
        {
            if (freeEnd < 0 || element.Location is not LocationCurve location)
                throw new InvalidOperationException("Witness lost its free editable end: " + kind);
            var points = new[] { location.Curve.GetEndPoint(0), location.Curve.GetEndPoint(1) };
            points[freeEnd] += (points[1 - freeEnd] - points[freeEnd]).Normalize() * delta;
            RapidEditTransaction(document, kind, element, () => location.Curve = Line.CreateBound(points[0], points[1]),
                actual => actual.Location is LocationCurve current && Same(P(current.Curve.GetEndPoint(0)), P(points[0]))
                    && Same(P(current.Curve.GetEndPoint(1)), P(points[1])));
        }

        void RapidEditTransaction(Document document, string kind, Element element, Action edit, Func<Element, bool> verify)
        {
            var before = Capture(element); var timer = Stopwatch.StartNew(); string uid = element.UniqueId;
            string comment = "BIMaestro rapid " + config.RunName + " " + kind;
            using var transaction = new Transaction(document, "BIMaestroStress " + config.RunName + " rapid edit " + kind);
            transaction.Start(); var failures = new DeleteFailures();
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            edit(); if (!BusinessComment(element).Set(comment)) throw new InvalidOperationException("Rapid comment edit refused.");
            document.Regenerate(); double beforeCommitMs = timer.Elapsed.TotalMilliseconds;
            if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Rapid edit did not commit: " + kind);
            double commitAndProductionCallbacksMs = timer.Elapsed.TotalMilliseconds - beforeCommitMs;
            var actual = document.GetElement(uid);
            if (actual == null || !verify(actual) || BusinessComment(actual)?.AsString() != comment)
                throw new InvalidOperationException("Actual committed edit differs from request: " + kind);
            var after = Capture(actual, true); specs[actual.Id.Value] = after;
            if (Same(before.Point, after.Point) && SamePoints(before.Curve, after.Curve))
                throw new InvalidOperationException("Rapid witness has no verified geometric change: " + kind);
            var result = new { Kind = kind, SourceId = actual.Id.Value, SourceUid = uid, AtUtc = DateTime.UtcNow,
                Passed = true, GeometricChangeVerified = true, Comment = comment, Before = before, After = after,
                CommitAndProductionDocumentChangedCallbacksMilliseconds = commitAndProductionCallbacksMs,
                TransactionAndIndependentVerificationMilliseconds = timer.Elapsed.TotalMilliseconds, Failures = failures.Messages };
            rapidEdits.Add(result); Record("rapid_edit_committed", result);
        }

        void ApplyRapidSelection(Document document)
        {
            if (rapidIds.Count != 3 || rapidIds.Any(id => !physicalCandidateIds.Contains(id)))
                throw new InvalidOperationException("The exact three rapid witnesses are not admissible physical targets.");
            foreach (var spec in specs.Values) { spec.Selected = rapidIds.Contains(spec.Id); if (!spec.Selected) spec.SelectionExclusionReason = "outside-three-rapid-edited-witnesses"; }
            selectedPhysicalIds = rapidIds.OrderBy(id => id).ToArray(); explicitlyExcludedPhysicalIds = Array.Empty<long>();
            Write("rapid-selection.json", new { AtUtc = DateTime.UtcNow, SelectedPhysicalIds = selectedPhysicalIds,
                OriginalAdmissiblePhysicalCount = physicalCandidateIds.Length, AllCascadeVictimsRemainInVerificationDenominator = true });
        }

        void ValidateRapidDelete()
        {
            if (!config.RapidModifiedDelete || !IsDurableCapture) return;
            if (!rapidEdited || rapidIds.Count != 3 || rapidCallbackSequence != rapidEditCallbackSequence + 1
                || (bool?)Read(rapidBackground, "Paused") != true
                || Convert.ToInt32(Read(State(doc), "NativePendingRoots")) != 0
                || Convert.ToInt32(Read(Read(State(doc), "Preload"), "Captured") ?? 0) != rapidPreloadCount)
                throw new InvalidOperationException("Rapid deletion must be the next observer callback, with no deferred inventory preparation between edit and delete.");
        }

        void CompleteRapidDelete(double deletionMs)
        {
            if (!config.RapidModifiedDelete || !IsDurableCapture) return;
            rapidDeleteComplete = true;
            rapidBackground.GetField("Paused", Flags).SetValue(null, rapidOriginalPaused);
            Write("rapid-deletion-gap.json", new { AtUtc = committedUtc, EditedUtc = rapidEditsCompletedUtc,
                GapMilliseconds = (committedUtc - rapidEditsCompletedUtc).TotalMilliseconds, DeleteCallbackMilliseconds = deletionMs,
                EditCallbackSequence = rapidEditCallbackSequence, DeleteCallbackSequence = rapidCallbackSequence,
                NextObserverCallback = rapidCallbackSequence == rapidEditCallbackSequence + 1,
                DeferredPreparationPreventedThroughDeletion = true, DocumentChangedCaptureWasEnabled = true,
                ModifiedIds = rapidIds.OrderBy(id => id).ToArray(), DeletedPhysicalIds = deletedPhysicalIds,
                NativePendingRootsAfterDeletion = Read(State(doc), "NativePendingRoots"),
                AllThreeEditedObjectsActuallyDeleted = rapidIds.All(deletedPhysicalIds.Contains) });
            if (!rapidIds.All(deletedPhysicalIds.Contains)) throw new InvalidOperationException("A rapid edited witness survived deletion.");
        }

        void PrepareRapidReadiness()
        {
            if (!config.RapidModifiedDelete || !IsDurableCapture || !journalReady || rapidReadinessEvents != null) return;
            var eventType = product.GetType("Analyse.ElementHistoryEvent", true);
            var json = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true);
            var events = persisted.Values.Select(value => Call(json, "DeserializeObject", value.GetRawText(), eventType)).ToArray();
            Volatile.Write(ref rapidReadinessEvents, events);
        }

        void RapidIdle()
        {
            if (!config.RapidModifiedDelete || !IsDurableCapture || !rapidDeleteComplete || rapidFailed || finished) return;
            try
            {
                if (journalReady && !rapidConvertQueued)
                {
                    if (changedIds.Except(persisted.Keys).Any()) throw new InvalidOperationException("Rapid natural journal is missing actual deleted IDs.");
                    rapidConvertQueued = true; actions.Enqueue("convert");
                }
                if (!durableConversionComplete || rapidPersistQueued) return;
                var events = Volatile.Read(ref rapidReadinessEvents); if (events == null) return;
                var readiness = events.Where(ev => rapidIds.Contains(Convert.ToInt64(Read(ev, "ElementId"))))
                    .Select(ev => new { ElementId = Convert.ToInt64(Read(ev, "ElementId")), Readiness = Call(durableBridge, "GetReadiness", ev) }).ToArray();
                bool ready = readiness.Length == 3 && readiness.All(item => Read(item.Readiness, "State")?.ToString() == "Ready");
                Write("rapid-readiness.json", new { AtUtc = DateTime.UtcNow, AllThreeReady = ready, Records = readiness,
                    CompleteNaturalJournal = true, Status = ReadDurableStatus(), PreparationWasForcedOnlyAfterDeletion = true });
                if (!ready) throw new InvalidOperationException("Converted counters are insufficient: all three edited objects must have durable Ready receipts.");
                rapidPersistQueued = true; actions.Enqueue("persist-deleted");
            }
            catch (Exception ex) { rapidFailed = true; phase = "rapid-error"; Write("rapid-error.json", new { AtUtc = DateTime.UtcNow, Error = ex.ToString() }); }
        }

        IEnumerable<string> RapidJournalLines(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
            using var reader = new StreamReader(input, Encoding.UTF8);
            while (reader.ReadLine() is string line) yield return line;
        }

        Dictionary<string, string> RapidDeletionFingerprints()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(historyDirectory, "element-history-*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase)))
                foreach (var line in RapidJournalLines(path))
                {
                    using var parsed = JsonDocument.Parse(line); var ev = parsed.RootElement;
                    if (ev.GetProperty("Action").GetString() != "delete" || ev.GetProperty("Tx").GetString() != transactionName) continue;
                    string key = ev.GetProperty("ElementId").GetInt64() + "|" + ev.GetProperty("Ts").GetString();
                    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(line)));
                    if (!result.TryAdd(key, hash)) throw new InvalidOperationException("Duplicate rapid deletion event in journal: " + key);
                }
            return result;
        }

        void PrepareRapidTimeSimulation()
        {
            if (!initialized || !config.RapidModifiedDelete || !IsDurableRestore || Interlocked.CompareExchange(ref rapidTimeSetup, 1, 0) != 0) return;
            try
            {
                var before = RapidDeletionFingerprints();
                if (before.Count == 0 || before.Count != persisted.Count) throw new InvalidOperationException("No exact capture journal for temporal simulation.");
                var maxTs = before.Keys.Select(key => DateTime.Parse(key[(key.IndexOf('|') + 1)..], null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)).Max();
                rapidEffectiveUtc = maxTs.AddDays(14);
                var dailyBefore = Directory.GetFiles(historyDirectory, "element-history-*.jsonl");
                if (dailyBefore.Length == 0) throw new InvalidOperationException("J+14 test requires an actual daily journal to exercise production compression.");
                Call(rapidClock, "SetTestUtcNow", (DateTime?)rapidEffectiveUtc);
                if ((DateTime)Read(rapidClock, "UtcNow") != rapidEffectiveUtc) throw new InvalidOperationException("Production clock injection was not applied.");
                Call(tracker, "CompressOldFiles");
                var after = RapidDeletionFingerprints();
                var compressed = Directory.GetFiles(historyDirectory, "element-history-*.jsonl.gz");
                bool unchanged = before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out var value) && item.Value == value);
                bool dailyRemoved = dailyBefore.All(path => !File.Exists(path));
                Write("rapid-j14-simulation.json", new { WallClockUtc = DateTime.UtcNow, EffectiveProductionUtc = rapidEffectiveUtc,
                    LatestActualDeletionUtc = maxTs, SimulatedElapsedDays = (rapidEffectiveUtc - maxTs).TotalDays,
                    SimulationOnly = true, WindowsClockChanged = false, EventTimestampsModified = false, FileTimestampsModified = false,
                    MaintenanceInvoked = "ElementHistoryTracker.CompressOldFiles", DailyJournalsBefore = dailyBefore,
                    OriginalDailyJournalsRemoved = dailyRemoved, CompressedJournals = compressed.Select(path => new { Path = path, Bytes = new FileInfo(path).Length, Sha256 = Sha(path) }).ToArray(),
                    ExactDeletionEventsUnchanged = unchanged, Before = before, After = after,
                    Note = "This is an injected J+14 retention/loading check in a real fresh Revit process, not fourteen days of real-world elapsed storage." });
                if (!unchanged || !dailyRemoved || compressed.Length == 0) throw new InvalidOperationException("Production J+14 compression did not preserve and archive the exact natural journal.");
                Volatile.Write(ref rapidTimeSetup, 2);
            }
            catch (Exception ex) { Volatile.Write(ref rapidTimeSetup, -1); rapidFailed = true; Write("rapid-time-error.json", new { AtUtc = DateTime.UtcNow, Error = ex.ToString() }); }
        }

        void RapidAfterRestore(object verification, bool repeat)
        {
            if (!config.RapidModifiedDelete || !IsDurableRestore || repeat) return;
            if (Volatile.Read(ref rapidTimeSetup) != 2 || rapidSourceLocks.Count < 1 || rapidSourceLocks.Any(stream => !ReadIsDenied(stream.Name)))
                throw new InvalidOperationException("J+14 simulation or source-denial guard is missing during actual restoration.");
            bool passed = (bool?)Read(verification, "AllDeletedPhysicalVerified") == true
                && Convert.ToInt32(Read(lastResult, "Failed")) == 0 && durableBridgePaths.All(path => !File.Exists(path))
                && !((IEnumerable)Read(verification, "UnexpectedModelObjects")).Cast<object>().Any();
            Write("rapid-restoration.json", new { WallClockUtc = DateTime.UtcNow, EffectiveProductionUtc = Read(rapidClock, "UtcNow"),
                SimulationOnly = true, UsesExistingModelDefinitions = true, ExactEditedWitnessIds = selectedPhysicalIds, DeletedPhysicalIds = deletedPhysicalIds,
                AllPreparedSourcesInaccessible = true, AllOldOpeningBridgesAbsent = true, VerificationPassed = passed,
                RestoreResult = lastResult, Verification = verification,
                Scope = "The three edited witnesses and their admissible physical cascade victims; not a universal model/category guarantee." });
            if (!passed) throw new InvalidOperationException("Rapid J+14 restoration fidelity failed; inspect the per-element evidence before persistence.");
            actions.Enqueue("persist");
        }

        void RapidAfterReopen(object verification, object persistence)
        {
            if (!config.RapidModifiedDelete || !IsDurableRestore) return;
            bool passed = (bool?)Read(verification, "AllDeletedPhysicalVerified") == true
                && rapidSourceLocks.Count > 0 && rapidSourceLocks.All(stream => ReadIsDenied(stream.Name))
                && durableBridgePaths.All(path => !File.Exists(path))
                && !((IEnumerable)Read(verification, "UnexpectedModelObjects")).Cast<object>().Any();
            Write("rapid-cycle-result.json", new { WallClockUtc = DateTime.UtcNow, EffectiveProductionUtc = Read(rapidClock, "UtcNow"),
                SimulationOnly = true, RealFourteenDayStorageTest = false, UsesExistingModelDefinitions = true, Passed = passed,
                ModifiedWitnessIds = selectedPhysicalIds, ActualDeletedPhysicalIds = deletedPhysicalIds,
                SourcesDeniedThroughSaveAndReopen = true, OldOpeningBridgesAbsent = true,
                Persistence = persistence, ReopenedVerification = verification,
                Scope = "Three actual edited witnesses plus admissible cascade victims, distinct capture/restore Revit processes, real SaveAs/Close/reopen; J+14 is simulated only." });
            if (!passed) throw new InvalidOperationException("Saved and reopened rapid cycle differs from the actual modified baseline.");
            MeasureRapidArchive("rapid-storage-final.json");
            actions.Enqueue("rapid-purge-refusal");
        }

        string[] RapidArchiveFiles() => Directory.Exists(historyDirectory)
            ? Directory.GetFiles(historyDirectory, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        object RapidFileEvidence(string path) => new { Path = path, RelativePath = Path.GetRelativePath(historyDirectory, path), Bytes = new FileInfo(path).Length, Sha256 = Sha(path) };
        static bool RapidNativePayload(string path) => new[] { ".rvt", ".rfa" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        void MeasureRapidArchive(string evidenceName)
        {
            if (!config.RapidModifiedDelete) return;
            var paths = RapidArchiveFiles();
            var payloads = paths.Where(RapidNativePayload).ToArray();
            var newPayloads = payloads.Where(path => !rapidInitialArchiveFiles.Contains(path)).ToArray();
            var journals = paths.Where(path => Path.GetFileName(path).StartsWith("element-history-", StringComparison.OrdinalIgnoreCase)
                && (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase))).ToArray();
            var recipes = new List<object>();
            foreach (var id in rapidIds.OrderBy(id => id))
            {
                var matches = new List<JsonElement>();
                foreach (var path in journals)
                    foreach (var line in RapidJournalLines(path))
                    {
                        using var json = JsonDocument.Parse(line); var ev = json.RootElement;
                        if (ev.GetProperty("Action").GetString() == "delete" && ev.GetProperty("Tx").GetString() == transactionName
                            && ev.GetProperty("ElementId").GetInt64() == id) matches.Add(ev.Clone());
                    }
                if (matches.Count != 1) throw new InvalidOperationException("Archive measurement requires exactly one actual deletion recipe per rapid witness: " + id);
                var recipe = matches[0].GetProperty("Delta").GetProperty("recipe");
                bool existing = recipe.ValueKind == JsonValueKind.Object && JsonTrue(recipe, "UsesExistingModelDefinitions");
                bool noArtifacts = RapidRecipeHasNoArtifacts(recipe);
                recipes.Add(new { SourceId = id, UsesExistingModelDefinitions = existing, NoNativeOrOpeningFallback = noArtifacts,
                    JsonUtf8Bytes = Encoding.UTF8.GetByteCount(recipe.GetRawText()) });
                if (!existing || !noArtifacts) throw new InvalidOperationException("Rapid witness archive still requires a model capsule: " + id);
            }
            var proof = new { AtUtc = DateTime.UtcNow, UsesExistingModelDefinitions = true, WitnessCount = rapidIds.Count,
                NoNativePayloads = payloads.Length == 0, NoNewNativePayloads = newPayloads.Length == 0,
                NativePayloadCount = payloads.Length, NewNativePayloadCount = newPayloads.Length,
                NativePayloadFiles = payloads.Select(RapidFileEvidence).ToArray(), NewNativePayloadFiles = newPayloads.Select(RapidFileEvidence).ToArray(),
                WitnessRecipes = recipes, JournalBytes = journals.Sum(path => new FileInfo(path).Length),
                JournalFiles = journals.Select(RapidFileEvidence).ToArray(), ArchiveTotalBytes = paths.Sum(path => new FileInfo(path).Length),
                Files = paths.Select(RapidFileEvidence).ToArray(),
                Scope = "Actual isolated history archive after automatic opening bridge cleanup. Test model copies/checkpoints are not archive payloads. Recipe byte counts are the exact UTF-8 recipe JSON inside each natural deletion event; journal bytes include its gzip representation when compressed." };
            Write(evidenceName, proof);
            if (payloads.Length != 0 || newPayloads.Length != 0) throw new InvalidOperationException("Existing-definition archive must contain no persistent RVT/RFA payload.");
        }

        static bool RapidRecipeHasNoArtifacts(JsonElement recipe)
        {
            if (recipe.ValueKind != JsonValueKind.Object || new[] { "Native", "OpeningFallback" }
                .Any(name => recipe.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)) return false;
            if (recipe.TryGetProperty("ElectricalCircuits", out var circuits) && circuits.ValueKind == JsonValueKind.Array)
                foreach (var circuit in circuits.EnumerateArray())
                    if (circuit.TryGetProperty("Recipe", out var child) && child.ValueKind != JsonValueKind.Null && !RapidRecipeHasNoArtifacts(child)) return false;
            return true;
        }

        void ValidateRapidExistingDefinitionInputs(IList requests)
        {
            if (!config.RapidModifiedDelete || !IsDurableRestore) return;
            var records = new List<object>(); rapidRestoreRequests.Clear();
            foreach (var id in rapidIds.OrderBy(id => id))
            {
                var expected = specs[id];
                var matches = requests.Cast<object>().Where(request => (string)Read(request, "SourceUniqueId") == expected.Uid).ToArray();
                object recipe = matches.Length == 1 ? Read(matches[0], "Recipe") : null;
                bool existing = (bool?)Read(recipe, "UsesExistingModelDefinitions") == true;
                int artifacts = ArtifactsOf(recipe).Count();
                bool noNative = recipe != null && artifacts == 0;
                records.Add(new { SourceId = id, SourceUid = expected.Uid, RequestCount = matches.Length,
                    UsesExistingModelDefinitions = existing, NoNativeOrOpeningFallback = noNative, RecursiveNativeArtifactCount = artifacts });
                if (matches.Length == 1) rapidRestoreRequests[expected.Uid] = matches[0];
                if (matches.Length != 1 || !existing || !noNative)
                { Write("rapid-existing-definition-inputs.json", new { Passed = false, UsesExistingModelDefinitions = true, Records = records }); throw new InvalidOperationException("All three witness requests must explicitly use existing definitions and no native/opening capsule."); }
            }
            Write("rapid-existing-definition-inputs.json", new { Passed = true, UsesExistingModelDefinitions = true, Records = records });
        }

        void RapidPurgeRefusal(Autodesk.Revit.UI.UIApplication application)
        {
            if (!config.RapidModifiedDelete || !IsDurableRestore || lastResult == null)
                throw new InvalidOperationException("Purge refusal requires the completed positive rapid cycle.");
            string copyPath = Path.Combine(folder, "Antibes-purge-refusal.rvt");
            string positiveHash = Sha(restoredPath), checkpointHash = Sha(durableDeletedPath);
            if (File.Exists(copyPath)) throw new InvalidOperationException("Purge refusal requires a fresh separate copy.");
            File.Copy(restoredPath, copyPath, false);
            var expected = specs.Values.Single(spec => rapidIds.Contains(spec.Id) && spec.Class == "FamilyInstance");
            if (!sourceTypeDefinitions.TryGetValue(expected.TypeUid, out var definition) || definition.FamilyUid == null
                || !rapidRestoreRequests.TryGetValue(expected.Uid, out var request))
                throw new InvalidOperationException("Purge refusal has no complete source family/type/request baseline.");
            Document copy = null; object result = null;
            bool closed = false;
            try
            {
                using ((IDisposable)Call(native, "BeginHistoricalScope", application.Application))
                    copy = application.Application.OpenDocumentFile(copyPath);
                var family = copy.GetElement(definition.FamilyUid);
                if (family is not Family || copy.GetElement(expected.TypeUid) is not FamilySymbol || !(bool)Call(native, "Owns", copy))
                    throw new InvalidOperationException("Separate positive-model copy does not contain the original family/type or is not guarded as historical.");
                long[] purged;
                using (var tx = new Transaction(copy, "BIMaestroStress purge original witness family in separate copy"))
                { tx.Start(); purged = copy.Delete(family.Id).Select(id => id.Value).ToArray(); if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Actual family purge did not commit."); }
                bool missingBefore = copy.GetElement(definition.FamilyUid) == null && copy.GetElement(expected.TypeUid) == null;
                if (!missingBefore) throw new InvalidOperationException("Family/type still exists after the native delete transaction.");
                var beforeIds = RapidAllElementIds(copy);
                var payloadsBefore = RapidArchiveFiles().Where(RapidNativePayload).ToArray();
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(request.GetType())); list.Add(request);
                result = Call(restoration, "Restore", copy, list, null);
                var items = ((IEnumerable)Read(result, "Items")).Cast<object>().ToArray();
                var afterIds = RapidAllElementIds(copy);
                bool refusal = items.Length == 1 && (string)Read(items[0], "SourceUniqueId") == expected.Uid
                    && (string)Read(items[0], "Reason") == "definition_missing"
                    && Read(items[0], "Detail") is string detail && detail.StartsWith("ExistingModelDefinitionMissing:", StringComparison.Ordinal)
                    && detail.Contains(expected.TypeUid, StringComparison.Ordinal)
                    && Convert.ToInt32(Read(result, "Created")) == 0 && Convert.ToInt32(Read(result, "Existing")) == 0
                    && Convert.ToInt32(Read(result, "Failed")) == 1;
                bool stillMissing = copy.GetElement(definition.FamilyUid) == null && copy.GetElement(expected.TypeUid) == null && copy.GetElement(expected.Uid) == null
                    && !new FilteredElementCollector(copy).WhereElementIsNotElementType().ToElements().Any(element => Origins(element).Contains(expected.Uid));
                bool noModelChanges = beforeIds.SequenceEqual(afterIds);
                bool noPayload = payloadsBefore.SequenceEqual(RapidArchiveFiles().Where(RapidNativePayload));
                bool originalsUnchanged = Sha(restoredPath) == positiveHash && Sha(durableDeletedPath) == checkpointHash;
                bool passed = missingBefore && refusal && stillMissing && noModelChanges && noPayload && originalsUnchanged
                    && rapidSourceLocks.Count > 0 && rapidSourceLocks.All(stream => ReadIsDenied(stream.Name)) && durableBridgePaths.All(path => !File.Exists(path));
                closed = copy.Close(false); copy = null;
                Write("rapid-purge-refusal.json", new { AtUtc = DateTime.UtcNow, Passed = passed && closed, UsesExistingModelDefinitions = true,
                    SeparateCopy = copyPath, OpenedUnderHistoricalScope = true, OwnedBeforePurge = true, ClosedWithoutSaving = closed,
                    PositiveChecksCompletedBeforePurge = true, SourceId = expected.Id, SourceUid = expected.Uid,
                    SourceTypeUid = expected.TypeUid, SourceFamilyUid = definition.FamilyUid, PurgedElementIds = purged,
                    OriginalTypeAndFamilyMissingBeforeRestore = missingBefore, ExplicitDefinitionMissingRefusal = refusal,
                    OriginalTypeAndFamilyStillMissingAfterRestore = stillMissing, NoModelElementsAddedOrRemovedByRestore = noModelChanges,
                    NoNewNativePayloads = noPayload, PositiveSavedModelAndDeletedCheckpointUnchanged = originalsUnchanged,
                    PreparedSourcesStillDenied = rapidSourceLocks.All(stream => ReadIsDenied(stream.Name)), OldOpeningBridgesAbsent = durableBridgePaths.All(path => !File.Exists(path)),
                    SeparateCopyClosed = closed, RestoreResult = result,
                    Note = "Negative scenario only, after successful restoration/save/reopen. A historical-owned copy of the saved positive model is opened under BeginHistoricalScope. A real committed transaction removes the original family, types and its restored instances. The copy is closed without saving. Explicit definition_missing is required; a generic failure, imported replacement, or recreated definition fails." });
                if (!passed || !closed) throw new InvalidOperationException("Purged family/type was not explicitly refused without fallback or positive-model changes.");
            }
            finally { if (copy != null && copy.IsValidObject) copy.Close(false); }
            actions.Enqueue("finish");
        }
        static long[] RapidAllElementIds(Document document) => new FilteredElementCollector(document).WhereElementIsNotElementType().ToElementIds()
            .Concat(new FilteredElementCollector(document).WhereElementIsElementType().ToElementIds()).Select(id => id.Value).OrderBy(id => id).ToArray();
    }
}

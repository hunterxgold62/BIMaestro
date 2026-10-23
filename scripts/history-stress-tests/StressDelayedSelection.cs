using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        DateTime delayedClockUtc, delayedFirstCaptureUtc;
        bool delayedCaptureObserved, delayedPlantArmed, delayedWallArmed, delayedPlantDone, delayedWallDone, delayedTestFailed;
        readonly HashSet<long> delayedTouchedIds = new();
        readonly List<object> delayedEdits = new();
        long[] physicalCandidateIds = Array.Empty<long>(), explicitlyExcludedPhysicalIds = Array.Empty<long>();
        bool HasDelayedDeletion => IsDurableCapture && !config.BusinessWorkflow && config.DeletionDelaySeconds > 0;
        string DelayedTransactionPrefix => "BIMaestroStress " + config.RunName + " delay edit ";

        void StartDelayedCapture()
        {
            if (!HasDelayedDeletion) return;
            delayedClockUtc = DateTime.UtcNow; phase = "delay-capture";
            Record("delayed_capture_clock_started", new { OpenReturnedUtc = delayedClockUtc, DocumentOpenedUtc = openedUtc,
                DelaySeconds = config.DeletionDelaySeconds, TargetFraction = config.PhysicalTargetFraction,
                DeterministicEdits = config.ApplyDeterministicEdits, State = State(doc),
                Note = "The full requested minute starts after OpenAndActivateDocument returns. No inventory priming or manual capture is invoked." });
        }

        void DelayedIdle(object state)
        {
            if (!HasDelayedDeletion || deleted || deleteQueued || delayedTestFailed || delayedClockUtc == default) return;
            double elapsed = (DateTime.UtcNow - delayedClockUtc).TotalSeconds;
            if (!delayedCaptureObserved && Convert.ToInt32(Read(Read(state, "Preload"), "Visited") ?? 0) > 0
                && Convert.ToInt32(Read(Read(state, "Preload"), "Captured") ?? 0) > 0)
            {
                delayedCaptureObserved = true; delayedFirstCaptureUtc = DateTime.UtcNow;
                Record("delayed_real_capture_observed", new { AfterOpenReturnSeconds = elapsed, State = state });
            }
            if (config.ApplyDeterministicEdits)
            {
                if (!delayedPlantArmed && elapsed >= config.DeletionDelaySeconds / 6d)
                { delayedPlantArmed = true; actions.Enqueue("delay-edit-plant"); }
                if (!delayedWallArmed && elapsed >= config.DeletionDelaySeconds * 5d / 12d)
                { delayedWallArmed = true; actions.Enqueue("delay-edit-wall"); }
                if (!delayedPlantDone || !delayedWallDone) return;
            }
            if (elapsed < config.DeletionDelaySeconds) return;
            if (!delayedCaptureObserved)
            {
                delayedTestFailed = true; phase = "delay-no-capture-progress";
                var failure = new { AtUtc = DateTime.UtcNow, DeletionAttempted = false, DelaySeconds = elapsed, State = state,
                    Reason = "No real production inventory visit observed during the requested minute; test is inactive, not a successful prepared capture." };
                Write("delayed-test-failed.json", failure); Record("delayed_test_failed", failure); return;
            }
            deleteQueued = true; actions.Enqueue("delete");
            Record("delayed_mass_delete_armed", new { AfterOpenReturnSeconds = elapsed, RealCaptureObserved = true,
                Edits = delayedEdits.ToArray(), ForcedPhysicalIds = delayedTouchedIds.OrderBy(id => id).ToArray(), State = state });
        }

        object SnapshotEvidence(IEnumerable<long> ids) => ids.Distinct().Select(id =>
        {
            var snapshots = (System.Collections.IDictionary)Read(tracker, "SnapshotByElementId");
            var key = (string)Call(tracker, "BuildElementSnapshotKey", doc, new ElementId(id)); var snapshot = snapshots[key];
            var recipe = Read(snapshot, "Recipe");
            return new { Id = id, Present = snapshot != null, Kind = Read(recipe, "Kind"), DetailCaptureAttempted = Read(snapshot, "DetailCaptureAttempted"),
                CaptureFailure = Read(snapshot, "CaptureFailure"), Native = Read(recipe, "Native") != null, OpeningFallback = Read(recipe, "OpeningFallback") != null };
        }).ToArray();

        void DelayedChanged(string[] names, long[] added, long[] modified)
        {
            if (!HasDelayedDeletion || !names.Any(name => name.StartsWith(DelayedTransactionPrefix, StringComparison.Ordinal))) return;
            foreach (var id in added.Concat(modified)) delayedTouchedIds.Add(id);
        }

        void EditDelayedPlant(Document document)
        {
            if (!HasDelayedDeletion || !config.ApplyDeterministicEdits || delayedPlantDone) throw new InvalidOperationException("Plant edit is not armed for this scenario.");
            bool Candidate(FamilyInstance instance) => instance != null && instance.Category?.Id.Value == (long)BuiltInCategory.OST_Planting
                && instance.Location is LocationPoint && instance.Host == null && instance.SuperComponent == null
                && instance.GroupId == ElementId.InvalidElementId && instance.MEPModel == null && !instance.Pinned;
            var plant = document.GetElement(new ElementId(520659L)) as FamilyInstance;
            if (!Candidate(plant)) plant = new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(Candidate).OrderBy(instance => instance.UniqueId, StringComparer.Ordinal).FirstOrDefault();
            if (plant == null)
            {
                var skipped = new { Kind = "plant_translation", AtUtc = DateTime.UtcNow,
                    AfterOpenReturnSeconds = (DateTime.UtcNow - delayedClockUtc).TotalSeconds, Skipped = true, Passed = false,
                    Reason = "No eligible independent unpinned planting instance; no geometry change was attempted." };
                delayedPlantDone = true; delayedEdits.Add(skipped); Write("delayed-edit-plant.json", skipped); Record("delayed_edit_skipped", skipped); return;
            }
            long id = plant.Id.Value; string uid = plant.UniqueId; var before = Capture(plant); var snapshotsBefore = SnapshotEvidence(new[] { id });
            var watch = Stopwatch.StartNew(); double rise = UnitUtils.ConvertToInternalUnits(100d, UnitTypeId.Millimeters);
            using (var transaction = new Transaction(document, DelayedTransactionPrefix + "plant +100mm Z"))
            {
                transaction.Start(); ElementTransformUtils.MoveElement(document, new ElementId(id), new XYZ(0, 0, rise));
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Plant edit did not commit.");
            }
            var after = Capture(document.GetElement(uid));
            bool exact = before.Point != null && after.Point != null && Same(new[] { before.Point[0], before.Point[1], before.Point[2] + rise }, after.Point)
                && before.TypeUid == after.TypeUid;
            delayedTouchedIds.Add(id); delayedPlantDone = exact;
            var result = new { Kind = "plant_translation", AtUtc = DateTime.UtcNow, AfterOpenReturnSeconds = (DateTime.UtcNow - delayedClockUtc).TotalSeconds,
                Milliseconds = watch.Elapsed.TotalMilliseconds, SourceId = id, SourceUid = uid, TranslationFeet = new[] { 0d, 0d, rise }, Skipped = false, Passed = exact,
                Before = before, After = after, ProductBefore = snapshotsBefore, ProductAfter = SnapshotEvidence(new[] { id }) };
            delayedEdits.Add(result); Write("delayed-edit-plant.json", result); Record("delayed_edit_completed", result);
            if (!exact) throw new InvalidOperationException("Plant edit did not retain the requested exact placement.");
        }

        void EditDelayedWall(Document document)
        {
            if (!HasDelayedDeletion || !config.ApplyDeterministicEdits || delayedWallDone) throw new InvalidOperationException("Wall edit is not armed for this scenario.");
            int JoinEnd(Wall wall)
            {
                if (wall == null || wall.Pinned || wall.GroupId != ElementId.InvalidElementId || wall.Location is not LocationCurve location) return -1;
                for (int end = 0; end < 2; end++)
                    if (WallUtils.IsWallJoinAllowedAtEnd(wall, end) && location.get_ElementsAtJoin(end).Size > 0) return end;
                return -1;
            }
            var wall = document.GetElement(new ElementId(556631L)) as Wall; int selectedEnd = JoinEnd(wall);
            if (selectedEnd < 0)
                foreach (var candidate in new FilteredElementCollector(document).OfClass(typeof(Wall)).Cast<Wall>().OrderBy(value => value.UniqueId, StringComparer.Ordinal))
                    if (JoinEnd(candidate) is int end && end >= 0) { wall = candidate; selectedEnd = end; break; }
            if (selectedEnd < 0 || wall == null)
            {
                var skipped = new { Kind = "wall_disallow_join", AtUtc = DateTime.UtcNow,
                    AfterOpenReturnSeconds = (DateTime.UtcNow - delayedClockUtc).TotalSeconds, Skipped = true, Passed = false,
                    Reason = "No eligible unpinned wall with a real joined end; no join change was attempted." };
                delayedWallDone = true; delayedEdits.Add(skipped); Write("delayed-edit-wall.json", skipped); Record("delayed_edit_skipped", skipped); return;
            }
            var peers = ((LocationCurve)wall.Location).get_ElementsAtJoin(selectedEnd).Cast<Element>().Select(element => element.UniqueId).ToArray();
            string uid = wall.UniqueId; long id = wall.Id.Value;
            var affectedUids = peers.Append(uid).Distinct().ToArray(); var before = affectedUids.Select(value => Capture(document.GetElement(value))).ToArray();
            foreach (var expected in before) delayedTouchedIds.Add(expected.Id);
            var snapshotsBefore = SnapshotEvidence(before.Select(spec => spec.Id)); var watch = Stopwatch.StartNew();
            using (var transaction = new Transaction(document, DelayedTransactionPrefix + "disallow wall join"))
            {
                transaction.Start(); WallUtils.DisallowWallJoinAtEnd(wall, selectedEnd);
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Wall join edit did not commit.");
            }
            var actualWall = document.GetElement(uid) as Wall;
            bool exact = actualWall != null && !WallUtils.IsWallJoinAllowedAtEnd(actualWall, selectedEnd);
            var after = affectedUids.Select(value => document.GetElement(value)).Where(element => element != null).Select(element => Capture(element)).ToArray();
            delayedWallDone = exact;
            var result = new { Kind = "wall_disallow_join", AtUtc = DateTime.UtcNow, AfterOpenReturnSeconds = (DateTime.UtcNow - delayedClockUtc).TotalSeconds,
                Milliseconds = watch.Elapsed.TotalMilliseconds, SourceId = id, SourceUid = uid, End = selectedEnd, Skipped = false, Passed = exact, PeerSourceUids = peers,
                Before = before, After = after, ProductBefore = snapshotsBefore, ProductAfter = SnapshotEvidence(before.Select(spec => spec.Id)) };
            delayedEdits.Add(result); Write("delayed-edit-wall.json", result); Record("delayed_edit_completed", result);
            if (!exact) throw new InvalidOperationException("Wall join edit did not retain the requested permission state.");
        }

        void ValidateDelayedDelete()
        {
            if (!HasDelayedDeletion) return;
            double elapsed = (DateTime.UtcNow - delayedClockUtc).TotalSeconds;
            if (delayedTestFailed || elapsed < config.DeletionDelaySeconds || !delayedCaptureObserved
                || config.ApplyDeterministicEdits && (!delayedPlantDone || !delayedWallDone))
                throw new InvalidOperationException("Delayed scenario has no complete minute/capture/edit evidence; Delete was not attempted.");
            Write("delayed-before-delete.json", new { AtUtc = DateTime.UtcNow, DocumentOpenedUtc = openedUtc, OpenReturnedUtc = delayedClockUtc,
                RequestedDelaySeconds = config.DeletionDelaySeconds, ActualDelayAfterOpenReturnSeconds = elapsed, FirstRealCaptureUtc = delayedFirstCaptureUtc,
                RealCaptureObserved = true, EditAttemptsCompleted = !config.ApplyDeterministicEdits || delayedPlantDone && delayedWallDone,
                CompletedEditsCount = delayedEdits.Count(edit => (bool?)Read(edit, "Passed") == true && (bool?)Read(edit, "Skipped") != true),
                SkippedEdits = delayedEdits.Where(edit => (bool?)Read(edit, "Skipped") == true).ToArray(),
                EditsPassed = !config.ApplyDeterministicEdits || delayedEdits.Count == 2 && delayedEdits.All(edit => (bool?)Read(edit, "Passed") == true && (bool?)Read(edit, "Skipped") != true),
                Edits = delayedEdits.ToArray(), ProductAtDeadline = State(doc),
                Note = "The full independent inventory follows these edits and is the restoration baseline; it does not populate production snapshots." });
        }

        void ApplyPhysicalSelection(Document document)
        {
            physicalCandidateIds = selectedPhysicalIds.ToArray();
            if (config.RapidModifiedDelete) { ApplyRapidSelection(document); return; }
            if (config.BusinessWorkflow) { ApplyBusinessSelection(document); return; }
            var all = physicalCandidateIds.ToHashSet(); var parents = all.ToDictionary(id => id, id => id);
            long Find(long id) { while (parents[id] != id) { parents[id] = parents[parents[id]]; id = parents[id]; } return id; }
            void Union(long first, long second) { long a = Find(first), b = Find(second); if (a != b) parents[Math.Max(a, b)] = Math.Min(a, b); }
            var byUid = catalog.Values.ToDictionary(entry => entry.Uid);
            foreach (var spec in specs.Values.Where(spec => all.Contains(spec.Id)))
                if (spec.RootUid != null && byUid.TryGetValue(spec.RootUid, out var root))
                {
                    if (all.Contains(root.Id)) Union(spec.Id, root.Id);
                    else if (document.GetElement(new ElementId(root.Id)) is Group group && TryPhysicalGroupRoot(document, group, all, out var logical))
                        foreach (var leaf in logical.PhysicalLeafIds) Union(spec.Id, leaf);
                }
            var skippedCategories = new HashSet<long>();
            foreach (var name in config.SkipPhysicalCategories ?? Array.Empty<string>())
                if (long.TryParse(name, out var id)) skippedCategories.Add(id);
                else if (Enum.TryParse<BuiltInCategory>(name, out var category)) skippedCategories.Add((long)category);
                else throw new InvalidOperationException("Unknown explicit physical exclusion: " + name);
            var skippedIds = (config.SkipPhysicalIds ?? Array.Empty<long>()).ToHashSet();
            var explicitSkips = specs.Values.Where(spec => all.Contains(spec.Id) && (skippedIds.Contains(spec.Id)
                || spec.RootUid != null && byUid.TryGetValue(spec.RootUid, out var sourceRoot) && skippedIds.Contains(sourceRoot.Id)
                || spec.CategoryId.HasValue && skippedCategories.Contains(spec.CategoryId.Value))).Select(spec => spec.Id).ToHashSet();
            foreach (var requested in skippedIds)
                if (document.GetElement(new ElementId(requested)) is Group excludedGroup)
                {
                    if (!TryPhysicalGroupRoot(document, excludedGroup, all, out var physicalGroup))
                        throw new InvalidOperationException("An explicitly excluded group contains unverified nonphysical members: " + requested);
                    foreach (var leaf in physicalGroup.PhysicalLeafIds) explicitSkips.Add(leaf);
                }
            var units = all.GroupBy(Find).Select(group => group.OrderBy(id => id).ToArray()).ToArray();
            var excludedUnits = units.Where(unit => unit.Any(explicitSkips.Contains)).ToArray();
            explicitlyExcludedPhysicalIds = excludedUnits.SelectMany(unit => unit).OrderBy(id => id).ToArray();
            var excluded = explicitlyExcludedPhysicalIds.ToHashSet();
            if (HasDelayedDeletion && delayedTouchedIds.Any(id => all.Contains(id) && excluded.Contains(id)))
                throw new InvalidOperationException("An edited physical target belongs to an explicitly excluded root; the test cannot omit its modified witness.");
            var allowed = units.Where(unit => !unit.Any(excluded.Contains)).ToArray();
            int count = allowed.Sum(unit => unit.Length), target = (int)Math.Ceiling(count * config.PhysicalTargetFraction);
            if (target == 0) throw new InvalidOperationException("Explicit exclusions left no physical target.");
            var selected = allowed.Where(unit => HasDelayedDeletion && unit.Any(delayedTouchedIds.Contains)).SelectMany(unit => unit).ToHashSet();
            string Key(long[] unit) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Antibes physical half v1|" + string.Join("|", unit.Select(id => specs[id].Uid).OrderBy(uid => uid, StringComparer.Ordinal)))));
            foreach (var unit in allowed.OrderBy(Key, StringComparer.Ordinal))
                if (!unit.Any(selected.Contains) && selected.Count + unit.Length <= target) foreach (var id in unit) selected.Add(id);
            if (Math.Abs(selected.Count - target) > Math.Max(1, (int)Math.Ceiling(count * .05)))
                throw new InvalidOperationException("A safe complete-root sample cannot approximate the requested fraction.");
            foreach (var spec in specs.Values.Where(spec => all.Contains(spec.Id)))
            {
                spec.Selected = selected.Contains(spec.Id);
                if (!spec.Selected) spec.SelectionExclusionReason = explicitSkips.Contains(spec.Id) ? "explicit-category-or-id-witness-exclusion"
                    : excluded.Contains(spec.Id) ? "complete-root-contains-explicit-witness-exclusion" : "outside-deterministic-physical-root-sample";
            }
            selectedPhysicalIds = selected.OrderBy(id => id).ToArray();
            Write("sampling-plan.json", new { AtUtc = DateTime.UtcNow, RequestedFraction = config.PhysicalTargetFraction, PhysicalEligibleBeforeSamplingIds = physicalCandidateIds,
                PhysicalEligibleBeforeSamplingCount = physicalCandidateIds.Length, ExplicitCategoryNamesOrIds = config.SkipPhysicalCategories, ExplicitRequestedIds = config.SkipPhysicalIds,
                DirectlyExcludedPhysicalIds = explicitSkips.OrderBy(id => id).ToArray(), ExcludedPhysicalIdsIncludingCompleteRoots = explicitlyExcludedPhysicalIds,
                AllowedPhysicalCount = count, TargetPhysicalCount = target, SelectedPhysicalIds = selectedPhysicalIds, SelectedPhysicalCount = selected.Count,
                SelectedFractionOfAllowed = (double)selected.Count / count, SelectedFractionOfOriginalPhysical = (double)selected.Count / physicalCandidateIds.Length,
                PhysicalRootUnits = units.Length, ForcedModifiedPhysicalIds = delayedTouchedIds.Where(all.Contains).OrderBy(id => id).ToArray(),
                ExcludedRecords = specs.Values.Where(spec => excluded.Contains(spec.Id)).ToArray(),
                WitnessExclusionReason = config.WitnessExclusionReason,
                Note = "All deletions caused by the transaction within the original admissible physical universe remain in final verification, including unselected or explicitly excluded cascade victims. The denominator is never reduced after deletion." });
        }
    }
}

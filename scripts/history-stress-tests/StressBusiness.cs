using Autodesk.Revit.DB;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        DateTime businessStartedUtc, businessEditedUtc, businessFirstCaptureUtc, businessNextReadinessUtc;
        bool businessEditQueued, businessEdited, businessFailed, businessCaptureObserved;
        readonly HashSet<long> businessModifiedIds = new();
        readonly List<object> businessEdits = new();
        string businessLastReadiness; object[] businessReadinessEvents;
        void StartBusinessCapture()
        {
            if (!config.BusinessWorkflow || !IsDurableCapture) return;
            businessStartedUtc = DateTime.UtcNow; phase = "business-natural-capture";
            Record("business_clock_started", new { AtUtc = businessStartedUtc, WarmupSeconds = config.DeletionDelaySeconds,
                WorkAfterEditsSeconds = config.WorkAfterEditsSeconds, State = State(doc), AutomaticPreparation = true, ForcedPreparation = false });
        }
        void BusinessIdle(object state)
        {
            if (!config.BusinessWorkflow || !IsDurableCapture || businessStartedUtc == default || deleted || deleteQueued || businessFailed) return;
            if (!businessCaptureObserved && Convert.ToInt32(Read(Read(state, "Preload"), "Captured") ?? 0) > 0)
            { businessCaptureObserved = true; businessFirstCaptureUtc = DateTime.UtcNow; Record("business_real_capture_observed", state); }
            if (!businessEditQueued && (DateTime.UtcNow - businessStartedUtc).TotalSeconds >= config.DeletionDelaySeconds)
            {
                if (!businessCaptureObserved) { businessFailed = true; Write("business-test-failed.json", new { AtUtc = DateTime.UtcNow, Reason = "No real captured snapshot observed during the warmup; no edits or deletion attempted.", State = state }); return; }
                businessEditQueued = true; actions.Enqueue("business-edit");
            }
            if (businessEdited && (DateTime.UtcNow - businessEditedUtc).TotalSeconds >= config.WorkAfterEditsSeconds)
            { deleteQueued = true; actions.Enqueue("delete"); Record("business_delete_armed", new { ModifiedIds = businessModifiedIds.OrderBy(id => id).ToArray(), State = state }); }
        }
        Parameter BusinessComment(Element element) => element?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        bool WritableComment(Element element) => BusinessComment(element) is Parameter p && !p.IsReadOnly && p.StorageType == StorageType.String;
        void BusinessTransaction(Document document, string kind, Element element, Action change, Func<bool> verify)
        {
            var before = Capture(element); string uid = element.UniqueId; string text = "BIMaestro business " + config.RunName + " " + kind;
            using var transaction = new Transaction(document, "BIMaestroStress " + config.RunName + " business edit " + kind);
            transaction.Start(); change();
            if (!BusinessComment(element).Set(text)) throw new InvalidOperationException("Comment parameter refused the business edit.");
            document.Regenerate();
            if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Business edit transaction did not commit.");
            var actual = document.GetElement(uid); bool passed = actual != null && verify() && BusinessComment(actual)?.AsString() == text;
            if (!passed) throw new InvalidOperationException("Business edit did not preserve its requested latest state: " + kind);
            businessModifiedIds.Add(actual.Id.Value);
            var result = new { Kind = kind, AtUtc = DateTime.UtcNow, SourceId = actual.Id.Value, SourceUid = uid, Passed = true, Skipped = false, Before = before, After = Capture(actual), Comment = text };
            businessEdits.Add(result); Write("business-edit-" + kind + ".json", result); Record("business_edit_committed", result);
        }
        void BusinessSkip(string kind, string reason)
        { var record = new { Kind = kind, AtUtc = DateTime.UtcNow, Passed = false, Skipped = true, Reason = reason }; businessEdits.Add(record); Write("business-edit-" + kind + ".json", record); Record("business_edit_skipped", record); }
        void EditBusinessObjects(Document document)
        {
            if (!config.BusinessWorkflow || !IsDurableCapture || businessEdited || businessFailed) throw new InvalidOperationException("Business edits are not armed.");
            try
            {
                bool FamilyCandidate(FamilyInstance f) => f != null && !f.Pinned && f.Location is LocationPoint && f.GroupId == ElementId.InvalidElementId
                    && f.SuperComponent == null && (f.Host == null || f.Host is Level) && Connectors(f).Count == 0 && WritableComment(f);
                var family = document.GetElement(new ElementId(474717L)) as FamilyInstance;
                if (!FamilyCandidate(family)) family = new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(FamilyCandidate).OrderBy(f => f.UniqueId, StringComparer.Ordinal).FirstOrDefault();
                if (family == null) BusinessSkip("family", "No independent unpinned point family with a writable comment and no ports.");
                else
                {
                    string uid = family.UniqueId; var point = ((LocationPoint)family.Location).Point; double delta = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters); var wanted = point + new XYZ(delta, 0, 0);
                    BusinessTransaction(document, "family", family, () => ElementTransformUtils.MoveElement(document, family.Id, new XYZ(delta, 0, 0)),
                        () => document.GetElement(uid)?.Location is LocationPoint p && p.Point.DistanceTo(wanted) <= Tolerance);
                }
                var mep = new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements().OfType<MEPCurve>()
                    .Where(e => (e is Autodesk.Revit.DB.Plumbing.Pipe || e is Autodesk.Revit.DB.Mechanical.Duct) && !e.Pinned && e.GroupId == ElementId.InvalidElementId && WritableComment(e))
                    .OrderBy(e => e.UniqueId, StringComparer.Ordinal).FirstOrDefault();
                if (mep == null) BusinessSkip("mep", "No unpinned pipe or duct with a writable instance comment.");
                else
                {
                    string uid = mep.UniqueId; var before = Capture(mep);
                    BusinessTransaction(document, "mep", mep, () => { }, () => { var after = Capture(document.GetElement(uid)); return SamePoints(before.Curve, after.Curve) && before.SystemTypeUid == after.SystemTypeUid; });
                }
                int FreeEnd(Wall w)
                {
                    if (w == null || w.Pinned || w.GroupId != ElementId.InvalidElementId || !WritableComment(w) || w.Location is not LocationCurve c || c.Curve is not Line || w.FindInserts(true, true, true, true).Count != 0) return -1;
                    for (int end = 0; end < 2; end++) if (c.get_ElementsAtJoin(end).Size == 0) return end;
                    return -1;
                }
                Wall wall = null; int freeEnd = -1;
                foreach (var candidate in new FilteredElementCollector(document).OfClass(typeof(Wall)).Cast<Wall>().OrderBy(w => w.Id.Value))
                    if (FreeEnd(candidate) is int end && end >= 0) { wall = candidate; freeEnd = end; break; }
                if (wall == null) BusinessSkip("wall", "No unpinned straight wall with a free end, no inserts and a writable comment.");
                else
                {
                    string uid = wall.UniqueId; var location = (LocationCurve)wall.Location; var curve = location.Curve;
                    var points = new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) }; double distance = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
                    if (curve.Length <= distance * 4) throw new InvalidOperationException("Selected free wall is too short for the edit.");
                    points[freeEnd] += (points[1 - freeEnd] - points[freeEnd]).Normalize() * distance;
                    var wanted = Line.CreateBound(points[0], points[1]);
                    BusinessTransaction(document, "wall", wall, () => location.Curve = wanted,
                        () => document.GetElement(uid)?.Location is LocationCurve current && current.Curve.GetEndPoint(0).DistanceTo(points[0]) <= Tolerance && current.Curve.GetEndPoint(1).DistanceTo(points[1]) <= Tolerance);
                }
                if (businessModifiedIds.Count < 2) throw new InvalidOperationException("Business scenario requires at least two real modified objects; no deletion was attempted.");
                businessEdited = true; businessEditedUtc = DateTime.UtcNow; phase = "business-natural-work-after-edits";
                Write("business-edits.json", new { AtUtc = businessEditedUtc, WarmupSeconds = (businessEditedUtc - businessStartedUtc).TotalSeconds, ModifiedIds = businessModifiedIds.ToArray(), Edits = businessEdits.ToArray(), MinimumRealEdits = 2 });
            }
            catch { businessFailed = true; throw; }
        }
        void ValidateBusinessDelete()
        {
            if (!config.BusinessWorkflow) return;
            if (!businessEdited || businessFailed || !businessCaptureObserved || businessModifiedIds.Count < 2 || (DateTime.UtcNow - businessEditedUtc).TotalSeconds < config.WorkAfterEditsSeconds)
                throw new InvalidOperationException("Business warmup, actual edits or post-edit working interval is incomplete.");
            Write("business-before-delete.json", new { AtUtc = DateTime.UtcNow, OpenReturnedUtc = businessStartedUtc, FirstRealCaptureUtc = businessFirstCaptureUtc,
                EditsCompletedUtc = businessEditedUtc, NaturalWorkAfterEditsSeconds = (DateTime.UtcNow - businessEditedUtc).TotalSeconds,
                ModifiedIds = businessModifiedIds.ToArray(), Edits = businessEdits.ToArray(), ForcedPreparation = false, State = State(doc) });
        }
        void ApplyBusinessSelection(Document document)
        {
            var physical = physicalCandidateIds.ToHashSet();
            bool Candidate(Spec s) => physical.Contains(s.Id) && s.RootUid == s.Uid && s.GroupUid == null && s.SuperUid == null
                && (s.HostUid == null || document.GetElement(s.HostUid) is Level) && !document.GetElement(new ElementId(s.Id)).Pinned
                && s.Class is "Wall" or "Pipe" or "Duct" or "FamilyInstance" && (s.Point != null || s.Curve != null);
            var candidates = specs.Values.Where(Candidate).OrderBy(s => s.Uid, StringComparer.Ordinal).ToList();
            var selected = businessModifiedIds.ToHashSet();
            if (selected.Any(id => !physical.Contains(id) || !candidates.Any(s => s.Id == id))) throw new InvalidOperationException("An actually edited object is not eligible for the business selection.");
            foreach (var bucket in candidates.GroupBy(s => s.Class + "|" + s.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
                if (selected.Count < config.BusinessSelectionCount) selected.Add(bucket.First().Id);
            foreach (var candidate in candidates) if (selected.Count < config.BusinessSelectionCount) selected.Add(candidate.Id);
            if (selected.Count < 10 || selected.Count > 30) throw new InvalidOperationException("Business selection must contain ten to thirty real physical instances.");
            foreach (var s in specs.Values.Where(s => physical.Contains(s.Id))) { s.Selected = selected.Contains(s.Id); if (!s.Selected) s.SelectionExclusionReason = "outside-explicit-business-selection"; }
            selectedPhysicalIds = selected.OrderBy(id => id).ToArray(); explicitlyExcludedPhysicalIds = Array.Empty<long>();
            Write("business-selection.json", new { AtUtc = DateTime.UtcNow, OriginalPhysicalUniverse = physicalCandidateIds.Length, RequestedCount = config.BusinessSelectionCount,
                SelectedPhysicalIds = selectedPhysicalIds, ModifiedIds = businessModifiedIds.ToArray(), ModifiedObjectsAllIncluded = businessModifiedIds.All(selected.Contains),
                Selected = selectedPhysicalIds.Select(id => specs[id]).ToArray(), Note = "Small distributed workflow, selected before Delete. All physical cascade victims remain in the original independent verification denominator." });
        }
        object CompareBusinessParameters(Document document, Spec expected, Spec actual)
        {
            var first = expected.InstanceParameters; var second = actual.InstanceParameters; var errors = new List<string>();
            bool SameParameter(TypeParameter a, TypeParameter b) => a.Storage == b.Storage && a.DataType == b.DataType && a.Error == null && b.Error == null && (a.Storage switch
            { "Double" => a.Number.HasValue && b.Number.HasValue && Math.Abs(a.Number.Value - b.Number.Value) <= 1e-9, "Integer" => a.Integer == b.Integer, "String" => a.Text == b.Text,
              "ElementId" => DefinitionReferenceMatches(document, a, b), "None" => true, _ => false });
            if (first == null || second == null) errors.Add("Instance parameter baseline missing.");
            else
            {
                var remaining = second.Where(p => p.ExclusionReason == null).ToList();
                foreach (var p in first.Where(p => p.ExclusionReason == null)) { int index = remaining.FindIndex(q => p.Key == q.Key && SameParameter(p, q)); if (index < 0) errors.Add("missing-or-different:" + p.Key); else remaining.RemoveAt(index); }
                foreach (var p in remaining) errors.Add("extra:" + p.Key);
            }
            if (expected.InstanceParameterErrors?.Count != 0 || actual.InstanceParameterErrors?.Count != 0) errors.Add("Parameter read error or missing error coverage.");
            if (expected.Pinned != actual.Pinned) errors.Add("Pinned changed.");
            return new { Passed = errors.Count == 0, Differences = errors, ExpectedCount = first?.Count, ActualCount = second?.Count, Expected = first, Actual = second };
        }
        void BuildBusinessReadinessEvents()
        {
            if (!config.BusinessWorkflow || businessReadinessEvents != null) return;
            var eventType = product.GetType("Analyse.ElementHistoryEvent", true);
            var json = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true);
            // This method runs only in the journal IO worker after the natural
            // persisted batch is stable. No loader, gzip or file IO on Idling.
            var events = persisted.Values.Select(value => Call(json, "DeserializeObject", value.GetRawText(), eventType)).ToArray();
            System.Threading.Volatile.Write(ref businessReadinessEvents, events);
        }
        void BusinessReadinessIdle()
        {
            if (!config.BusinessWorkflow || !IsDurableCapture || !journalReady || partialCapturePaused || finished || DateTime.UtcNow < businessNextReadinessUtc) return;
            businessNextReadinessUtc = DateTime.UtcNow.AddSeconds(1);
            try
            {
                var events = System.Threading.Volatile.Read(ref businessReadinessEvents); if (events == null) return;
                var records = events.Select(ev => { var readiness = Call(durableBridge, "GetReadiness", ev); return new { ElementId = Convert.ToInt64(Read(ev, "ElementId")), SourceUid = Read(ev, "UniqueId"), EventId = Read(readiness, "EventId"), State = Read(readiness, "State")?.ToString(), Legacy = Read(readiness, "Legacy"), Reason = Read(readiness, "Reason") }; }).ToArray();
                var modified = records.Where(r => businessModifiedIds.Contains(r.ElementId)).ToArray();
                string signature = string.Join("|", records.Select(r => r.ElementId + ":" + r.State + ":" + r.Reason));
                bool exact = changedIds.Length > 0 && changedIds.OrderBy(id => id).SequenceEqual(records.Select(r => r.ElementId).Distinct().OrderBy(id => id));
                var result = new { AtUtc = DateTime.UtcNow, ForcedPreparation = false, ExactDeletedEventCoverage = exact, Records = records,
                    PhysicalReadyCount = records.Count(r => deletedPhysicalIds.Contains(r.ElementId) && r.State == "Ready"),
                    ModifiedReady = modified.Length == businessModifiedIds.Count && modified.All(r => r.State == "Ready"), Modified = modified,
                    AllTerminal = exact && records.All(r => r.State is "Ready" or "Unavailable"), Status = ReadDurableStatus(),
                    TimedOut = (DateTime.UtcNow - committedUtc).TotalSeconds >= config.BusinessReadinessTimeoutSeconds };
                Write("business-readiness.json", result);
                if (signature != businessLastReadiness) { businessLastReadiness = signature; Record("business_readiness_changed", result); }
                CaptureRawBridgePaths(); ObserveDurableCompletion(result.Status);
            }
            catch (Exception ex) { Write("business-readiness-error.json", new { AtUtc = DateTime.UtcNow, Error = ex.ToString() }); }
        }
        void BusinessDurableCompletionIdle()
        {
            if (!config.BusinessWorkflow || !IsDurableCapture || !durableAwaitingCloseCleanup || finished || DateTime.UtcNow < nextDurableUtc) return;
            nextDurableUtc = DateTime.UtcNow.AddMilliseconds(500); ObserveDurableCompletion(ReadDurableStatus());
        }
    }
}

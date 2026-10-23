using Autodesk.Revit.DB;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        long[] diagnosticOriginalSubsetIds = Array.Empty<long>();
        long diagnosticLastIdleTicks;
        long diagnosticWindowStartedTicks, diagnosticWindowCallbacks, diagnosticWindowGaps, diagnosticTotalCallbacks;
        long diagnosticWindowGapsOver100, diagnosticWindowGapsOver1000, diagnosticWindowCallbacksOver100;
        double diagnosticWindowGapSum, diagnosticWindowGapMax, diagnosticWindowCallbackSum, diagnosticWindowCallbackMax;
        bool diagnosticFinalIdleAggregateWritten;
        double diagnosticMaximumIdleGapMilliseconds;
        bool DiagnosticLane => config?.DiagnosticPhysicalIds?.Length > 0;
        void LoadDiagnosticSelection()
        {
            if (!config.IsolatedRestoreWorkspace || !config.AllowPartialDurableRestore || !IsDurableRestore)
                throw new InvalidOperationException("Diagnostic lanes require an isolated partial restore workspace.");
            diagnosticOriginalSubsetIds = deletedPhysicalIds.ToArray();
            var ids = config.DiagnosticPhysicalIds;
            if (ids.Length == 0 || ids.Distinct().Count() != ids.Length || ids.Except(diagnosticOriginalSubsetIds).Any())
                throw new InvalidOperationException("Diagnostic IDs changed the original converted physical subset.");
            deletedPhysicalIds = ids.OrderBy(id => id).ToArray();
            partialRestoreEventIds.Clear(); persisted.Clear();
            foreach (var record in partialSelection.Records.Where(record => record.Included && ids.Contains(record.ElementId)))
            { partialRestoreEventIds.Add(record.EventId); persisted.Add(record.ElementId, default); }
            if (persisted.Count != ids.Length) throw new InvalidOperationException("Diagnostic event mapping is incomplete.");
            Write("diagnostic-lane-baseline.json", new { AtUtc = DateTime.UtcNow, LanePhysicalIds = deletedPhysicalIds,
                OriginalSubsetPhysicalIds = diagnosticOriginalSubsetIds, FullDeletedPhysicalIds = fullDeletedPhysicalIds,
                OriginalCaptureDirectory = evidenceDirectory, IsolatedHistoryDirectory = historyDirectory,
                LocalDeletedProject = modelPath, FullRestoreVerified = false, LaneOnly = true });
        }
        void Idle(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs args)
        {
            if (!DiagnosticLane || diagnosticFinalIdleAggregateWritten) { IdleCore(sender, args); return; }
            long started = Stopwatch.GetTimestamp();
            if (diagnosticWindowStartedTicks == 0) diagnosticWindowStartedTicks = started;
            bool hasGap = diagnosticLastIdleTicks != 0;
            double gap = hasGap ? (started - diagnosticLastIdleTicks) * 1000d / Stopwatch.Frequency : 0;
            try { IdleCore(sender, args); }
            finally
            {
                long end = Stopwatch.GetTimestamp(); diagnosticLastIdleTicks = end;
                double callback = (end - started) * 1000d / Stopwatch.Frequency;
                diagnosticWindowCallbacks++; diagnosticTotalCallbacks++;
                diagnosticWindowCallbackSum += callback;
                diagnosticWindowCallbackMax = Math.Max(diagnosticWindowCallbackMax, callback);
                if (callback > 100) diagnosticWindowCallbacksOver100++;
                if (hasGap)
                {
                    diagnosticWindowGaps++; diagnosticWindowGapSum += gap;
                    diagnosticWindowGapMax = Math.Max(diagnosticWindowGapMax, gap);
                    diagnosticMaximumIdleGapMilliseconds = Math.Max(diagnosticMaximumIdleGapMilliseconds, gap);
                    if (gap > 100) diagnosticWindowGapsOver100++;
                    if (gap > 1000) diagnosticWindowGapsOver1000++;
                }
                double period = (end - diagnosticWindowStartedTicks) * 1000d / Stopwatch.Frequency;
                if (period >= 2000 || finished)
                {
                    Record("diagnostic_idling_aggregate", new {
                        PeriodMilliseconds = period, Callbacks = diagnosticWindowCallbacks, TotalCallbacks = diagnosticTotalCallbacks,
                        GapSamples = diagnosticWindowGaps,
                        MeanGapMilliseconds = diagnosticWindowGaps == 0 ? 0 : diagnosticWindowGapSum / diagnosticWindowGaps,
                        MaximumGapMilliseconds = diagnosticWindowGapMax, LifetimeMaximumGapMilliseconds = diagnosticMaximumIdleGapMilliseconds,
                        GapsOver100Milliseconds = diagnosticWindowGapsOver100, GapsOver1000Milliseconds = diagnosticWindowGapsOver1000,
                        MeanCallbackMilliseconds = diagnosticWindowCallbackSum / diagnosticWindowCallbacks,
                        MaximumCallbackMilliseconds = diagnosticWindowCallbackMax, CallbacksOver100Milliseconds = diagnosticWindowCallbacksOver100,
                        Final = finished,
                        Note = "Aggregated intervals include rendering, other addins and OS scheduling; these are not FPS or attributed plugin freezes."
                    });
                    diagnosticWindowStartedTicks = end;
                    diagnosticWindowCallbacks = diagnosticWindowGaps = diagnosticWindowGapsOver100 = diagnosticWindowGapsOver1000 = diagnosticWindowCallbacksOver100 = 0;
                    diagnosticWindowGapSum = diagnosticWindowGapMax = diagnosticWindowCallbackSum = diagnosticWindowCallbackMax = 0;
                    diagnosticFinalIdleAggregateWritten = finished;
                }
            }
        }
        object DiagnosticIfc(Element element)
        {
            if (element == null) return null;
            return new[] { -1019000, -1019001 }.Select(id =>
            {
                var parameter = element.get_Parameter((BuiltInParameter)id);
                return new { BuiltInId = id, Exists = parameter != null, Name = parameter?.Definition?.Name,
                    IsReadOnly = parameter == null ? (bool?)null : parameter.IsReadOnly,
                    HasValue = parameter == null ? (bool?)null : parameter.HasValue,
                    Storage = parameter?.StorageType.ToString(), Value = parameter?.StorageType == StorageType.String ? parameter.AsString() : null };
            }).ToArray();
        }
        object LaneDiagnosticElement(Element element)
        {
            if (element == null) return null;
            var level = element.Document.GetElement(element.LevelId) as Level;
            var type = element.Document.GetElement(element.GetTypeId());
            var location = element.Location; var curve = location as LocationCurve; var point = location as LocationPoint;
            var references = new List<object>();
            foreach (Parameter p in element.Parameters)
                if (p.StorageType == StorageType.ElementId)
                { var target = element.Document.GetElement(p.AsElementId()); references.Add(new { ParameterId = p.Id.Value, Name = p.Definition?.Name, IsReadOnly = p.IsReadOnly, HasValue = p.HasValue,
                    Value = p.AsElementId().Value, TargetUid = target?.UniqueId, TargetClass = target?.GetType().Name, TargetName = target?.Name,
                    Origins = target == null ? Array.Empty<string>() : Origins(target).ToArray(), Elevation = target is Level referencedLevel ? (double?)referencedLevel.Elevation : null,
                    ProjectElevation = target is Level projectLevel ? (double?)projectLevel.ProjectElevation : null }); }
            return new { Id = element.Id.Value, Uid = element.UniqueId, Class = element.GetType().Name, Name = element.Name, Origins = Origins(element),
                LevelId = element.LevelId.Value, LevelUid = level?.UniqueId, LevelName = level?.Name, LevelElevationFeet = level == null ? (double?)null : level.Elevation,
                LevelProjectElevationFeet = level == null ? (double?)null : level.ProjectElevation,
                LevelOrigins = level == null ? Array.Empty<string>() : Origins(level).ToArray(), Point = point == null ? null : P(point.Point),
                Curve = curve == null ? null : new[] { P(curve.Curve.GetEndPoint(0)), P(curve.Curve.GetEndPoint(1)), P(curve.Curve.Evaluate(.5, true)) },
                TypeId = type?.Id.Value, TypeUid = type?.UniqueId, TypeName = type?.Name, TypeClass = type?.GetType().Name,
                TypeOrigins = type == null ? Array.Empty<string>() : Origins(type).ToArray(), TypeIfc = DiagnosticIfc(type), InstanceIfc = DiagnosticIfc(element),
                ReferenceParameters = references, Ports = Connectors(element).Select(ReadPort).ToArray() };
        }
        object ProbeCapsuleTypes(Document document)
        {
            var wanted = config.DiagnosticPhysicalIds.SelectMany(id => new[] { specs[id].TypeUid, specs[id].SystemTypeUid }
                .Concat(specs[id].Ports.Select(p => p.SystemTypeUid)).Concat(specs[id].Ports.SelectMany(p => p.Peers).Select(p => p.SystemTypeUid)))
                .Where(uid => !string.IsNullOrWhiteSpace(uid)).ToHashSet();
            var records = new List<object>(); var found = new HashSet<string>();
            foreach (var type in new FilteredElementCollector(document).WhereElementIsElementType().ToElements())
            {
                var origins = Origins(type); var matched = wanted.Where(uid => uid == type.UniqueId || origins.Contains(uid)).ToArray();
                if (matched.Length == 0) continue;
                foreach (var uid in matched) found.Add(uid);
                try { records.Add(new { SourceTypeUids = matched, Id = type.Id.Value, Uid = type.UniqueId, Class = type.GetType().Name,
                    Name = type.Name, Origins = origins, Ifc = DiagnosticIfc(type), Definition = ReadTypeDefinition(type) }); }
                catch (Exception ex) { records.Add(new { SourceTypeUids = matched, Id = type.Id.Value, Error = ex.ToString() }); }
            }
            var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().Select(level => new {
                Id = level.Id.Value, Uid = level.UniqueId, Name = level.Name, Origins = Origins(level), ElevationFeet = level.Elevation,
                ProjectElevationFeet = level.ProjectElevation }).ToArray();
            return new { TypeRecords = records, SourceTypeUidsAbsentFromThisDocument = wanted.Except(found).ToArray(), Levels = levels };
        }
        void ProbeDiagnosticDestination(Document document, string stage)
        {
            if (!DiagnosticLane) return;
            var watch = Stopwatch.StartNew(); var ids = config.DiagnosticPhysicalIds.Select(id => specs[id].Uid).ToHashSet(); var records = new List<object>();
            foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements())
                if (ids.Contains(element.UniqueId) || Origins(element).Any(ids.Contains))
                    try { records.Add(LaneDiagnosticElement(element)); } catch (Exception ex) { records.Add(new { Id = element.Id.Value, Error = ex.ToString() }); }
            var typeAndLevelProbe = ProbeCapsuleTypes(document);
            Write("diagnostic-destination-" + stage + ".json", new { AtUtc = DateTime.UtcNow, Stage = stage, DocumentPath = document.PathName, TypeAndLevelProbe = typeAndLevelProbe,
                Source = config.DiagnosticPhysicalIds.Select(id => specs[id]).ToArray(), Records = records, Milliseconds = watch.Elapsed.TotalMilliseconds,
                FullRestoreVerified = false, OriginalSubsetPhysicalIds = diagnosticOriginalSubsetIds, FullDeletedPhysicalIds = fullDeletedPhysicalIds });
            Record("diagnostic_probe_api_span", new { Stage = stage, Milliseconds = watch.Elapsed.TotalMilliseconds });
        }
        int diagnosticOpenedCapsules;
        void ProbeDiagnosticRequests(IList requests)
        {
            if (!DiagnosticLane) return;
            Write("diagnostic-resolved-requests.json", new { AtUtc = DateTime.UtcNow, Records = requests.Cast<object>().Select(request => new {
                SourceUid = Read(request, "SourceUniqueId"), Kind = Read(Read(request, "Recipe"), "Kind"),
                Artifacts = ArtifactsOf(Read(request, "Recipe")).Select(artifact => new { PayloadId = Read(artifact, "PayloadId"), TypeVersion = Read(artifact, "TypeVersion"), RootSourceUniqueId = Read(artifact, "RootSourceUniqueId") }).ToArray() }).ToArray(),
                SourceBaseline = config.DiagnosticPhysicalIds.Select(id => specs[id]).ToArray(),
                SourceIfcReadOnlyAvailability = "Original immutable baseline did not record IFC IsReadOnly/HasValue. Capsule and destination API reads are separate; missing source flags remain unknown." });
        }
        void ProbeDiagnosticOpenedCapsule(Document document)
        {
            if (!DiagnosticLane) return;
            var watch = Stopwatch.StartNew(); var wanted = config.DiagnosticPhysicalIds.Select(id => specs[id].Uid).ToHashSet(); var records = new List<object>();
            foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements())
                if (wanted.Contains(element.UniqueId) || Origins(element).Any(wanted.Contains))
                    try { records.Add(LaneDiagnosticElement(element)); } catch (Exception ex) { records.Add(new { Id = element.Id.Value, Error = ex.ToString() }); }
            var typeAndLevelProbe = ProbeCapsuleTypes(document);
            int number = ++diagnosticOpenedCapsules;
            Write("diagnostic-capsule-opened-" + number + ".json", new { AtUtc = DateTime.UtcNow, Stage = "Actual production-owned capsule DocumentOpened before import",
                OwnedByProduction = true, Records = records, TypeAndLevelProbe = typeAndLevelProbe, Milliseconds = watch.Elapsed.TotalMilliseconds, SourceBaseline = config.DiagnosticPhysicalIds.Select(id => specs[id]).ToArray(),
                Note = "Observer opens and materializes no archive; only the source document already opened by the real Restore is read. JSON IO runs in the observer worker." });
            Record("diagnostic_probe_api_span", new { Stage = "actual-capsule-DocumentOpened", Number = number, Milliseconds = watch.Elapsed.TotalMilliseconds });
        }
    }
}

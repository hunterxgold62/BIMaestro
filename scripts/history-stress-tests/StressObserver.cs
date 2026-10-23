using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BIMaestro.StressTests
{
    // This observer never invokes the inventory scheduler or manufactures a history event.
    // The explicit durable scenario invokes only the production converter, one wave per Idling.
    public sealed partial class StressObserver : IExternalApplication, IExternalEventHandler
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const double Tolerance = 1e-5;
        // IDs are the exact generated/internal categories observed in the original catalogue.
        // Do not exclude every base Element: other categories may contain real model geometry.
        static readonly HashSet<long> ProtectedGeneratedCategories = new()
        {
            (long)BuiltInCategory.OST_Cameras,
            (long)BuiltInCategory.OST_PreviewLegendComponents,
            (long)BuiltInCategory.OST_ConduitFittingCenterLine,
            (long)BuiltInCategory.OST_ConduitCenterLine,
            (long)BuiltInCategory.OST_PipeFittingCenterLine,
            (long)BuiltInCategory.OST_DuctFittingCenterLine,
            (long)BuiltInCategory.OST_FlexPipeCurvesCenterLine,
            (long)BuiltInCategory.OST_PipeCurvesCenterLine,
            (long)BuiltInCategory.OST_DuctCurvesCenterLine
        };
        readonly ConcurrentQueue<object> rows = new();
        readonly ConcurrentQueue<(string Name, object Value)> exports = new();
        readonly ConcurrentQueue<string> actions = new();
        readonly Dictionary<string, long> requests = new();
        readonly Dictionary<long, Spec> specs = new();
        readonly Dictionary<long, CatalogEntry> catalog = new();
        readonly Dictionary<long, long> sketchOwners = new();
        readonly List<LogicalGroupRoot> logicalGroupRoots = new();
        readonly HashSet<long> changedDeleted = new(), changedModified = new(), changedAdded = new();
        readonly Dictionary<long, JsonElement> persisted = new();
        readonly HashSet<string> batchIds = new();
        UIControlledApplication controlled;
        UIApplication ui;
        ExternalEvent external;
        CancellationTokenSource cancellation;
        Task worker;
        Assembly product;
        Type tracker, opening, native, restoration;
        Document doc;
        Config config;
        string folder, modelPath, restoredPath, docKey, transactionName;
        string phase = "waiting-open";
        DateTime openedUtc, committedUtc, nextStateUtc, lastJournalChangeUtc;
        bool initialized, deleteQueued, deleted, finished, originalDetailed, journalReady;
        int opened, restoring, restoreChangedAdded, refreshCount, preparedTimedOut;
        long[] selectedPhysicalIds = Array.Empty<long>(), returnedIds = Array.Empty<long>(), changedIds = Array.Empty<long>(), deletedPhysicalIds = Array.Empty<long>();
        object lastState, lastResult;
        DateTime lastHeartbeat;

        public sealed class Config
        {
            public string Scenario { get; set; } = "early";
            public string RunName { get; set; }
            public int PreparedTimeoutSeconds { get; set; } = 300;
            public int JournalTimeoutSeconds { get; set; } = 180;
            public string ReferenceInventoryPath { get; set; }
            public string ReferenceOwnershipPath { get; set; }
            public string DurablePhase { get; set; } = "capture";
            public string DurableCaptureDirectory { get; set; }
            public int DeletionDelaySeconds { get; set; }
            public double PhysicalTargetFraction { get; set; } = 1;
            public bool ApplyDeterministicEdits { get; set; }
            public string[] SkipPhysicalCategories { get; set; } = Array.Empty<string>();
            public long[] SkipPhysicalIds { get; set; } = Array.Empty<long>();
            public string WitnessExclusionReason { get; set; }
            public bool AllowPartialDurableRestore { get; set; }
            public bool BusinessWorkflow { get; set; }
            public bool RapidModifiedDelete { get; set; }
            public long[] DiagnosticPhysicalIds { get; set; } = Array.Empty<long>();
            public bool IsolatedRestoreWorkspace { get; set; }
            public int WorkAfterEditsSeconds { get; set; } = 45;
            public int BusinessSelectionCount { get; set; } = 16;
            public int BusinessReadinessTimeoutSeconds { get; set; } = 600;
        }
        public sealed class CatalogEntry
        {
            public long Id { get; set; }
            public string Uid { get; set; }
            public string Class { get; set; }
            public string Category { get; set; }
            public long? CategoryId { get; set; }
            public string Name { get; set; }
            public bool ElementType { get; set; }
            public string ProtectionReason { get; set; }
        }
        public sealed class LogicalGroupRoot
        {
            public long Id { get; set; }
            public string Uid { get; set; }
            public long[] PhysicalLeafIds { get; set; }
            public long[] NestedGroupIds { get; set; }
        }
        public sealed class Peer
        {
            public string Uid { get; set; }
            public int PortId { get; set; }
            public double[] Point { get; set; }
            public string Domain { get; set; }
            public string Type { get; set; }
            public string Shape { get; set; }
            public string Classification { get; set; }
            public string SystemTypeUid { get; set; }
        }
        public sealed class Port
        {
            public int Id { get; set; }
            public string Domain { get; set; }
            public string Type { get; set; }
            public string Shape { get; set; }
            public string Classification { get; set; }
            public string SystemTypeUid { get; set; }
            public double[] Point { get; set; }
            public List<Peer> Peers { get; set; } = new();
        }
        public sealed class Spec
        {
            public long Id { get; set; }
            public string Uid { get; set; }
            public string Class { get; set; }
            public string Category { get; set; }
            public long? CategoryId { get; set; }
            public string Name { get; set; }
            public string TypeUid { get; set; }
            public string TypeName { get; set; }
            public string FamilyName { get; set; }
            public bool TypeInPlace { get; set; }
            public bool Selected { get; set; }
            public string ProtectionReason { get; set; }
            public string SelectionExclusionReason { get; set; }
            public string RootUid { get; set; }
            public string HostUid { get; set; }
            public string GroupUid { get; set; }
            public string SuperUid { get; set; }
            public string LevelUid { get; set; }
            public double[] Point { get; set; }
            public double[][] Curve { get; set; }
            public double[][] BBox { get; set; }
            public double[][] Transform { get; set; }
            public Dictionary<string, double> Dimensions { get; set; } = new();
            public List<Port> Ports { get; set; } = new();
            public List<string> JoinedUids { get; set; } = new();
            public bool JoinQuerySucceeded { get; set; }
            public string SystemTypeUid { get; set; }
            public List<string> ConnectedPeerSystemTypeUids { get; set; } = new();
            public List<string> ProbeErrors { get; set; } = new();
            public List<ExcavationRelation> Excavations { get; set; } = new();
            public bool ExcavationQuerySucceeded { get; set; }
            public List<WallJoinEnd> WallJoins { get; set; } = new();
            public List<TypeParameter> InstanceParameters { get; set; }
            public List<string> InstanceParameterErrors { get; set; }
            public bool? Pinned { get; set; }
        }
        sealed class DeleteFailures : IFailuresPreprocessor
        {
            public List<string> Messages { get; } = new();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var failures = accessor.GetFailureMessages();
                Messages.AddRange(failures.Select(f => f.GetSeverity() + ": " + f.GetDescriptionText()));
                if (failures.Any(f => f.GetSeverity() != FailureSeverity.Warning)) return FailureProcessingResult.ProceedWithRollBack;
                foreach (var failure in failures) accessor.DeleteWarning(failure);
                return FailureProcessingResult.Continue;
            }
        }
        static object Read(object obj, string name)
        {
            if (obj == null) return null;
            var type = obj as Type ?? obj.GetType(); var instance = obj is Type ? null : obj;
            return type.GetField(name, Flags)?.GetValue(instance) ?? type.GetProperty(name, Flags)?.GetValue(instance);
        }
        static object Call(Type type, string name, params object[] args)
        {
            var methods = type.GetMethods(Flags).Where(m => m.Name == name && m.GetParameters().Length == args.Length).ToList();
            var method = methods.FirstOrDefault(m => m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(v => v))
                ?? throw new MissingMethodException(type.FullName, name);
            return method.Invoke(null, args);
        }
        static T Safe<T>(Func<T> action, T fallback = default) { try { return action(); } catch { return fallback; } }
        static string Sha(string path) { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(s)); }
        static double[] P(XYZ p) => p == null ? null : new[] { p.X, p.Y, p.Z };
        static double Distance(double[] a, double[] b) => a == null || b == null ? double.PositiveInfinity : Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
        static bool Same(double[] a, double[] b) => a == null && b == null || a != null && b != null && Distance(a, b) <= Tolerance;
        static bool SamePoints(double[][] a, double[][] b) => a == null && b == null || a != null && b != null && a.Length == b.Length && a.Zip(b, Same).All(v => v);
        void Record(string kind, object data = null) => rows.Enqueue(new { Kind = kind, AtUtc = DateTime.UtcNow, Data = data });
        void Write(string name, object value) => exports.Enqueue((name, value));
        bool IsCopy(Document candidate) => candidate != null && (string.Equals(candidate.PathName, modelPath, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate.PathName, restoredPath, StringComparison.OrdinalIgnoreCase)
            || IsDurable && string.Equals(candidate.PathName, durableDeletedPath, StringComparison.OrdinalIgnoreCase));
        public string GetName() => "BIMaestro isolated Antibes mass-deletion stress observer";
        public Result OnStartup(UIControlledApplication application)
        {
            controlled = application; folder = Path.GetDirectoryName(GetType().Assembly.Location);
            modelPath = Path.Combine(folder, "Antibes-stress.rvt"); restoredPath = Path.Combine(folder, "Antibes-restaure.rvt");
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(folder, "config.json")));
            ConfigureDurablePaths();
            transactionName = "BIMaestroStress " + config.RunName + " mass delete";
            controlled.Idling += Idle; controlled.ControlledApplication.DocumentOpened += Opened;
            controlled.ControlledApplication.DocumentChanged += Changed;
            external = ExternalEvent.Create(this); return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication application)
        {
            controlled.Idling -= Idle; controlled.ControlledApplication.DocumentOpened -= Opened; controlled.ControlledApplication.DocumentChanged -= Changed;
            cancellation?.Cancel(); try { worker?.Wait(2000); } catch { }
            ReleaseRapidSources();
            external?.Dispose(); return Result.Succeeded;
        }
        void IdleCore(object sender, IdlingEventArgs args)
        {
            ui = sender as UIApplication;
            if (!initialized)
            {
                var pidPath = Path.Combine(folder, "observer-pid.txt");
                if (!File.Exists(pidPath)) return;
                if (File.ReadAllText(pidPath).Trim() != Environment.ProcessId.ToString()) { controlled.Idling -= Idle; return; }
                try
                {
                    if (ui.Application.Documents.Size != 0) throw new InvalidOperationException("Expected an empty isolated process.");
                    product = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("BIMaestroApp") != null);
                    tracker = product.GetType("Analyse.ElementHistoryTracker", true); opening = product.GetType("Analyse.ElementHistoryOpeningArchive", true);
                    native = product.GetType("Analyse.ElementHistoryNativeArchive", true); restoration = product.GetType("Analyse.ElementHistoryRestoration", true);
                    if (IsDurable) durableBridge = product.GetType("Analyse.ElementHistoryDurableBridge", true);
                    var store = product.GetType("Analyse.CollaborativeModelTrackerStore", true);
                    Directory.CreateDirectory(historyDirectory);
                    store.GetField("_activeDirectory", Flags).SetValue(null, historyDirectory);
                    originalDetailed = (bool)Read(tracker, "_captureDetailedDeletedMesh");
                    if (config.Scenario == "prepared") tracker.GetField("_captureDetailedDeletedMesh", Flags).SetValue(null, true);
                    if (config.BusinessWorkflow) tracker.GetField("_captureDetailedDeletedMesh", Flags).SetValue(null, false); if (IsDurableRestore) LoadDurableBaseline();
                    InitializeRapidCycle();
                    cancellation = new CancellationTokenSource(); worker = Task.Run(() => Worker(cancellation.Token)); initialized = true; if (config.BusinessWorkflow && IsDurableCapture) Call(durableBridge, "EnableIsolatedAutomaticPreparation");
                    var ready = new { Pid = Environment.ProcessId, MainWindowHandle = ui.MainWindowHandle.ToInt64(), Product = product.Location, Sha256 = Sha(product.Location), Model = modelPath, HistoryDirectory = historyDirectory, ManualHistoryMode = Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST"), Config = config, OriginalDetailed = originalDetailed, RuntimeDetailed = Read(tracker, "_captureDetailedDeletedMesh") };
                    Record("ready", ready); Write("ready.json", ready);
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); controlled.Idling -= Idle; }
            }
            if (initialized && !finished)
            {
                try { PartialIdle(); }
                catch (Exception ex) { partialAwaitingSessionClose = false; phase = "partial-close-error"; Record("partial_close_error", ex.ToString()); Write("partial-error.json", new { AtUtc = DateTime.UtcNow, Error = ex.ToString() }); }
                DurableIdle(ui); BusinessReadinessIdle(); RapidIdle();
            }
            if (!initialized || finished || doc == null || !doc.IsValidObject || !IsCopy(doc) || DateTime.UtcNow < nextStateUtc) return;
            nextStateUtc = DateTime.UtcNow.AddMilliseconds(500);
            try
            {
                var watch = Stopwatch.StartNew(); lastState = State(doc); Record("production_state", lastState); Record("state_probe_cost", watch.Elapsed.TotalMilliseconds);
                DelayedIdle(lastState); BusinessIdle(lastState);
                if (config.Scenario == "prepared" && !deleteQueued && !deleted)
                {
                    bool complete = IsPrepared(doc);
                    bool timeout = (DateTime.UtcNow - openedUtc).TotalSeconds >= config.PreparedTimeoutSeconds;
                    if (complete || timeout) { preparedTimedOut = timeout && !complete ? 1 : 0; deleteQueued = true; actions.Enqueue("delete"); Record("prepared_delete_armed", new { Complete = complete, TimedOut = preparedTimedOut == 1, State = lastState }); }
                }
            }
            catch (Exception ex) { Record("state_error", ex.ToString()); }
        }
        void Opened(object sender, DocumentOpenedEventArgs args)
        {
            if (initialized && DiagnosticLane && (bool)Call(native, "Owns", args.Document)) { try { ProbeDiagnosticOpenedCapsule(args.Document); } catch (Exception ex) { Record("diagnostic_capsule_probe_error", ex.ToString()); } return; } if (!initialized || !IsCopy(args.Document)) return;
            doc = args.Document;
            if (string.Equals(doc.PathName, modelPath, StringComparison.OrdinalIgnoreCase) && Interlocked.CompareExchange(ref opened, 1, 0) == 0)
            {
                var currentKey = (string)Call(tracker, "GetDocumentKeyForHistory", doc);
                if (!IsDurableRestore) { openedUtc = DateTime.UtcNow; docKey = currentKey; }
                Record("document_opened", new { doc.Title, doc.IsModified, State = State(doc), CurrentDocumentKey = currentKey, HistoryDocumentKey = docKey, DurableRestore = IsDurableRestore });
                if (config.RapidModifiedDelete) { actions.Enqueue(IsDurableRestore ? "restore" : "rapid-edit"); }
                else if (config.Scenario == "early" || IsDurableCapture && !HasDelayedDeletion && !config.BusinessWorkflow) { deleteQueued = true; actions.Enqueue("delete"); }
            }
            else Record("restored_document_opened", new { doc.Title, doc.PathName });
        }
        void Changed(object sender, DocumentChangedEventArgs args)
        {
            if (!initialized || !IsCopy(args.GetDocument())) return;
            var names = args.GetTransactionNames().ToArray(); var added = args.GetAddedElementIds().Select(id => id.Value).ToArray();
            var removed = args.GetDeletedElementIds().Select(id => id.Value).ToArray(); var modified = args.GetModifiedElementIds().Select(id => id.Value).ToArray();
            Record("document_changed", new { Transactions = names, Added = added, Modified = modified, Deleted = removed });
            DelayedChanged(names, added, modified);
            if (names.Contains(transactionName))
            {
                foreach (var id in removed) changedDeleted.Add(id);
                foreach (var id in modified) changedModified.Add(id);
                foreach (var id in added) changedAdded.Add(id);
            }
            if (Volatile.Read(ref restoring) == 1) Interlocked.Add(ref restoreChangedAdded, added.Length);
        }
        object State(Document document)
        {
            var key = (string)Call(tracker, "GetDocumentKeyForHistory", document);
            var audits = (IDictionary)Read(tracker, "PreloadAuditByDocumentKey"); var audit = audits.Contains(key) ? audits[key] : null;
            var captures = (IDictionary)Read(tracker, "RestorationCaptureByDocumentKey"); var capture = captures.Contains(key) ? captures[key] : null;
            var archive = OpeningState(document);
            var nativeArgs = new object[] { document, 0, 0 }; native.GetMethod("GetAuditCounts", Flags).Invoke(null, nativeArgs);
            return new { Key = key, Snapshots = Read(Read(tracker, "SnapshotByElementId"), "Count"), Queue = Read(Read(tracker, "Queue"), "Count"), AuditQueue = Read(Read(tracker, "AuditQueue"), "Count"), Detailed = Read(tracker, "_captureDetailedDeletedMesh"), Preload = audit == null ? null : new { Complete = Read(audit, "Complete"), Visited = Read(audit, "Visited"), Total = Read(audit, "TotalCandidates"), Captured = Read(audit, "Captured"), Failed = Read(audit, "Failed") }, Capture = capture == null ? null : new { Pending = Read(Read(capture, "Pending"), "Count"), LightPending = Read(Read(capture, "LightPending"), "Count") }, NativePendingRoots = nativeArgs[1], NativeReadyRecipes = nativeArgs[2], Opening = archive == null ? null : new { Ready = Read(archive, "Ready"), Failure = Read(archive, "Failure"), RequestedUtc = Read(archive, "RequestedUtc"), ReadyUtc = Read(archive, "ReadyUtc") } };
        }
        bool IsPrepared(Document document)
        {
            var key = (string)Call(tracker, "GetDocumentKeyForHistory", document);
            var audit = ((IDictionary)Read(tracker, "PreloadAuditByDocumentKey"))[key];
            var captures = (IDictionary)Read(tracker, "RestorationCaptureByDocumentKey"); var capture = captures.Contains(key) ? captures[key] : null;
            var args = new object[] { document, 0, 0 }; native.GetMethod("GetAuditCounts", Flags).Invoke(null, args);
            var archive = OpeningState(document);
            return (bool?)Read(audit, "Complete") == true && Convert.ToInt32(Read(Read(capture, "Pending"), "Count") ?? 0) == 0
                && Convert.ToInt32(Read(Read(capture, "LightPending"), "Count") ?? 0) == 0 && (int)args[1] == 0 && (bool?)Read(archive, "Ready") == true;
        }
        object OpeningState(Document document)
        {
            // Revit callbacks can expose different wrappers for the same live
            // document. Reference-keyed collections protect closed wrappers, but
            // lookup must use the production live-document identity resolver.
            var resolver = opening.GetMethods(Flags).FirstOrDefault(method => (method.Name == "TryGetState" || method.Name == "TryState")
                && method.GetParameters().Length == 2 && method.GetParameters()[1].IsOut);
            if (resolver != null)
            {
                var args = new object[] { document, null };
                return (bool)resolver.Invoke(null, args) ? args[1] : null;
            }
            // Older frozen products used Document.Equals in this dictionary.
            var states = (IDictionary)Read(opening, "States");
            return states.Contains(document) ? states[document] : null;
        }
        string Protection(Element element)
        {
            if (element is ElementType) return "definition:type";
            if (element is View || element.ViewSpecific || element.OwnerViewId != ElementId.InvalidElementId) return "view-or-view-specific";
            if (element is DatumPlane || element is SketchPlane || element is BasePoint || element.GetType().Name == "InternalOrigin") return "datum-or-origin";
            if (element is RevitLinkInstance || element is ImportInstance import && import.IsLinked || element.GetType().Name == "PointCloudInstance") return "external-link";
            if (element is Family || element is Material || element is GraphicsStyle || element is ProjectInfo || element is ParameterElement || element.GetType().Name == "DataStorage" || element is MEPSystem) return "definition-or-system";
            if (element.GetType().Name == "PropertySetElement") return "definition:property-set";
            if (element.GetType().Name == "SunAndShadowSettings") return "definition:sun-settings";
            if (element.GetType().Name is "ConduitRun" or "Zone" || element is SpatialElement) return "logical-model-record";
            if (element.Category?.CategoryType != CategoryType.Model) return "not-model-category";
            if (ProtectedGeneratedCategories.Contains(element.Category.Id.Value)) return "generated-model-category";
            if (element.GetType() == typeof(Element) && element.Category.Id.Value == (long)BuiltInCategory.OST_TopographyContours) return "generated-model-category";
            if (sketchOwners.ContainsKey(element.Id.Value)) return "sketch-owner-dependent";
            if (Safe(() => element.Name)?.StartsWith("BIMaestro_HistoryPreview", StringComparison.OrdinalIgnoreCase) == true) return "diagnostic-preview";
            if (!(bool)Call(opening, "IsEligibleElement", element)) return "production-ineligible";
            return null;
        }
        List<Connector> Connectors(Element element)
        {
            ConnectorSet set = null;
            if (element is MEPCurve curve) set = curve.ConnectorManager?.Connectors;
            else if (element is FamilyInstance family) set = family.MEPModel?.ConnectorManager?.Connectors;
            else set = Read(Read(element, "ConnectorManager"), "Connectors") as ConnectorSet;
            return set == null ? new List<Connector>() : set.Cast<Connector>().Where(c => c.ConnectorType != ConnectorType.Logical).ToList();
        }
        Port ReadPort(Connector connector)
        {
            var port = new Port { Id = connector.Id, Domain = Safe(() => connector.Domain.ToString()), Type = connector.ConnectorType.ToString(), Shape = Safe(() => connector.Shape.ToString()), Point = Safe(() => P(connector.Origin)) };
            port.Classification = connector.Domain == Domain.DomainHvac ? Safe(() => connector.DuctSystemType.ToString()) : connector.Domain == Domain.DomainPiping ? Safe(() => connector.PipeSystemType.ToString()) : connector.Domain == Domain.DomainElectrical ? Safe(() => connector.ElectricalSystemType.ToString()) : null;
            var ownerSystem = Safe(() => connector.MEPSystem);
            var ownerSystemType = ownerSystem == null ? Safe(() => (Element)Call(native, "ReadMepSystemType", connector.Owner)) : connector.Owner.Document.GetElement(ownerSystem.GetTypeId());
            port.SystemTypeUid = ownerSystemType is MEPSystemType ? ownerSystemType.UniqueId : null;
            if (Safe(() => connector.IsConnected))
                foreach (Connector peer in connector.AllRefs)
                    if (peer.Owner.Id != connector.Owner.Id && peer.ConnectorType != ConnectorType.Logical && Safe(() => connector.IsConnectedTo(peer)))
                    {
                        string classification = peer.Domain == Domain.DomainHvac ? Safe(() => peer.DuctSystemType.ToString()) : peer.Domain == Domain.DomainPiping ? Safe(() => peer.PipeSystemType.ToString()) : peer.Domain == Domain.DomainElectrical ? Safe(() => peer.ElectricalSystemType.ToString()) : null;
                        var system = Safe(() => peer.MEPSystem);
                        var systemType = system == null ? Safe(() => (Element)Call(native, "ReadMepSystemType", peer.Owner)) : peer.Owner.Document.GetElement(system.GetTypeId());
                        port.Peers.Add(new Peer { Uid = peer.Owner.UniqueId, PortId = peer.Id, Point = Safe(() => P(peer.Origin)), Domain = Safe(() => peer.Domain.ToString()), Type = peer.ConnectorType.ToString(), Shape = Safe(() => peer.Shape.ToString()), Classification = classification, SystemTypeUid = systemType is MEPSystemType ? systemType.UniqueId : null });
                    }
            return port;
        }
        Spec Capture(Element element, bool selected = false, string protection = null)
        {
            var type = element.Document.GetElement(element.GetTypeId());
            var value = new Spec { Id = element.Id.Value, Uid = element.UniqueId, Class = element.GetType().Name, Category = element.Category?.Name, CategoryId = element.Category?.Id.Value, Name = Safe(() => element.Name), TypeUid = type?.UniqueId, TypeName = Safe(() => type?.Name), FamilyName = (type as FamilySymbol)?.FamilyName, TypeInPlace = Safe(() => (type as FamilySymbol)?.Family?.IsInPlace ?? false), Selected = selected, ProtectionReason = protection, GroupUid = Safe(() => element.Document.GetElement(element.GroupId)?.UniqueId), LevelUid = Safe(() => element.Document.GetElement(element.LevelId)?.UniqueId) };
            try
            {
                var root = (Element)Call(native, "OpeningRoot", element); value.RootUid = root?.UniqueId;
                if (element is FamilyInstance instance)
                {
                    value.HostUid = instance.Host?.UniqueId; value.SuperUid = instance.SuperComponent?.UniqueId;
                    var transform = instance.GetTransform(); value.Transform = new[] { P(transform.Origin), P(transform.BasisX), P(transform.BasisY), P(transform.BasisZ) };
                }
                if (element.Location is LocationPoint point) value.Point = P(point.Point);
                if (element.Location is LocationCurve curve) value.Curve = new[] { P(curve.Curve.GetEndPoint(0)), P(curve.Curve.GetEndPoint(1)), P(curve.Curve.Evaluate(.5, true)) };
            }
            catch (Exception ex) { value.ProbeErrors.Add("placement:" + ex.Message); }
            try { var box = element.get_BoundingBox(null); if (box != null) value.BBox = new[] { P(box.Min), P(box.Max) }; } catch (Exception ex) { value.ProbeErrors.Add("bbox:" + ex.Message); }
            foreach (var name in new[] { "RBS_PIPE_DIAMETER_PARAM", "RBS_CURVE_DIAMETER_PARAM", "RBS_CURVE_WIDTH_PARAM", "RBS_CURVE_HEIGHT_PARAM" })
                if (Enum.TryParse<BuiltInParameter>(name, out var builtIn)) { var p = element.get_Parameter(builtIn); if (p?.StorageType == StorageType.Double) value.Dimensions[name] = p.AsDouble(); }
            try { value.Ports = Connectors(element).Select(ReadPort).ToList(); } catch (Exception ex) { value.ProbeErrors.Add("connectors:" + ex.Message); }
            try { value.JoinedUids = JoinGeometryUtils.GetJoinedElements(element.Document, element).Select(id => element.Document.GetElement(id)?.UniqueId).Where(uid => uid != null).OrderBy(uid => uid).ToList(); value.JoinQuerySucceeded = true; } catch (Exception ex) { value.ProbeErrors.Add("joins-unverified:" + ex.Message); }
            if (element is Wall wall && wall.Location is LocationCurve wallLocation)
                for (int end = 0; end < 2; end++)
                    try
                    {
                        value.WallJoins.Add(new WallJoinEnd { End = end, JoinAllowed = WallUtils.IsWallJoinAllowedAtEnd(wall, end),
                            JoinType = wallLocation.get_JoinType(end).ToString(),
                            Participants = wallLocation.get_ElementsAtJoin(end).Cast<Element>().Select(peer => peer.UniqueId).ToArray() });
                    }
                    catch (Exception ex) { value.ProbeErrors.Add("wall-join-order-unverified:" + end + ":" + ex.Message); }
            try { if ((bool)Call(native, "HasMepSystemType", element)) value.SystemTypeUid = ((Element)Call(native, "ReadMepSystemType", element))?.UniqueId; } catch (Exception ex) { value.ProbeErrors.Add("system:" + ex.Message); }
            string networkDomain = element.GetType().Name is "Duct" or "FlexDuct" ? Domain.DomainHvac.ToString() : element.GetType().Name is "Pipe" or "FlexPipe" ? Domain.DomainPiping.ToString() : null;
            if (networkDomain != null) value.ConnectedPeerSystemTypeUids = value.Ports.Where(p => p.Domain == networkDomain && p.Type is "End" or "Curve" or "Physical")
                .SelectMany(p => p.Peers).Where(p => p.Domain == networkDomain && p.Type is "End" or "Curve" or "Physical").Select(p => p.SystemTypeUid).Where(uid => uid != null).Distinct().OrderBy(uid => uid).ToList();
            value.Excavations = excavationByUid.TryGetValue(element.UniqueId, out var excavationPairs) ? excavationPairs.ToList() : new List<ExcavationRelation>();
            value.ExcavationQuerySucceeded = excavationProbeErrors.Count == 0;
            if (excavationProbeErrors.Count > 0) value.ProbeErrors.AddRange(excavationProbeErrors.Select(error => "excavation-unverified:" + error)); if (config.BusinessWorkflow || config.RapidModifiedDelete && (IsDurableRestore || rapidIds.Contains(element.Id.Value))) { var parameters = ReadTypeDefinition(element); value.InstanceParameters = parameters.Parameters; value.InstanceParameterErrors = parameters.Errors; value.Pinned = element.Pinned; }
            return value;
        }
        internal static bool TryPhysicalGroupRoot(Document document, Group root, ISet<long> selectedIds, out LogicalGroupRoot result)
        {
            var leaves = new HashSet<long>(); var groups = new HashSet<long>(); var visiting = new HashSet<long>();
            bool Visit(Group group)
            {
                if (!visiting.Add(group.Id.Value)) return false;
                groups.Add(group.Id.Value);
                var members = group.GetMemberIds(); if (members.Count == 0) return false;
                foreach (var memberId in members)
                {
                    var member = document.GetElement(memberId);
                    if (member is Group nested) { if (!Visit(nested)) return false; }
                    else if (member != null && !(member is ElementType) && selectedIds.Contains(memberId.Value)) leaves.Add(memberId.Value);
                    else return false;
                }
                visiting.Remove(group.Id.Value); return true;
            }
            bool valid = root != null && Visit(root) && leaves.Count > 0;
            result = valid ? new LogicalGroupRoot { Id = root.Id.Value, Uid = root.UniqueId, PhysicalLeafIds = leaves.OrderBy(id => id).ToArray(), NestedGroupIds = groups.OrderBy(id => id).ToArray() } : null;
            return valid;
        }
        void Inventory(Document document)
        {
            phase = "inventory"; var watch = Stopwatch.StartNew(); var before = State(document);
            RefreshExcavations(document);
            var all = new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements()
                .Concat(new FilteredElementCollector(document).WhereElementIsElementType().ToElements()).ToList();
            foreach (var sketch in all.OfType<Sketch>()) foreach (var id in sketch.GetAllElements()) sketchOwners[id.Value] = sketch.OwnerId.Value;
            foreach (var element in all)
            {
                var protection = Protection(element);
                catalog[element.Id.Value] = new CatalogEntry { Id = element.Id.Value, Uid = element.UniqueId, Class = element.GetType().Name, Category = element.Category?.Name, CategoryId = element.Category?.Id.Value, Name = Safe(() => element.Name), ElementType = element is ElementType, ProtectionReason = protection };
                if (!(element is ElementType) && element.Category?.CategoryType == CategoryType.Model && !element.ViewSpecific)
                {
                    var spec = Capture(element, protection == null, protection);
                    if (spec.Selected && spec.Point == null && spec.Curve == null && spec.BBox == null && spec.Transform == null)
                    {
                        spec.Selected = false; spec.ProtectionReason = "no-verifiable-physical-placement";
                        catalog[element.Id.Value].ProtectionReason = spec.ProtectionReason;
                    }
                    specs[element.Id.Value] = spec;
                }
            }
            selectedPhysicalIds = specs.Values.Where(s => s.Selected).Select(s => s.Id).OrderBy(id => id).ToArray();
            ApplyPhysicalSelection(document);
            var eligible = new HashSet<long>(selectedPhysicalIds); var roots = new HashSet<long>(); var catalogByUid = catalog.Values.ToDictionary(c => c.Uid);
            foreach (var spec in specs.Values.Where(s => s.Selected))
            {
                var root = spec.RootUid != null && catalogByUid.TryGetValue(spec.RootUid, out var rootEntry) ? rootEntry : null;
                if (root != null && document.GetElement(new ElementId(root.Id)) is Group group
                    && TryPhysicalGroupRoot(document, group, eligible, out var logical))
                {
                    roots.Add(logical.Id);
                    if (!logicalGroupRoots.Any(g => g.Id == logical.Id)) logicalGroupRoots.Add(logical);
                }
                else roots.Add(root != null && eligible.Contains(root.Id) ? root.Id : spec.Id);
            }
            var snapshots = (IDictionary)Read(tracker, "SnapshotByElementId");
            var coverage = specs.Values.Where(s => s.Selected).Select(s =>
            {
                var key = (string)Call(tracker, "BuildElementSnapshotKey", document, new ElementId(s.Id)); var snapshot = snapshots[key]; var recipe = Read(snapshot, "Recipe");
                var archive = Read(recipe, "Native"); var backup = Read(recipe, "OpeningFallback"); var placement = Read(recipe, "HistoricalPlacement");
                return new
                {
                    s.Id, s.Uid, SnapshotPresent = snapshot != null, Kind = Read(recipe, "Kind"), OpeningFallback = backup != null,
                    DetailCaptureAttempted = Read(snapshot, "DetailCaptureAttempted"), CaptureFailure = Read(snapshot, "CaptureFailure"),
                    RecipeHistoricalPlacement = placement == null ? (JsonElement?)null : JsonSerializer.SerializeToElement(placement, placement.GetType()),
                    NativeArchive = archive == null ? null : new { File = Read(archive, "File"), Ready = Read(archive, "Ready"), WholeModel = Read(archive, "WholeModel"), SourceElementId = Read(archive, "SourceElementId"), RootUniqueId = Read(archive, "RootUniqueId") },
                    OpeningArchive = backup == null ? null : new { File = Read(backup, "File"), Ready = Read(backup, "Ready"), WholeModel = Read(backup, "WholeModel"), SourceElementId = Read(backup, "SourceElementId"), RootUniqueId = Read(backup, "RootUniqueId") }
                };
            }).ToArray();
            if (IsDurableCapture) CaptureSourceTypeDefinitions(document);
            Write("inventory.json", new { CapturedUtc = DateTime.UtcNow, Milliseconds = watch.Elapsed.TotalMilliseconds, StateBefore = before, StateAfter = State(document), CatalogCount = catalog.Count, EligibleCount = selectedPhysicalIds.Length, PhysicalEligibleIds = physicalCandidateIds, ExplicitlyExcludedPhysicalIds = explicitlyExcludedPhysicalIds, SelectedRootIds = roots.OrderBy(id => id).ToArray(), LogicalGroupRoots = logicalGroupRoots.ToArray(), ProtectedCounts = catalog.Values.Where(c => c.ProtectionReason != null).GroupBy(c => c.ProtectionReason).ToDictionary(g => g.Key, g => g.Count()), Catalog = catalog.Values.ToArray(), Specs = specs.Values.ToArray(), SnapshotCoverage = coverage, TypeDefinitions = IsDurableCapture ? sourceTypeDefinitions.Values.ToArray() : null });
            selectedRoots = roots.OrderBy(id => id).ToArray(); Record("inventory_complete", new { Milliseconds = watch.Elapsed.TotalMilliseconds, Catalog = catalog.Count, PhysicalSpecs = specs.Count, Eligible = eligible.Count, RootCount = roots.Count, SnapshotMissing = coverage.Count(c => !c.SnapshotPresent), PreparedTimedOut = preparedTimedOut == 1 });
        }
        long[] selectedRoots = Array.Empty<long>();
        void Delete(Document document)
        {
            if (deleted) throw new InvalidOperationException("The one mass deletion already ran.");
            ValidateDelayedDelete(); ValidateBusinessDelete(); ValidateRapidDelete();
            if (IsDurableCapture)
            {
                var archive = OpeningState(document);
                if (archive == null || !string.IsNullOrWhiteSpace(Read(archive, "Failure")?.ToString())
                    || (bool?)Read(archive, "SourceInvalidated") == true)
                {
                    var failure = new { AtUtc = DateTime.UtcNow, DeletionAttempted = false, Reason = "Opening checkpoint unavailable or invalid before early deletion.",
                        State = State(document), OpeningFailure = Read(archive, "Failure"), SourceInvalidated = Read(archive, "SourceInvalidated"),
                        Note = "Test aborted without Delete. The observer does not call Schedule, prime an element or wait for a completed scan. A valid pending byte copy is allowed; Ready is not required." };
                    Write("deletion-not-attempted.json", failure); Record("mass_delete_precondition_failed", failure);
                    phase = "delete-not-attempted";
                    throw new InvalidOperationException("No usable opening checkpoint state; the mass deletion was not attempted.");
                }
            }
            if (!config.RapidModifiedDelete) Inventory(document);
            phase = "delete"; var before = State(document); var watch = Stopwatch.StartNew(); var failures = new DeleteFailures();
            using var tx = new Transaction(document, transactionName); tx.Start(); tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            try
            {
                returnedIds = document.Delete(selectedRoots.Select(id => new ElementId(id)).ToList()).Select(id => id.Value).OrderBy(id => id).ToArray();
                var status = tx.Commit();
                if (status != TransactionStatus.Committed) throw new InvalidOperationException("Mass deletion transaction returned " + status);
                deleted = true; committedUtc = DateTime.UtcNow; lastJournalChangeUtc = committedUtc;
            }
            catch (Exception ex)
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                Write("deletion-failed.json", new { Error = ex.ToString(), Milliseconds = watch.Elapsed.TotalMilliseconds, Failures = failures.Messages, Transaction = transactionName, SelectedRootIds = selectedRoots, SurvivingSelectedIds = selectedPhysicalIds.Where(id => document.GetElement(new ElementId(id)) != null).ToArray(), State = State(document), Note = "No per-element deletion fallback was attempted. All original physical targets remain eligible for a separately reviewed alternative root collection." });
                phase = "delete-failed"; throw;
            }
            changedIds = changedDeleted.OrderBy(id => id).ToArray();
            deletedPhysicalIds = physicalCandidateIds.Where(id => document.GetElement(new ElementId(id)) == null).ToArray();
            var result = new { Transaction = transactionName, Milliseconds = watch.Elapsed.TotalMilliseconds, Status = "Committed", ReturnedIds = returnedIds, DocumentChangedDeletedIds = changedIds, DocumentChangedModifiedIds = changedModified.OrderBy(id => id).ToArray(), DocumentChangedAddedIds = changedAdded.OrderBy(id => id).ToArray(), ModifiedCatalogRecords = changedModified.Where(catalog.ContainsKey).Select(id => new { Source = catalog[id], Survives = document.GetElement(new ElementId(id)) != null }).ToArray(), SelectedPhysicalIds = selectedPhysicalIds, DeletedPhysicalIds = deletedPhysicalIds, UnselectedPhysicalCascadeIds = deletedPhysicalIds.Except(selectedPhysicalIds).ToArray(), ExplicitlyExcludedPhysicalDeletedIds = explicitlyExcludedPhysicalIds.Intersect(deletedPhysicalIds).ToArray(), SurvivingSelectedPhysicalIds = selectedPhysicalIds.Except(deletedPhysicalIds).ToArray(), RequestedRootIds = selectedRoots, DependentIds = changedIds.Except(selectedPhysicalIds).ToArray(), NonTargetedDeletedModelCatalog = specs.Values.Where(s => !s.Selected && !deletedPhysicalIds.Contains(s.Id) && changedIds.Contains(s.Id)).ToArray(), Failures = failures.Messages, StateBefore = before, StateAfter = State(document), PreparedTimedOut = preparedTimedOut == 1, ActualAfterOpenMilliseconds = (committedUtc - openedUtc).TotalMilliseconds };
            Write("deletion.json", result); Record("mass_delete_returned", new { Milliseconds = watch.Elapsed.TotalMilliseconds, Roots = selectedRoots.Length, SelectedPhysical = selectedPhysicalIds.Length, DeletedPhysical = deletedPhysicalIds.Length, Returned = returnedIds.Length, ChangedDeleted = changedIds.Length }); phase = "journal-wait";
            CompleteRapidDelete(watch.Elapsed.TotalMilliseconds);
        }
        void Restore(Document document, bool repeat)
        {
            if (!deleted || !journalReady) throw new InvalidOperationException("Await journal-ready.json before restoration.");
            phase = repeat ? "restore-again" : "restore";
            var range = ((IEnumerable)Call(tracker, "LoadModelHistoryForLocalRange", new[] { docKey }, openedUtc.ToLocalTime().Date.AddDays(-1), DateTime.Today.AddDays(1))).Cast<object>().ToList();
            var events = range.Where(ev => (string)Read(ev, "Action") == "delete" && (string)Read(ev, "Tx") == transactionName).ToList();
            var recent = ((IEnumerable)Call(tracker, "LoadRecentDeletedHistory", document)).Cast<object>().ToList();
            var requestType = product.GetType("Analyse.HistoryRestoreRequest", true); var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(requestType));
            var convert = product.GetType("Analyse.ElementHistoryWindow", true).GetMethod("ToRestoreRequest", Flags);
            events = FilterPartialEvents(events);
            foreach (var ev in events)
            {
                var request = convert.Invoke(null, new[] { ev });
                if (config.AllowPartialDurableRestore)
                {
                    var durable = ResolveConverted(ev);
                    if (durable == null || (string)Read(durable, "SourceUniqueId") != (string)Read(request, "SourceUniqueId")
                        || (string)Read(Read(durable, "Recipe"), "Kind") != (string)Read(Read(request, "Recipe"), "Kind")
                        || !ArtifactsOf(Read(durable, "Recipe")).Select(artifact => (string)Read(artifact, "PayloadId")).OrderBy(value => value)
                            .SequenceEqual(ArtifactsOf(Read(request, "Recipe")).Select(artifact => (string)Read(artifact, "PayloadId")).OrderBy(value => value)))
                        throw new InvalidOperationException("UI conversion differs from the validated subset sidecar; no raw fallback is permitted.");
                }
                list.Add(request);
            }
            ValidateDurableRestoreRequests(list, events);
            int fullRequestsCount = list.Count; list = SelectRapidRestorationRequests(list, events);
            Write(repeat ? "load-again.json" : "load.json", new { RangeAllCount = range.Count, RangeDeletionCount = events.Count, DisplayableDeletionCount = events.Count(ev => (bool)Call(tracker, "IsDisplayableHistoryEvent", ev)), RecentDeletionCount = recent.Count, RequestsCount = fullRequestsCount, FullRequestsCount = fullRequestsCount, RestorationRequestsCount = list.Count, FullEventIds = events.Select(ev => Read(ev, "ElementId")).ToArray(), RequestEventIds = events.Select(ev => Read(ev, "ElementId")).ToArray(), DefaultEventIds = recent.Select(ev => Read(ev, "ElementId")).ToArray(), FullDeletionBatchIds = events.Select(ev => Read(Read(ev, "DeletionAudit"), "BatchId")).Distinct().ToArray(), Route = "Actual range loader and actual UI conversion preserve one request per event. Rapid restoration selects all and only independently admissible deleted physical objects after proving the excluded nonphysical records." });
            ProbeDiagnosticRequests(list); ProbeDiagnosticDestination(document, "before-restore");
            var watch = Stopwatch.StartNew(); restoreChangedAdded = 0; refreshCount = 0; Volatile.Write(ref restoring, 1);
            Record("restore_begin", new { Repeat = repeat, Requests = list.Count });
            try
            {
                Action refresh = () => { var timer = Stopwatch.StartNew(); ui.ActiveUIDocument.RefreshActiveView(); Interlocked.Increment(ref refreshCount); Record("restore_refresh", new { Number = refreshCount, Milliseconds = timer.Elapsed.TotalMilliseconds, RestoreElapsedMilliseconds = watch.Elapsed.TotalMilliseconds, ChangedAddedIds = Volatile.Read(ref restoreChangedAdded) }); };
                lastResult = Call(restoration, "Restore", document, list, refresh);
                Write(repeat ? "restore-again-result.json" : "restore-result.json", lastResult);
                Record("restore_returned", new { Repeat = repeat, Milliseconds = watch.Elapsed.TotalMilliseconds, Created = Read(lastResult, "Created"), Existing = Read(lastResult, "Existing"), Failed = Read(lastResult, "Failed"), RefreshCount = refreshCount });
            }
            finally { Volatile.Write(ref restoring, 0); }
            phase = "verification"; var verified = Verify(document); Write(repeat ? "verification-again.json" : "verification.json", verified); phase = "restored";
            RapidAfterRestore(verified, repeat);
        }
        List<string> Origins(Element element) => ((IEnumerable)Call(restoration, "GetOrigins", element))?.Cast<string>().Where(uid => uid != null).ToList() ?? new List<string>();
        object Verify(Document document)
        {
            currentTypeDefinitions.Clear();
            var watch = Stopwatch.StartNew(); var live = new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements().ToList();
            RefreshExcavations(document);
            var origins = new Dictionary<string, List<Element>>(StringComparer.Ordinal); var actualOrigins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var element in live)
            {
                var ids = Origins(element); actualOrigins[element.UniqueId] = ids;
                foreach (var uid in ids.Append(element.UniqueId).Distinct()) { if (!origins.TryGetValue(uid, out var matches)) origins[uid] = matches = new List<Element>(); matches.Add(element); }
            }
            var sourceUids = new HashSet<string>(catalog.Values.Select(c => c.Uid));
            var originPairs = new List<(string SourceUid, string ActualUid)>();
            foreach (var id in deletedPhysicalIds)
                if (origins.TryGetValue(specs[id].Uid, out var mapped))
                    foreach (var element in mapped) originPairs.Add((specs[id].Uid, element.UniqueId));
            var reverseConflicts = originPairs.GroupBy(pair => pair.ActualUid).Where(g => g.Select(pair => pair.SourceUid).Distinct().Count() > 1)
                .Select(g => new { ActualUid = g.Key, SourceUids = g.Select(pair => pair.SourceUid).Distinct().ToArray() }).ToArray();
            var reverseConflictUids = new HashSet<string>(reverseConflicts.Select(c => c.ActualUid));
            string Resolve(string actualUid) => sourceUids.Contains(actualUid ?? "") ? actualUid : actualUid != null && actualOrigins.TryGetValue(actualUid, out var saved) ? saved.FirstOrDefault(sourceUids.Contains) ?? actualUid : actualUid;
            bool Identity(string expected, string actual) => string.Equals(expected, Resolve(actual), StringComparison.Ordinal);
            var records = new List<object>(); var missing = new List<object>(); var duplicates = new List<object>(); var failed = new List<object>();
            foreach (var id in deletedPhysicalIds)
            {
                var expected = specs[id]; var found = origins.TryGetValue(expected.Uid, out var candidates) ? candidates.DistinctBy(e => e.UniqueId).ToList() : new List<Element>();
                if (found.Count == 0) { var entry = new { SourceId = id, SourceUid = expected.Uid, expected.Category, expected.Class, expected.Name, Reason = "missing", ActualUids = Array.Empty<string>(), Passed = false }; missing.Add(entry); records.Add(entry); continue; }
                if (found.Count != 1) { var entry = new { SourceId = id, SourceUid = expected.Uid, expected.Category, expected.Class, expected.Name, Reason = "duplicate-origins", ActualUids = found.Select(e => e.UniqueId).ToArray(), Passed = false }; duplicates.Add(entry); records.Add(entry); continue; }
                var element = found[0]; var actual = Capture(element);
                bool injective = !reverseConflictUids.Contains(element.UniqueId);
                bool type = expected.TypeUid == actual.TypeUid; bool allowedTypeReplacement = false;
                if (!type && expected.TypeInPlace && !string.IsNullOrWhiteSpace(expected.TypeUid) && document.GetElement(expected.TypeUid) == null)
                    type = allowedTypeReplacement = actual.TypeInPlace && expected.TypeName == actual.TypeName && expected.FamilyName == actual.FamilyName;
                object typeDefinitionEvidence = null;
                if (IsDurableRestore)
                {
                    typeDefinitionEvidence = CompareTypeDefinition(document, expected, actual);
                    type = (bool)Read(typeDefinitionEvidence, "Passed"); allowedTypeReplacement = false;
                }
                bool geometry = Same(expected.Point, actual.Point) && (expected.Curve == null && actual.Curve == null || expected.Curve != null && actual.Curve != null && Same(expected.Curve[2], actual.Curve[2]) && (Same(expected.Curve[0], actual.Curve[0]) && Same(expected.Curve[1], actual.Curve[1]) || Same(expected.Curve[0], actual.Curve[1]) && Same(expected.Curve[1], actual.Curve[0]))) && SamePoints(expected.Transform, actual.Transform);
                bool bbox = SamePoints(expected.BBox, actual.BBox);
                bool dimensions = expected.Dimensions.Count == actual.Dimensions.Count && expected.Dimensions.All(p => actual.Dimensions.TryGetValue(p.Key, out var value) && Math.Abs(p.Value - value) <= Tolerance);
                bool host = Identity(expected.HostUid, actual.HostUid) && Identity(expected.SuperUid, actual.SuperUid) && Identity(expected.LevelUid, actual.LevelUid);
                bool group = Identity(expected.GroupUid, actual.GroupUid);
                bool joins = expected.JoinQuerySucceeded && actual.JoinQuerySucceeded && expected.JoinedUids.OrderBy(v => v).SequenceEqual(actual.JoinedUids.Select(Resolve).OrderBy(v => v));
                bool wallJoins = expected.WallJoins.Count == actual.WallJoins.Count
                    && expected.WallJoins.All(end => actual.WallJoins.Count(current => end.End == current.End && end.JoinType == current.JoinType
                        && end.JoinAllowed == current.JoinAllowed && end.Participants.SequenceEqual(current.Participants.Select(Resolve))) == 1);
                bool connections = expected.Ports.Count == actual.Ports.Count;
                bool portSystemTypesMatch = true; var portSystemTypeEvidence = new List<object>();
                foreach (var port in expected.Ports)
                {
                    var matched = actual.Ports.Where(p => p.Domain == port.Domain && p.Type == port.Type && p.Shape == port.Shape && Same(p.Point, port.Point)).ToList();
                    if (matched.Count != 1 || matched[0].Classification != port.Classification) { connections = false; continue; }
                    if (IsDurableRestore)
                    {
                        var systemEvidence = CompareHistoricalSystem(document, expected, port.SystemTypeUid, matched[0].SystemTypeUid);
                        portSystemTypesMatch &= (bool)Read(systemEvidence, "Passed");
                        portSystemTypeEvidence.Add(new { PortId = port.Id, PeerSourceUid = (string)null, Evidence = systemEvidence });
                    }
                    if (port.Peers.Count != matched[0].Peers.Count) connections = false;
                    foreach (var peer in port.Peers)
                    {
                        var peers = matched[0].Peers.Where(current => Identity(peer.Uid, current.Uid) && Same(peer.Point, current.Point)
                            && peer.Domain == current.Domain && peer.Type == current.Type && peer.Shape == current.Shape && peer.Classification == current.Classification).ToArray();
                        if (peers.Length != 1) { connections = false; continue; }
                        if (IsDurableRestore)
                        {
                            var peerEvidence = CompareHistoricalSystem(document, expected, peer.SystemTypeUid, peers[0].SystemTypeUid);
                            portSystemTypesMatch &= (bool)Read(peerEvidence, "Passed");
                            portSystemTypeEvidence.Add(new { PortId = port.Id, PeerSourceUid = peer.Uid, Evidence = peerEvidence });
                        }
                    }
                }
                connections &= portSystemTypesMatch;
                var items = (Read(lastResult, "Items") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();
                var item = items.FirstOrDefault(i => (string)Read(i, "SourceUniqueId") == expected.Uid); var adjustment = Read(item, "SystemTypeAdjustment");
                bool normalized = expected.SystemTypeUid == null && actual.SystemTypeUid != null && (bool?)Read(adjustment, "SourceDefined") == false && (bool?)Read(adjustment, "ActualDefined") == true
                    && (bool?)Read(adjustment, "RevitAssignedPreviouslyUndefinedType") == true && (bool?)Read(adjustment, "MatchesUniqueConnectedPeerType") == true
                    && (string)Read(adjustment, "ActualTypeUniqueId") == actual.SystemTypeUid && actual.ConnectedPeerSystemTypeUids.Count == 1 && actual.ConnectedPeerSystemTypeUids[0] == actual.SystemTypeUid
                    && (expected.ConnectedPeerSystemTypeUids.Count == 0 || expected.ConnectedPeerSystemTypeUids.Count == 1 && expected.ConnectedPeerSystemTypeUids[0] == actual.SystemTypeUid)
                    && !string.IsNullOrWhiteSpace((string)Read(adjustment, "Note"));
                // An originally undefined type is also historical data. After the
                // undefined-type fix, NULL must remain NULL; normalization is diagnostic only.
                bool system = expected.SystemTypeUid == actual.SystemTypeUid;
                object systemTypeEvidence = null;
                if (IsDurableRestore) { systemTypeEvidence = CompareHistoricalSystem(document, expected, expected.SystemTypeUid, actual.SystemTypeUid); system = (bool)Read(systemTypeEvidence, "Passed"); }
                bool excavations = expected.ExcavationQuerySucceeded && actual.ExcavationQuerySucceeded
                    && expected.Excavations.Count == actual.Excavations.Count
                    && expected.Excavations.All(pair => actual.Excavations.Count(current => pair.Kind == current.Kind
                        && Identity(pair.IntersectedUid, current.IntersectedUid) && Identity(pair.IntersectingUid, current.IntersectingUid)
                        && Math.Abs(pair.VolumeCubicFeet - current.VolumeCubicFeet) <= Math.Max(1e-5, Math.Abs(pair.VolumeCubicFeet) * 1e-8)) == 1);
                bool geometryChecked = expected.Point != null || expected.Curve != null || expected.BBox != null || expected.Transform != null;
                bool compareInstance = config.BusinessWorkflow || config.RapidModifiedDelete && selectedPhysicalIds.Contains(id);
                var instanceEvidence = compareInstance ? CompareBusinessParameters(document, expected, actual) : null; bool instanceParameters = !compareInstance || (bool?)Read(instanceEvidence, "Passed") == true; bool passed = instanceParameters && injective && expected.Class == actual.Class && type && geometry && bbox && dimensions && host && group && joins && wallJoins && connections && system && excavations && geometryChecked && expected.ProbeErrors.Count == 0 && actual.ProbeErrors.Count == 0;
                var record = new { SourceId = id, SourceUid = expected.Uid, expected.Category, expected.Class, expected.Name, ActualUids = found.Select(e => e.UniqueId).ToArray(), MatchesIdentity = true, IdentityInjective = injective, ClassMatches = expected.Class == actual.Class, TypeMatches = type, TypeUidMatchesSource = expected.TypeUid == actual.TypeUid, FamilyUidMatchesSource = (bool?)Read(typeDefinitionEvidence, "FamilyUidMatchesSource"), UsesExistingModelDefinitions = config.RapidModifiedDelete && rapidRestoreRequests.TryGetValue(expected.Uid, out var actualRequest) && (bool?)Read(Read(actualRequest, "Recipe"), "UsesExistingModelDefinitions") == true, ExistingModelDefinitionsUsed = (bool?)Read(typeDefinitionEvidence, "ExistingModelDefinitionsUsed"), HistoricalTypeProvenance = (bool?)Read(typeDefinitionEvidence, "HistoricalTypeProvenance"), TypeDefinitionParametersMatch = (bool?)Read(typeDefinitionEvidence, "TypeDefinitionParametersMatch"), TypeDefinitionEvidence = typeDefinitionEvidence, AllowedInPlaceTypeReplacement = allowedTypeReplacement, GeometryMatches = geometry, GeometryChecked = geometryChecked, BBoxMatches = bbox, DimensionsMatch = dimensions, HostMatches = host, GroupMatches = group, JoinsMatch = joins, WallJoinsMatch = wallJoins, ConnectionsMatch = connections, SystemTypeMatches = system, SystemTypeUidMatchesSource = expected.SystemTypeUid == actual.SystemTypeUid, SystemTypeEvidence = systemTypeEvidence, PortSystemTypesMatch = IsDurableRestore ? (bool?)portSystemTypesMatch : null, PortSystemTypeEvidence = IsDurableRestore ? portSystemTypeEvidence : null, UndefinedSystemNormalized = normalized, SystemTypeAdjustment = adjustment, ExcavationsMatch = excavations, ExpectedExcavations = expected.Excavations, ActualExcavations = actual.Excavations, InstanceParametersMatch = compareInstance ? (bool?)instanceParameters : null, InstanceParameterEvidence = instanceEvidence, Passed = passed, Expected = expected, Actual = actual, ExpectedProbeErrors = expected.ProbeErrors, ActualProbeErrors = actual.ProbeErrors, ActualWhenFailed = passed ? null : actual };
                records.Add(record); if (!passed) failed.Add(record);
            }
            var selectedUids = new HashSet<string>(deletedPhysicalIds.Select(id => specs[id].Uid));
            var newModelRecords = live.Where(e => !sourceUids.Contains(e.UniqueId) && Safe(() => e.Category?.CategoryType == CategoryType.Model && !e.ViewSpecific) && !Origins(e).Any(selectedUids.Contains)).ToArray();
            var generatedCandidates = newModelRecords.Where(IsRapidGeneratedRecord).ToArray();
            var generated = generatedCandidates.Where(element => generatedCandidates.Count(candidate => candidate.Category.Id.Value == element.Category.Id.Value)
                <= catalog.Values.Count(baseline => changedIds.Contains(baseline.Id) && baseline.Class == "Element" && baseline.CategoryId == element.Category.Id.Value && baseline.ProtectionReason == "generated-model-category")).ToArray();
            var generatedUids = new HashSet<string>(generated.Select(element => element.UniqueId), StringComparer.Ordinal);
            var generatedRecords = generated.Select(element => new { Id = element.Id.Value, Uid = element.UniqueId, Class = element.GetType().FullName,
                CategoryId = element.Category.Id.Value, ExactNativeElementClass = element.GetType() == typeof(Element), NativeCategoryWhitelisted = ProtectedGeneratedCategories.Contains(element.Category.Id.Value),
                BaselineDeletedRecords = catalog.Values.Where(baseline => changedIds.Contains(baseline.Id) && baseline.Class == "Element" && baseline.CategoryId == element.Category.Id.Value && baseline.ProtectionReason == "generated-model-category").ToArray() }).ToArray();
            var unexpected = newModelRecords.Where(element => !generatedUids.Contains(element.UniqueId))
                .Select(e => new { Id = e.Id.Value, Uid = e.UniqueId, Class = e.GetType().Name, Category = e.Category?.Name, Name = Safe(() => e.Name), Origins = Origins(e), Spec = Capture(e), OwnerViewId = e.OwnerViewId.Value, IsTransient = Safe(() => e.IsTransient) }).ToArray();
            var dependentModel = specs.Values.Where(s => !s.Selected && !deletedPhysicalIds.Contains(s.Id) && changedIds.Contains(s.Id)).Select(s => new { SourceId = s.Id, SourceUid = s.Uid, s.Class, s.Category, s.ProtectionReason, ActualUids = origins.TryGetValue(s.Uid, out var matches) ? matches.Select(e => e.UniqueId).Distinct().ToArray() : Array.Empty<string>(), GeometryVerified = false }).ToArray();
            var result = new { CapturedUtc = DateTime.UtcNow, Milliseconds = watch.Elapsed.TotalMilliseconds, DeletedPhysicalCount = deletedPhysicalIds.Length, RestoredUniqueCount = records.Count - missing.Count - duplicates.Count, Missing = missing, Duplicates = duplicates, ReverseOriginConflicts = reverseConflicts, Failed = failed, Records = records, NonTargetedDeletedModelAfter = dependentModel, GeneratedModelRecords = generatedRecords, UnexpectedModelObjects = unexpected, CategoryCounts = records.GroupBy(r => (string)Read(r, "Category") ?? "(none)").ToDictionary(g => g.Key, g => new { Expected = g.Count(), Passed = g.Count(r => (bool?)Read(r, "Passed") == true) }), RelationCoverage = "Host, supercomponent, level, group and geometric joins; not all constraints/cuts/sketch semantics. Non-targeted dependent models are catalogued but not geometry-verified.", GeometryCoverage = "Class/type, point or curve endpoints/midpoint, family transform, world bbox and standard MEP section dimensions. No exhaustive solid mesh comparison.", AllDeletedPhysicalVerified = !config.AllowPartialDurableRestore && missing.Count == 0 && duplicates.Count == 0 && failed.Count == 0 && reverseConflicts.Length == 0, LaneOnly = DiagnosticLane, OriginalConvertedSubsetPhysicalIds = diagnosticOriginalSubsetIds, DiagnosticLanePhysicalIds = config.DiagnosticPhysicalIds, LaneSubsetVerified = DiagnosticLane && missing.Count == 0 && duplicates.Count == 0 && failed.Count == 0 && reverseConflicts.Length == 0, SelectedSubsetVerified = config.AllowPartialDurableRestore && !DiagnosticLane && missing.Count == 0 && duplicates.Count == 0 && failed.Count == 0 && reverseConflicts.Length == 0, FullDeletedPhysicalCount = config.AllowPartialDurableRestore ? fullDeletedPhysicalIds.Length : deletedPhysicalIds.Length, VerificationScope = DiagnosticLane ? "isolated diagnostic lane; not the full converted subset or original model" : config.AllowPartialDurableRestore ? "validated converted physical subset; all other failures remain in capture evidence" : "all deleted admissible physical objects" };
            if (IsDurableRestore) Write("type-definitions-" + phase + ".json", new { AtUtc = DateTime.UtcNow, Definitions = currentTypeDefinitions.Values.ToArray(), SourceInventory = Path.Combine(evidenceDirectory, "inventory.json"), Note = "Independent live type parameter reads for all present verified targets; source values remain in the capture inventory." });
            Record("verification_complete", new { result.Milliseconds, Expected = deletedPhysicalIds.Length, Missing = missing.Count, Duplicates = duplicates.Count, Failed = failed.Count, Unexpected = unexpected.Length, Passed = result.AllDeletedPhysicalVerified });
            ProbeDiagnosticDestination(document, phase); return result;
        }
        void ActivateBlank(UIApplication application)
        {
            var path = Path.Combine(folder, "empty-finish.rvt");
            if (!File.Exists(path)) { var blank = application.Application.NewProjectDocument(UnitSystem.Metric); blank.SaveAs(path); if (!blank.Close(false)) throw new InvalidOperationException("Temporary blank document did not close."); }
            application.OpenAndActivateDocument(path);
        }
        void Persist(UIApplication application)
        {
            if (lastResult == null) throw new InvalidOperationException("Restore first.");
            phase = "save-restored-copy"; var watch = Stopwatch.StartNew();
            doc.SaveAs(restoredPath, new SaveAsOptions { OverwriteExistingFile = false }); Record("restored_copy_saved", new { Path = restoredPath, Milliseconds = watch.Elapsed.TotalMilliseconds });
            var saved = doc; int savedRuntime = saved.GetHashCode(); ActivateBlank(application);
            bool closed = saved.Close(false); bool invalid = !saved.IsValidObject;
            bool absent = !application.Application.Documents.Cast<Document>().Any(d => d.GetHashCode() == savedRuntime);
            Record("saved_document_close_result", new { Closed = closed, OldDocumentInvalid = invalid, AbsentFromApplicationDocuments = absent, SavedRuntimeDocumentId = savedRuntime });
            if (!closed || !invalid || !absent) throw new InvalidOperationException("Saved restored copy did not actually close; reopen verification would be invalid.");
            application.OpenAndActivateDocument(restoredPath); doc = application.ActiveUIDocument.Document;
            bool distinct = !ReferenceEquals(saved, doc) && !Safe(() => doc.Equals(saved));
            var persistence = new { Path = restoredPath, SavedRuntimeDocumentId = savedRuntime, ReopenedRuntimeDocumentId = doc.GetHashCode(), Closed = closed, OldDocumentInvalid = invalid, AbsentFromApplicationDocuments = absent, ReopenedDistinctDocument = distinct, FileExists = File.Exists(restoredPath), FileLength = new FileInfo(restoredPath).Length };
            Write("persistence.json", persistence);
            if (!distinct || !doc.IsValidObject || !string.Equals(doc.PathName, restoredPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Reopening did not return a distinct valid restored-copy document.");
            phase = "reopened-verification"; var verified = Verify(doc); Write("reopened-verification.json", verified); phase = "reopened-verified";
            RapidAfterReopen(verified, persistence);
        }
        void Finish(UIApplication application)
        {
            ReleaseRapidSources();
            phase = "closing"; tracker.GetField("_captureDetailedDeletedMesh", Flags).SetValue(null, originalDetailed);
            var closing = doc; int closingRuntime = closing.GetHashCode(); ActivateBlank(application);
            bool closed = closing.Close(false); bool invalid = !closing.IsValidObject;
            bool absent = !application.Application.Documents.Cast<Document>().Any(d => d.GetHashCode() == closingRuntime);
            Record("final_document_close_result", new { Closed = closed, OldDocumentInvalid = invalid, AbsentFromApplicationDocuments = absent, RuntimeDocumentId = closingRuntime });
            if (!closed || !invalid || !absent) throw new InvalidOperationException("Stress copy close was cancelled or failed; no integrity completion is asserted.");
            var initial = File.ReadAllText(Path.Combine(evidenceDirectory, "source-sha256.txt")).Trim(); var copy = Sha(Path.Combine(evidenceDirectory, "Antibes-stress.rvt"));
            var source = Sha(File.ReadAllText(Path.Combine(evidenceDirectory, "source-path.txt")).Trim());
            var result = new { AtUtc = DateTime.UtcNow, SourceSha256Before = initial, SourceSha256After = source, SourceUnchanged = initial == source, OriginalTestCopySha256 = initial, FinalTestCopySha256 = copy, FinalSourceSha256 = source, CopyAndSourceUnchanged = initial == copy && initial == source, RestoredSavedPath = File.Exists(restoredPath) ? restoredPath : null, RestoredCopySha256 = File.Exists(restoredPath) ? Sha(restoredPath) : null, Closed = closed, OldDocumentInvalid = invalid, AbsentFromApplicationDocuments = absent, ClosedWithoutSaving = closed, DurablePhase = IsDurable ? config.DurablePhase : null, AllOpeningBridgeFilesStillAbsent = !IsDurableRestore || durableBridgePaths.All(path => !File.Exists(path)), DeletedProjectSha256AfterRestore = IsDurableRestore ? Sha(durableDeletedPath) : null };
            Write("integrity.json", result); Record("copy_closed_without_saving", result); finished = true; phase = "finished"; Write("finished.json", result);
        }
        public void Execute(UIApplication application)
        {
            if (!actions.TryDequeue(out var action) || finished) return; var diagnosticApiWatch = Stopwatch.StartNew();
            rapidCallbackSequence++;
            try
            {
                if (config.RapidModifiedDelete && IsDurableRestore && (action == "open" || action.StartsWith("restore", StringComparison.Ordinal))
                    && (Volatile.Read(ref rapidTimeSetup) != 2 || rapidFailed))
                    throw new InvalidOperationException("Rapid J+14 maintenance/source guards must succeed before opening or restoring the deleted-state checkpoint.");
                if (partialCapturePaused && action != "finalize-subset" && action != "durable-diagnostics") { Record("partial_request_ignored", new { Action = action, Reason = "Source readers and converter are frozen until controlled removal." }); return; }
                if (action == "finalize-subset") { FinalizeSubset(); return; }
                if (partialCapturePaused && action == "durable-diagnostics") { DurableDiagnostics(); return; }
                if (action == "open") { if (application.Application.Documents.Size != 0) throw new InvalidOperationException("Only empty isolated process can open stress copy."); phase = "opening"; Record("open_begin"); application.OpenAndActivateDocument(modelPath); Record("open_returned"); phase = IsDurableRestore ? "await-restore" : "await-delete"; StartDelayedCapture(); StartBusinessCapture(); return; }
                if (!IsCopy(application.ActiveUIDocument?.Document)) throw new InvalidOperationException("Active document is not the exact stress copy or its saved restored derivative.");
                doc = application.ActiveUIDocument.Document;
                if (action == "delete") Delete(doc);
                else if (action == "rapid-edit") EditRapidObjects(doc);
                else if (action == "rapid-purge-refusal") RapidPurgeRefusal(application);
                else if (action == "business-edit") EditBusinessObjects(doc); else if (action == "delay-edit-plant") EditDelayedPlant(doc);
                else if (action == "delay-edit-wall") EditDelayedWall(doc);
                else if (action == "diagnose") Diagnose(application);
                else if (action == "probe-unset") ProbeUnsetSystems(application);
                else if (action == "regressions") RunRegressions(application);
                else if (action == "convert") BeginDurableConversion();
                else if (action == "persist-deleted") PersistDeleted(application);
                else if (action == "persist-subset") PersistSubset(application);
                else if (action == "durable-diagnostics") DurableDiagnostics();
                else if (action == "restore" || action == "restore-again") Restore(doc, action == "restore-again");
                else if (action == "persist") Persist(application);
                else if (action == "finish") Finish(application);
            }
            catch (Exception ex) { if (action.StartsWith("delay-edit-", StringComparison.Ordinal)) delayedTestFailed = true; Record("command_error", new { Action = action, Phase = phase, Error = ex.ToString() }); Write("last-error.json", new { Action = action, Phase = phase, Error = ex.ToString() }); }
            finally { if (DiagnosticLane || config.RapidModifiedDelete) Record("diagnostic_external_event_span", new { CallbackSequence = rapidCallbackSequence, Action = action, Phase = phase, Milliseconds = diagnosticApiWatch.Elapsed.TotalMilliseconds }); }
        }
        void PollJournal()
        {
            if (!deleted || journalReady) return;
            foreach (var path in Directory.GetFiles(historyDirectory, "element-history-*.jsonl"))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is string line)
                {
                    JsonDocument parsed; try { parsed = JsonDocument.Parse(line); } catch { continue; }
                    using (parsed)
                    {
                        var root = parsed.RootElement;
                        if (!root.TryGetProperty("Action", out var action) || action.GetString() != "delete" || !root.TryGetProperty("Tx", out var tx) || tx.GetString() != transactionName) continue;
                        long id = root.GetProperty("ElementId").GetInt64();
                        if (persisted.ContainsKey(id)) continue; persisted[id] = root.Clone(); lastJournalChangeUtc = DateTime.UtcNow;
                        if (root.TryGetProperty("DeletionAudit", out var audit) && audit.ValueKind == JsonValueKind.Object && audit.TryGetProperty("BatchId", out var batch)) batchIds.Add(batch.GetString());
                    }
                }
            }
            int queue = Convert.ToInt32(Read(Read(tracker, "Queue"), "Count") ?? 0); int auditQueue = Convert.ToInt32(Read(Read(tracker, "AuditQueue"), "Count") ?? 0);
            bool timeout = (DateTime.UtcNow - committedUtc).TotalSeconds >= config.JournalTimeoutSeconds;
            if (timeout || queue == 0 && auditQueue == 0 && persisted.Count > 0 && (DateTime.UtcNow - lastJournalChangeUtc).TotalSeconds >= 2)
            {
                BuildBusinessReadinessEvents(); journalReady = true;
                var summary = new { DeletionBatchIds = batchIds.ToArray(), EventIds = persisted.Keys.OrderBy(id => id).ToArray(), ExpectedChangedIds = changedIds, MissingChangedIds = changedIds.Except(persisted.Keys).ToArray(), FullCount = persisted.Count, SnapshotMissingCount = persisted.Values.Count(ev => ev.TryGetProperty("Delta", out var delta) && delta.TryGetProperty("snapshotMissing", out var missing) && missing.ValueKind == JsonValueKind.True), RecipeKinds = persisted.Values.Select(ev => ev.TryGetProperty("Delta", out var delta) && delta.TryGetProperty("recipe", out var recipe) && recipe.ValueKind == JsonValueKind.Object && recipe.TryGetProperty("Kind", out var kind) ? kind.GetString() : "(none)").GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count()), PollMilliseconds = (DateTime.UtcNow - committedUtc).TotalMilliseconds, TimedOut = timeout, Queue = queue, AuditQueue = auditQueue, Note = "Natural worker persistence observed after queues drained and count stable for two seconds, or explicit timeout; missing IDs remain visible." };
                Write("journal-summary.json", summary); Write("journal-ready.json", new { AtUtc = DateTime.UtcNow, summary.FullCount, summary.TimedOut }); Record("journal_ready", summary); phase = "await-restore";
            }
        }
        async Task Worker(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    PrepareRapidTimeSimulation();
                    foreach (var name in new[] { "open", "restore", "restore-again", "persist", "finish", "diagnose", "regressions", "probe-unset", "convert", "persist-deleted", "durable-diagnostics", "persist-subset", "finalize-subset" })
                    {
                        var path = Path.Combine(folder, name + ".request"); if (!File.Exists(path)) continue;
                        long stamp = File.GetLastWriteTimeUtc(path).Ticks; if (requests.TryGetValue(name, out var previous) && stamp == previous) continue;
                        requests[name] = stamp;
                        if (partialCapturePaused && name != "finalize-subset" && name != "durable-diagnostics") { Record("partial_request_ignored", new { Request = name, Reason = "Source readers and converter are frozen." }); continue; }
                        Record("external_request", new { Request = name, FileUtc = File.GetLastWriteTimeUtc(path) }); actions.Enqueue(name);
                    }
                    if (!actions.IsEmpty && !finished) external.Raise();
                    PollJournal();
                    PrepareRapidReadiness();
                    var batch = new List<string>(); while (rows.TryDequeue(out var row)) batch.Add(JsonSerializer.Serialize(row));
                    if (batch.Count > 0) File.AppendAllLines(Path.Combine(folder, "observer.jsonl"), batch);
                    while (exports.TryDequeue(out var export)) File.WriteAllText(Path.Combine(folder, export.Name), JsonSerializer.Serialize(export.Value, new JsonSerializerOptions { WriteIndented = true }));
                    if ((DateTime.UtcNow - lastHeartbeat).TotalSeconds >= 1)
                    {
                        lastHeartbeat = DateTime.UtcNow; File.WriteAllText(Path.Combine(folder, "heartbeat.json"), JsonSerializer.Serialize(new { AtUtc = lastHeartbeat, Phase = phase, Restoring = Volatile.Read(ref restoring) == 1, RestoreChangedAddedIds = Volatile.Read(ref restoreChangedAdded), RefreshCount = Volatile.Read(ref refreshCount), PersistedDeletionCount = persisted.Count, Finished = finished }));
                    }
                }
                catch (Exception ex) { File.AppendAllText(Path.Combine(folder, "observer-errors.txt"), DateTime.UtcNow.ToString("O") + " " + ex + Environment.NewLine); }
                try { await Task.Delay(250, token); } catch (OperationCanceledException) { break; }
            }
        }
    }
}

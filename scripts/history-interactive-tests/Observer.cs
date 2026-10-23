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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BIMaestro.InteractiveTests
{
    public sealed class Observer : IExternalApplication, IExternalEventHandler
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        readonly ConcurrentQueue<object> rows = new ConcurrentQueue<object>();
        readonly ConcurrentQueue<string> actions = new ConcurrentQueue<string>();
        readonly Dictionary<string,long> requests = new Dictionary<string,long>();
        readonly HashSet<string> seenJournal = new HashSet<string>();
        readonly Dictionary<string, Spec> specs = new Dictionary<string, Spec>();
        readonly List<double> tickGaps = new List<double>();
        readonly string[] targets = { "2c5202a5-15c8-4179-86b7-68fe9802c14d-00073e5d", "ed00bb15-8d5c-445b-9e0c-fb4a015aaced-0007f1d3" };
        string folder, modelPath, stage = "waiting-open";
        UIControlledApplication app;
        UIApplication ui;
        Assembly product;
        Type tracker, opening;
        ExternalEvent external;
        DispatcherTimer dispatcher;
        CancellationTokenSource cancellation;
        Task writer;
        Document testDoc;
        DateTime openedUtc, firstActiveIdleUtc, firstViewUtc;
        long previousTick;
        uint previousInput;
        int openedFlag, queued1, queued3;
        bool initialized, finished;
        DateTime nextStateUtc;

        [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size; public uint Tick; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput input);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        sealed class Spec
        {
            public string Uid { get; set; }
            public string Class { get; set; }
            public string TypeUid { get; set; }
            public string Label { get; set; }
            public double[] Point { get; set; }
            public double[][] Curve { get; set; }
        }
        static object Read(object instance, string name)
        {
            if (instance == null) return null;
            var type = instance as Type ?? instance.GetType();
            return type.GetField(name, Flags)?.GetValue(instance is Type ? null : instance)
                ?? type.GetProperty(name, Flags)?.GetValue(instance is Type ? null : instance);
        }
        static object Call(Type type, string name, params object[] args) => type.GetMethod(name, Flags).Invoke(null, args);
        static double[] Point(XYZ value) => value == null ? null : new[] { value.X, value.Y, value.Z };
        static string Sha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        void Record(string kind, object data = null) => rows.Enqueue(new { Kind = kind, AtUtc = DateTime.UtcNow, Stage = stage, Data = data });
        bool IsTest(Document doc) => doc != null && string.Equals(doc.PathName, modelPath, StringComparison.OrdinalIgnoreCase);
        public Result OnStartup(UIControlledApplication application)
        {
            app = application; folder = Path.GetDirectoryName(GetType().Assembly.Location);
            modelPath = Path.Combine(folder, "Antibes-interactif.rvt");
            app.Idling += Idle; app.ControlledApplication.DocumentOpened += Opened;
            app.ControlledApplication.DocumentChanged += Changed; app.SelectionChanged += Selected; app.ViewActivated += View;
            external = ExternalEvent.Create(this);
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication application)
        {
            app.Idling -= Idle; app.ControlledApplication.DocumentOpened -= Opened;
            app.ControlledApplication.DocumentChanged -= Changed; app.SelectionChanged -= Selected; app.ViewActivated -= View;
            dispatcher?.Stop(); cancellation?.Cancel(); try { writer?.Wait(1500); } catch { }
            external?.Dispose(); return Result.Succeeded;
        }
        public string GetName() => "Antibes interactive observer: isolated copy only";
        void Idle(object sender, IdlingEventArgs args)
        {
            ui = sender as UIApplication;
            if (!initialized)
            {
                var pid = Path.Combine(folder, "observer-pid.txt");
                if (!File.Exists(pid)) return;
                if (File.ReadAllText(pid).Trim() != Environment.ProcessId.ToString()) { app.Idling -= Idle; return; }
                try
                {
                    if (ui.Application.Documents.Size != 0) throw new InvalidOperationException("Observer requires an empty isolated process before opening the copy.");
                    product = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("BIMaestroApp") != null);
                    tracker = product.GetType("Analyse.ElementHistoryTracker", true); opening = product.GetType("Analyse.ElementHistoryOpeningArchive", true);
                    var store = product.GetType("Analyse.CollaborativeModelTrackerStore", true);
                    Directory.CreateDirectory(Path.Combine(folder, "history"));
                    store.GetField("_activeDirectory", Flags).SetValue(null, Path.Combine(folder, "history"));
                    cancellation = new CancellationTokenSource(); writer = Task.Run(() => Writer(cancellation.Token));
                    dispatcher = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
                    dispatcher.Tick += Tick; dispatcher.Start(); initialized = true;
                    Record("observer_ready", new { Pid = Environment.ProcessId, Product = product.Location, Sha256 = Sha(product.Location), History = Read(store, "ActiveDirectory"), WorkerStatus = Read(tracker, "_worker") is Task task ? task.Status.ToString() : null });
                    File.WriteAllText(Path.Combine(folder, "ready.json"), JsonSerializer.Serialize(new { Pid = Environment.ProcessId, Product = product.Location, Sha256 = Sha(product.Location), Model = modelPath }));
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); app.Idling -= Idle; }
            }
            if (!initialized || finished || !IsTest(ui.ActiveUIDocument?.Document)) return;
            if (firstActiveIdleUtc == default) { firstActiveIdleUtc = DateTime.UtcNow; Record("first_active_document_idle", new { View = ui.ActiveUIDocument.ActiveView?.Name, Note = "API availability, not a painted-viewport guarantee" }); }
            if (DateTime.UtcNow < nextStateUtc) return;
            nextStateUtc = DateTime.UtcNow.AddMilliseconds(250);
            var watch = Stopwatch.StartNew();
            try { Record("production_state", State(testDoc)); } catch (Exception ex) { Record("state_error", ex.Message); }
            Record("observer_state_cost", new { Milliseconds = watch.Elapsed.TotalMilliseconds });
        }
        void Tick(object sender, EventArgs args)
        {
            var now = Stopwatch.GetTimestamp();
            var gap = previousTick == 0 ? 0 : (now - previousTick) * 1000.0 / Stopwatch.Frequency; previousTick = now;
            if (gap > 0) tickGaps.Add(gap);
            var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
            var got = GetLastInputInfo(ref input); var changed = got && input.Tick != previousInput; previousInput = input.Tick;
            Record("dispatcher_tick", new { GapMilliseconds = gap, ForegroundRevit = foreground == Environment.ProcessId, NewLatestInput = changed, LatestInputAgeMilliseconds = got ? (uint?)unchecked((uint)Environment.TickCount - input.Tick) : null });
        }
        void Opened(object sender, DocumentOpenedEventArgs args)
        {
            if (!initialized || !IsTest(args.Document)) return;
            testDoc = args.Document; openedUtc = DateTime.UtcNow; Volatile.Write(ref openedFlag, 1);
            Record("document_opened", new { args.Document.Title, args.Document.IsModified, State = State(args.Document) });
        }
        void View(object sender, ViewActivatedEventArgs args)
        {
            if (!initialized || !IsTest(args.CurrentActiveView?.Document)) return;
            if (firstViewUtc == default) firstViewUtc = DateTime.UtcNow;
            Record("view_activated", new { Name = args.CurrentActiveView.Name });
        }
        void Selected(object sender, SelectionChangedEventArgs args)
        {
            if (!initialized || !IsTest(args.GetDocument())) return;
            var watch = Stopwatch.StartNew();
            foreach (var id in args.GetSelectedElements().Take(12)) Capture(args.GetDocument().GetElement(id));
            Record("selection_changed", new { Ids = args.GetSelectedElements().Select(id => id.Value).ToArray(), ProbeMilliseconds = watch.Elapsed.TotalMilliseconds });
        }
        void Changed(object sender, DocumentChangedEventArgs args)
        {
            if (!initialized || !IsTest(args.GetDocument())) return;
            Record("document_changed", new { Transactions = args.GetTransactionNames(), Added = args.GetAddedElementIds().Select(id => id.Value).ToArray(), Modified = args.GetModifiedElementIds().Select(id => id.Value).ToArray(), Deleted = args.GetDeletedElementIds().Select(id => id.Value).ToArray() });
        }
        Spec Capture(Element element)
        {
            if (element == null) return null;
            try
            {
                var spec = new Spec { Uid = element.UniqueId, Class = element.GetType().Name, TypeUid = element.Document.GetElement(element.GetTypeId())?.UniqueId, Label = element.Name };
                if (element.Location is LocationPoint point) spec.Point = Point(point.Point);
                if (element.Location is LocationCurve curve) spec.Curve = new[] { Point(curve.Curve.GetEndPoint(0)), Point(curve.Curve.GetEndPoint(1)), Point(curve.Curve.Evaluate(.5, true)) };
                specs[spec.Uid] = spec; return spec;
            }
            catch (Exception ex) { Record("metadata_probe_error", ex.Message); return null; }
        }
        object State(Document doc)
        {
            var key = (string)Call(tracker, "GetDocumentKeyForHistory", doc);
            var audits = Read(tracker, "PreloadAuditByDocumentKey") as IDictionary;
            var audit = audits?[key];
            var states = Read(opening, "States") as IDictionary;
            var state = states != null && states.Contains(doc) ? states[doc] : null;
            return new { Key = key, Snapshots = Read(Read(tracker, "SnapshotByElementId"), "Count"), Queue = Read(Read(tracker, "Queue"), "Count"), Preload = audit == null ? null : new { Complete = Read(audit, "Complete"), Visited = Read(audit, "Visited"), Total = Read(audit, "TotalCandidates"), Captured = Read(audit, "Captured"), Failed = Read(audit, "Failed") }, Opening = state == null ? null : new { Ready = Read(state, "Ready"), Failure = Read(state, "Failure"), RequestedUtc = Read(state, "RequestedUtc"), ReadyUtc = Read(state, "ReadyUtc") } };
        }
        object TargetState(Document doc, Element element)
        {
            var key = (string)Call(tracker, "BuildElementSnapshotKey", doc, element.Id);
            var snapshots = (IDictionary)Read(tracker, "SnapshotByElementId"); var snapshot = snapshots[key];
            var recipe = Read(snapshot, "Recipe");
            return new { SnapshotPresent = snapshot != null, CaptureSource = Read(snapshot, "CaptureSource"), RecipeKind = Read(recipe, "Kind"), NativeWholeModel = Read(Read(recipe, "Native"), "WholeModel"), OpeningFallback = Read(recipe, "OpeningFallback") != null, State = State(doc) };
        }
        public void Execute(UIApplication application)
        {
            if (!actions.TryDequeue(out var action) || finished) return;
            try
            {
                if (action == "open")
                {
                    if (application.Application.Documents.Size != 0) throw new InvalidOperationException("Only empty isolated process can open test copy.");
                    Record("open_command_begin"); application.OpenAndActivateDocument(modelPath); Record("open_command_returned"); return;
                }
                if (!IsTest(application.ActiveUIDocument?.Document)) throw new InvalidOperationException("Active document is not the exact workspace test copy.");
                var doc = application.ActiveUIDocument.Document;
                if (action.StartsWith("select:"))
                {
                    var target = doc.GetElement(action.Substring(7));
                    if (target == null) throw new InvalidOperationException("Selection target missing from test copy.");
                    Capture(target); application.ActiveUIDocument.Selection.SetElementIds(new[] { target.Id });
                    Record("selection_command", new { target.UniqueId, Id = target.Id.Value });
                }
                else if (action.StartsWith("delete"))
                {
                    int index = action == "delete1" ? 0 : 1; var element = doc.GetElement(targets[index]);
                    if (element == null) { Record("automatic_delete_missing", action); return; }
                    var before = TargetState(doc, element); var watch = Stopwatch.StartNew(); var spec = Capture(element);
                    Record("automatic_delete_begin", new { Target = spec, Before = before, RequestedAfterSeconds = index == 0 ? 1 : 3, ActualAfterOpenMilliseconds = (DateTime.UtcNow - openedUtc).TotalMilliseconds, MetadataProbeMilliseconds = watch.Elapsed.TotalMilliseconds, FirstViewUtc = firstViewUtc, FirstActiveIdleUtc = firstActiveIdleUtc, ViewportReadyMarker = File.Exists(Path.Combine(folder, "viewport-ready.request")) });
                    watch.Restart(); using var tx = new Transaction(doc, "Interactive early deletion " + (index + 1)); tx.Start(); var deleted = doc.Delete(element.Id).Select(id => id.Value).ToArray(); var status = tx.Commit();
                    Record("automatic_delete_returned", new { Action = action, Status = status.ToString(), Deleted = deleted, Milliseconds = watch.Elapsed.TotalMilliseconds, State = State(doc) });
                }
                else if (action == "restore")
                {
                    var events = ((IEnumerable)Call(tracker, "LoadRecentDeletedHistory", doc)).Cast<object>().ToList();
                    var restore = product.GetType("Analyse.ElementHistoryRestoration", true);
                    var requestType = product.GetType("Analyse.HistoryRestoreRequest", true); var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(requestType));
                    var convert = product.GetType("Analyse.ElementHistoryWindow", true).GetMethod("ToRestoreRequest", Flags);
                    foreach (var ev in events) list.Add(convert.Invoke(null, new[] { ev }));
                    var watch = Stopwatch.StartNew(); var result = Call(restore, "Restore", doc, list, null);
                    Record("restore_result", new { Milliseconds = watch.Elapsed.TotalMilliseconds, Requests = list.Count, Result = result });
                    foreach (var item in ((IEnumerable)Read(result, "Items")).Cast<object>())
                    {
                        var sourceUid = Read(item, "SourceUniqueId") as string; var actualUid = Read(item, "UniqueId") as string;
                        var actual = string.IsNullOrWhiteSpace(actualUid) ? null : doc.GetElement(actualUid);
                        if (actual == null || !specs.TryGetValue(sourceUid ?? "", out var expected)) continue;
                        var current = Capture(actual); bool geometry = expected.Point == null || Distance(expected.Point, current.Point) < 1e-5;
                        if (expected.Curve != null) geometry = current.Curve != null && Distance(expected.Curve[2], current.Curve[2]) < 1e-5 && ((Distance(expected.Curve[0], current.Curve[0]) < 1e-5 && Distance(expected.Curve[1], current.Curve[1]) < 1e-5) || (Distance(expected.Curve[0], current.Curve[1]) < 1e-5 && Distance(expected.Curve[1], current.Curve[0]) < 1e-5));
                        Record("restore_verification", new { Source = sourceUid, Expected = expected, Actual = current, ClassMatches = expected.Class == current.Class, TypeMatches = expected.TypeUid == current.TypeUid, GeometryMatches = geometry });
                    }
                }
                else if (action == "finish")
                {
                    var gaps = tickGaps.OrderBy(value => value).ToList();
                    Record("finish_requested", new { State = State(doc), TickCount = gaps.Count, DispatcherP95Milliseconds = gaps.Count == 0 ? 0 : gaps[(int)Math.Ceiling(gaps.Count * .95) - 1], DispatcherMaximumMilliseconds = gaps.Count == 0 ? 0 : gaps.Last(), Note = "Dispatcher gaps include Revit commands/rendering/OS/other addins; they are not plugin-only or paint-frame times." });
                    var blank = application.Application.NewProjectDocument(UnitSystem.Metric); var path = Path.Combine(folder, "empty-finish.rvt"); blank.SaveAs(path); blank.Close(false); application.OpenAndActivateDocument(path); doc.Close(false);
                    Record("copy_closed_without_saving", new { ModelSha256 = Sha(modelPath) }); finished = true; dispatcher.Stop(); File.WriteAllText(Path.Combine(folder, "finished.txt"), DateTime.UtcNow.ToString("O"));
                }
            }
            catch (Exception ex) { Record("command_error", new { Action = action, Error = ex.ToString() }); }
        }
        static double Distance(double[] a, double[] b) => a == null || b == null ? double.PositiveInfinity : Math.Sqrt(a.Zip(b, (x,y) => (x-y)*(x-y)).Sum());
        async Task Writer(CancellationToken token)
        {
            var path = Path.Combine(folder, "observer.jsonl");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var stagePath = Path.Combine(folder, "stage.txt"); if (File.Exists(stagePath)) stage = File.ReadAllText(stagePath).Trim();
                    foreach (var name in new[] { "open", "restore", "finish", "viewport-ready", "select" })
                    {
                        var request = Path.Combine(folder, name + ".request");
                        if (!File.Exists(request)) continue;
                        var modified = File.GetLastWriteTimeUtc(request).Ticks;
                        if (requests.TryGetValue(name, out var seen) && modified == seen) continue;
                        requests[name] = modified;
                        Record("external_request", new { Request = name, FileUtc = File.GetLastWriteTimeUtc(request) });
                        if (name != "viewport-ready") actions.Enqueue(name == "select" ? "select:" + File.ReadAllText(request).Trim() : name);
                    }
                    if (Volatile.Read(ref openedFlag) == 1)
                    {
                        var elapsed = (DateTime.UtcNow - openedUtc).TotalSeconds;
                        if (elapsed >= 1 && Interlocked.Exchange(ref queued1, 1) == 0) actions.Enqueue("delete1");
                        if (elapsed >= 3 && Interlocked.Exchange(ref queued3, 1) == 0) actions.Enqueue("delete3");
                    }
                    if (!actions.IsEmpty && !finished) external.Raise();
                    var history = Path.Combine(folder, "history");
                    foreach (var journal in Directory.GetFiles(history, "element-history-*.jsonl"))
                    foreach (var line in File.ReadLines(journal))
                    {
                        if (!seenJournal.Add(line)) continue;
                        using var parsed = JsonDocument.Parse(line);
                        if (parsed.RootElement.TryGetProperty("Action", out var kind) && kind.GetString() == "delete")
                        {
                            var ts = parsed.RootElement.GetProperty("Ts").GetDateTime();
                            Record("journal_delete_first_seen", new { PollUpperBoundMilliseconds = (DateTime.UtcNow - ts).TotalMilliseconds, Event = parsed.RootElement.Clone(), Path = journal });
                        }
                    }
                    var batch = new List<string>(); while (rows.TryDequeue(out var row)) batch.Add(JsonSerializer.Serialize(row));
                    if (batch.Count > 0) File.AppendAllLines(path, batch);
                }
                catch (Exception ex) { File.AppendAllText(Path.Combine(folder, "observer-errors.txt"), ex + Environment.NewLine); }
                try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
            }
            var final = new List<string>(); while (rows.TryDequeue(out var row)) final.Add(JsonSerializer.Serialize(row)); if (final.Count > 0) File.AppendAllLines(path, final);
        }
    }
}

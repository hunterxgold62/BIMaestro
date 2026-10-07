using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grid = System.Windows.Controls.Grid;
using Color = Autodesk.Revit.DB.Color;
using Transform = Autodesk.Revit.DB.Transform;
using Point = System.Windows.Point;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Analyse.Tests
{
    public sealed partial class NativeValidation : IExternalApplication
    {
        private UIControlledApplication _app;
        private string _folder;
        private readonly List<object> _results = new List<object>();
        private int _failed;
        private ApiStep _next;
        private ExternalEvent _nextEvent;
        private Document _realDocument;
        private SmartScanSession _realScan;
        private bool _realEarlyResult;
        private DateTime _realDeadline;
        private sealed class ApiStep : IExternalEventHandler
        {
            public Action<UIApplication> Action;
            public void Execute(UIApplication app) { var action = Action; Action = null; action?.Invoke(app); }
            public string GetName() => "Clash validation · next API step";
        }
        public Result OnStartup(UIControlledApplication app)
        { _app = app; _folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += UiFailure;
            app.Idling += Run; return Result.Succeeded; }
        public Result OnShutdown(UIControlledApplication app)
        { app.Idling -= Run; System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException -= UiFailure;
            _nextEvent?.Dispose();
            File.WriteAllText(Path.Combine(_folder, "exited-cleanly.txt"), "OnShutdown"); return Result.Succeeded; }
        private void UiFailure(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // Test-only containment: every unexpected UI exception fails the run instead of crashing the test instance.
            _failed++; _results.Add(new { name = "unexpected dispatcher exception", passed = false, error = e.Exception.ToString() });
            WriteReport(); File.WriteAllText(Path.Combine(_folder, "ui-exception.txt"), e.Exception.ToString()); e.Handled = true;
        }
        private void Test(string name, Action action)
        {
            File.WriteAllText(Path.Combine(_folder, "progress.txt"), name);
            var timer = Stopwatch.StartNew();
            try { action(); _results.Add(new { name, passed = true, seconds = timer.Elapsed.TotalSeconds }); }
            catch (Exception ex) { _failed++; _results.Add(new { name, passed = false, error = ex.ToString() }); }
            WriteReport();
        }
        private void WriteReport() => File.WriteAllText(Path.Combine(_folder, "result.json"), JsonConvert.SerializeObject(new { failed = _failed, tests = _results }, Formatting.Indented));
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        private void FinishOrReadRealFixture(UIApplication ui)
        {
            var plan = Path.Combine(_folder, "real-fixture-path.txt");
            if (!File.Exists(plan)) { FinishRun(ui); return; }
            try
            {
                var path = Path.GetFullPath(File.ReadAllText(plan).Trim());
                var fixturesRoot = Path.GetFullPath(Path.GetDirectoryName(_folder)) + Path.DirectorySeparatorChar;
                Check(path.StartsWith(fixturesRoot, StringComparison.OrdinalIgnoreCase), "Only a workspace fixture copy can be opened.");
                _realDocument = ui.Application.OpenDocumentFile(path);
                var options = new SmartScanOptions { Equipment = true, GenericModels = true, Scope = SmartScanScope.Selection };
                var ids = new FilteredElementCollector(_realDocument).WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(options.SourceCategories())).ToElementIds().Take(563).ToList();
                options.LinkIds = new FilteredElementCollector(_realDocument).OfClass(typeof(RevitLinkInstance)).ToElementIds().ToList();
                _realScan = new SmartScanSession(new SmartScanSetup { Document = _realDocument, Selection = ids, DocumentKey = "real-fixture-copy" }, options);
                _realDeadline = DateTime.UtcNow.AddMinutes(10);
                ScheduleRealStep();
            }
            catch (Exception ex)
            {
                Test("real project copy opens and analyses selected sources", () => { throw new Exception("Opening the workspace copy failed.", ex); });
                _realDocument?.Close(false); FinishRun(ui);
            }
        }
        private void ScheduleRealStep()
        {
            _next.Action = AdvanceRealFixture;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => _nextEvent.Raise()), System.Windows.Threading.DispatcherPriority.Background);
        }
        private void AdvanceRealFixture(UIApplication ui)
        {
            if (DateTime.UtcNow > _realDeadline) _realScan.CancelRequested = true;
            _realScan.Advance();
            _realEarlyResult |= !_realScan.Complete && _realScan.Issues.Count > 0;
            File.WriteAllText(Path.Combine(_folder, "real-project-progress.json"), JsonConvert.SerializeObject(new
            { stage = _realScan.Stage, sources = _realScan.SourceCount, processed = _realScan.ProcessedSources,
                obstacles = _realScan.CandidateCount, issues = _realScan.Issues.Count, seconds = _realScan.Seconds, complete = _realScan.Complete }));
            if (!_realScan.Complete) { ScheduleRealStep(); return; }
            Test("real project copy opens and analyses selected sources", () =>
            {
                Check(_realScan.Error == null && !_realScan.Cancelled && _realScan.SourceCount > 0, _realScan.Error ?? "No eligible sources or timed-out analysis.");
                Check(_realScan.Issues.Count == 0 || _realEarlyResult, "Real project results were not available before completion.");
                File.WriteAllText(Path.Combine(_folder, "real-project-summary.json"), JsonConvert.SerializeObject(new
                { sources = _realScan.SourceCount, obstacles = _realScan.CandidateCount, pairs = _realScan.TestedPairs,
                    issues = _realScan.Issues.Count, seconds = _realScan.Seconds, earlyResults = _realEarlyResult,
                    diagnostics = _realScan.Diagnostics.Count }, Formatting.Indented));
            });
            _realScan.Dispose(); _realDocument.Close(false); FinishRun(ui);
        }
        private void FinishRun(UIApplication ui)
        {
            WriteReport(); File.WriteAllText(Path.Combine(_folder, "finished.txt"), "finished");
            ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }
        private void Run(object sender, IdlingEventArgs args)
        {
            _app.Idling -= Run; var ui = sender as UIApplication;
            File.WriteAllText(Path.Combine(_folder, "started.txt"), Process.GetCurrentProcess().Id.ToString());
            if (ui == null || ui.Application.Documents.Size != 0)
            { _failed++; _results.Add(new { error = "The harness requires an empty Revit instance. No user document was touched." }); WriteReport(); return; }
            Document host = null;
            try
            {
                _next = new ApiStep(); _nextEvent = ExternalEvent.Create(_next);
                Test("world box applies its own rotation and external translation once", () =>
                {
                    var b = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(2, 1, 3), Transform = Transform.CreateRotation(XYZ.BasisZ, Math.PI / 2) };
                    var actual = SmartGeometry.WorldBox(b, Transform.CreateTranslation(new XYZ(10, 20, 30)));
                    Check(actual.Min.DistanceTo(new XYZ(9, 20, 30)) < 1e-8 && actual.Max.DistanceTo(new XYZ(10, 22, 33)) < 1e-8, "Incorrect transformed AABB.");
                });
                Test("diagonal pipe envelope does not imply physical collision", () =>
                {
                    var rod = Cylinder(0.1, 10);
                    var rotate = Transform.CreateRotation(XYZ.BasisY, Math.PI / 2);
                    var diagonal = Transform.CreateRotation(XYZ.BasisZ, Math.PI / 4).Multiply(rotate);
                    rod = SolidUtils.CreateTransformed(rod, diagonal);
                    var obstacle = Box(new XYZ(0.2, 6, -0.05), new XYZ(0.4, 6.2, 0.05));
                    var a = SmartGeometry.WorldBox(rod.GetBoundingBox(), Transform.Identity); var b = SmartGeometry.WorldBox(obstacle.GetBoundingBox(), Transform.Identity);
                    Check(SmartGeometry.Overlap(a, b), "Fixture should have overlapping bounding boxes.");
                    var hit = SmartGeometry.Intersection(new[] { rod }, new[] { obstacle }, a, b, 0.0000001, false);
                    Check(!hit.Confirmed && hit.Box == null, "False clash on diagonal pipe.");
                });
                Test("solid intersection returns local world-space intersection", () =>
                {
                    var a = Box(XYZ.Zero, new XYZ(2, 2, 2)); var b = Box(new XYZ(1, 1, 1), new XYZ(3, 3, 3));
                    var hit = SmartGeometry.Intersection(new[] { a }, new[] { b }, BB(XYZ.Zero, new XYZ(2, 2, 2)), BB(new XYZ(1, 1, 1), new XYZ(3, 3, 3)), 0.001, false);
                    Check(hit.Confirmed && Math.Abs(hit.Volume - 1) < 1e-8 && hit.Box.Min.DistanceTo(new XYZ(1, 1, 1)) < 1e-8, "Incorrect volume or local box.");
                });
                Test("touching faces and small overlaps below threshold are excluded", () =>
                {
                    var a = Box(XYZ.Zero, new XYZ(1, 1, 1)); var b = Box(new XYZ(1, 0, 0), new XYZ(2, 1, 1));
                    Check(SmartGeometry.Intersection(new[] { a }, new[] { b }, BB(XYZ.Zero, new XYZ(1, 1, 1)), BB(new XYZ(1, 0, 0), new XYZ(2, 1, 1)), 0, false).Box == null, "Contact should not be a clash.");
                    b = Box(new XYZ(0.99, 0, 0), new XYZ(2, 1, 1));
                    Check(SmartGeometry.Intersection(new[] { a }, new[] { b }, BB(XYZ.Zero, new XYZ(1, 1, 1)), BB(new XYZ(.99, 0, 0), new XYZ(2, 1, 1)), .02, false).Box == null, "Threshold ignored.");
                });
                Test("mesh-only geometry is labelled as a suspicion", () =>
                {
                    var hit = SmartGeometry.Intersection(new Solid[0], new Solid[0], BB(XYZ.Zero, new XYZ(2, 2, 2)), BB(new XYZ(1, 1, 1), new XYZ(3, 3, 3)), 0, true);
                    Check(hit.Box != null && !hit.Confirmed, "Mesh treated as exact.");
                    var issue = new ModelIssue { IsApproximate = true, Kind = IssueKind.LinkPipeClash };
                    Check(issue.Severity == IssueSeverity.Check, "Approximation classified as critical.");
                });
                Test("multiple physical solids are all tested", () =>
                {
                    var a = Box(XYZ.Zero, new XYZ(1, 1, 1)); var far = Box(new XYZ(10, 10, 10), new XYZ(15, 15, 15));
                    var b = Box(new XYZ(.5, .5, .5), new XYZ(2, 2, 2));
                    Check(SmartGeometry.Intersection(new[] { far, a }, new[] { b }, BB(XYZ.Zero, new XYZ(15, 15, 15)), BB(new XYZ(.5, .5, .5), new XYZ(2, 2, 2)), 0, false).Confirmed, "Small solid missed.");
                });
                Test("autonomous preview copies curved and multiple physical solids", () =>
                {
                    var capture = new SmartVisualCapture();
                    var mesh = capture.Read(new[] { Cylinder(.2, 3), Box(new XYZ(1, 0, 0), new XYZ(2, 1, 1)) },
                        new List<(Mesh, Transform)>(), false);
                    var points = mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
                    Check(mesh.TriangleCount > 24 && points.Any(p => p.X > .5) && points.Any(p => p.Z > 2),
                        "Actual curved or secondary geometry missing: " + mesh.TriangleCount + " triangles; " + mesh.Notice);
                });
                Test("local clipping preserves crossing triangles with no original vertex in frame", () =>
                {
                    var mesh = new SmartVisualMesh(new[] { new SmartVisualTriangle(new SmartVisualPoint(-10, -10, 0),
                        new SmartVisualPoint(10, -10, 0), new SmartVisualPoint(0, 10, 0)) });
                    var clipped = SmartVisualScene.Clip(mesh, new SmartVisualPoint(0, 0, 0), 1).ToArray();
                    Check(clipped.Length > 0 && clipped.SelectMany(t => new[] { t.A, t.B, t.C }).All(p => Math.Abs(p.X) <= 1.000001 && Math.Abs(p.Y) <= 1.000001), "Incorrect clipping at local boundaries.");
                });
                Test("preview complexity limit is explicit and never fabricates a box", () =>
                {
                    var capture = new SmartVisualCapture();
                    var box = Box(XYZ.Zero, new XYZ(1, 1, 1));
                    var mesh = capture.Read(Enumerable.Repeat(box, 510).ToArray(), new List<(Mesh, Transform)>(), false);
                    Check(mesh.TriangleCount == 0 && !string.IsNullOrWhiteSpace(mesh.Notice), "Complex object displayed as invented geometry.");
                    var scene = capture.Scene(mesh, null, BB(XYZ.Zero, new XYZ(1, 1, 1)), false);
                    Check(!scene.HasGeometry && scene.Thumbnail == null && !string.IsNullOrWhiteSpace(scene.Notice), "Unavailable scene not explained.");
                });
                Test("imported mesh snapshots apply their link transform exactly once", () =>
                {
                    var face = Box(XYZ.Zero, new XYZ(1, 1, 1)).Faces.get_Item(0);
                    var transform = Transform.CreateTranslation(new XYZ(100, 200, 300)).Multiply(Transform.CreateRotation(XYZ.BasisZ, Math.PI / 2));
                    var mesh = new SmartVisualCapture().Read(new Solid[0], new List<(Mesh, Transform)> { (face.Triangulate(), transform) }, true);
                    Check(mesh.TriangleCount > 0 && mesh.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).All(p =>
                        p.X >= 98.99 && p.X <= 100.01 && p.Y >= 199.99 && p.Y <= 201.01 && p.Z >= 299.99 && p.Z <= 301.01), "Mesh transform missing or applied twice.");
                    Check(!string.IsNullOrEmpty(mesh.Notice), "Mesh representation not labelled as indicative.");
                });
                Test("total preview drawing budget remains bounded across many clashes", () =>
                {
                    var triangle = new SmartVisualTriangle(new SmartVisualPoint(0, 0, 0), new SmartVisualPoint(1, 0, 0), new SmartVisualPoint(0, 1, 0));
                    var mesh = new SmartVisualMesh(Enumerable.Repeat(triangle, 2500)); var capture = new SmartVisualCapture(); int ready = 0;
                    SmartVisualScene last = null;
                    for (int i = 0; i < 40; i++) { last = capture.Scene(mesh, null, BB(XYZ.Zero, new XYZ(1, 1, 1)), false); if (last.HasGeometry) ready++; }
                    Check(ready > 0 && ready * 2500 <= SmartVisualCapture.DrawingLimit && !last.HasGeometry && !string.IsNullOrEmpty(last.Notice), "Unbounded drawings or missing budget notice.");
                });
                Test("rotated family instances retain every solid in world coordinates", () => FamilyFixture(ui));
                host = ui.Application.NewProjectDocument(UnitSystem.Metric);
                ElementId sourceId = null, obstacleId = null;
                using (var t = new Transaction(host, "Create clash fixture"))
                {
                    t.Start(); Level.Create(host, 0);
                    sourceId = Shape(host, Box(XYZ.Zero, new XYZ(3, 1, 1))).Id;
                    obstacleId = Shape(host, Box(new XYZ(1, -.5, .2), new XYZ(2, 1.5, .8))).Id;
                    Shape(host, Box(new XYZ(30, 30, 0), new XYZ(31, 31, 1))); t.Commit();
                }
                var setup = new SmartScanSetup { Document = host, DocumentKey = "validation-" + Guid.NewGuid(), Selection = new List<ElementId> { sourceId } };
                SmartScanSession complete = null;
                Test("local clash engine deduplicates symmetric pairs", () =>
                {
                    complete = Scan(setup, GenericOptions());
                    Check(complete.Issues.Count == 1 && complete.Issues[0].RelatedUniqueId != null && !complete.Issues[0].IsApproximate, "Expected one identified physical clash.");
                });
                Test("autonomous images prepare off the API thread and are cached frozen vectors", () =>
                {
                    var scene = complete.Issues[0].VisualScene;
                    Check(scene?.HasGeometry == true && scene.Source != null && scene.Obstacle != null, "No complete preview.");
                    var image = System.Threading.Tasks.Task.Run(() => scene.Thumbnail).GetAwaiter().GetResult();
                    Check(image.IsFrozen && ReferenceEquals(image, scene.Thumbnail), "Thumbnail is regenerated or thread-bound.");
                    var svg = System.Threading.Tasks.Task.Run(() => SmartVisualProjection.Svg(scene)).GetAwaiter().GetResult();
                    Check(svg.Contains("<polygon") && !svg.Contains("NaN") && !svg.Contains("Infinity"), "Independent vector export invalid.");
                    File.WriteAllText(Path.Combine(_folder, "autonomous-preview.svg"), svg);
                });
                Test("interactive viewer orbits and zooms without changing the Revit view or document", () =>
                {
                    var before = host.IsModified;
                    var viewer = new SmartClashViewer(complete.Issues[0].VisualScene);
                    var position = viewer.Camera.Position; viewer.Orbit(50, 25); viewer.Zoom(120);
                    Check(viewer.VertexCount > 0 && viewer.Camera.Position != position && host.IsModified == before, "Viewer needs Revit changes or has no mesh.");
                    viewer.Orbit(0, 100000); viewer.Zoom(120000);
                    Check(!double.IsNaN(viewer.Camera.Position.Z) && ((System.Windows.Media.Media3D.Vector3D)viewer.Camera.Position).Length >= 2.69, "Camera bounds failed.");
                });
                Test("standalone HTML includes autonomous visuals with no Revit capture", () =>
                {
                    var html = SmartClashReport.Html("test", complete.Issues, complete, false, "all");
                    Check(html.Contains("<svg") && html.Contains("<polygon") && !html.Contains("data:image/png"), "Report requires screenshots.");
                    File.WriteAllText(Path.Combine(_folder, "autonomous-report.html"), html);
                });
                Test("selection scopes source objects but includes nearby host obstacles", () =>
                {
                    var options = GenericOptions(); options.Scope = SmartScanScope.Selection;
                    var result = Scan(setup, options);
                    Check(result.SourceCount == 1 && result.Issues.Count == 1 && result.Issues[0].RelatedId == obstacleId, "Selection excluded unselected obstacle.");
                });
                Test("cancellation cannot produce a completed clean bill of health", () =>
                {
                    var session = new SmartScanSession(setup, GenericOptions()); session.CancelRequested = true; session.Advance();
                    Check(session.Complete && session.Cancelled, "Cancellation ignored.");
                    Check(SmartClashReport.Html("test", session.Issues, session, false, "empty").Contains("Résultats partiels"), "Partial export not labelled.");
                });
                Test("empty selection and unsupported views produce explicit errors", () =>
                {
                    var options = GenericOptions(); options.Scope = SmartScanScope.Selection;
                    var empty = new SmartScanSetup { Document = host }; var result = Scan(empty, options, false);
                    Check(result.Error != null, "Empty selection silently accepted.");
                    options.Scope = SmartScanScope.ActiveView; result = Scan(empty, options, false);
                    Check(result.Error != null, "Unsupported view silently accepted.");
                });
                Test("rotated and translated link keeps two distinct obstacle identities", () => LinkedFixture(ui, host, sourceId));
                Test("spatial index prunes disjoint objects and keeps sparse pairs", () =>
                {
                    var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
                    try
                    {
                        using (var t = new Transaction(doc, "Create sparse model"))
                        { t.Start(); for (int i = 0; i < 250; i++) Shape(doc, Box(new XYZ(i * 20, 0, 0), new XYZ(i * 20 + 1, 1, 1))); t.Commit(); }
                        var result = Scan(new SmartScanSetup { Document = doc }, GenericOptions());
                        Check(result.SourceCount == 250 && result.TestedPairs == 0 && result.Issues.Count == 0, "Disjoint model was not spatially pruned.");
                    }
                    finally { doc.Close(false); }
                });
                Test("actual pipe, duct, tray and conduit categories are analysed", () => MepFixture(ui));
                Test("insulation-only collision expands broad and narrow phase geometry", () => InsulationFixture(ui));
                Test("connected MEP parts are not reported as clashes", () => ConnectedFixture(ui));
                ReservationFixture(ui);
                Test("decision remains independent from detection severity", () =>
                {
                    var issue = complete.Issues[0]; var severity = issue.Severity; issue.Status = ModelIssue.StatusFixed;
                    Check(issue.Ignored && issue.Severity == severity, "Manual status changed collision certainty/severity."); issue.Status = ModelIssue.StatusActive;
                });
                Test("status persistence is linked to geometric fingerprint", () =>
                {
                    var state = typeof(SmartCheckState);
                    foreach (var field in new[] { "StoreFolder", "StorePath", "StatusStorePath" })
                    {
                        var path = (string)state.GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                        Check(Path.GetFullPath(path).StartsWith(Path.GetFullPath(_folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                            "Persistence is not isolated in the test output folder.");
                    }
                    var issue = complete.Issues[0]; SmartCheckState.SetIssueStatus(setup.DocumentKey, issue, ModelIssue.StatusFixed, "reviewed", "tester");
                    var same = new ModelIssue { Kind = issue.Kind, ElementUniqueId = issue.ElementUniqueId, RelatedUniqueId = issue.RelatedUniqueId, Fingerprint = issue.Fingerprint };
                    SmartCheckState.RestoreIgnored(setup.DocumentKey, new[] { same }); Check(same.Status == ModelIssue.StatusFixed, "Same geometry lost status.");
                    same.Fingerprint += "changed"; SmartCheckState.RestoreIgnored(setup.DocumentKey, new[] { same });
                    Check(same.Status == ModelIssue.StatusReview && !same.Ignored, "Changed geometry retained a resolved status.");
                });
                Test("analysis preferences persist without serializing Revit objects", () =>
                {
                    var options = new SmartScanOptions { Pipes = false, Ducts = true, MinimumVolumeMm3 = 123.5, Scope = SmartScanScope.Selection };
                    options.LinkIds.Add(sourceId); SmartCheckState.SavePreferences(options);
                    var restored = SmartCheckState.LoadPreferences();
                    Check(restored != null && !restored.Pipes && restored.Ducts && restored.MinimumVolumeMm3 == 123.5 && restored.LinkIds.Count == 0, "Preferences roundtrip failed.");
                    SmartCheckState.SavePreferences(new SmartScanOptions());
                });
                Test("64-bit IDs and stable display choices preserve business values", () =>
                {
                    long value = typeof(ElementId).GetProperty("Value") != null ? 5000000000L : 2000000000L;
                    var id = ElementIdExtensions.CreateElementId(value);
                    Check(new ModelIssue { ElementId = id }.ElementIdValue == value, "Element ID truncated.");
                    Check(new SmartDisplayChoice(ModelIssue.StatusFixed).Value == ModelIssue.StatusFixed, "Display localization changed status value.");
                });
                Test("HTML is escaped, standalone and export respects an empty filtered list", () =>
                {
                    var issue = complete.Issues[0]; issue.StatusComment = "<script>alert(1)</script>";
                    var png = Path.Combine(_folder, "tiny.png"); File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jfP0AAAAASUVORK5CYII=")); issue.ThumbnailPath = png;
                    var html = SmartClashReport.Html("<Model>", new[] { issue }, complete, false, "<filter>");
                    Check(!html.Contains("<script>") && html.Contains("&lt;script&gt;") && html.Contains("data:image/png;base64,") && !html.Contains("file:///"), "Unsafe or nonportable report.");
                    Check(!SmartClashReport.Html("test", new ModelIssue[0], complete, false, "empty").Contains("<article"), "Export leaked hidden results.");
                    issue.ThumbnailPath = null; issue.StatusComment = null;
                });
                Test("CSV escapes multiline comments and neutralizes spreadsheet formulas", () =>
                {
                    var issue = complete.Issues[0]; issue.StatusComment = "  =HYPERLINK(\"url\")\nnext";
                    var csv = SmartClashReport.Csv(new[] { issue }); Check(csv.Contains("'  =HYPERLINK(\"\"url\"\")\nnext"), "Formula injection or escaping error."); issue.StatusComment = null;
                });
                Test("preview names vary with geometry and remain path-safe", () =>
                {
                    var issue = complete.Issues[0]; var before = SmartClashReport.PreviewName(issue); issue.Fingerprint += "changed";
                    Check(before != SmartClashReport.PreviewName(issue) && before.Length == 68 && !before.Contains(":"), "Preview cache key unsafe or stale.");
                });
                // Save/open only this generated fixture to obtain an active UIDocument for real modeless UI tests.
                var projectPath = Path.Combine(_folder, "ui-fixture.rvt"); host.SaveAs(projectPath, new SaveAsOptions { OverwriteExistingFile = true }); host.Close(false);
                host = ui.OpenAndActivateDocument(projectPath).Document;
                StreamingFixture(ui);
                // Opening a document changes its active view asynchronously. Test on a later API callback.
                _next.Action = nextUi => StartFinalTests(nextUi, host); _nextEvent.Raise();
            }
            catch (Exception ex) { _failed++; _results.Add(new { fatal = ex.ToString() }); WriteReport(); File.WriteAllText(Path.Combine(_folder, "finished.txt"), "fatal"); }
        }
        private static SmartScanOptions GenericOptions() => new SmartScanOptions { Pipes = false, Ducts = false, CableTrays = false,
            Conduits = false, GenericModels = true, LinkedClashes = false };
        private static SmartScanSession Scan(SmartScanSetup setup, SmartScanOptions options, bool assertSuccess = true)
        {
            var session = new SmartScanSession(setup, options); int limit = 0;
            while (!session.Complete && limit++ < 20000) session.Advance(10);
            Check(session.Complete, "Scan did not terminate."); if (assertSuccess) Check(session.Error == null, session.Error); return session;
        }
        private static BoundingBoxXYZ BB(XYZ min, XYZ max) => new BoundingBoxXYZ { Min = min, Max = max };
        private static Solid Box(XYZ min, XYZ max)
        {
            var loop = new CurveLoop(); var points = new[] { min, new XYZ(max.X, min.Y, min.Z), new XYZ(max.X, max.Y, min.Z), new XYZ(min.X, max.Y, min.Z) };
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, max.Z - min.Z);
        }
        private static Solid Cylinder(double radius, double length)
        {
            var loop = new CurveLoop(); loop.Append(Arc.Create(XYZ.Zero, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            loop.Append(Arc.Create(XYZ.Zero, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, length);
        }
        private static DirectShape Shape(Document doc, Solid solid)
        { var shape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel)); shape.SetShape(new GeometryObject[] { solid }); return shape; }
        private void LinkedFixture(UIApplication ui, Document host, ElementId sourceId)
        {
            var doc = ui.Application.NewProjectDocument(UnitSystem.Metric); var path = Path.Combine(_folder, "linked-fixture.rvt");
            try
            {
                using (var t = new Transaction(doc, "Link obstacles"))
                { t.Start(); Shape(doc, Box(new XYZ(-19.8, 9, .2), new XYZ(-19.2, 9.5, .8))); Shape(doc, Box(new XYZ(-19.8, 8, .2), new XYZ(-19.2, 8.5, .8))); t.Commit(); }
                doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally { doc.Close(false); }
            ElementId linkId;
            using (var t = new Transaction(host, "Load rotated link"))
            {
                t.Start(); var result = RevitLinkType.Create(host, ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new RevitLinkOptions(false));
                var instance = RevitLinkInstance.Create(host, result.ElementId); linkId = instance.Id;
                ElementTransformUtils.RotateElement(host, linkId, Line.CreateBound(XYZ.Zero, XYZ.BasisZ), Math.PI / 2);
                ElementTransformUtils.MoveElement(host, linkId, new XYZ(10, 20, 0)); t.Commit();
            }
            var setup = new SmartScanSetup { Document = host, Selection = new List<ElementId> { sourceId } };
            var options = GenericOptions(); options.Scope = SmartScanScope.Selection; options.LocalClashes = false; options.LinkedClashes = true; options.LinkIds.Add(linkId);
            var scan = Scan(setup, options);
            Check(scan.Issues.Count == 2 && scan.Issues.Select(i => i.LinkedElementId.GetIdValue()).Distinct().Count() == 2, "Link obstacles aggregated or transform incorrect: " + scan.Issues.Count);
            Check(scan.Issues.All(i => i.BBox.Min.X >= -.01 && i.BBox.Max.X <= 3.01), "Linked intersection box in wrong coordinates.");
            Check(scan.Issues.All(i => i.VisualScene?.HasGeometry == true && i.VisualScene.Obstacle.Triangles.SelectMany(t => new[] { t.A, t.B, t.C })
                .All(p => p.X >= -.01 && p.X <= 3.01)), "Linked preview does not use host-world coordinates.");
            Check(ReferenceEquals(scan.Issues[0].VisualScene.Source, scan.Issues[1].VisualScene.Source), "Source mesh copied for each conflict.");
        }
        private void MepFixture(UIApplication ui)
        {
            var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                using (var t = new Transaction(doc, "Create MEP fixtures"))
                {
                    t.Start(); var level = Level.Create(doc, 0);
                    var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
                    var ductType = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().First();
                    var pipeSystem = PipingSystemType.Create(doc, MEPSystemClassification.DomesticColdWater, "Test water");
                    var ductSystem = MechanicalSystemType.Create(doc, MEPSystemClassification.SupplyAir, "Test air");
                    Pipe.Create(doc, pipeSystem.Id, pipeType.Id, level.Id, new XYZ(-3, 0, 1), new XYZ(3, 0, 1));
                    Duct.Create(doc, ductSystem.Id, ductType.Id, level.Id, new XYZ(-3, 4, 1), new XYZ(3, 4, 1));
                    var trayType = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>().First();
                    var conduitType = new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ConduitType>().First();
                    CableTray.Create(doc, trayType.Id, new XYZ(-3, 8, 1), new XYZ(3, 8, 1), level.Id);
                    Conduit.Create(doc, conduitType.Id, new XYZ(-3, 12, 1), new XYZ(3, 12, 1), level.Id);
                    Shape(doc, Box(new XYZ(-.1, -2, 0), new XYZ(.1, 14, 3))); t.Commit();
                }
                var options = new SmartScanOptions { GenericModels = true, LinkedClashes = false, OpenConnectors = true };
                var scan = Scan(new SmartScanSetup { Document = doc }, options);
                File.WriteAllText(Path.Combine(_folder, "mep-details.json"), JsonConvert.SerializeObject(new { scan.SourceCount, scan.CandidateCount, scan.TestedPairs,
                    scan.Diagnostics, issues = scan.Issues.Select(i => new { kind = i.Kind.ToString(), i.ElementLabel, i.ObstacleLabel, i.Message, i.IsApproximate, i.IntersectionVolumeMm3 }) }, Formatting.Indented));
                Check(scan.Issues.Count(i => i.Kind == IssueKind.LocalClash) >= 4, "MEP categories not all covered.");
                Check(scan.Issues.Count(i => i.Kind == IssueKind.MepUnconnected) == 4, "Open end connector count incorrect.");
                Check(scan.Issues.Where(i => i.Kind == IssueKind.LocalClash).All(i => i.VisualScene?.Source != null && i.VisualScene.Obstacle != null), "A MEP category has no autonomous preview.");
                var pipeIssue = scan.Issues.First(i => i.Kind == IssueKind.LocalClash && doc.GetElement(i.ElementId) is Pipe);
                File.WriteAllText(Path.Combine(_folder, "mep-pipe.svg"), SmartVisualProjection.Svg(pipeIssue.VisualScene));
                var inspection = new SmartIssueInspector(pipeIssue, _ => { }, (i,s,c) => true, _ => { }, (i,n) => { });
                try { inspection.Show(); Render(inspection, "mep-inspection.png", 650, 760); }
                finally { inspection.Close(); }
                var pipeOnly = new SmartScanOptions { Ducts = false, CableTrays = false, Conduits = false, LinkedClashes = false };
                var subset = Scan(new SmartScanSetup { Document = doc }, pipeOnly);
                Check(subset.SourceCount == 1 && subset.Issues.Count == 1, "Source category selection excluded an obstacle category.");
            }
            finally { doc.Close(false); }
        }
        private void RenderUi(UIApplication ui)
        {
            var setup = SmartScanSetup.Capture(ui.ActiveUIDocument); var handler = new SmartExternalHandler(ui);
            var ev = ExternalEvent.Create(handler); var win = new SmartCheckWindow(ev, handler, setup);
            try
            {
                win.Show(); Render(win, "main-ready-960.png", 960, 760);
                ((Expander)win.FindName("SettingsExpander")).IsExpanded = true; Render(win, "main-settings-760.png", 760, 560);
                ((Expander)win.FindName("SettingsExpander")).IsExpanded = false;
                var session = Scan(setup, GenericOptions());
                typeof(SmartCheckWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(win, session);
                typeof(SmartCheckWindow).GetMethod("FinishScan", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(win, null);
                Render(win, "main-results-960.png", 960, 760); Render(win, "main-results-760.png", 760, 560);
                handler.FocusIssue = session.Issues[0]; handler.Action = SmartAction.FocusApply; handler.Execute(ui);
                Render(win, "main-focused-760.png", 760, 560);
                Check(((System.Windows.UIElement)win.FindName("FocusBar")).Visibility == System.Windows.Visibility.Visible, "Focused actions are missing from the main window.");
                handler.Action = SmartAction.ToggleContext; handler.Execute(ui); Render(win, "main-context-960.png", 960, 760);
                handler.Action = SmartAction.RestoreView; handler.Execute(ui);
                Check(((System.Windows.UIElement)win.FindName("FocusBar")).Visibility == System.Windows.Visibility.Collapsed, "Focus actions remain after view restoration.");
                var list = (ListBox)win.FindName("ResultsList"); Check(list.Items.Count > 0, "Results not visible.");
                var search = (TextBox)win.FindName("SearchBox"); search.Text = "nonexistent-filter";
                Check(list.Items.Count == 0, "Search did not filter results."); search.Clear();
                var statuses = (ComboBox)win.FindName("StatusCombo");
                statuses.SelectedItem = statuses.Items.Cast<SmartDisplayChoice>().First(c => c.Value == ModelIssue.StatusFixed);
                Check(list.Items.Count == 0, "Status filter did not use stable values.");
                statuses.SelectedItem = statuses.Items.Cast<SmartDisplayChoice>().First(c => c.Value == "Tous");
                Check(list.Items.Count > 0, "All-status filter did not restore results.");
                var inspector = new SmartIssueInspector(session.Issues[0], _ => { }, (i,s,c) => true, _ => { }, (i,n) => { }) { Owner = win };
                inspector.Show(); Render(inspector, "inspection-650.png", 650, 760); Render(inspector, "inspection-480.png", 480, 500); inspector.Close();
            }
            finally { typeof(SmartCheckWindow).GetField("_closeConfirmed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(win, true); win.Close(); }
        }
        private void StreamingFixture(UIApplication ui)
        {
            var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
            SmartCheckWindow win = null; SmartScanSession session = null;
            try
            {
                using (var t = new Transaction(doc, "Progressive results fixture"))
                {
                    t.Start();
                    for (int i = 0; i < 40; i++)
                    {
                        var x = i * 20;
                        Shape(doc, Box(new XYZ(x, 0, 0), new XYZ(x + 2, 2, 2)));
                        Shape(doc, Box(new XYZ(x + 1, 1, 1), new XYZ(x + 3, 3, 3)));
                    }
                    t.Commit();
                }
                var setup = new SmartScanSetup { Document = doc, DocumentKey = "streaming-fixture", Title = "Affichage progressif" };
                var handler = new SmartExternalHandler(ui) { Action = SmartAction.ScanBatch };
                win = new SmartCheckWindow(ExternalEvent.Create(handler), handler, setup);
                session = new SmartScanSession(setup, GenericOptions()); handler.Session = session;
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                typeof(SmartCheckWindow).GetField("_session", flags).SetValue(win, session);
                typeof(SmartCheckWindow).GetMethod("SetScanning", flags).Invoke(win, new object[] { true });
                ((FrameworkElement)win.FindName("FilterPanel")).Visibility = System.Windows.Visibility.Visible;
                win.Show();
                var list = (ListBox)win.FindName("ResultsList"); var search = (TextBox)win.FindName("SearchBox");
                var statuses = (ComboBox)win.FindName("StatusCombo");
                statuses.SelectedItem = statuses.Items.Cast<SmartDisplayChoice>().First(c => c.Value == "Tous");
                Action publish = () =>
                {
                    typeof(SmartCheckWindow).GetMethod("Handler_Completed", flags).Invoke(win, null);
                    ((System.Windows.Threading.DispatcherTimer)typeof(SmartCheckWindow).GetField("_queueTimer", flags).GetValue(win)).Stop();
                    typeof(SmartCheckWindow).GetField("_queuedAction", flags).SetValue(win, null);
                };
                Test("preparation and empty filters never claim a clean completed analysis", () =>
                {
                    session.Advance(0); publish(); search.Text = "missing";
                    Check(((TextBlock)win.FindName("EmptyTitle")).Text == "Analyse en cours", "Preparation displayed a clean bill of health.");
                    Check(((TextBlock)win.FindName("SummaryText")).Text.Contains("provisoire"), "Provisional summary missing.");
                    Check(!((Button)win.FindName("ExportButton")).IsEnabled, "Incomplete export enabled."); search.Clear();
                });
                Test("first result is published before triangulation and its preview updates in place", () =>
                {
                    int limit = 0; while (session.Issues.Count == 0 && !session.Complete && limit++ < 400) session.Advance(0);
                    Check(session.Issues.Count == 1 && !session.Complete, "No early result.");
                    var first = session.Issues[0]; Check(first.VisualPending && first.VisualScene == null, "Preview delayed result publication.");
                    publish(); Check(list.Items.Count == 1 && ReferenceEquals(list.Items[0], first), "Early result absent from UI.");
                    Render(win, "main-progressive-first.png", 960, 760);
                    typeof(SmartCheckWindow).GetMethod("Inspect", flags).Invoke(win, new object[] { first });
                    var inspector = (SmartIssueInspector)typeof(SmartCheckWindow).GetField("_inspector", flags).GetValue(win);
                    bool notified = false; first.PropertyChanged += (s,e) => { if (e.PropertyName == "VisualScene") notified = true; };
                    session.Advance(0); publish();
                    Check(notified && !first.VisualPending && first.VisualScene?.HasGeometry == true, "Delayed preview did not notify existing bindings.");
                    var visual = (StackPanel)typeof(SmartIssueInspector).GetField("_visual", flags).GetValue(inspector);
                    Check(visual.Children.OfType<SmartClashViewer>().Any(), "Open inspector did not receive the preview.");
                    Check(!((Button)typeof(SmartIssueInspector).GetField("_focusButton", flags).GetValue(inspector)).IsEnabled, "Native focus enabled while scanning.");
                });
                Test("successive result batches preserve selection, filters, decisions and collection identity", () =>
                {
                    var first = session.Issues[0]; var collection = list.ItemsSource;
                    list.SelectedItem = first;
                    search.Text = "missing";
                    int limit = 0; while (session.Issues.Count < 2 && !session.Complete && limit++ < 400) { session.Advance(0); publish(); }
                    Check(list.Items.Count == 0 && session.Issues.Count >= 2, "New batch bypassed the active search."); search.Clear();
                    list.SelectedItem = first;
                    typeof(SmartCheckWindow).GetMethod("SaveDecision", flags).Invoke(win, new object[] { first, ModelIssue.StatusFixed, "during scan" });
                    int resets = 0;
                    ((System.Collections.Specialized.INotifyCollectionChanged)collection).CollectionChanged += (s,e) =>
                    { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && !session.Complete) resets++; };
                    limit = 0; while (!session.Complete && limit++ < 1000) { session.Advance(0); publish(); }
                    Check(session.Complete && session.Error == null && session.Issues.Count == 40, session.Error ?? "Missing dense fixture clashes.");
                    Check(ReferenceEquals(collection, list.ItemsSource) && resets == 0, "Incoming batch reset the result list.");
                    Check(ReferenceEquals(list.SelectedItem, first), "Incoming batch lost the selection.");
                    Check(first.Status == ModelIssue.StatusFixed && first.StatusComment == "during scan", "Finishing restored over a decision taken during scan.");
                    Check(list.Items.Count == 40 && list.Items.Cast<ModelIssue>().Distinct().Count() == 40, "Duplicate or missing rows.");
                    Check(!((TextBlock)win.FindName("SummaryText")).Text.Contains("provisoire"), "Completed summary still provisional.");
                    Check(session.Issues.All(i => !i.VisualPending && i.VisualScene != null), "Complete scan left pending previews.");
                    Render(win, "main-progressive-complete.png", 960, 760);
                });
                Test("cancelling after first publication keeps partial rows and clears pending previews", () =>
                {
                    using (var cancelled = new SmartScanSession(setup, GenericOptions()))
                    {
                        int limit = 0; while (cancelled.Issues.Count == 0 && !cancelled.Complete && limit++ < 400) cancelled.Advance(0);
                        var first = cancelled.Issues[0]; Check(first.VisualPending, "Expected pending preview before cancellation.");
                        cancelled.CancelRequested = true; cancelled.Advance();
                        Check(cancelled.Cancelled && cancelled.Complete && cancelled.Issues.Count == 1 && !first.VisualPending, "Partial results or pending preview state lost.");
                        Check(SmartClashReport.Html("partial", cancelled.Issues, cancelled, false, "all").Contains("Résultats partiels"), "Cancelled export reports complete results.");
                    }
                });
                Test("dense scan yields a bounded batch before generating its previews", () =>
                {
                    using (var dense = new SmartScanSession(setup, GenericOptions()))
                    {
                        dense.Advance(10000);
                        Check(!dense.Complete && dense.Issues.Count == 32 && dense.Issues.All(i => i.VisualPending && i.VisualScene == null),
                            "Dense scan monopolized result publication or generated new previews in the same callback.");
                        dense.CancelRequested = true; dense.Advance();
                        Check(dense.Cancelled && dense.Issues.All(i => !i.VisualPending), "Dense cancellation left pending state.");
                    }
                });
            }
            finally
            {
                session?.Dispose();
                if (win != null) { typeof(SmartCheckWindow).GetField("_closeConfirmed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(win, true); win.Close(); }
                doc.Close(false);
            }
        }
        private void InsulationFixture(UIApplication ui)
        {
            var templates = "C:/ProgramData/Autodesk/RVT " + ui.Application.VersionNumber + "/Templates";
            var template = Directory.EnumerateFiles(templates, "Plumbing-*.rte", SearchOption.AllDirectories)
                .OrderBy(p => p.Contains("French") ? 0 : 1).First();
            var doc = ui.Application.NewProjectDocument(template);
            try
            {
                using (var t = new Transaction(doc, "Insulation fixture"))
                {
                    t.Start(); var level = Level.Create(doc, 0);
                    var type = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
                    var system = PipingSystemType.Create(doc, MEPSystemClassification.DomesticColdWater, "Insulated test");
                    var pipe = Pipe.Create(doc, system.Id, type.Id, level.Id, new XYZ(-3,0,1), new XYZ(3,0,1));
                    pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(100 / 304.8);
                    var insulation = new FilteredElementCollector(doc).OfClass(typeof(PipeInsulationType)).Cast<PipeInsulationType>().First();
                    PipeInsulation.Create(doc, pipe.Id, insulation.Id, 50 / 304.8);
                    Shape(doc, Box(new XYZ(-.2, 80 / 304.8, 1 - 10 / 304.8), new XYZ(.2, 90 / 304.8, 1 + 10 / 304.8))); t.Commit();
                }
                var options = new SmartScanOptions { Ducts = false, CableTrays = false, Conduits = false, LinkedClashes = false, IncludeInsulation = false };
                Check(Scan(new SmartScanSetup { Document = doc }, options).Issues.Count == 0, "Bare pipe should clear the obstacle.");
                options.IncludeInsulation = true;
                var result = Scan(new SmartScanSetup { Document = doc }, options);
                Check(result.Issues.Count == 1 && !result.Issues[0].IsApproximate, "Physical insulation clash missed.");
            }
            finally { doc.Close(false); }
        }
        private void FamilyFixture(UIApplication ui)
        {
            var root = Directory.Exists(ui.Application.FamilyTemplatePath) ? ui.Application.FamilyTemplatePath
                : "C:/ProgramData/Autodesk/RVT " + ui.Application.VersionNumber + "/Family Templates";
            var template = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)
                .Where(p => new[] { "Modèle générique métrique", "Metric Generic Model", "Appareil téléphonique" }.Contains(Path.GetFileNameWithoutExtension(p)))
                .OrderBy(p => Path.GetFileNameWithoutExtension(p) == "Appareil téléphonique" ? 1 : 0).First();
            var familyDoc = ui.Application.NewFamilyDocument(template); var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                using (var t = new Transaction(familyDoc, "Multi-solid family"))
                {
                    t.Start(); familyDoc.OwnerFamily.FamilyCategory = Category.GetCategory(familyDoc, BuiltInCategory.OST_GenericModel);
                    var plane = SketchPlane.Create(familyDoc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    foreach (var limits in new[] { new[] { XYZ.Zero, new XYZ(1,1,1) }, new[] { new XYZ(10,10,0), new XYZ(15,15,5) } })
                    {
                        var min = limits[0]; var max = limits[1]; var profile = new CurveArray();
                        var pts = new[] { min, new XYZ(max.X,min.Y,0), new XYZ(max.X,max.Y,0), new XYZ(min.X,max.Y,0) };
                        for (int i=0;i<4;i++) profile.Append(Line.CreateBound(pts[i],pts[(i+1)%4]));
                        var profiles = new CurveArrArray(); profiles.Append(profile); familyDoc.FamilyCreate.NewExtrusion(true,profiles,plane,max.Z);
                    }
                    t.Commit();
                }
                var family = familyDoc.LoadFamily(doc); var symbol = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().First();
                using (var t = new Transaction(doc, "Rotate multi-solid instance"))
                {
                    t.Start(); var level = Level.Create(doc,0); symbol.Activate(); doc.Regenerate();
                    var instance = doc.Create.NewFamilyInstance(new XYZ(20,20,0),symbol,level,Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    ElementTransformUtils.RotateElement(doc,instance.Id,Line.CreateBound(new XYZ(20,20,0),new XYZ(20,20,1)),Math.PI/4);
                    Shape(doc, Box(new XYZ(19.9,20.5,.1), new XYZ(20.1,20.8,.8))); t.Commit();
                }
                var result = Scan(new SmartScanSetup { Document = doc }, GenericOptions());
                Check(result.Issues.Count == 1 && !result.Issues[0].IsApproximate, "Transformed small family solid missed.");
            }
            finally { doc.Close(false); familyDoc.Close(false); }
        }
        private void ConnectedFixture(UIApplication ui)
        {
            var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                using (var t = new Transaction(doc, "Connected pipes fixture"))
                {
                    t.Start(); var level = Level.Create(doc, 0);
                    var type = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
                    var system = PipingSystemType.Create(doc, MEPSystemClassification.DomesticColdWater, "Connected test");
                    var p = Pipe.Create(doc, system.Id, type.Id, level.Id, new XYZ(-3,0,1), new XYZ(0,0,1));
                    var q = Pipe.Create(doc, system.Id, type.Id, level.Id, new XYZ(0,0,1), new XYZ(0,3,1));
                    var a = p.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(new XYZ(0,0,1))).First();
                    var b = q.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(new XYZ(0,0,1))).First(); a.ConnectTo(b); t.Commit();
                }
                var options = new SmartScanOptions { Ducts = false, CableTrays = false, Conduits = false, LinkedClashes = false };
                var result = Scan(new SmartScanSetup { Document = doc }, options);
                Check(result.Issues.Count == 0, "Expected connected components to be excluded.");
            }
            finally { doc.Close(false); }
        }
        private void Render(Window win, string filename, double width, double height)
        {
            win.Width = width; win.Height = height; win.UpdateLayout();
            var content = (FrameworkElement)win.Content; content.Measure(new Size(width - 40, height - 60)); content.Arrange(new Rect(0, 0, width - 40, height - 60)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(width - 40), (int)(height - 60), 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(win.Background, null, new Rect(0,0,width-40,height-60));
            bitmap.Render(visual); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(_folder, filename))) encoder.Save(stream);
            foreach (var button in Descendants(content).OfType<Button>().Where(b => b.IsVisible))
            {
                var p = button.TranslatePoint(new Point(0,0), content);
                DependencyObject parent = button; bool scrolls = false;
                while ((parent = VisualTreeHelper.GetParent(parent)) != null) if (parent is ScrollViewer) { scrolls = true; break; }
                Check(p.X >= -1 && p.X + button.ActualWidth <= content.ActualWidth + 1
                    && (scrolls || p.Y >= -1 && p.Y + button.ActualHeight <= content.ActualHeight + 1),
                    "Clipped button: " + button.Content + " at " + p + " size " + button.ActualWidth + "x" + button.ActualHeight
                    + " in " + content.ActualWidth + "x" + content.ActualHeight);
            }
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        { for (int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var child=VisualTreeHelper.GetChild(parent,i); yield return child; foreach(var c in Descendants(child)) yield return c; } }
        private void StartFinalTests(UIApplication ui, Document host)
        {
            Test("main window and inspection render at default and minimum sizes", () => RenderUi(ui));
            var originalView = new FilteredElementCollector(host).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate);
            if (originalView == null)
                using (var t = new Transaction(host, "Create original validation view"))
                { t.Start(); var type = new FilteredElementCollector(host).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                    originalView = View3D.CreateIsometric(host, type.Id); originalView.Name = "Validation · vue originale"; t.Commit(); }
            ui.ActiveUIDocument.ActiveView = originalView;
            var setup = SmartScanSetup.Capture(ui.ActiveUIDocument); var scan = Scan(setup, GenericOptions()); var original = originalView.Id;
            var issue = scan.Issues.First(i => i.Kind == IssueKind.LocalClash);
            var handler = new SmartExternalHandler(ui); var external = ExternalEvent.Create(handler);
            string error = null; handler.Failed += e => error = e;
            int contextPhase = 0;
            handler.FocusIssue = issue; handler.Action = SmartAction.FocusApply;
            handler.Completed += () =>
            {
                if (handler.Action == SmartAction.FocusApply)
                {
                    Test("real ExternalEvent focus is local and uses distinct colours", () =>
                    {
                        Check(error == null, error); var view = ui.ActiveUIDocument.ActiveView as View3D;
                        Check(view != null && view.IsSectionBoxActive && view.GetSectionBox().Max.X - view.GetSectionBox().Min.X < 10, "Focus was not local.");
                        var a = view.GetElementOverrides(issue.ElementId).ProjectionLineColor; var b = view.GetElementOverrides(issue.RelatedId).ProjectionLineColor;
                        Check(a.Red != b.Red, "Objects not distinguished by colour.");
                    });
                    handler.Action = SmartAction.ToggleContext; error = null;
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => external.Raise()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    return;
                }
                if (handler.Action == SmartAction.ToggleContext)
                {
                    bool around = contextPhase++ == 0;
                    Test(around ? "context toggle removes section box and keeps the focused clash" : "context toggle restores the exact local frame", () =>
                    {
                        Check(error == null, error); var view = (View3D)ui.ActiveUIDocument.ActiveView;
                        Check(view.IsSectionBoxActive != around && handler.ContextVisible == around && ReferenceEquals(handler.DisplayedIssue, issue), "Context toggle lost focus or clipping state.");
                        if (!around) Check(view.GetSectionBox().Max.X - view.GetSectionBox().Min.X < 10, "Local frame was not restored.");
                    });
                    handler.Action = around ? SmartAction.ToggleContext : SmartAction.RestoreView;
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => external.Raise()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    return;
                }
                Test("real ExternalEvent restores the original view", () => Check(error == null && ui.ActiveUIDocument.ActiveView.Id == original, error ?? "Original view not restored."));
                Test("model modification cancels a scan between API slices", () =>
                {
                    using (var scanHandler = new SmartExternalHandler(ui))
                    {
                        scanHandler.Session = new SmartScanSession(SmartScanSetup.Capture(ui.ActiveUIDocument), GenericOptions()); scanHandler.Session.Advance(0);
                        using (var t = new Transaction(host, "Modify fixture")) { t.Start(); Shape(host, Box(new XYZ(50, 0, 0), new XYZ(51, 1, 1))); t.Commit(); }
                        scanHandler.Session.Advance(); Check(scanHandler.Session.Cancelled, "Model edit did not invalidate ongoing geometry.");
                    }
                });
                host.Save();
                var liveSelection = new List<ElementId>();
                using (var t = new Transaction(host, "Selection changed after opening Clash"))
                {
                    t.Start();
                    for (int i = 0; i < 563; i++)
                    {
                        double x = 200 + i * 20;
                        if (i == 1) x = 200.5;
                        liveSelection.Add(Shape(host, Box(new XYZ(x, 0, 0), new XYZ(x + 1, 1, 1))).Id);
                    }
                    t.Commit();
                }
                host.Save();
                ui.ActiveUIDocument.Selection.SetElementIds(new List<ElementId>());
                var windowHandler = new SmartExternalHandler(ui); var windowEvent = ExternalEvent.Create(windowHandler);
                var window = new SmartCheckWindow(windowEvent, windowHandler, SmartScanSetup.Capture(ui.ActiveUIDocument)); window.Show();
                foreach (var name in new[] { "PipesCheck", "DuctsCheck", "TraysCheck", "ConduitsCheck", "FittingsCheck", "EquipmentCheck", "LinksCheck", "InsulationCheck", "ConnectorsCheck", "WallsCheck" })
                    ((CheckBox)window.FindName(name)).IsChecked = false;
                ((CheckBox)window.FindName("GenericCheck")).IsChecked = ((CheckBox)window.FindName("LocalCheck")).IsChecked = true;
                ((ComboBox)window.FindName("ScopeCombo")).SelectedIndex = 2;
                ui.ActiveUIDocument.Selection.SetElementIds(liveSelection);
                bool earlyRows = false, scanRecorded = false;
                windowHandler.Completed += () =>
                {
                    if (windowHandler.Action != SmartAction.ScanBatch || scanRecorded) return;
                    var liveScan = windowHandler.Session;
                    if (!liveScan.Complete && ((ListBox)window.FindName("ResultsList")).Items.Count > 0) earlyRows = true;
                    if (!liveScan.Complete) return;
                    scanRecorded = true;
                    Test("modeless Analyze refreshes 563 objects selected after window creation", () =>
                    {
                        Check(liveScan.Error == null && !liveScan.Cancelled && liveScan.SourceCount == 563, liveScan.Error ?? "Live selection not captured.");
                        Check(((TextBlock)window.FindName("DocumentText")).Text.Contains("563 objet(s)"), "Header still displays the old empty selection.");
                        Check(liveScan.Issues.Count == 1 && ((ListBox)window.FindName("ResultsList")).Items.Count == 1, "Expected one actual selected-object collision.");
                    });
                    Test("actual WPF Analyze click streams results through real Revit ExternalEvents", () =>
                        Check(earlyRows, "Actual modeless analysis only displayed results after completion."));
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(window.Close), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                };
                _next.Action = nextUi =>
                {
                    Test("modeless window closes and unregisters events in a valid API callback", () =>
                        Check((bool)typeof(SmartCheckWindow).GetField("_closeConfirmed", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window), "CloseSession did not finish."));
                    Test("native harness cleanup completes in the Revit API context", () => { handler.Dispose(); external.Dispose(); });
                    FinishOrReadRealFixture(nextUi);
                };
                window.Closed += (s, e) => _nextEvent.Raise();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                    typeof(SmartCheckWindow).GetMethod("Analyze_Click", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(window, new object[] { window.FindName("AnalyzeButton"), new RoutedEventArgs() })),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            external.Raise();
        }
    }
}


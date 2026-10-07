using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Analyse;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace BIMaestro.Tests
{
    // References the complete production assembly: no tutorial, window or engine stubs.
    public sealed class TutorialValidation : IExternalApplication
    {
        private string _folder;
        private UIControlledApplication _application;
        private UIApplication _ui;
        private Document _doc;
        private SmartCheckWindow _window;
        private SmartExternalHandler _handler;
        private ExternalEvent _event, _nextEvent;
        private ApiStep _next;
        private int _stage, _failures;
        private DateTime _deadline;
        private string _preferencesSnapshot;
        private ElementId _pendingView;
        private bool _ownsRun;
        private int _scanNumber;
        private readonly List<object> _results = new List<object>();
        private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        private static Type Tutorial(string name) => typeof(SmartCheckWindow).Assembly.GetType("BIMaestro.Tutorials." + name, true);
        private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Hidden).Invoke(target, args);
        private static object Static(string type, string method, params object[] args) => Tutorial(type).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
        private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden).GetValue(target);
        private object Guide => ((IDictionary)Tutorial("DemoTourService").GetField("ActiveGuides", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null))[_window];
        private sealed class ApiStep : IExternalEventHandler
        {
            internal Action Pending;
            public void Execute(UIApplication app) { var action = Pending; Pending = null; action?.Invoke(); }
            public string GetName() => "BIMaestro · validation du tutoriel Clash 3D";
        }
        public Result OnStartup(UIControlledApplication app)
        {
            _application = app; _folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            try
            {
                using (var claim = new FileStream(Path.Combine(_folder, "claim-pid.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(claim)) writer.Write(Process.GetCurrentProcess().Id);
                _ownsRun = true;
            }
            catch (IOException) { return Result.Succeeded; }
            Dispatcher.CurrentDispatcher.UnhandledException += Unexpected;
            _deadline = DateTime.MaxValue;
            app.Idling += Start; app.Idling += Watchdog;
            return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app)
        {
            if (!_ownsRun) return Result.Succeeded;
            app.Idling -= Start; app.Idling -= Watchdog;
            Dispatcher.CurrentDispatcher.UnhandledException -= Unexpected;
            File.WriteAllText(Path.Combine(_folder, "exited-cleanly.txt"), "OnShutdown");
            return Result.Succeeded;
        }
        private void Unexpected(object sender, DispatcherUnhandledExceptionEventArgs e)
        { Test("unexpected UI exception", () => { throw e.Exception; }); e.Handled = true; Schedule(Cleanup); }
        private void Test(string name, Action action)
        {
            File.WriteAllText(Path.Combine(_folder, "progress.txt"), name);
            try { action(); _results.Add(new { name, passed = true }); }
            catch (Exception ex) { _failures++; _results.Add(new { name, passed = false, error = ex.ToString() }); }
            Report();
        }
        private void Report() => File.WriteAllText(Path.Combine(_folder, "result.json"), JsonConvert.SerializeObject(new { failed = _failures, tests = _results }, Formatting.Indented));
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        private void Schedule(Action action)
        {
            _next.Pending = () => { try { action(); } catch (Exception ex) { Test("stage " + _stage, () => { throw ex; }); Cleanup(); } };
            _nextEvent.Raise();
        }
        private void Watchdog(object sender, IdlingEventArgs args)
        {
            if (_pendingView != null && _ui?.ActiveUIDocument?.ActiveView.Id == _pendingView)
            { _pendingView = null; Schedule(BeginSceneTests); return; }
            if (DateTime.UtcNow <= _deadline || _stage == 99) return;
            Test("tutorial completes before deadline", () => { throw new TimeoutException("Stage " + _stage); });
            _deadline = DateTime.UtcNow.AddMinutes(1); Schedule(Cleanup);
        }
        private void Start(object sender, IdlingEventArgs args)
        {
            _application.Idling -= Start; _ui = sender as UIApplication;
            File.WriteAllText(Path.Combine(_folder, "started.txt"), Process.GetCurrentProcess().Id.ToString());
            if (_ui == null || _ui.Application.Documents.Size != 0)
            { Test("dedicated empty Revit instance", () => { throw new Exception("No user document was touched."); }); return; }
            _next = new ApiStep(); _nextEvent = ExternalEvent.Create(_next);
            _deadline = DateTime.UtcNow.AddMinutes(3);
            try
            {
                Check(typeof(SmartCheckWindow).Assembly.GetName().Name.StartsWith("BIMaestro.ClashTutorialValidation", StringComparison.Ordinal), "Installed plugin resolved instead of the tested assembly.");
                foreach (string name in new[] { "StoreFolder", "StorePath", "StatusStorePath" })
                    typeof(SmartCheckState).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null,
                        name == "StoreFolder" ? _folder : Path.Combine(_folder, name + ".json"));
                var draft = _ui.Application.NewProjectDocument(UnitSystem.Metric);
                using (var tx = new Transaction(draft, "Prepare training template"))
                {
                    tx.Start();
                    if (!new FilteredElementCollector(draft).OfClass(typeof(Level)).Any()) Level.Create(draft, 0);
                    if (!new FilteredElementCollector(draft).OfClass(typeof(PipingSystemType)).Any())
                        PipingSystemType.Create(draft, MEPSystemClassification.DomesticColdWater, "Tutorial water");
                    tx.Commit();
                }
                string path = Path.Combine(_folder, "BIMaestro_Apprentissage_ClashTest.rvt");
                draft.SaveAs(path); draft.Close(false); _doc = _ui.OpenAndActivateDocument(path).Document;
                // Suppress only the installed plugin's invitation for this test fixture in this dedicated process.
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    assembly.GetType("BIMaestro.Tutorials.DemoTrainingInvitation")?.GetField("_pendingPath", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
                Test("ordinary project is refused by correction and scene preparation", () =>
                {
                    var other = _ui.Application.NewProjectDocument(UnitSystem.Metric);
                    bool initiallyModified = other.IsModified;
                    try
                    {
                        foreach (string method in new[] { "Prepare", "Correct" })
                        {
                            bool refused = false;
                            try { Static("DemoClashExercise", method, other); }
                            catch (TargetInvocationException ex) { refused = ex.InnerException is InvalidOperationException; }
                            Check(refused && other.IsModified == initiallyModified, method + " changed an ordinary project.");
                        }
                    }
                    finally { other.Close(false); }
                });
                var view = (View3D)Static("DemoClashExercise", "Prepare", _doc);
                _pendingView = view.Id;
                _ui.ActiveUIDocument.RequestViewChange(view);
            }
            catch (Exception ex) { Test("initialization", () => { throw ex; }); Schedule(Cleanup); }
        }
        private void BeginSceneTests()
        {
            try
            {
                Test("scene has exactly three sources and two confirmed pairs", () =>
                {
                    var scan = SceneScan(); Check((bool)Static("DemoClashExercise", "Verify", _doc, scan, false), "Initial pairs differ."); scan.Dispose();
                });
                Test("correction, repeat guard and reset preserve unrelated geometry", () =>
                {
                    ElementId unrelated;
                    using (var tx = new Transaction(_doc, "Unrelated wall"))
                    { tx.Start(); var type = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
                        var level = new FilteredElementCollector(_doc).OfClass(typeof(Level)).First();
                        unrelated = Wall.Create(_doc, Line.CreateBound(new XYZ(100, 100, 0), new XYZ(110, 100, 0)), type.Id, level.Id, 10, 0, false, false).Id; tx.Commit(); }
                    Static("DemoClashExercise", "Correct", _doc);
                    var scan = SceneScan(); Check((bool)Static("DemoClashExercise", "Verify", _doc, scan, true), "Corrected pairs differ."); scan.Dispose();
                    bool refused = false; try { Static("DemoClashExercise", "Correct", _doc); } catch (TargetInvocationException) { refused = true; }
                    Check(refused, "Repeated correction moved the pipe twice.");
                    Static("DemoClashExercise", "Prepare", _doc); Static("DemoClashExercise", "Prepare", _doc);
                    Check(_doc.GetElement(unrelated) != null, "Reset deleted another element.");
                    scan = SceneScan(); Check((bool)Static("DemoClashExercise", "Verify", _doc, scan, false), "Reset duplicates or loses pairs."); scan.Dispose();
                });
                Test("cancelled scans and altered thresholds cannot validate the exercise", () =>
                {
                    var setup = SceneSetup(); var options = SceneOptions();
                    var scan = new SmartScanSession(setup, options) { CancelRequested = true }; scan.Advance();
                    Check(!(bool)Static("DemoClashExercise", "Verify", _doc, scan, false), "Cancellation passed."); scan.Dispose();
                    options.MinimumVolumeMm3 = 11; scan = Scan(setup, options);
                    Check(!(bool)Static("DemoClashExercise", "Verify", _doc, scan, false), "Changed threshold passed."); scan.Dispose();
                });
                Test("tenth discovery card maps to Clash 3D", () =>
                {
                    var discovery = (Window)Activator.CreateInstance(Tutorial("DemoDiscoveryWindow"), true);
                    try { discovery.Show(); discovery.UpdateLayout(); var cards = Descendants(discovery).OfType<Button>().ToList();
                        var card = cards.Single(b => Descendants(b).OfType<TextBlock>().Any(t => t.Text == "10 · Clash 3D"));
                        card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check((string)discovery.GetType().GetProperty("SelectedTour", Hidden).GetValue(discovery) == "clash-3d", "Card 10 maps to card 1."); }
                    finally { discovery.Close(); }
                });
                _ui.ActiveUIDocument.Selection.SetElementIds((List<ElementId>)Static("DemoClashExercise", "Sources", _doc));
                SmartCheckState.SavePreferences(new SmartScanOptions { Pipes = false, GenericModels = true, MinimumVolumeMm3 = 1234, OpenConnectors = true });
                _preferencesSnapshot = File.ReadAllText(Path.Combine(_folder, "clash3d_options.json"));
                _handler = new SmartExternalHandler(_ui); _event = ExternalEvent.Create(_handler);
                _window = new SmartCheckWindow(_event, _handler, SmartScanSetup.Capture(_ui.ActiveUIDocument));
                new System.Windows.Interop.WindowInteropHelper(_window).Owner = _ui.MainWindowHandle;
                _window.Show();
                Test("standalone TUTO has no training correction and is fully explainable", () =>
                {
                    Static("DemoTourService", "StartInWindow", "clash-3d", _window); ShowStep();
                    Check(!Field<bool>(_window, "_tutorialPrepared"), "Ordinary TUTO enabled correction.");
                    var steps = (Array)Field<object>(Guide, "_steps");
                    foreach (var step in steps) Check((bool)Field<object>(step, "IsExplanation"), "Standalone guide requires demo data.");
                    Call(Guide, "Close");
                });
                Static("DemoTourService", "StartInWindowCore", "clash-3d", _window, true); ShowStep();
                Test("prepared options are deterministic without overwriting preferences", () =>
                {
                    var options = (SmartScanOptions)Call(_window, "ReadOptions");
                    Check(options.Scope == SmartScanScope.Selection && options.Pipes && options.LocalClashes && options.MinimumVolumeMm3 == 10 &&
                        !options.LinkedClashes && !options.OpenConnectors && !options.WallSupports && !options.Ducts, "Options were not seeded.");
                });
                Test("all tutorial targets exist and guide reserves a separate column", () =>
                {
                    foreach (var step in (Array)Field<object>(Guide, "_steps")) Check(_window.FindName((string)Field<object>(step, "Target")) != null, "Missing target.");
                    var card = Field<Border>(Guide, "_card"); Check(Grid.GetColumn(card) == 1, "Guide overlays controls.");
                    Render("tutorial-intro.png", 1180, 760); Render("tutorial-minimum.png", 1100, 560);
                    var panel = (FrameworkElement)_window.FindName("ScopePanel"); var point = panel.TranslatePoint(new System.Windows.Point(panel.ActualWidth, 0), _window);
                    var guidePoint = card.TranslatePoint(new System.Windows.Point(0, 0), _window);
                    Check(point.X <= guidePoint.X, "Guide covers scope controls.");
                });
                Next(); Next(); Next();
                Check(!Field<Button>(Guide, "_next").IsEnabled, "Scan step can be skipped.");
                _handler.Completed += Completed;
                _handler.Failed += error => { Test("native action succeeds", () => { throw new Exception(error); }); Schedule(Cleanup); };
                _stage = 1; Call(_window, "Analyze_Click", null, new RoutedEventArgs());
                Check(!Field<Button>(Guide, "_next").IsEnabled, "Analyze click passed before completion.");
            }
            catch (Exception ex) { Test("initialization", () => { throw ex; }); Cleanup(); }
        }
        private void Completed()
        {
            if (_stage == 1 && _handler.Action == SmartAction.ScanBatch && _handler.Session.Complete) { _stage = 2; Schedule(FirstScan); }
            else if (_stage == 3 && _handler.Action == SmartAction.FocusApply) { _stage = 4; Schedule(Focused); }
            else if (_stage == 5 && _handler.Action == SmartAction.TutorialCorrect) { _stage = 6; Schedule(Corrected); }
            else if (_stage == 7 && _handler.Action == SmartAction.ScanBatch && _handler.Session.Complete) { _stage = 8; Schedule(Rescanned); }
        }
        private void FirstScan()
        {
            Test("real async analysis validates only after both expected pairs exist", () =>
            { Check(Field<Button>(Guide, "_next").IsEnabled && _handler.Session.Issues.Count == 2, "First scan not verified."); });
            Next();
            Test("detail opens the selected crossing and validates its own step", () =>
            {
                var selected = ((ListBox)_window.FindName("ResultsList")).SelectedItem as ModelIssue;
                Check((bool)Static("DemoClashExercise", "IsCrossing", _doc, selected), "Wrong pair selected.");
                Check(!Field<Button>(Guide, "_next").IsEnabled, "Detail step already satisfied.");
                Call(_window, "TutorialInspect_Click", null, new RoutedEventArgs());
                Check(Field<Button>(Guide, "_next").IsEnabled && Field<Window>(_window, "_inspector").IsVisible, "Detail was not observed.");
                Field<Window>(_window, "_inspector").Close(); Render("tutorial-detail.png", 1180, 760);
            });
            Next(); _stage = 3; Call(_window, "TutorialFocus_Click", null, new RoutedEventArgs());
        }
        private void Focused()
        {
            Test("real external event focuses before the observation card validates", () =>
            {
                Check(_ui.ActiveUIDocument.ActiveView.Name == SmartClashCommand.Smart3DName, "Wrong Revit view.");
                Check(!_window.IsVisible && !Field<Button>(Guide, "_next").IsEnabled, "Observation completed without confirmation.");
                var card = Field<Window>(_window, "_tutorialObservation");
                Descendants(card).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(_window.IsVisible && Field<Button>(Guide, "_next").IsEnabled, "Observation card did not resume.");
            });
            Next(); Render("tutorial-correction.png", 1100, 560);
            _stage = 5; Call(_window, "TutorialFix_Click", null, new RoutedEventArgs());
        }
        private void Corrected()
        {
            Test("real correction commits once and marks the previous results stale", () =>
            { Check(Field<bool>(_window, "_tutorialCorrected") && Field<bool>(_window, "_stale") && Field<Button>(Guide, "_next").IsEnabled, "Correction not committed."); });
            Test("previous and next revisit correction without demanding a second move", () =>
            {
                Field<Button>(Guide, "_previous").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ShowStep();
                Next(); Check(Field<Button>(Guide, "_next").IsEnabled && !((Button)_window.FindName("TutorialFixButton")).IsEnabled, "Revisited correction is blocked or repeatable.");
            });
            // Reproduce the learner's live selection after focusing the crossing.
            _ui.ActiveUIDocument.Selection.SetElementIds(new List<ElementId>
                { ((List<ElementId>)Static("DemoClashExercise", "Sources", _doc))[0] });
            Next(); _stage = 7; Call(_window, "Analyze_Click", null, new RoutedEventArgs());
        }
        private void Rescanned()
        {
            Test("rescan removes the crossing but retains the wall intersection", () =>
            { Check(Field<SmartScanSetup>(_window, "_setup").Selection.Count == 3, "Training lost its three-pipe scope after focusing.");
                Check(_ui.ActiveUIDocument.Selection.GetElementIds().Count == 1, "Training changed the user's live selection.");
                Check((bool)Static("DemoClashExercise", "Verify", _doc, _handler.Session, true) && Field<Button>(Guide, "_next").IsEnabled, "Wrong corrected scan.");
                Check(File.ReadAllText(Path.Combine(_folder, "clash3d_options.json")) == _preferencesSnapshot, "Training overwrote preferences."); });
            Next();
            Test("exports validate after actual writes and honor displayed filters", () =>
            {
                Check(!Field<Button>(Guide, "_next").IsEnabled, "Export step can be skipped.");
                var search = (TextBox)_window.FindName("SearchBox"); search.Text = "no-match-at-all";
                Call(_window, "WriteExport", Path.Combine(_folder, "empty.csv"), true);
                Check(!Field<Button>(Guide, "_next").IsEnabled, "Empty filtered export passed tutorial.");
                search.Clear();
                bool failed = false; try { Call(_window, "WriteExport", Path.Combine(_folder, "missing-folder", "report.html"), false); } catch (TargetInvocationException) { failed = true; }
                Check(failed && !Field<Button>(Guide, "_next").IsEnabled, "Failed write passed tutorial.");
                Call(_window, "WriteExport", Path.Combine(_folder, "tutorial-report.html"), false);
                Call(_window, "WriteExport", Path.Combine(_folder, "tutorial-report.csv"), true);
                Check(Field<Button>(Guide, "_next").IsEnabled && File.ReadAllText(Path.Combine(_folder, "tutorial-report.html")).Contains("svg"), "Successful report not validated.");
                Render("tutorial-export.png", 1180, 760);
            });
            Test("leaving the tutorial removes its column and correction controls", () =>
            {
                Call(Guide, "Close");
                Check(!Field<bool>(_window, "_tutorialPrepared") && ((Button)_window.FindName("TutorialFixButton")).Visibility == System.Windows.Visibility.Collapsed,
                    "Tutorial correction remains accessible.");
            });
            Cleanup();
        }
        private void Cleanup()
        {
            _stage = 99; _application.Idling -= Watchdog;
            if (_window != null && _window.IsVisible)
            {
                _window.Closed += (s, e) => Schedule(Finish);
                _window.Close();
            }
            else Finish();
        }
        private void Finish()
        {
            Test("modeless close restores the training view in API context", () =>
            { if (_doc != null) Check(_ui.ActiveUIDocument.ActiveView.Name == "BIMaestro - 10 Clash 3D", "Original training view not restored."); });
            if (_doc != null && _doc.IsValidObject)
            {
                _doc.Save(); var empty = _ui.Application.NewProjectDocument(UnitSystem.Metric);
                string path = Path.Combine(_folder, "empty.rvt"); empty.SaveAs(path); empty.Close(false);
                _ui.OpenAndActivateDocument(path); _doc.Close(false);
            }
            Report(); File.WriteAllText(Path.Combine(_folder, "finished.txt"), _failures.ToString());
            _ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
        }
        private void Next() { Field<Button>(Guide, "_next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ShowStep(); }
        private void ShowStep() { Call(Guide, "ShowStep"); _window.UpdateLayout(); }
        private SmartScanSetup SceneSetup() => new SmartScanSetup { Document = _doc, DocumentKey = SmartCheckState.GetDocKey(_doc),
            Selection = (List<ElementId>)Static("DemoClashExercise", "Sources", _doc) };
        private static SmartScanOptions SceneOptions() => new SmartScanOptions { Scope = SmartScanScope.Selection, Pipes = true, LocalClashes = true,
            Ducts = false, CableTrays = false, Conduits = false, Fittings = false, LinkedClashes = false, IncludeInsulation = false, MinimumVolumeMm3 = 10 };
        private SmartScanSession SceneScan()
        {
            var scan = Scan(SceneSetup(), SceneOptions());
            File.WriteAllText(Path.Combine(_folder, "scene-scan-" + (++_scanNumber) + ".json"), JsonConvert.SerializeObject(new
            {
                scan.SourceCount, scan.CandidateCount, scan.Diagnostics,
                issues = scan.Issues.Select(i => new { kind = i.Kind.ToString(), i.IsApproximate, i.IntersectionVolumeMm3,
                    source = _doc.GetElement(i.ElementId)?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                    obstacle = _doc.GetElement(i.RelatedId)?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() })
            }, Formatting.Indented));
            return scan;
        }
        private static SmartScanSession Scan(SmartScanSetup setup, SmartScanOptions options)
        {
            var session = new SmartScanSession(setup, options);
            for (int i = 0; i < 1000 && !session.Complete; i++) session.Advance();
            Check(session.Complete && !session.Cancelled && session.Error == null, session.Error ?? "Scan incomplete."); return session;
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null) yield break;
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        private void Render(string name, double width, double height)
        {
            _window.Width = width; _window.Height = height; _window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(_window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(_folder, name))) encoder.Save(stream);
        }
    }
}

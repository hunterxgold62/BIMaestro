using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace BIMaestro.MotorTests
{
    // Hook from an existing Revit API context, against an already-open test copy:
    // MaintenanceDemoNativeChecks.Run(productionAssembly, doc, app, folder);
    // This harness neither opens Revit nor creates/modifies model elements.
    internal static class MaintenanceDemoNativeChecks
    {
        private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static object Get(object target, string name)
            => target.GetType().GetField(name, Hidden)?.GetValue(target);
        private static void Set(object target, string name, object value)
            => target.GetType().GetField(name, Hidden).SetValue(target, value);
        private static object Call(object target, string name, params object[] values)
            => target.GetType().GetMethod(name, Hidden).Invoke(target, values);
        private static object Property(object target, string name)
            => target.GetType().GetProperty(name, Hidden)?.GetValue(target);
        private static object[] Items(object target, string name)
            => Get(target, name) is IEnumerable items ? items.Cast<object>().ToArray() : Array.Empty<object>();
        private static string Role(object value) => Get(value, "Role")?.ToString() ?? "";
        private static double[] Vector(XYZ point) => point == null ? null : new[] { point.X, point.Y, point.Z };

        internal static void Run(Assembly assembly, Document doc, UIApplication app, string folder)
        {
            if (assembly == null || doc == null || app == null) throw new ArgumentNullException("Assembly, document and UIApplication are required.");
            Directory.CreateDirectory(folder);
            var checks = new List<object>();
            var failures = new List<string>();
            var fixtures = new List<Dictionary<string, object>>();
            var report = new Dictionary<string, object>
            {
                ["test"] = "maintenance-demonstration",
                ["assembly"] = assembly.Location,
                ["moduleId"] = assembly.ManifestModule.ModuleVersionId.ToString(),
                ["scope"] = "Native WPF checks on an already-open test copy; no fixture element or document write."
            };
            Type Production(string name) => assembly.GetType("BIMaestro.MepBooster." + name, true);
            void Check(bool passed, string label)
            {
                checks.Add(new { passed, label });
                if (!passed) failures.Add(label);
            }
            try
            {
                var source = FindSource(doc);
                report["source"] = new { id = source.Id.Value, family = source.Symbol.Family.Name, type = source.Name };
                var identity = Production("MaintenanceBuildIdentity");
                string revision = identity.GetField("Revision", Hidden)?.GetValue(null)?.ToString() ?? "";
                string loadedId = assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8).ToUpperInvariant();
                report["revision"] = revision;
                report["loadedId"] = loadedId;
                bool initialModified = doc.IsModified;

                foreach (string operation in new[] { "LeverValve", "HandwheelValve", "ChainwheelValve", "Actuator", "Filter" })
                {
                    var row = new Dictionary<string, object> { ["operation"] = operation };
                    fixtures.Add(row);
                    Window window = null;
                    object state = null;
                    try
                    {
                        File.WriteAllText(Path.Combine(folder, "demo-stage.txt"), "Opening " + operation);
                        state = Activator.CreateInstance(Production("MaintenanceWorkbenchState"), true);
                        Set(state, "Operation", Enum.Parse(Production("MaintenanceAccessOperation"), operation));
                        // Intentionally do not call Suggest: this is the fallback
                        // path with neither an accepted mechanism nor real feet.
                        Set(state, "CommandPoint", null);
                        Set(state, "FootPoint", null);
                        window = (Window)Activator.CreateInstance(Production("MaintenanceWorkbenchWindow"), Hidden, null,
                            new object[] { doc, source, state, app.MainWindowHandle }, null);
                        var currentWindow = window;
                        var currentState = state;
                        currentWindow.Loaded += (_, __) => currentWindow.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                        {
                            try
                            {
                                await Task.Delay(80);
                                var mode = (ComboBox)Get(currentWindow, "_mode");
                                var phase = (Slider)Get(currentWindow, "_phase");
                                var volumes = (CheckBox)Get(currentWindow, "_showVolumes");
                                var analyze = (Button)Get(currentWindow, "_analyze");
                                var preview = Get(currentWindow, "_preview");
                                string prefix = operation + ": ";
                                row["title"] = currentWindow.Title;
                                row["initialMode"] = mode.SelectedItem?.ToString();
                                Check(mode.SelectedIndex == 1, prefix + "no command or feet defaults to demonstration");
                                Check(Get(currentState, "CommandPoint") == null && Get(currentState, "FootPoint") == null,
                                    prefix + "window construction does not invent real command or feet");
                                Check(!string.IsNullOrWhiteSpace(revision) && currentWindow.Title.Contains(revision)
                                    && currentWindow.Title.Contains(loadedId), prefix + "title identifies revision and loaded production module");
                                var expanders = Descendants(currentWindow).OfType<Expander>().ToArray();
                                var dimensions = expanders.FirstOrDefault(e => e.Header?.ToString()?.StartsWith("Cotes du geste", StringComparison.Ordinal) == true);
                                Check(dimensions != null && !dimensions.IsExpanded, prefix + "gesture dimensions start collapsed");
                                Check(expanders.Where(e => e.Header?.ToString()?.StartsWith("Réglages du gabarit", StringComparison.Ordinal) == true)
                                    .Any(e => !e.IsExpanded), prefix + "operator and part settings start collapsed");

                                // Continue testing the guard even if the initial
                                // mode check failed, without allowing a real scan.
                                mode.SelectedIndex = 1;
                                volumes.IsChecked = false;
                                phase.Value = 0;
                                Call(currentWindow, "Draw");
                                await Task.Delay(50);
                                var scene = Get(currentWindow, "_lastScene");
                                var poseProfile = Get(currentWindow, "_poseProfile");
                                Check(scene != null && HasAvatar(scene), prefix + "demonstration contains torso, head, arms and legs");
                                Check(PreviewHasAvatar(preview), prefix + "avatar reaches actual preview geometry");
                                var demoFoot = Get(poseProfile, "OperatorOrigin") as XYZ;
                                Check(demoFoot != null && Math.Abs(demoFoot.Z) < 1e-8,
                                    prefix + "demonstration foot is on synthetic zero-height floor");
                                Check(Get(currentState, "FootPoint") == null && !(bool)Get(currentState, "SupportVerified"),
                                    prefix + "synthetic foot is separate from real placement state");
                                Check(VisualContent(preview, "_selectedVisual") == null && VisualContent(preview, "_contextVisual") == null,
                                    prefix + "real equipment and context are hidden in demonstration");
                                string note = Property(preview, "CaptureNote")?.ToString() ?? "";
                                Check(note.Contains("Démonstration") && note.Contains("aucun poste dans la maquette validé"),
                                    prefix + "permanent preview note excludes real placement validation");
                                row["demonstrationNote"] = note;
                                row["syntheticFoot"] = Vector(demoFoot);
                                int boxesWithoutVolumes = PreviewBoxCount(preview);
                                Check(HasSyntheticFloor(preview), prefix + "illustrative floor reaches preview");
                                double[] beginning = PoseFingerprint(scene);
                                if (CaptureOperation(operation)) SaveImage(currentWindow, Path.Combine(folder, "demo-" + operation + "-start.png"));

                                phase.Value = 50;
                                Call(currentWindow, "Draw");
                                await Task.Delay(70);
                                var middleScene = Get(currentWindow, "_lastScene");
                                double[] middle = PoseFingerprint(middleScene);
                                Check(Changed(beginning, middle), prefix + "halfway phase changes mechanism or operating hand geometry");
                                Check(HasAvatar(middleScene) && PreviewHasAvatar(preview), prefix + "avatar remains visible during the movement");
                                if (CaptureOperation(operation)) SaveImage(currentWindow, Path.Combine(folder, "demo-" + operation + "-middle.png"));

                                volumes.IsChecked = true;
                                Call(currentWindow, "Draw");
                                int boxesWithVolumes = PreviewBoxCount(preview);
                                Check(boxesWithVolumes > boxesWithoutVolumes, prefix + "show volumes adds visible preview envelopes");
                                Check(PreviewBoxSlots(preview).Any(b => (double)Get(b, "Opacity") > 0 && (double)Get(b, "Opacity") < .1),
                                    prefix + "controlled envelopes include translucent faces");
                                volumes.IsChecked = false;
                                Call(currentWindow, "Draw");
                                Check(PreviewBoxCount(preview) == boxesWithoutVolumes, prefix + "hide volumes restores the readable preview");
                                row["boxesWithoutVolumes"] = boxesWithoutVolumes;
                                row["boxesWithVolumes"] = boxesWithVolumes;
                                row["animation"] = await MaintenanceCaseDiagnostics.MeasureAnimation(assembly,currentWindow,
                                    (passed,label)=>Check(passed,prefix+label),CaptureOperation(operation)?Path.Combine(folder,"demo-"+operation+"-75percent.png"):null);

                                Check(!analyze.IsEnabled, prefix + "real analysis button is disabled in demonstration");
                                // Invoke directly as well as checking the button:
                                // UI disablement alone must not protect the scanner.
                                Call(currentWindow, "Analyze");
                                Check(Get(currentWindow, "_report") == null, prefix + "Analyze in demonstration never produces a collision report");
                                Check(Get(currentState, "CommandPoint") == null && Get(currentState, "FootPoint") == null
                                    && !(bool)Get(currentState, "SupportVerified"), prefix + "Analyze in demonstration never validates real placement");
                                row["demoAnalyzeStatus"] = ((TextBlock)Get(currentWindow, "_status")).Text;

                                mode.SelectedIndex = 0;
                                Call(currentWindow, "Draw");
                                await Task.Delay(60);
                                Check(Get(currentWindow, "_poseProfile") == null && Get(currentWindow, "_controlScene") == null,
                                    prefix + "switching back to unconfigured model clears synthetic profile caches");
                                Check(Get(currentWindow,"_preparedMotion")==null,prefix+"switching to an unconfigured model clears the prepared IK path");
                                Check(Get(currentWindow, "_lastScene") == null, prefix + "unconfigured model has no synthetic operating scene");
                                Check(PreviewBoxCount(preview) == 0 && Items(preview, "_lineSlots").Length == 0,
                                    prefix + "unconfigured model contains no synthetic avatar, floor or action envelopes");
                                Check(VisualContent(preview, "_selectedVisual") != null && VisualContent(preview, "_contextVisual") != null,
                                    prefix + "model equipment and context are restored");
                                Check(!analyze.IsEnabled && Get(currentWindow, "_report") == null && Get(currentState, "FootPoint") == null,
                                    prefix + "model mode still requires genuine command and foot placement");
                                volumes.IsChecked = true;
                                Call(currentWindow, "Draw");
                                Check(PreviewBoxCount(preview) == 0, prefix + "volumes toggle cannot reveal synthetic envelopes in unconfigured model");
                                if (CaptureOperation(operation)) SaveImage(currentWindow, Path.Combine(folder, "demo-" + operation + "-model.png"));
                                row["modelNote"] = Property(preview, "CaptureNote")?.ToString();
                                row["previewTiming"] = Property(preview, "PreviewTiming")?.ToString();
                            }
                            catch (Exception ex)
                            {
                                row["error"] = ex.ToString();
                                failures.Add(operation + ": " + ex);
                            }
                            finally { currentWindow.Close(); }
                        }));
                        currentWindow.ShowDialog();
                        Check(Get(state, "FootPoint") == null && Get(state, "CommandPoint") == null,
                            operation + ": closing demonstration does not persist synthetic placement");
                    }
                    catch (Exception ex)
                    {
                        row["error"] = ex.ToString();
                        failures.Add(operation + ": " + ex);
                    }
                    finally { if (window?.IsVisible == true) window.Close(); }
                }
                Check(doc.IsModified == initialModified, "demonstration UI checks leave document modified state unchanged");
            }
            catch (Exception ex) { report["error"] = ex.ToString(); failures.Add(ex.ToString()); }
            finally
            {
                report["passed"] = failures.Count == 0;
                report["checks"] = checks;
                report["fixtures"] = fixtures;
                report["failures"] = failures;
                File.WriteAllText(Path.Combine(folder, "demonstration-maintenance-report.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(folder, "demo-stage.txt"), failures.Count == 0 ? "done" : "failed");
            }
        }

        private static FamilyInstance FindSource(Document doc)
        {
            foreach (long id in new[] { 1014886L, 649237L, 953028L, 958173L, 950354L, 983094L })
                if (doc.GetElement(new ElementId(id)) is FamilyInstance source) return source;
            return new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .FirstOrDefault(e => e.Category != null && (e.Category.Id.Value == (long)BuiltInCategory.OST_PipeAccessory
                    || e.Category.Id.Value == (long)BuiltInCategory.OST_MechanicalEquipment))
                ?? throw new InvalidOperationException("No existing family available for the demonstration UI snapshot.");
        }
        private static bool CaptureOperation(string operation) => operation == "Filter" || operation == "ChainwheelValve";
        private static object[] PreviewBoxSlots(object preview) => Items(preview, "_boxSlots");
        private static int PreviewBoxCount(object preview) => PreviewBoxSlots(preview).Length;
        private static Model3D VisualContent(object preview, string name) => (Get(preview, name) as ModelVisual3D)?.Content;
        private static bool PreviewHasAvatar(object preview)
            => PreviewBoxSlots(preview).Any(b => Get(b, "Shape")?.ToString() == "head")
                && PreviewBoxSlots(preview).Any(b => Get(b, "Shape")?.ToString() == "torso")
                && Items(preview, "_lineSlots").Length >= 4;
        private static bool HasSyntheticFloor(object preview)
            => PreviewBoxSlots(preview).Any(b => Get(b, "Color") is System.Windows.Media.Color color
                && color == Colors.SlateGray && Math.Abs((double)Get(b, "Opacity") - .22) < 1e-8);
        private static bool HasAvatar(object scene)
            => scene != null && Items(scene, "Boxes").Any(b => Role(b) == "HumanBody" && !(bool)Get(b, "CheckCollision"))
                && Items(scene, "Lines").Count(l => Role(l) == "HumanArm") >= 2
                && Items(scene, "Lines").Count(l => Role(l) == "HumanBody") >= 2;
        private static double[] PoseFingerprint(object scene)
        {
            if (scene == null) return Array.Empty<double>();
            var numbers = new List<double>();
            foreach (var line in Items(scene, "Lines").Where(l => Role(l) == "Handle" || Role(l) == "Chain" || Role(l) == "HumanArm"))
                if (Get(line, "Points") is XYZ[] points) foreach (var point in points) numbers.AddRange(Vector(point));
            foreach (var box in Items(scene, "Boxes").Where(b => Role(b) == "EquipmentGhost" || Role(b) == "Handle"))
                numbers.AddRange(Vector(((Autodesk.Revit.DB.Transform)Get(box, "Frame")).OfPoint((XYZ)Get(box, "Center"))));
            return numbers.ToArray();
        }
        private static bool Changed(double[] a, double[] b)
            => a.Length != b.Length || a.Where((value, index) => Math.Abs(value - b[index]) > 1e-7).Any();
        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            yield return parent;
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                foreach (var descendant in Descendants(child)) yield return descendant;
        }
        private static void SaveImage(Window window, string path)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) png.Save(file);
        }
    }
}

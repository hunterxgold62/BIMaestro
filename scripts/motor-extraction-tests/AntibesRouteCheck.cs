using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BIMaestro.MotorTests
{
    public sealed class AntibesRouteCheck : IExternalApplication
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private bool _done;
        private string _folder;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private void Stage(string stage) => File.WriteAllText(Path.Combine(_folder, "stage.txt"), _watch.Elapsed + " " + stage);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Hidden).SetValue(obj, value);
        private static object Field(object obj, string name) => obj.GetType().GetField(name, Hidden).GetValue(obj);
        public Result OnStartup(UIControlledApplication app)
        {
            if (Environment.GetEnvironmentVariable("BIMAESTRO_ANTIBES_ROUTE_CHECK") != "1") return Result.Succeeded;
            _folder = Path.GetDirectoryName(GetType().Assembly.Location);
            app.Idling += Run; Stage("loaded"); return Result.Succeeded;
        }
        public Result OnShutdown(UIControlledApplication app) { app.Idling -= Run; return Result.Succeeded; }
        private void Run(object sender, IdlingEventArgs args)
        {
            if (_done) return; _done = true;
            var app = (UIApplication)sender;
            Document doc = null;
            Window window = null;
            var output = new Dictionary<string, object>();
            try
            {
                Stage("opening-copy");
                doc = app.Application.OpenDocumentFile(Environment.GetEnvironmentVariable("BIMAESTRO_ANTIBES_COPY"));
                if(Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_MODE")=="valve955038")
                {
                    var production=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="BIMaestro");
                    Stage("valve955038-diagnostics");MaintenanceValve955038Checks.Run(production,doc,app,_folder);
                    Stage("valve955038-done");return;
                }
                if(Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_MODE")=="generic"||Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_MODE")=="generic-only")
                {
                    var production=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="BIMaestro");
                    const string memoryOverride="BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH";
                    string previousMemoryPath=Environment.GetEnvironmentVariable(memoryOverride);
                    try
                    {
                        // The complete generic run, including old-case regressions,
                        // must never read or overwrite the user's real profiles.
                        Environment.SetEnvironmentVariable(memoryOverride,Path.Combine(_folder,"correction-memory","corrections-v1.json"));
                        Stage("generic-lever-and-memory");MaintenanceGenericNativeChecks.Run(production,doc,app,_folder);
                        if(Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_MODE")=="generic")
                        {Stage("generic-five-case-regression");MaintenanceCaseDiagnostics.Run(production,doc,_folder,app);}
                        Stage("generic-done");return;
                    }
                    finally {Environment.SetEnvironmentVariable(memoryOverride,previousMemoryPath);}
                }
                if(Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_MODE")=="cases")
                { Stage("case-diagnostics");MaintenanceCaseDiagnostics.Run(AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="BIMaestro"),doc,_folder,app);Stage("cases-done");return; }
                if(Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_MODE")=="inspect")
                {
                    string requested=Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_INSPECTION_IDS");
                    long[] ids=string.IsNullOrWhiteSpace(requested)?null:requested.Split(new[] {',',';',' '},StringSplitOptions.RemoveEmptyEntries)
                        .Select(value=>long.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out long id)&&id>0
                            ?id:throw new ArgumentException("Invalid inspection element ID: "+value)).Distinct().ToArray();
                    Stage("feature-inspection");MaintenanceFeatureInspection.Run(doc,_folder,ids,ids==null?null:app);
                    if(ids==null){Stage("door-inspection");MotorRouteDoorInspection.Run(doc,_folder);}
                    Stage("inspection-done");return;
                }
                if(Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_MODE")=="release")
                {
                    var production=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="BIMaestro");
                    Stage("case-diagnostics");MaintenanceCaseDiagnostics.Run(production,doc,_folder,app);
                    Stage("routes-multiportes");MotorRouteNativeCases.Run(production,doc,app,_folder);
                    Stage("placement-fixtures");MaintenancePlacementNativeChecks.Run(production,doc,(FamilyInstance)doc.GetElement(new ElementId(809675L)),_folder);
                    Stage("demonstration-fixtures");MaintenanceDemoNativeChecks.Run(production,doc,app,_folder);
                    Stage("automatic-maintenance");MaintenanceAutomaticNativeChecks.Run(production,doc,app,_folder);
                    Stage("native-sweeps");typeof(NativeGeometry).GetMethod("Run",Hidden).Invoke(new NativeGeometry(),new object[] {app,args});
                    Stage("native-doors");typeof(NativeDoorGeometry).GetMethod("Run",Hidden).Invoke(new NativeDoorGeometry(),new object[] {app,args});
                    Stage("release-done");return;
                }
                if(Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_MODE")=="auto")
                {
                    var production=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="BIMaestro");
                    Stage("placement-fixtures");MaintenancePlacementNativeChecks.Run(production,doc,(FamilyInstance)doc.GetElement(new ElementId(809675L)),_folder);
                    Stage("automatic-maintenance");MaintenanceAutomaticNativeChecks.Run(production,doc,app,_folder);Stage("automatic-done");return;
                }
                var pump = (FamilyInstance)doc.GetElement(new ElementId(809675L));
                var door = (FamilyInstance)doc.GetElement(new ElementId(923915L));
                output["pump"] = new { name = pump.Name, family = pump.Symbol.Family.Name };
                output["door"] = new { name = door.Name, family = door.Symbol.Family.Name };
                var ass = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "BIMaestro");
                output["assembly"] = ass.Location;
                var state = Activator.CreateInstance(ass.GetType("BIMaestro.MepBooster.MotorExtractionState", true), true);
                var location = ((LocationPoint)door.Location).Point;
                Set(state, "DoorPoint", location); Set(state, "DoorNormal", door.GetTransform().BasisY.Normalize());
                Set(state, "DoorId", door.Id); Set(state, "DoorLinkId", ElementId.InvalidElementId); Set(state, "DoorName", door.Name);
                Set(state, "DoorWidth", door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? door.Symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0);
                Set(state, "DoorHeight", door.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? door.Symbol.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble() ?? 0);
                Set(state, "DoorSill", location.Z);
                var projection = Activator.CreateInstance(ass.GetType("BIMaestro.MepBooster.BoosterProjection", true), true);
                var pumpCenter = pump.GetTransform().Origin;
                Set(projection, "Left", 500); Set(projection, "Top", 200); Set(projection, "Right", 1400); Set(projection, "Bottom", 1100);
                Set(projection, "Center", (pumpCenter + location) / 2); Set(projection, "Up", XYZ.BasisY); Set(projection, "RightAxis", XYZ.BasisX);
                Set(projection, "Width", 80.0); Set(projection, "Height", 80.0);
                var type = ass.GetType("BIMaestro.MepBooster.MotorExtractionWindow", true);
                window = (Window)Activator.CreateInstance(type, Hidden, null,
                    new object[] { new UIDocument(doc), pump, projection, app.MainWindowHandle, state }, null);
                window.Loaded += (_, __) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        output["fields"] = ((IDictionary)Field(window, "_fields")).Keys.Cast<string>()
                            .ToDictionary(k => k, k => ((System.Windows.Controls.TextBox)((IDictionary)Field(window, "_fields"))[k]).Text);
                        Stage("searching"); var search = Stopwatch.StartNew();
                        type.GetMethod("FindRoute", Hidden).Invoke(window, null);
                        output["seconds"] = search.Elapsed.TotalSeconds;
                        output["status"] = ((TextBlock)Field(window, "_status")).Text;
                        output["error_details"] = window.GetType().GetField("_lastError",Hidden)?.GetValue(window);
                        var route = Field(window, "_route") as IEnumerable;
                        output["route"] = route == null ? null : route.Cast<object>().Select(p =>
                        {
                            var c = (XYZ)Field(p, "Center"); return new { center = new[] { c.X, c.Y, c.Z }, yaw = (double)Field(p, "Yaw") };
                        }).ToList();
                        window.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(Path.Combine(_folder, "window.png"))) png.Save(file);
                        Stage("done");
                    }
                    catch (Exception ex) { output["error"] = ex.ToString(); Stage("error"); }
                    finally { window.Close(); }
                }));
                window.ShowDialog();
                MaintenanceNativeChecks.Run(ass, doc, app, _folder);
                typeof(NativeGeometry).GetMethod("Run", Hidden).Invoke(new NativeGeometry(), new object[] { app, args });
                typeof(NativeDoorGeometry).GetMethod("Run", Hidden).Invoke(new NativeDoorGeometry(), new object[] { app, args });
            }
            catch (Exception ex) { output["error"] = ex.ToString(); Stage("error"); }
            finally
            {
                if (window?.IsVisible == true) window.Close();
                if (doc != null) doc.Close(false);
                File.WriteAllText(Path.Combine(_folder, "route-report.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
                if (app.ActiveUIDocument == null) app.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
            }
        }
    }
}

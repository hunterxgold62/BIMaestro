using System;
using System.Collections;
using System.Collections.Generic;
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
using Transform = Autodesk.Revit.DB.Transform;

namespace BIMaestro.MotorTests
{
    // Executed exclusively by the explicit Antibes test add-in, on the disposable
    // background copy. Synthetic obstacles live in a rolled-back transaction.
    internal static class MaintenanceNativeChecks
    {
        private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden).SetValue(target, value);
        private static object Get(object target, string name) => target.GetType().GetField(name, Hidden).GetValue(target);
        private static object Call(object target, string name, params object[] values) => target.GetType().GetMethod(name, Hidden).Invoke(target, values);
        private static IEnumerable<object> Items(object target, string name) => ((IEnumerable)Get(target, name)).Cast<object>();
        private static Solid Box(XYZ center, XYZ size)
        {
            var min = center - size / 2; var max = center + size / 2;
            var points = new[] { min, new XYZ(max.X,min.Y,min.Z), new XYZ(max.X,max.Y,min.Z), new XYZ(min.X,max.Y,min.Z) };
            var loop = new CurveLoop();
            for(int i=0;i<4;i++) loop.Append(Line.CreateBound(points[i],points[(i+1)%4]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] {loop}, XYZ.BasisZ, size.Z);
        }
        internal static void Run(Assembly assembly, Document doc, UIApplication app, string folder)
        {
            var checks = new List<string>(); var output = new Dictionary<string, object>();
            void Check(bool condition, string label) { if(!condition) throw new Exception(label); checks.Add(label); }
            Type Type(string name) => assembly.GetType("BIMaestro.MepBooster."+name,true);
            object Operation(string name) => Enum.Parse(Type("MaintenanceAccessOperation"),name);
            object Profile(string operation)
            {
                var profile = Type("MaintenanceAccessProfile").GetMethod("For",Hidden).Invoke(null,new[] {Operation(operation)});
                var frame = Transform.Identity; frame.Origin = new XYZ(10000,10000,4);
                Set(profile,"Frame",frame); Set(profile,"OperatorOrigin",new XYZ(10000,9998,0));
                return profile;
            }
            object Scene(object profile, double phase=0) => Type("MaintenanceAccessGeometry").GetMethod("Build",Hidden).Invoke(null,new[] {profile,(object)phase});
            object Scan(object scene, FamilyInstance target) => Type("MaintenanceAccessScanner").GetMethod("Run",Hidden).Invoke(null,new object[] {doc,target,scene,null,null});
            var source = (FamilyInstance)doc.GetElement(new ElementId(809675L));
            try
            {
                foreach(string operation in new[] {"LeverValve","HandwheelValve","ChainwheelValve","Actuator","Filter"})
                {
                    var profile = Profile(operation); var scene = Scene(profile,.5);
                    Check(Items(scene,"Lines").Any(),operation+": human and gesture geometry generated");
                    Check(Items(scene,"Boxes").Any(b=>(bool)Get(b,"CheckCollision")),operation+": check volumes generated");
                    Check(Items(scene,"Boxes").All(b=>((XYZ)Get(b,"Size")).X>0 && ((XYZ)Get(b,"Size")).Y>0 && ((XYZ)Get(b,"Size")).Z>0),operation+": nondegenerate volumes");
                    var report = Scan(scene,source);
                    Check(!Items(report,"Obstacles").Any(),operation+": empty isolated scene is clear");
                    var floorMin = Items(scene,"Boxes").Where(b=>Get(b,"Role").ToString()=="OperatorSpace")
                        .SelectMany(b=>((IEnumerable)Call(b,"Corners")).Cast<XYZ>()).Min(p=>p.Z);
                    Check(floorMin>=-1e-8,operation+": operator clearance does not penetrate floor");
                }
                var removal = Profile("Filter"); Set(removal,"FollowRemoval",false);
                var removalScene = Scene(removal);
                using(var transaction = new Transaction(doc,"Temporary maintenance test obstacles"))
                {
                    transaction.Start();
                    var obstruction = DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));
                    using(var solid = Box(new XYZ(10000,10000,4+100/304.8),new XYZ(.03,.03,.03)))
                        obstruction.SetShape(new GeometryObject[] {solid});
                    doc.Regenerate();
                    var report = Scan(removalScene,source);
                    Check(Items(report,"Obstacles").Any(o=>(long)Get(o,"ElementId")==obstruction.Id.Value),"Filter: thin obstacle in middle of withdrawal detected");
                    transaction.RollBack();
                }
                var sourceBox = source.get_BoundingBox(null);
                var sceneType = Type("MaintenanceAccessScene"); var sourceScene=Activator.CreateInstance(sceneType,true);
                var accessBox=Activator.CreateInstance(Type("MaintenanceAccessBox"),true);
                Set(accessBox,"Role",Enum.Parse(Type("MaintenanceAccessRole"),"OperatorSpace")); Set(accessBox,"Label","Source body check");
                Set(accessBox,"Frame",Transform.Identity); Set(accessBox,"Center",(sourceBox.Min+sourceBox.Max)/2); Set(accessBox,"Size",sourceBox.Max-sourceBox.Min);
                Set(accessBox,"CheckCollision",true); Set(accessBox,"CheckSource",true); ((IList)Get(sourceScene,"Boxes")).Add(accessBox);
                Check(Items(Scan(sourceScene,source),"Obstacles").Any(o=>(long)Get(o,"ElementId")==source.Id.Value),"Operator space includes selected equipment body in collision checks");
                output["inventory"]=new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(e=>e.Category?.Id.Value==(long)BuiltInCategory.OST_PipeAccessory)
                    .GroupBy(e=>e.Symbol.Family.Name+" / "+e.Name).Select(g=>new {name=g.Key,ids=g.Take(4).Select(e=>e.Id.Value).ToArray()}).ToArray();
                output["ui_smoke_scope"]="Repères placés automatiquement pour tester l’interface, pas une validation de maintenance des produits réels.";
                foreach(var test in new[] {new {Operation="HandwheelValve",Id=958173L},new {Operation="ChainwheelValve",Id=953028L},new {Operation="Filter",Id=649237L},new {Operation="Actuator",Id=950354L},new {Operation="LeverValve",Id=983094L}})
                {
                    var target=(FamilyInstance)doc.GetElement(new ElementId(test.Id));
                    Check(target!=null,test.Operation+": actual fixture found in Antibes");
                    var state=Activator.CreateInstance(Type("MaintenanceWorkbenchState"),true);
                    var bounds=target.get_BoundingBox(null); var center=(bounds.Min+bounds.Max)/2;
                    Set(state,"Operation",Operation(test.Operation)); Set(state,"CommandPoint",center);
                    Set(state,"FootPoint",new XYZ(center.X,center.Y-500/304.8,center.Z-1200/304.8));
                    Set(state,"Axis",2);
                    var fields=(IDictionary)Get(state,"Fields");
                    if(test.Operation=="ChainwheelValve") fields["ChainDrop"]="800";
                    Window window=null;
                    try
                    {
                        window=(Window)Activator.CreateInstance(Type("MaintenanceWorkbenchWindow"),Hidden,null,new object[] {doc,target,state,app.MainWindowHandle},null);
                        var captured=window;
                        window.Loaded+=(_,__)=>captured.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
                        {
                            try
                            {
                                Call(captured,"Analyze"); captured.UpdateLayout();
                                if(Get(captured,"_report")==null) throw new Exception("Production UI scan returned no report: "+((TextBlock)Get(captured,"_status")).Text);
                                Check(Items(Get(captured,"_report"),"Obstacles").All(o=>!(doc.GetElement(new ElementId((long)Get(o,"ElementId"))) is SpatialElement)),test.Operation+": rooms/spaces are not physical obstacles");
                                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(captured.ActualWidth),(int)Math.Ceiling(captured.ActualHeight),96,96,PixelFormats.Pbgra32);
                                bitmap.Render(captured);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
                                using(var file=File.Create(Path.Combine(folder,"maintenance-"+test.Operation+".png"))) png.Save(file);
                                output[test.Operation]=new {element=target.Id.Value,name=target.Name,status=((TextBlock)Get(captured,"_status")).Text,
                                    details=((TextBlock)Get(captured,"_details")).Text};
                                ((Slider)Get(captured,"_phase")).Value=50;
                                var animated=new RenderTargetBitmap((int)Math.Ceiling(captured.ActualWidth),(int)Math.Ceiling(captured.ActualHeight),96,96,PixelFormats.Pbgra32);
                                animated.Render(captured);var animatedPng=new PngBitmapEncoder();animatedPng.Frames.Add(BitmapFrame.Create(animated));
                                using(var file=File.Create(Path.Combine(folder,"maintenance-"+test.Operation+"-mid.png"))) animatedPng.Save(file);
                                var inputs=(IDictionary)Get(captured,"_fields");
                                ((System.Windows.Controls.TextBox)inputs["Radius"]).Text="201";
                                Check(Get(captured,"_report")==null,test.Operation+": editing a dimension invalidates the report");
                            }
                            catch(Exception ex){output[test.Operation]=new {error=ex.ToString()};}
                            finally{captured.Close();}
                        }));
                        window.ShowDialog();
                        Check(output.ContainsKey(test.Operation),test.Operation+": production UI and actual Antibes geometry captured");
                    }
                    finally{if(window?.IsVisible==true)window.Close();}
                    Check(!JsonSerializer.Serialize(output[test.Operation]).Contains("\"error\""),test.Operation+": UI scan completed without error");
                }
                output["passed"]=true;
            }
            catch(Exception ex){output["passed"]=false;output["error"]=ex.ToString();}
            output["checks"]=checks;
            File.WriteAllText(Path.Combine(folder,"maintenance-report.json"),JsonSerializer.Serialize(output,new JsonSerializerOptions {WriteIndented=true}));
        }
    }
}

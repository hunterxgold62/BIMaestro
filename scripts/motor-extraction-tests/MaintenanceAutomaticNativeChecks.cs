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
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Transform=Autodesk.Revit.DB.Transform;
using ComboBox=System.Windows.Controls.ComboBox;

namespace BIMaestro.MotorTests
{
    internal static class MaintenanceAutomaticNativeChecks
    {
        private const BindingFlags Hidden=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static void Set(object o,string name,object value)=>o.GetType().GetField(name,Hidden).SetValue(o,value);
        private static object Get(object o,string name)=>o.GetType().GetField(name,Hidden)?.GetValue(o);
        private static object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Hidden).Invoke(o,args);
        private static double[] V(XYZ p)=>p==null?null:new[] {p.X,p.Y,p.Z};
        private static object[] Items(object o,string name)=>((IEnumerable)Get(o,name)).Cast<object>().ToArray();
        private static void SaveImage(Window window,string path)
        {
            window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);
        }
        internal static void Run(Assembly assembly,Document doc,UIApplication app,string folder)
        {
            var checks=new List<string>();var failures=new List<string>();var rows=new List<object>();var output=new Dictionary<string,object>();
            void Check(bool condition,string label){if(!condition)throw new Exception(label);checks.Add(label);}
            Type T(string name)=>assembly.GetType("BIMaestro.MepBooster."+name,true);
            object Op(string name)=>Enum.Parse(T("MaintenanceAccessOperation"),name);
            try
            {
                output["productionAssembly"]=new {location=assembly.Location,sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)))};
                output["cadImports"]=new[] {567835L,690789L}.Select(id=>InspectImport(doc,assembly,id)).ToArray();
                foreach(var item in new[] {new {Id=1014886L,Operation="Filter",Expected="Filter",Radius=0.0},new {Id=953028L,Operation="HandwheelValve",Expected="ChainwheelValve",Radius=150.0},new {Id=958173L,Operation="HandwheelValve",Expected="HandwheelValve",Radius=155.0},new {Id=649237L,Operation="Filter",Expected="Filter",Radius=0.0},new {Id=950354L,Operation="HandwheelValve",Expected="Actuator",Radius=0.0},new {Id=983094L,Operation="HandwheelValve",Expected="LeverValve",Radius=0.0}})
                {
                    try
                    {
                    File.WriteAllText(Path.Combine(folder,"auto-stage.txt"),"Config "+item.Id);
                    var source=(FamilyInstance)doc.GetElement(new ElementId(item.Id));
                    var state=Activator.CreateInstance(T("MaintenanceWorkbenchState"),true);Set(state,"Operation",Op(item.Operation));
                    var watch=Stopwatch.StartNew();
                    Action<string> progress=text=>File.WriteAllText(Path.Combine(folder,"auto-stage.txt"),item.Id+" "+text);
                    T("MaintenanceAutoconfiguration").GetMethod("Suggest",Hidden).Invoke(null,new object[] {doc,source,state,progress});
                    double configuration=watch.Elapsed.TotalSeconds;
                    var point=Get(state,"CommandPoint")as XYZ;var foot=Get(state,"FootPoint")as XYZ;var frame=Get(state,"DetectedFrame")as Transform;
                    var fields=(IDictionary)Get(state,"Fields");
                    var row=new Dictionary<string,object>{{"element",item.Id},{"family",source.Symbol.Family.Name},{"operation",Get(state,"Operation").ToString()},
                        {"command",V(point)},{"localCommand",point==null?null:V(source.GetTransform().Inverse.OfPoint(point))},{"foot",V(foot)},{"axis",V(frame?.BasisZ)},
                        {"seconds",configuration},{"evidence",Get(state,"DetectionNote")},{"featureWarnings",Items(state,"FeatureWarnings")},{"placementWarnings",Items(state,"PlacementWarnings")},
                        {"support",Get(state,"SupportNote")},{"supportVerified",Get(state,"SupportVerified")},{"fields",fields.Keys.Cast<string>().ToDictionary(k=>k,k=>fields[k])}};
                    rows.Add(row);
                    if(item.Id==983094L&&point==null)
                    {
                        Check(Items(state,"FeatureWarnings").Length>0&&foot==null,item.Id+": unsupported lever geometry leaves a manual fallback without invented posture");
                        continue;
                    }
                    Check(point!=null&&frame!=null,item.Id+": command and axis proposed from geometry");
                    Check(Get(state,"Operation").ToString()==item.Expected,item.Id+": correct operation classified");
                    var exactProfile=T("MaintenanceAutoconfiguration").GetMethod("ReadSavedProfile",Hidden).Invoke(null,new object[] {source,state});
                    var diagnosticWatch=Stopwatch.StartNew();
                    var placement=T("MaintenanceOperatorPlacement").GetMethod("Suggest",Hidden).Invoke(null,new object[] {doc,source,exactProfile,progress});
                    row["placementDiagnosticSeconds"]=diagnosticWatch.Elapsed.TotalSeconds;
                    row["placementDiagnostics"]=Items(placement,"Diagnostics");
                    row["placementDiagnosticWarnings"]=Items(placement,"Warnings");
                    row["placementDiagnosticAssumptions"]=Items(placement,"Assumptions");
                    row["placementDiagnosticFoot"]=V(Get(placement,"FootPoint")as XYZ);
                    row["placementConfidence"]=Get(placement,"Confidence")?.ToString();
                    row["provisionalUnknown"]=Items(placement,"ProvisionalUnknown");
                    if(item.Id==1014886L)
                    {
                        Check(!(bool)Get(state,"SupportVerified"),item.Id+": incomplete or blocked actual posture cannot claim verified support");
                        if(foot==null)
                            Check(Items(placement,"Diagnostics").Cast<string>().Any(s=>s.StartsWith("Poste occupé par",StringComparison.Ordinal))
                                &&Items(state,"PlacementWarnings").Any(),item.Id+": known physical blockers explain refusal instead of inventing an operator");
                        else
                            Check(Get(placement,"Confidence").ToString()=="LimitedGeometry"&&Items(placement,"ProvisionalUnknown").Any()
                                &&Items(state,"PlacementWarnings").Any(),item.Id+": provisional CAD identity and warnings remain attached to the actual posture");
                    }
                    row["suggestedWorkHeightMm"]=(double)Get(placement,"SuggestedWorkHeight")*304.8;
                    var exactFrame=(Transform)Get(exactProfile,"Frame");
                    row["exactPlacementProfile"]=new {command=V(exactFrame.OfPoint((XYZ)Get(exactProfile,"Center"))),frameX=V(exactFrame.BasisX),frameY=V(exactFrame.BasisY),frameZ=V(exactFrame.BasisZ),
                        operatorHeightMm=(double)Get(exactProfile,"OperatorHeight")*304.8,workHeightMm=(double)Get(exactProfile,"WorkHeight")*304.8,
                        gripMm=(double)Get(exactProfile,"GripClearance")*304.8,pullMm=(double)Get(exactProfile,"ChainPullStroke")*304.8,
                        chainDropMm=(double)Get(exactProfile,"ChainDrop")*304.8,chainSpacingMm=(double)Get(exactProfile,"ChainSpacing")*304.8};
                    if(item.Radius>0)
                    {
                        double radius=double.Parse((string)fields["Radius"],System.Globalization.CultureInfo.CurrentCulture);
                        Check(Math.Abs(radius-item.Radius)<3,item.Id+": wheel radius agrees with family measurement");
                        double rim=fields.Contains("Thickness")?double.Parse((string)fields["Thickness"],System.Globalization.CultureInfo.CurrentCulture):35;
                        Check(Math.Abs(radius+rim/2-(item.Id==953028L?159.5:165))<3,item.Id+": rim centreline and thickness recover the real outside radius");
                        var local=source.GetTransform().Inverse.OfPoint(point);
                        Check(local.Z>.9,item.Id+": wheel is above reducer base, not at whole-family centre");
                    }
                    if(foot!=null)
                    {
                        var profile=T("MaintenanceAutoconfiguration").GetMethod("CreateProfile",Hidden,null,new[] {typeof(FamilyInstance),T("MaintenanceWorkbenchState"),T("MaintenanceAccessOperation"),typeof(int),typeof(Func<string,double>)},null)
                            .Invoke(null,new object[] {source,state,Get(state,"Operation"),(int)Get(state,"Axis"),new Func<string,double>(k=>fields.Contains(k)?double.Parse((string)fields[k],System.Globalization.CultureInfo.CurrentCulture):Default(k))});
                        var validation=T("MaintenanceOperatorPlacement").GetMethod("Validate",Hidden).Invoke(null,new object[] {doc,source,profile,progress});
                        Check(Get(validation,"FootPoint")!=null,item.Id+": proposed foot is on a validated walking surface");
                    }
                    var window=(Window)Activator.CreateInstance(T("MaintenanceWorkbenchWindow"),Hidden,null,new object[] {doc,source,state,app.MainWindowHandle},null);
                    window.Loaded+=(_,__)=>window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async ()=>
                    {
                        try
                        {
                            SaveImage(window,Path.Combine(folder,"auto-"+item.Id+".png"));
                            var mode=Get(window,"_mode")as ComboBox;
                            row["initialPreviewMode"]=mode?.SelectedItem;
                            if(mode!=null){mode.SelectedIndex=0;SaveImage(window,Path.Combine(folder,"model-"+item.Id+".png"));}
                            if(item.Id==1014886L)
                            {
                                var preview=Get(window,"_preview");
                                if(foot!=null)
                                    Check(Items(preview,"_boxSlots").Any(b=>Get(b,"Shape")as string=="torso"&&Get(b,"Color")is System.Windows.Media.Color color&&color==Colors.DarkOrange),
                                        item.Id+": real provisional avatar reaches the WPF preview in amber");
                                else if(Get(state,"RejectedFoot")!=null)
                                    Check(Items(preview,"_boxSlots").Any(b=>Get(b,"Shape")as string=="torso"&&Get(b,"Color")is System.Windows.Media.Color color&&color==Colors.Firebrick)
                                        &&((TextBlock)Get(window,"_modeNote")).Text.Contains("POSTE REFUSÉ"),
                                        item.Id+": actual blocked model shows a red rejected posture, never an accepted operator");
                                else
                                    Check(!Items(preview,"_boxSlots").Any(b=>Get(b,"Shape")as string=="torso"),
                                        item.Id+": no operator is invented without a supported or explicitly rejected position");
                                var withdrawal=(Slider)Get(window,"_phase");withdrawal.Value=0;Call(window,"Draw");
                                object Ghost()=>Items(Get(window,"_lastScene"),"Boxes").First(b=>Get(b,"Role").ToString()=="EquipmentGhost");
                                var start=(XYZ)Get(Ghost(),"Center");withdrawal.Value=50;Call(window,"Draw");
                                Check(((XYZ)Get(Ghost(),"Center")-start).GetLength()>1e-6,
                                    item.Id+": actual model mode moves the withdrawal ghost even without a valid operator position");
                                var volumes=(CheckBox)Get(window,"_showVolumes");
                                int plain=Items(preview,"_boxSlots").Length;volumes.IsChecked=true;Call(window,"Draw");
                                Check(Items(preview,"_boxSlots").Length>plain,item.Id+": show volumes adds actual controlled envelopes");
                                SaveImage(window,Path.Combine(folder,"model-"+item.Id+"-volumes.png"));
                                volumes.IsChecked=false;
                                if(foot!=null)
                                {
                                    ((ComboBox)Get(window,"_provenance")).SelectedIndex=2;
                                    ((CheckBox)Get(window,"_confirmed")).IsChecked=true;
                                }
                            }
                            var phase=(Slider)Get(window,"_phase");var costs=new List<double>();
                            for(int i=0;i<120;i++){watch.Restart();phase.Value=i*100.0/119;costs.Add(watch.Elapsed.TotalMilliseconds);}
                            row["poseMeanMs"]=costs.Average();row["poseP95Ms"]=costs.OrderBy(v=>v).ElementAt(114);
                            row["previewTiming"]=Get(window,"_preview").GetType().GetProperty("PreviewTiming",Hidden)?.GetValue(Get(window,"_preview"));
                            if(foot!=null)
                            {
                                Call(window,"Analyze");row["status"]=((TextBlock)Get(window,"_status")).Text;
                                Check(Get(window,"_report")!=null,item.Id+": production collision analysis completed");
                                if(item.Id==1014886L)
                                    Check(!(bool)Get(state,"SupportVerified")&&Items(state,"PlacementWarnings").Any()
                                        &&!((string)row["status"]).StartsWith("Libre",StringComparison.Ordinal),
                                        item.Id+": confirming repères and provenance cannot erase CAD uncertainty or produce a free result");
                            }
                            for(int i=0;i<60;i++){phase.Value=i*100.0/59;await Task.Delay(33);}
                            row["renderFramesPerSecond"]=Get(window,"_preview").GetType().GetProperty("RenderFramesPerSecond",Hidden)?.GetValue(Get(window,"_preview"));
                            row["sceneUpdatesPerSecond"]=Get(window,"_preview").GetType().GetProperty("SceneUpdatesPerSecond",Hidden)?.GetValue(Get(window,"_preview"));
                            row["renderingTier"]=Get(window,"_preview").GetType().GetProperty("RenderingTier",Hidden)?.GetValue(Get(window,"_preview"));
                            SaveImage(window,Path.Combine(folder,"auto-"+item.Id+"-end.png"));
                            var operation=(System.Windows.Controls.ComboBox)Get(window,"_operation");operation.SelectedIndex=operation.SelectedIndex==0?1:0;
                            Check(Get(state,"CommandPoint")==null&&Get(state,"FootPoint")==null&&Get(state,"DetectedFrame")==null,
                                item.Id+": changing operation clears the previous command, feet and detected axis");
                            Check(!(bool)Get(state,"Measured")&&!(bool)Get(state,"Confirmed")&&!(bool)Get(state,"Manufacturer"),
                                item.Id+": changing operation clears measured and declared provenance");
                        }
                        catch(Exception ex){row["uiError"]=ex.ToString();}
                        finally{window.Close();}
                    }));
                    window.ShowDialog();
                    Check(!row.ContainsKey("uiError"),item.Id+": viewer and animation completed");
                    }
                    catch(Exception ex){failures.Add(item.Id+": "+ex);}
                }
                output["passed"]=failures.Count==0;
            }
            catch(Exception ex){output["passed"]=false;output["error"]=ex.ToString();}
            output["checks"]=checks;output["fixtures"]=rows;
            output["failures"]=failures;
            File.WriteAllText(Path.Combine(folder,"automatic-maintenance-report.json"),JsonSerializer.Serialize(output,new JsonSerializerOptions {WriteIndented=true}));
        }
        private static object InspectImport(Document doc,Assembly assembly,long id)
        {
            var element=doc.GetElement(new ElementId(id));
            if(!(element is ImportInstance import)) return new {id,type=element?.GetType().FullName};
            var types=new Dictionary<string,int>();var errors=new List<string>();
            int primitives=0,points=0,solids=0,meshes=0,other=0;long meshTriangles=0;
            double minZ=double.PositiveInfinity,maxZ=double.NegativeInfinity;
            void Point(XYZ p){points++;minZ=Math.Min(minZ,p.Z);maxZ=Math.Max(maxZ,p.Z);}
            void Read(GeometryElement geometry)
            {
                if(geometry==null){errors.Add("null geometry");return;}
                foreach(var item in geometry)
                {
                    string name=item.GetType().Name;if(!types.ContainsKey(name))types[name]=0;types[name]++;
                    try
                    {
                        if(item is GeometryInstance nested){using(var expanded=nested.GetInstanceGeometry())Read(expanded);}
                        else if(item is Curve curve){primitives++;foreach(var p in curve.Tessellate())Point(p);}
                        else if(item is PolyLine polyline){primitives++;foreach(var p in polyline.GetCoordinates())Point(p);}
                        else if(item is Solid solid){solids++;foreach(Edge edge in solid.Edges)foreach(var p in edge.Tessellate())Point(p);}
                        else if(item is Mesh mesh){meshes++;meshTriangles+=mesh.NumTriangles;foreach(var p in mesh.Vertices)Point(p);}
                        else other++;
                    }
                    catch(Exception ex){errors.Add(ex.GetType().Name+": "+ex.Message);}
                }
            }
            try{using(var options=new Options {DetailLevel=ViewDetailLevel.Fine,IncludeNonVisibleObjects=false})using(var geometry=import.get_Geometry(options))Read(geometry);}
            catch(Exception ex){errors.Add(ex.ToString());}
            bool? production2D=null;
            try{production2D=(bool)assembly.GetType("BIMaestro.MepBooster.MaintenanceOperatorPlacement",true).GetMethod("IsPlanarReferenceImport",Hidden)
                .Invoke(null,new object[] {import,null});}catch(Exception ex){errors.Add("classifier: "+ex);}
            using(var box=import.get_BoundingBox(null))
                return new {id,type=import.GetType().FullName,category=import.Category?.Name,name=import.Name,viewSpecific=import.ViewSpecific,ownerViewId=import.OwnerViewId.Value,
                    types,primitives,points,solids,meshes,meshTriangles,other,zMin=points==0?(double?)null:minZ,zMax=points==0?(double?)null:maxZ,
                    zSpanMm=points==0?(double?)null:(maxZ-minZ)*304.8,bboxZ=box==null?null:new[] {box.Min.Z,box.Max.Z},
                    independentlyHorizontalLinework=primitives>0&&points>0&&solids==0&&meshes==0&&other==0&&errors.Count==0&&(maxZ-minZ)*304.8<1,
                    production2D,errors};
        }
        private static double Default(string key)
        {
            switch(key){case "Radius":return 200;case "Thickness":return 35;case "WorkHeight":return 1200;case "Angle":return 360;case "Height":return 1700;case "Width":case "Depth":return 650;case "Grip":return 70;case "Margin":return 10;case "ChainDrop":return 2000;case "ChainSpacing":return 400;case "Pull":return 350;case "RX":case "RY":return 250;case "RZ":return 500;case "Travel":return 700;case "OffsetZ":return -250;default:return 0;}
        }
    }
}

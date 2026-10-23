using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Transform = Autodesk.Revit.DB.Transform;
using ComboBox = System.Windows.Controls.ComboBox;

namespace BIMaestro.MotorTests
{
    // Explicit native checks against the disposable Antibes copy. All correction
    // files are redirected into this run folder and all fixture elements roll back.
    internal static class MaintenanceGenericNativeChecks
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private const string Override="BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH";
        private static object Get(object value,string name)=>value?.GetType().GetField(name,Hidden)?.GetValue(value)
            ??value?.GetType().GetProperty(name,Hidden)?.GetValue(value);
        private static void Set(object value,string name,object content)=>value.GetType().GetField(name,Hidden).SetValue(value,content);
        private static object[] Items(object value,string name)=>Get(value,name)is IEnumerable values?values.Cast<object>().ToArray():Array.Empty<object>();
        private static double[] V(XYZ value)=>value==null?null:new[]{value.X,value.Y,value.Z};
        private static object Frame(Transform value)=>value==null?null:new{origin=V(value.Origin),x=V(value.BasisX),y=V(value.BasisY),z=V(value.BasisZ),determinant=value.Determinant};
        private static double Mm(double value)=>value/304.8;
        private static object Failure(Exception error)=>error is TargetInvocationException invoked&&invoked.InnerException!=null?invoked.InnerException.ToString():error.ToString();
        private static Dictionary<string,object> Fields(object state)
        {var fields=(IDictionary)Get(state,"Fields");return fields.Keys.Cast<string>().ToDictionary(key=>key,key=>fields[key]);}

        internal static void Run(Assembly assembly,Document document,UIApplication app,string folder)
        {
            Directory.CreateDirectory(folder);
            var checks=new List<object>();var failures=new List<string>();
            var report=new Dictionary<string,object>{["test"]="generic-lever-and-correction-memory",
                ["assembly"]=new{path=assembly.Location,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),moduleId=assembly.ManifestModule.ModuleVersionId},
                ["scope"]="Real Antibes 1016925; user correction storage redirected; fixture edits rolled back; model never saved.",
                ["checks"]=checks,["failures"]=failures};
            string memoryPath=Path.GetFullPath(Path.Combine(folder,"correction-memory","corrections-v1.json"));
            string previous=Environment.GetEnvironmentVariable(Override);
            Type T(string name)=>assembly.GetType("BIMaestro.MepBooster."+name,true);
            object Operation(string name)=>Enum.Parse(T("MaintenanceAccessOperation"),name);
            object State(string operation="LeverValve")
            {var state=Activator.CreateInstance(T("MaintenanceWorkbenchState"),true);Set(state,"Operation",Operation(operation));return state;}
            void Check(bool condition,string message){checks.Add(new{passed=condition,message});if(!condition)failures.Add(message);}
            void Save()=>File.WriteAllText(Path.Combine(folder,"maintenance-generic-report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            void Stage(string text){File.WriteAllText(Path.Combine(folder,"generic-stage.txt"),text);Save();}
            object Detect(FamilyInstance source,string operation)=>T("MaintenanceFeatureDetection").GetMethod("Detect",Hidden).Invoke(null,new[]{(object)document,source,Operation(operation)});
            object DescribeFeature(object feature,FamilyInstance source)
            {
                var point=Get(feature,"CommandPoint")as XYZ;
                return new{operation=Get(feature,"Operation")?.ToString(),confidence=Get(feature,"Confidence")?.ToString(),command=V(point),
                    localCommand=point==null?null:V(source.GetTransform().Inverse.OfPoint(point)),frame=Frame(Get(feature,"Frame")as Transform),
                    radiusMm=Convert.ToDouble(Get(feature,"Radius"))*304.8,thicknessMm=Convert.ToDouble(Get(feature,"HandleThickness"))*304.8,
                    initialAngle=Get(feature,"InitialAngleDegrees"),rotation=Get(feature,"RotationDegrees"),rotationAssumed=Get(feature,"RotationAssumed"),
                    evidence=Items(feature,"Evidence"),warnings=Items(feature,"Warnings"),ambiguous=Get(feature,"Ambiguous")};
            }
            object DescribeState(object state)=>new{operation=Get(state,"Operation")?.ToString(),command=V(Get(state,"CommandPoint")as XYZ),frame=Frame(Get(state,"DetectedFrame")as Transform),
                foot=V(Get(state,"FootPoint")as XYZ),supportVerified=Get(state,"SupportVerified"),confirmed=Get(state,"Confirmed"),measured=Get(state,"Measured"),manufacturer=Get(state,"Manufacturer"),
                correctionApplied=Get(state,"CorrectionApplied"),correctionNote=Get(state,"CorrectionNote"),rotationAssumed=Get(state,"RotationAssumed"),
                wholeGestureChecked=Get(state,"WholeGestureChecked"),wholeGestureClear=Get(state,"WholeGestureClear"),searchLimited=Get(state,"SearchLimited"),
                fields=Fields(state),
                featureWarnings=Items(state,"FeatureWarnings"),placementWarnings=Items(state,"PlacementWarnings"),gestureUnknown=Items(state,"GestureUnknown"),
                gestureObstacles=Items(state,"GestureObstacles").Select(o=>new{id=Get(o,"ElementId"),role=Get(o,"Role")?.ToString(),label=Get(o,"VolumeLabel")}).ToArray(),
                fixedObstacles=Items(state,"FixedObstacles").Select(o=>new{id=Get(o,"ElementId"),role=Get(o,"Role")?.ToString(),label=Get(o,"VolumeLabel")}).ToArray()};
            bool Memory(string name,FamilyInstance source,object stateOrOperation,out string note)
            {
                var args=new[]{(object)document,source,stateOrOperation,null};
                bool result=(bool)T("MaintenanceCorrectionMemory").GetMethod(name,Hidden).Invoke(null,args);note=args[3]as string;return result;
            }
            try
            {
                Environment.SetEnvironmentVariable(Override,memoryPath);
                var source=document.GetElement(new ElementId(1016925L))as FamilyInstance;
                if(source==null)throw new InvalidOperationException("Real Antibes1016925 is missing; no substitute is permitted.");
                report["source"]=new{id=source.Id.Value,uniqueId=source.UniqueId,family=source.Symbol.Family.Name,type=source.Name,frame=Frame(source.GetTransform())};
                report["memoryPath"]=memoryPath;
                Check(!document.IsModifiable,"The generic test starts outside a model transaction");
                Memory("Remove",source,Operation("LeverValve"),out _);
                Memory("Remove",source,Operation("HandwheelValve"),out _);
                object feature=null;
                foreach(string preferred in new[]{"HandwheelValve","LeverValve"})
                {
                    Stage("Detect visible lever from "+preferred);
                    var detected=Detect(source,preferred);report["detection_"+preferred]=DescribeFeature(detected,source);
                    Check(Get(detected,"Operation")?.ToString()=="LeverValve",preferred+" identifies the actual visible lever generically");
                    Check(Get(detected,"HasCommandPoint")is bool hasPoint&&hasPoint&&Get(detected,"HasAxis")is bool hasAxis&&hasAxis,
                        preferred+" produces a measured pivot and operating axis instead of an equipment-centre fallback");
                    if(Get(detected,"Frame")is Transform frame&&Get(detected,"CommandPoint")is XYZ command)
                    {
                        var local=source.GetTransform().Inverse.OfPoint(command);
                        Check(frame.BasisX.DotProduct(source.GetTransform().BasisX)>.995,preferred+" initial frame follows the visible open handle, not the hidden closed alternative");
                        Check(Math.Abs(frame.BasisZ.DotProduct(source.GetTransform().BasisZ))>.995,preferred+" operating axis agrees with the real handle stem");
                        Check(Math.Sqrt(local.X*local.X+local.Y*local.Y)<Mm(5)&&local.Z>Mm(85)&&local.Z<Mm(110),
                            preferred+" pivot lies on the actual elevated stem at the grip height");
                    }
                    double length=source.Symbol.LookupParameter("Long poignée")?.AsDouble()??0;
                    Check(length>0&&Math.Abs(Convert.ToDouble(Get(detected,"Radius"))-length)<Mm(2),preferred+" handle length agrees with the actual family dimension");
                    double width=source.Symbol.LookupParameter("Larg poignée")?.AsDouble()??0;
                    double thickness=Convert.ToDouble(Get(detected,"HandleThickness"));
                    Check(width>0&&thickness>=width-Mm(1)&&thickness<width+Mm(15),preferred+" conservative thickness contains the real bent handle cross-section");
                    Check(Get(detected,"RotationAssumed")is bool assumed&&assumed&&Get(detected,"HasRotation")is bool hasRotation&&hasRotation,
                        preferred+" quarter-turn is explicitly an assumption rather than a measured manufacturer travel");
                    feature=detected;
                }
                Stage("Automatic real-model whole-gesture placement");
                var automatic=State("HandwheelValve");var watch=Stopwatch.StartNew();
                T("MaintenanceAutoconfiguration").GetMethod("Suggest",Hidden).Invoke(null,new object[]{document,source,automatic,(Action<string>)Stage});
                report["autoconfigurationSeconds"]=watch.Elapsed.TotalSeconds;report["automatic"]=DescribeState(automatic);
                Check(Get(automatic,"Operation")?.ToString()=="LeverValve"&&Get(automatic,"CommandPoint")!=null,"Automatic configuration retains the measured lever instead of requesting a manual pivot");
                Check(!(bool)Get(automatic,"Confirmed"),"Automatic lever recognition never confirms its own assumptions");
                var handlePoints=VisibleHandlePoints(source);report["visibleHandleVertexCount"]=handlePoints.Count;
                Check(handlePoints.Count>0,"The real visible plastic handle supplies independent native face vertices");
                if(handlePoints.Count>0)
                {
                    var profile=T("MaintenanceAutoconfiguration").GetMethod("ReadSavedProfile",Hidden).Invoke(null,new object[]{source,automatic});
                    var geometry=T("MaintenanceAccessGeometry").GetMethod("Build",Hidden).Invoke(null,new[]{profile,(object)0.0});
                    var represented=Items(geometry,"Boxes").First(b=>Get(b,"Role")?.ToString()=="Handle"&&Get(b,"Label")as string=="Levier");
                    Check(handlePoints.All(p=>Contains(represented,p,Mm(1))),"The initial represented lever contains the actual visible handle including its bent heel");
                    var fixedVolumes=Items(geometry,"Boxes").Where(b=>Get(b,"CheckCollision")is bool collision&&collision
                        &&Get(b,"DependsOnOperator")is bool dependent&&!dependent).ToArray();
                    var frame=(Transform)Get(profile,"Frame");double rotation=(double)Get(profile,"RotationDegrees");
                    Check(Enumerable.Range(0,33).All(i=>handlePoints.All(p=>fixedVolumes.Any(b=>Contains(b,
                        Transform.CreateRotationAtPoint(frame.BasisZ,rotation*i/32*Math.PI/180,frame.Origin).OfPoint(p),Mm(1))))),
                        "The continuous conservative lever sweep contains real handle vertices through all33 sampled rotations");
                }
                if(Get(automatic,"FootPoint")!=null)
                    Check((bool)Get(automatic,"WholeGestureChecked"),"A proposed real operator has appuis, body, arms and mechanism checked for the whole lever gesture");
                if(Get(automatic,"WholeGestureClear")is bool clear&&clear)
                    Check(Items(automatic,"GestureObstacles").Length==0&&Items(automatic,"FixedObstacles").Length==0,"A clear-known lever gesture retains no known fixed or arm collision");
                Visual(assembly,document,source,automatic,app,folder,report,Check,Save);

                Stage("Persist isolated corrected lever profile");
                var corrected=State();
                Set(corrected,"CommandPoint",(XYZ)Get(automatic,"CommandPoint"));Set(corrected,"DetectedFrame",Get(automatic,"DetectedFrame"));Set(corrected,"Axis",6);
                if(Get(corrected,"CommandPoint")==null||Get(corrected,"DetectedFrame")==null)throw new InvalidOperationException("Measured command/frame required before memory test.");
                var sourceFrame=source.GetTransform();
                var correctedPoint=(XYZ)Get(corrected,"CommandPoint")+sourceFrame.OfVector(new XYZ(Mm(2),-Mm(1),Mm(1)));
                var correctedFrame=(Transform)Get(corrected,"DetectedFrame");
                correctedFrame=Transform.CreateRotationAtPoint(correctedFrame.BasisZ,15*Math.PI/180,correctedPoint).Multiply(correctedFrame);
                correctedFrame.Origin=correctedPoint;Set(corrected,"CommandPoint",correctedPoint);Set(corrected,"DetectedFrame",correctedFrame);
                Set(corrected,"Confirmed",false);Set(corrected,"Measured",true);Set(corrected,"Manufacturer",false);Set(corrected,"Notice","Native fixture: measured user correction; travel hypothesis.");
                Set(corrected,"RotationAssumed",true);((IList)Get(corrected,"FeatureWarnings")).Add("Native fixture: course83deg supposée, à confirmer sur la notice.");
                Set(corrected,"FootPoint",new XYZ(99999,99999,99999));Set(corrected,"SupportVerified",true);Set(corrected,"WholeGestureChecked",true);Set(corrected,"WholeGestureClear",true);
                Set(corrected,"OperatorPosture",Enum.Parse(T("MaintenanceOperatorPosture"),"Crouched"));Set(corrected,"BodyYawDegrees",81.0);
                var correctedFields=(IDictionary)Get(corrected,"Fields");
                correctedFields["Radius"]="173.25";correctedFields["Thickness"]="46";correctedFields["Angle"]="83";correctedFields["InitialAngle"]="7";
                correctedFields["OffsetY"]="12.5";correctedFields["Height"]="1234";
                Check(Memory("Save",source,corrected,out string savedNote),"Explicit saving persists an unconfirmed user correction as a hypothesis in the isolated run file");
                report["savedMemoryNote"]=savedNote;
                Check(File.Exists(memoryPath),"The correction has a real persisted JSON file in the test directory");
                if(File.Exists(memoryPath))report["persistedMemory"]=new{path=memoryPath,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(memoryPath))),json=File.ReadAllText(memoryPath)};
                var localPoint=sourceFrame.Inverse.OfPoint(correctedPoint);
                var localX=sourceFrame.Inverse.OfVector(correctedFrame.BasisX);var localY=sourceFrame.Inverse.OfVector(correctedFrame.BasisY);
                var localZ=sourceFrame.Inverse.OfVector(correctedFrame.BasisZ);
                var baselineProbe=MemoryProbe(assembly,document,source);
                SaveProbe(folder,"memory-baseline",baselineProbe);
                report["memoryBaselineIdentity"]=baselineProbe.TryGetValue("identity",out var baselineIdentity)?baselineIdentity:null;
                void Applied(FamilyInstance target,string key,Transform physicalFrame=null)
                {
                    var state=State();Set(state,"Confirmed",true);Set(state,"FootPoint",new XYZ(88888,88888,88888));Set(state,"SupportVerified",true);
                    Set(state,"WholeGestureChecked",true);Set(state,"WholeGestureClear",true);((IDictionary)Get(state,"Fields"))["Height"]="1900";
                    Set(state,"SupportNote","old physical support must be cleared");
                    foreach(string name in new[]{"GestureUnknown","PlacementWarnings","PlacementDiagnostics"})((IList)Get(state,name)).Add("old validation");
                    foreach(string name in new[]{"BlockingObstacles","GestureObstacles","FixedObstacles"})
                        ((IList)Get(state,name)).Add(Activator.CreateInstance(T("MaintenanceAccessObstacle"),true));
                    bool applied=Memory("TryApply",target,state,out string note);
                    var probe=MemoryProbe(assembly,document,target);string probePath=SaveProbe(folder,key,probe);
                    report[key]=new{applied,note,state=DescribeState(state),frame=Frame(target.GetTransform()),expectedPhysicalFrame=Frame(physicalFrame??target.GetTransform()),
                        mirrored=target.Mirrored,handFlipped=target.HandFlipped,facingFlipped=target.FacingFlipped,
                        identity=probe.TryGetValue("identity",out var identity)?identity:null,
                        descriptorComparison=ProbeDifference(baselineProbe,probe),descriptorFile=probePath};
                    Save();
                    Check(applied,key+": persisted correction applies to identical local geometry");
                    if(!applied)return;
                    var expected=physicalFrame??target.GetTransform();var command=Get(state,"CommandPoint")as XYZ;var frame=Get(state,"DetectedFrame")as Transform;
                    double handedness=expected.OfVector(localZ).CrossProduct(expected.OfVector(localX)).DotProduct(expected.OfVector(localY))<0?-1:1;
                    Check(command!=null&&(command-expected.OfPoint(localPoint)).GetLength()<Mm(.1),key+": corrected point follows the instance transform");
                    Check(frame!=null&&frame.BasisX.DotProduct(expected.OfVector(localX).Normalize())>.99999&&frame.BasisZ.DotProduct(expected.OfVector(localZ).Normalize())>.99999,
                        key+": corrected initial handle and operating axis follow rotation or mirror");
                    Check(!(bool)Get(state,"Confirmed")&&Get(state,"FootPoint")==null&&!(bool)Get(state,"SupportVerified")
                        &&!(bool)Get(state,"WholeGestureChecked")&&!(bool)Get(state,"WholeGestureClear"),key+": confirmation, real feet and previous geometric result are never recycled");
                    Check(new[]{"GestureUnknown","PlacementWarnings","PlacementDiagnostics","BlockingObstacles","GestureObstacles","FixedObstacles"}.All(name=>Items(state,name).Length==0)
                        &&string.IsNullOrEmpty(Get(state,"SupportNote")as string),key+": all stale support notes and previous collision lists are cleared");
                    var fields=(IDictionary)Get(state,"Fields");
                    double Number(string name)=>double.Parse(Convert.ToString(fields[name],CultureInfo.InvariantCulture).Replace(',','.'),CultureInfo.InvariantCulture);
                    Check(Math.Abs(Number("Radius")-173.25)<1e-8&&Math.Abs(Number("Height")-1900)<1e-8,
                        key+": corrected gesture dimensions reload while the receiver's human size is preserved");
                    Check(Math.Abs(Number("InitialAngle")-7*handedness)<1e-8&&Math.Abs(Number("Angle")-83*handedness)<1e-8
                        &&Math.Abs(Number("OffsetY")-12.5*handedness)<1e-8,
                        key+": initial angle, complete angle and local offset preserve signed mirror parity");
                    if(frame!=null)
                    {
                        foreach(double fraction in new[]{0.0,.5,1.0})
                        {
                            double originalAngle=(7+83*fraction)*Math.PI/180;
                            double appliedAngle=(Number("InitialAngle")+Number("Angle")*fraction)*Math.PI/180;
                            var realDirection=expected.OfVector(localX*Math.Cos(originalAngle)+localY*Math.Sin(originalAngle)).Normalize();
                            var appliedDirection=(frame.BasisX*Math.Cos(appliedAngle)+frame.BasisY*Math.Sin(appliedAngle)).Normalize();
                            Check(appliedDirection.DotProduct(realDirection)>.99999,key+": persisted phase"+fraction+" follows the physically transformed handle direction");
                        }
                    }
                    Check(Get(state,"RotationAssumed")is bool assumed&&assumed&&!(bool)Get(state,"Manufacturer")&&Items(state,"FeatureWarnings").Length>0,
                        key+": assumed travel and its warning remain explicit after profile reuse");
                    Check(Get(state,"OperatorPosture")?.ToString()=="Standing"&&Math.Abs((double)Get(state,"BodyYawDegrees"))<1e-8,
                        key+": saved posture is not imported as an accepted operator position");
                }
                Applied(source,"memory_original_reload");
                var refreshed=State("HandwheelValve");Set(refreshed,"Confirmed",true);Set(refreshed,"FootPoint",new XYZ(77777,77777,77777));
                T("MaintenanceAutoconfiguration").GetMethod("Suggest",Hidden).Invoke(null,new object[]{document,source,refreshed,(Action<string>)Stage});
                report["memory_autoconfiguration_reload"]=DescribeState(refreshed);
                Check(Get(refreshed,"CorrectionApplied")is bool remembered&&remembered&&!(bool)Get(refreshed,"Confirmed"),
                    "Automatic recognition reapplies the saved hypothesis while clearing previous confirmation");
                Check(Get(refreshed,"FootPoint")==null?Items(refreshed,"PlacementWarnings").Length>0:
                    ((XYZ)Get(refreshed,"FootPoint")-new XYZ(77777,77777,77777)).GetLength()>100&&(bool)Get(refreshed,"WholeGestureChecked"),
                    "Automatic profile reuse recomputes the actual posture and whole gesture or explains its absence");
                var temporary=new List<ElementId>();
                using(var transaction=new Transaction(document,"Temporary generic correction transformations"))
                {
                    transaction.Start();
                    try
                    {
                        var copied=ElementTransformUtils.CopyElement(document,source.Id,new XYZ(20000,20000,0));temporary.AddRange(copied);
                        var copy=copied.Select(document.GetElement).OfType<FamilyInstance>().First(v=>v.Symbol.Id==source.Symbol.Id);
                        var physicalFrame=Transform.CreateTranslation(new XYZ(20000,20000,0)).Multiply(sourceFrame);
                        if(copy.Pinned)copy.Pinned=false;document.Regenerate();Applied(copy,"memory_translated_copy",physicalFrame);
                        var rotation=Transform.CreateRotationAtPoint(XYZ.BasisZ,37*Math.PI/180,copy.GetTransform().Origin);
                        ElementTransformUtils.RotateElement(document,copy.Id,Line.CreateBound(copy.GetTransform().Origin,copy.GetTransform().Origin+XYZ.BasisZ),37*Math.PI/180);
                        physicalFrame=rotation.Multiply(physicalFrame);
                        document.Regenerate();Applied(copy,"memory_rotated_copy",physicalFrame);
                        Check(ElementTransformUtils.CanMirrorElement(document,copy.Id),"The temporary real valve copy supports a native mirror fixture");
                        if(ElementTransformUtils.CanMirrorElement(document,copy.Id))
                        {
                            using(var plane=Plane.CreateByNormalAndOrigin(XYZ.BasisX,copy.GetTransform().Origin))
                            {
                                physicalFrame=Transform.CreateReflection(plane).Multiply(physicalFrame);
                                ElementTransformUtils.MirrorElements(document,new[]{copy.Id},plane,false);
                            }
                            document.Regenerate();Applied(copy,"memory_mirrored_copy",physicalFrame);
                        }
                        var renamed=(FamilySymbol)source.Symbol.Duplicate("Native_same_geometry_"+Guid.NewGuid().ToString("N").Substring(0,8));temporary.Add(renamed.Id);
                        if(!renamed.IsActive)renamed.Activate();copy.Symbol=renamed;document.Regenerate();Applied(copy,"memory_renamed_identical_geometry",physicalFrame);
                        var nominal=source.Symbol.LookupParameter("HYD_DN")?.AsDouble()??0;
                        var variant=source.Symbol.Family.GetFamilySymbolIds().Select(document.GetElement).OfType<FamilySymbol>()
                            .FirstOrDefault(s=>Math.Abs((s.LookupParameter("HYD_DN")?.AsDouble()??nominal)-nominal)>Mm(1));
                        Check(variant!=null,"The actual valve family offers a geometrically different nominal-size variant");
                        if(variant!=null)
                        {
                            if(!variant.IsActive)variant.Activate();copy.Symbol=variant;document.Regenerate();
                            var rejected=State();bool applied=Memory("TryApply",copy,rejected,out string note);
                            var variantProbe=MemoryProbe(assembly,document,copy);
                            report["memory_variant_rejection"]=new{applied,note,type=variant.Name,nominalMm=(variant.LookupParameter("HYD_DN")?.AsDouble()??0)*304.8,state=DescribeState(rejected),
                                identity=variantProbe.TryGetValue("identity",out var variantIdentity)?variantIdentity:null,
                                descriptorComparison=ProbeDifference(baselineProbe,variantProbe),descriptorFile=SaveProbe(folder,"memory-variant",variantProbe)};
                            Check(!applied&&Get(rejected,"CommandPoint")==null,"A genuinely different nominal geometry refuses the saved correction instead of inheriting the old handle");
                        }
                    }
                    finally{transaction.RollBack();}
                }
                Check(temporary.All(id=>document.GetElement(id)==null),"Every copied, rotated and mirrored test valve is rolled back");
                Check(Memory("Remove",source,Operation("LeverValve"),out string removedNote),"The correction service removes the persisted test profile");
                report["removedMemoryNote"]=removedNote;
                var afterRemove=State();Check(!Memory("TryApply",source,afterRemove,out _),"A removed correction cannot reappear from an in-memory cache");
                Stage("Preserve the originally requested generic memory scope");
                // Deliberately override the detected lever with a user-selected
                // handwheel scenario. This tests correction lookup precedence,
                // not a claim that the actual valve has a circular handwheel.
                Set(corrected,"Operation",Operation("HandwheelValve"));
                try
                {
                    Check(Memory("Save",source,corrected,out string overrideNote),"A user-selected mechanism override can be persisted for the same strict geometry");
                    var generic=State("HandwheelValve");
                    T("MaintenanceAutoconfiguration").GetMethod("Suggest",Hidden).Invoke(null,new object[]{document,source,generic,(Action<string>)Stage});
                    report["memory_requested_mechanism_scope"]=new{note=overrideNote,state=DescribeState(generic),
                        fixture="Intentional user handwheel override of an automatically detected real lever; mechanism hypothesis remains unconfirmed."};
                    Check(Get(generic,"CorrectionApplied")is bool applied&&applied&&Get(generic,"Operation")?.ToString()=="HandwheelValve"
                        &&!(bool)Get(generic,"Confirmed"),"Generic memory lookup preserves the requested mechanism scope when automatic detection proposes a different mechanism");
                }
                finally
                {
                    Check(Memory("Remove",source,Operation("HandwheelValve"),out _),"The temporary alternative-mechanism profile is removed before old-case regressions");
                }
                Check(!document.IsModifiable,"The generic test leaves no model transaction open");
            }
            catch(Exception error){report["error"]=Failure(error);failures.Add("Unhandled native generic check failure");}
            finally
            {
                Environment.SetEnvironmentVariable(Override,previous);
                report["passed"]=failures.Count==0;report["completedUtc"]=DateTime.UtcNow;Save();
                File.WriteAllText(Path.Combine(folder,"generic-stage.txt"),failures.Count==0?"generic-checks-passed":"generic-checks-failed");
            }
        }

        private static void Visual(Assembly assembly,Document document,FamilyInstance source,object state,UIApplication app,string folder,
            Dictionary<string,object> report,Action<bool,string> check,Action save)
        {
            var type=assembly.GetType("BIMaestro.MepBooster.MaintenanceWorkbenchWindow",true);
            var window=(Window)Activator.CreateInstance(type,Hidden,null,new object[]{document,source,state,app.MainWindowHandle},null);
            var visual=new Dictionary<string,object>();report["visual"]=visual;
            window.Loaded+=(_,__)=>window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async()=>
            {
                try
                {
                    void Call(string name)=>type.GetMethod(name,Hidden).Invoke(window,null);
                    ((ComboBox)Get(window,"_mode")).SelectedIndex=0;
                    var phase=(Slider)Get(window,"_phase");var tips=new List<XYZ>();
                    foreach(int value in new[]{0,50,100})
                    {
                        phase.Value=value;Call("Draw");window.UpdateLayout();await Task.Delay(40);
                        var scene=Get(window,"_lastScene");
                        var handle=Items(scene,"Boxes").FirstOrDefault(b=>Get(b,"Role")?.ToString()=="Handle"&&Get(b,"Label")as string=="Levier");
                        check(handle!=null,"Actual-model phase"+value+" contains a lever representation instead of a wheel");
                        if(handle!=null)
                        {
                            var frame=(Transform)Get(handle,"Frame");var center=(XYZ)Get(handle,"Center");var size=(XYZ)Get(handle,"Size");
                            var tip=frame.OfPoint(center+new XYZ(size.X/2,0,0));tips.Add(tip);
                            visual["phase"+value]=new{tip=V(tip),left=V(Get(scene,"LeftHand")as XYZ),right=V(Get(scene,"RightHand")as XYZ),
                                operatorPlaced=Get(scene,"OperatorPlaced"),status=(Get(window,"_status")as TextBlock)?.Text};
                        }
                        SaveImage(window,Path.Combine(folder,"generic-1016925-phase"+value+".png"));
                    }
                    check(tips.Count==3&&(tips[0]-tips[2]).GetLength()>Mm(100)&&(tips[0]-tips[1]).GetLength()>Mm(40),
                        "The real lever moves through a visible intermediate and final pose");
                    if(Get(state,"FootPoint")!=null)
                    {
                        Call("Analyze");var collision=Get(window,"_report");
                        visual["analysis"]=new{status=(Get(window,"_status")as TextBlock)?.Text,details=(Get(window,"_details")as TextBlock)?.Text,
                            obstacles=Items(collision,"Obstacles").Select(o=>new{id=Get(o,"ElementId"),role=Get(o,"Role")?.ToString(),label=Get(o,"VolumeLabel")}).ToArray(),unknown=Items(collision,"Unknown")};
                        check(collision!=null||Get(state,"FootPoint")==null,"Native whole-gesture analysis completes or explains refusal of the proposed lever posture");
                        check(!((Get(window,"_status")as TextBlock)?.Text??"").StartsWith("Libre",StringComparison.Ordinal),
                            "Unconfirmed lever travel or geometry cannot produce a certified-free result");
                    }
                    visual["animation"]=await MaintenanceCaseDiagnostics.MeasureAnimation(assembly,window,check,
                        Path.Combine(folder,"generic-1016925-playback.png"),false);
                }
                catch(Exception error){visual["error"]=Failure(error);check(false,"Generic actual-model lever window completed without UI error");}
                finally{save();window.Close();}
            }));
            window.ShowDialog();
            check(Get(window,"_frameTimer")is DispatcherTimer timer&&!timer.IsEnabled,"The generic lever window stops its animation timer when closed");
        }
        private static void SaveImage(Window window,string path)
        {
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);
        }
        private static bool Contains(object box,XYZ point,double tolerance)
        {
            var local=((Transform)Get(box,"Frame")).Inverse.OfPoint(point)-(XYZ)Get(box,"Center");var size=(XYZ)Get(box,"Size");
            return Math.Abs(local.X)<=size.X/2+tolerance&&Math.Abs(local.Y)<=size.Y/2+tolerance&&Math.Abs(local.Z)<=size.Z/2+tolerance;
        }
        private static string SaveProbe(string folder,string key,Dictionary<string,object> probe)
        {
            string directory=Path.Combine(folder,"memory-descriptors");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,key+".json");File.WriteAllText(path,JsonSerializer.Serialize(probe,new JsonSerializerOptions{WriteIndented=true}));return path;
        }
        private static Dictionary<string,object> MemoryProbe(Assembly assembly,Document document,FamilyInstance source)
        {
            var result=new Dictionary<string,object>{["element"]=source.Id.Value,["uniqueId"]=source.UniqueId,["type"]=source.Name,
                ["nativeFrame"]=Frame(source.GetTransform()),["mirrored"]=source.Mirrored,["handFlipped"]=source.HandFlipped,["facingFlipped"]=source.FacingFlipped};
            try
            {
                var service=assembly.GetType("BIMaestro.MepBooster.MaintenanceCorrectionMemory",true);
                var captured=service.GetMethod("Capture",Hidden).Invoke(null,new object[]{document,source});
                result["identity"]=new[]{"Signature","Geometry","Parameters","Connectors","Type","Family"}.ToDictionary(name=>name,name=>Get(captured,name));
                var localFrame=Get(captured,"Source")as Transform??source.GetTransform();result["captureFrame"]=Frame(localFrame);
                var parameters=new List<string>();var read=service.GetMethod("ReadParameters",Hidden);
                read.Invoke(null,new object[]{source,"instance",parameters});read.Invoke(null,new object[]{source.Symbol,"type",parameters});
                result["parameters"]=parameters.OrderBy(value=>value,StringComparer.Ordinal).ToArray();
                var fingerprint=service.GetNestedType("Fingerprint",Hidden);
                var shape=Activator.CreateInstance(fingerprint,Hidden,null,new object[]{localFrame.Inverse},null);
                fingerprint.GetMethod("Element",Hidden).Invoke(shape,new object[]{document,source});
                result["independentGeometryDigest"]=fingerprint.GetMethod("Complete",Hidden).Invoke(shape,null);
                result["geometryTraits"]=Items(shape,"_features").Select(value=>value.ToString()).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
                result["rawInstanceParameters"]=RawParameters(source);result["rawTypeParameters"]=RawParameters(source.Symbol);
                var inverse=localFrame.Inverse;var ports=source.MEPModel?.ConnectorManager?.Connectors;
                result["rawConnectors"]=ports==null?Array.Empty<object>():ports.Cast<Connector>().Where(c=>c.ConnectorType!=ConnectorType.Logical).Select(c=>(object)new
                {domain=c.Domain.ToString(),kind=c.ConnectorType.ToString(),shape=c.Shape.ToString(),connected=c.IsConnected,
                    local=V(inverse.OfPoint(c.Origin)),axis=V(inverse.OfVector(c.CoordinateSystem.BasisZ)),
                    radius=c.Shape==ConnectorProfileType.Round?(double?)c.Radius:null,
                    width=c.Shape!=ConnectorProfileType.Round?(double?)c.Width:null,height=c.Shape!=ConnectorProfileType.Round?(double?)c.Height:null}).ToArray();
            }
            catch(Exception error){result["error"]=Failure(error);}
            return result;
        }
        private static object[] RawParameters(Element element)=>element.Parameters.Cast<Parameter>().Select(p=>(object)new
        {
            name=p.Definition?.Name,id=p.Id.Value,builtin=p.Id.Value<0?Enum.GetName(typeof(BuiltInParameter),(int)p.Id.Value):null,
            kind=p.StorageType.ToString(),spec=p.Definition?.GetDataType()?.TypeId,readOnly=p.IsReadOnly,
            number=p.StorageType==StorageType.Double?(double?)p.AsDouble():p.StorageType==StorageType.Integer?p.AsInteger():(double?)null,
            text=p.StorageType==StorageType.String?p.AsString():p.AsValueString()
        }).ToArray();
        private static object ProbeDifference(Dictionary<string,object> baseline,Dictionary<string,object> current)
        {
            string[] Rows(Dictionary<string,object> probe,string name)=>probe.TryGetValue(name,out var rows)&&rows is string[] strings?strings:Array.Empty<string>();
            object Difference(string name)
            {
                var expected=Rows(baseline,name);var actual=Rows(current,name);
                string[] Remaining(string[] a,string[] b)
                {
                    var counts=b.GroupBy(value=>value,StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.Ordinal);
                    var missing=new List<string>();
                    foreach(string value in a)if(counts.TryGetValue(value,out int count)&&count>0)counts[value]=count-1;else missing.Add(value);
                    return missing.ToArray();
                }
                var missing=Remaining(expected,actual);var added=Remaining(actual,expected);
                return new{baselineCount=expected.Length,currentCount=actual.Length,missingCount=missing.Length,addedCount=added.Length,
                    missing=missing.Take(32).ToArray(),added=added.Take(32).ToArray(),fullDescriptors="See per-case memory-descriptors JSON files for every untruncated trait."};
            }
            var identity=baseline.TryGetValue("identity",out var a)?a as Dictionary<string,object>:null;
            var other=current.TryGetValue("identity",out var b)?b as Dictionary<string,object>:null;
            return new{identityEqual=new[]{"Signature","Geometry","Parameters","Connectors","Type","Family"}.ToDictionary(name=>name,
                    name=>identity!=null&&other!=null&&Equals(identity[name],other[name])),parameters=Difference("parameters"),geometry=Difference("geometryTraits"),
                diagnosticError=current.TryGetValue("error",out var error)?error:null};
        }
        private static List<XYZ> VisibleHandlePoints(FamilyInstance source)
        {
            var result=new List<XYZ>();var material=source.Symbol.LookupParameter("Matériau poignée")?.AsElementId();
            if(material==null||material==ElementId.InvalidElementId)return result;
            using(var options=new Options{DetailLevel=ViewDetailLevel.Fine,IncludeNonVisibleObjects=false})
            using(var geometry=source.get_Geometry(options))Read(geometry);
            return result;
            void Read(GeometryElement geometry)
            {
                if(geometry==null)return;
                foreach(var item in geometry)
                {
                    if(item is GeometryInstance instance){using(var expanded=instance.GetInstanceGeometry())Read(expanded);}
                    else if(item is Solid solid&&solid.Volume>1e-12)
                        foreach(Face face in solid.Faces)
                            if(face.MaterialElementId==material)
                                using(var mesh=face.Triangulate())
                                    if(mesh!=null)result.AddRange(mesh.Vertices);
                }
            }
        }
    }
}

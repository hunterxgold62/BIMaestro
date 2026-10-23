using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
using Transform=Autodesk.Revit.DB.Transform;
using ComboBox=System.Windows.Controls.ComboBox;
using Color=System.Windows.Media.Color;
using Button=System.Windows.Controls.Button;
using Visibility=System.Windows.Visibility;

namespace BIMaestro.MotorTests
{
    // Read-only baseline for the five reported cases. No assertions about their
    // identity or accessibility: absent IDs and per-stage failures remain data.
    internal static class MaintenanceCaseDiagnostics
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private static object Get(object value,string name)=>value?.GetType().GetField(name,Hidden)?.GetValue(value)
            ??value?.GetType().GetProperty(name,Hidden)?.GetValue(value);
        private static object[] Items(object value,string name)=>Get(value,name)is IEnumerable items?items.Cast<object>().ToArray():Array.Empty<object>();
        private static double[] V(XYZ point)=>point==null?null:new[] {point.X,point.Y,point.Z};
        private static object Frame(Transform frame)=>frame==null?null:new {origin=V(frame.Origin),x=V(frame.BasisX),y=V(frame.BasisY),z=V(frame.BasisZ)};
        private static string Error(Exception error)=>(error is TargetInvocationException invocation&&invocation.InnerException!=null?invocation.InnerException:error).ToString();
        private static void Set(object value,string name,object field)=>value.GetType().GetField(name,Hidden).SetValue(value,field);
        private static object Bounds(IEnumerable<XYZ> input)
        {
            var points=input.Where(p=>p!=null).ToArray();
            return points.Length==0?null:new {count=points.Length,min=V(new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z))),
                max=V(new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)))};
        }
        private static object Parameters(Element element)=>element.Parameters.Cast<Parameter>().Select(p=>new {
            name=p.Definition.Name,kind=p.StorageType.ToString(),text=p.AsValueString()??(p.StorageType==StorageType.String?p.AsString():null),
            number=p.StorageType==StorageType.Double?(double?)p.AsDouble():p.StorageType==StorageType.Integer?p.AsInteger():(double?)null,
            length=p.StorageType==StorageType.Double&&p.Definition.GetDataType()==SpecTypeId.Length}).ToArray();
        private static object Probe(object snapshot,string coordinates)
        {
            var features=Items(snapshot,"Features");
            return new {coordinates,warnings=Items(snapshot,"Warnings"),dimensions=Items(snapshot,"Dimensions").Select(d=>new {
                kind=Get(d,"Kind")?.ToString(),name=Get(d,"Name"),component=Get(d,"Component"),value=Get(d,"Value"),origin=Get(d,"Origin")}).ToArray(),
                components=features.GroupBy(o=>Get(o,"Component")as string).Select(group=>new {component=group.Key,
                    frame=Frame(group.Select(o=>Get(o,"ComponentFrame")as Transform).FirstOrDefault(f=>f!=null)),
                    points=Bounds(group.SelectMany(o=>(Get(o,"ComponentPoints")as XYZ[])??Array.Empty<XYZ>())),
                    lineBounds=Bounds(group.SelectMany(o=>(Get(o,"CurvePoints")as XYZ[])??Array.Empty<XYZ>()))}).ToArray(),
                features=features.Select(o=>new {component=Get(o,"Component"),origin=Get(o,"Origin"),center=V(Get(o,"Center")as XYZ),axis=V(Get(o,"Axis")as XYZ),
                    radius=Get(o,"Radius"),axialLength=Get(o,"AxialLength"),coverage=Get(o,"Coverage"),cylinder=Get(o,"Cylinder"),linear=Get(o,"Linear"),explicitComponent=Get(o,"ComponentExplicit"),
                    centerlineRadius=Get(o,"CenterlineRadius"),rimThickness=Get(o,"RimThickness"),curvePoints=((Get(o,"CurvePoints")as XYZ[])??Array.Empty<XYZ>()).Select(V).ToArray()}).ToArray()};
        }
        private static object Detection(object feature,FamilyInstance source)
        {
            var inverse=source.GetTransform().Inverse;var command=Get(feature,"CommandPoint")as XYZ;
            return new {operation=Get(feature,"Operation")?.ToString(),confidence=Get(feature,"Confidence")?.ToString(),
                command=V(command),localCommand=command==null?null:V(inverse.OfPoint(command)),axis=V(Get(feature,"Axis")as XYZ),frame=Frame(Get(feature,"Frame")as Transform),
                radius=Get(feature,"Radius"),handleThickness=Get(feature,"HandleThickness"),chainDrop=Get(feature,"ChainDrop"),chainSpacing=Get(feature,"ChainSpacing"),
                removalSize=V(Get(feature,"RemovalSize")as XYZ),removalCenterOffset=V(Get(feature,"RemovalCenterOffset")as XYZ),removalTravel=Get(feature,"RemovalTravel"),
                ambiguous=Get(feature,"Ambiguous"),directionAmbiguous=Get(feature,"DirectionAmbiguous"),manufacturer=Get(feature,"Manufacturer"),
                hasRemoval=Get(feature,"HasRemoval"),hasRemovalTravel=Get(feature,"HasRemovalTravel"),evidence=Items(feature,"Evidence"),warnings=Items(feature,"Warnings"),
                candidates=Items(feature,"Candidates").Select(c=>new {label=Get(c,"Label"),score=Get(c,"Score"),command=V(Get(c,"CommandPoint")as XYZ),
                    localCommand=Get(c,"CommandPoint")is XYZ point?V(inverse.OfPoint(point)):null,axis=V(Get(c,"Axis")as XYZ),radius=Get(c,"Radius"),
                    centerlineRadius=Get(c,"CenterlineRadius"),handleThickness=Get(c,"HandleThickness"),evidence=Items(c,"Evidence")}).ToArray()};
        }
        private static object Support(object support)=>support==null?null:new {label=Get(support,"Label"),element=Get(support,"ElementId")is ElementId id?id.Value:(long?)null,
            link=Get(support,"LinkId")is ElementId link?link.Value:(long?)null,linkName=Get(support,"LinkName")};
        private static object[] Obstacles(object owner,string name)=>Items(owner,name).Select(o=>(object)new {element=Get(o,"ElementId"),link=Get(o,"LinkId"),linkName=Get(o,"LinkName"),label=Get(o,"Label"),
            role=Get(o,"Role")?.ToString(),volume=Get(o,"VolumeLabel"),box=((Get(o,"Box")as XYZ[])??Array.Empty<XYZ>()).Select(V).ToArray()}).ToArray();
        private static object Gesture(object owner)=>new {wholeGestureChecked=Get(owner,"WholeGestureChecked"),wholeGestureClear=Get(owner,"WholeGestureClear"),searchLimited=Get(owner,"SearchLimited"),
            unknown=Items(owner,"GestureUnknown"),gestureObstacles=Obstacles(owner,"GestureObstacles"),fixedObstacles=Obstacles(owner,"FixedObstacles")};
        private static string[] GestureKeys(IEnumerable<object> obstacles)=>obstacles.Where(o=>new[] {"HumanArm","ManipulationSpace","ExtractionSpace"}.Contains(Get(o,"Role")?.ToString()))
            .Select(o=>(Get(o,"Role")?.ToString()??"")+"|"+Get(o,"ElementId")+"|"+Get(o,"LinkId")).Distinct().OrderBy(k=>k).ToArray();
        private static object Placement(object placement)=>new {foot=V(Get(placement,"FootPoint")as XYZ),confidence=Get(placement,"Confidence")?.ToString(),support=Support(Get(placement,"Support")),
            suggestedWorkHeight=Get(placement,"SuggestedWorkHeight"),warnings=Items(placement,"Warnings"),assumptions=Items(placement,"Assumptions"),diagnostics=Items(placement,"Diagnostics"),
            wholeGestureChecked=Get(placement,"WholeGestureChecked"),wholeGestureClear=Get(placement,"WholeGestureClear"),searchLimited=Get(placement,"SearchLimited"),
            gestureUnknown=Items(placement,"GestureUnknown"),gestureObstacles=Obstacles(placement,"GestureObstacles"),fixedObstacles=Obstacles(placement,"FixedObstacles"),
            posture=Get(placement,"SuggestedPosture")?.ToString(),bodyYawDegrees=Get(placement,"SuggestedBodyYawDegrees"),postureWarnings=Items(placement,"PostureWarnings"),
            rejectedFoot=V(Get(placement,"RejectedFoot")as XYZ),rejectedPosture=Get(placement,"RejectedPosture")?.ToString(),rejectedBodyYawDegrees=Get(placement,"RejectedBodyYawDegrees"),
            blockingObstacles=Items(placement,"BlockingObstacles").Select(o=>new {element=Get(o,"ElementId"),link=Get(o,"LinkId"),linkName=Get(o,"LinkName"),label=Get(o,"Label"),
                role=Get(o,"Role")?.ToString(),volume=Get(o,"VolumeLabel"),box=((Get(o,"Box")as XYZ[])??Array.Empty<XYZ>()).Select(V).ToArray()}).ToArray(),
            provisionalUnknown=Items(placement,"ProvisionalUnknown"),alternatives=Items(placement,"Alternatives").Select(a=>new {foot=V(Get(a,"FootPoint")as XYZ),support=Support(Get(a,"Support")),
                score=Get(a,"Score"),workHeight=Get(a,"SuggestedWorkHeight"),posture=Get(a,"SuggestedPosture")?.ToString(),bodyYawDegrees=Get(a,"SuggestedBodyYawDegrees"),
                wholeGestureChecked=Get(a,"WholeGestureChecked"),wholeGestureClear=Get(a,"WholeGestureClear"),gestureUnknown=Items(a,"GestureUnknown"),gestureObstacles=Obstacles(a,"GestureObstacles"),
                postureWarnings=Items(a,"PostureWarnings"),provisionalUnknown=Items(a,"ProvisionalUnknown")}).ToArray()};
        private static object Profile(object profile)
        {
            var values=new Dictionary<string,object>();
            foreach(var field in profile.GetType().GetFields(Hidden).Where(f=>!f.IsStatic))
            {
                var value=field.GetValue(profile);
                values[field.Name]=value is XYZ point?V(point):value is Transform frame?Frame(frame):value is Enum?value.ToString():value;
            }
            return values;
        }
        // Actual modeled walking surfaces around the command, including direct
        // links. These broad-phase bounds supplement the placement solver's
        // exact sampled floor levels; they do not certify support by themselves.
        private static object[] WalkingSurfaces(Document document,XYZ command,Type placementType)
        {
            if(command==null)return Array.Empty<object>();
            var rows=new List<object>();
            var isWalking=placementType.GetMethod("IsWalkingSurface",Hidden);
            Capture(document,Transform.Identity,null);
            foreach(RevitLinkInstance link in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)))
                if(link.GetLinkDocument()is Document linked)Capture(linked,link.GetTotalTransform(),link);
            return rows.ToArray();
            void Capture(Document model,Transform world,RevitLinkInstance link)
            {
                foreach(var element in new FilteredElementCollector(model).WhereElementIsNotElementType())
                {
                    if(!(bool)isWalking.Invoke(null,new object[] {element}))continue;
                    using(var box=element.get_BoundingBox(null))
                    {
                        if(box==null)continue;
                        var corners=Enumerable.Range(0,8).Select(i=>world.OfPoint(box.Transform.OfPoint(new XYZ((i&1)==0?box.Min.X:box.Max.X,
                            (i&2)==0?box.Min.Y:box.Max.Y,(i&4)==0?box.Min.Z:box.Max.Z)))).ToArray();
                        if(corners.Max(p=>p.X)<command.X-13.2||corners.Min(p=>p.X)>command.X+13.2||corners.Max(p=>p.Y)<command.Y-13.2||corners.Min(p=>p.Y)>command.Y+13.2)continue;
                        rows.Add(new {element=element.Id.Value,name=element.Name,category=element.Category?.Name,link=link?.Id.Value,linkName=link?.Name,bounds=Bounds(corners),
                            sourceLevel=element.LevelId.Value,topRelativeToCommandMm=(corners.Max(p=>p.Z)-command.Z)*304.8});
                    }
                }
            }
        }
        private static void SaveImage(Window window,string path)
        {
            window.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(path))encoder.Save(file);
        }
        private static object Pose(object scene)=>scene==null?null:new {foot=V(Get(scene,"OperatorFoot")as XYZ),left=V(Get(scene,"LeftHand")as XYZ),right=V(Get(scene,"RightHand")as XYZ),
            reachable=Get(scene,"Reachable"),ghost=Items(scene,"Boxes").Where(b=>Get(b,"Role")?.ToString()=="EquipmentGhost").Select(b=>new {center=V(Get(b,"Center")as XYZ),frame=Frame(Get(b,"Frame")as Transform)}).ToArray()};
        internal static async Task<Dictionary<string,object>> MeasureAnimation(Assembly assembly,Window window,Action<bool,string> check,string capture75Path=null,bool compareBackgroundDriver=false)
        {
            var metrics=new Dictionary<string,object>();
            var geometry=assembly.GetType("BIMaestro.MepBooster.MaintenanceAccessGeometry",true);
            long Counter(string name)=>Convert.ToInt64(geometry.GetProperty(name,Hidden)?.GetValue(null)??-1L);
            var slider=(Slider)Get(window,"_phase");var play=(Button)Get(window,"_play");
            var preview=Get(window,"_preview");
            void Click()=>play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            slider.Value=0;window.GetType().GetMethod("Draw",Hidden).Invoke(window,null);
            var motion=Get(window,"_preparedMotion");
            check(motion!=null,"Animation has a prepared movement snapshot before playback");
            long PreviewCounter(string name)=>Convert.ToInt64(Get(preview,name)??-1L);
            long staticLinesBefore=PreviewCounter("StaticLineCount"),staticModelsBefore=PreviewCounter("StaticBatchModelCount"),staticBuildsBefore=PreviewCounter("StaticBatchBuildCount");
            metrics["staticLineCountBefore"]=staticLinesBefore;metrics["staticBatchModelCountBefore"]=staticModelsBefore;metrics["staticBatchBuildCountBefore"]=staticBuildsBefore;
            check(staticLinesBefore>=0&&staticModelsBefore>=0&&staticBuildsBefore>=0,"The viewer exposes fixed-line batching counters for native verification");
            if(staticLinesBefore>0)
                check(staticModelsBefore>0&&staticModelsBefore<staticLinesBefore,"Fixed visual lines are preserved in nonempty material batches with fewer models than source segments");
            var preparation=Get(motion,"PreparationMilliseconds");var sampleCount=Get(motion,"SampleCount");
            long ikBefore=Counter("HumanPoseEvaluationCount"),preparedBefore=Counter("PreparedMotionCount"),updatesBefore=Counter("PreparedPoseUpdateCount");
            var drawCosts=new List<double>();
            // Updating the slider exercises the production Draw path, while the
            // following timed playback measures the actual application clock.
            for(int i=0;i<90;i++){var draw=Stopwatch.StartNew();slider.Value=i*75.0/89;drawCosts.Add(draw.Elapsed.TotalMilliseconds);}
            slider.Value=0;
            metrics["staticBatchBuildDeltaAfterSliderScrub"]=PreviewCounter("StaticBatchBuildCount")-staticBuildsBefore;
            long ikAfterBurst=Counter("HumanPoseEvaluationCount");
            check(ikBefore<0||ikAfterBurst==ikBefore,"Repeated prepared poses do not recompute the49 inverse-kinematics samples");
            var intervals=new List<double>();var renderIntervals=new List<double>();var renderedPoseIntervals=new List<double>();
            double priorRenderedPhase=slider.Value,lastChange=0,lastRender=0,lastRenderedPose=0;
            int renderEvents=0,poseChanges=0,renderedPhaseChanges=0;bool measuring=true;TimeSpan renderingTime=TimeSpan.MinValue;
            var watch=Stopwatch.StartNew();var process=Process.GetCurrentProcess();TimeSpan cpuBefore=process.TotalProcessorTime;
            RoutedPropertyChangedEventHandler<double> poseChanged=(_,__)=>
            {
                if(!measuring)return;
                double now=watch.Elapsed.TotalMilliseconds;
                if(poseChanges>0)intervals.Add(now-lastChange);
                lastChange=now;poseChanges++;
            };
            EventHandler rendering=(_,args)=>
            {
                if(!measuring)return;
                if(args is RenderingEventArgs frame){if(frame.RenderingTime==renderingTime)return;renderingTime=frame.RenderingTime;}
                double now=watch.Elapsed.TotalMilliseconds;
                if(renderEvents>0)renderIntervals.Add(now-lastRender);lastRender=now;renderEvents++;
                if(Math.Abs(slider.Value-priorRenderedPhase)>1e-9)
                {if(renderedPhaseChanges>0)renderedPoseIntervals.Add(now-lastRenderedPose);lastRenderedPose=now;renderedPhaseChanges++;priorRenderedPhase=slider.Value;}
            };
            slider.ValueChanged+=poseChanged;
            try
            {
                Click();
                CompositionTarget.Rendering+=rendering;
                check(Get(window,"_playing")is bool playing&&playing,"The real playback button starts the application animation clock");
                var viewport=Get(preview,"_viewport")as Viewport3D;var inputSurface=Get(preview,"_inputSurface")as UIElement;
                metrics["playbackRenderedEdgeMode"]=viewport==null?null:RenderOptions.GetEdgeMode(viewport).ToString();
                metrics["usesGeometryHitTesting"]=Get(preview,"UsesGeometryHitTesting");
                metrics["viewportHitTestVisible"]=viewport?.IsHitTestVisible;
                metrics["inputSurfaceHitTestVisible"]=inputSurface?.IsHitTestVisible;
                metrics["inputSurfaceVisibility"]=inputSurface?.Visibility.ToString();
                check(viewport!=null&&RenderOptions.GetEdgeMode(viewport)==EdgeMode.Aliased
                    &&Get(preview,"IsFastRenderingActive")is bool fast&&fast,"Playback uses the fast aliased3D render mode");
                check(viewport!=null&&!viewport.IsHitTestVisible&&Get(preview,"UsesGeometryHitTesting")is bool hitTesting&&!hitTesting,
                    "Animated geometry does not participate in expensive3D pointer hit testing");
                check(inputSurface!=null&&inputSurface.IsHitTestVisible&&inputSurface.Visibility==Visibility.Visible,
                    "A visible2D input surface preserves pointer navigation while3D hit testing is disabled");
                while(slider.Value<75&&watch.ElapsedMilliseconds<4500&&Get(window,"_playing")is bool active&&active)await Task.Delay(20);
                double elapsed=watch.Elapsed.TotalMilliseconds,phase75=slider.Value;TimeSpan cpuAfter=process.TotalProcessorTime;
                measuring=false;
                int measuredRenders=renderEvents,measuredChanges=poseChanges;double[] measuredIntervals=intervals.ToArray();
                var frameTimer=Get(window,"_frameTimer")as DispatcherTimer;
                bool driverChosen=Get(window,"_playbackDriverChosen")is bool chosen&&chosen;
                bool preferTimer=Get(window,"_preferTimerPlayback")is bool preference&&preference;
                bool usingTimer=Get(window,"_usingTimerPlayback")is bool usingFrameTimer&&usingFrameTimer;
                double measuredProbe=Convert.ToDouble(Get(window,"_measuredRenderFps")??0);
                metrics["playbackDriverChosenAt75"]=driverChosen;metrics["timerPreferredAt75"]=preferTimer;metrics["usingTimerAt75"]=usingTimer;
                metrics["probeMeasuredRenderFps"]=measuredProbe;metrics["frameTimerEnabledAt75"]=frameTimer?.IsEnabled;
                check(driverChosen,"Product playback chooses its adaptive driver during the initial rendering probe");
                check(!driverChosen||preferTimer==(measuredProbe<27),"The adaptive choice follows its measured rendering probe without an imposed FPS target");
                check(frameTimer!=null&&frameTimer.IsEnabled==preferTimer&&usingTimer==preferTimer,
                    "Only the timer selected by the production adaptive controller is enabled");
                Click();double paused=slider.Value;
                check(!(Get(window,"_playing")is bool enabled&&enabled),"Playback can be paused before the end of the gesture");
                metrics["frameTimerEnabledAfterPause"]=frameTimer?.IsEnabled;
                check(frameTimer!=null&&!frameTimer.IsEnabled,"Pausing the product stops its adaptive frame timer");
                await Task.Delay(120);
                check(Math.Abs(slider.Value-paused)<.001,"Paused playback preserves the current mechanism and hand position");
                metrics["pausedRenderedEdgeMode"]=viewport==null?null:RenderOptions.GetEdgeMode(viewport).ToString();
                check(viewport!=null&&RenderOptions.GetEdgeMode(viewport)!=EdgeMode.Aliased
                    &&Get(preview,"IsFastRenderingActive")is bool pausedFast&&!pausedFast,"Pausing playback restores normal antialiased rendering");
                if(capture75Path!=null)SaveImage(window,capture75Path);
                double expectedMs=75*33; // 75% of the documented3.3-second gesture.
                metrics["elapsedTo75PercentMs"]=elapsed;metrics["phaseAtPause"]=phase75;metrics["expectedTo75PercentMs"]=expectedMs;
                metrics["processCpuMsTo75Percent"]=(cpuAfter-cpuBefore).TotalMilliseconds;
                metrics["renderEventsTo75Percent"]=measuredRenders;metrics["poseChangesTo75Percent"]=measuredChanges;
                metrics["renderCallbacksWithChangedPhase"]=renderedPhaseChanges;
                metrics["renderedPoseChangesPerSecond"]=renderedPhaseChanges*1000.0/Math.Max(1,elapsed);
                metrics["renderedPoseIntervalP95Ms"]=renderedPoseIntervals.Count==0?(double?)null:renderedPoseIntervals.OrderBy(x=>x).ElementAt((int)Math.Floor((renderedPoseIntervals.Count-1)*.95));
                metrics["samplingScope"]="Distinct WPF RenderingTime callbacks and separate slider ValueChanged pose updates; neither counter proves monitor presentation.";
                metrics["driver"]="Adaptive product playback: initial Rendering probe, optional production Background timer";
                metrics["observedPoseChangesPerSecond"]=measuredChanges*1000.0/Math.Max(1,elapsed);
                metrics["renderEventsPerSecond"]=measuredRenders*1000.0/Math.Max(1,elapsed);
                metrics["poseIntervalP95Ms"]=measuredIntervals.Length==0?(double?)null:measuredIntervals.OrderBy(x=>x).ElementAt((int)Math.Floor((measuredIntervals.Length-1)*.95));
                metrics["poseIntervalMaxMs"]=measuredIntervals.Length==0?(double?)null:measuredIntervals.Max();
                metrics["renderIntervalP95Ms"]=renderIntervals.Count==0?(double?)null:renderIntervals.OrderBy(x=>x).ElementAt((int)Math.Floor((renderIntervals.Count-1)*.95));
                check(phase75>=74.9&&phase75<100,"Timed playback advances to75% without jumping directly to the last pose");
                Click();
                metrics["timerPreferredOnResume"]=Get(window,"_preferTimerPlayback");metrics["driverChosenOnResume"]=Get(window,"_playbackDriverChosen");
                check(Get(window,"_playbackDriverChosen")is bool resumedChosen&&resumedChosen
                    &&Get(window,"_preferTimerPlayback")is bool resumedPreference&&resumedPreference==preferTimer,
                    "Resuming playback preserves the adaptive driver decision for the unchanged profile");
                check(frameTimer!=null&&frameTimer.IsEnabled==preferTimer,
                    "Resuming playback enables exactly the previously selected production driver");
                await Task.Delay(180);double resumed=slider.Value;
                check(resumed>paused&&resumed<=100,"Playback resumes from its paused phase");
                if(Get(window,"_playing")is bool running&&running)Click();
                check(frameTimer!=null&&!frameTimer.IsEnabled,"Stopping after resume leaves no adaptive timer running");
                metrics["resumedPhase"]=resumed;
            }
            finally{CompositionTarget.Rendering-=rendering;slider.ValueChanged-=poseChanged;process.Dispose();}
            long ikAfter=Counter("HumanPoseEvaluationCount"),preparedAfter=Counter("PreparedMotionCount"),updatesAfter=Counter("PreparedPoseUpdateCount");
            metrics["humanPoseEvaluationDelta"]=ikAfter-ikBefore;metrics["preparedMotionDelta"]=preparedAfter-preparedBefore;metrics["preparedPoseUpdateDelta"]=updatesAfter-updatesBefore;
            metrics["sampleCount"]=sampleCount;metrics["preparationMilliseconds"]=preparation;
            metrics["synchronousDrawMeanMs"]=drawCosts.Average();metrics["synchronousDrawP95Ms"]=drawCosts.OrderBy(x=>x).ElementAt(85);
            check(ikBefore>=0&&ikAfter==ikBefore,"Animation reuses its prepared IK path for the whole playback measurement");
            check(preparedBefore>=0&&preparedAfter==preparedBefore&&ReferenceEquals(motion,Get(window,"_preparedMotion")),"Prepared movement identity stays stable throughout playback, pause and resume");
            check(updatesAfter>updatesBefore,"Prepared pose-update instrumentation records the animated frames");
            foreach(string name in new[] {"PreviewTiming","RenderFramesPerSecond","SceneUpdatesPerSecond","SceneIntervalP95Milliseconds","SceneIntervalP99Milliseconds",
                "RenderIntervalP95Milliseconds","RenderIntervalP99Milliseconds","InputToRenderP95Milliseconds","InputToRenderP99Milliseconds","InputLatencySampleCount",
                "TransparencySortCount","TransparencyOrderChangeCount","LastTransparencySortMilliseconds","TimingScope"})metrics[name]=Get(preview,name);
            slider.Value=75;
            long staticLinesAfter=PreviewCounter("StaticLineCount"),staticModelsAfter=PreviewCounter("StaticBatchModelCount"),staticBuildsAfter=PreviewCounter("StaticBatchBuildCount");
            metrics["staticLineCountAfter"]=staticLinesAfter;metrics["staticBatchModelCountAfter"]=staticModelsAfter;metrics["staticBatchBuildCountAfter"]=staticBuildsAfter;
            metrics["staticBatchBuildDelta"]=staticBuildsAfter-staticBuildsBefore;
            if(staticLinesBefore>0&&ReferenceEquals(motion,Get(window,"_preparedMotion")))
            {
                check(staticLinesAfter==staticLinesBefore&&staticModelsAfter==staticModelsBefore,"Playback, pause, resume and timeline scrubbing preserve the fixed line geometry and material batches");
                check(staticBuildsAfter==staticBuildsBefore,"Fixed line batches are not reconstructed during playback, pause, resume or timeline scrubbing");
            }
            metrics["renderPipeline"]=RenderPipeline(window,preview);
            if(compareBackgroundDriver)
                metrics["backgroundDriver"]=await MeasureBackgroundDriver(assembly,window,check);
            return metrics;
        }
        private static object RenderPipeline(Window window,object preview)
        {
            var host=PresentationSource.FromVisual(window)as System.Windows.Interop.HwndSource;
            var values=new Dictionary<string,object> {
                ["processRenderMode"]=System.Windows.Media.RenderOptions.ProcessRenderMode.ToString(),
                ["hwndRenderMode"]=host?.CompositionTarget?.RenderMode.ToString(),
                ["renderingTier"]=RenderCapability.Tier>>16,
                ["viewerEdgeMode"]=Get(preview,"_viewport")is Viewport3D viewport?RenderOptions.GetEdgeMode(viewport).ToString():null,
                ["usesGeometryHitTesting"]=Get(preview,"UsesGeometryHitTesting")
            };
            foreach(string name in new[] {"MaxVisualMeshPositions","MaxVisualMeshIndices"})values[name]=Get(preview,name);
            return values;
        }
        private static async Task<Dictionary<string,object>> MeasureBackgroundDriver(Assembly assembly,Window window,Action<bool,string> check)
        {
            var metrics=new Dictionary<string,object>{{"driver","DispatcherPriority.Background16ms invoking the unchanged production AdvancePlayback"},
                {"samplingScope","Distinct WPF RenderingTime callbacks and separate slider ValueChanged pose updates; neither counter proves monitor presentation."}};
            var type=window.GetType();var stop=type.GetMethod("StopPlayback",Hidden);
            void StopController()=>stop.Invoke(window,stop.GetParameters().Length==0?null:new object[] {false});
            var advance=(EventHandler)Delegate.CreateDelegate(typeof(EventHandler),window,type.GetMethod("AdvancePlayback",Hidden));
            var slider=(Slider)Get(window,"_phase");var play=(Button)Get(window,"_play");var preview=Get(window,"_preview");
            var geometry=assembly.GetType("BIMaestro.MepBooster.MaintenanceAccessGeometry",true);
            long Counter(string name)=>Convert.ToInt64(geometry.GetProperty(name,Hidden)?.GetValue(null)??-1L);
            long PreviewCounter(string name)=>Convert.ToInt64(Get(preview,name)??-1L);
            StopController();slider.Value=0;type.GetMethod("Draw",Hidden).Invoke(window,null);
            var motion=Get(window,"_preparedMotion");
            long ikBefore=Counter("HumanPoseEvaluationCount"),motionsBefore=Counter("PreparedMotionCount"),updatesBefore=Counter("PreparedPoseUpdateCount"),
                staticBuildsBefore=PreviewCounter("StaticBatchBuildCount"),modelsBefore=PreviewCounter("CachedModelBuildCount");
            var poseIntervals=new List<double>();var renderIntervals=new List<double>();var renderedPoseIntervals=new List<double>();
            int poses=0,renders=0,changedRenderPoses=0,driverCalls=0;double lastPose=0,lastRender=0,lastRenderedPhase=0,lastRenderedPose=0;TimeSpan lastRenderingTime=TimeSpan.MinValue;
            bool measuring=false;Exception driverError=null;var watch=new Stopwatch();
            var timer=new DispatcherTimer(DispatcherPriority.Background,window.Dispatcher){Interval=TimeSpan.FromMilliseconds(16)};
            EventHandler tick=(_,__)=>
            {
                if(!(Get(window,"_playing")is bool playing&&playing)){timer.Stop();return;}
                try{driverCalls++;advance(window,EventArgs.Empty);}
                catch(Exception error){driverError=error;timer.Stop();StopController();}
            };
            RoutedPropertyChangedEventHandler<double> poseChanged=(_,__)=>
            {
                if(!measuring)return;double now=watch.Elapsed.TotalMilliseconds;
                if(poses>0)poseIntervals.Add(now-lastPose);lastPose=now;poses++;
            };
            EventHandler rendered=(_,args)=>
            {
                if(!measuring)return;
                if(args is RenderingEventArgs frame){if(frame.RenderingTime==lastRenderingTime)return;lastRenderingTime=frame.RenderingTime;}
                double now=watch.Elapsed.TotalMilliseconds;
                if(renders>0)renderIntervals.Add(now-lastRender);lastRender=now;renders++;
                if(Math.Abs(slider.Value-lastRenderedPhase)>1e-9)
                {if(changedRenderPoses>0)renderedPoseIntervals.Add(now-lastRenderedPose);lastRenderedPose=now;changedRenderPoses++;lastRenderedPhase=slider.Value;}
            };
            void StartBackgroundPlayback()
            {
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // Play and resume both subscribe the controller again.
                CompositionTarget.Rendering-=advance;
                timer.Start();
            }
            timer.Tick+=tick;slider.ValueChanged+=poseChanged;
            using(var process=Process.GetCurrentProcess())
            try
            {
                TimeSpan cpu=process.TotalProcessorTime;
                watch.Start();measuring=true;
                // Remove only the controller delegate just attached by Play.
                // Preview rendering listeners and all other add-ins stay intact.
                StartBackgroundPlayback();
                CompositionTarget.Rendering+=rendered;
                check(Get(window,"_playing")is bool active&&active,"A/B background driver starts the same production clock and prepared movement");
                while(slider.Value<75&&watch.ElapsedMilliseconds<4500&&Get(window,"_playing")is bool playing&&playing&&driverError==null)await Task.Delay(20);
                measuring=false;timer.Stop();double elapsed=watch.Elapsed.TotalMilliseconds,phase=slider.Value;
                metrics["elapsedTo75PercentMs"]=elapsed;metrics["phaseAtPause"]=phase;metrics["expectedTo75PercentMs"]=2475;
                metrics["processCpuMsTo75Percent"]=(process.TotalProcessorTime-cpu).TotalMilliseconds;
                metrics["renderEventsTo75Percent"]=renders;metrics["poseChangesTo75Percent"]=poses;metrics["renderCallbacksWithChangedPhase"]=changedRenderPoses;metrics["driverCalls"]=driverCalls;
                metrics["observedPoseChangesPerSecond"]=poses*1000.0/Math.Max(1,elapsed);metrics["renderEventsPerSecond"]=renders*1000.0/Math.Max(1,elapsed);
                metrics["renderedPoseChangesPerSecond"]=changedRenderPoses*1000.0/Math.Max(1,elapsed);
                metrics["renderedPoseIntervalP95Ms"]=renderedPoseIntervals.Count==0?(double?)null:renderedPoseIntervals.OrderBy(x=>x).ElementAt((int)Math.Floor((renderedPoseIntervals.Count-1)*.95));
                metrics["poseIntervalP95Ms"]=poseIntervals.Count==0?(double?)null:poseIntervals.OrderBy(x=>x).ElementAt((int)Math.Floor((poseIntervals.Count-1)*.95));
                metrics["renderIntervalP95Ms"]=renderIntervals.Count==0?(double?)null:renderIntervals.OrderBy(x=>x).ElementAt((int)Math.Floor((renderIntervals.Count-1)*.95));
                metrics["driverError"]=driverError==null?null:Error(driverError);
                check(driverError==null&&phase>=74.9&&phase<100,"A/B background playback reaches75% through the unchanged controller");
                play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double paused=slider.Value;await Task.Delay(120);
                check(!(Get(window,"_playing")is bool pausedPlaying&&pausedPlaying)&&Math.Abs(slider.Value-paused)<.001,
                    "A/B background playback respects the production pause control");
                StartBackgroundPlayback();await Task.Delay(180);timer.Stop();
                metrics["resumedPhase"]=slider.Value;
                check(slider.Value>paused&&slider.Value<=100,"A/B background playback resumes with only its timer driver attached");
                metrics["humanPoseEvaluationDelta"]=Counter("HumanPoseEvaluationCount")-ikBefore;
                metrics["preparedMotionDelta"]=Counter("PreparedMotionCount")-motionsBefore;
                metrics["preparedPoseUpdateDelta"]=Counter("PreparedPoseUpdateCount")-updatesBefore;
                metrics["staticBatchBuildDelta"]=PreviewCounter("StaticBatchBuildCount")-staticBuildsBefore;
                metrics["cachedModelBuildDelta"]=PreviewCounter("CachedModelBuildCount")-modelsBefore;
                check(Counter("HumanPoseEvaluationCount")==ikBefore&&Counter("PreparedMotionCount")==motionsBefore&&ReferenceEquals(motion,Get(window,"_preparedMotion")),
                    "A/B background playback reuses the exact prepared movement with zero new IK evaluations");
                check(PreviewCounter("StaticBatchBuildCount")==staticBuildsBefore&&PreviewCounter("CachedModelBuildCount")==modelsBefore,
                    "A/B background playback reuses existing static batches and model caches without reconstruction");
                metrics["renderPipeline"]=RenderPipeline(window,preview);
            }
            finally
            {
                measuring=false;timer.Stop();timer.Tick-=tick;slider.ValueChanged-=poseChanged;CompositionTarget.Rendering-=rendered;
                StopController();CompositionTarget.Rendering-=advance;
                check(!(Get(window,"_playing")is bool playing&&playing),"A/B cleanup stops the production clock and removes only its temporary driver");
            }
            slider.Value=75;
            return metrics;
        }
        private static void Visual(Assembly assembly,Document document,FamilyInstance source,object state,UIApplication app,string folder,Dictionary<string,object> row,Action save)
        {
            var failures=new List<string>();var checks=new List<string>();var visual=new Dictionary<string,object>();row["visual"]=visual;
            visual["checks"]=checks;visual["failures"]=failures;
            void Check(bool condition,string label){if(condition)checks.Add(label);else failures.Add(label);}
            var construction=Stopwatch.StartNew();
            var type=assembly.GetType("BIMaestro.MepBooster.MaintenanceWorkbenchWindow",true);
            var window=(Window)Activator.CreateInstance(type,Hidden,null,new object[] {document,source,state,app.MainWindowHandle},null);
            visual["windowAndCaptureSeconds"]=construction.Elapsed.TotalSeconds;
            window.Loaded+=(_,__)=>window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async ()=>
            {
                try
                {
                    void Call(string name)=>type.GetMethod(name,Hidden).Invoke(window,null);
                    var mode=(ComboBox)Get(window,"_mode");mode.SelectedIndex=0;
                    var phase=(Slider)Get(window,"_phase");var volumes=(CheckBox)Get(window,"_showVolumes");
                    var blocked=(CheckBox)Get(window,"_showBlockedPosture");if(blocked!=null)blocked.IsChecked=true;
                    volumes.IsChecked=false;phase.Value=0;Call("Draw");
                    var preview=Get(window,"_preview");
                    visual["initialStatus"]=(Get(window,"_status")as TextBlock)?.Text;
                    visual["initialModeNote"]=(Get(window,"_modeNote")as TextBlock)?.Text;
                    visual["initialGesture"]=Gesture(state);
                    visual["pose0"]=Pose(Get(window,"_lastScene"));
                    visual["capture"]=new {note=Get(preview,"CaptureNote"),contextElements=Get(preview,"_contextElements"),contextTriangles=Get(preview,"ContextTriangleCount"),
                        captureMilliseconds=Get(preview,"CaptureMilliseconds"),failedElements=Get(preview,"_failedElements"),truncated=Get(preview,"_truncated")};
                    SaveImage(window,Path.Combine(folder,"case-"+source.Id.Value+"-start.png"));
                    phase.Value=50;Call("Draw");visual["pose50"]=Pose(Get(window,"_lastScene"));
                    SaveImage(window,Path.Combine(folder,"case-"+source.Id.Value+".png"));
                    Check(mode.SelectedIndex==0,"The inspected case is explicitly shown in actual-model mode");
                    Check(!string.IsNullOrWhiteSpace(visual["initialStatus"]as string),"The actual case exposes a readable status");
                    bool rejected=Get(state,"FootPoint")==null&&Get(state,"RejectedFoot")!=null;
                    bool redAvatar=Items(preview,"_boxSlots").Any(b=>Get(b,"Shape")as string=="torso"&&Get(b,"Color")is Color color&&color==Colors.Firebrick);
                    int blockedModels=Get(Get(preview,"_blockedSnapshot"),"Children")is IEnumerable children?children.Cast<object>().Count():0;
                    bool selectedRed=Get(Get(preview,"_selectedVisual"),"Content")!=null
                        &&ReferenceEquals(Get(Get(preview,"_selectedVisual"),"Content"),Get(preview,"_selectedBlockedSnapshot"));
                    visual["refusedPosture"]=new {rejected,redAvatar,blockedModels,selectedRed,blockingObstacles=Items(state,"BlockingObstacles").Length,
                        rejectedFoot=V(Get(state,"RejectedFoot")as XYZ),posture=Get(state,"RejectedPosture")?.ToString(),yaw=Get(state,"RejectedBodyYawDegrees")};
                    if(rejected)
                    {
                        Check(redAvatar,"A refused candidate is represented by a red avatar instead of an accepted posture");
                        Check(((visual["initialModeNote"]as string)??"").Contains("POSTE REFUSÉ EN ROUGE"),"The red avatar is explicitly identified as a refused position");
                        if(Items(state,"BlockingObstacles").Length>0)
                            Check(blockedModels>0||selectedRed,"At least one captured blocking element is rendered with its actual red geometry");
                    }
                    else if(Get(state,"FootPoint")==null)
                        Check(Items(state,"PlacementWarnings").Length>0,"An absent actual posture keeps the physical placement refusal explanation");
                    int plainBoxes=Items(preview,"_boxSlots").Length;
                    volumes.IsChecked=true;Call("Draw");
                    visual["volumes"]=new {plainBoxes,controlledBoxes=Items(preview,"_boxSlots").Length,note=Get(preview,"CaptureNote")};
                    SaveImage(window,Path.Combine(folder,"case-"+source.Id.Value+"-volumes.png"));
                    volumes.IsChecked=false;Call("Draw");
                    if(Get(state,"FootPoint")!=null)
                    {
                        var analysis=Stopwatch.StartNew();Call("Analyze");visual["analysisSeconds"]=analysis.Elapsed.TotalSeconds;
                        visual["analysisStatus"]=(Get(window,"_status")as TextBlock)?.Text;
                        visual["analysisDetails"]=(Get(window,"_details")as TextBlock)?.Text;
                        var report=Get(window,"_report");
                        visual["analysis"]=report==null?null:new {checkedVolumes=Get(report,"CheckedVolumes"),obstacleCount=Items(report,"Obstacles").Length,
                            obstacles=Items(report,"Obstacles").Select(o=>new {label=Get(o,"Label"),volumeLabel=Get(o,"VolumeLabel"),role=Get(o,"Role")?.ToString(),
                                elementId=Get(o,"ElementId"),linkId=Get(o,"LinkId"),linkName=Get(o,"LinkName"),box=((Get(o,"Box")as XYZ[])??Array.Empty<XYZ>()).Select(V).ToArray()}).ToArray(),
                            unknown=Items(report,"Unknown"),cancelled=Get(report,"Cancelled")};
                        visual["validatedGesture"]=Gesture(state);
                        if(report!=null&&Get(state,"WholeGestureChecked")is bool gestureChecked&&gestureChecked)
                        {
                            var placementKeys=GestureKeys(Items(state,"FixedObstacles").Concat(Items(state,"GestureObstacles")));
                            var scanKeys=GestureKeys(Items(report,"Obstacles"));
                            var missing=scanKeys.Except(placementKeys).ToArray();var extra=placementKeys.Except(scanKeys).ToArray();
                            visual["gestureScannerComparison"]=new {placementKeys,scanKeys,missingFromPlacement=missing,placementOnly=extra};
                            Check(missing.Length==0,"Exact placement validation preserves all fixed/arm conflicts found by the production scanner");
                            Check(!(Get(state,"WholeGestureClear")is bool clear&&clear)||scanKeys.Length==0,
                                "A placement marked whole-gesture-clear cannot produce a known fixed/arm scanner collision");
                        }
                        bool unconfirmed=Get(state,"WholeGestureChecked")is not bool checkedGesture||!checkedGesture
                            ||Get(state,"WholeGestureClear")is not bool clearGesture||!clearGesture
                            ||Items(state,"GestureUnknown").Length>0||Items(state,"FixedObstacles").Length>0||Items(state,"GestureObstacles").Length>0
                            ||Items(state,"FeatureWarnings").Length>0||Items(state,"PlacementWarnings").Length>0
                            ||Get(state,"SearchLimited")is bool limited&&limited||Get(state,"Confirmed")is not bool confirmed||!confirmed;
                        var status=visual["analysisStatus"]as string??"";
                        if(unconfirmed)Check(!status.StartsWith("Libre",StringComparison.Ordinal)&&!status.StartsWith("Geste possible géométriquement",StringComparison.Ordinal),
                            "Unconfirmed dimensions, uncertainty, limited search or remaining gesture obstacles cannot produce a validated-free UI result");
                        // A stricter validation may refuse a previously proposed
                        // candidate. Both results remain legitimate diagnostics.
                        Check(report!=null||Get(state,"FootPoint")==null,"Analysis completes or explicitly rejects the proposed posture during exact validation");
                        SaveImage(window,Path.Combine(folder,"case-"+source.Id.Value+"-checked.png"));
                    }
                    var animation=await MeasureAnimation(assembly,window,Check,Path.Combine(folder,"case-"+source.Id.Value+"-75percent.png"),false);
                    visual["animation"]=animation;
                    if(new[] {1045122L,1045267L,980915L}.Contains(source.Id.Value))
                        Check(animation.TryGetValue("timerPreferredAt75",out var preferredTimer)&&preferredTimer is bool selectedTimer&&selectedTimer,
                            "The recorded heavy Antibes scene selects the production timer fallback");
                    visual["renderFramesPerSecond"]=Get(preview,"RenderFramesPerSecond");visual["sceneUpdatesPerSecond"]=Get(preview,"SceneUpdatesPerSecond");
                    visual["renderingTier"]=Get(preview,"RenderingTier");visual["previewTiming"]=Get(preview,"PreviewTiming");
                    Check(mode.SelectedIndex==0,"Animation remains in actual-model mode throughout measurement");
                    if(rejected&&blocked!=null)
                    {
                        blocked.IsChecked=false;Call("Draw");
                        Check(!Items(preview,"_boxSlots").Any(b=>Get(b,"Shape")as string=="torso")&&Get(state,"FootPoint")==null,
                            "Hiding the rejected example removes its avatar without accepting the position");
                        blocked.IsChecked=true;Call("Draw");
                    }
                    Call("Invalidate");
                    Check(Get(window,"_report")==null,"Changing the validation state clears the previous collision report");
                    if(Items(state,"BlockingObstacles").Length==0&&Items(state,"GestureObstacles").Length==0&&Items(state,"FixedObstacles").Length==0)
                    {
                        int remaining=Get(Get(preview,"_blockedSnapshot"),"Children")is IEnumerable remainingChildren?remainingChildren.Cast<object>().Count():0;
                        bool oldSourceRed=Get(Get(preview,"_selectedVisual"),"Content")!=null
                            &&ReferenceEquals(Get(Get(preview,"_selectedVisual"),"Content"),Get(preview,"_selectedBlockedSnapshot"));
                        Check(remaining==0&&!oldSourceRed,"Invalidating analysis removes stale red model geometry when no placement blocker remains");
                    }
                    visual["passed"]=failures.Count==0;
                }
                catch(Exception error){visual["error"]=Error(error);visual["passed"]=false;}
                finally{save();window.Close();}
            }));
            window.ShowDialog();
            var closedTimer=Get(window,"_frameTimer")as DispatcherTimer;
            visual["frameTimerEnabledAfterClose"]=closedTimer?.IsEnabled;
            Check(closedTimer!=null&&!closedTimer.IsEnabled,"Closing the window leaves the adaptive frame timer stopped");
            visual["passed"]=failures.Count==0&&!visual.ContainsKey("error");save();
        }
        internal static void Run(Assembly assembly,Document document,string folder,UIApplication app=null)
        {
            Type T(string name)=>assembly.GetType("BIMaestro.MepBooster."+name,true);
            var rows=new List<Dictionary<string,object>>();var output=new Dictionary<string,object>();
            output["model"]=new {path=document.PathName,title=document.Title,readOnly=document.IsReadOnly,units="Geometry/lengths in Revit feet unless a key explicitly ends in Mm or M."};
            output["productionAssembly"]=new {path=assembly.Location,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))};
            output["cases"]=rows;
            void Save()=>File.WriteAllText(Path.Combine(folder,"maintenance-case-diagnostics.json"),JsonSerializer.Serialize(output,new JsonSerializerOptions {WriteIndented=true}));
            foreach(var item in new[] {new {Id=1014886L,Operation="Filter"},new {Id=1045122L,Operation="Filter"},new {Id=1045267L,Operation="HandwheelValve"},new {Id=950354L,Operation="HandwheelValve"},new {Id=980915L,Operation="HandwheelValve"}})
            {
                var row=new Dictionary<string,object>{{"id",item.Id},{"preferredOperation",item.Operation}};rows.Add(row);Save();
                Action<string> progress=text=>File.WriteAllText(Path.Combine(folder,"case-stage.txt"),item.Id+" "+text);
                try
                {
                    progress("Resolve actual model element");
                    var source=document.GetElement(new ElementId(item.Id))as FamilyInstance;
                    if(source==null){row["missing"]=true;row["message"]="ID absent or not a FamilyInstance in the explicitly reported model; no substitute inferred.";continue;}
                    row["family"]=source.Symbol.Family.Name;row["type"]=source.Name;row["uniqueId"]=source.UniqueId;row["frame"]=Frame(source.GetTransform());
                    row["parameters"]=Parameters(source);row["typeParameters"]=Parameters(source.Symbol);
                    row["level"]=document.GetElement(source.LevelId)is Level level?new {id=level.Id.Value,name=level.Name,elevation=level.Elevation}:null;
                    var local=source.GetTransform().Inverse;
                    row["connectors"]=source.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>().Select(c=>new {world=V(c.Origin),local=V(local.OfPoint(c.Origin)),
                        axis=V(c.CoordinateSystem.BasisZ),radius=c.Shape==ConnectorProfileType.Round?c.Radius:0}).ToArray();
                    var detector=T("MaintenanceFeatureDetection");var op=Enum.Parse(T("MaintenanceAccessOperation"),item.Operation);
                    progress("Detect baseline command and dimensions");var watch=Stopwatch.StartNew();
                    var feature=detector.GetMethod("Detect",Hidden).Invoke(null,new object[] {document,source,op});
                    row["detectionSeconds"]=watch.Elapsed.TotalSeconds;row["detection"]=Detection(feature,source);Save();
                    try
                    {
                        var snapshot=Activator.CreateInstance(detector.GetNestedType("Snapshot",BindingFlags.NonPublic),true);
                        detector.GetMethod("ReadElement",Hidden).Invoke(null,new object[] {source,Transform.Identity,source.Symbol.Family.Name,"diagnostic instance",snapshot,false});
                        row["projectProbe"]=Probe(snapshot,"world feet");
                        row["familyProbe"]=Probe(detector.GetMethod("ProbeFamily",Hidden).Invoke(null,new object[] {document,source}),"family local feet");
                    }
                    catch(Exception error){row["probeError"]=Error(error);}
                    row["shape"]=typeof(MaintenanceFeatureInspection).GetMethod("Shape",Hidden).Invoke(null,new object[] {source,local});
                    row["walkingSurfaces"]=WalkingSurfaces(document,Get(feature,"CommandPoint")as XYZ,T("MaintenanceOperatorPlacement"));Save();
                    var state=Activator.CreateInstance(T("MaintenanceWorkbenchState"),true);Set(state,"Operation",op);
                    watch.Restart();T("MaintenanceAutoconfiguration").GetMethod("Suggest",Hidden).Invoke(null,new object[] {document,source,state,progress});
                    row["autoconfigurationSeconds"]=watch.Elapsed.TotalSeconds;
                    var fields=(IDictionary)Get(state,"Fields");
                    row["proposedState"]=new {operation=Get(state,"Operation")?.ToString(),command=V(Get(state,"CommandPoint")as XYZ),foot=V(Get(state,"FootPoint")as XYZ),frame=Frame(Get(state,"DetectedFrame")as Transform),
                        supportVerified=Get(state,"SupportVerified"),supportNote=Get(state,"SupportNote"),measured=Get(state,"Measured"),manufacturer=Get(state,"Manufacturer"),detectionNote=Get(state,"DetectionNote"),
                        posture=Get(state,"OperatorPosture")?.ToString(),bodyYawDegrees=Get(state,"BodyYawDegrees"),postureSearch=Get(state,"PostureSearch")?.ToString(),
                        postureWarnings=Items(state,"PostureWarnings"),rejectedFoot=V(Get(state,"RejectedFoot")as XYZ),rejectedPosture=Get(state,"RejectedPosture")?.ToString(),
                        rejectedBodyYawDegrees=Get(state,"RejectedBodyYawDegrees"),placementDiagnostics=Items(state,"PlacementDiagnostics"),
                        wholeGesture=Gesture(state),
                        fields=fields.Keys.Cast<string>().ToDictionary(k=>k,k=>fields[k]),featureWarnings=Items(state,"FeatureWarnings"),placementWarnings=Items(state,"PlacementWarnings")};Save();
                    if(Get(state,"CommandPoint")==null)continue;
                    var profile=T("MaintenanceAutoconfiguration").GetMethod("ReadSavedProfile",Hidden).Invoke(null,new object[] {source,state});row["profile"]=Profile(profile);
                    watch.Restart();var placement=T("MaintenanceOperatorPlacement").GetMethod("Suggest",Hidden).Invoke(null,new object[] {document,source,profile,progress});
                    row["placementSeconds"]=watch.Elapsed.TotalSeconds;row["placement"]=Placement(placement);
                    var invariants=new List<string>();var invariantFailures=new List<string>();row["wholeGestureChecks"]=invariants;row["wholeGestureFailures"]=invariantFailures;
                    void Invariant(bool condition,string label){if(condition)invariants.Add(label);else invariantFailures.Add(label);}
                    bool checkedAll=Get(placement,"WholeGestureChecked")is bool checkedFlag&&checkedFlag;
                    bool clearAll=Get(placement,"WholeGestureClear")is bool clearFlag&&clearFlag;
                    var alternativeClear=Items(placement,"Alternatives").Any(a=>Get(a,"WholeGestureChecked")is bool checkedAlternative&&checkedAlternative
                        &&Get(a,"WholeGestureClear")is bool clearAlternative&&clearAlternative);
                    Invariant(!clearAll||checkedAll&&Items(placement,"GestureObstacles").Length==0&&Items(placement,"FixedObstacles").Length==0,
                        "Whole-gesture-clear requires a performed check and no known fixed/arm obstruction");
                    Invariant(!alternativeClear||clearAll,"A clear checked alternative is preferred over a colliding alternative");
                    if(Get(placement,"FootPoint")!=null)Invariant(checkedAll,"A proposed body position has also been checked for the full operating gesture");
                    if(clearAll&&(Items(placement,"GestureUnknown").Length>0||Items(placement,"ProvisionalUnknown").Length>0))
                        Invariant(Get(placement,"Confidence")?.ToString()!="GeometryChecked","Unresolved model geometry keeps a clear-known gesture provisional");
                    if(item.Id==1014886L)Invariant(Items(placement,"FixedObstacles").Any(o=>Get(o,"Role")?.ToString()=="ExtractionSpace")&&!clearAll,
                        "The recorded fixed filter withdrawal conflict remains explicit instead of being hidden by moving the body");
                    if(item.Id==950354L)Invariant(Get(placement,"FootPoint")==null&&Items(placement,"Warnings").Length>0,
                        "The recorded high actuator cannot gain an unsupported floating work position");
                    if(Get(state,"Operation").ToString()=="ChainwheelValve")
                    {
                        var command=(XYZ)Get(state,"CommandPoint");double drop=(double)Get(profile,"ChainDrop");
                        row["chainLoop"]=new {wheelCenterZM=command.Z*.3048,modeledBottomZM=(command.Z-drop)*.3048,dropMm=drop*304.8,
                            spacingMm=(double)Get(profile,"ChainSpacing")*304.8,footClearanceMm=Get(placement,"FootPoint")is XYZ foot?(double?)((command.Z-drop-foot.Z)*304.8):null};
                    }
                    if(app!=null)Visual(assembly,document,source,state,app,folder,row,Save);
                }
                catch(Exception error){row["error"]=Error(error);}
                finally{Save();}
            }
            output["passed"]=rows.All(r=>!r.ContainsKey("error")&&!r.ContainsKey("missing")
                &&(!(r.TryGetValue("wholeGestureFailures",out var failures)&&failures is List<string> list)||list.Count==0)
                &&(!(r.TryGetValue("visual",out var visual)&&visual is Dictionary<string,object> result)||!result.TryGetValue("passed",out var passed)||passed is bool ok&&ok));
            output["completedUtc"]=DateTime.UtcNow;Save();File.WriteAllText(Path.Combine(folder,"case-stage.txt"),"cases-done");
        }
    }
}

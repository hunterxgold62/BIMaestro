using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Transform = Autodesk.Revit.DB.Transform;

namespace BIMaestro.MotorTests
{
    // Called only by the explicit disposable-document test harness. All fixture
    // geometry is distant from the model and is rolled back, even on failure.
    internal static class MaintenancePlacementNativeChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static double Mm(double value) => value / 304.8;
        private static object Field(object target, string name) => target.GetType().GetField(name, Hidden).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden).Invoke(target, args);
        private static IEnumerable<object> Items(object target, string name) => ((IEnumerable)Field(target, name)).Cast<object>();

        private static CurveLoop Rectangle(XYZ center, double width, double depth)
        {
            var points = new[] { center + new XYZ(-width / 2, -depth / 2, 0), center + new XYZ(width / 2, -depth / 2, 0),
                center + new XYZ(width / 2, depth / 2, 0), center + new XYZ(-width / 2, depth / 2, 0) };
            var loop = new CurveLoop();
            for (int i = 0; i < points.Length; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Length]));
            return loop;
        }
        private static Solid Box(XYZ center, XYZ size)
        {
            using (var loop = Rectangle(center - XYZ.BasisZ * size.Z / 2, size.X, size.Y))
                return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, size.Z);
        }

        internal static void Run(Assembly assembly, Document doc, FamilyInstance source, string folder)
        {
            var checks = new List<string>();
            var report = new Dictionary<string, object>();
            var temporaryIds = new List<ElementId>();
            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
                checks.Add(message);
            }
            Type Type(string name) => assembly.GetType("BIMaestro.MepBooster." + name, true);
            var placementType = Type("MaintenanceOperatorPlacement");
            object Place(string method, object profile) => placementType.GetMethod(method, Hidden).Invoke(null, new object[] { doc, source, profile, null });
            bool Walking(Element element) => (bool)placementType.GetMethod("IsWalkingSurface", Hidden).Invoke(null, new object[] { element });
            bool Reference(Element element) => (bool)placementType.GetMethod("IsReferenceElement", Hidden).Invoke(null, new object[] { element });
            var site = new XYZ(20000, 20000, 0);
            object Profile(double floorHeight = 0)
            {
                var operation = Enum.Parse(Type("MaintenanceAccessOperation"), "HandwheelValve");
                var profile = Type("MaintenanceAccessProfile").GetMethod("For", Hidden).Invoke(null, new[] { operation });
                var frame = Transform.Identity;
                frame.Origin = site + XYZ.BasisZ * (floorHeight + Mm(1200));
                frame.BasisX = XYZ.BasisX; frame.BasisY = XYZ.BasisZ; frame.BasisZ = -XYZ.BasisY;
                Set(profile, "Frame", frame); Set(profile, "HandleRadius", Mm(100));
                Set(profile, "OperatorOrigin", null);
                return profile;
            }
            object Summary(object result)
            {
                var p = (XYZ)Field(result, "FootPoint");
                var support = Field(result, "Support");
                return new { foot = p == null ? null : new[] { p.X, p.Y, p.Z }, confidence = Field(result, "Confidence").ToString(),
                    support = support == null ? null : Field(support, "Label"), warnings = Items(result, "Warnings").ToArray(),
                    provisionalUnknown = Items(result, "ProvisionalUnknown").ToArray(),
                    wholeGestureChecked=Field(result,"WholeGestureChecked"),wholeGestureClear=Field(result,"WholeGestureClear"),searchLimited=Field(result,"SearchLimited"),
                    gestureUnknown=Items(result,"GestureUnknown").ToArray(),
                    gestureObstacles=Items(result,"GestureObstacles").Select(b=>new{label=Field(b,"Label"),id=Field(b,"ElementId"),link=Field(b,"LinkId"),role=Field(b,"Role").ToString(),volume=Field(b,"VolumeLabel")}).ToArray(),
                    fixedObstacles=Items(result,"FixedObstacles").Select(b=>new{label=Field(b,"Label"),id=Field(b,"ElementId"),link=Field(b,"LinkId"),role=Field(b,"Role").ToString(),volume=Field(b,"VolumeLabel")}).ToArray(),
                    posture = Field(result,"SuggestedPosture").ToString(),bodyYawDegrees=Field(result,"SuggestedBodyYawDegrees"),
                    postureWarnings=Items(result,"PostureWarnings").ToArray(),rejectedFoot=Field(result,"RejectedFoot")is XYZ rejected?new[]{rejected.X,rejected.Y,rejected.Z}:null,
                    blocking=Items(result,"BlockingObstacles").Select(b=>new{label=Field(b,"Label"),id=Field(b,"ElementId"),link=Field(b,"LinkId"),volume=Field(b,"VolumeLabel")}).ToArray(),
                    suggestedWorkHeightMm = (double)Field(result, "SuggestedWorkHeight") * 304.8,
                    diagnostics = Items(result, "Diagnostics").ToArray(), alternatives = Items(result, "Alternatives").Count() };
            }
            bool BodyIntersects(object profile, Element obstacle)
            {
                var scene = Type("MaintenanceAccessGeometry").GetMethod("Build", Hidden).Invoke(null, new[] { profile, (object)0.0 });
                foreach(var body in Items(scene, "Boxes").Where(b => (bool)Field(b, "CheckCollision") && Field(b, "Role").ToString() == "OperatorSpace"))
                {
                var parts=((IEnumerable)Call(body,"CollisionParts")).Cast<Solid>().ToList();
                try{
                foreach(var volume in parts)
                using (var options = new Options { DetailLevel = ViewDetailLevel.Fine })
                using (var geometry = obstacle.get_Geometry(options))
                    foreach (var solid in geometry.OfType<Solid>().Where(s => s.Volume > 1e-12))
                        using (var overlap = BooleanOperationsUtils.ExecuteBooleanOperation(volume, solid, BooleanOperationsType.Intersect))
                            if (overlap.Volume > 1e-10) return true;
                }finally{foreach(var part in parts)part.Dispose();}
                }
                return false;
            }
            void PostureFixtures(double floorTop)
            {
                DirectShape Block(XYZ center,XYZ size)
                {
                    var block=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));temporaryIds.Add(block.Id);
                    using(var solid=Box(center,size))block.SetShape(new GeometryObject[]{solid});doc.Regenerate();return block;
                }
                var foot=site+new XYZ(0,-Mm(250),floorTop);
                var side=Profile(floorTop);Set(side,"OperatorOrigin",foot);Set(side,"BodyYawDegrees",90.0);
                var frame=(Transform)Field(side,"Frame");frame.Origin=site+XYZ.BasisZ*(floorTop+Mm(1700)*.81);
                var scene=Type("MaintenanceAccessGeometry").GetMethod("Build",Hidden).Invoke(null,new[]{side,(object)0.0});
                var points=Items(scene,"Boxes").Where(b=>(bool)Field(b,"CheckCollision")&&Field(b,"Role").ToString()=="OperatorSpace")
                    .SelectMany(b=>((IEnumerable)Call(b,"BoundsCorners")).Cast<XYZ>()).ToArray();
                double middle=(points.Min(p=>p.X)+points.Max(p=>p.X))/2;
                var walls=new[]{Block(new XYZ(middle-Mm(250),foot.Y,floorTop+Mm(1000)),new XYZ(Mm(100),Mm(3000),Mm(2000))),
                    Block(new XYZ(middle+Mm(250),foot.Y,floorTop+Mm(1000)),new XYZ(Mm(100),Mm(3000),Mm(2000)))};
                Check(!walls.Any(w=>BodyIntersects(side,w)),"Native full articulated profile body including shoes clears the400mm corridor");
                var sideResult=Place("Validate",side);report["physical_profile_corridor"]=Summary(sideResult);
                Check(Field(sideResult,"FootPoint")!=null&&Items(sideResult,"PostureWarnings").Any(),"A reachable profile intervention on real supported feet is geometrically possible with a separate posture constraint");
                Set(side,"BodyYawDegrees",0.0);
                Check(walls.Any(w=>BodyIntersects(side,w)),"Native standing torso really intersects the same narrow corridor walls");
                var faceResult=Place("Validate",side);report["physical_face_corridor_refused"]=Summary(faceResult);
                Check(Field(faceResult,"FootPoint")==null&&Field(faceResult,"RejectedFoot")!=null&&Items(faceResult,"BlockingObstacles").Any(),
                    "An actual blocked body exposes a refused pose and native-proven obstacle identities instead of only a global AABB");
                foreach(var wall in walls)doc.Delete(wall.Id);doc.Regenerate();
                var gap=Block(foot+new XYZ(0,Mm(100),Mm(300)),new XYZ(Mm(80),Mm(80),Mm(80)));
                Check(!BodyIntersects(side,gap),"A small solid between the legs is not a collision with a fictitious full-body rectangle");
                Check(Field(Place("Validate",side),"FootPoint")!=null,"Placement also preserves the actual empty gap between articulated legs");
                doc.Delete(gap.Id);doc.Regenerate();
                var ceiling=Block(site+XYZ.BasisZ*(floorTop+Mm(1450)),new XYZ(Mm(3000),Mm(3000),Mm(100)));
                var squat=Profile(floorTop);Set(squat,"Operation",Enum.Parse(Type("MaintenanceAccessOperation"),"Filter"));
                Set(squat,"OperatorOrigin",foot);Set(squat,"OperatorPosture",Enum.Parse(Type("MaintenanceOperatorPosture"),"Crouched"));
                Set(squat,"RemovalTravel",Mm(150));Set(squat,"RemovalDirection",-XYZ.BasisY);Set(squat,"FollowRemoval",false);
                ((Transform)Field(squat,"Frame")).Origin=site+XYZ.BasisZ*(floorTop+Mm(650));
                Check(!BodyIntersects(squat,ceiling),"Native articulated crouch including head clears the1400mm overhead obstruction");
                var squatResult=Place("Validate",squat);report["physical_crouch_low_withdrawal"]=Summary(squatResult);
                Check(Field(squatResult,"FootPoint")!=null&&Items(squatResult,"PostureWarnings").Any(),"A low withdrawal has a reachable crouched pose with real floor support and an explicit constraint note");
                Set(squat,"OperatorPosture",Enum.Parse(Type("MaintenanceOperatorPosture"),"Standing"));
                Check(BodyIntersects(squat,ceiling),"The same full standing body cannot be accepted under the low ceiling");
                doc.Delete(ceiling.Id);doc.Regenerate();
            }
            void WholeGestureFixtures(double floorTop,XYZ originalFoot)
            {
                DirectShape Block(XYZ center,XYZ size)
                {
                    var block=DirectShape.CreateElement(doc,new ElementId(BuiltInCategory.OST_GenericModel));temporaryIds.Add(block.Id);
                    using(var solid=Box(center,size))block.SetShape(new GeometryObject[]{solid});doc.Regenerate();return block;
                }
                var profile=Profile(floorTop);Set(profile,"OperatorOrigin",originalFoot);
                var baseline=Place("Validate",profile);
                Check((bool)Field(baseline,"WholeGestureChecked")&&(bool)Field(baseline,"WholeGestureClear"),
                    "A clear real-floor baseline checks the entire gesture, not only the supported body");
                var scene=Type("MaintenanceAccessGeometry").GetMethod("Build",Hidden).Invoke(null,new[]{profile,(object)0.0});
                var right=Items(scene,"Lines").First(l=>(string)Field(l,"Label")=="Bras droit");
                var elbow=((XYZ[])Field(right,"Points"))[1];
                var armBlock=Block(elbow,new XYZ(Mm(30),Mm(30),Mm(30)));
                Check(!BodyIntersects(profile,armBlock),"The gesture regression obstacle occupies the right arm while leaving the articulated body free");
                var armRefused=Place("Validate",profile);report["whole_gesture_arm_only"]=Summary(armRefused);
                Check(Field(armRefused,"FootPoint")!=null&&(bool)Field(armRefused,"WholeGestureChecked")&&!(bool)Field(armRefused,"WholeGestureClear"),
                    "Manual validation retains the useful supported posture but refuses a whole-gesture clear verdict when an arm collides");
                Check(Items(armRefused,"GestureObstacles").Any(o=>(long)Field(o,"ElementId")==armBlock.Id.Value&&Field(o,"Role").ToString()=="HumanArm")
                    &&!Items(armRefused,"FixedObstacles").Any(),"An arm-only blocker exposes its exact element identity and role without becoming a fixed mechanism conflict");
                Set(profile,"OperatorOrigin",null);
                var alternative=Place("Suggest",profile);report["whole_gesture_alternative"]=Summary(alternative);
                Check(Field(alternative,"FootPoint")is XYZ other&&(other-originalFoot).GetLength()>Mm(10)
                    &&(bool)Field(alternative,"WholeGestureClear")&&!Items(alternative,"GestureObstacles").Any(),
                    "Automatic search moves to a genuinely clear alternative instead of stopping at a body-clear arm-blocked posture");
                doc.Delete(armBlock.Id);doc.Regenerate();
                var fixedBlock=Block(((Transform)Field(profile,"Frame")).Origin,new XYZ(Mm(20),Mm(20),Mm(20)));
                var fixedResult=Place("Suggest",profile);report["whole_gesture_fixed_conflict"]=Summary(fixedResult);
                Check(Field(fixedResult,"FootPoint")!=null&&(bool)Field(fixedResult,"WholeGestureChecked")&&!(bool)Field(fixedResult,"WholeGestureClear")
                    &&Items(fixedResult,"FixedObstacles").Any(o=>(long)Field(o,"ElementId")==fixedBlock.Id.Value),
                    "A fixed wheel conflict is reported once independently of the useful operator posture and never yields whole-gesture clear");
                doc.Delete(fixedBlock.Id);doc.Regenerate();
                try
                {
                    placementType.GetMethod("SuggestControlled",Hidden).Invoke(null,new object[]{doc,source,profile,null,(Func<bool>)(()=>true)});
                    Check(false,"Cancellation must escape controlled posture search");
                }
                catch(TargetInvocationException ex)when(ex.InnerException is OperationCanceledException)
                { Check(true,"Controlled posture search propagates cancellation instead of converting it into missing geometry"); }
            }
            try
            {
                Check(source != null, "Placement fixture has a production FamilyInstance source");
                Check(!doc.IsModifiable, "Placement fixture starts outside another transaction");
                var viewerOriginal=doc.GetElement(new ElementId(953028L))as FamilyInstance;
                Check(viewerOriginal!=null,"Positive viewer fixture has the real chain-family source");
                var viewerFeature=Type("MaintenanceFeatureDetection").GetMethod("Detect",Hidden).Invoke(null,new object[] {doc,viewerOriginal,Enum.Parse(Type("MaintenanceAccessOperation"),"ChainwheelValve")});
                Check(Field(viewerFeature,"CommandPoint")!=null&&Field(viewerFeature,"Frame")!=null,"Positive viewer fixture has a measured command frame before starting its transaction");
                var projectPoint = BasePoint.GetProjectBasePoint(doc);
                Check(projectPoint == null || Reference(projectPoint), "Project base point is a reference datum, never a physical body obstacle");
                var surveyPoint = BasePoint.GetSurveyPoint(doc);
                Check(surveyPoint == null || Reference(surveyPoint), "Survey point is a reference datum, never a physical body obstacle");
                Check(doc.SiteLocation == null || Reference(doc.SiteLocation), "SiteLocation metadata is excluded from physical body obstacles");
                Check(doc.ActiveProjectLocation == null || Reference(doc.ActiveProjectLocation), "ProjectLocation metadata is excluded from physical body obstacles");
                Check(doc.ProjectInformation == null || Reference(doc.ProjectInformation), "ProjectInfo metadata is excluded from physical body obstacles");
                foreach (long id in new[] { 567835L, 690789L })
                {
                    var originalDatum = doc.GetElement(new ElementId(id));
                    if (originalDatum != null) report["original_" + id] = new { type = originalDatum.GetType().FullName, category = originalDatum.Category?.Name,
                        name = originalDatum.Name, reference = Reference(originalDatum) };
                }
                var empty = Place("Suggest", Profile());
                report["no_floor"] = Summary(empty);
                Check(Field(empty, "FootPoint") == null, "No floor at isolated XY returns no proposed feet in the air");

                using (var transaction = new Transaction(doc, "Temporary automatic operator placement checks"))
                {
                    transaction.Start();
                    try
                    {
                        var level = Level.Create(doc, 0); temporaryIds.Add(level.Id);
                        Check(Reference(level), "Level datum is excluded from body obstacles");
                        var grid = Autodesk.Revit.DB.Grid.Create(doc, Line.CreateBound(site + new XYZ(-Mm(3000), 0, 0), site + new XYZ(Mm(3000), 0, 0)));
                        temporaryIds.Add(grid.Id);
                        Check(Reference(grid), "Grid datum is excluded from body obstacles");
                        Element pipeFixture;
                        double pipeTop;
                        var pipeCategory = new ElementId(BuiltInCategory.OST_PipeCurves);
                        if (DirectShape.IsValidCategoryId(pipeCategory, doc))
                        {
                            var shape = DirectShape.CreateElement(doc, pipeCategory); temporaryIds.Add(shape.Id);
                            using (var solid = Box(site + XYZ.BasisZ * Mm(600), new XYZ(Mm(3500), Mm(3500), Mm(200))))
                                shape.SetShape(new GeometryObject[] { solid });
                            pipeFixture = shape;
                            report["pipe_fixture_kind"] = "flat DirectShape in OST_PipeCurves";
                        }
                        else
                        {
                            var system = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().First();
                            var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
                            var pipe = Pipe.Create(doc, system.Id, pipeType.Id, level.Id, site + new XYZ(-Mm(1800), 0, Mm(500)), site + new XYZ(Mm(1800), 0, Mm(500)));
                            temporaryIds.Add(pipe.Id);
                            pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(Mm(600));
                            pipeFixture = pipe;
                            report["pipe_fixture_kind"] = "real Pipe fallback; DirectShape rejects OST_PipeCurves on this installation";
                        }
                        doc.Regenerate();
                        pipeTop = pipeFixture.get_BoundingBox(null).Max.Z;
                        Check(!Walking(pipeFixture), "A pipe category is never an allowed walking support even with a broad horizontal top");
                        Check(!Reference(pipeFixture), "Physical pipes remain physical obstacles after datum filtering");
                        var onPipeOnly = Place("Suggest", Profile(pipeTop));
                        report["pipe_only"] = Summary(onPipeOnly);
                        Check(Field(onPipeOnly, "FootPoint") == null, "A pipe without a real floor never creates a proposed standing position");
                        var manualPipeProfile = Profile(pipeTop);
                        Set(manualPipeProfile, "OperatorOrigin", site + XYZ.BasisZ * pipeTop);
                        var manualPipe = Place("Validate", manualPipeProfile);
                        report["manual_pipe_without_floor"] = Summary(manualPipe);
                        Check(Field(manualPipe, "FootPoint") == null, "Manual feet on a pipe are refused instead of treating its top as a floor");

                        // Remove the non-walking object to keep the positive floor
                        // case independent of its physical obstruction.
                        doc.Delete(pipeFixture.Id);
                        var floorType = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t => !t.IsFoundationSlab);
                        Floor floor;
                        using (var outline = Rectangle(site, Mm(5000), Mm(5000)))
                            floor = Floor.Create(doc, new[] { outline }, floorType.Id, level.Id);
                        temporaryIds.Add(floor.Id);
                        doc.Regenerate();
                        double floorTop = floor.get_BoundingBox(null).Max.Z;
                        Check(Walking(floor), "A real Floor is recognized as an eligible walking surface");
                        Check(!Reference(floor), "Physical floors remain in capture after datum filtering");
                        var floorProfile = Profile(floorTop);
                        var onFloor = Place("Suggest", floorProfile);
                        report["real_floor"] = Summary(onFloor);
                        var firstFoot = (XYZ)Field(onFloor, "FootPoint");
                        Check(firstFoot != null, "A clear real floor at standing reach yields a proposed position");
                        Check(Math.Abs(firstFoot.Z - floorTop) < Mm(2), "Proposed feet lie on the actual Floor top elevation");
                        var support = Field(onFloor, "Support");
                        Check(support != null && ((ElementId)Field(support, "ElementId")) == floor.Id,
                            "The chosen support identifies the actual Floor element");
                        Check(((ElementId)Field(support, "LinkId")) == ElementId.InvalidElementId,
                            "Host floor support is not reported as a linked element");
                        Set(floorProfile, "OperatorOrigin", firstFoot);
                        var manualFloor = Place("Validate", floorProfile);
                        report["manual_real_floor"] = Summary(manualFloor);
                        Check(Field(manualFloor, "FootPoint") != null, "The proposed real-floor position also passes manual validation");
                        CheckUncertainBodyObstacles(assembly, floorProfile, Check);
                        PostureFixtures(floorTop);
                        WholeGestureFixtures(floorTop,firstFoot);

                        // A real, model-wide import of 3D curves has an AABB,
                        // but supplies no physical solid or triangle. It must
                        // keep a visible uncertainty, not invent a collision.
                        var cad = ImportUncertainCad(doc, site, floorTop, folder);
                        temporaryIds.Add(cad.Id); doc.Regenerate();
                        report["uncertain_import_fixture"] = ImportSummary(cad);
                        Set(floorProfile, "OperatorOrigin", firstFoot);
                        Check(BodyOverlapsImportBounds(assembly, floorProfile, cad), "The imported CAD fixture's actual world bounding box overlaps the preferred operator body");
                        Check(!Walking(cad), "An uncertain CAD import can never supply the standing support");
                        Check(!(bool)placementType.GetMethod("IsPlanarReferenceImport", Hidden).Invoke(null, new object[] { cad, null }),
                            "Three-dimensional CAD linework is not silently classified as a harmless 2D plan");
                        Set(floorProfile, "OperatorOrigin", null);
                        var provisional = Place("Suggest", floorProfile);
                        report["uncertain_import_on_real_floor"] = Summary(provisional);
                        var provisionalFoot = (XYZ)Field(provisional, "FootPoint");
                        Check(provisionalFoot != null, "A geometry-free import AABB allows a provisional position on the real Floor");
                        Check(Field(provisional, "Confidence").ToString() == "LimitedGeometry", "A provisional CAD position can never claim GeometryChecked confidence");
                        Check(Items(provisional, "ProvisionalUnknown").Any(), "The chosen provisional position retains the overlapping import identity");
                        Check(Items(provisional, "Warnings").Cast<string>().Any(w => w.IndexOf("provisoire", StringComparison.OrdinalIgnoreCase) >= 0),
                            "The provisional CAD position has a persistent explicit warning");
                        Check(Items(provisional, "Alternatives").All(a => Items(a, "ProvisionalUnknown").Any()),
                            "Every returned alternative under the same import keeps its own uncertainty");
                        Set(floorProfile, "OperatorOrigin", provisionalFoot);
                        var provisionalManual = Place("Validate", floorProfile);
                        report["uncertain_import_manual"] = Summary(provisionalManual);
                        Check(Field(provisionalManual, "FootPoint") != null && Field(provisionalManual, "Confidence").ToString() == "LimitedGeometry"
                            && Items(provisionalManual, "ProvisionalUnknown").Any(), "Manual validation preserves the provisional status instead of hiding CAD uncertainty");
                        using (var noFloor = new SubTransaction(doc))
                        {
                            noFloor.Start(); doc.Delete(floor.Id); doc.Regenerate();
                            Set(floorProfile, "OperatorOrigin", null);
                            var noSupport = Place("Suggest", floorProfile);
                            report["uncertain_import_without_floor"] = Summary(noSupport);
                            Check(Field(noSupport, "FootPoint") == null, "An uncertain import AABB without a real floor still returns no feet in the air");
                            noFloor.RollBack(); doc.Regenerate();
                        }
                        CapturePositiveViewer(assembly, doc, viewerOriginal, viewerFeature, site, floorTop, folder, report, temporaryIds, Check, true);
                        doc.Delete(cad.Id); doc.Regenerate();
                        var smallCad = ImportUncertainCad(doc, new XYZ(firstFoot.X, firstFoot.Y, 0), floorTop, folder, 150);
                        temporaryIds.Add(smallCad.Id); doc.Regenerate();
                        report["small_import_fixture"] = ImportSummary(smallCad);
                        Set(floorProfile, "OperatorOrigin", firstFoot);
                        Check(BodyOverlapsImportBounds(assembly, floorProfile, smallCad), "The small import's actual world bounding box overlaps the former preferred body");
                        var formerPreferred = Place("Validate", floorProfile);
                        report["small_import_former_preferred"] = Summary(formerPreferred);
                        Check(Field(formerPreferred, "FootPoint") != null && Items(formerPreferred, "ProvisionalUnknown").Any(),
                            "The small import really makes the former preferred body position provisional");
                        Set(floorProfile, "OperatorOrigin", null);
                        var lessUncertain = Place("Suggest", floorProfile);
                        report["small_import_prefer_clear_candidate"] = Summary(lessUncertain);
                        Check(Field(lessUncertain, "FootPoint") != null && !Items(lessUncertain, "ProvisionalUnknown").Any(),
                            "A candidate clear of the uncertain import is preferred over the closer provisional position");
                        doc.Delete(smallCad.Id); doc.Regenerate();

                        var chainProfile = Profile(floorTop);
                        Set(chainProfile, "Operation", Enum.Parse(Type("MaintenanceAccessOperation"), "ChainwheelValve"));
                        var chainFrame = Transform.Identity;
                        chainFrame.Origin = site + XYZ.BasisZ * (floorTop + Mm(3300));
                        chainFrame.BasisX = XYZ.BasisX; chainFrame.BasisY = XYZ.BasisZ; chainFrame.BasisZ = -XYZ.BasisY;
                        Set(chainProfile, "Frame", chainFrame); Set(chainProfile, "ChainDrop", Mm(2200));
                        Set(chainProfile, "ChainSpacing", Mm(180)); Set(chainProfile, "ChainPullStroke", Mm(350));
                        var chainPlacement = Place("Suggest", chainProfile);
                        report["chain_grip_auto"] = Summary(chainPlacement);
                        var chainFoot = (XYZ)Field(chainPlacement, "FootPoint");
                        Check(chainFoot != null, "A reachable elevated chain loop gets a real-floor operator position");
                        Check(Math.Abs((double)Field(chainPlacement, "SuggestedWorkHeight") - Mm(1520)) < Mm(1),
                            "Automatic chain grip is raised geometrically to allow the full requested pull stroke");
                        Check(Math.Abs((double)Field(chainProfile, "WorkHeight") - Mm(1200)) < Mm(1),
                            "Suggest does not mutate the original configured chain grip height");
                        Check(Items(chainPlacement, "Alternatives").All(a => Math.Abs((double)Field(a, "SuggestedWorkHeight") - Mm(1520)) < Mm(1)),
                            "Each chain alternative carries its own grip height for the returned floor");
                        Set(chainProfile, "OperatorOrigin", chainFoot);
                        var chainManual = Place("Validate", chainProfile);
                        report["chain_grip_manual"] = Summary(chainManual);
                        Check(Field(chainManual, "FootPoint") != null && Math.Abs((double)Field(chainManual, "SuggestedWorkHeight") - Mm(1200)) < Mm(1),
                            "Manual validation preserves the explicitly configured grip without hidden adjustment");
                        CapturePositiveViewer(assembly,doc,viewerOriginal,viewerFeature,site,floorTop,folder,report,temporaryIds,Check);

                        // A manual selection at the former pipe elevation is
                        // still rejected even with a real floor beneath it.
                        var elevatedProfile = Profile(floorTop + Mm(700));
                        Set(elevatedProfile, "OperatorOrigin", new XYZ(firstFoot.X, firstFoot.Y, floorTop + Mm(700)));
                        var elevated = Place("Validate", elevatedProfile);
                        report["manual_above_real_floor"] = Summary(elevated);
                        Check(Field(elevated, "FootPoint") == null, "Manual elevated feet do not snap through 700 mm of air to the floor below");

                        var blocker = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                        temporaryIds.Add(blocker.Id);
                        using (var solid = Box(firstFoot + XYZ.BasisZ * Mm(850), new XYZ(Mm(700), Mm(700), Mm(1700))))
                            blocker.SetShape(new GeometryObject[] { solid });
                        doc.Regenerate();
                        Check(!Walking(blocker), "An equipment-like block is an obstacle, never a walking support");
                        Check(!Reference(blocker), "Physical GenericModel blocks are never excluded as reference data");
                        Check(BodyIntersects(floorProfile, blocker), "Fixture blocker really intersects the previously preferred operator body");
                        Set(floorProfile, "OperatorOrigin", null);
                        var obstructed = Place("Suggest", floorProfile);
                        report["preferred_side_blocked"] = Summary(obstructed);
                        var otherFoot = (XYZ)Field(obstructed, "FootPoint");
                        if (otherFoot != null)
                        {
                            Set(floorProfile, "OperatorOrigin", otherFoot);
                            Check((otherFoot - firstFoot).GetLength() > Mm(100), "Blocking the preferred side produces a different standing position");
                            Check(Math.Abs(otherFoot.Z - floorTop) < Mm(2), "The alternative stays on the real floor rather than standing on the block");
                            Check(!BodyIntersects(floorProfile, blocker), "Native solid intersection confirms the alternative body is outside the blocker");
                        }
                        else Check(true, "With the preferred side blocked, safe refusal is permitted instead of body penetration");
                        foreach (var alternative in Items(obstructed, "Alternatives"))
                        {
                            Set(floorProfile, "OperatorOrigin", (XYZ)Field(alternative, "FootPoint"));
                            Check(!BodyIntersects(floorProfile, blocker), "Every returned alternative also avoids the blocking solid");
                        }
                    }
                    finally
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                    }
                }
                Check(temporaryIds.All(id => doc.GetElement(id) == null), "All temporary placement fixtures and levels were rolled back");
                report["passed"] = true;
            }
            catch (Exception ex)
            { report["passed"] = false; report["error"] = ex.ToString(); }
            report["checks"] = checks;
            report["scope"] = "Opt-in native fixture on the disposable copy; no document saved, all synthetic geometry rolled back.";
            File.WriteAllText(Path.Combine(folder, "maintenance-placement-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static ImportInstance ImportUncertainCad(Document doc, XYZ site, double floorTop, string folder, double extentMm = 3000)
        {
            var dxf = new List<string> { "0", "SECTION", "2", "HEADER", "9", "$ACADVER", "1", "AC1009", "0", "ENDSEC", "0", "SECTION", "2", "ENTITIES" };
            void Line(XYZ a, XYZ b)
            {
                dxf.AddRange(new[] { "0", "LINE", "8", "0", "10", a.X.ToString("R", CultureInfo.InvariantCulture), "20", a.Y.ToString("R", CultureInfo.InvariantCulture),
                    "30", a.Z.ToString("R", CultureInfo.InvariantCulture), "11", b.X.ToString("R", CultureInfo.InvariantCulture), "21", b.Y.ToString("R", CultureInfo.InvariantCulture),
                    "31", b.Z.ToString("R", CultureInfo.InvariantCulture) });
            }
            // Import near the CAD origin, then explicitly locate the resulting
            // Revit geometry. ImportPlacement.Origin can use the document's
            // shared/project origin; it is not proof of internal coordinates.
            Line(new XYZ(-Mm(extentMm), -Mm(extentMm), Mm(100)), new XYZ(Mm(extentMm), Mm(extentMm), Mm(2200)));
            Line(new XYZ(-Mm(extentMm), Mm(extentMm), Mm(2200)), new XYZ(Mm(extentMm), -Mm(extentMm), Mm(100)));
            dxf.AddRange(new[] { "0", "ENDSEC", "0", "EOF" });
            string path = Path.Combine(folder, "placement-uncertain-3d-lines-" + extentMm.ToString("0", CultureInfo.InvariantCulture) + ".dxf");
            File.WriteAllLines(path, dxf, System.Text.Encoding.ASCII);
            using (var options = new DWGImportOptions { Unit = ImportUnit.Foot, Placement = ImportPlacement.Origin, ThisViewOnly = false, OrientToView = false })
            {
                if (!doc.Import(path, options, doc.ActiveView, out ElementId id)) throw new InvalidOperationException("Could not import the native 3D-line CAD uncertainty fixture.");
                doc.Regenerate();
                var import = (ImportInstance)doc.GetElement(id);
                // Revit pins newly imported CAD instances by default. Only
                // this disposable fixture is unpinned, inside the rollback.
                if (import.Pinned) import.Pinned = false;
                using (var box = import.get_BoundingBox(null))
                {
                    if (box == null) throw new InvalidOperationException("The imported CAD fixture has no bounding box.");
                    var actual = box.Transform.OfPoint((box.Min + box.Max) / 2);
                    var desired = new XYZ(site.X, site.Y, floorTop + Mm(1150));
                    ElementTransformUtils.MoveElement(doc, id, desired - actual);
                }
                doc.Regenerate(); return import;
            }
        }

        private static object ImportSummary(ImportInstance import)
        {
            using (var box = import.get_BoundingBox(null))
                return new { id = import.Id.Value, viewSpecific = import.ViewSpecific, ownerViewId = import.OwnerViewId.Value,
                    transformOrigin = new[] { import.GetTransform().Origin.X, import.GetTransform().Origin.Y, import.GetTransform().Origin.Z },
                    min = box == null ? null : new[] { box.Transform.OfPoint(box.Min).X, box.Transform.OfPoint(box.Min).Y, box.Transform.OfPoint(box.Min).Z },
                    max = box == null ? null : new[] { box.Transform.OfPoint(box.Max).X, box.Transform.OfPoint(box.Max).Y, box.Transform.OfPoint(box.Max).Z } };
        }
        private static bool BodyOverlapsImportBounds(Assembly assembly, object profile, ImportInstance import)
        {
            var scene = assembly.GetType("BIMaestro.MepBooster.MaintenanceAccessGeometry", true).GetMethod("Build", Hidden).Invoke(null, new[] { profile, (object)0.0 });
            var points = Items(scene, "Boxes").Where(b => (bool)Field(b, "CheckCollision") && Field(b, "Role").ToString() == "OperatorSpace")
                .SelectMany(body=>((IEnumerable)Call(body, "BoundsCorners")).Cast<XYZ>()).ToArray();
            using (var box = import.get_BoundingBox(null))
            {
                if (box == null) return false;
                var a = box.Transform.OfPoint(box.Min); var b = box.Transform.OfPoint(box.Max);
                return a.X <= points.Max(p => p.X) && b.X >= points.Min(p => p.X)
                    && a.Y <= points.Max(p => p.Y) && b.Y >= points.Min(p => p.Y)
                    && a.Z <= points.Max(p => p.Z) && b.Z >= points.Min(p => p.Z);
            }
        }

        private static void CheckUncertainBodyObstacles(Assembly assembly, object profile, Action<bool, string> check)
        {
            Type Type(string name) => assembly.GetType("BIMaestro.MepBooster." + name, true);
            var placement = Type("MaintenanceOperatorPlacement");
            var snapshotType = placement.GetNestedType("Snapshot", Hidden);
            var obstacleType = placement.GetNestedType("BodyObstacle", Hidden);
            var snapshot = Activator.CreateInstance(snapshotType, true);
            var scene = Type("MaintenanceAccessGeometry").GetMethod("Build", Hidden).Invoke(null, new[] { profile, (object)0.0 });
            var body = Items(scene, "Boxes").First(b => (bool)Field(b, "CheckCollision") && Field(b, "Role").ToString() == "OperatorSpace"&&(Field(b,"Label")as string).StartsWith("Buste"));
            var corners = ((IEnumerable)Call(body, "BoundsCorners")).Cast<XYZ>().ToArray();
            var lo = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z));
            var hi = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z));
            Set(snapshot, "Lo", lo); Set(snapshot, "Hi", hi);
            var obstacles = (IList)Field(snapshot, "Obstacles");
            object Obstacle(bool import, bool incomplete)
            {
                obstacles.Clear(); ((IDictionary)Field(snapshot, "BodyIndex")).Clear();
                var item = Activator.CreateInstance(obstacleType, true);
                Set(item, "Lo", lo); Set(item, "Hi", hi); Set(item, "Label", "Native fixture");
                Set(item, "Incomplete", incomplete); Set(item, "BoundingBoxOnlyImport", import);
                obstacles.Add(item); Call(snapshot, "Index"); return item;
            }
            bool Fast(out int unknown)
            {
                var args = new[] { snapshot, body, null, null };
                bool clear = (bool)placement.GetMethod("FastBodyClear", Hidden).Invoke(null, args);
                unknown = ((IEnumerable)args[3]).Cast<object>().Count(); return clear;
            }
            Obstacle(true, true);
            check(Fast(out int uncertain) && uncertain == 1, "Only a geometry-free import placeholder becomes provisional rather than a proven body collision");
            Obstacle(false, true);
            check(!Fast(out _), "An unknown family or equipment placeholder remains a blocking uncertainty");
            var mesh = Obstacle(true, true);
            var center = (lo + hi) / 2;
            object Vector(XYZ p) => Activator.CreateInstance(Type("MotorVector"), Hidden, null, new object[] { p.X, p.Y, p.Z }, null);
            var triangle = Activator.CreateInstance(Type("MotorTriangle"), Hidden, null, new[] { Vector(center + new XYZ(-1, -1, 0)), Vector(center + new XYZ(1, -1, 0)), Vector(center + new XYZ(0, 1, 0)) }, null);
            ((IList)Field(mesh, "Triangles")).Add(triangle);
            check(!Fast(out _), "Known mesh triangles still block even if an import placeholder flag was mistakenly set");
            var solid = Obstacle(true, false);
            using (var volume = Box(center, new XYZ(Mm(100), Mm(100), Mm(100))))
            {
                Set(solid, "Solid", volume);
                check(!Fast(out int solidUnknown) && solidUnknown == 0, "A known CAD solid is confirmed as a physical collision, never downgraded to an unverified bounding-box marker");
                var args = new[] { snapshot, body, null };
                check(!(bool)placement.GetMethod("PreciseBodyClear", Hidden).Invoke(null, args), "A native intersection with a known CAD solid still refuses the operator position");
            }
        }

        private static void CapturePositiveViewer(Assembly assembly,Document doc,FamilyInstance original,object feature,XYZ site,double floorTop,
            string folder,Dictionary<string,object> report,List<ElementId> temporaryIds,Action<bool,string> check,bool provisional=false)
        {
            string prefix=provisional?"provisional_viewer":"positive_viewer";
            string imagePrefix=provisional?"placement-provisional-chain":"placement-positive-chain";
            Type Type(string name)=>assembly.GetType("BIMaestro.MepBooster."+name,true);
            double drop=(double)Field(feature,"ChainDrop");
            var originalCommand=(XYZ)Field(feature,"CommandPoint");
            var desiredCommand=site+XYZ.BasisZ*(floorTop+drop+Mm(1100));
            var copiedIds=ElementTransformUtils.CopyElement(doc,original.Id,desiredCommand-originalCommand);
            temporaryIds.AddRange(copiedIds);
            var source=copiedIds.Select(doc.GetElement).OfType<FamilyInstance>().First(e=>e.Symbol.Id==original.Symbol.Id);
            doc.Regenerate();
            var detected=(Transform)Field(feature,"Frame");
            var frame=Transform.Identity;frame.Origin=desiredCommand;
            frame.BasisX=detected.BasisX;frame.BasisY=detected.BasisY;frame.BasisZ=detected.BasisZ;
            var state=Activator.CreateInstance(Type("MaintenanceWorkbenchState"),true);
            Set(state,"Operation",Enum.Parse(Type("MaintenanceAccessOperation"),"ChainwheelValve"));
            Set(state,"CommandPoint",desiredCommand);Set(state,"DetectedFrame",frame);Set(state,"Axis",6);Set(state,"Measured",true);
            var fields=(IDictionary)Field(state,"Fields");
            void Number(string name,double value)=>fields[name]=value.ToString("0.###",CultureInfo.CurrentCulture);
            Number("Radius",(double)Field(feature,"Radius")*304.8);Number("Thickness",(double)Field(feature,"HandleThickness")*304.8);
            Number("ChainDrop",drop*304.8);Number("ChainSpacing",(double)Field(feature,"ChainSpacing")*304.8);
            Number("Angle",360);Number("WorkHeight",1200);Number("Pull",350);
            var profile=Type("MaintenanceAutoconfiguration").GetMethod("ReadSavedProfile",Hidden).Invoke(null,new object[] {source,state});
            var placement=Type("MaintenanceOperatorPlacement").GetMethod("Suggest",Hidden).Invoke(null,new object[] {doc,source,profile,null});
            var foot=(XYZ)Field(placement,"FootPoint");
            report[prefix+"_placement"]=new {foot=foot==null?null:new[] {foot.X,foot.Y,foot.Z},warnings=Items(placement,"Warnings").ToArray(),
                confidence=Field(placement,"Confidence").ToString(),provisionalUnknown=Items(placement,"ProvisionalUnknown").ToArray(),
                diagnostics=Items(placement,"Diagnostics").ToArray(),workHeightMm=(double)Field(placement,"SuggestedWorkHeight")*304.8};
            check(foot!=null,"Positive real-chain viewer fixture has a supported and reachable operator position");
            if(provisional)check(Field(placement,"Confidence").ToString()=="LimitedGeometry"&&Items(placement,"ProvisionalUnknown").Any(),
                "Provisional real-chain viewer starts with an explicit uncertain import overlap");
            Set(state,"FootPoint",foot);Set(state,"SupportVerified",Field(placement,"Confidence").ToString()=="GeometryChecked");
            Set(state,"OperatorPosture",Field(placement,"SuggestedPosture"));Set(state,"BodyYawDegrees",Field(placement,"SuggestedBodyYawDegrees"));
            foreach(var warning in Items(placement,"PostureWarnings"))((IList)Field(state,"PostureWarnings")).Add(warning);
            Set(state,"SupportNote","Synthetic real-floor fixture; copied real chain family, not a maintenance verdict for the original model.");
            foreach(var warning in Items(placement,"Warnings"))((IList)Field(state,"PlacementWarnings")).Add(warning);
            Number("WorkHeight",(double)Field(placement,"SuggestedWorkHeight")*304.8);
            Window window=null;
            try
            {
                var app=new UIApplication(doc.Application);
                window=(Window)Activator.CreateInstance(Type("MaintenanceWorkbenchWindow"),Hidden,null,new object[] {doc,source,state,app.MainWindowHandle},null);
                window.Loaded+=(_,__)=>window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async()=>
                {
                    try
                    {
                        ((System.Windows.Controls.ComboBox)Field(window,"_mode")).SelectedIndex=0;Call(window,"Draw");
                        window.UpdateLayout();
                        CheckOverlayFraming(window, prefix, report, check);
                        SaveWindowImage(window,Path.Combine(folder,imagePrefix+".png"));
                        var scene=Field(window,"_lastScene");
                        check(scene!=null&&Items(scene,"Boxes").Any(b=>Field(b,"Role").ToString()=="HumanBody"),
                            "Positive viewer renders a real operator rather than an empty no-foot preview");
                        if(provisional)
                        {
                            var provisionalPreview=Field(window,"_preview");
                            check(!(bool)Field(state,"SupportVerified")&&Items(provisionalPreview,"_boxSlots").Any(b=>Field(b,"Shape")as string=="torso"
                                &&Field(b,"Color")is System.Windows.Media.Color color&&color==Colors.DarkOrange),
                                "A real-floor provisional operator reaches the actual WPF preview as an amber torso");
                            var volumes=(CheckBox)Field(window,"_showVolumes");int plain=Items(provisionalPreview,"_boxSlots").Count();
                            volumes.IsChecked=true;Call(window,"Draw");
                            check(Items(provisionalPreview,"_boxSlots").Count()>plain,"Showing volumes adds the real controlled boxes around the provisional operator");
                            SaveWindowImage(window,Path.Combine(folder,imagePrefix+"-volumes.png"));volumes.IsChecked=false;
                            ((System.Windows.Controls.ComboBox)Field(window,"_provenance")).SelectedIndex=2;((CheckBox)Field(window,"_confirmed")).IsChecked=true;
                            Call(window,"Analyze");string status=((TextBlock)Field(window,"_status")).Text;
                            report[prefix+"_analysis"]=new {status,supportVerified=(bool)Field(state,"SupportVerified"),warnings=Items(state,"PlacementWarnings").ToArray()};
                            check(Field(window,"_report")!=null,"The production scanner completes on the real provisional operator fixture");
                            check(!(bool)Field(state,"SupportVerified")&&Items(state,"PlacementWarnings").Any()&&!status.StartsWith("Libre",StringComparison.Ordinal),
                                "Confirming dimensions and family provenance never clears CAD uncertainty or produces Libre for a provisional operator");
                        }
                        var slider=(Slider)Field(window,"_phase");var costs=new List<double>();
                        for(int i=0;i<120;i++){var watch=Stopwatch.StartNew();slider.Value=i*100.0/119;costs.Add(watch.Elapsed.TotalMilliseconds);}
                        for(int i=0;i<60;i++){slider.Value=i*100.0/59;await Task.Delay(33);}
                        var preview=Field(window,"_preview");
                        object Property(string name)=>preview.GetType().GetProperty(name,Hidden)?.GetValue(preview);
                        report[prefix+"_performance"]=new {poseMeanMs=costs.Average(),poseP95Ms=costs.OrderBy(v=>v).ElementAt(114),
                            renderFramesPerSecond=Property("RenderFramesPerSecond"),sceneUpdatesPerSecond=Property("SceneUpdatesPerSecond"),renderingTier=Property("RenderingTier"),
                            scope="Real copied chain geometry and an operator on the temporary real Floor; read-only WPF animation after capture."};
                        SaveWindowImage(window,Path.Combine(folder,imagePrefix+"-end.png"));
                    }
                    catch(Exception ex){report[prefix+"_error"]=ex.ToString();}
                    finally{window.Close();}
                }));
                window.ShowDialog();
                check(!report.ContainsKey(prefix+"_error"),(provisional?"Provisional":"Positive")+" viewer and animated human complete without a UI error");
            }
            finally{if(window?.IsVisible==true)window.Close();}
            // Keep subsequent isolated-body fixtures independent of this copy.
            if(source.Pinned)source.Pinned=false;
            doc.Delete(source.Id);doc.Regenerate();
        }

        private static void CheckOverlayFraming(Window window, string prefix, Dictionary<string, object> report, Action<bool, string> check)
        {
            var preview = Field(window, "_preview");
            var viewport = (Viewport3D)Field(preview, "_viewport");
            var camera = (OrthographicCamera)viewport.Camera;
            var bounds = (Rect3D)Field(preview, "_overlayBounds");
            double width = viewport.ActualWidth, height = viewport.ActualHeight;
            check(!bounds.IsEmpty && width > 1 && height > 1 && camera.Width > 0,
                "The loaded real operator preview has nonempty overlay bounds and a measured visible viewport");
            var forward = camera.LookDirection; forward.Normalize();
            var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
            var up = Vector3D.CrossProduct(right, forward); up.Normalize();
            double viewHeight = camera.Width * height / width;
            var pixels = Enumerable.Range(0, 8).Select(i =>
            {
                // Overlay coordinates are already WPF-local. Do not subtract
                // the Revit document origin again when projecting the camera.
                var point = new Point3D(bounds.X + ((i & 1) == 0 ? 0 : bounds.SizeX), bounds.Y + ((i & 2) == 0 ? 0 : bounds.SizeY),
                    bounds.Z + ((i & 4) == 0 ? 0 : bounds.SizeZ));
                if (camera.Transform?.Inverse is Transform3D inverse) point = inverse.Transform(point);
                var offset = point - camera.Position;
                return new { x = width / 2 + Vector3D.DotProduct(offset, right) * width / camera.Width,
                    y = height / 2 - Vector3D.DotProduct(offset, up) * height / viewHeight, depth = Vector3D.DotProduct(offset, forward) };
            }).ToArray();
            report[prefix + "_initial_framing"] = new { viewportWidth = width, viewportHeight = height, cameraWidth = camera.Width,
                minX = pixels.Min(p => p.x), maxX = pixels.Max(p => p.x), minY = pixels.Min(p => p.y), maxY = pixels.Max(p => p.y), corners = pixels };
            check(pixels.All(p => p.x >= -1 && p.x <= width + 1 && p.y >= -1 && p.y <= height + 1
                && p.depth >= camera.NearPlaneDistance - 1e-6 && p.depth <= camera.FarPlaneDistance + 1e-6),
                "The initial real preview frames the entire operator and command inside the visible viewport without clipping feet or head");
        }

        private static void SaveWindowImage(Window window,string path)
        {
            window.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(path))encoder.Save(file);
        }
    }
}

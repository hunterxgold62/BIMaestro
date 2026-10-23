using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal enum MaintenancePlacementConfidence { None, LimitedGeometry, GeometryChecked }

    internal sealed class MaintenanceOperatorSupport
    {
        internal string Label, LinkName;
        internal ElementId ElementId, LinkId;
    }
    internal sealed class MaintenanceOperatorAlternative
    {
        internal XYZ FootPoint;
        internal MaintenanceOperatorSupport Support;
        internal double Score;
        internal double SuggestedWorkHeight;
        internal MaintenanceOperatorPosture SuggestedPosture;
        internal double SuggestedBodyYawDegrees;
        internal readonly List<string> PostureWarnings = new List<string>();
        internal readonly List<string> ProvisionalUnknown = new List<string>();
        internal bool WholeGestureChecked, WholeGestureClear;
        internal readonly List<string> GestureUnknown = new List<string>();
        internal readonly List<MaintenanceAccessObstacle> GestureObstacles = new List<MaintenanceAccessObstacle>();
    }
    internal sealed class MaintenanceOperatorPlacementResult
    {
        internal XYZ FootPoint;
        internal MaintenanceOperatorSupport Support;
        internal MaintenancePlacementConfidence Confidence;
        internal double SuggestedWorkHeight;
        internal MaintenanceOperatorPosture SuggestedPosture;
        internal double SuggestedBodyYawDegrees;
        internal XYZ RejectedFoot;
        internal MaintenanceOperatorPosture RejectedPosture;
        internal double RejectedBodyYawDegrees;
        internal readonly List<string> PostureWarnings = new List<string>();
        internal readonly List<MaintenanceAccessObstacle> BlockingObstacles = new List<MaintenanceAccessObstacle>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> Assumptions = new List<string>();
        internal readonly List<string> Diagnostics = new List<string>();
        internal readonly List<string> ProvisionalUnknown = new List<string>();
        internal readonly List<MaintenanceOperatorAlternative> Alternatives = new List<MaintenanceOperatorAlternative>();
        internal bool WholeGestureChecked, WholeGestureClear, SearchLimited;
        internal readonly List<string> GestureUnknown = new List<string>();
        internal readonly List<MaintenanceAccessObstacle> GestureObstacles = new List<MaintenanceAccessObstacle>();
        internal readonly List<MaintenanceAccessObstacle> FixedObstacles = new List<MaintenanceAccessObstacle>();
        internal MaintenanceOperatorFailureReason NoOperatorReasonCode;
        internal string NoOperatorReason;
    }

    // One detached geometry capture, reused for all candidate positions. This
    // proposes a standing work position; it does not find an approach route,
    // certify the supporting structure or infer a ladder/platform.
    internal static class MaintenanceOperatorPlacement
    {
        private static double Mm(double value) => value / 304.8;
        private const double CellSize = 3.280839895013123; // 1 m, spatial index only.
        private static readonly HashSet<long> WalkingCategories = WalkingCategoryIds();

        internal static bool IsWalkingSurface(Element element)
            => element?.Category != null && WalkingCategories.Contains(BoosterIds.Value(element.Category.Id));

        internal static bool IsReferenceElement(Element element)
            => element is BasePoint || element is DatumPlane || element is SiteLocation || element is ProjectLocation
                || element is ProjectInfo || element is View || element is SpatialElement;

        internal static bool IsPlanarReferenceImport(Element element, Transform toWorld = null)
        {
            if (!(element is ImportInstance import)) return false;
            if (import.ViewSpecific) return true;
            var linework = new MaintenanceHorizontalLinework();
            var transform = toWorld ?? Transform.Identity;
            try
            {
                using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false })
                using (var geometry = import.get_Geometry(options)) Read(geometry);
                return linework.IsHorizontal2D(Mm(1));
            }
            catch (Exception ex) when (Recoverable(ex)) { return false; }
            void Read(GeometryElement geometry)
            {
                if (geometry == null) { linework.RejectNonLinework(); return; }
                foreach (var item in geometry)
                {
                    if (!linework.CanRead) return;
                    if (item is GeometryInstance instance) { using (var expanded = instance.GetInstanceGeometry()) Read(expanded); }
                    else ObserveLinework(item, transform, linework);
                }
            }
        }
        private static void ObserveLinework(GeometryObject item, Transform transform, MaintenanceHorizontalLinework linework)
        {
            IList<XYZ> points = item is Curve curve ? curve.Tessellate() : item is PolyLine polyline ? polyline.GetCoordinates() : null;
            if (points == null) { linework.RejectNonLinework(); return; }
            linework.AddPrimitive(points.Select(p => Vector(transform.OfPoint(p))));
        }

        private static HashSet<long> WalkingCategoryIds()
        {
            var ids = new HashSet<long>();
            // Runtime names preserve Revit 2023 compilation: Toposolid was
            // introduced later. Equipment foundations and pipe categories are
            // deliberately absent, even when their tops are horizontal.
            foreach (string name in new[] { "OST_Floors", "OST_Stairs", "OST_StairsRuns", "OST_StairsLandings", "OST_Ramps", "OST_Topography", "OST_Toposolid" })
                if (Enum.TryParse(name, out BuiltInCategory category)) ids.Add((long)category);
            return ids;
        }

        internal static MaintenanceOperatorPlacementResult Suggest(Document doc, FamilyInstance source,
            MaintenanceAccessProfile profile, Action<string> progress = null)
            => Place(doc, source, profile, false, progress, null);

        internal static MaintenanceOperatorPlacementResult Validate(Document doc, FamilyInstance source,
            MaintenanceAccessProfile profile, Action<string> progress = null)
            => Place(doc, source, profile, true, progress, null);

        // Distinct names preserve the four-argument reflection contract used by
        // existing add-ins and disposable native harnesses.
        internal static MaintenanceOperatorPlacementResult SuggestControlled(Document doc, FamilyInstance source,
            MaintenanceAccessProfile profile, Action<string> progress, Func<bool> canceled)
            => Place(doc, source, profile, false, progress, canceled);
        internal static MaintenanceOperatorPlacementResult ValidateControlled(Document doc, FamilyInstance source,
            MaintenanceAccessProfile profile, Action<string> progress, Func<bool> canceled)
            => Place(doc, source, profile, true, progress, canceled);

        private sealed class SupportTriangle
        {
            internal MotorTriangle Triangle;
            internal MaintenanceOperatorSupport Support;
        }
        private sealed class BodyObstacle
        {
            internal Solid Solid;
            internal XYZ Lo, Hi;
            internal string Label;
            internal long ElementId, LinkId = -1;
            internal string LinkName;
            internal List<MotorTriangle> Triangles = new List<MotorTriangle>();
            internal bool Incomplete, WalkingMesh, BoundingBoxOnlyImport;
        }
        private sealed class Candidate
        {
            internal XYZ Foot;
            internal MaintenanceOperatorSupport Support;
            internal List<MaintenanceAccessBox> Bodies;
            internal double Score, WorkHeight;
            internal MaintenanceOperatorPosture Posture;
            internal double BodyYawDegrees;
            internal readonly List<string> PostureWarnings = new List<string>();
            internal readonly List<string> ProvisionalUnknown = new List<string>();
            internal GestureEvaluation Gesture;
        }
        private sealed class Snapshot : IDisposable
        {
            internal XYZ Lo, Hi;
            internal readonly List<SupportTriangle> Supports = new List<SupportTriangle>();
            internal readonly List<BodyObstacle> Obstacles = new List<BodyObstacle>();
            internal readonly Dictionary<long, List<SupportTriangle>> SupportIndex = new Dictionary<long, List<SupportTriangle>>();
            internal readonly Dictionary<long, List<BodyObstacle>> BodyIndex = new Dictionary<long, List<BodyObstacle>>();
            internal readonly List<string> Unknown = new List<string>();
            internal readonly List<string> Assumptions = new List<string>();
            internal readonly List<string> Diagnostics = new List<string>();
            internal readonly HashSet<long> SourceIds = new HashSet<long>();
            internal void Index()
            {
                foreach (var support in Supports)
                    for (int x = Cell(Math.Max(Lo.X, support.Triangle.Min.X)); x <= Cell(Math.Min(Hi.X, support.Triangle.Max.X)); x++)
                    for (int y = Cell(Math.Max(Lo.Y, support.Triangle.Min.Y)); y <= Cell(Math.Min(Hi.Y, support.Triangle.Max.Y)); y++)
                    {
                        long key = Key(x, y);
                        if (!SupportIndex.TryGetValue(key, out var list)) SupportIndex[key] = list = new List<SupportTriangle>();
                        list.Add(support);
                    }
                foreach (var obstacle in Obstacles)
                    for (int x = Cell(Math.Max(Lo.X, obstacle.Lo.X)); x <= Cell(Math.Min(Hi.X, obstacle.Hi.X)); x++)
                    for (int y = Cell(Math.Max(Lo.Y, obstacle.Lo.Y)); y <= Cell(Math.Min(Hi.Y, obstacle.Hi.Y)); y++)
                    {
                        long key = Key(x, y);
                        if (!BodyIndex.TryGetValue(key, out var list)) BodyIndex[key] = list = new List<BodyObstacle>();
                        list.Add(obstacle);
                    }
            }
            public void Dispose() { foreach (var obstacle in Obstacles) obstacle.Solid?.Dispose(); }
        }

        private sealed class SearchLimitException : Exception { }
        private sealed class SearchControl
        {
            private readonly MaintenanceAccessCallbackGuard _callbacks = new MaintenanceAccessCallbackGuard();
            private readonly Action<string> _progress;
            private readonly Func<bool> _canceled;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private long _lastProgress, _searchStarted = -1;
            internal int Intersections;
            internal SearchControl(Action<string> progress, Func<bool> canceled) { _progress=progress; _canceled=canceled; }
            internal bool IsCallbackFailure(Exception error) => _callbacks.IsCallbackFailure(error);
            internal void StartSearch() { _searchStarted = _clock.ElapsedMilliseconds; }
            internal void Check(string message = null)
            {
                _callbacks.CheckCanceled(_canceled);
                // A limit bounds optimisation, never the checked scope of a
                // returned candidate. Partially evaluated candidates are dropped.
                if (_searchStarted >= 0 && (_clock.ElapsedMilliseconds-_searchStarted>25000 || Intersections>100000))
                    throw new SearchLimitException();
                if(message!=null&&_clock.ElapsedMilliseconds-_lastProgress>=150)
                { _lastProgress=_clock.ElapsedMilliseconds; _callbacks.Report(_progress,message); }
            }
        }
        private sealed class GestureEvaluation
        {
            internal readonly List<MaintenanceAccessObstacle> Obstacles = new List<MaintenanceAccessObstacle>();
            internal readonly List<string> Unknown = new List<string>();
            internal readonly List<BodyObstacle> UnknownSources = new List<BodyObstacle>();
            internal void AddUnknown(BodyObstacle obstacle,string message)
            {
                if(!UnknownSources.Any(o=>o.ElementId==obstacle.ElementId&&o.LinkId==obstacle.LinkId))UnknownSources.Add(obstacle);
                if(!Unknown.Contains(message))Unknown.Add(message);
            }
        }
        private sealed class GestureVolume : IDisposable
        {
            internal MaintenanceAccessBox Box;
            internal XYZ Lo, Hi;
            private IList<Solid> _parts;
            internal IList<Solid> Parts => _parts ?? (_parts=Box.CollisionParts());
            internal GestureVolume(MaintenanceAccessBox box)
            { Box=box; var points=box.BoundsCorners(); Lo=Minimum(points); Hi=Maximum(points); }
            public void Dispose() { if(_parts!=null)foreach(var part in _parts)part.Dispose(); }
        }

        // This is the same functional grouping as the final scanner. A merged
        // group is only a negative broad-phase: a positive result must always
        // be confirmed against the actual continuous interval volumes.
        private static GestureEvaluation EvaluateGesture(Snapshot snapshot, IEnumerable<MaintenanceAccessBox> boxes, SearchControl control)
        {
            var result=new GestureEvaluation();
            var seen=new HashSet<string>();
            foreach(var group in boxes.GroupBy(b=>new{b.Role,b.Label,b.CheckSource}))
            {
                var volumes=group.Select(b=>new GestureVolume(b)).ToArray();
                if(volumes.Length==0)continue;
                var points=volumes.SelectMany(v=>v.Box.BoundsCorners()).ToArray();
                var first=volumes[0].Box;
                var local=points.Select(first.Frame.Inverse.OfPoint).ToArray();
                using(var broad=new GestureVolume(new MaintenanceAccessBox {Label=first.Label,Role=first.Role,Frame=first.Frame,
                    Center=(Minimum(local)+Maximum(local))/2,Size=Maximum(local)-Minimum(local)}))
                try
                {
                    foreach(var obstacle in BodyCandidates(snapshot,Minimum(points),Maximum(points)))
                    {
                        control.Check();
                        if(!first.CheckSource&&obstacle.LinkId<0&&snapshot.SourceIds.Contains(obstacle.ElementId))continue;
                        string key=obstacle.LinkId+":"+obstacle.ElementId+":"+first.Role+":"+first.Label;
                        if(seen.Contains(key))continue;
                        if(obstacle.Solid==null)
                        {
                            string warning=obstacle.Label+" : géométrie indéterminée pour « "+first.Label+" ».";
                            result.AddUnknown(obstacle,warning);
                            continue;
                        }
                        if(obstacle.Incomplete)result.AddUnknown(obstacle,obstacle.Label+" : géométrie partiellement exploitable pour « "+first.Label+" ».");
                        bool broadFailed;
                        if(volumes.Length>1&&!NativeIntersects(broad,obstacle,control,out broadFailed)&&!broadFailed)continue;
                        foreach(var volume in volumes)
                        {
                            if(!Overlap(volume.Lo,volume.Hi,obstacle.Lo,obstacle.Hi))continue;
                            if(NativeIntersects(volume,obstacle,control,out bool failed))
                            { seen.Add(key); result.Obstacles.Add(AsObstacle(obstacle,first)); break; }
                            if(failed)
                            {
                                string warning=obstacle.Label+" : contrôle indéterminé pour « "+first.Label+" ».";
                                result.AddUnknown(obstacle,warning);
                            }
                        }
                    }
                }
                finally { foreach(var volume in volumes)volume.Dispose(); }
            }
            return result;
        }
        private static bool NativeIntersects(GestureVolume volume,BodyObstacle obstacle,SearchControl control,out bool failed)
        {
            failed=false;
            try
            {
                foreach(var part in volume.Parts)
                {
                    control.Check();control.Intersections++;
                    using(var intersection=BooleanOperationsUtils.ExecuteBooleanOperation(part,obstacle.Solid,BooleanOperationsType.Intersect))
                        if(intersection.Volume>1e-10)return true;
                }
            }
            catch(Exception ex)when(!control.IsCallbackFailure(ex)&&Recoverable(ex)){failed=true;}
            return false;
        }
        private static MaintenanceAccessObstacle AsObstacle(BodyObstacle obstacle,MaintenanceAccessBox volume)
            => new MaintenanceAccessObstacle {Label=obstacle.Label,VolumeLabel=volume.Label,Role=volume.Role,
                ElementId=obstacle.ElementId,LinkId=obstacle.LinkId,LinkName=obstacle.LinkName,
                Box=Enumerable.Range(0,8).Select(i=>new XYZ((i&1)==0?obstacle.Lo.X:obstacle.Hi.X,
                    (i&2)==0?obstacle.Lo.Y:obstacle.Hi.Y,(i&4)==0?obstacle.Lo.Z:obstacle.Hi.Z)).ToArray()};

        private static MaintenanceOperatorPlacementResult Place(Document doc, FamilyInstance source,
            MaintenanceAccessProfile profile, bool manual, Action<string> progress, Func<bool> canceled)
        {
            if (doc == null || source == null || profile == null) throw new ArgumentNullException("Document, équipement et profil nécessaires.");
            profile.Validate();
            var result = new MaintenanceOperatorPlacementResult { SuggestedWorkHeight = profile.WorkHeight,
                SuggestedPosture=profile.OperatorPosture,SuggestedBodyYawDegrees=profile.BodyYawDegrees };
            void NoOperator(MaintenanceOperatorFailureReason reason, string detail = null)
            {
                result.NoOperatorReasonCode = reason;
                result.NoOperatorReason = MaintenanceOperatorFailure.Describe(reason, detail);
                if (!string.IsNullOrWhiteSpace(result.NoOperatorReason)) result.Warnings.Add(result.NoOperatorReason);
            }
            if (manual && profile.OperatorOrigin == null)
            { NoOperator(MaintenanceOperatorFailureReason.MissingFootPoint); return result; }
            var control=new SearchControl(progress,canceled);
            control.Check();
            var fixedBoxes=MaintenanceAccessGeometry.Build(profile,0).CollisionBoxes.Where(b=>!b.DependsOnOperator).ToArray();
            var command = profile.WorldCenter;
            double radius = Math.Min(Mm(3000), Math.Max(Mm(850), profile.OperatorHeight * .34 + profile.HandleRadius));
            var travel = FollowShift(profile);
            double down = profile.OperatorHeight * 1.3 + (profile.Operation == MaintenanceAccessOperation.ChainwheelValve ? profile.ChainDrop : profile.HandleRadius);
            double up = profile.OperatorHeight + Math.Max(profile.HandleRadius, Math.Max(0, profile.Frame.OfVector(profile.RemovalDirection).Z * profile.RemovalTravel));
            var basePoint = manual ? profile.OperatorOrigin : command;
            var xy = new[] { command, basePoint, command + travel, basePoint + travel }.Concat(fixedBoxes.SelectMany(b=>b.BoundsCorners())).ToArray();
            using (var bounds = new Outline(new XYZ(xy.Min(p => p.X) - radius - Math.Max(profile.OperatorWidth,profile.OperatorHeight*.4), xy.Min(p => p.Y) - radius - Math.Max(profile.OperatorDepth,profile.OperatorHeight*.4),
                Math.Min(xy.Min(p=>p.Z),Math.Min(command.Z - down, basePoint.Z - (manual ? Mm(200) : down)))),
                new XYZ(xy.Max(p => p.X) + radius + Math.Max(profile.OperatorWidth,profile.OperatorHeight*.4), xy.Max(p => p.Y) + radius + Math.Max(profile.OperatorDepth,profile.OperatorHeight*.4), Math.Max(xy.Max(p=>p.Z),Math.Max(command.Z, basePoint.Z) + up))))
            using (var snapshot = Capture(doc, bounds, control))
            {
                void AddSource(FamilyInstance item)
                {
                    if(!snapshot.SourceIds.Add(BoosterIds.Value(item.Id)))return;
                    foreach(var id in item.GetSubComponentIds())if(doc.GetElement(id)is FamilyInstance child)AddSource(child);
                }
                AddSource(source);
                snapshot.Index();
                result.Assumptions.AddRange(snapshot.Assumptions);
                result.Diagnostics.AddRange(snapshot.Diagnostics);
                if (snapshot.Supports.Count == 0)
                {
                    NoOperator(MaintenanceOperatorFailureReason.NoWalkingSurface);
                    result.Warnings.AddRange(snapshot.Unknown.Distinct().Take(8));
                    return result;
                }
                var fixedGesture=EvaluateGesture(snapshot,fixedBoxes,control);
                result.FixedObstacles.AddRange(fixedGesture.Obstacles);
                control.StartSearch();
                var candidates = new List<Candidate>();
                // A CAD container covering the complete search region and all
                // possible head elevations cannot be escaped by rotating or
                // crouching. Do not repeat 10 posture searches merely to seek
                // a lower uncertainty count that geometry makes impossible.
                double supportLo=snapshot.Supports.Min(s=>s.Triangle.Min.Z),supportHi=snapshot.Supports.Max(s=>s.Triangle.Max.Z);
                var unavoidableSources=new HashSet<BodyObstacle>(snapshot.Obstacles.Where(o=>o.BoundingBoxOnlyImport&&o.Solid==null&&o.Triangles.Count==0
                    &&o.Lo.X<=snapshot.Lo.X&&o.Hi.X>=snapshot.Hi.X&&o.Lo.Y<=snapshot.Lo.Y&&o.Hi.Y>=snapshot.Hi.Y
                    &&o.Lo.Z<=supportLo+profile.OperatorHeight*.61&&o.Hi.Z>=supportHi+profile.OperatorHeight));
                int unavoidableImports=unavoidableSources.Count;
                int attempted = 0, supported = 0, reachable = 0, blocked = 0, preciseRejected = 0, gestureChecked=0, unverifiedBodies=0;
                string rejected = null, lastReachRejection = null, lastBodyRejection = null, lastUnverifiedBody = null;
                double minimumStartHeight=double.PositiveInfinity,maximumStartHeight=double.NegativeInfinity;
                var rejectionNotes = new List<string>();
                var floorLevels = new SortedSet<double>();
                double rejectedScore=double.PositiveInfinity;
                var sourceLevel=doc.GetElement(source.LevelId) as Level;
                try
                {
                foreach(var configuration in Configurations(profile,manual))
                {
                foreach (var point in manual ? new[] { profile.OperatorOrigin } : SearchPoints(profile, radius))
                {
                    attempted++;control.Check("Recherche du geste complet : "+attempted+" positions, "+gestureChecked+" gestes contrôlés…");
                    double minFloor = manual ? point.Z - Mm(20) : command.Z - down;
                    double maxFloor = manual ? point.Z + Mm(20) : command.Z + profile.HandleRadius;
                    var hits = Heights(snapshot, point.X, point.Y, minFloor, maxFloor)
                        .OrderByDescending(h => h.Height).ToList();
                    foreach (var hit in hits)
                    {
                        floorLevels.Add(Math.Round(hit.Height, 4));
                        var foot = new XYZ(point.X, point.Y, manual ? point.Z : hit.Height);
                        var candidateProfile=Copy(profile,foot);
                        candidateProfile.OperatorPosture=configuration.Posture;candidateProfile.BodyYawDegrees=configuration.Yaw;
                        if (!Supported(snapshot, candidateProfile, foot, manual, out var levelFoot)) continue;
                        supported++;
                        minimumStartHeight=Math.Min(minimumStartHeight,command.Z-levelFoot.Z);
                        maximumStartHeight=Math.Max(maximumStartHeight,command.Z-levelFoot.Z);
                        candidateProfile.OperatorOrigin=levelFoot;
                        if (!manual && profile.Operation == MaintenanceAccessOperation.ChainwheelValve)
                            candidateProfile.WorkHeight = MaintenancePlacementMath.ChainWorkHeight(profile.WorkHeight, command.Z, levelFoot.Z,
                                profile.ChainDrop, profile.GripClearance, profile.ChainPullStroke);
                        if (candidateProfile.WorkHeight > Mm(20000))
                        { lastReachRejection = "La hauteur de prise nécessaire sort du gabarit de visualisation disponible."; continue; }
                        var scene = MaintenanceAccessGeometry.Build(candidateProfile, 0);
                        if(!manual&&profile.Operation==MaintenanceAccessOperation.ChainwheelValve&&!scene.Reachable)
                        {
                            double minimum=command.Z-profile.ChainDrop-levelFoot.Z+profile.GripClearance+profile.ChainPullStroke;
                            double maximum=command.Z-levelFoot.Z-profile.GripClearance;
                            double centered=MaintenanceAccessGeometry.ShoulderHeight(candidateProfile)+profile.ChainPullStroke/2;
                            if(minimum<=maximum)
                            {
                                double originalHeight=candidateProfile.WorkHeight;
                                double alternate=Math.Max(minimum,Math.Min(maximum,centered));
                                if(alternate<=Mm(20000)&&Math.Abs(alternate-originalHeight)>Mm(1))
                                {
                                    candidateProfile.WorkHeight=alternate;
                                    var adjusted=MaintenanceAccessGeometry.Build(candidateProfile,0);
                                    if(adjusted.Reachable)scene=adjusted;else candidateProfile.WorkHeight=originalHeight;
                                }
                            }
                        }
                        if (!scene.Reachable) { lastReachRejection = "Le point de prise ou une partie du geste dépasse la portée du mannequin dans la posture testée."; continue; }
                        if (profile.Operation == MaintenanceAccessOperation.ChainwheelValve && command.Z - profile.ChainDrop < levelFoot.Z + Mm(50))
                        { lastReachRejection = "La boucle de chaîne se trouve au niveau ou sous le sol ; ajustez sa longueur réelle."; continue; }
                        reachable++;
                        var bodies = scene.CollisionBoxes.Where(b => b.Role == MaintenanceAccessRole.OperatorSpace).ToList();
                        var provisional = new List<string>();
                        bool bodyClear=bodies.Count>0;
                        MaintenanceAccessObstacle proved=null;
                        bool bodyUnverified=false;
                        foreach(var body in bodies)
                        {
                            control.Check();
                            if(!CheckBody(snapshot,body,out rejected,out var unknown,out proved,out bodyUnverified)){bodyClear=false;break;}
                            foreach(var label in unknown)if(!provisional.Contains(label))provisional.Add(label);
                        }
                        if (!bodyClear)
                        {
                            blocked++; lastBodyRejection = rejected ?? "Volume du corps indisponible.";
                            if(bodyUnverified){unverifiedBodies++;lastUnverifiedBody=lastBodyRejection;}
                            if (!rejectionNotes.Contains(lastBodyRejection)) rejectionNotes.Add(lastBodyRejection);
                            double rank=(configuration.Posture==MaintenanceOperatorPosture.Crouched?10:0)+Math.Abs(configuration.Yaw)/90
                                +Math.Abs(Horizontal(levelFoot-command).GetLength()-Mm(450));
                            if(proved!=null&&rank<rejectedScore)
                            {
                                rejectedScore=rank;result.RejectedFoot=levelFoot;result.RejectedPosture=configuration.Posture;
                                result.RejectedBodyYawDegrees=configuration.Yaw;result.BlockingObstacles.Clear();result.BlockingObstacles.Add(proved);
                            }
                            continue;
                        }
                        var outward = Horizontal(profile.Frame.BasisZ);
                        var offset = Horizontal(levelFoot - command);
                        double side = outward.GetLength() < 1e-8 || offset.GetLength() < 1e-8 ? 0 : -offset.Normalize().DotProduct(outward.Normalize()) * .1;
                        double score = Math.Abs(offset.GetLength() - Mm(450)) + side;
                        if (sourceLevel!=null) score += Math.Abs(levelFoot.Z - sourceLevel.Elevation) * .15;
                        // Confirm every body against native solids once before
                        // spending work on hands and arms. Broad triangles never
                        // constitute the final proof of free space.
                        if(bodies.Any(body=>!PreciseBodyClearDetailed(snapshot,body,out rejected,out bodyUnverified)))
                        { preciseRejected++;lastBodyRejection=rejected;if(bodyUnverified){unverifiedBodies++;lastUnverifiedBody=rejected;}continue; }
                        var gesture=EvaluateGesture(snapshot,scene.CollisionBoxes.Where(b=>b.DependsOnOperator&&b.Role!=MaintenanceAccessRole.OperatorSpace),control);
                        gestureChecked++;
                        var candidate = new Candidate { Foot = levelFoot, Support = hit.Support, Bodies = bodies, Score = score, WorkHeight = candidateProfile.WorkHeight,
                            Posture=configuration.Posture,BodyYawDegrees=configuration.Yaw,Gesture=gesture };
                        candidate.ProvisionalUnknown.AddRange(provisional);
                        candidate.PostureWarnings.AddRange(scene.ConstraintNotes);
                        candidates.Add(candidate);
                        // Never fall through to a lower storey at the same XY
                        // after a supported, reachable work level has been found.
                        break;
                    }
                }
                // The ordinary standing option is preferred when a fully known
                // clear position exists. Forced modes never silently change.
                if(!manual&&configuration.Posture==MaintenanceOperatorPosture.Standing&&Math.Abs(configuration.Yaw)<1e-8
                    &&candidates.Any(c=>c.Gesture.Obstacles.Count==0&&c.Gesture.UnknownSources.All(unavoidableSources.Contains)
                        &&c.ProvisionalUnknown.Count==unavoidableImports))break;
                }
                }
                catch(SearchLimitException)
                { result.SearchLimited=true;result.Warnings.Add("Recherche de postes limitée après 25 s ou 100 000 intersections : seuls les gestes entièrement contrôlés sont proposés. D’autres solutions peuvent exister."); }
                foreach (var candidate in candidates.OrderBy(c=>c.Gesture.Obstacles.Count>0?1:0).ThenBy(c=>c.Gesture.Obstacles.Count)
                    .ThenBy(c=>c.Gesture.UnknownSources.Count+c.ProvisionalUnknown.Count).ThenBy(c=>c.Posture)
                    .ThenBy(c=>Math.Abs(c.BodyYawDegrees)).ThenBy(c => c.Score))
                {
                    if (result.Alternatives.Count >= 6) break;
                    if (result.Alternatives.Any(a => (a.FootPoint - candidate.Foot).GetLength() < Mm(120)
                        &&a.SuggestedPosture==candidate.Posture&&Math.Abs(a.SuggestedBodyYawDegrees-candidate.BodyYawDegrees)<1)) continue;
                    var alternative = new MaintenanceOperatorAlternative { FootPoint = candidate.Foot, Support = candidate.Support, Score = candidate.Score,
                        SuggestedWorkHeight = candidate.WorkHeight,SuggestedPosture=candidate.Posture,SuggestedBodyYawDegrees=candidate.BodyYawDegrees,
                        WholeGestureChecked=true,WholeGestureClear=candidate.Gesture.Obstacles.Count==0&&fixedGesture.Obstacles.Count==0 };
                    alternative.GestureObstacles.AddRange(candidate.Gesture.Obstacles);
                    alternative.GestureUnknown.AddRange(fixedGesture.Unknown.Concat(candidate.Gesture.Unknown).Distinct());
                    alternative.ProvisionalUnknown.AddRange(candidate.ProvisionalUnknown);
                    alternative.PostureWarnings.AddRange(candidate.PostureWarnings);
                    result.Alternatives.Add(alternative);
                }
                if (result.Alternatives.Count > 0)
                {
                    result.FootPoint = result.Alternatives[0].FootPoint;
                    result.Support = result.Alternatives[0].Support;
                    result.SuggestedWorkHeight = result.Alternatives[0].SuggestedWorkHeight;
                    result.SuggestedPosture=result.Alternatives[0].SuggestedPosture;result.SuggestedBodyYawDegrees=result.Alternatives[0].SuggestedBodyYawDegrees;
                    result.PostureWarnings.AddRange(result.Alternatives[0].PostureWarnings);
                    result.WholeGestureChecked=result.Alternatives[0].WholeGestureChecked;
                    result.WholeGestureClear=result.Alternatives[0].WholeGestureClear;
                    result.GestureObstacles.AddRange(result.Alternatives[0].GestureObstacles);
                    result.GestureUnknown.AddRange(result.Alternatives[0].GestureUnknown);
                    result.RejectedFoot=null;result.BlockingObstacles.Clear();
                    result.ProvisionalUnknown.AddRange(result.Alternatives[0].ProvisionalUnknown);
                    result.Confidence = snapshot.Unknown.Count == 0 && result.ProvisionalUnknown.Count == 0&&result.GestureUnknown.Count==0&&!result.SearchLimited
                        ? MaintenancePlacementConfidence.GeometryChecked : MaintenancePlacementConfidence.LimitedGeometry;
                    result.Assumptions.Add(result.ProvisionalUnknown.Count == 0
                        ? "Poste proposé sur un sol modélisé : appuis des deux pieds, corps, portée, bras et mains contrôlés sur tout le geste. L’accès jusqu’au poste, la résistance et l’état du sol restent à confirmer."
                        : "Poste provisoire sur un sol modélisé : empreinte contrôlée en neuf points, portée et obstacles connus contrôlés. Une géométrie d’import reste indéterminée. L’accès jusqu’au poste, la résistance et l’état du sol restent à confirmer.");
                    foreach (string unknown in result.ProvisionalUnknown)
                        result.Warnings.Add("Poste provisoire : " + unknown + " — seule l’emprise de l’import recoupe le corps ; aucune collision n’est prouvée, mais l’absence de collision n’est pas confirmée. Vérifiez cet import dans la maquette.");
                    result.Warnings.AddRange(result.GestureUnknown);
                    if(result.FixedObstacles.Count>0)
                        result.Assumptions.Add("Le poste est proposé malgré "+result.FixedObstacles.Count+" conflit(s) du mécanisme ou du volume de retrait, indépendants de la posture. Changer le poste ne supprime pas ces conflits avec les hypothèses actuelles.");
                    if(result.GestureObstacles.Count>0)
                        result.Warnings.Add("Aucun geste entièrement libre trouvé : le meilleur poste contrôlé conserve "+result.GestureObstacles.Count+" conflit(s) de bras ou de main. Consultez les obstacles avant de conclure.");
                    if (!manual && result.SuggestedWorkHeight > profile.WorkHeight + Mm(1))
                        result.Assumptions.Add("Hauteur de prise de chaîne proposée selon la boucle et la course demandée : "
                            + (result.SuggestedWorkHeight * 304.8).ToString("0", CultureInfo.CurrentCulture) + " mm au-dessus du sol choisi. Cote géométrique réglable, pas un seuil normatif.");
                    if (travel.GetLength() > 1e-8) result.Assumptions.Add("Le sol est aussi échantillonné sous le recul rectiligne du mannequin ; aucun franchissement de marche n’est simulé.");
                }
                else
                {
                    if(result.RejectedFoot!=null)
                    {
                        var rejectedProfile=Copy(profile,result.RejectedFoot);rejectedProfile.OperatorPosture=result.RejectedPosture;
                        rejectedProfile.BodyYawDegrees=result.RejectedBodyYawDegrees;
                        var rejectedBodies=MaintenanceAccessGeometry.Build(rejectedProfile).CollisionBoxes.Where(b=>b.Role==MaintenanceAccessRole.OperatorSpace);
                        var seen=new HashSet<string>(result.BlockingObstacles.Select(o=>o.LinkId+":"+o.ElementId));
                        foreach(var body in rejectedBodies)
                        {
                            var points=body.BoundsCorners();
                            foreach(var obstacle in BodyCandidates(snapshot,Minimum(points),Maximum(points)))
                            {
                                if(result.BlockingObstacles.Count>=8)break;
                                string identity=obstacle.LinkId+":"+obstacle.ElementId;
                                if(obstacle.Solid==null||seen.Contains(identity))continue;
                                if(!NativeBodyClear(body,obstacle,out _,out bool overlaps)&&overlaps)
                                {
                                    seen.Add(identity);result.BlockingObstacles.Add(new MaintenanceAccessObstacle{Label=obstacle.Label,VolumeLabel=body.Label,
                                        Role=MaintenanceAccessRole.OperatorSpace,ElementId=obstacle.ElementId,LinkId=obstacle.LinkId,LinkName=obstacle.LinkName,
                                        Box=Enumerable.Range(0,8).Select(i=>new XYZ((i&1)==0?obstacle.Lo.X:obstacle.Hi.X,(i&2)==0?obstacle.Lo.Y:obstacle.Hi.Y,(i&4)==0?obstacle.Lo.Z:obstacle.Hi.Z)).ToArray()});
                                }
                            }
                        }
                    }
                    var reasonCode=MaintenanceOperatorFailure.Classify(result.SearchLimited,true,supported,reachable,unverifiedBodies);
                    string detail = reasonCode==MaintenanceOperatorFailureReason.BodyUnverified ? lastUnverifiedBody
                        : reachable==0 ? lastReachRejection : result.BlockingObstacles.Count>0
                            ? "Obstacle du corps : "+result.BlockingObstacles[0].Label+"." : lastBodyRejection;
                    if (supported>0 && (profile.Operation==MaintenanceAccessOperation.Actuator || profile.Operation==MaintenanceAccessOperation.Filter))
                    {
                        string Height(double minimum,double maximum) => Math.Abs(maximum-minimum)<Mm(1)
                            ? (minimum*304.8).ToString("0",CultureInfo.CurrentCulture)
                            : (minimum*304.8).ToString("0",CultureInfo.CurrentCulture)+" à "+(maximum*304.8).ToString("0",CultureInfo.CurrentCulture);
                        double verticalTravel=profile.Frame.OfVector(profile.RemovalDirection).Normalize().Z*profile.RemovalTravel;
                        string heights="Point de prise au-dessus des appuis testés : début "+Height(minimumStartHeight,maximumStartHeight)
                            +" mm ; fin "+Height(minimumStartHeight+verticalTravel,maximumStartHeight+verticalTravel)+" mm pour "
                            +(profile.RemovalTravel*304.8).ToString("0",CultureInfo.CurrentCulture)+" mm de retrait.";
                        detail=string.IsNullOrWhiteSpace(detail)?heights:detail+" "+heights;
                    }
                    NoOperator(reasonCode,detail);
                }
                result.Diagnostics.Add(attempted + " positions XY ; " + supported + " appuis valides ; " + reachable + " postures atteignables ; "
                    + blocked + " corps refusés au contrôle rapide ; " + preciseRejected + " au contrôle natif ; "
                    + candidates.Count(c => c.ProvisionalUnknown.Count > 0) + " postes candidats avec emprise d’import indéterminée.");
                result.Diagnostics.Add(gestureChecked+" gestes complets contrôlés ; "+result.FixedObstacles.Count+" conflits fixes ; "+result.GestureObstacles.Count
                    +" conflits de bras/mains sur le poste retenu ; "+control.Intersections+" intersections natives du geste. Géométrie de maquette lue une seule fois.");
                result.Diagnostics.Add("Commande Z=" + (command.Z * .3048).ToString("0.000", CultureInfo.InvariantCulture)
                    + " m ; sols rencontrés Z=" + string.Join(", ", floorLevels.Select(z => (z * .3048).ToString("0.000", CultureInfo.InvariantCulture))) + " m.");
                if (profile.Operation == MaintenanceAccessOperation.ChainwheelValve)
                    result.Diagnostics.Add("Bas de boucle modélisé Z=" + ((command.Z - profile.ChainDrop) * .3048).ToString("0.000", CultureInfo.InvariantCulture) + " m.");
                result.Diagnostics.AddRange(rejectionNotes.Take(8));
                result.Warnings.AddRange(snapshot.Unknown.Distinct().Take(8));
                return result;
            }
        }

        private sealed class FloorHit
        {
            internal double Height;
            internal MaintenanceOperatorSupport Support;
        }
        private static IEnumerable<FloorHit> Heights(Snapshot snapshot, double x, double y, double lo, double hi)
        {
            if (!snapshot.SupportIndex.TryGetValue(Key(Cell(x), Cell(y)), out var list)) yield break;
            var found = new List<double>();
            foreach (var item in list)
                if (MaintenancePlacementMath.TryHeight(item.Triangle, x, y, out double height) && height >= lo && height <= hi
                    && !found.Any(z => Math.Abs(z - height) < Mm(1)))
                { found.Add(height); yield return new FloorHit { Height = height, Support = item.Support }; }
        }
        private static bool Supported(Snapshot snapshot, MaintenanceAccessProfile p, XYZ initial, bool manual, out XYZ foot)
        {
            foot = initial;
            var frame=MaintenanceAccessGeometry.OperatorFrame(p,initial);
            var forward=frame.BasisY;var right=frame.BasisX;
            // The two represented boots are checked separately, in the same
            // body orientation as the joints. No fictitious support is required
            // in the empty gap between the feet.
            double width = p.OperatorHeight * .10, depth = p.OperatorHeight * .13;
            var travel = FollowShift(p);
            int intervals = Math.Max(1, (int)Math.Ceiling(travel.GetLength() / Mm(150)));
            var elevations = new List<double>();
            for (int i = 0; i <= intervals; i++)
            foreach(int side in new[]{-1,1})
                foreach (var probe in MaintenancePlacementMath.Footprint(Vector(initial + travel * (i / (double)intervals)+right*(side*p.OperatorHeight*.11)),
                    Vector(right), Vector(forward), width, depth, p.OperatorHeight * .06))
                {
                    var hit = Heights(snapshot, probe.X, probe.Y, initial.Z - Mm(15), initial.Z + Mm(15)).OrderBy(h => Math.Abs(h.Height - initial.Z)).FirstOrDefault();
                    if (hit == null) return false;
                    elevations.Add(hit.Height);
                }
            if (!MaintenancePlacementMath.LevelContact(elevations, Mm(15), out double highest)) return false;
            if (manual && (highest > initial.Z + Mm(2) || initial.Z - highest > Mm(15))) return false;
            foot = new XYZ(initial.X, initial.Y, manual ? initial.Z : highest);
            return true;
        }
        private static IEnumerable<XYZ> SearchPoints(MaintenanceAccessProfile p, double maxRadius)
        {
            var center = p.WorldCenter;
            var preferred = Horizontal(p.Frame.BasisZ);
            double startAngle = preferred.GetLength() > 1e-8 ? Math.Atan2(preferred.Y, preferred.X) : -Math.PI / 2;
            foreach (double distance in new[] { Mm(250), Mm(350), Mm(450), Mm(525), Mm(600), Mm(750), maxRadius })
                for (int i = 0; i < 16; i++)
                {
                    double angle = startAngle + i * Math.PI / 8;
                    yield return center + new XYZ(Math.Cos(angle), Math.Sin(angle), 0) * distance;
                }
        }
        private static XYZ FollowShift(MaintenanceAccessProfile p)
            => p.FollowRemoval && (p.Operation == MaintenanceAccessOperation.Filter || p.Operation == MaintenanceAccessOperation.Actuator)
                ? Horizontal(p.Frame.OfVector(p.RemovalDirection).Normalize() * p.RemovalTravel) : XYZ.Zero;
        private static IEnumerable<(MaintenanceOperatorPosture Posture,double Yaw)> Configurations(MaintenanceAccessProfile p,bool manual)
        {
            if(manual){yield return(p.OperatorPosture,p.BodyYawDegrees);yield break;}
            if(p.PostureSearch==MaintenancePostureSearch.Standing){yield return(MaintenanceOperatorPosture.Standing,0);yield break;}
            if(p.PostureSearch==MaintenancePostureSearch.SidewaysLeft){yield return(MaintenanceOperatorPosture.Standing,90);yield break;}
            if(p.PostureSearch==MaintenancePostureSearch.SidewaysRight){yield return(MaintenanceOperatorPosture.Standing,-90);yield break;}
            if(p.PostureSearch!=MaintenancePostureSearch.Crouched)
                foreach(double yaw in new[]{0.0,-45,45,-90,90})yield return(MaintenanceOperatorPosture.Standing,yaw);
            foreach(double yaw in new[]{0.0,-45,45,-90,90})yield return(MaintenanceOperatorPosture.Crouched,yaw);
        }

        private static bool FastBodyClear(Snapshot snapshot, MaintenanceAccessBox body, out string rejected, out List<string> provisional)
            => CheckBody(snapshot,body,out rejected,out provisional,out _,out _);
        private static bool CheckBody(Snapshot snapshot, MaintenanceAccessBox body, out string rejected, out List<string> provisional,
            out MaintenanceAccessObstacle proved,out bool indeterminate)
        {
            rejected = null;
            provisional = new List<string>();
            proved=null;
            indeterminate=false;
            var corners = body.BoundsCorners();
            var lo = Minimum(corners); var hi = Maximum(corners);
            var center = body.Frame.OfPoint(body.Center)+body.Translation/2;
            var half=body.Size/2+new XYZ(Math.Abs(body.Translation.DotProduct(body.Frame.BasisX))/2,
                Math.Abs(body.Translation.DotProduct(body.Frame.BasisY))/2,Math.Abs(body.Translation.DotProduct(body.Frame.BasisZ))/2);
            var box = new MotorCollisionBox(Vector(center), Vector(body.Frame.BasisX), Vector(body.Frame.BasisY), Vector(body.Frame.BasisZ), Vector(half));
            foreach (var obstacle in BodyCandidates(snapshot, lo, hi))
            {
                // An unreadable CAD container may span the entire project. Its
                // AABB is evidence of uncertainty, not of a physical collision.
                // This exception is restricted to geometry-free import markers:
                // known CAD solids/meshes and unknown equipment still obstruct.
                if (obstacle.BoundingBoxOnlyImport && obstacle.Incomplete && obstacle.Solid == null
                    && obstacle.Triangles.Count == 0 && !obstacle.WalkingMesh)
                {
                    if (!provisional.Contains(obstacle.Label)) provisional.Add(obstacle.Label);
                    continue;
                }
                if (obstacle.Solid!=null)
                {
                    bool broadHit=obstacle.Incomplete||obstacle.Triangles.Count==0||obstacle.Triangles.Any(t=>box.Intersects(t))
                        ||MotorMeshCollision.Contains(obstacle.Triangles,Vector(center));
                    if(!broadHit)continue;
                    if(!NativeBodyClear(body,obstacle,out rejected,out bool overlap))
                    {
                        indeterminate=!overlap;
                        if(overlap)proved=new MaintenanceAccessObstacle{Label=obstacle.Label,VolumeLabel=body.Label,Role=MaintenanceAccessRole.OperatorSpace,
                            ElementId=obstacle.ElementId,LinkId=obstacle.LinkId,LinkName=obstacle.LinkName,
                            Box=Enumerable.Range(0,8).Select(i=>new XYZ((i&1)==0?obstacle.Lo.X:obstacle.Hi.X,(i&2)==0?obstacle.Lo.Y:obstacle.Hi.Y,
                                (i&4)==0?obstacle.Lo.Z:obstacle.Hi.Z)).ToArray()};
                        return false;
                    }
                    continue;
                }
                if (obstacle.Incomplete && !obstacle.WalkingMesh)
                { indeterminate=true;rejected = obstacle.Label + " : géométrie non vérifiable dans le volume du corps."; return false; }
                foreach (var triangle in obstacle.Triangles)
                    if (triangle.Max.X >= lo.X && triangle.Min.X <= hi.X && triangle.Max.Y >= lo.Y && triangle.Min.Y <= hi.Y
                        && triangle.Max.Z >= lo.Z && triangle.Min.Z <= hi.Z && box.Intersects(triangle))
                    { rejected = "Poste occupé par " + obstacle.Label + "."; return false; }
            }
            return true;
        }
        private static bool PreciseBodyClear(Snapshot snapshot, MaintenanceAccessBox body, out string rejected)
            => PreciseBodyClearDetailed(snapshot,body,out rejected,out _);
        private static bool PreciseBodyClearDetailed(Snapshot snapshot, MaintenanceAccessBox body, out string rejected,out bool indeterminate)
        {
            rejected = null;
            indeterminate=false;
            var points = body.BoundsCorners();
                foreach (var obstacle in BodyCandidates(snapshot, Minimum(points), Maximum(points)))
                {
                    if (obstacle.Solid == null) continue;
                    if(!NativeBodyClear(body,obstacle,out rejected,out bool overlaps)){indeterminate=!overlaps;return false;}
                }
            return true;
        }
        private static bool NativeBodyClear(MaintenanceAccessBox body,BodyObstacle obstacle,out string rejected,out bool overlap)
        {
            rejected=null;overlap=false;IList<Solid> parts=null;
            try
            {
                parts=body.CollisionParts();
                foreach(var volume in parts)
                    using(var intersection=BooleanOperationsUtils.ExecuteBooleanOperation(volume,obstacle.Solid,BooleanOperationsType.Intersect))
                        if(intersection.Volume>1e-10){overlap=true;rejected="Collision physique : "+body.Label+" avec "+obstacle.Label+".";return false;}
                return true;
            }
            catch(Exception ex)when(Recoverable(ex)){rejected=obstacle.Label+" : contrôle du corps indéterminé ; poste refusé.";return false;}
            finally{if(parts!=null)foreach(var part in parts)part.Dispose();}
        }
        private static IEnumerable<BodyObstacle> BodyCandidates(Snapshot snapshot, XYZ lo, XYZ hi)
        {
            var found = new HashSet<BodyObstacle>();
            for (int x = Cell(lo.X); x <= Cell(hi.X); x++) for (int y = Cell(lo.Y); y <= Cell(hi.Y); y++)
                if (snapshot.BodyIndex.TryGetValue(Key(x, y), out var list))
                    foreach (var obstacle in list)
                        if (found.Add(obstacle) && Overlap(lo, hi, obstacle.Lo, obstacle.Hi)) yield return obstacle;
        }

        private static Snapshot Capture(Document doc, Outline world, SearchControl control)
        {
            var snapshot = new Snapshot { Lo = world.MinimumPoint, Hi = world.MaximumPoint };
            int read = 0;
            try
            {
                CaptureModel(doc, Transform.Identity, ElementId.InvalidElementId, null);
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    var linked = link.GetLinkDocument();
                    if (linked == null) { snapshot.Unknown.Add("Lien non chargé : " + link.Name); continue; }
                    CaptureModel(linked, link.GetTotalTransform(), link.Id, link.Name);
                }
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
            void CaptureModel(Document model, Transform transform, ElementId linkId, string linkName)
            {
                var corners = new[] { world.MinimumPoint, world.MaximumPoint };
                using (var bounds = MotorEnvelope.Bounds(Enumerable.Range(0, 8).Select(i => transform.Inverse.OfPoint(new XYZ(corners[i & 1].X, corners[(i >> 1) & 1].Y, corners[(i >> 2) & 1].Z)))))
                using (var collector = new FilteredElementCollector(model).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(bounds)))
                    foreach (var element in collector)
                    {
                        read++;control.Check("Lecture des sols et obstacles : " + read + " éléments…");
                        // Datum extents and project/survey base points may have
                        // enormous model-category boxes. They describe a frame
                        // of reference and can never obstruct a standing body.
                        if (IsReferenceElement(element) || element.Category == null || element.Category.CategoryType != CategoryType.Model) continue;
                        if (element is RevitLinkInstance)
                        { if (linkId != ElementId.InvalidElementId) snapshot.Unknown.Add("Lien imbriqué non analysé : " + element.Name); continue; }
                        string label = (linkName == null ? "" : linkName + " / ") + element.Name + " [" + BoosterIds.Value(element.Id) + "]";
                        if (element is ImportInstance viewImport && viewImport.ViewSpecific)
                        {
                            snapshot.Assumptions.Add(label + " : import limité à une vue, exclu des obstacles physiques.");
                            snapshot.Diagnostics.Add(label + " : ViewSpecific=true, OwnerViewId=" + BoosterIds.Value(viewImport.OwnerViewId) + ", import de vue ignoré.");
                            continue;
                        }
                        XYZ lo = null, hi = null;
                        void Register(BodyObstacle obstacle)
                        {
                            obstacle.ElementId=BoosterIds.Value(element.Id);obstacle.LinkId=BoosterIds.Value(linkId);obstacle.LinkName=linkName;
                            snapshot.Obstacles.Add(obstacle);
                        }
                        bool walking = IsWalkingSurface(element);
                        int before = snapshot.Obstacles.Count;
                        try
                        {
                            using (var box = element.get_BoundingBox(null))
                            {
                                if (box == null) continue;
                                var points = Enumerable.Range(0, 8).Select(i => transform.OfPoint(box.Transform.OfPoint(new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X,
                                    (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z)))).ToArray();
                                lo = Minimum(points); hi = Maximum(points);
                            }
                            if (!Overlap(lo, hi, world.MinimumPoint, world.MaximumPoint)) continue;
                            var support = new MaintenanceOperatorSupport { Label = label, ElementId = element.Id, LinkId = linkId, LinkName = linkName };
                            var linework = element is ImportInstance ? new MaintenanceHorizontalLinework() : null;
                            bool missingFragment=false;
                            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false })
                            using (var shape = element.get_Geometry(options)) Read(shape);
                            void Read(GeometryElement geometry)
                            {
                                if (geometry == null) { missingFragment=true;linework?.RejectNonLinework(); return; }
                                foreach (var item in geometry)
                                {
                                    control.Check();
                                    if (linework != null && linework.CanRead && !(item is GeometryInstance))
                                    {
                                        ObserveLinework(item, transform, linework);
                                    }
                                    if (item is GeometryInstance instance) { using (var expanded = instance.GetInstanceGeometry()) Read(expanded); }
                                    else if (item is Solid solid && solid.Volume > 1e-12)
                                    {
                                        var copy = SolidUtils.CreateTransformed(solid, transform);
                                        var obstacle = new BodyObstacle { Solid = copy, Label = label, Lo = lo, Hi = hi };
                                        // Register ownership before querying native bounds;
                                        // even a recoverable bounds/face read failure must
                                        // dispose this detached native solid exactly once.
                                        Register(obstacle);
                                        XYZ solidLo=lo,solidHi=hi;
                                        using(var solidBox=copy.GetBoundingBox())
                                        {
                                            if(solidBox!=null)
                                            {
                                                var points=Enumerable.Range(0,8).Select(i=>solidBox.Transform.OfPoint(new XYZ((i&1)==0?solidBox.Min.X:solidBox.Max.X,
                                                    (i&2)==0?solidBox.Min.Y:solidBox.Max.Y,(i&4)==0?solidBox.Min.Z:solidBox.Max.Z))).ToArray();
                                                solidLo=Minimum(points);solidHi=Maximum(points);
                                            }
                                        }
                                        obstacle.Lo=solidLo;obstacle.Hi=solidHi;
                                        foreach (Face face in copy.Faces)
                                        {
                                            var mesh = face.Triangulate();
                                            if (mesh == null) { obstacle.Incomplete = true; continue; }
                                            var uv = face.GetBoundingBox();
                                            bool upper = walking && face.ComputeNormal((uv.Min + uv.Max) / 2).Z >= .995;
                                            ReadMesh(mesh, Transform.Identity, obstacle, upper);
                                        }
                                    }
                                    else if (item is Mesh mesh && mesh.NumTriangles > 0)
                                    {
                                        bool terrainSheet = Enum.TryParse("OST_Topography", out BuiltInCategory terrainCategory)
                                            && BoosterIds.Value(element.Category.Id) == (long)terrainCategory;
                                        var obstacle = new BodyObstacle { Label = label, Lo = lo, Hi = hi, Incomplete = true, WalkingMesh = terrainSheet };
                                        Register(obstacle);
                                        ReadMesh(mesh, transform, obstacle, walking);
                                        if (!terrainSheet) snapshot.Unknown.Add(label + " : maillage sans contrôle volumique natif ; poste refusé si son corps recoupe cette emprise");
                                    }
                                }
                            }
                            void ReadMesh(Mesh mesh, Transform meshTransform, BodyObstacle obstacle, bool supportFace)
                            {
                                for (int i = 0; i < mesh.NumTriangles; i++)
                                {
                                    if(i%100==0)control.Check();
                                    var triangle = mesh.get_Triangle(i);
                                    if (triangle == null) { obstacle.Incomplete = true; continue; }
                                    var t = new MotorTriangle(Vector(meshTransform.OfPoint(triangle.get_Vertex(0))), Vector(meshTransform.OfPoint(triangle.get_Vertex(1))), Vector(meshTransform.OfPoint(triangle.get_Vertex(2))));
                                    if (t.Max.X < world.MinimumPoint.X || t.Min.X > world.MaximumPoint.X || t.Max.Y < world.MinimumPoint.Y || t.Min.Y > world.MaximumPoint.Y) continue;
                                    obstacle.Triangles.Add(t);
                                    if (supportFace && MaintenancePlacementMath.NearHorizontal(t)) snapshot.Supports.Add(new SupportTriangle { Triangle = t, Support = support });
                                }
                            }
                            bool horizontalPlan = linework != null && linework.IsHorizontal2D(Mm(1));
                            if (linework != null)
                                snapshot.Diagnostics.Add(label + " : primitives Curve/PolyLine=" + linework.PrimitiveCount + ", points=" + linework.PointCount
                                    + ", Zspan=" + (linework.ZSpan * 304.8).ToString("0.###", CultureInfo.InvariantCulture)
                                    + " mm ; plan horizontal 2D=" + horizontalPlan + "; lecture bornée=" + linework.ReadingLimited + ".");
                            if(missingFragment)
                            {
                                // Keep every readable solid and its native test.
                                // A missing sibling lowers confidence without
                                // turning a family's whole AABB into a hard clash.
                                var readable=snapshot.Obstacles.Skip(before).Where(o=>o.Solid!=null).ToArray();
                                foreach(var obstacle in readable)obstacle.Incomplete=true;
                                if(readable.Length==0||element is ImportInstance)
                                    Register(new BodyObstacle {Label=label,Lo=lo,Hi=hi,Incomplete=true,
                                        BoundingBoxOnlyImport=element is ImportInstance});
                                snapshot.Unknown.Add(label+" : lecture de géométrie partielle, un fragment est indisponible");
                            }
                            else if (horizontalPlan)
                                snapshot.Assumptions.Add(label + " : plan horizontal formé uniquement de courbes/polylignes, épaisseur Z inférieure à 1 mm ; exclu des obstacles physiques.");
                            else if (snapshot.Obstacles.Count == before && (element is FamilyInstance || element is HostObject || element is MEPCurve || element is ImportInstance || element is DirectShape))
                            {
                                Register(new BodyObstacle { Label = label, Lo = lo, Hi = hi, Incomplete = true,
                                    BoundingBoxOnlyImport = element is ImportInstance });
                                snapshot.Unknown.Add(label + " : géométrie solide indisponible");
                            }
                        }
                        catch (Exception ex) when (!control.IsCallbackFailure(ex)&&Recoverable(ex))
                        {
                            snapshot.Unknown.Add(label + " : géométrie non exploitable");
                            if (lo != null) Register(new BodyObstacle { Label = label, Lo = lo, Hi = hi, Incomplete = true,
                                BoundingBoxOnlyImport = element is ImportInstance });
                        }
                    }
            }
        }

        private static MaintenanceAccessProfile Copy(MaintenanceAccessProfile p, XYZ foot)
            => new MaintenanceAccessProfile { Operation = p.Operation, Frame = p.Frame, Center = p.Center, OperatorOrigin = foot,
                OperatorPosture=p.OperatorPosture,BodyYawDegrees=p.BodyYawDegrees,PostureSearch=p.PostureSearch,
                HandleRadius = p.HandleRadius, HandleThickness = p.HandleThickness, GripClearance = p.GripClearance,
                InitialAngleDegrees = p.InitialAngleDegrees, RotationDegrees = p.RotationDegrees, OperatorHeight = p.OperatorHeight,
                OperatorWidth = p.OperatorWidth, OperatorDepth = p.OperatorDepth, OperatorDistance = p.OperatorDistance, WorkHeight = p.WorkHeight,
                ChainDrop = p.ChainDrop, ChainSpacing = p.ChainSpacing, ChainPullStroke = p.ChainPullStroke, RemovalSize = p.RemovalSize,
                RemovalCenterOffset = p.RemovalCenterOffset, RemovalDirection = p.RemovalDirection, RemovalTravel = p.RemovalTravel,
                Margin = p.Margin, FollowRemoval = p.FollowRemoval };
        private static bool Recoverable(Exception ex) => ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException || ex is InvalidOperationException;
        private static MotorVector Vector(XYZ point) => new MotorVector(point.X, point.Y, point.Z);
        private static XYZ Horizontal(XYZ point) => new XYZ(point.X, point.Y, 0);
        private static XYZ Minimum(IEnumerable<XYZ> points) => new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
        private static XYZ Maximum(IEnumerable<XYZ> points) => new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
        private static int Cell(double value) => (int)Math.Floor(value / CellSize);
        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
        private static bool Overlap(XYZ lo, XYZ hi, XYZ otherLo, XYZ otherHi)
            => lo.X <= otherHi.X && hi.X >= otherLo.X && lo.Y <= otherHi.Y && hi.Y >= otherLo.Y && lo.Z <= otherHi.Z && hi.Z >= otherLo.Z;
    }
}

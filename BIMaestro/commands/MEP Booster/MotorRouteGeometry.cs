using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal sealed class MotorRoutePose
    {
        internal XYZ Center;
        internal double Yaw;
        internal MotorRoutePose(XYZ center, double yaw) { Center = center; Yaw = yaw; }
    }
    internal sealed class MotorRouteGeometry : IDisposable
    {
        private sealed class Obstacle
        {
            internal Solid Solid;
            internal XYZ Min, Max;
            internal string Name;
            internal List<MotorTriangle> Triangles;
            internal bool MeshComplete = true;
        }
        private readonly List<Obstacle> _obstacles = new List<Obstacle>();
        private readonly List<MotorDoorPortal> _doors = new List<MotorDoorPortal>();
        private readonly MotorEnvelope _reference;
        private readonly Dictionary<long, List<Obstacle>> _index = new Dictionary<long, List<Obstacle>>();
        internal readonly List<string> Unknown = new List<string>();
        internal readonly List<string> Assumptions = new List<string>();
        internal IReadOnlyList<MotorDoorPortal> Portals => _doors;
        internal string LastObstacle;
        internal MotorRouteGeometry(Document doc, FamilyInstance pump, MotorEnvelope reference, XYZ target,
            double padding, ElementId doorId, ElementId doorLinkId, XYZ approach = null, Action<string> progress = null,
            bool allowIntermediateDoors = false)
        {
            _reference = reference;
            try
            {
                var start = reference.Frame.OfPoint(reference.Center);
                double radius = reference.Size.GetLength() / 2 + reference.Margin;
                var routeBounds = new[] { start, target, approach ?? target };
                using (var world = new Outline(new XYZ(routeBounds.Min(p => p.X) - padding - radius, routeBounds.Min(p => p.Y) - padding - radius, start.Z - radius),
                    new XYZ(routeBounds.Max(p => p.X) + padding + radius, routeBounds.Max(p => p.Y) + padding + radius, start.Z + radius)))
                {
                    int readCount=0;
                    var ignored = new HashSet<long>();
                    void Ignore(FamilyInstance instance)
                    {
                        if (!ignored.Add(BoosterIds.Value(instance.Id))) return;
                        foreach (var id in instance.GetSubComponentIds()) if (doc.GetElement(id) is FamilyInstance nested) Ignore(nested);
                    }
                    Ignore(pump);
                    Capture(doc, Transform.Identity, ElementId.InvalidElementId, ignored);
                    foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                    {
                        var linked = link.GetLinkDocument();
                        if (linked == null) { Unknown.Add("Lien non chargé : " + link.Name); continue; }
                        var excluded = new HashSet<long>();
                        Capture(linked, link.GetTotalTransform(), link.Id, excluded);
                    }
                    void Capture(Document source, Transform transform, ElementId linkId, HashSet<long> excluded)
                    {
                        var p = new[] { world.MinimumPoint, world.MaximumPoint };
                        using (var bounds = MotorEnvelope.Bounds(Enumerable.Range(0, 8).Select(i => transform.Inverse.OfPoint(new XYZ(
                            p[i & 1].X, p[(i >> 1) & 1].Y, p[(i >> 2) & 1].Z)))))
                        using (var collector = new FilteredElementCollector(source).WhereElementIsNotElementType()
                            .WherePasses(new BoundingBoxIntersectsFilter(bounds)))
                        {
                        var elements = collector.ToElements();
                        // Resolve the open-door assumption before reading any solids,
                        // including shared nested components that precede their host.
                        foreach (var candidate in elements.OfType<FamilyInstance>())
                        {
                            if (candidate.Category == null || BoosterIds.Value(candidate.Category.Id) != (long)BuiltInCategory.OST_Doors
                                || candidate.SuperComponent != null) continue;
                            bool targetDoor = candidate.Id == doorId && linkId == doorLinkId;
                            if (!targetDoor && !allowIntermediateDoors) continue;
                            string doorLabel = (linkId == ElementId.InvalidElementId ? "" : "Lien " + BoosterIds.Value(linkId) + " / ")
                                + candidate.Name + " [" + BoosterIds.Value(candidate.Id) + "]";
                            MotorDoorPortal portal = null;
                            bool checkedAperture;
                            try { checkedAperture = TryDoorPortal(candidate, transform, doorLabel, out portal); }
                            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException || ex is InvalidOperationException)
                            {
                                Unknown.Add(doorLabel + " : repère de porte illisible ; porte conservée comme obstacle");
                                continue;
                            }
                            if (!checkedAperture && !(candidate.Host is Wall))
                            {
                                Unknown.Add(doorLabel + " : passage non dimensionné et mur hôte indisponible ; porte conservée comme obstacle");
                                continue;
                            }
                            void Exclude(FamilyInstance instance)
                            {
                                if (!excluded.Add(BoosterIds.Value(instance.Id))) return;
                                foreach (var id in instance.GetSubComponentIds()) if (source.GetElement(id) is FamilyInstance nested) Exclude(nested);
                            }
                            Exclude(candidate);
                            if (checkedAperture) { portal.Target = targetDoor; _doors.Add(portal); }
                            else Unknown.Add(doorLabel + " : dimensions nominales indisponibles ; seul le trou du mur est contrôlé, dormant non vérifié");
                        }
                        foreach (var element in elements)
                        {
                            if(++readCount%32==1) progress?.Invoke("Lecture de la géométrie : " + readCount + " éléments…");
                            if (excluded.Contains(BoosterIds.Value(element.Id)) || element is View || element is SpatialElement
                                || element.Category == null || element.Category.CategoryType != CategoryType.Model) continue;
                            if (element is RevitLinkInstance)
                            { if (linkId != ElementId.InvalidElementId) Unknown.Add("Lien imbriqué non analysé : " + element.Name); continue; }
                            string label = (linkId == ElementId.InvalidElementId ? "" : "Lien " + BoosterIds.Value(linkId) + " / ")
                                + element.Name + " [" + BoosterIds.Value(element.Id) + "]";
                            int before = _obstacles.Count;
                            try
                            {
                                using (var options = new Options { DetailLevel = ViewDetailLevel.Fine })
                                using (var geometry = element.get_Geometry(options))
                                {
                                    if (geometry != null) Read(geometry);
                                    void Read(GeometryElement items)
                                    {
                                        if(items==null) return;
                                        foreach (var item in items)
                                        {
                                            if (item is GeometryInstance nested)
                                            { using (var expanded = nested.GetInstanceGeometry()) Read(expanded); }
                                            else if (item is Mesh) Unknown.Add(label + " : maillage non analysé");
                                            else if (item is Solid solid && solid.Volume > 1e-12)
                                            {
                                                var copy = SolidUtils.CreateTransformed(solid, transform);
                                                var box = copy.GetBoundingBox();
                                                if(box==null){copy.Dispose();Unknown.Add(label+" : boîte du solide indisponible");continue;}
                                                using (var outline = MotorEnvelope.Bounds(Enumerable.Range(0, 8).Select(i => box.Transform.OfPoint(new XYZ(
                                                    (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                                                    (i & 4) == 0 ? box.Min.Z : box.Max.Z)))))
                                                {
                                                    var obstacle = new Obstacle { Solid = copy, Min = outline.MinimumPoint, Max = outline.MaximumPoint, Name = label, Triangles = new List<MotorTriangle>() };
                                                    foreach (Face face in copy.Faces)
                                                    {
                                                        var mesh = face.Triangulate();
                                                        if(mesh==null){obstacle.MeshComplete=false;continue;}
                                                        for (int i=0;i<mesh.NumTriangles;i++)
                                                        {
                                                            var triangle=mesh.get_Triangle(i);
                                                            if(triangle==null){obstacle.MeshComplete=false;continue;}
                                                            obstacle.Triangles.Add(new MotorTriangle(Vector(triangle.get_Vertex(0)),Vector(triangle.get_Vertex(1)),Vector(triangle.get_Vertex(2))));
                                                        }
                                                    }
                                                    _obstacles.Add(obstacle);
                                                    for(int x=Math.Max(Cell(obstacle.Min.X),Cell(world.MinimumPoint.X));x<=Math.Min(Cell(obstacle.Max.X),Cell(world.MaximumPoint.X));x++)
                                                    for(int y=Math.Max(Cell(obstacle.Min.Y),Cell(world.MinimumPoint.Y));y<=Math.Min(Cell(obstacle.Max.Y),Cell(world.MaximumPoint.Y));y++)
                                                    {
                                                        long key=Key(x,y);
                                                        if(!_index.TryGetValue(key,out var bucket)) _index[key]=bucket=new List<Obstacle>();
                                                        bucket.Add(obstacle);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                if (_obstacles.Count == before && (element is FamilyInstance || element is HostObject || element is MEPCurve || element is ImportInstance || element is DirectShape))
                                    Unknown.Add(label + " : aucun solide exploitable");
                            }
                            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException || ex is InvalidOperationException)
                            { Unknown.Add(label + " : géométrie illisible"); }
                        }
                        }
                    }
                }
                Assumptions.Add(allowIntermediateDoors
                    ? "Porte cible et portes intermédiaires supposées ouvrables et ouvertes : familles exclues, trous des murs et dimensions nominales disponibles contrôlés. Le passage nominal est supposé centré à l'origine de la famille. Les vantaux ouverts, dormants et manœuvres d'ouverture ne sont pas certifiés."
                    : "Porte cible supposée ouverte : trou du mur et dimensions nominales disponibles contrôlés, passage centré à l'origine de la famille ; portes intermédiaires conservées comme obstacles. Dormant et vantail ouvert non certifiés.");
            }
            catch { Dispose(); throw; }
        }
        private static bool TryDoorPortal(FamilyInstance door, Transform sourceTransform, string label, out MotorDoorPortal portal)
        {
            portal = null;
            double width = DoorLength(door, BuiltInParameter.DOOR_WIDTH, new[] { "Largeur de passage", "Largeur de baie", "Largeur", "Width" });
            double height = DoorLength(door, BuiltInParameter.DOOR_HEIGHT, new[] { "Hauteur de passage", "Hauteur de baie", "Hauteur", "Height" });
            if (width <= 1e-6 || height <= 1e-6 || !(door.Location is LocationPoint location)) return false;
            var frame = door.GetTransform();
            var x = sourceTransform.OfVector(frame.BasisX).Normalize();
            var n = sourceTransform.OfVector(frame.BasisY).Normalize();
            var z = sourceTransform.OfVector(frame.BasisZ).Normalize();
            // A tilted/invalid family frame is not enough to infer a doorway.
            if (Math.Abs(x.DotProduct(n)) > 1e-5 || Math.Abs(x.DotProduct(z)) > 1e-5
                || Math.Abs(n.DotProduct(z)) > 1e-5 || z.DotProduct(XYZ.BasisZ) < .999) return false;
            double depth = door.Host is Wall wall ? wall.Width : .01;
            portal = new MotorDoorPortal(label, Vector(sourceTransform.OfPoint(location.Point)), Vector(x), Vector(n), Vector(z), width, height, Math.Max(.01, depth));
            return true;
        }
        private static double DoorLength(FamilyInstance door, BuiltInParameter builtIn, IEnumerable<string> names)
        {
            foreach (Element element in new Element[] { door, door.Symbol })
            {
                if (element == null) continue;
                var parameter = element.get_Parameter(builtIn);
                if (parameter != null && parameter.StorageType == StorageType.Double && parameter.AsDouble() > 1e-6) return parameter.AsDouble();
                foreach (var name in names)
                {
                    parameter = element.LookupParameter(name);
                    if (parameter != null && parameter.StorageType == StorageType.Double && parameter.AsDouble() > 1e-6) return parameter.AsDouble();
                }
            }
            return 0;
        }
        internal static double AngleDelta(double from, double to)
        {
            double delta = (to - from) % (Math.PI * 2);
            if (delta > Math.PI) delta -= Math.PI * 2;
            if (delta < -Math.PI) delta += Math.PI * 2;
            return delta;
        }
        internal MotorEnvelope Envelope(MotorRoutePose pose, double extraMargin = 0)
            => At(_reference, pose, extraMargin);
        internal static MotorEnvelope At(MotorEnvelope reference, MotorRoutePose pose, double extraMargin = 0)
        {
            var rotation = Transform.CreateRotation(XYZ.BasisZ, pose.Yaw);
            var frame = Transform.Identity; frame.Origin = pose.Center;
            frame.BasisX = rotation.OfVector(reference.Frame.BasisX);
            frame.BasisY = rotation.OfVector(reference.Frame.BasisY);
            frame.BasisZ = rotation.OfVector(reference.Frame.BasisZ);
            return new MotorEnvelope { Frame = frame, Center = XYZ.Zero, Size = reference.Size, Margin = reference.Margin + extraMargin,
                Axis = 0, Sign = 1, Travel = 0 };
        }
        internal bool Clear(MotorRoutePose from, MotorRoutePose to)
        {
            var delta = to.Center - from.Center;
            double angle = AngleDelta(from.Yaw, to.Yaw);
            if (Math.Abs(angle) > 1e-8)
            {
                // Cover every intermediate orientation, including between samples:
                // inflation bounds the maximum corner displacement about the midpoint.
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(angle) / (Math.PI / 32)));
                double radius = _reference.Size.GetLength() / 2 + Math.Sqrt(3) * _reference.Margin;
                double inflation = radius * Math.Abs(angle) / (2 * steps);
                for (int i = 0; i < steps; i++)
                {
                    var middle = new MotorRoutePose(from.Center, from.Yaw + angle * (i + 0.5) / steps);
                    if (!ClearEnvelope(Envelope(middle, inflation), 0)) return false;
                }
                return true;
            }
            var envelope = Envelope(from);
            double length = delta.GetLength();
            if (length > 1e-8) envelope.TranslationDirection = delta / length;
            return ClearEnvelope(envelope, length);
        }
        private static MotorVector Vector(XYZ point) => new MotorVector(point.X,point.Y,point.Z);
        private static int Cell(double value) => (int)Math.Floor(value/3.280839895013123);
        private static long Key(int x,int y) => ((long)x<<32) ^ (uint)y;
        internal bool ClearForSearch(MotorRoutePose from, MotorRoutePose to)
        {
            double angle=AngleDelta(from.Yaw,to.Yaw);
            if(Math.Abs(angle)>1e-8)
            {
                int steps=Math.Max(1,(int)Math.Ceiling(Math.Abs(angle)/(Math.PI/32)));
                double radius=_reference.Size.GetLength()/2+Math.Sqrt(3)*_reference.Margin;
                for(int i=0;i<steps;i++)
                    if(!ClearFast(Envelope(new MotorRoutePose(from.Center,from.Yaw+angle*(i+.5)/steps),radius*Math.Abs(angle)/(2*steps)),XYZ.Zero)) return false;
                return true;
            }
            return ClearFast(Envelope(new MotorRoutePose((from.Center+to.Center)/2,from.Yaw)),to.Center-from.Center);
        }
        private bool ClearFast(MotorEnvelope envelope,XYZ translation)
        {
            // Search uses the swept box's midpoint. Check the actual endpoint
            // corners at portals; the enclosing OBB would reject valid diagonal
            // approaches by testing corners that the motor never occupies.
            if (!ClearDoors(envelope.Corners(0, true).Select(p => p - translation / 2), translation)) return false;
            var f=envelope.Frame;
            var half=envelope.Size/2+new XYZ(envelope.Margin,envelope.Margin,envelope.Margin);
            half+=new XYZ(Math.Abs(translation.DotProduct(f.BasisX))/2,Math.Abs(translation.DotProduct(f.BasisY))/2,Math.Abs(translation.DotProduct(f.BasisZ))/2);
            var box=new MotorCollisionBox(Vector(f.Origin),Vector(f.BasisX),Vector(f.BasisY),Vector(f.BasisZ),Vector(half));
            var extent=new XYZ(Math.Abs(f.BasisX.X)*half.X+Math.Abs(f.BasisY.X)*half.Y+Math.Abs(f.BasisZ.X)*half.Z,
                Math.Abs(f.BasisX.Y)*half.X+Math.Abs(f.BasisY.Y)*half.Y+Math.Abs(f.BasisZ.Y)*half.Z,
                Math.Abs(f.BasisX.Z)*half.X+Math.Abs(f.BasisY.Z)*half.Y+Math.Abs(f.BasisZ.Z)*half.Z);
            var min=f.Origin-extent;var max=f.Origin+extent;
            var candidates=new HashSet<Obstacle>();
            for(int x=Cell(min.X);x<=Cell(max.X);x++) for(int y=Cell(min.Y);y<=Cell(max.Y);y++)
                if(_index.TryGetValue(Key(x,y),out var bucket)) foreach(var obstacle in bucket) candidates.Add(obstacle);
            foreach(var obstacle in candidates)
            {
                if(obstacle.Min.X>max.X||obstacle.Max.X<min.X||obstacle.Min.Y>max.Y||obstacle.Max.Y<min.Y||obstacle.Min.Z>max.Z||obstacle.Max.Z<min.Z) continue;
                if(!obstacle.MeshComplete || obstacle.Triangles.Count==0)
                {
                    var enclosing=new MotorEnvelope {Frame=f,Center=XYZ.Zero,Size=half*2,Margin=0,Axis=0,Sign=1};
                    try
                    {
                        using(var volume=enclosing.Sweep(0))
                        using(var overlap=BooleanOperationsUtils.ExecuteBooleanOperation(volume,obstacle.Solid,BooleanOperationsType.Intersect))
                            if(overlap.Volume>1e-10){LastObstacle=obstacle.Name;return false;}
                    }
                    catch(Autodesk.Revit.Exceptions.ApplicationException){LastObstacle=obstacle.Name;return false;}
                    continue;
                }
                foreach(var triangle in obstacle.Triangles)
                    if(triangle.Min.X<=max.X && triangle.Max.X>=min.X && triangle.Min.Y<=max.Y && triangle.Max.Y>=min.Y && triangle.Min.Z<=max.Z && triangle.Max.Z>=min.Z && box.Intersects(triangle))
                    {LastObstacle=obstacle.Name;return false;}
                var center=f.Origin;
                if(center.X>=obstacle.Min.X && center.X<=obstacle.Max.X && center.Y>=obstacle.Min.Y && center.Y<=obstacle.Max.Y && center.Z>=obstacle.Min.Z && center.Z<=obstacle.Max.Z
                    && MotorMeshCollision.Contains(obstacle.Triangles,Vector(center))) {LastObstacle=obstacle.Name;return false;}
            }
            return true;
        }
        private bool ClearEnvelope(MotorEnvelope envelope, double length)
        {
            if (!ClearDoors(envelope.Corners(0, true), envelope.Direction * length)) return false;
            using (var bounds = MotorEnvelope.Bounds(envelope.Corners(0, true).Concat(envelope.Corners(length, true))))
            {
                var candidates = _obstacles.Where(o => o.Max.X >= bounds.MinimumPoint.X && o.Min.X <= bounds.MaximumPoint.X
                    && o.Max.Y >= bounds.MinimumPoint.Y && o.Min.Y <= bounds.MaximumPoint.Y
                    && o.Max.Z >= bounds.MinimumPoint.Z && o.Min.Z <= bounds.MaximumPoint.Z).ToList();
                if (candidates.Count == 0) return true;
                var parts = envelope.SweepParts(length);
                try
                {
                    foreach (var candidate in candidates) foreach (var part in parts)
                    {
                        try
                        {
                            using (var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(part, candidate.Solid, BooleanOperationsType.Intersect))
                                if (intersection.Volume > 1e-10) { LastObstacle = candidate.Name; return false; }
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException)
                        {
                            var note = candidate.Name + " : intersection indéterminée (passage refusé)";
                            if (!Unknown.Contains(note)) Unknown.Add(note);
                            LastObstacle = candidate.Name; return false;
                        }
                    }
                    return true;
                }
                finally { foreach (var part in parts) part.Dispose(); }
            }
        }
        private bool ClearDoors(IEnumerable<XYZ> start, XYZ translation)
        {
            // Existing native geometry checks construct an uninitialized object
            // deliberately; a missing optional portal registry means no portals.
            if (_doors == null || _doors.Count == 0) return true;
            var corners = start.Select(Vector).ToList();
            var delta = Vector(translation);
            int count = corners.Count;
            for (int i = 0; i < count; i++) corners.Add(corners[i] + delta);
            foreach (var door in _doors)
                if (!door.AcceptsSweep(corners))
                { LastObstacle = door.Name + " : gabarit incompatible avec le passage nominal"; return false; }
            return true;
        }
        public void Dispose() { foreach (var obstacle in _obstacles) obstacle.Solid.Dispose(); _obstacles.Clear(); }
    }
}

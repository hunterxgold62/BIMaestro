using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace BIMaestro.VideoGames
{
    // Signed distance is positive on the retained side, for rendering and picking.
    internal sealed class GameSectionPlane
    {
        public bool Enabled { get; set; }
        public int Axis { get; set; } = 2;
        public bool Inverted { get; set; }
        public double Position { get; set; }
        public string Name { get; set; } = "Coupe";
        public Vector3D? FaceNormal { get; set; }
        public Point3D Anchor { get; set; }
        public double Offset { get; set; }
        public Vector3D Normal => FaceNormal.HasValue
            ? FaceNormal.Value * (Inverted ? -1 : 1)
            : new Vector3D(Axis == 0 ? Sign : 0, Axis == 1 ? Sign : 0, Axis == 2 ? Sign : 0);
        public double PlaneD => FaceNormal.HasValue
            ? Vector3D.DotProduct(Normal, (Vector3D)Anchor) + Offset * (Inverted ? -1 : 1)
            : Sign * Position;
        public override string ToString() => Name;
        public double Sign => Inverted ? 1 : -1;
        public double Distance(Point3D point) => Vector3D.DotProduct(Normal, (Vector3D)point) - PlaneD;

        public bool ClipSegment(ref Point3D start, ref Point3D end)
        {
            if (!Enabled) return true;
            double a = Distance(start), b = Distance(end);
            if (a < 0 && b < 0) return false;
            if (a >= 0 && b >= 0) return true;
            Point3D intersection = start + (end - start) * (a / (a - b));
            if (a < 0) start = intersection; else end = intersection;
            return true;
        }

        public bool ClipRay(ref Point3D origin, Vector3D direction, ref double length)
        {
            Point3D end = origin + direction * length;
            if (!ClipSegment(ref origin, ref end)) return false;
            length = (end - origin).Length;
            return length > 1e-6;
        }
    }

    internal sealed class GameSectionVolume
    {
        public const int MaximumPlanes = 8;
        public List<GameSectionPlane> Planes { get; } = new List<GameSectionPlane>();
        public bool Enabled => Planes.Any(plane => plane.Enabled);
        public bool ClipSegment(ref Point3D start, ref Point3D end)
        {
            foreach (var plane in Planes)
                if (!plane.ClipSegment(ref start, ref end)) return false;
            return true;
        }
        public bool ClipRay(ref Point3D origin, Vector3D direction, ref double length)
        {
            Point3D end = origin + direction * length;
            if (!ClipSegment(ref origin, ref end)) return false;
            length = (end - origin).Length;
            return length > 1e-6;
        }
    }
}

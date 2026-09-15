using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace BIMaestro.MepBooster
{
    internal static class BoosterOrientationMath
    {
        // Roll relative to projected world-up; vertical pipes use world Y.
        internal static double Roll(Vector3D axis, Vector3D radial)
        {
            if (axis.Length < 1e-9) throw new InvalidOperationException("Axe indéterminé.");
            axis.Normalize();
            radial -= axis * Vector3D.DotProduct(radial, axis);
            if (radial.Length < 1e-8) throw new InvalidOperationException("Orientation transversale indéterminée.");
            radial.Normalize();
            var up = Math.Abs(axis.Z) > 0.99 ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1);
            up -= axis * Vector3D.DotProduct(up, axis); up.Normalize();
            return Math.Atan2(Vector3D.DotProduct(axis, Vector3D.CrossProduct(up, radial)),
                Vector3D.DotProduct(up, radial)) * 180 / Math.PI;
        }
        internal static double Delta(double reference, double target)
        {
            double delta = (reference - target) % 360;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            return delta;
        }
    }
    // Pure numerical snapshot. No Revit calls during action availability checks.
    internal sealed class BoosterPortPose
    {
        internal string Key;
        internal Point3D Position;
        internal Vector3D Direction;
        internal bool Connected;
        internal int Shape;
        internal double SizeA, SizeB;
    }

    internal sealed class BoosterKinematics
    {
        internal Point3D Center, FlipCenter;
        internal Vector3D Axis, FlipAxis;
        internal string Label;
        internal BoosterPortPose[] Ports;

        private static Vector3D Unit(Vector3D v)
        {
            if (v.Length < 1e-9) throw new InvalidOperationException("Axe de raccord indéterminé.");
            v.Normalize(); return v;
        }
        private static Vector3D Positive(Vector3D v)
        {
            v = Unit(v);
            double largest = Math.Abs(v.X) >= Math.Abs(v.Y) && Math.Abs(v.X) >= Math.Abs(v.Z)
                ? v.X : Math.Abs(v.Y) >= Math.Abs(v.Z) ? v.Y : v.Z;
            return largest < 0 ? -v : v;
        }
        private static Point3D Mid(Point3D a, Point3D b) => a + (b - a) * 0.5;

        internal static BoosterKinematics Create(BoosterPortPose[] ports)
        {
            if (ports.Length < 2 || ports.Length > 3)
                throw new InvalidOperationException("Deux connecteurs (droit/coude) ou trois connecteurs (té) sont nécessaires.");
            var result = new BoosterKinematics { Ports = ports };
            int a = 0, b = 1;
            if (ports.Length == 3)
            {
                double best = 1;
                for (int i = 0; i < 3; i++)
                    for (int j = i + 1; j < 3; j++)
                    {
                        double dot = Vector3D.DotProduct(Unit(ports[i].Direction), Unit(ports[j].Direction));
                        if (dot < best) { best = dot; a = i; b = j; }
                    }
                if (best > -0.9999) throw new InvalidOperationException("Ce raccord à trois voies n’a pas de passage principal aligné.");
            }
            Vector3D run = Unit(ports[b].Position - ports[a].Position);
            bool straight = Math.Abs(Vector3D.DotProduct(Unit(ports[a].Direction), run)) > 0.9999
                && Math.Abs(Vector3D.DotProduct(Unit(ports[b].Direction), run)) > 0.9999;
            if (ports.Length == 3 && !straight) throw new InvalidOperationException("Le passage principal du té n’est pas aligné.");
            if (straight)
            {
                result.Center = result.FlipCenter = Mid(ports[a].Position, ports[b].Position);
                result.Axis = Positive(run);
                Vector3D reference;
                if (ports.Length == 3)
                {
                    int branch = Enumerable.Range(0, 3).Single(i => i != a && i != b);
                    reference = ports[branch].Direction;
                    result.Label = "Té · axe du passage principal";
                }
                else
                {
                    reference = Math.Abs(run.Z) < 0.95 ? new Vector3D(0, 0, 1) : new Vector3D(0, 1, 0);
                    result.Label = "Pièce droite · axe de canalisation";
                }
                result.FlipAxis = Unit(reference - result.Axis * Vector3D.DotProduct(reference, result.Axis));
            }
            else
            {
                // Keep the only connected end fixed; otherwise use the first stable connector.
                int anchor = ports.Count(p => p.Connected) == 1 ? Array.FindIndex(ports, p => p.Connected) : 0;
                result.Center = ports[anchor].Position;
                result.Axis = Positive(ports[anchor].Direction);
                result.FlipCenter = Mid(ports[0].Position, ports[1].Position);
                result.FlipAxis = Unit(Unit(ports[0].Direction) + Unit(ports[1].Direction));
                result.Label = "Coude · pivot sur une extrémité";
            }
            return result;
        }

        internal Matrix3D Rotation(double degrees, bool flip)
        {
            var matrix = Matrix3D.Identity;
            matrix.RotateAt(new Quaternion(flip ? FlipAxis : Axis, degrees), flip ? FlipCenter : Center);
            return matrix;
        }

        // Maps each OLD connected socket to the NEW port occupying the same socket.
        // Unconnected outlets may move freely. A missing or ambiguous match disables the action.
        internal Dictionary<string, string> ConnectionMap(double degrees, bool flip)
        {
            Matrix3D matrix = Rotation(degrees, flip);
            var matches = new Dictionary<string, string>();
            var occupied = new HashSet<string>();
            foreach (var old in Ports.Where(p => p.Connected))
            {
                var candidates = Ports.Where(p => p.Shape == old.Shape
                    && Math.Abs(p.SizeA - old.SizeA) < 1e-6 && Math.Abs(p.SizeB - old.SizeB) < 1e-6
                    && (matrix.Transform(p.Position) - old.Position).Length < 1e-5
                    && Vector3D.DotProduct(Unit(matrix.Transform(p.Direction)), Unit(old.Direction)) > 0.9999).ToArray();
                if (candidates.Length != 1 || !occupied.Add(candidates[0].Key)) return null;
                matches.Add(old.Key, candidates[0].Key);
            }
            return matches;
        }
    }
}

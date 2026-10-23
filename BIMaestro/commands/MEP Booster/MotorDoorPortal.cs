using System;
using System.Collections.Generic;

namespace BIMaestro.MepBooster
{
    // Nominal aperture check in addition to the actual wall geometry. Coordinates
    // use the same unit as the model; axes must be orthonormal. The frame origin
    // is at the middle of the aperture's width and at its sill elevation.
    internal sealed class MotorDoorPortal
    {
        internal readonly string Name;
        internal bool Target;
        private readonly MotorVector _origin, _widthAxis, _normal, _up;
        private readonly double _halfWidth, _height, _halfDepth;
        internal MotorVector Origin => _origin;
        internal MotorVector WidthAxis => _widthAxis;
        internal MotorVector Normal => _normal;
        internal double Width => _halfWidth * 2;
        internal double Height => _height;
        internal double Depth => _halfDepth * 2;

        internal MotorDoorPortal(string name, MotorVector origin, MotorVector widthAxis,
            MotorVector normal, MotorVector up, double width, double height, double depth)
        {
            Name = name;
            _origin = origin; _widthAxis = widthAxis; _normal = normal; _up = up;
            _halfWidth = width / 2; _height = height; _halfDepth = depth / 2;
        }

        // For a translation, pass the eight corners at each end. Their convex
        // hull is the exact swept box. Clipping that hull by the wall slab gives
        // the part that must fit through the opening, including diagonal moves.
        internal bool AcceptsSweep(IList<MotorVector> vertices)
        {
            const double tolerance = 1e-7;
            var local = new MotorVector[vertices.Count];
            double minN = double.PositiveInfinity, maxN = double.NegativeInfinity;
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
            for (int i = 0; i < vertices.Count; i++)
            {
                var offset = vertices[i] - _origin;
                var point = new MotorVector(offset.Dot(_widthAxis), offset.Dot(_normal), offset.Dot(_up));
                local[i] = point;
                minN = Math.Min(minN, point.Y); maxN = Math.Max(maxN, point.Y);
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minZ = Math.Min(minZ, point.Z); maxZ = Math.Max(maxZ, point.Z);
            }
            // Another opening in the same wall must not be constrained by this
            // door's infinite plane. The wall solids still forbid crossing it.
            if (minN > _halfDepth + tolerance || maxN < -_halfDepth - tolerance
                || minX > _halfWidth + tolerance || maxX < -_halfWidth - tolerance
                || minZ > _height + tolerance || maxZ < -tolerance) return true;

            minX = minZ = double.PositiveInfinity;
            maxX = maxZ = double.NegativeInfinity;
            void Include(MotorVector point)
            {
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minZ = Math.Min(minZ, point.Z); maxZ = Math.Max(maxZ, point.Z);
            }
            for (int i = 0; i < local.Length; i++)
            {
                var a = local[i];
                if (a.Y >= -_halfDepth - tolerance && a.Y <= _halfDepth + tolerance) Include(a);
                // All hull edges are among these pairs; extra chords remain
                // inside the convex hull and cannot enlarge its true extrema.
                for (int j = 0; j < i; j++)
                {
                    var b = local[j];
                    double denominator = b.Y - a.Y;
                    if (Math.Abs(denominator) <= tolerance) continue;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        double t = (side * _halfDepth - a.Y) / denominator;
                        if (t >= -tolerance && t <= 1 + tolerance) Include(a + (b - a) * Math.Max(0, Math.Min(1, t)));
                    }
                }
            }
            if (double.IsPositiveInfinity(minX)) return true;
            if (minX > _halfWidth + tolerance || maxX < -_halfWidth - tolerance
                || minZ > _height + tolerance || maxZ < -tolerance) return true;
            return minX >= -_halfWidth - tolerance && maxX <= _halfWidth + tolerance
                && minZ >= -tolerance && maxZ <= _height + tolerance;
        }
    }
}

using System;
using System.Threading;
using System.Windows.Media.Media3D;
using BIMaestro.VideoGames;

namespace BIMaestro.Navisworks.Services
{
    internal static class StraightTubeMesh
    {
        private const double MaxRadialErrorFeet = 5.0 / 304.8;

        // Only replace an isolated, straight, fully circular piece. Junctions,
        // elbows, flanges and continuous meshes with mixed shapes stay untouched.
        internal static GameMeshData Simplify(GameMeshData source, CancellationToken cancellation)
        {
            if (source.Indices.Count < 768 || source.Positions.Count < 48) return source;
            var center = new Point3D();
            var min = source.Positions[0];
            var max = min;
            foreach (var p in source.Positions)
            {
                center += (Vector3D)p;
                min = new Point3D(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new Point3D(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
            center = (Point3D)((Vector3D)center / source.Positions.Count);
            var extent = max - min;
            var axis = extent.X >= extent.Y && extent.X >= extent.Z ? new Vector3D(1,0,0)
                : extent.Y >= extent.Z ? new Vector3D(0,1,0) : new Vector3D(0,0,1);
            // Find the longitudinal direction without assuming a world axis.
            for (int pass = 0; pass < 7; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                var next = new Vector3D();
                foreach (var p in source.Positions)
                {
                    var v = p - center;
                    next += v * Vector3D.DotProduct(v, axis);
                }
                if (next.LengthSquared < 1e-20) return source;
                next.Normalize();
                if (Vector3D.DotProduct(axis, next) > .999999999) break;
                axis = next;
            }

            // Refine from two well-separated lateral face normals.
            var lateral = new Vector3D();
            bool refined = false;
            for (int i = 0; i < source.Indices.Count; i += 3)
            {
                var a = source.Positions[source.Indices[i]];
                var b = source.Positions[source.Indices[i + 1]];
                var c = source.Positions[source.Indices[i + 2]];
                var n = Vector3D.CrossProduct(b - a, c - a);
                if (n.LengthSquared < 1e-20) continue;
                n.Normalize();
                if (Math.Abs(Vector3D.DotProduct(n, axis)) > .08) continue;
                if (lateral.LengthSquared == 0) { lateral = n; continue; }
                if (Math.Abs(Vector3D.DotProduct(n, lateral)) > .7) continue;
                var exact = Vector3D.CrossProduct(n, lateral);
                exact.Normalize();
                double alignment = Vector3D.DotProduct(exact, axis);
                if (Math.Abs(alignment) < .99) return source;
                axis = alignment < 0 ? -exact : exact;
                refined = true;
                break;
            }
            if (!refined) return source;

            var right = Vector3D.CrossProduct(Math.Abs(axis.Z) < .9 ? new Vector3D(0,0,1) : new Vector3D(0,1,0), axis);
            right.Normalize();
            var up = Vector3D.CrossProduct(axis, right);
            Point3D Project(Point3D p)
            {
                var v = p - center;
                return new Point3D(Vector3D.DotProduct(v, right), Vector3D.DotProduct(v, up), Vector3D.DotProduct(v, axis));
            }
            var bounds = Rect3D.Empty;
            foreach (var p in source.Positions) bounds.Union(Project(p));
            double x0 = bounds.X + bounds.SizeX / 2, y0 = bounds.Y + bounds.SizeY / 2;
            double radius = Math.Max(bounds.SizeX, bounds.SizeY) / 2;
            if (radius < 1e-5 || bounds.SizeZ < 6 * radius || Math.Abs(bounds.SizeX - bounds.SizeY) > radius * .02)
                return source;

            double head = bounds.Z, tail = bounds.Z + bounds.SizeZ;
            double radialTolerance = Math.Min(1.0 / 304.8, radius * .016);
            double axialTolerance = Math.Min(1.0 / 304.8, radius * .01);
            double Radius(Point3D q) => Math.Sqrt((q.X - x0) * (q.X - x0) + (q.Y - y0) * (q.Y - y0));
            double inner = 0;
            int outerHead = 0, outerTail = 0, innerHead = 0, innerTail = 0;
            foreach (var p in source.Positions)
            {
                var q = Project(p);
                bool atHead = Math.Abs(q.Z - head) <= axialTolerance;
                bool atTail = Math.Abs(q.Z - tail) <= axialTolerance;
                double r = Radius(q);
                if (Math.Abs(r - radius) <= radialTolerance)
                {
                    if (atHead) outerHead++; else if (atTail) outerTail++;
                }
                else if (r > radialTolerance)
                {
                    if (r > radius - radialTolerance) return source;
                    if (inner == 0) inner = r;
                    if (Math.Abs(r - inner) > radialTolerance) return source;
                    if (atHead) innerHead++; else if (atTail) innerTail++;
                }
                else if (!atHead && !atTail) return source;
            }
            if (outerHead < 12 || outerTail < 12 || (inner > 0 && (innerHead < 12 || innerTail < 12))) return source;

            double outerArea = 0, innerArea = 0, headArea = 0, tailArea = 0;
            for (int i = 0; i < source.Indices.Count; i += 3)
            {
                if ((i & 2047) == 0) cancellation.ThrowIfCancellationRequested();
                var a = Project(source.Positions[source.Indices[i]]);
                var b = Project(source.Positions[source.Indices[i + 1]]);
                var c = Project(source.Positions[source.Indices[i + 2]]);
                var normal = Vector3D.CrossProduct(b - a, c - a);
                double area = normal.Length / 2;
                if (area < 1e-12) continue;
                if (Math.Max(a.Z, Math.Max(b.Z, c.Z)) - Math.Min(a.Z, Math.Min(b.Z, c.Z)) <= axialTolerance)
                {
                    if (Math.Abs(a.Z - head) <= axialTolerance) headArea += area;
                    else if (Math.Abs(a.Z - tail) <= axialTolerance) tailArea += area;
                    else return source;
                    continue;
                }
                if (Math.Abs(normal.Z) > area * .002) return source;
                double ra = Radius(a), rb = Radius(b), rc = Radius(c);
                if (Math.Abs(ra - radius) <= radialTolerance && Math.Abs(rb - radius) <= radialTolerance &&
                    Math.Abs(rc - radius) <= radialTolerance) outerArea += area;
                else if (inner > 0 && Math.Abs(ra - inner) <= radialTolerance && Math.Abs(rb - inner) <= radialTolerance &&
                    Math.Abs(rc - inner) <= radialTolerance) innerArea += area;
                else return source;
            }
            bool Similar(double actual, double expected) => Math.Abs(actual - expected) <= expected * .015;
            if (!Similar(outerArea, 2 * Math.PI * radius * bounds.SizeZ) ||
                (inner > 0 && !Similar(innerArea, 2 * Math.PI * inner * bounds.SizeZ))) return source;
            double capArea = Math.PI * (radius * radius - inner * inner);
            bool capHead = headArea > capArea * .001, capTail = tailArea > capArea * .001;
            if ((capHead && !Similar(headArea, capArea)) || (capTail && !Similar(tailArea, capArea))) return source;

            double radialError = Math.Min(MaxRadialErrorFeet, radius * .02);
            // Multiple of four keeps the fitted diameter on the two section axes.
            double required = Math.PI / Math.Acos(1 - radialError / radius);
            if (!(required <= source.Indices.Count / 6d)) return source;
            int sides = Math.Max(12, (int)Math.Ceiling(required / 4) * 4);
            if ((long)sides * (inner > 0 ? 8 : 4) * 3 >= source.Indices.Count) return source;
            var mesh = new GameMeshData { IsTransparent = source.IsTransparent, HasCompleteNormals = true };
            Point3D Ring(double r, double z, int i)
            {
                double angle = 2 * Math.PI * (i % sides) / sides;
                return center + right * (x0 + r * Math.Cos(angle)) + up * (y0 + r * Math.Sin(angle)) + axis * z;
            }
            void Triangle(Point3D a, Point3D b, Point3D c, bool reverse)
            {
                if (reverse) { var t = b; b = c; c = t; }
                var normal = Vector3D.CrossProduct(b - a, c - a);
                normal.Normalize();
                foreach (var p in new[] {a,b,c})
                {
                    mesh.Indices.Add(mesh.Positions.Count);
                    mesh.Positions.Add(p);
                    mesh.VertexNormals.Add(normal);
                    mesh.VertexColors.Add(source.VertexColors[0]);
                    mesh.ElementIndices.Add(source.ElementIndices[0]);
                }
            }
            void Side(double r, int i, bool reverse)
            {
                var a = Ring(r, head, i); var b = Ring(r, head, i + 1);
                var c = Ring(r, tail, i + 1); var d = Ring(r, tail, i);
                Triangle(a,b,c,reverse); Triangle(a,c,d,reverse);
            }
            void Cap(double z, int i, bool reverse)
            {
                var a = Ring(radius,z,i); var b = Ring(radius,z,i+1); var c = Ring(inner,z,i+1);
                Triangle(a,b,c,reverse);
                if (inner > 0) Triangle(a,c,Ring(inner,z,i),reverse);
            }
            for (int i = 0; i < sides; i++)
            {
                Side(radius,i,false);
                if (inner > 0) Side(inner,i,true);
                if (capHead) Cap(head,i,true);
                if (capTail) Cap(tail,i,false);
            }
            mesh.Freeze();
            return mesh;
        }
    }
}

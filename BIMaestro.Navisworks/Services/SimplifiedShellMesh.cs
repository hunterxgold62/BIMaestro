using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BIMaestro.VideoGames;

namespace BIMaestro.Navisworks.Services
{
    internal enum WebModelScope { Complete, Exterior, SimplifiedShell, SimplifiedModel }

    // Cluster vertices while reading the source so discarded triangles never enter the scene.
    internal sealed class SimplifiedShellMesh
    {
        internal const int InitialSubdivisions = 80;
        private const double TargetCellDiagonalFeet = 20.0 / 304.8;
        private static readonly Regex InteriorHostsAndNetworks = new Regex(
            @"\b(?:murs?|walls?|sols?|floors?|dalles?|slabs?|planchers?|tuyaux?|tuyauterie|canalisations?|pipes?|piping|gaines?|ducts?|brides?|flanges?|raccords?|fittings?)\b|\b(?:ifc(?:wall|slab|pipe|duct|flowsegment|flowfitting)\w*|ost_(?:walls|floors|pipe\w*|duct\w*))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private readonly GameMeshData _output;
        private readonly Point3D _origin;
        private readonly Point3D _max;
        private readonly Vector3D _step;
        private readonly Color _color;
        private readonly int _element;
        private readonly Dictionary<(long X, long Y, long Z), int> _vertices = new Dictionary<(long, long, long), int>();
        private readonly HashSet<(int A, int B, int C)> _faces = new HashSet<(int, int, int)>();
        private (Point3D A, Point3D B, Point3D C)? _firstFace;

        public SimplifiedShellMesh(GameMeshData output, Point3D min, Point3D max, int subdivisions, Color color, int element, double? maxCellDiagonal = null)
        {
            _output = output;
            _output.HasCompleteNormals = false;
            _origin = min;
            _max = max;
            var size = max - min;
            double longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
            // Aim for a 20 mm initial cell diagonal while bounding the number
            // of cells for very large parts. Budget retries relax both limits.
            double target = TargetCellDiagonalFeet / Math.Sqrt(3) * InitialSubdivisions / subdivisions;
            double step = Math.Max(1e-6, Math.Max(longest / (subdivisions * 1.5), Math.Min(longest / subdivisions, target)));
            if (maxCellDiagonal.HasValue) step = Math.Min(step, maxCellDiagonal.Value / Math.Sqrt(3));
            _step = new Vector3D(AxisStep(size.X, step), AxisStep(size.Y, step), AxisStep(size.Z, step));
            _color = color;
            _element = element;
        }

        private static double AxisStep(double size, double step) => Math.Max(1e-6, Math.Min(step, size / 2));

        public void AddTriangle(Point3D a, Point3D b, Point3D c)
        {
            if (Vector3D.CrossProduct(b - a, c - a).LengthSquared < 1e-20) return;
            if (_firstFace == null) _firstFace = (a, b, c);

            var ka = Key(a);
            var kb = Key(b);
            var kc = Key(c);
            if (ka == kb || ka == kc || kb == kc) return;

            int ia = Vertex(ka, a), ib = Vertex(kb, b), ic = Vertex(kc, c);
            // Deduplicate cyclic permutations and keep opposite-facing surfaces.
            var face = ia < ib && ia < ic ? (ia, ib, ic) : ib < ic ? (ib, ic, ia) : (ic, ia, ib);
            if (!_faces.Add(face)) return;
            _output.Indices.Add(ia);
            _output.Indices.Add(ib);
            _output.Indices.Add(ic);
        }

        public void Complete(CancellationToken cancellation)
        {
            var originalPositions = _output.Positions.ToArray();
            var sourceIndices = _output.Indices.ToArray();
            _output.Positions.Clear();
            _output.Indices.Clear();
            _output.VertexColors.Clear();
            _output.ElementIndices.Clear();

            var normals = new List<SurfaceNormal>[originalPositions.Length];
            for (int i = 0; i < sourceIndices.Length; i += 3)
            {
                if ((i & 2047) == 0) cancellation.ThrowIfCancellationRequested();
                int a = sourceIndices[i], b = sourceIndices[i + 1], c = sourceIndices[i + 2];
                var normal = Vector3D.CrossProduct(originalPositions[b] - originalPositions[a],
                    originalPositions[c] - originalPositions[a]);
                if (normal.LengthSquared < 1e-20) continue;
                normal.Normalize();
                _output.Indices.Add(SurfaceVertex(a, normal));
                _output.Indices.Add(SurfaceVertex(b, normal));
                _output.Indices.Add(SurfaceVertex(c, normal));
            }

            foreach (var groups in normals)
            {
                if (groups == null) continue;
                foreach (var group in groups)
                {
                    var normal = group.Accumulated;
                    normal.Normalize();
                    _output.VertexNormals[group.Index] = normal;
                }
            }

            if (_output.Indices.Count == 0 && _firstFace.HasValue)
            {
                var face = _firstFace.Value;
                var normal = Vector3D.CrossProduct(face.B - face.A, face.C - face.A);
                normal.Normalize();
                _output.Indices.Add(WriteVertex(face.A));
                _output.Indices.Add(WriteVertex(face.B));
                _output.Indices.Add(WriteVertex(face.C));
                for (int i = 0; i < 3; i++) _output.VertexNormals.Add(normal);
            }
            _output.HasCompleteNormals = true;

            int SurfaceVertex(int vertex, Vector3D normal)
            {
                // Use the same position across sharp edges, with a separate shading normal.
                var groups = normals[vertex] ?? (normals[vertex] = new List<SurfaceNormal>(1));
                foreach (var group in groups)
                {
                    if (Vector3D.DotProduct(group.Reference, normal) < .8) continue;
                    group.Accumulated += normal;
                    return group.Index;
                }
                int index = WriteVertex(originalPositions[vertex]);
                _output.VertexNormals.Add(normal);
                groups.Add(new SurfaceNormal(index, normal));
                return index;
            }
        }

        private sealed class SurfaceNormal
        {
            public readonly int Index;
            public readonly Vector3D Reference;
            public Vector3D Accumulated;

            public SurfaceNormal(int index, Vector3D normal)
            {
                Index = index;
                Reference = Accumulated = normal;
            }
        }

        private static long AxisKey(double value, double min, double max, double step)
        {
            // Keep the outer planes in their own cells so a nearby interior
            // vertex cannot replace a surface extremum.
            if (Math.Abs(value - min) <= 1e-6) return long.MinValue;
            if (Math.Abs(value - max) <= 1e-6) return long.MaxValue;
            return (long)Math.Round((value - min) / step);
        }

        private (long X, long Y, long Z) Key(Point3D p) =>
            (AxisKey(p.X, _origin.X, _max.X, _step.X), AxisKey(p.Y, _origin.Y, _max.Y, _step.Y),
                AxisKey(p.Z, _origin.Z, _max.Z, _step.Z));

        private static double Center(long key, double min, double max, double step) =>
            key == long.MinValue ? min : key == long.MaxValue ? max : min + key * step;

        private int Vertex((long X, long Y, long Z) key, Point3D point)
        {
            if (_vertices.TryGetValue(key, out int index))
            {
                var center = new Point3D(Center(key.X, _origin.X, _max.X, _step.X),
                    Center(key.Y, _origin.Y, _max.Y, _step.Y), Center(key.Z, _origin.Z, _max.Z, _step.Z));
                if ((point - center).LengthSquared < (_output.Positions[index] - center).LengthSquared)
                    _output.Positions[index] = point;
                return index;
            }
            index = WriteVertex(point);
            _vertices.Add(key, index);
            return index;
        }

        private int WriteVertex(Point3D point)
        {
            int index = _output.Positions.Count;
            _output.Positions.Add(point);
            _output.VertexColors.Add(_color);
            _output.ElementIndices.Add(_element);
            return index;
        }

        internal static bool IsInteriorGeometryProtected(GameElementData element)
        {
            return InteriorHostsAndNetworks.IsMatch(element.Category)
                || InteriorHostsAndNetworks.IsMatch(element.TypeName)
                || InteriorHostsAndNetworks.IsMatch(element.Name);
        }

        internal static bool HasVariedSurfaceNormals(GameMeshData source, CancellationToken cancellation)
        {
            // Panels and box-like parts usually have at most three plane directions.
            // Opposite sides count as the same direction. Mixed geometry stays in
            // the detailed group as soon as a fourth direction is found.
            var directions = new Vector3D[3];
            int count = 0;
            for (int i = 0; i < source.Indices.Count; i += 3)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var a = source.Positions[source.Indices[i]];
                var b = source.Positions[source.Indices[i + 1]];
                var c = source.Positions[source.Indices[i + 2]];
                var normal = Vector3D.CrossProduct(b - a, c - a);
                if (normal.LengthSquared < 1e-20) continue;
                normal.Normalize();

                int direction = 0;
                while (direction < count && Math.Abs(Vector3D.DotProduct(directions[direction], normal)) < .9995)
                    direction++;
                if (direction < count) continue;
                if (count == directions.Length) return true;
                directions[count++] = normal;
            }
            return false;
        }

        internal static IEnumerable<(int Flat, int Detailed)> BudgetSteps()
        {
            yield return (32, InitialSubdivisions);
            yield return (18, 64);
            yield return (12, 48);
            yield return (8, 36);
            yield return (4, 26);
            yield return (4, 18);
            yield return (4, 12);
            yield return (4, 8);
            yield return (4, 4);
        }

        public static GameMeshData Reduce(GameMeshData source, int subdivisions, CancellationToken cancellation)
            => Reduce(source, subdivisions, cancellation, null);

        internal static GameMeshData ReduceInterior(GameMeshData source, int subdivisions, CancellationToken cancellation)
            => Reduce(source, subdivisions, cancellation, TargetCellDiagonalFeet);

        private static GameMeshData Reduce(GameMeshData source, int subdivisions, CancellationToken cancellation,
            double? maxCellDiagonal)
        {
            var min = source.Positions[0];
            var max = min;
            foreach (var point in source.Positions)
            {
                min = new Point3D(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                max = new Point3D(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
            }
            var result = new GameMeshData { IsTransparent = source.IsTransparent };
            var simplify = new SimplifiedShellMesh(result, min, max, subdivisions, source.VertexColors[0], source.ElementIndices[0], maxCellDiagonal);
            for (int i = 0; i < source.Indices.Count; i += 3)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                simplify.AddTriangle(source.Positions[source.Indices[i]], source.Positions[source.Indices[i + 1]],
                    source.Positions[source.Indices[i + 2]]);
            }
            simplify.Complete(cancellation);
            if (result.Indices.Count == 0 || result.Indices.Count >= source.Indices.Count) return source;
            if (maxCellDiagonal.HasValue && !PreservesExtent(result, min, max, maxCellDiagonal.Value)) return source;
            result.Freeze();
            return result;
        }

        private static bool PreservesExtent(GameMeshData mesh, Point3D min, Point3D max, double tolerance)
        {
            var bounds = Rect3D.Empty;
            foreach (var point in mesh.Positions) bounds.Union(point);
            return mesh.Indices.Count >= 12 && bounds.X - min.X <= tolerance && bounds.Y - min.Y <= tolerance
                && bounds.Z - min.Z <= tolerance && max.X - (bounds.X + bounds.SizeX) <= tolerance
                && max.Y - (bounds.Y + bounds.SizeY) <= tolerance && max.Z - (bounds.Z + bounds.SizeZ) <= tolerance;
        }
    }
}

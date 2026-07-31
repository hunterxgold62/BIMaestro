using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace BIMaestro.VideoGames
{
    /// <summary>
    /// Index spatial et requêtes physiques du mode jeu.
    /// Les distances sont exprimées en pieds, l'unité interne native de Revit.
    /// </summary>
    internal sealed class GameCollisionWorld
    {
        private const double WalkableNormalZ = 0.54; // pente maximale proche de 57°
        private const int MaximumCellsPerTriangle = 512;

        private readonly IList<GameTriangle> _triangles;
        private readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
        private readonly List<int> _largeTriangles = new List<int>();
        private readonly List<int> _queryBuffer = new List<int>(256);
        private readonly int[] _queryMarks;
        private readonly double _cellSize;
        private int _queryGeneration;

        public GameCollisionWorld(GameSceneData scene)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            _triangles = scene.Triangles;
            _queryMarks = new int[_triangles.Count];

            double horizontalDiagonal = Math.Sqrt(
                scene.Bounds.SizeX * scene.Bounds.SizeX +
                scene.Bounds.SizeY * scene.Bounds.SizeY);
            _cellSize = Math.Max(5.0, Math.Min(20.0, horizontalDiagonal / 80.0));
            BuildIndex();
            scene.SpawnFootPosition = FindSpawn(scene);
        }

        public bool TryFindGround(double x, double y, double maximumZ, double minimumZ, out double groundZ)
        {
            return TryFindGroundDetailed(
                x,
                y,
                maximumZ,
                minimumZ,
                false,
                out groundZ,
                out _);
        }

        public bool TryFindGround(
            double x,
            double y,
            double maximumZ,
            double minimumZ,
            out double groundZ,
            out double groundNormalZ)
        {
            return TryFindGroundDetailed(
                x,
                y,
                maximumZ,
                minimumZ,
                false,
                out groundZ,
                out groundNormalZ);
        }

        public bool TryFindCeiling(double x, double y, double minimumZ, double maximumZ, out double ceilingZ)
        {
            ceilingZ = double.MaxValue;
            bool found = false;

            List<int> candidates = CollectPointCandidates(x, y);
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                int index = candidates[candidateIndex];
                GameTriangle triangle = _triangles[index];
                if (Math.Abs(triangle.Normal.Z) < WalkableNormalZ)
                    continue;
                if (triangle.MaxZ < minimumZ || triangle.MinZ > maximumZ)
                    continue;
                if (!TryInterpolateZ(triangle, x, y, out double z))
                    continue;
                if (z >= minimumZ && z <= maximumZ && z < ceilingZ)
                {
                    ceilingZ = z;
                    found = true;
                }
            }

            return found;
        }

        public bool IsBodyBlocked(Point3D foot, double radius, double height, double stepHeight)
        {
            double minX = foot.X - radius;
            double maxX = foot.X + radius;
            double minY = foot.Y - radius;
            double maxY = foot.Y + radius;
            double bodyMinZ = foot.Z + stepHeight + 0.05;
            double bodyMaxZ = foot.Z + height - 0.12;

            List<int> candidates = CollectAreaCandidates(minX, minY, maxX, maxY);
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                int index = candidates[candidateIndex];
                GameTriangle triangle = _triangles[index];
                if (triangle.MaxX < minX || triangle.MinX > maxX ||
                    triangle.MaxY < minY || triangle.MinY > maxY ||
                    triangle.MaxZ < bodyMinZ || triangle.MinZ > bodyMaxZ)
                    continue;

                // Les surfaces praticables sont résolues par la requête de sol.
                // Les plafonds sont résolus séparément pendant le saut.
                if (Math.Abs(triangle.Normal.Z) >= WalkableNormalZ)
                    continue;

                double available = Math.Max(0.0, bodyMaxZ - bodyMinZ);
                const int samples = 4;
                for (int i = 0; i <= samples; i++)
                {
                    double ratio = samples == 0 ? 0.0 : (double)i / samples;
                    var center = new Point3D(foot.X, foot.Y, bodyMinZ + available * ratio);
                    Point3D closest = ClosestPointOnTriangle(center, triangle.A, triangle.B, triangle.C);
                    if ((closest - center).LengthSquared < radius * radius)
                        return true;
                }
            }

            return false;
        }

        public IList<GameMapSegment> GetMiniMapSegments(
            double centerX,
            double centerY,
            double sliceZ,
            double radius,
            int maximumSegments)
        {
            var segments = new List<GameMapSegment>(
                Math.Min(maximumSegments, 1024));
            var knownSegments = new HashSet<MiniMapSegmentKey>();
            List<int> candidates = CollectMiniMapCandidates(
                centerX - radius,
                centerY - radius,
                centerX + radius,
                centerY + radius);

            for (int candidateIndex = 0;
                candidateIndex < candidates.Count;
                candidateIndex++)
            {
                GameTriangle triangle = _triangles[candidates[candidateIndex]];
                if (Math.Abs(triangle.Normal.Z) > 0.82 ||
                    triangle.MinZ > sliceZ ||
                    triangle.MaxZ < sliceZ ||
                    triangle.MaxX < centerX - radius ||
                    triangle.MinX > centerX + radius ||
                    triangle.MaxY < centerY - radius ||
                    triangle.MinY > centerY + radius)
                {
                    continue;
                }

                if (TrySliceTriangle(
                        triangle,
                        sliceZ,
                        out GameMapSegment segment) &&
                    knownSegments.Add(new MiniMapSegmentKey(segment)))
                {
                    segments.Add(segment);
                }
            }

            return SelectRepresentativeMiniMapSegments(
                segments,
                centerX,
                centerY,
                radius,
                maximumSegments);
        }

        private static IList<GameMapSegment>
            SelectRepresentativeMiniMapSegments(
                List<GameMapSegment> segments,
                double centerX,
                double centerY,
                double radius,
                int maximumSegments)
        {
            if (segments.Count <= maximumSegments)
                return segments;

            const int bucketCountPerAxis = 16;
            var buckets =
                new List<GameMapSegment>[
                    bucketCountPerAxis * bucketCountPerAxis];
            double minimumX = centerX - radius;
            double minimumY = centerY - radius;
            double bucketScale =
                bucketCountPerAxis / Math.Max(0.01, radius * 2.0);

            foreach (GameMapSegment segment in segments)
            {
                double middleX = (segment.X1 + segment.X2) * 0.5;
                double middleY = (segment.Y1 + segment.Y2) * 0.5;
                int bucketX = Math.Max(
                    0,
                    Math.Min(
                        bucketCountPerAxis - 1,
                        (int)((middleX - minimumX) * bucketScale)));
                int bucketY = Math.Max(
                    0,
                    Math.Min(
                        bucketCountPerAxis - 1,
                        (int)((middleY - minimumY) * bucketScale)));
                int bucketIndex =
                    bucketY * bucketCountPerAxis + bucketX;
                if (buckets[bucketIndex] == null)
                    buckets[bucketIndex] = new List<GameMapSegment>();
                buckets[bucketIndex].Add(segment);
            }

            foreach (List<GameMapSegment> bucket in buckets)
            {
                bucket?.Sort((left, right) =>
                    SegmentLengthSquared(right).CompareTo(
                        SegmentLengthSquared(left)));
            }

            var result = new List<GameMapSegment>(maximumSegments);
            for (int depth = 0;
                result.Count < maximumSegments;
                depth++)
            {
                bool foundAtDepth = false;
                foreach (List<GameMapSegment> bucket in buckets)
                {
                    if (bucket == null || depth >= bucket.Count)
                        continue;
                    result.Add(bucket[depth]);
                    foundAtDepth = true;
                    if (result.Count >= maximumSegments)
                        break;
                }

                if (!foundAtDepth)
                    break;
            }

            return result;
        }

        private static double SegmentLengthSquared(GameMapSegment segment)
        {
            double deltaX = segment.X2 - segment.X1;
            double deltaY = segment.Y2 - segment.Y1;
            return deltaX * deltaX + deltaY * deltaY;
        }

        private void BuildIndex()
        {
            for (int index = 0; index < _triangles.Count; index++)
            {
                GameTriangle triangle = _triangles[index];
                int minX = ToCell(triangle.MinX);
                int maxX = ToCell(triangle.MaxX);
                int minY = ToCell(triangle.MinY);
                int maxY = ToCell(triangle.MaxY);
                long cellCount = (long)(maxX - minX + 1) * (maxY - minY + 1);

                if (cellCount > MaximumCellsPerTriangle)
                {
                    _largeTriangles.Add(index);
                    continue;
                }

                for (int cellX = minX; cellX <= maxX; cellX++)
                {
                    for (int cellY = minY; cellY <= maxY; cellY++)
                    {
                        long key = CellKey(cellX, cellY);
                        if (!_grid.TryGetValue(key, out List<int> bucket))
                        {
                            bucket = new List<int>();
                            _grid.Add(key, bucket);
                        }
                        bucket.Add(index);
                    }
                }
            }
        }

        private Point3D FindSpawn(GameSceneData scene)
        {
            Rect3D bounds = scene.Bounds;
            Point3D eye = scene.ViewEye;
            bool eyeNearModel =
                eye.X >= bounds.X - 5 && eye.X <= bounds.X + bounds.SizeX + 5 &&
                eye.Y >= bounds.Y - 5 && eye.Y <= bounds.Y + bounds.SizeY + 5 &&
                eye.Z >= bounds.Z && eye.Z <= bounds.Z + bounds.SizeZ + 15;

            if (eyeNearModel &&
                TryFindGroundDetailed(
                    eye.X,
                    eye.Y,
                    eye.Z + 1.0,
                    bounds.Z - 2.0,
                    true,
                    out double eyeGround,
                    out _))
            {
                return new Point3D(eye.X, eye.Y, eyeGround + 0.04);
            }

            double targetX = bounds.X + bounds.SizeX * 0.5;
            double targetY = bounds.Y + bounds.SizeY * 0.5;
            Vector3D forward = scene.ViewForward;
            double targetPlaneZ = bounds.Z + bounds.SizeZ * 0.35;
            if (Math.Abs(forward.Z) > 0.08)
            {
                double distance = (targetPlaneZ - eye.Z) / forward.Z;
                if (distance > 0)
                {
                    targetX = Clamp(eye.X + forward.X * distance, bounds.X, bounds.X + bounds.SizeX);
                    targetY = Clamp(eye.Y + forward.Y * distance, bounds.Y, bounds.Y + bounds.SizeY);
                }
            }

            List<GameTriangle> preferred = _triangles
                .Where(t => t.PreferredWalkable && t.Normal.Z >= WalkableNormalZ && t.HorizontalArea > 0.05)
                .ToList();
            List<GameTriangle> candidates = preferred.Count > 0
                ? preferred
                : _triangles.Where(t => t.Normal.Z >= WalkableNormalZ && t.HorizontalArea > 0.05).ToList();

            if (candidates.Count == 0)
                return new Point3D(targetX, targetY, bounds.Z + bounds.SizeZ + 3.0);

            // Favorise un niveau bas (entrée/RDC) tout en restant proche du centre
            // visé par la caméra Revit. Cela évite de faire apparaître le joueur
            // automatiquement sur une toiture.
            double[] elevations = candidates.Select(t => t.Centroid.Z).OrderBy(z => z).ToArray();
            double lowLevel = elevations[Math.Min(elevations.Length - 1, elevations.Length / 5)];
            double acceptedTop = lowLevel + 6.0;

            GameTriangle selected = candidates
                .Where(t => t.Centroid.Z <= acceptedTop)
                .OrderBy(t => SquaredDistance2D(t.Centroid.X, t.Centroid.Y, targetX, targetY))
                .ThenByDescending(t => t.HorizontalArea)
                .FirstOrDefault()
                ?? candidates.OrderBy(t => SquaredDistance2D(t.Centroid.X, t.Centroid.Y, targetX, targetY)).First();

            Point3D centroid = selected.Centroid;
            if (TryFindGroundDetailed(
                centroid.X,
                centroid.Y,
                selected.MaxZ + 1.0,
                selected.MinZ - 2.0,
                selected.PreferredWalkable,
                out double spawnZ,
                out _))
            {
                return new Point3D(centroid.X, centroid.Y, spawnZ + 0.04);
            }

            return new Point3D(centroid.X, centroid.Y, centroid.Z + 0.04);
        }

        private bool TryFindGroundDetailed(
            double x,
            double y,
            double maximumZ,
            double minimumZ,
            bool preferredOnly,
            out double groundZ,
            out double groundNormalZ)
        {
            groundZ = double.MinValue;
            groundNormalZ = 1.0;
            bool found = false;

            List<int> candidates = CollectPointCandidates(x, y);
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                int index = candidates[candidateIndex];
                GameTriangle triangle = _triangles[index];
                if (triangle.Normal.Z < WalkableNormalZ)
                    continue;
                if (preferredOnly && !triangle.PreferredWalkable)
                    continue;
                if (triangle.MinZ > maximumZ || triangle.MaxZ < minimumZ)
                    continue;
                if (!TryInterpolateZ(triangle, x, y, out double z))
                    continue;
                if (z <= maximumZ + 1e-5 && z >= minimumZ - 1e-5 && z > groundZ)
                {
                    groundZ = z;
                    groundNormalZ = triangle.Normal.Z;
                    found = true;
                }
            }

            return found;
        }

        private List<int> CollectPointCandidates(double x, double y)
        {
            BeginQuery();
            if (_grid.TryGetValue(CellKey(ToCell(x), ToCell(y)), out List<int> bucket))
            {
                for (int i = 0; i < bucket.Count; i++)
                    AddQueryCandidate(bucket[i]);
            }

            for (int i = 0; i < _largeTriangles.Count; i++)
                AddQueryCandidate(_largeTriangles[i]);

            return _queryBuffer;
        }

        private List<int> CollectAreaCandidates(double minX, double minY, double maxX, double maxY)
        {
            BeginQuery();
            int firstX = ToCell(minX);
            int lastX = ToCell(maxX);
            int firstY = ToCell(minY);
            int lastY = ToCell(maxY);

            for (int cellX = firstX; cellX <= lastX; cellX++)
            {
                for (int cellY = firstY; cellY <= lastY; cellY++)
                {
                    if (!_grid.TryGetValue(CellKey(cellX, cellY), out List<int> bucket))
                        continue;
                    for (int i = 0; i < bucket.Count; i++)
                        AddQueryCandidate(bucket[i]);
                }
            }

            for (int i = 0; i < _largeTriangles.Count; i++)
                AddQueryCandidate(_largeTriangles[i]);

            return _queryBuffer;
        }

        private List<int> CollectMiniMapCandidates(
            double minX,
            double minY,
            double maxX,
            double maxY)
        {
            // La mini-carte est construite sur un thread de fond. Elle utilise
            // donc ses propres buffers au lieu des buffers très sollicités par
            // la physique sur le thread d'affichage.
            var candidates = new List<int>(1024);
            var knownCandidates = new HashSet<int>();
            int firstX = ToCell(minX);
            int lastX = ToCell(maxX);
            int firstY = ToCell(minY);
            int lastY = ToCell(maxY);

            for (int cellX = firstX; cellX <= lastX; cellX++)
            {
                for (int cellY = firstY; cellY <= lastY; cellY++)
                {
                    if (!_grid.TryGetValue(
                            CellKey(cellX, cellY),
                            out List<int> bucket))
                    {
                        continue;
                    }

                    for (int index = 0; index < bucket.Count; index++)
                    {
                        int triangleIndex = bucket[index];
                        if (knownCandidates.Add(triangleIndex))
                            candidates.Add(triangleIndex);
                    }
                }
            }

            for (int index = 0; index < _largeTriangles.Count; index++)
            {
                int triangleIndex = _largeTriangles[index];
                if (knownCandidates.Add(triangleIndex))
                    candidates.Add(triangleIndex);
            }

            return candidates;
        }

        private void BeginQuery()
        {
            _queryBuffer.Clear();
            if (_queryGeneration == int.MaxValue)
            {
                Array.Clear(_queryMarks, 0, _queryMarks.Length);
                _queryGeneration = 1;
            }
            else
            {
                _queryGeneration++;
            }
        }

        private void AddQueryCandidate(int index)
        {
            if (_queryMarks[index] == _queryGeneration)
                return;

            _queryMarks[index] = _queryGeneration;
            _queryBuffer.Add(index);
        }

        private int ToCell(double coordinate) => (int)Math.Floor(coordinate / _cellSize);

        private static long CellKey(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        private static bool TryInterpolateZ(GameTriangle triangle, double x, double y, out double z)
        {
            double denominator =
                (triangle.B.Y - triangle.C.Y) * (triangle.A.X - triangle.C.X) +
                (triangle.C.X - triangle.B.X) * (triangle.A.Y - triangle.C.Y);

            if (Math.Abs(denominator) < 1e-12)
            {
                z = 0;
                return false;
            }

            double u =
                ((triangle.B.Y - triangle.C.Y) * (x - triangle.C.X) +
                 (triangle.C.X - triangle.B.X) * (y - triangle.C.Y)) / denominator;
            double v =
                ((triangle.C.Y - triangle.A.Y) * (x - triangle.C.X) +
                 (triangle.A.X - triangle.C.X) * (y - triangle.C.Y)) / denominator;
            double w = 1.0 - u - v;
            const double tolerance = -1e-7;
            if (u < tolerance || v < tolerance || w < tolerance)
            {
                z = 0;
                return false;
            }

            z = u * triangle.A.Z + v * triangle.B.Z + w * triangle.C.Z;
            return true;
        }

        private static bool TrySliceTriangle(
            GameTriangle triangle,
            double sliceZ,
            out GameMapSegment segment)
        {
            var intersections = new Point3D[3];
            int count = 0;
            AddSliceIntersection(
                triangle.A,
                triangle.B,
                sliceZ,
                intersections,
                ref count);
            AddSliceIntersection(
                triangle.B,
                triangle.C,
                sliceZ,
                intersections,
                ref count);
            AddSliceIntersection(
                triangle.C,
                triangle.A,
                sliceZ,
                intersections,
                ref count);

            if (count < 2)
            {
                segment = default;
                return false;
            }

            Point3D first = intersections[0];
            Point3D second = intersections[1];
            double deltaX = second.X - first.X;
            double deltaY = second.Y - first.Y;
            if (deltaX * deltaX + deltaY * deltaY < 0.0025)
            {
                segment = default;
                return false;
            }

            segment = new GameMapSegment(first.X, first.Y, second.X, second.Y);
            return true;
        }

        private static void AddSliceIntersection(
            Point3D first,
            Point3D second,
            double sliceZ,
            Point3D[] intersections,
            ref int count)
        {
            if (count >= intersections.Length)
                return;

            double firstDelta = first.Z - sliceZ;
            double secondDelta = second.Z - sliceZ;
            if (Math.Abs(firstDelta) < 1e-8 && Math.Abs(secondDelta) < 1e-8)
                return;
            if ((firstDelta > 0 && secondDelta > 0) ||
                (firstDelta < 0 && secondDelta < 0))
            {
                return;
            }

            double denominator = second.Z - first.Z;
            if (Math.Abs(denominator) < 1e-10)
                return;
            double amount = (sliceZ - first.Z) / denominator;
            if (amount < -1e-8 || amount > 1.0 + 1e-8)
                return;

            var point = new Point3D(
                first.X + (second.X - first.X) * amount,
                first.Y + (second.Y - first.Y) * amount,
                sliceZ);
            for (int index = 0; index < count; index++)
            {
                if ((intersections[index] - point).LengthSquared < 1e-10)
                    return;
            }
            intersections[count++] = point;
        }

        // Algorithme "closest point on triangle" de Real-Time Collision Detection.
        private static Point3D ClosestPointOnTriangle(Point3D point, Point3D a, Point3D b, Point3D c)
        {
            Vector3D ab = b - a;
            Vector3D ac = c - a;
            Vector3D ap = point - a;
            double d1 = Vector3D.DotProduct(ab, ap);
            double d2 = Vector3D.DotProduct(ac, ap);
            if (d1 <= 0.0 && d2 <= 0.0) return a;

            Vector3D bp = point - b;
            double d3 = Vector3D.DotProduct(ab, bp);
            double d4 = Vector3D.DotProduct(ac, bp);
            if (d3 >= 0.0 && d4 <= d3) return b;

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
            {
                double v = d1 / (d1 - d3);
                return a + v * ab;
            }

            Vector3D cp = point - c;
            double d5 = Vector3D.DotProduct(ab, cp);
            double d6 = Vector3D.DotProduct(ac, cp);
            if (d6 >= 0.0 && d5 <= d6) return c;

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
            {
                double w = d2 / (d2 - d6);
                return a + w * ac;
            }

            double va = d3 * d6 - d5 * d4;
            if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
            {
                double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + w * (c - b);
            }

            double denominator = 1.0 / (va + vb + vc);
            double insideV = vb * denominator;
            double insideW = vc * denominator;
            return a + ab * insideV + ac * insideW;
        }

        private static double SquaredDistance2D(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return dx * dx + dy * dy;
        }

        private readonly struct MiniMapSegmentKey :
            IEquatable<MiniMapSegmentKey>
        {
            private const double Quantization = 0.08; // environ 2,4 cm

            private readonly long _x1;
            private readonly long _y1;
            private readonly long _x2;
            private readonly long _y2;

            public MiniMapSegmentKey(GameMapSegment segment)
            {
                long firstX = Quantize(segment.X1);
                long firstY = Quantize(segment.Y1);
                long secondX = Quantize(segment.X2);
                long secondY = Quantize(segment.Y2);
                bool reverse =
                    firstX > secondX ||
                    (firstX == secondX && firstY > secondY);
                if (reverse)
                {
                    _x1 = secondX;
                    _y1 = secondY;
                    _x2 = firstX;
                    _y2 = firstY;
                }
                else
                {
                    _x1 = firstX;
                    _y1 = firstY;
                    _x2 = secondX;
                    _y2 = secondY;
                }
            }

            public bool Equals(MiniMapSegmentKey other)
            {
                return
                    _x1 == other._x1 &&
                    _y1 == other._y1 &&
                    _x2 == other._x2 &&
                    _y2 == other._y2;
            }

            public override bool Equals(object obj)
            {
                return obj is MiniMapSegmentKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + _x1.GetHashCode();
                    hash = hash * 31 + _y1.GetHashCode();
                    hash = hash * 31 + _x2.GetHashCode();
                    return hash * 31 + _y2.GetHashCode();
                }
            }

            private static long Quantize(double value)
            {
                return (long)Math.Round(value / Quantization);
            }
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}

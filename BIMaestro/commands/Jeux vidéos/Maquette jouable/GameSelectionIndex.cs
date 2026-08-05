using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace BIMaestro.VideoGames
{
    internal sealed class GameSelectionHit
    {
        public GameSelectionHit(
            GameElementData element,
            double distance,
            Point3D position,
            bool isPrecise)
        {
            Element = element;
            Distance = distance;
            Position = position;
            IsPrecise = isPrecise;
        }

        public GameElementData Element { get; }
        public double Distance { get; }
        public Point3D Position { get; }
        public bool IsPrecise { get; }
    }

    /// <summary>
    /// Index BVH immuable consacré à la sélection. Le graphe MEP n'est jamais
    /// consulté ici : un survol reste une opération purement géométrique.
    /// </summary>
    internal sealed class GameSelectionIndex
    {
        private const int LeafSize = 8;
        private readonly List<GameElementData> _elements;
        private readonly Dictionary<string, GameElementData> _elementsByKey;
        private readonly Node? _root;

        private sealed class Node
        {
            public Rect3D Bounds;
            public int Start;
            public int Count;
            public Node? First;
            public Node? Second;
            public bool IsLeaf => First == null && Second == null;
        }

        public GameSelectionIndex(IEnumerable<GameElementData> elements)
        {
            _elements = (elements ?? Enumerable.Empty<GameElementData>())
                .Where(element => element != null && !element.Bounds.IsEmpty)
                .ToList();
            _elementsByKey = _elements
                .Where(element => !string.IsNullOrWhiteSpace(element.Key))
                .GroupBy(element => element.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.Ordinal);
            if (_elements.Count > 0)
                _root = Build(0, _elements.Count);
        }

        public int ElementCount => _elements.Count;

        public GameSelectionHit? FindNearest(
            Point3D origin,
            Vector3D direction,
            double maximumDistance = 300.0)
        {
            if (_root == null || direction.LengthSquared < 1e-12 ||
                maximumDistance <= 0.0)
            {
                return null;
            }

            direction.Normalize();
            GameElementData? preciseElement = null;
            double preciseDistance = maximumDistance;
            GameElementData? fallbackElement = null;
            double fallbackDistance = maximumDistance;
            Query(
                _root,
                origin,
                direction,
                ref preciseElement,
                ref preciseDistance,
                ref fallbackElement,
                ref fallbackDistance);

            if (preciseElement != null)
            {
                return new GameSelectionHit(
                    preciseElement,
                    preciseDistance,
                    origin + direction * preciseDistance,
                    true);
            }
            if (fallbackElement != null)
            {
                return new GameSelectionHit(
                    fallbackElement,
                    fallbackDistance,
                    origin + direction * fallbackDistance,
                    false);
            }
            return null;
        }

        private Node Build(int start, int count)
        {
            Rect3D bounds = BoundsOf(start, count);
            var node = new Node { Bounds = bounds, Start = start, Count = count };
            if (count <= LeafSize)
                return node;

            int axis = LargestAxis(bounds);
            _elements.Sort(start, count, Comparer<GameElementData>.Create((first, second) =>
                Center(first.Bounds, axis).CompareTo(Center(second.Bounds, axis))));
            int firstCount = count / 2;
            node.First = Build(start, firstCount);
            node.Second = Build(start + firstCount, count - firstCount);
            return node;
        }

        private void Query(
            Node node,
            Point3D origin,
            Vector3D direction,
            ref GameElementData? preciseElement,
            ref double preciseDistance,
            ref GameElementData? fallbackElement,
            ref double fallbackDistance)
        {
            double limit = preciseElement == null
                ? Math.Max(preciseDistance, fallbackDistance)
                : preciseDistance;
            if (!TryIntersectBounds(origin, direction, node.Bounds, out double nodeDistance) ||
                nodeDistance > limit)
            {
                return;
            }

            if (!node.IsLeaf)
            {
                Node? first = node.First;
                Node? second = node.Second;
                double firstDistance = double.MaxValue;
                double secondDistance = double.MaxValue;
                bool firstHit = first != null && TryIntersectBounds(
                    origin, direction, first.Bounds, out firstDistance);
                bool secondHit = second != null && TryIntersectBounds(
                    origin, direction, second.Bounds, out secondDistance);
                if (secondHit && (!firstHit || secondDistance < firstDistance))
                {
                    Node? swap = first;
                    first = second;
                    second = swap;
                }
                if (first != null)
                    Query(first, origin, direction, ref preciseElement,
                        ref preciseDistance, ref fallbackElement, ref fallbackDistance);
                if (second != null)
                    Query(second, origin, direction, ref preciseElement,
                        ref preciseDistance, ref fallbackElement, ref fallbackDistance);
                return;
            }

            for (int index = node.Start; index < node.Start + node.Count; index++)
            {
                GameElementData raw = _elements[index];
                if (!TryIntersectBounds(origin, direction, raw.Bounds, out double boundsDistance) ||
                    boundsDistance >= preciseDistance)
                {
                    continue;
                }

                GameElementData? target = ResolveTarget(raw);
                if (target == null)
                    continue;
                if (raw.SelectionTriangles.Count == 0)
                {
                    if (boundsDistance < fallbackDistance)
                    {
                        fallbackDistance = boundsDistance;
                        fallbackElement = target;
                    }
                    continue;
                }

                foreach (GameTriangle triangle in raw.SelectionTriangles)
                {
                    if (TryIntersectTriangle(
                            origin,
                            direction,
                            triangle,
                            out double triangleDistance) &&
                        triangleDistance < preciseDistance)
                    {
                        preciseDistance = triangleDistance;
                        preciseElement = target;
                    }
                }
            }
        }

        private GameElementData? ResolveTarget(GameElementData element)
        {
            if (string.IsNullOrWhiteSpace(element.SelectionTargetKey))
                return element;
            return _elementsByKey.TryGetValue(element.SelectionTargetKey, out GameElementData target)
                ? target
                : null;
        }

        private Rect3D BoundsOf(int start, int count)
        {
            Rect3D result = Rect3D.Empty;
            for (int index = start; index < start + count; index++)
            {
                Rect3D next = _elements[index].Bounds;
                if (result.IsEmpty)
                    result = next;
                else
                    result.Union(next);
            }
            return result;
        }

        private static int LargestAxis(Rect3D bounds)
        {
            if (bounds.SizeX >= bounds.SizeY && bounds.SizeX >= bounds.SizeZ)
                return 0;
            return bounds.SizeY >= bounds.SizeZ ? 1 : 2;
        }

        private static double Center(Rect3D bounds, int axis)
        {
            if (axis == 0)
                return bounds.X + bounds.SizeX * 0.5;
            if (axis == 1)
                return bounds.Y + bounds.SizeY * 0.5;
            return bounds.Z + bounds.SizeZ * 0.5;
        }

        internal static bool TryIntersectBounds(
            Point3D origin,
            Vector3D direction,
            Rect3D bounds,
            out double distance)
        {
            distance = 0.0;
            if (bounds.IsEmpty)
                return false;
            double minimum = 0.0;
            double maximum = double.MaxValue;
            if (!UpdateInterval(origin.X, direction.X, bounds.X, bounds.X + bounds.SizeX,
                    ref minimum, ref maximum) ||
                !UpdateInterval(origin.Y, direction.Y, bounds.Y, bounds.Y + bounds.SizeY,
                    ref minimum, ref maximum) ||
                !UpdateInterval(origin.Z, direction.Z, bounds.Z, bounds.Z + bounds.SizeZ,
                    ref minimum, ref maximum))
            {
                return false;
            }
            distance = minimum;
            return maximum >= 0.0;
        }

        internal static bool TryIntersectTriangle(
            Point3D origin,
            Vector3D direction,
            GameTriangle triangle,
            out double distance)
        {
            distance = 0.0;
            Vector3D edge1 = triangle.B - triangle.A;
            Vector3D edge2 = triangle.C - triangle.A;
            Vector3D p = Vector3D.CrossProduct(direction, edge2);
            double determinant = Vector3D.DotProduct(edge1, p);
            if (Math.Abs(determinant) < 1e-9)
                return false;
            double inverse = 1.0 / determinant;
            Vector3D t = origin - triangle.A;
            double u = Vector3D.DotProduct(t, p) * inverse;
            if (u < 0.0 || u > 1.0)
                return false;
            Vector3D q = Vector3D.CrossProduct(t, edge1);
            double v = Vector3D.DotProduct(direction, q) * inverse;
            if (v < 0.0 || u + v > 1.0)
                return false;
            distance = Vector3D.DotProduct(edge2, q) * inverse;
            return distance >= 0.0 && !double.IsNaN(distance) && !double.IsInfinity(distance);
        }

        private static bool UpdateInterval(
            double origin,
            double direction,
            double minimumBound,
            double maximumBound,
            ref double minimum,
            ref double maximum)
        {
            if (Math.Abs(direction) < 1e-12)
                return origin >= minimumBound && origin <= maximumBound;
            double inverse = 1.0 / direction;
            double first = (minimumBound - origin) * inverse;
            double second = (maximumBound - origin) * inverse;
            if (first > second)
            {
                double swap = first;
                first = second;
                second = swap;
            }
            minimum = Math.Max(minimum, first);
            maximum = Math.Min(maximum, second);
            return minimum <= maximum;
        }
    }
}

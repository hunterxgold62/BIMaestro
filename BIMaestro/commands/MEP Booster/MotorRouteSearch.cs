using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BIMaestro.MepBooster
{
    internal readonly struct MotorRouteCell : IEquatable<MotorRouteCell>
    {
        internal readonly int X, Y, Heading;
        internal MotorRouteCell(int x, int y, int heading) { X = x; Y = y; Heading = (heading + 16) % 16; }
        public bool Equals(MotorRouteCell other) => X == other.X && Y == other.Y && Heading == other.Heading;
        public override bool Equals(object obj) => obj is MotorRouteCell c && Equals(c);
        public override int GetHashCode() => ((X * 397) ^ Y) * 31 + Heading;
    }
    internal sealed class MotorRouteSearchResult
    {
        internal List<MotorRouteCell> Cells;
        internal string Message;
        internal int Visited;
    }
    // Positions are expressed in grid-step units. Door anchors deliberately keep
    // their exact coordinates instead of being rounded onto that grid.
    internal readonly struct MotorRoutePoint : IEquatable<MotorRoutePoint>
    {
        internal readonly double X, Y;
        internal readonly int Heading;
        internal MotorRoutePoint(double x, double y, int heading)
        { X = x; Y = y; Heading = heading; }
        public bool Equals(MotorRoutePoint other) => X == other.X && Y == other.Y && Heading == other.Heading;
        public override bool Equals(object obj) => obj is MotorRoutePoint other && Equals(other);
        public override int GetHashCode() => ((X.GetHashCode() * 397) ^ Y.GetHashCode()) * 31 + Heading;
    }
    internal sealed class MotorRouteGate
    {
        internal readonly double X, Y, NormalX, NormalY, StandOff;
        internal readonly bool Target;
        internal MotorRouteGate(double x, double y, double normalX, double normalY, double standOff, bool target = false)
        { X = x; Y = y; NormalX = normalX; NormalY = normalY; StandOff = standOff; Target = target; }
    }
    internal sealed class MotorRoutePlan
    {
        internal List<MotorRoutePoint> Points;
        internal string Message;
        internal int Visited;
    }
    internal static class MotorRouteSearch
    {
        private readonly struct AnchoredCell : IEquatable<AnchoredCell>
        {
            internal readonly int X, Y, Heading, Anchor;
            internal AnchoredCell(int x, int y, int heading, int anchor = -1)
            { X = x; Y = y; Heading = heading; Anchor = anchor; }
            public bool Equals(AnchoredCell c) => X == c.X && Y == c.Y && Heading == c.Heading && Anchor == c.Anchor;
            public override bool Equals(object o) => o is AnchoredCell c && Equals(c);
            public override int GetHashCode() => (((X * 397) ^ Y) * 31 + Heading) * 397 + Anchor;
        }
        private sealed class AnchoredNode
        {
            internal AnchoredCell Cell;
            internal double Cost, Score;
            internal int Sequence;
            internal AnchoredNode Parent;
        }
        // Exact portal-centre anchors bridge holes narrower than the grid step's
        // alignment error. The sides additionally offer a straight full crossing.
        // Anchors are graph hints only: every edge, including turns and portal
        // crossings, is checked by the same whole-volume collision callback.
        internal static MotorRoutePlan FindWithPortals(IList<MotorRouteGate> gates, int padding, double turnCost,
            Func<MotorRoutePoint, MotorRoutePoint, bool> clear, int maxVisited = 24000, double seconds = 40,
            Action<int> progress = null, IList<double> headingAngles = null)
        {
            if (gates == null || !gates.Any(g => g.Target)) throw new ArgumentException("Une porte cible est nécessaire.", nameof(gates));
            var watch = Stopwatch.StartNew();
            var angles = headingAngles ?? Enumerable.Range(0, 16).Select(i => i * Math.PI / 8).ToArray();
            if (angles.Count == 0) throw new ArgumentException("Au moins une orientation est nécessaire.", nameof(headingAngles));
            int initialHeading = Enumerable.Range(0, angles.Count).OrderBy(i => Math.Abs(Math.Atan2(Math.Sin(angles[i]), Math.Cos(angles[i])))).First();
            var result = new MotorRoutePlan();
            var positions = new List<MotorRoutePoint>();
            var attached = new List<List<(int X, int Y)>>();
            var gridAnchors = new Dictionary<(int X, int Y), List<int>>();
            var crossings = new Dictionary<int, (int Other, bool Target)>();
            foreach (var gate in gates)
            {
                int first = positions.Count;
                AddAnchor(gate.X - gate.NormalX * gate.StandOff, gate.Y - gate.NormalY * gate.StandOff);
                AddAnchor(gate.X + gate.NormalX * gate.StandOff, gate.Y + gate.NormalY * gate.StandOff);
                AddAnchor(gate.X, gate.Y);
                crossings[first] = (first + 1, gate.Target);
                crossings[first + 1] = (first, gate.Target);
            }
            void AddAnchor(double x, double y)
            {
                int index = positions.Count;
                positions.Add(new MotorRoutePoint(x, y, 0));
                var neighbours = new List<(int, int)>();
                // Include both sides of a half-step tie, plus one surrounding
                // cell, without requiring a rounded cell to fit in the opening.
                for (int ix = (int)Math.Floor(x) - 1; ix <= (int)Math.Ceiling(x) + 1; ix++)
                for (int iy = (int)Math.Floor(y) - 1; iy <= (int)Math.Ceiling(y) + 1; iy++)
                {
                    var key = (ix, iy); neighbours.Add(key);
                    if (!gridAnchors.TryGetValue(key, out var list)) gridAnchors[key] = list = new List<int>();
                    list.Add(index);
                }
                attached.Add(neighbours);
            }
            MotorRoutePoint Point(AnchoredCell cell) => cell.Anchor < 0
                ? new MotorRoutePoint(cell.X, cell.Y, cell.Heading)
                : new MotorRoutePoint(positions[cell.Anchor].X, positions[cell.Anchor].Y, cell.Heading);
            var targetEnds = crossings.Where(p => p.Value.Target).Select(p => positions[p.Key]).ToList();
            double Heuristic(AnchoredCell cell)
            {
                var p = Point(cell);
                return targetEnds.Min(t => Math.Sqrt((p.X - t.X) * (p.X - t.X) + (p.Y - t.Y) * (p.Y - t.Y)));
            }
            // Stay inside the region whose actual geometry the caller captured.
            // Intermediate hints must never enlarge that region silently.
            double minX = Math.Min(0, targetEnds.Min(p => p.X)) - padding, maxX = Math.Max(0, targetEnds.Max(p => p.X)) + padding;
            double minY = Math.Min(0, targetEnds.Min(p => p.Y)) - padding, maxY = Math.Max(0, targetEnds.Max(p => p.Y)) + padding;
            var best = new Dictionary<AnchoredCell, double>();
            int sequence = 0;
            var open = new SortedSet<AnchoredNode>(Comparer<AnchoredNode>.Create((a, b) =>
            { int order = a.Score.CompareTo(b.Score); return order != 0 ? order : a.Sequence.CompareTo(b.Sequence); }));
            var start = new AnchoredCell(0, 0, initialHeading);
            if (!clear(Point(start), Point(start)))
            { result.Message = "Le volume moteur touche déjà un obstacle au départ. Ajustez sa taille et son centre."; return result; }
            best[start] = 0;
            open.Add(new AnchoredNode { Cell = start, Score = Heuristic(start), Sequence = sequence++ });
            while (open.Count > 0 && result.Visited < maxVisited && watch.Elapsed.TotalSeconds < seconds)
            {
                var current = open.Min; open.Remove(current);
                if (best[current.Cell] < current.Cost - 1e-8) continue;
                result.Visited++;
                if (result.Visited % 32 == 1) progress?.Invoke(result.Visited);
                var from = Point(current.Cell);
                // A target is reached only by a verified crossing, from either
                // side. Merely touching its centre or its far side is not a goal.
                if (current.Cell.Anchor >= 0 && crossings.TryGetValue(current.Cell.Anchor, out var crossing) && crossing.Target)
                {
                    var terminal = new AnchoredCell(0, 0, from.Heading, crossing.Other);
                    if (clear(from, Point(terminal)))
                    {
                        var path = new List<MotorRoutePoint> { Point(terminal) };
                        for (var node = current; node != null; node = node.Parent) path.Add(Point(node.Cell));
                        path.Reverse(); result.Points = path; result.Message = "Trajet trouvé"; return result;
                    }
                }
                var neighbours = new List<AnchoredCell>();
                if (current.Cell.Anchor < 0)
                {
                    for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
                        if (x != 0 || y != 0) neighbours.Add(new AnchoredCell(current.Cell.X + x, current.Cell.Y + y, from.Heading));
                    if (gridAnchors.TryGetValue((current.Cell.X, current.Cell.Y), out var anchors))
                        foreach (int anchor in anchors) neighbours.Add(new AnchoredCell(0, 0, from.Heading, anchor));
                }
                else
                {
                    foreach (var grid in attached[current.Cell.Anchor]) neighbours.Add(new AnchoredCell(grid.X, grid.Y, from.Heading));
                    int group = current.Cell.Anchor / 3 * 3;
                    for (int anchor = group; anchor < group + 3; anchor++)
                        if (anchor != current.Cell.Anchor) neighbours.Add(new AnchoredCell(0, 0, from.Heading, anchor));
                }
                neighbours.Add(new AnchoredCell(current.Cell.X, current.Cell.Y, (from.Heading + 1) % angles.Count, current.Cell.Anchor));
                neighbours.Add(new AnchoredCell(current.Cell.X, current.Cell.Y, (from.Heading + angles.Count - 1) % angles.Count, current.Cell.Anchor));
                foreach (var next in neighbours)
                {
                    var to = Point(next);
                    if (to.X < minX || to.X > maxX || to.Y < minY || to.Y > maxY) continue;
                    bool turn = to.Heading != from.Heading;
                    double angle = Math.Abs(Math.Atan2(Math.Sin(angles[to.Heading] - angles[from.Heading]), Math.Cos(angles[to.Heading] - angles[from.Heading])));
                    double edge = turn ? turnCost * angle / (Math.PI / 8) : Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
                    double cost = current.Cost + Math.Max(1e-7, edge);
                    if (best.TryGetValue(next, out double old) && old <= cost) continue;
                    if (!clear(from, to)) continue;
                    best[next] = cost;
                    open.Add(new AnchoredNode { Cell = next, Cost = cost, Score = cost + 1.15 * Heuristic(next), Parent = current, Sequence = sequence++ });
                }
            }
            result.Message = open.Count == 0 ? "Aucun trajet trouvé à cette hauteur dans le périmètre de recherche."
                : "Limite de recherche atteinte : aucun trajet validé. Le résultat reste indéterminé.";
            return result;
        }
        private sealed class Node
        {
            internal MotorRouteCell Cell;
            internal double Cost, Score;
            internal int Sequence;
            internal Node Parent;
        }
        // Translation and turning are distinct physical moves. Every edge is
        // checked by the caller using the entire transported box, not its centre.
        internal static MotorRouteSearchResult Find(int goalX, int goalY, int padding, double turnCost,
            Func<MotorRouteCell, MotorRouteCell, bool> clear, int maxVisited = 12000, double seconds = 40,
            Func<MotorRouteCell, bool> finish = null, Action<int> progress = null)
        {
            var watch = Stopwatch.StartNew();
            var result = new MotorRouteSearchResult();
            var best = new Dictionary<MotorRouteCell, double>();
            int sequence = 0;
            var open = new SortedSet<Node>(Comparer<Node>.Create((a, b) =>
            { int order = a.Score.CompareTo(b.Score); return order != 0 ? order : a.Sequence.CompareTo(b.Sequence); }));
            double Heuristic(MotorRouteCell c) => Math.Sqrt((double)(c.X - goalX) * (c.X - goalX) + (double)(c.Y - goalY) * (c.Y - goalY));
            var start = new MotorRouteCell(0, 0, 0);
            if (!clear(start, start)) { result.Message = "Le volume moteur touche déjà un obstacle au départ. Ajustez sa taille et son centre."; return result; }
            best[start] = 0; open.Add(new Node { Cell = start, Score = Heuristic(start), Sequence = sequence++ });
            while (open.Count > 0 && result.Visited < maxVisited && watch.Elapsed.TotalSeconds < seconds)
            {
                var current = open.Min; open.Remove(current);
                if (best[current.Cell] < current.Cost - 1e-8) continue;
                result.Visited++;
                if(result.Visited%32==1) progress?.Invoke(result.Visited);
                if (current.Cell.X == goalX && current.Cell.Y == goalY && (finish == null || finish(current.Cell)))
                {
                    var path = new List<MotorRouteCell>();
                    for (var node = current; node != null; node = node.Parent) path.Add(node.Cell);
                    path.Reverse(); result.Cells = path; result.Message = "Trajet trouvé"; return result;
                }
                var neighbours = new List<MotorRouteCell>();
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
                    if (x != 0 || y != 0) neighbours.Add(new MotorRouteCell(current.Cell.X + x, current.Cell.Y + y, current.Cell.Heading));
                neighbours.Add(new MotorRouteCell(current.Cell.X, current.Cell.Y, current.Cell.Heading + 1));
                neighbours.Add(new MotorRouteCell(current.Cell.X, current.Cell.Y, current.Cell.Heading - 1));
                foreach (var next in neighbours)
                {
                    if (next.X < Math.Min(0, goalX) - padding || next.X > Math.Max(0, goalX) + padding
                        || next.Y < Math.Min(0, goalY) - padding || next.Y > Math.Max(0, goalY) + padding) continue;
                    bool turn = next.Heading != current.Cell.Heading;
                    double cost = current.Cost + (turn ? turnCost : Math.Sqrt((double)(next.X - current.Cell.X) * (next.X - current.Cell.X)
                        + (double)(next.Y - current.Cell.Y) * (next.Y - current.Cell.Y)));
                    if (best.TryGetValue(next, out double old) && old <= cost) continue;
                    if (!clear(current.Cell, next)) continue;
                    best[next] = cost;
                    open.Add(new Node { Cell = next, Cost = cost, Score = cost + 1.15 * Heuristic(next), Parent = current, Sequence = sequence++ });
                }
            }
            result.Message = open.Count == 0 ? "Aucun trajet trouvé à cette hauteur dans le périmètre de recherche."
                : "Limite de recherche atteinte : aucun trajet validé. Réduisez le périmètre ou augmentez le pas.";
            return result;
        }
    }
}

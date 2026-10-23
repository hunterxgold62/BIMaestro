using System;
using System.Linq;
using BIMaestro.MepBooster;

internal static class Program
{
    private static void Main()
    {
        Check(MotorSweepMath.FirstContact(600, 1, d => false) == null, "clear route");
        Check(MotorSweepMath.FirstContact(600, 1, d => true) == 0, "initial overlap");
        Check(MotorSweepMath.FirstContact(600, 1, d => d >= 600) == 600, "obstacle at final position");
        // A thin wall intersects the continuous sweep even though neither the
        // starting nor final moving box intersects the wall.
        foreach (int sign in new[] { -1, 1 })
        {
            double wallMin = sign > 0 ? 237.25 : -237.35;
            double wallMax = sign > 0 ? 237.35 : -237.25;
            bool SweptCollision(double distance)
            {
                double sweptMin = -50 + Math.Min(0, sign * distance);
                double sweptMax = 50 + Math.Max(0, sign * distance);
                return sweptMax > wallMin && sweptMin < wallMax;
            }
            var first = MotorSweepMath.FirstContact(600, 1, SweptCollision);
            Check(first.HasValue && first.Value >= 187.25 && first.Value <= 188.25,
                "thin intermediate obstacle, sign " + sign);
        }
        var marginContact = MotorSweepMath.FirstContact(600, 1, d => 50 + 10 + d > 237.25);
        Check(marginContact >= 177.25 && marginContact <= 178.25, "clearance margin advances first contact");
        int evaluations = 0;
        MotorSweepMath.FirstContact(1000000, 1, d => { evaluations++; return d >= 123456.7; });
        Check(evaluations <= 23, "bounded logarithmic search");
        var direct = MotorRouteSearch.Find(8, 3, 2, 1, (a, b) => true);
        Check(direct.Cells != null && direct.Cells.Last().X == 8 && direct.Cells.Last().Y == 3, "automatic destination reached");
        var blocked = MotorRouteSearch.Find(8, 0, 2, 1, (a, b) => b.X != 4);
        Check(blocked.Cells == null, "solid barrier rejects point route");
        var detour = MotorRouteSearch.Find(8, 0, 3, 1, (a, b) => !(b.X >= 3 && b.X <= 5 && b.Y >= -1 && b.Y <= 1));
        Check(detour.Cells != null && detour.Cells.Any(c => Math.Abs(c.Y) >= 2), "automatic detour around obstacle");
        // A narrow vertical corridor needs a ninety-degree change in the motor's
        // orientation, performed in a larger turning bay, before reaching the exit.
        bool Inside(double x, double y) => (x >= -2 && x <= 7 && y >= -1 && y <= 1)
            || (x >= 3 && x <= 7 && y >= -2 && y <= 3) || (x >= 4 && x <= 6 && y >= 0 && y <= 9);
        bool Fits(double x, double y, double angle)
        {
            for (int u = -2; u <= 2; u++) for (int v = -2; v <= 2; v++)
            {
                double px = u * 0.6, py = v * 0.2;
                if (!Inside(x + px * Math.Cos(angle) - py * Math.Sin(angle), y + px * Math.Sin(angle) + py * Math.Cos(angle))) return false;
            }
            return true;
        }
        bool Motion(MotorRouteCell a, MotorRouteCell b)
        {
            int difference = b.Heading - a.Heading; if (difference > 8) difference -= 16; if (difference < -8) difference += 16;
            for (int i = 0; i <= 8; i++)
            {
                double t = i / 8.0;
                if (!Fits(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, (a.Heading + difference * t) * Math.PI / 8)) return false;
            }
            return true;
        }
        var turn = MotorRouteSearch.Find(5, 7, 3, 0.8, Motion, seconds: 5);
        Check(turn.Cells != null && turn.Cells.Any(c => c.Heading != 0), "turn needed for full motor footprint in L corridor");
        var noTurn = MotorRouteSearch.Find(5, 7, 3, 0.8, (a, b) => a.Heading == b.Heading && Motion(a, b), seconds: 5);
        Check(noTurn.Cells == null, "centre path alone does not prove motor passage");
        var exhausted = MotorRouteSearch.Find(100, 100, 2, 1, (a, b) => true, maxVisited: 2);
        Check(exhausted.Cells == null && exhausted.Message.Contains("Limite"), "bounded search reports inconclusive result");
        var collisionBox=new MotorCollisionBox(new MotorVector(0,0,0),new MotorVector(1,0,0),new MotorVector(0,1,0),new MotorVector(0,0,1),new MotorVector(1,1,1));
        Check(collisionBox.Intersects(new MotorTriangle(new MotorVector(-2,0,0),new MotorVector(2,0,0),new MotorVector(0,2,0))),"triangle crosses box despite its vertices being outside");
        Check(!collisionBox.Intersects(new MotorTriangle(new MotorVector(2,2,0),new MotorVector(3,2,0),new MotorVector(2,3,0))),"separated triangle rejected");
        var vertices=Enumerable.Range(0,8).Select(i=>new MotorVector((i&1)==0?-2:2,(i&2)==0?-2:2,(i&4)==0?-2:2)).ToArray();
        var faces=new[]{new[]{0,1,3,2},new[]{4,6,7,5},new[]{0,4,5,1},new[]{2,3,7,6},new[]{0,2,6,4},new[]{1,5,7,3}};
        var triangles=faces.SelectMany(f=>new[]{new MotorTriangle(vertices[f[0]],vertices[f[1]],vertices[f[2]]),new MotorTriangle(vertices[f[0]],vertices[f[2]],vertices[f[3]])}).ToList();
        Check(MotorMeshCollision.Contains(triangles,new MotorVector(0,0,0)),"box entirely inside a closed solid is detected");
        Check(!MotorMeshCollision.Contains(triangles,new MotorVector(3,0,0)),"point outside closed solid is rejected");
        PortalRoutes();
        Console.WriteLine("PASS: " + _checks + " checks (search, exact door anchors, both final sides, turns and solid collision). Native Revit validation is tested separately.");
    }
    private static int _checks;
    private static void Check(bool ok, string name)
    { _checks++; if (!ok) throw new Exception("FAIL: " + name); }
    private static void PortalRoutes()
    {
        // The motor is 1.6 x .56 grid units. Both .68-wide holes are
        // .47 off the lattice: no rounded lattice centre can fit through.
        bool Motion(MotorRoutePoint a, MotorRoutePoint b, bool oppositeSide, double aperture = .68)
        {
            double aa = a.Heading * Math.PI / 8, ba = b.Heading * Math.PI / 8;
            double da = Math.Atan2(Math.Sin(ba - aa), Math.Cos(ba - aa));
            int samples = Math.Max(16, (int)Math.Ceiling(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) * 80));
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples, x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t, angle = aa + da * t;
                double hx = Math.Abs(Math.Cos(angle)) * .8 + Math.Abs(Math.Sin(angle)) * .28;
                double hy = Math.Abs(Math.Sin(angle)) * .8 + Math.Abs(Math.Cos(angle)) * .28;
                if (x - hx < -2 || x + hx > 12 || y - hy < -2 || y + hy > 9) return false;
                bool WallX(double wall, double hole) => x + hx > wall - .1 && x - hx < wall + .1
                    && (y - hy < hole - aperture / 2 || y + hy > hole + aperture / 2);
                if (!oppositeSide)
                {
                    if (WallX(3, .47) || WallX(8, 2.47)) return false;
                }
                else
                {
                    // Leave the first room through x=8, go up, then approach
                    // the final door x=5 from its right-hand side.
                    if (y + hy > 1.9 && y - hy < 2.1 && (x - hx < 7 || x + hx > 9)) return false;
                    if (y + hy > 2.1 && WallX(5, 5.47)) return false;
                }
            }
            return true;
        }
        var gates = new[] { new MotorRouteGate(3, .47, 1, 0, 1.2), new MotorRouteGate(8, 2.47, 1, 0, 1.2, true) };
        var route = MotorRouteSearch.FindWithPortals(gates, 3, .8, (a, b) => Motion(a, b, false), seconds: 5);
        Check(route.Points != null, "two offset door holes reached through exact anchors");
        Check(route.Points.Any(p => Math.Abs(p.Y - .47) < 1e-9) && route.Points.Any(p => Math.Abs(p.Y - 2.47) < 1e-9), "intermediate and target retain real aperture centres");
        Check(Enumerable.Range(1, route.Points.Count - 1).All(i => Motion(route.Points[i - 1], route.Points[i], false)), "every portal route movement is collision checked");
        var noHints = MotorRouteSearch.FindWithPortals(new[] { gates[1] }, 3, .8, (a, b) => Motion(a, b, false), maxVisited: 5000, seconds: 2);
        Check(noHints.Points == null, "rounded lattice cannot pretend to traverse offset intermediate hole");
        var narrow = MotorRouteSearch.FindWithPortals(gates, 2, .8, (a, b) => Motion(a, b, false, .5), maxVisited: 5000, seconds: 2);
        Check(narrow.Points == null, "portal hint never overrides insufficient real width");
        var reverseGates = new[] { new MotorRouteGate(8, 2, 0, 1, 1.2), new MotorRouteGate(5, 5.47, 1, 0, 1.2, true) };
        var reverse = MotorRouteSearch.FindWithPortals(reverseGates, 4, .8, (a, b) => Motion(a, b, true), seconds: 5);
        Check(reverse.Points != null && reverse.Points[reverse.Points.Count - 2].X > 5 && reverse.Points.Last().X < 5,
            "final exit can be approached from opposite side after intermediate door");
        var arbitrary = MotorRouteSearch.FindWithPortals(new[] { new MotorRouteGate(4, .4, 1, 0, 1, true) }, 2, .8,
            (a, b) => a.X < 2.9 && b.X < 2.9 || a.Heading == 1 && b.Heading == 1,
            headingAngles: new[] { 0.0, .173, Math.PI });
        Check(arbitrary.Points != null && arbitrary.Points.Last().Heading == 1, "additional exact door orientation participates in checked turn search");
        var bound = MotorRouteSearch.FindWithPortals(gates, 3, .8, (a, b) => true, maxVisited: 1);
        Check(bound.Points == null && bound.Message.Contains("indéterminé"), "portal search budget remains an inconclusive result");
    }
}

using System;
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
        Console.WriteLine("PASS: 7 motor extraction search checks. Native Revit solid intersections and ghost rendering require an in-app check.");
    }
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception("FAIL: " + name); }
}

using System;
using System.Collections.Generic;
using System.Linq;
using BIMaestro.MepBooster;

static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    private static MotorTriangle Triangle(double z = 115)
        => new MotorTriangle(new MotorVector(50, 230, z), new MotorVector(55, 230, z), new MotorVector(50, 235, z));
    private static bool Supported(IEnumerable<MotorVector> probes, IEnumerable<MotorTriangle> floor, double elevation)
    {
        var heights = new List<double>();
        foreach (var probe in probes)
        {
            var hits = floor.Where(MaintenancePlacementMath.NearHorizontal)
                .Select(t => MaintenancePlacementMath.TryHeight(t, probe.X, probe.Y, out double z) ? z : double.NaN)
                .Where(z => !double.IsNaN(z)).ToArray();
            if (hits.Length == 0) return false;
            heights.Add(hits.OrderBy(z => Math.Abs(z - elevation)).First());
        }
        return MaintenancePlacementMath.LevelContact(heights, .015 / .3048, out _);
    }
    private static void Main()
    {
        var triangle = Triangle();
        Check(MaintenancePlacementMath.TryHeight(triangle, 51, 231, out double height) && Math.Abs(height - 115) < 1e-9,
            "A vertical ray finds a real floor at a nonzero site elevation");
        Check(!MaintenancePlacementMath.TryHeight(triangle, 54, 234, out _), "A point outside the slab triangle is not supported by its bounding box");
        Check(MaintenancePlacementMath.TryHeight(triangle, 52.5, 232.5, out _), "A point on an internal triangulation diagonal remains supported");
        var wall = new MotorTriangle(new MotorVector(0, 0, 0), new MotorVector(0, 0, 2), new MotorVector(0, 2, 0));
        Check(!MaintenancePlacementMath.TryHeight(wall, 0, 1, out _), "A vertical wall cannot supply a floor height");
        Check(!MaintenancePlacementMath.NearHorizontal(wall), "A vertical wall is excluded from support normals");
        var ramp = new MotorTriangle(new MotorVector(0, 0, 0), new MotorVector(2, 0, 1), new MotorVector(0, 2, 0));
        Check(!MaintenancePlacementMath.NearHorizontal(ramp), "A steep face cannot masquerade as a level standing surface");
        Check(MaintenancePlacementMath.NearHorizontal(triangle), "Horizontal floor triangles are eligible for support");

        var floor = new[] { triangle, new MotorTriangle(new MotorVector(55, 230, 115), new MotorVector(55, 235, 115), new MotorVector(50, 235, 115)) };
        var probes = MaintenancePlacementMath.Footprint(new MotorVector(52.5, 232.5, 115), new MotorVector(1, 0, 0),
            new MotorVector(0, 1, 0), 1.6, 1.05, .17).ToArray();
        Check(probes.Length == 9, "Footprint checks center, corners and edges rather than one ray");
        Check(Supported(probes, floor, 115), "A full footprint can stand across a floor triangulation seam");
        var unsupportedCorner = probes.Concat(new[] { new MotorVector(56, 232, 115) });
        Check(!Supported(unsupportedCorner, floor, 115), "One unsupported footprint point rejects placement near a slab edge");
        Check(!Supported(probes, new MotorTriangle[0], 115), "No real floor means no placement in the air");
        Check(!MaintenancePlacementMath.LevelContact(new[] { 115.0, 115.3, 115.0 }, .015 / .3048, out _), "A step under the feet is refused for an upright level posture");
        Check(MaintenancePlacementMath.LevelContact(new[] { 115.0, 115.01, 115.02 }, .015 / .3048, out double highest) && highest == 115.02,
            "Small modeled contact differences use the highest supporting surface");
        Check(!MaintenancePlacementMath.LevelContact(new[] { 115.0, double.NaN }, 1, out _), "Missing support is never averaged into a valid floor");

        MotorVector Transform(MotorVector p) => new MotorVector(1000 - p.Y, 2000 + p.X, 35 + p.Z);
        var linked = new MotorTriangle(Transform(triangle.A), Transform(triangle.B), Transform(triangle.C));
        var linkedProbe = Transform(new MotorVector(51, 231, 115));
        Check(MaintenancePlacementMath.TryHeight(linked, linkedProbe.X, linkedProbe.Y, out double linkedHeight) && Math.Abs(linkedHeight - 150) < 1e-9,
            "A rotated and translated linked floor is tested in world coordinates");
        var mirrored = new MotorTriangle(new MotorVector(-triangle.A.X, triangle.A.Y, triangle.A.Z),
            new MotorVector(-triangle.B.X, triangle.B.Y, triangle.B.Z), new MotorVector(-triangle.C.X, triangle.C.Y, triangle.C.Z));
        Check(MaintenancePlacementMath.TryHeight(mirrored, -51, 231, out double mirrorHeight) && mirrorHeight == 115,
            "A mirrored link retains correct support height despite winding reversal");
        Check(Math.Abs(MaintenancePlacementMath.ChainWorkHeight(1.2, 3.3, 0, 2.2, .07, .35) - 1.52) < 1e-9,
            "A loop ending 1100 mm above the floor gets enough grip height for a full 350 mm pull");
        Check(Math.Abs(MaintenancePlacementMath.ChainWorkHeight(1.2, 2.4, 0, 2, .07, .35) - 1.2) < 1e-9,
            "An already sufficient 1200 mm grip height stays unchanged");
        Check(Math.Abs(MaintenancePlacementMath.ChainWorkHeight(1.2, 39.6, 35.9, 2.6, .07, .35) - 1.52) < 1e-9,
            "Grip height uses the actual world floor elevation rather than a zero datum");
        Check(Math.Abs(MaintenancePlacementMath.ChainWorkHeight(1.7, 3.3, 0, 2.2, .07, .35) - 1.7) < 1e-9,
            "A deliberately higher configured grip is never lowered by automatic suggestion");
        var planar = new MaintenanceHorizontalLinework();
        planar.AddPrimitive(new[] { new MotorVector(0, 0, 115), new MotorVector(50, 50, 115) });
        planar.AddPrimitive(new[] { new MotorVector(2, 1, 115), new MotorVector(5, 3, 115), new MotorVector(6, 7, 115) });
        Check(planar.IsHorizontal2D(.001), "Fully read horizontal linework is non-volumetric reference data");
        var tilted = new MaintenanceHorizontalLinework();
        tilted.AddPrimitive(new[] { new MotorVector(0, 0, 115), new MotorVector(50, 50, 115.01) });
        Check(!tilted.IsHorizontal2D(.001), "A 3D linework import retains its unknown physical interpretation");
        planar.RejectNonLinework();
        Check(!planar.IsHorizontal2D(.001), "One mesh, solid or unknown primitive prevents the 2D conclusion");
        Check(!new MaintenanceHorizontalLinework().IsHorizontal2D(.001), "Missing CAD geometry is never assumed to be a 2D plan");
        var invalid = new MaintenanceHorizontalLinework();
        invalid.AddPrimitive(new[] { new MotorVector(0, 0, double.NaN) });
        Check(!invalid.IsHorizontal2D(.001), "An invalid CAD coordinate cannot be silently discarded to prove planarity");
        var bounded = new MaintenanceHorizontalLinework();
        bounded.AddPrimitive(Enumerable.Range(0, 250002).Select(i => new MotorVector(i, 0, 115)));
        Check(bounded.ReadingLimited && !bounded.IsHorizontal2D(.001), "A bounded partial read stays unknown rather than proving planarity from a sample");

        Check(MaintenanceOperatorFailure.Classify(false, false, 0, 0, 0) == MaintenanceOperatorFailureReason.NoWalkingSurface,
            "A missing modeled walking surface has its own explanation instead of a reach failure");
        Check(MaintenanceOperatorFailure.Classify(false, true, 0, 0, 0) == MaintenanceOperatorFailureReason.NoSupportedFootprint,
            "A visible floor with no full supported footprint is distinct from a missing floor");
        Check(MaintenanceOperatorFailure.Classify(false, true, 40, 0, 0) == MaintenanceOperatorFailureReason.GestureOutOfReach,
            "Supported but unreachable full gestures explain arm reach rather than a body collision");
        Check(MaintenanceOperatorFailure.Classify(false, true, 40, 8, 0) == MaintenanceOperatorFailureReason.BodyBlocked,
            "A rejected reachable body reports the physical occupancy case");
        Check(MaintenanceOperatorFailure.Classify(false, true, 40, 8, 2) == MaintenanceOperatorFailureReason.BodyUnverified,
            "Unverified body geometry is not described as a proved collision");
        foreach (var observed in new[] { (false, 0, 0, 0), (true, 0, 0, 0), (true, 40, 0, 0), (true, 40, 8, 0), (true, 40, 8, 2) })
            Check(MaintenanceOperatorFailure.Classify(true, observed.Item1, observed.Item2, observed.Item3, observed.Item4)
                == MaintenanceOperatorFailureReason.SearchIncomplete,
                "Budget exhaustion always preserves incomplete search despite the last observed rejection");
        foreach (MaintenanceOperatorFailureReason reason in Enum.GetValues(typeof(MaintenanceOperatorFailureReason)))
        {
            string description = MaintenanceOperatorFailure.Describe(reason);
            Check(reason == MaintenanceOperatorFailureReason.None ? description == null : !string.IsNullOrWhiteSpace(description),
                "Every missing-operator outcome has a user explanation and a valid result has none");
        }
        Check(MaintenanceOperatorFailure.Describe(MaintenanceOperatorFailureReason.SearchIncomplete, "Obstacle du corps : Tube [42].")
            .StartsWith("Recherche incomplète"), "An observed obstacle cannot replace an incomplete-search diagnosis");
        Check(MaintenanceOperatorFailure.Describe(MaintenanceOperatorFailureReason.GestureOutOfReach, "Prise : début 1263 mm ; fin 1963 mm.")
            .Contains("début 1263 mm ; fin 1963 mm"), "Measured start/end heights remain available in the visible diagnosis");
        Console.WriteLine($"Maintenance placement: {_checks} checks passed.");
    }
}

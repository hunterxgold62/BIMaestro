using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

internal static class ParametricDesignTests
{
    internal static void Run()
    {
        var window = JObject.Parse(File.ReadAllText("scripts/codex-tests/native-fixtures/host-opening-window.json"));
        var windowDesign = CodexParametricDesign.Parse(window);
        var resized = windowDesign.Initial; resized["Largeur"] = 1400; resized["Hauteur"] = 1500;
        windowDesign.ValidateAt(resized);
        if (windowDesign.Metadata.HostOpening.Max[0].Value(resized) != 700 || windowDesign.Metadata.HostOpening.Max[1].Value(resized) != 1700)
            throw new Exception("Host opening does not follow window dimensions");
        Reject(window, x => x.Remove("host_opening"), "window missing host opening");
        Reject(window, x => x["host_opening"] = JValue.CreateNull(), "window null host opening");
        Reject(window, x => x["hosting"] = "floor", "wall opening on floor host");
        Reject(window, x => x["host_opening"]["max_xz"][0]["terms"][0]["parameter"] = "Unknown", "unknown opening parameter");
        Reject(window, x => x["host_opening"]["max_xz"][0] = x["host_opening"]["min_xz"][0].DeepClone(), "zero opening width");
        Reject(window, x => x["host_opening"]["max_xz"][0]["terms"][0]["factor"] = -1, "inverted opening");
        Reject(window, x => { x["host_opening"]["max_xz"][0]["offset_mm"] = 650; x["host_opening"]["max_xz"][0]["terms"][0]["factor"] = -0.5; }, "opening crossing origin on flex");
        Console.WriteLine("PASS: window host opening, width/height flex and invalid host/contour rejection");
        var floor = JObject.Parse(File.ReadAllText("scripts/codex-tests/native-fixtures/host-opening-floor.json"));
        var floorDesign = CodexParametricDesign.Parse(floor);
        var floorValues = floorDesign.Initial;
        floorValues["Largeur"] = 800; floorValues["Profondeur"] = 700; floorValues["ProfondeurVide"] = 1500;
        floorDesign.ValidateAt(floorValues);
        var floorOpening = floorDesign.Metadata.HostOpening;
        if (!floorOpening.IsFloor || floorOpening.Max[0].Value(floorValues) - floorOpening.Min[0].Value(floorValues) != 700 ||
            floorOpening.Max[1].Value(floorValues) - floorOpening.Min[1].Value(floorValues) != 600 || floorOpening.Min[2].Value(floorValues) != -1500)
            throw new Exception("Floor void does not follow width, length and depth");
        Reject(floor, x => x["hosting"] = "wall", "XYZ void on wall host");
        Reject(floor, x => x["hosting"] = "ceiling", "unsupported void host");
        Reject(floor, x => x["host_opening"]["min_xyz"][2]["terms"][0]["parameter"] = "Unknown", "unknown void depth driver");
        Reject(floor, x => x["host_opening"]["max_xyz"][2] = x["host_opening"]["min_xyz"][2].DeepClone(), "zero void depth");
        Reject(floor, x => x["host_opening"]["min_xyz"] = new JArray(x["host_opening"]["min_xyz"].Take(2)), "missing void Z coordinate");
        Console.WriteLine("PASS: floor void width/length/depth flex and invalid host/geometry rejection");
        var source = JObject.Parse(File.ReadAllText("scripts/codex-tests/parametric-diffuser.design.json"));
        var design = CodexParametricDesign.Parse(source);
        var wide = design.Initial; wide["Largeur"] = 800;
        if (design.Parts[0].Max[0].Value(wide) - design.Parts[0].Min[0].Value(wide) != 800 || design.Parts[0].Max[1].Value(wide) - design.Parts[0].Min[1].Value(wide) != 600)
            throw new Exception("Width must change independently from depth");
        if (design.Parts[0].Openings[0].Min[0].Value(wide) != -360 || design.Parts[0].Openings[0].Max[0].Value(wide) != 360)
            throw new Exception("Opening did not keep its 40 mm rim");
        if (design.Parameters.Count != 3 || design.TestCases().Count() != 5) throw new Exception("Missing independent or combined flex cases");
        Reject(source, x => x["parameters"][0]["test_value_mm"] = 600, "unchanged flex test");
        Reject(source, x => x["materials"] = "invalid", "malformed materials");
        Reject(source, x => x["materials"] = new JArray("invalid"), "malformed material entry");
        Reject(source, x => x["parameters"][0]["test_value_mm"] = 50, "geometry invalid at test width");
        Reject(source, x => x["parameters"][1]["name"] = "Largeur", "duplicate parameter");
        Reject(source, x => x["parameters"][0]["name"] = "BIM_reserved", "reserved parameter name");
        Reject(source, x => x["parameters"][0]["name"] = "Largeur; code()", "expression injection");
        Reject(source, x => x["parts"][0]["minimum"][0]["terms"][0]["parameter"] = "Unknown", "unknown expression parameter");
        Reject(source, x => x["parts"][0]["minimum"][0]["offset_mm"] = 300, "zero variable coordinate");
        Reject(source, x => x["parts"][0]["minimum"][0]["offset_mm"] = 350, "coordinate crossing origin");
        Reject(source, x => ((JArray)x["parts"][0]["openings"]).Add(x["parts"][0]["openings"][0].DeepClone()), "overlapping holes");
        Reject(source, x => x["parts"][0]["axis"] = "q", "unsupported extrusion axis");
        Reject(source, x => x["parts"][0]["maximum"][0] = x["parts"][0]["minimum"][0].DeepClone(), "zero thickness");
        if ((string)CodexParametricDesign.Tool()["name"] != "revit_create_parametric_family") throw new Exception("Missing parametric tool schema");
        Console.WriteLine("PASS: parametric diffuser 600 to 800, independent depth, preserved rim, expressions and flex cases");
        var networkSource = JObject.Parse(File.ReadAllText("scripts/codex-tests/parametric-array-grille.design.json"));
        var network = CodexParametricDesign.Parse(networkSource);
        var array = network.Arrays.Single();
        foreach (double length in new[] { 500d, 550d, 600d, 549.999d })
        {
            var values = network.Initial; values["LongueurUtile"] = length;
            network.ValidateAt(values);
            if (array.Count(values) != (int)Math.Floor(length / 50)) throw new Exception("Incorrect array count at " + length);
            double first = array.Min[1].Value(values), last = first + (array.Count(values) - 1) * array.Pitch.Value(values);
            if (last + 4 > length / 2 || first < -length / 2) throw new Exception("Bar exceeds the clear opening");
        }
        if (!network.TestCases().Any(v => v["LongueurUtile"] == 550 && v["Pas"] == 50)) throw new Exception("Missing intermediate native flex case");
        var pitchValues = network.Initial; pitchValues["Pas"] = 40;
        if (array.Count(pitchValues) != 12 || network.SolidCount != 11) throw new Exception("Pitch or solid count incorrect");
        foreach (int axis in new[] { 0, 2 })
        {
            var otherObject = (JObject)networkSource.DeepClone();
            otherObject["parts"] = new JArray(); otherObject["arrays"][0]["axis"] = "xyz"[axis].ToString();
            foreach (string bound in new[] { "minimum", "maximum" })
            {
                var coordinates = (JArray)otherObject["arrays"][0][bound];
                var previous = coordinates[axis].DeepClone(); coordinates[axis] = coordinates[1].DeepClone(); coordinates[1] = previous;
            }
            var other = CodexParametricDesign.Parse(otherObject);
            if (other.SolidCount != 10 || other.Parts.Count != 0 || other.Arrays[0].Axis != axis) throw new Exception("Arrays were restricted to one object or axis");
        }
        Reject(networkSource, x => x["arrays"][0]["pitch"]["offset_mm"] = -100, "negative array pitch");
        Reject(networkSource, x => x["arrays"][0]["count_parameter"] = "Largeur", "array count collides with length");
        Reject(networkSource, x => x["arrays"][0]["count_parameter"] = "Count; code()", "array count injection");
        Reject(networkSource, x => x["parameters"][1]["test_value_mm"] = 50, "one-member array");
        Reject(networkSource, x => x["parameters"][1]["test_value_mm"] = 15000, "array above 200 members");
        Reject(networkSource, x => x["parameters"][3]["test_value_mm"] = 60, "overlapping array bars");
        Reject(networkSource, x => { x["parameters"][1]["test_value_mm"] = 501; x["parameters"][2]["test_value_mm"] = 49; }, "unchanged array count during all tests");
        Reject(networkSource, x => { x["parts"] = new JArray(); x["arrays"] = new JArray(); }, "empty parametric family");
        var lengthExpression = new LengthExpression(); lengthExpression.Terms.Add("Largeur", 1);
        var firstHalf = lengthExpression.Scaled(-0.5); var secondHalf = lengthExpression.Scaled(0.5);
        var difference = LengthExpression.Combine(secondHalf, firstHalf, -1);
        if (difference.Formula() != lengthExpression.Formula()) throw new Exception("Direct driver not recognized after subtraction");
        var shifted = LengthExpression.Combine(lengthExpression, new LengthExpression { Offset = 20 });
        var constant = LengthExpression.Combine(shifted, lengthExpression, -1);
        if (constant.Terms.Count != 0 || constant.Offset != 20) throw new Exception("Cancelled terms would create useless parameters");
        if (LengthExpression.Combine(lengthExpression, lengthExpression, -1).IsZero != true) throw new Exception("Zero expression not simplified");
        Console.WriteLine("PASS: native-array description, 500/550/600 count rule, pitch, bounds and parameter-expression reuse");
        var tiltedSource = JObject.Parse(File.ReadAllText("scripts/codex-tests/parametric-angled-array.design.json"));
        var tilted = CodexParametricDesign.Parse(tiltedSource);
        var tiltedArray = tilted.Arrays.Single();
        if (tilted.Initial["Inclinaison"] != 30 || !tilted.TestCases().Any(v => v["Inclinaison"] == 60 && v["LongueurUtile"] == 500)) throw new Exception("Missing independent angle test");
        foreach (var degrees in new[] { 30d, 60d })
        {
            var values = tilted.Initial; values["Inclinaison"] = degrees;
            var corners = tiltedArray.Corners(values, 0);
            double projectedY = 4 * Math.Cos(degrees * Math.PI / 180) + 20 * Math.Sin(degrees * Math.PI / 180);
            if (Math.Abs(corners.Max(p => p[1]) - corners.Min(p => p[1]) - projectedY) > 1e-9) throw new Exception("Inclined bounding width is incorrect");
            if (Math.Abs(corners.Max(p => p[0]) - corners.Min(p => p[0]) - 600) > 1e-9) throw new Exception("Tilt changed extrusion length");
            var next = tiltedArray.Corners(values, 1);
            if (Math.Abs(next[0][1] - corners[0][1] - 50) > 1e-9) throw new Exception("Tilt changed array pitch");
        }
        for (int axis = 0; axis < 3; axis++)
        {
            var vector = new[] { 3d, 4d, 5d };
            var rotated = ParametricArray.Rotate(vector, axis, 37);
            if (Math.Abs(rotated.Sum(v => v * v) - 50) > 1e-9 || rotated[axis] != vector[axis]) throw new Exception("Invalid rotation axis or scaling");
        }
        foreach (double endpoint in new[] { 0d, 90d, 180d })
        {
            var fullAngle = (JObject)tiltedSource.DeepClone(); fullAngle["angles"][0]["value_deg"] = endpoint;
            CodexParametricDesign.Parse(fullAngle);
        }
        Reject(tiltedSource, x => x["angles"][0]["value_deg"] = -1, "negative angle");
        Reject(tiltedSource, x => x["angles"][0]["test_value_deg"] = 181, "angle above 180");
        Reject(tiltedSource, x => x["angles"][0]["test_value_deg"] = 30, "unchanged angle test");
        Reject(tiltedSource, x => x["angles"][0]["name"] = "Largeur", "angle collides with length");
        Reject(tiltedSource, x => x["arrays"][0]["rotation"]["angle_parameter"] = "Unknown", "unknown angle reference");
        Reject(tiltedSource, x => x["arrays"][0]["rotation"] = JValue.CreateNull(), "unused angle");
        Reject(tiltedSource, x => x["arrays"][0]["minimum"][0]["terms"][0]["parameter"] = "Inclinaison", "angle used as length");
        Reject(tiltedSource, x => x["arrays"][0]["maximum"][2]["offset_mm"] = 100, "inclined projection overlaps adjacent member");
        Console.WriteLine("PASS: angle values, rotated bounds, preserved pitch, three axes, unit separation and singularity rejection");
        var gridSource = JObject.Parse(File.ReadAllText("scripts/codex-tests/native-fixtures/enhancement-grid.json"));
        var gridDesign = CodexParametricDesign.Parse(gridSource); var gridNetwork = gridDesign.Arrays.Single();
        if (gridNetwork.Grid.Columns(gridDesign.Initial) != 9 || gridNetwork.Count(gridDesign.Initial) != 2 || gridDesign.SolidCount != 126) throw new Exception("2D grid quantities incorrect.");
        foreach(var sample in new[] { new[]{999d,0d},new[]{1000d,1d},new[]{2019d,1d},new[]{2020d,2d},new[]{10000d,9d} })
        { var values=gridDesign.Initial;values["LargeurChamp"]=sample[0]; if(gridNetwork.Grid.Columns(values)!=sample[1])throw new Exception("Grid fitting threshold incorrect."); }
        Reject(gridSource, x=>x["component_grids"][0]["axis_v"]="x", "duplicate grid axes");
        Reject(gridSource, x=>x["component_grids"][0]["rows_parameter"]="NombreColonnes", "duplicate grid count names");
        Reject(gridSource, x=>x["component_grids"][0]["components"][0]["maximum_mm"][0]=1001, "component outside module");
        Reject(gridSource, x=>x["family_options"]["parameters"][0]["value"]=100000, "grid solid budget");
        Console.WriteLine("PASS: complete module grid, 0/1/2 thresholds, two directions and budgets");
    }
    private static void Reject(JObject original, Action<JObject> mutate, string name)
    {
        var source = (JObject)original.DeepClone(); mutate(source);
        try { CodexParametricDesign.Parse(source); } catch (InvalidOperationException) { Console.WriteLine("PASS: reject " + name); return; }
        throw new Exception("Accepted " + name);
    }
}

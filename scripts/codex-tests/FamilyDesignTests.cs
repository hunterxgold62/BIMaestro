using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

internal static class FamilyDesignTests
{
    internal static void Run()
    {
        var fixture = JObject.Parse(File.ReadAllText("scripts/codex-tests/transformer.design.json"));
        var design = CodexFamilyDesign.Parse(fixture);
        if (design.SolidCount != 103 || design.Parts.Count != 13 || design.Materials.Count != 5) throw new Exception("Transformer design count mismatch");
        Console.WriteLine("PASS: transformer design, 103 solids / 13 groups / 5 materials");
        design.TargetDimensions = new[] { 100.0, 0.0, 200.0 };
        design.CheckDimensions(new[] { 100.5, 999.0, 200.0 });
        bool mismatch = false;
        try { design.CheckDimensions(new[] { 130.0, 999.0, 200.0 }); } catch (InvalidOperationException) { mismatch = true; }
        if (!mismatch) throw new Exception("Mismatched dimensions were accepted");
        Console.WriteLine("PASS: known dimensions checked before saving, unknown dimensions ignored");
        try { design.CheckDimensions(new[] { 130.0, 999.0, 220.0 }); throw new Exception("Expected mismatch"); }
        catch (InvalidOperationException ex) { if (!ex.Message.Contains("X :") || !ex.Message.Contains("Z :")) throw new Exception("Dimension errors not aggregated"); }
        var invalidPart = (JObject)fixture.DeepClone(); invalidPart["parts"][0]["geometry"]["kind"] = "invalid";
        try { CodexFamilyDesign.Parse(invalidPart); throw new Exception("Expected invalid geometry"); }
        catch (InvalidOperationException ex) { if (!ex.Message.Contains("parts[0]") || !ex.Message.Contains((string)fixture["parts"][0]["name"])) throw new Exception("Missing part identity in failure"); }
        if ((string)CodexFamilyDesign.Tool(true)["name"] != "revit_validate_family") throw new Exception("Missing validation tool");
        Console.WriteLine("PASS: validation tool, named part errors and all mismatched axes");
        Reject(fixture, x => x["category"] = "unknown", "unknown category");
        Reject(fixture, x => x["parts"][0]["material"] = "unknown", "unknown material");
        Reject(fixture, x => x["parts"][0]["repeat_count"] = 2, "coincident repetitions");
        Reject(fixture, x => x["parts"][4]["repeat_step_mm"] = new JArray(100000, 0, 0), "repetition outside limits");
        Reject(fixture, x => x["parts"][0]["geometry"]["size_mm"][0] = -5, "negative dimension");
        Reject(fixture, x => x["parts"][11]["geometry"]["size_mm"][1] = 24, "zero tube wall");
        Reject(fixture, x => x["place_at_origin"] = true, "placement without load");
        Reject(fixture, x => x["parts"][0]["geometry"]["kind"] = "execute_code", "unknown primitive");
        Reject(fixture, x => x["parts"][3]["geometry"]["profile_mm"] = new JArray(new JArray(0, 0), new JArray(10, 10), new JArray(0, 10), new JArray(10, 0)), "self-intersecting profile");
        Reject(fixture, x => x["parts"][3]["geometry"]["profile_mm"] = new JArray(new JArray(0, 0), new JArray(10, 0), new JArray(10, 10), new JArray(0, 0)), "repeated endpoint");
        Reject(fixture, x => x["output_path"] = "untrusted.rfa", "model-specified output path");
        Reject(fixture, x =>
        {
            var extra = x["parts"][0].DeepClone(); extra["name"] = "Duplicate geometry"; ((JArray)x["parts"]).Add(extra);
        }, "duplicate solids");
        var schema = CodexFamilyDesign.Tool();
        if ((string)schema["name"] != "revit_create_family" || schema["inputSchema"]["properties"]["parts"] == null) throw new Exception("Missing tool schema");
        var sphere = (JObject)fixture.DeepClone(); sphere["parts"][0]["geometry"]["kind"] = "sphere"; sphere["parts"][0]["geometry"]["size_mm"] = new JArray(50, 0, 0);
        CodexFamilyDesign.Parse(sphere);
        Console.WriteLine("PASS: seven supported primitive types and schema");
        var diffuser = JObject.Parse(File.ReadAllText("scripts/codex-tests/diffuser.design.json"));
        var parsedDiffuser = CodexFamilyDesign.Parse(diffuser);
        if (parsedDiffuser.Parts.Count(p => p.Geometry.Kind == "loft") != 7 || parsedDiffuser.Parts[0].Cuts.Count != 5)
            throw new Exception("Diffuser should have seven sloped hollow vanes and real frame openings");
        Reject(diffuser, x => ((JObject)x["parts"][2]["geometry"]).Remove("sections_mm"), "loft missing sections");
        Reject(diffuser, x => x["parts"][2]["geometry"]["sections_mm"][0][1][2] = 5, "nonplanar loft section");
        Reject(diffuser, x => x["parts"][2]["geometry"]["sections_mm"][1] = x["parts"][2]["geometry"]["sections_mm"][0].DeepClone(), "coincident loft planes");
        Reject(diffuser, x => ((JArray)x["parts"][2]["geometry"]["sections_mm"][1]).RemoveAt(0), "loft vertex count mismatch");
        Reject(diffuser, x => x["parts"][0]["geometry"]["sections_mm"] = x["parts"][2]["geometry"]["sections_mm"].DeepClone(), "loft sections on a box");
        var project = (JObject)diffuser.DeepClone(); project.Remove("load_into_project"); project.Remove("place_at_origin");
        project["project_origin_mm"] = new JArray(150000, 200000, 7000); project["project_rotation_deg"] = 35;
        var composition = CodexFamilyDesign.ParseProject(project, out var origin, out var rotation);
        if (origin[0] != 150000 || rotation != 35 || composition.Load) throw new Exception("Project placement lost");
        project["load_into_project"] = true;
        try { CodexFamilyDesign.ParseProject(project, out origin, out rotation); throw new Exception("Unexpected project field accepted"); }
        catch (InvalidOperationException) { }
        if (CodexFamilyDesign.ProjectTool()["inputSchema"]["properties"]["project_origin_mm"] == null) throw new Exception("Missing project tool origin");
        Console.WriteLine("PASS: hollow diffuser lofts, legacy geometry compatibility and project placement validation");
    }
    private static void Reject(JObject original, Action<JObject> mutate, string name)
    {
        var copy = (JObject)original.DeepClone(); mutate(copy);
        bool rejected = false;
        try { CodexFamilyDesign.Parse(copy); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("FAILED: accepted " + name);
        Console.WriteLine("PASS: reject " + name);
    }
}

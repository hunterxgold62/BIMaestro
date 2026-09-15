using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

internal static class FamilyParameterTests
{
    internal static JObject Parameter(string name, string kind, JToken value, string formula = "", bool instance = false, params JToken[] tests) =>
        new JObject { ["name"] = name, ["kind"] = kind, ["instance"] = instance, ["value"] = value, ["test_values"] = new JArray(tests),
            ["formula"] = formula, ["group"] = kind == "yesno" ? "visibility" : "geometry", ["description"] = "Paramètre témoin", ["shared_guid"] = "" };
    internal static JObject Fixture()
    {
        var source = JObject.Parse(File.ReadAllText("scripts/codex-tests/parametric-diffuser.design.json"));
        source["family_options"] = new JObject {
            ["parameters"] = new JArray(
                Parameter("Largeur", "length", 600, "", true, 999, 1000, 1001, 1200),
                Parameter("OptionFixations", "yesno", true, "", true, false),
                Parameter("AfficherRenfort", "yesno", false, "Largeur > 1000 mm", true),
                Parameter("AfficherFixations", "yesno", true, "and(OptionFixations, Largeur >= 600 mm)", true),
                Parameter("Rapport", "number", 1, "Largeur / 600 mm", true),
                Parameter("Libelle", "text", "Standard", "if(AfficherRenfort, \"Avec renfort\", \"Sans renfort\")", true)),
            ["types"] = new JArray(new JObject { ["name"] = "600", ["values"] = new JArray() },
                new JObject { ["name"] = "1200", ["values"] = new JArray(new JObject { ["parameter"] = "Largeur", ["value"] = 1200 }) }),
            ["representations"] = new JArray(new JObject { ["component"] = (string)source["parts"][0]["name"], ["visible_parameter"] = "AfficherFixations", ["subcategory"] = "Fixations",
                ["coarse"] = false, ["medium"] = false, ["fine"] = true, ["plan"] = true, ["front_back"] = true, ["left_right"] = true }) };
        return source;
    }
    internal static void Run()
    {
        var aliases = new System.Collections.Generic.Dictionary<string, string> { ["hauteur"] = "Hauteur", ["longueur"] = "Longueur" };
        string rewritten = FamilyFormula.RewriteParameterNames("if(hauteur > 10 mm, longueur + hauteur, hauteur_totale)", aliases);
        if (rewritten != "if(Hauteur > 10 mm, Longueur + Hauteur, hauteur_totale)") throw new Exception("Parameter aliases must replace whole identifiers");
        if (FamilyFormula.RewriteParameterNames("if(hauteur > 1 mm, \"hauteur longueur\", \"longueur\")", aliases) != "if(Hauteur > 1 mm, \"hauteur longueur\", \"longueur\")") throw new Exception("Aliases must preserve quoted text");
        var nested = new CodexParametricDesign();
        nested.ValidateAt(new System.Collections.Generic.Dictionary<string, double>());
        Console.WriteLine("PASS: native parameter alias formulas, preserved text, nested family without metadata");
        var source = Fixture(); var design = CodexParametricDesign.Parse(source); var registry = design.Registry;
        if (registry == null || registry.Types.Count != 2 || registry.Find("Largeur").Instance != true) throw new Exception("Missing type/instance contract");
        var tests = registry.Cases().ToArray();
        foreach (double width in new[] { 999d, 1000d, 1001d })
        {
            var sample = tests.First(c => c.Numeric["Largeur"] == width);
            if (sample.Values["AfficherRenfort"].Boolean != (width > 1000)) throw new Exception("Incorrect threshold transition");
            if (Math.Abs(sample.Numeric["Rapport"] - width / 600) > 1e-10) throw new Exception("Incorrect dimensionless ratio");
        }
        if (!tests.Any(c => !c.Values["OptionFixations"].Boolean && !c.Values["AfficherFixations"].Boolean)) throw new Exception("Hidden option lost");
        if (registry.Initial["Libelle"].Text != "Sans renfort") throw new Exception("Text conditional failed");
        if (!registry.Find("AfficherFixations").Formula.Revit().Contains("not(")) throw new Exception("Revit >= translation missing");
        var automatic = (JObject)source.DeepClone();
        automatic["family_options"]["parameters"][0]["test_values"] = new JArray();
        automatic["family_options"]["parameters"][1]["test_values"] = new JArray();
        var automaticCases = CodexParametricDesign.Parse(automatic).Registry.Cases().ToArray();
        foreach (double width in new[] { 999d, 1000d, 1001d })
            if (!automaticCases.Any(c => c.Numeric["Largeur"] == width)) throw new Exception("Missing automatic threshold case " + width);
        if (!automaticCases.Any(c => !c.Values["OptionFixations"].Boolean)) throw new Exception("Missing automatic boolean variation");
        foreach (var hosting in new[] { "free", "face", "wall", "ceiling", "work_plane" })
        {
            var hosted = (JObject)source.DeepClone(); hosted["hosting"] = hosting; hosted["place_at_origin"] = false;
            if (CodexParametricDesign.Parse(hosted).Hosting != hosting) throw new Exception("Hosting lost: " + hosting);
        }
        Reject(source, s => s["hosting"] = "adaptive", "unsupported hosting");
        Reject(source, s => { s["hosting"] = "wall"; s["load_into_project"] = true; s["place_at_origin"] = true; }, "host required for placement");
        Reject(source, s => s["family_options"]["parameters"][2]["formula"] = "Largeur > 20 deg", "unit mismatch");
        Reject(source, s => s["family_options"]["parameters"][2]["instance"] = false, "type depends on instance");
        Reject(source, s => s["family_options"]["parameters"][2]["formula"] = "Missing > 2 mm", "unknown dependency");
        Reject(source, s => s["family_options"]["parameters"][2]["formula"] = "if(true, false, AfficherRenfort)", "cycle in inactive branch");
        Reject(source, s => s["family_options"]["parameters"][4]["formula"] = "if(true, 1, 3 mm)", "unit mismatch in inactive branch");
        Reject(source, s => s["family_options"]["parameters"][4]["formula"] = "Largeur / (Largeur - Largeur)", "division by zero");
        Reject(source, s => s["family_options"]["parameters"][4]["formula"] = "Process.Start(1)", "formula injection");
        Reject(source, s => s["family_options"]["representations"][0]["visible_parameter"] = "Largeur", "visibility length");
        Reject(source, s => s["family_options"]["representations"][0]["component"] = "Missing", "unknown component");
        Reject(source, s => s["family_options"]["parameters"][4]["shared_guid"] = "invalid", "shared identity");
        var trig = FamilyFormula.Parse("sin(30 deg)").Evaluate(_ => throw new Exception());
        if (Math.Abs(trig.Number - 0.5) > 1e-10) throw new Exception("Degree conversion");
        var repeated = JObject.Parse(File.ReadAllText("scripts/codex-tests/parametric-array-grille.design.json"));
        repeated["family_options"] = new JObject { ["parameters"] = new JArray(Parameter("Quantite", "integer", 1, "", true, 0, 2, 10)), ["types"] = new JArray(), ["representations"] = new JArray() };
        repeated["arrays"][0]["quantity_parameter"] = "Quantite";
        var repetition = CodexParametricDesign.Parse(repeated);
        foreach (int count in new[] { 0, 1, 2, 10 })
            if (!repetition.TestCases().Any(c => repetition.Arrays[0].Count(c) == count)) throw new Exception("Missing visible repetition case " + count);
        Reject(repeated, s => s["family_options"]["parameters"][0]["test_values"][0] = -1, "negative visible count");
        Reject(repeated, s => s["arrays"][0]["quantity_parameter"] = "Largeur", "non-integer quantity source");
        var connected = (JObject)source.DeepClone(); connected["category"] = "mechanical";
        connected["parts"][0]["openings"] = new JArray();
        connected["connectors"] = new JArray(new JObject { ["name"] = "Soufflage", ["component"] = (string)connected["parts"][0]["name"], ["face"] = "z_max", ["domain"] = "duct", ["system"] = "supply", ["shape"] = "rectangular",
            ["width_parameter"] = "Largeur", ["height_parameter"] = "Profondeur", ["diameter_parameter"] = "", ["flow"] = "out" });
        connected["symbolic_outlines"] = new JArray(new JObject { ["component"] = (string)connected["parts"][0]["name"], ["plane"] = "xy", ["visible_parameter"] = "AfficherFixations", ["coarse"] = true, ["medium"] = false, ["fine"] = false });
        var mep = CodexParametricDesign.Parse(connected);
        if (mep.Connectors.Count != 1 || mep.Symbols.Count != 1) throw new Exception("Missing connector or symbolic outline");
        Reject(connected, s => s["category"] = "furniture", "connector category mismatch");
        Reject(connected, s => s["connectors"][0]["width_parameter"] = "OptionFixations", "connector dimension type");
        Reject(connected, s => s["symbolic_outlines"][0]["plane"] = "unknown", "invalid symbolic plane");
        File.WriteAllText("tmp/codex-tests/parametric-v1-repetition.design.json", repeated.ToString());
        File.WriteAllText("tmp/codex-tests/parametric-v1-connected.design.json", connected.ToString());
        var polygon = (JObject)source.DeepClone(); polygon["name"] = "Gousset paramétrique";
        polygon["parts"] = new JArray(polygon["parts"][0].DeepClone());
        var part = (JObject)polygon["parts"][0]; part["openings"] = new JArray();
        part["profile_uv"] = new JArray(new JArray(part["minimum"][0].DeepClone(), part["minimum"][1].DeepClone()),
            new JArray(part["maximum"][0].DeepClone(), part["minimum"][1].DeepClone()), new JArray(part["maximum"][0].DeepClone(), part["maximum"][1].DeepClone()));
        var poly = CodexParametricDesign.Parse(polygon);
        if (poly.Parts[0].Profile.Length != 3) throw new Exception("Polygon profile missing");
        double area = CodexProfileDesign.Area(poly.Parts[0].Profile.Select(p => p.Select(v => v.Value(poly.Initial)).ToArray()).ToArray());
        if (Math.Abs(area - 180000) > 0.1) throw new Exception("Triangle area");
        Reject(polygon, s => s["parts"][0]["profile_uv"][2] = s["parts"][0]["profile_uv"][1].DeepClone(), "collapsed polygon");
        Reject(polygon, s => s["parts"][0]["maximum"][0]["offset_mm"] = 2, "polygon envelope mismatch");
        File.WriteAllText("tmp/codex-tests/parametric-v1-polygon.design.json", polygon.ToString());
        File.WriteAllText("tmp/codex-tests/parametric-v1.design.json", source.ToString());
        Console.WriteLine("PASS: V1 typed formulas, type/instance, named types, yes/no visibility, exact thresholds, text, units and graph validation");
        foreach (var file in Directory.GetFiles("scripts/codex-tests/diagnostic-fixtures", "*.json"))
        {
            var original = JObject.Parse(File.ReadAllText(file)); var before = original.ToString();
            var replay = CodexParametricDesign.Parse(original);
            if (original.ToString() != before) throw new Exception("Input normalization mutated diagnostic");
            var normalized = CodexParametricInput.Normalize(original);
            if (!JToken.DeepEquals(normalized, CodexParametricInput.Normalize(normalized))) throw new Exception("Normalization is not idempotent");
            if (replay.Metadata.Load || replay.Metadata.Place) throw new Exception("Normalization elevated project permissions");
            if (replay.Parts.Count != 5 || replay.Arrays.Count != 1 || replay.Initial["Largeur"] != 600 || replay.Initial["Hauteur"] != 800)
                throw new Exception("Diagnostic replay changed requested geometry");
            if (Math.Abs(replay.Initial["Pas_ventelles"] - (50 + 82 / Math.Sqrt(2))) > 1e-6) throw new Exception("Diagnostic formula changed");
            Console.WriteLine("PASS: replay recorded format failure " + Path.GetFileName(file));
        }
        Reject(source, s => { s["family_options"]["parameters"][0]["scope"] = "type"; }, "contradictory scope");
        Reject(source, s => { s["family_options"]["parameters"].Last["value"] = null; s["family_options"]["parameters"].Last["formula"] = ""; }, "missing free value");
    }
    private static void Reject(JObject original, Action<JObject> change, string name)
    {
        var source = (JObject)original.DeepClone(); change(source);
        try { CodexParametricDesign.Parse(source); } catch (InvalidOperationException) { Console.WriteLine("PASS: reject " + name); return; }
        throw new Exception("Accepted " + name);
    }
}

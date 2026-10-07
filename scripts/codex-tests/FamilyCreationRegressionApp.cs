using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BIMaestro.CodexTests
{
    // Isolated native regression against the actual built plugin. No user file
    // is opened; the PID marker prevents execution in an existing Revit session.
    public sealed class FamilyCreationRegressionApp : IExternalApplication
    {
        private UIControlledApplication application;
        private string Root => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public Result OnStartup(UIControlledApplication app) { application = app; app.Idling += Run; return Result.Succeeded; }
        public Result OnShutdown(UIControlledApplication app) { app.Idling -= Run; return Result.Succeeded; }
        private static JObject Ref(string id) => new JObject { ["ref"] = id };
        private static JObject New(string id, string type) => new JObject { ["op"] = "new", ["id"] = id, ["type"] = "Autodesk.Revit.DB." + type, ["args"] = new JArray() };
        private static JObject Get(string id, string type, string member) => new JObject { ["op"] = "get", ["id"] = id, ["type"] = "Autodesk.Revit.DB." + type, ["member"] = member };
        private static JObject Call(string id, string target, string member, params object[] args) => new JObject { ["op"] = "call", ["id"] = id, ["target"] = Ref(target), ["member"] = member, ["args"] = new JArray(args) };
        private static JObject Static(string id, string type, string member, params object[] args) => new JObject { ["op"] = "static", ["id"] = id, ["type"] = "Autodesk.Revit.DB." + type, ["member"] = member, ["args"] = new JArray(args) };
        private static JObject Mm(string id, double mm) => new JObject { ["op"] = "mm", ["id"] = id, ["value"] = mm };
        private static JObject Program()
        {
            var steps = new JArray(Get("zero", "XYZ", "Zero"), Get("x", "XYZ", "BasisX"), Get("y", "XYZ", "BasisY"), Get("z", "XYZ", "BasisZ"),
                Static("plane", "Plane", "CreateByNormalAndOrigin", Ref("z"), Ref("zero")), Static("work", "SketchPlane", "Create", Ref("doc"), Ref("plane")),
                new JObject { ["op"] = "xyz_mm", ["id"] = "holeCenter", ["args"] = new JArray(0, 90, 0) }, Mm("height", 20));
            foreach (var loop in new[] { new { id = "outer", r = 110.0, center = "zero" }, new { id = "inner", r = 50.0, center = "zero" }, new { id = "hole", r = 9.5, center = "holeCenter" } })
            {
                steps.Add(Mm(loop.id + "Radius", loop.r)); steps.Add(New(loop.id, "CurveArray"));
                for (int half = 0; half < 2; half++)
                {
                    string id = loop.id + half;
                    steps.Add(Static(id, "Arc", "Create", Ref(loop.center), Ref(loop.id + "Radius"), half * Math.PI, (half + 1) * Math.PI, Ref("x"), Ref("y")));
                    steps.Add(Call(id + "Append", loop.id, "Append", Ref(id)));
                }
            }
            steps.Add(New("bodyLoops", "CurveArrArray")); steps.Add(Call("outerAppend", "bodyLoops", "Append", Ref("outer"))); steps.Add(Call("innerAppend", "bodyLoops", "Append", Ref("inner")));
            steps.Add(Call("body", "factory", "NewExtrusion", true, Ref("bodyLoops"), Ref("work"), Ref("height")));
            steps.Add(New("holeLoops", "CurveArrArray")); steps.Add(Call("holeAppend", "holeLoops", "Append", Ref("hole")));
            steps.Add(Call("void", "factory", "NewExtrusion", false, Ref("holeLoops"), Ref("work"), Ref("height")));
            steps.Add(Call("regen", "doc", "Regenerate")); steps.Add(New("cutElements", "CombinableElementArray"));
            steps.Add(Call("bodyAppend", "cutElements", "Append", Ref("body"))); steps.Add(Call("voidAppend", "cutElements", "Append", Ref("void")));
            steps.Add(Call("combination", "doc", "CombineElements", Ref("cutElements"))); steps.Add(Call("finalRegen", "doc", "Regenerate"));
            return new JObject { ["steps"] = steps, ["outputs"] = new JArray("combination") };
        }
        private static JArray FormulaSteps()
        {
            return new JArray(Get("group", "GroupTypeId", "Geometry"), Get("integer", "SpecTypeId+Int", "Integer"), Get("angle", "SpecTypeId", "Angle"),
                Call("authoredType", "manager", "NewType", "Secteur"),
                Call("count", "manager", "AddParameter", "NombreTrous", Ref("group"), Ref("integer"), true),
                new JObject { ["op"] = "call", ["target"] = Ref("manager"), ["member"] = "Set", ["args"] = new JArray(Ref("count"), 8),
                    ["signature"] = new JArray("Autodesk.Revit.DB.FamilyParameter", "System.Int32") },
                Call("flatAngle", "manager", "AddParameter", "BIM_AnglePlat", Ref("group"), Ref("angle"), false),
                Call("flatAngleValue", "manager", "Set", Ref("flatAngle"), Math.PI),
                Call("halfAngle", "manager", "AddParameter", "DemiAngle", Ref("group"), Ref("angle"), true),
                Call("halfAngleFormula", "manager", "SetFormula", Ref("halfAngle"), "BIM_AnglePlat / NombreTrous"),
                new JObject { ["op"] = "get", ["id"] = "types", ["target"] = Ref("manager"), ["member"] = "Types" },
                new JObject { ["op"] = "get", ["id"] = "typeCount", ["target"] = Ref("types"), ["member"] = "Size" },
                new JObject { ["op"] = "math", ["id"] = "onlyAuthoredType", ["member"] = "equal", ["args"] = new JArray(Ref("typeCount"), 1) },
                new JObject { ["op"] = "assert", ["value"] = Ref("onlyAuthoredType"), ["message"] = "No unintended empty Standard type" });
        }
        private static JObject PipeConnectorProgram()
        {
            var program = Program();
            var steps = (JArray)program["steps"];
            steps.Add(Get("length", "SpecTypeId", "Length"));
            steps.Add(Get("group", "GroupTypeId", "Geometry"));
            steps.Add(Call("dn", "manager", "AddParameter", "DN", Ref("group"), Ref("length"), true));
            steps.Add(Mm("dnInitial", 50)); steps.Add(Call("setInitialDn", "manager", "Set", Ref("dn"), Ref("dnInitial")));
            steps.Add(new JObject { ["op"] = "pipe_connectors", ["id"] = "connectors", ["target"] = Ref("body"), ["diameter"] = Ref("dn"), ["axis"] = Ref("z"), ["center"] = Ref("zero") });
            steps.Add(Call("connectorCount", "connectors", "get_Count"));
            steps.Add(new JObject { ["op"] = "math", ["id"] = "twoConnectors", ["member"] = "equal", ["args"] = new JArray(Ref("connectorCount"), 2) });
            steps.Add(new JObject { ["op"] = "assert", ["value"] = Ref("twoConnectors"), ["message"] = "Exactly two native pipe connectors" });
            foreach (double dn in new[] { 50.0, 200.0, 500.0, 50.0 })
            {
                steps.Add(Mm("dnValue", dn)); steps.Add(Call("setDn", "manager", "Set", Ref("dn"), Ref("dnValue"))); steps.Add(Call("flex", "doc", "Regenerate"));
                steps.Add(new JObject { ["op"] = "foreach", ["target"] = Ref("connectors"), ["var"] = "connector", ["steps"] = new JArray(
                    new JObject { ["op"] = "get", ["id"] = "radius", ["target"] = Ref("connector"), ["member"] = "Radius" },
                    new JObject { ["op"] = "math", ["id"] = "error", ["member"] = "subtract", ["args"] = new JArray(Ref("radius"), dn / 609.6) },
                    new JObject { ["op"] = "math", ["id"] = "absolute", ["member"] = "abs", ["args"] = new JArray(Ref("error")) },
                    new JObject { ["op"] = "math", ["id"] = "correct", ["member"] = "less", ["args"] = new JArray(Ref("absolute"), 1e-7) },
                    new JObject { ["op"] = "assert", ["value"] = Ref("correct"), ["message"] = "Connector DN " + dn }) });
            }
            program["outputs"] = new JArray("connectors");
            return program;
        }
        private void Run(object sender, IdlingEventArgs args)
        {
            string marker = Path.Combine(Root, "launched-pid.txt");
            if (!File.Exists(marker)) return;
            if (File.ReadAllText(marker).Trim() != Process.GetCurrentProcess().Id.ToString()) { application.Idling -= Run; return; }
            application.Idling -= Run;
            File.WriteAllText(Path.Combine(Root, "started.txt"), Process.GetCurrentProcess().Id.ToString());
            var ui = (UIApplication)sender;
            try
            {
                if (ui.Application.Documents.Size != 0) throw new Exception("Tests require an empty dedicated Revit.");
                string enginePath = File.ReadAllText(Path.Combine(Root, "engine-path.txt")).Trim();
                var assembly = Assembly.LoadFrom(enginePath);
                if (!string.Equals(Path.GetFullPath(assembly.Location), Path.GetFullPath(enginePath), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Revit already loaded a different engine assembly: " + assembly.Location + ". Build the test engine with a distinct AssemblyName.");
                string pdfTest = Path.Combine(Root, "pdf-test.txt");
                if (File.Exists(pdfTest))
                {
                    var pdfType = assembly.GetType("BIMaestro.Codex.CodexPdfAttachment", true);
                    var attachment = pdfType.GetMethod("FromFile", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { File.ReadAllText(pdfTest).Trim() });
                    var pages = (Array)pdfType.GetMethod("RenderPages", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(attachment, new object[] { new[] { 1 } });
                    var png = (byte[])pages.GetValue(0).GetType().GetField("Item2").GetValue(pages.GetValue(0));
                    if (png.Length < 100) throw new Exception("PDF rendering returned an empty image.");
                    File.WriteAllBytes(Path.Combine(Root, "pdf-page.png"), png);
                    File.WriteAllText(Path.Combine(Root, "result.json"), new JObject { ["passed"] = true, ["pdf_render_bytes"] = png.Length }.ToString());
                    return;
                }
                var engine = assembly.GetType("BIMaestro.Codex.CodexFamilyProgram", true);
                var method = engine.GetMethod("CreateSteps", BindingFlags.Static | BindingFlags.NonPublic);
                var api = engine.GetMethod("Api", BindingFlags.Static | BindingFlags.NonPublic);
                foreach (string member in new[] { "CombineElements", "SaveAs" })
                {
                    var signature = JObject.FromObject(api.Invoke(null, new object[] { JObject.Parse("{type_name:'Autodesk.Revit.DB.Document',member_name:'" + member + "',offset:0,limit:100}") }));
                    if (signature["members"].Count() == 0 || signature["members"].Any(m => (bool)m["exposed"] != (member == "CombineElements"))) throw new Exception("Incorrect exposure: " + member);
                }
                var request = new JObject { ["name"] = "RegressionBride", ["category"] = "pipe_fitting", ["hosting"] = "free",
                    ["description"] = "Circular plate with a real void cut", ["geometry_policy"] = new JObject { ["mode"] = "shared", ["reason"] = "" },
                    ["program_json"] = Program().ToString(Newtonsoft.Json.Formatting.None), ["validate_only"] = true };
                string outputs = Path.Combine(Root, "families");
                object Execute()
                {
                    object result = null;
                    var sequence = (IEnumerable)method.Invoke(null, new object[] { ui, request, outputs });
                    var enumerator = sequence.GetEnumerator();
                    try { while (enumerator.MoveNext()) if (enumerator.Current != null) result = enumerator.Current; }
                    finally { (enumerator as IDisposable)?.Dispose(); }
                    return result ?? throw new Exception("No artifact returned.");
                }
                JObject Artifact(object result) => JObject.FromObject(result.GetType().GetField("Report", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result));
                string replay = Path.Combine(Root, "replay-request.json");
                if (File.Exists(replay))
                {
                    request = JObject.Parse(File.ReadAllText(replay));
                    request["validate_only"] = true;
                    var replayReport = Artifact(Execute());
                    if (ui.Application.Documents.Size != 0 || Directory.Exists(outputs)) throw new Exception("Replay leaked a document or saved a dry run.");
                    File.WriteAllText(Path.Combine(Root, "result.json"), new JObject { ["passed"] = true, ["replay"] = replayReport }.ToString());
                    return;
                }
                var dry = Artifact(Execute());
                if ((bool)dry["saved"] || ui.Application.Documents.Size != 0 || Directory.Exists(outputs)) throw new Exception("Dry run saved or leaked a document.");
                // Reproduce the angle formula from the failed flange in both the
                // root and a nested family, before any geometry constraints exist.
                var formulas = Program();
                var formulaSteps = FormulaSteps();
                foreach (var step in ((JArray)formulas["steps"]).ToArray()) formulaSteps.Add(step);
                var child = FormulaSteps();
                foreach (var step in (JArray)Program()["steps"]) child.Add(step);
                formulaSteps.Add(new JObject { ["op"] = "nested", ["id"] = "formulaChild", ["name"] = "FormulaChild", ["hosting"] = "free", ["steps"] = child });
                formulas["steps"] = formulaSteps;
                request["program_json"] = formulas.ToString(Newtonsoft.Json.Formatting.None);
                Execute();
                request["program_json"] = PipeConnectorProgram().ToString(Newtonsoft.Json.Formatting.None);
                Execute();
                request["program_json"] = Program().ToString(Newtonsoft.Json.Formatting.None);
                request["validate_only"] = false;
                var saved = Artifact(Execute());
                if (!(bool)saved["saved"] || ui.Application.Documents.Size != 0) throw new Exception("Successful native creation not saved/closed.");
                var doc = ui.Application.OpenDocumentFile((string)saved["file"]);
                try
                {
                    var combination = doc.GetElement((string)saved["program"]["outputs"]["combination"]["unique_id"]);
                    double volume;
                    using (var geometry = combination.get_Geometry(new Options())) volume = geometry.OfType<Solid>().Sum(s => s.Volume) * Math.Pow(304.8, 3);
                    double expected = Math.PI * (110 * 110 - 50 * 50 - 9.5 * 9.5) * 20;
                    // Revit integrates curved-face volumes approximately. 0.01%
                    // tolerates that error while an uncut hole differs by ~0.95%.
                    if (Math.Abs(volume - expected) > expected * 1e-4) throw new Exception("Void did not cut the circular plate: " + volume + " / " + expected);
                    if (!new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Any(v => v.Name == "BIMaestro - Aperçu")) throw new Exception("Final preview missing.");
                }
                finally { doc.Close(false); }
                int fileCount = Directory.GetFiles(outputs, "*.rfa", SearchOption.AllDirectories).Length;
                var bad = Program(); ((JArray)bad["steps"]).Add(new JObject { ["op"] = "assert", ["value"] = false, ["message"] = "Expected failure before saving" });
                request["program_json"] = bad.ToString(Newtonsoft.Json.Formatting.None);
                bool rejected = false;
                try { Execute(); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected || ui.Application.Documents.Size != 0 || Directory.GetFiles(outputs, "*.rfa", SearchOption.AllDirectories).Length != fileCount) throw new Exception("Failed program saved/leaked a partial family.");
                File.WriteAllText(Path.Combine(Root, "result.json"), new JObject { ["passed"] = true, ["combine_elements_real_void_cut"] = true,
                    ["explicit_type_angle_formula_root_and_nested"] = true,
                    ["two_pipe_connectors_dn_50_200_500_50"] = true,
                    ["dry_run_no_file_no_document"] = true, ["failed_program_no_partial_rfa"] = true, ["final_preview"] = true, ["file"] = saved["file"] }.ToString());
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(Root, "result.json"), new JObject { ["passed"] = false, ["error"] = error.ToString(),
                ["pdf_assemblies"] = new JArray(AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.Contains("Skia") || a.GetName().Name.Contains("PdfPig") || a.GetName().Name.Contains("HarfBuzz") || a.GetName().Name == "System.Memory").Select(a => a.FullName + " | " + a.Location)) }.ToString()); }
            finally { if (ui.Application.Documents.Size == 0) ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit)); }
        }
    }
}

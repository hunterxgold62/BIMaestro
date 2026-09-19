using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.DB;
using BIMaestro.Codex;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;

namespace BIMaestro.CodexTests
{
    // Test-only application. Loaded in a separately launched empty Revit process.
    // It neither opens user projects nor closes Revit itself.
    public sealed class NativeValidationApp : IExternalApplication
    {
        private UIControlledApplication application;
        private string DirectoryPath => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public Result OnStartup(UIControlledApplication value)
        {
            application = value; application.Idling += OnIdle;
            return Result.Succeeded;
        }
        private async void OnIdle(object sender, IdlingEventArgs args)
        {
            // Revit may hot-load a newly registered add-in into an existing session.
            // Only the process explicitly launched by StartNativeHarness may run tests.
            string launch = Path.Combine(DirectoryPath, "launched-pid.txt");
            if (!File.Exists(launch)) return;
            if (File.ReadAllText(launch).Trim() != Process.GetCurrentProcess().Id.ToString())
            { application.Idling -= OnIdle; return; }
            application.Idling -= OnIdle;
            File.WriteAllText(Path.Combine(DirectoryPath, "started.txt"), Process.GetCurrentProcess().Id.ToString());
            try
            {
                var ui = sender as UIApplication ?? throw new InvalidOperationException("Contexte UIApplication absent.");
                if (ui.Application.Documents.Size != 0) throw new InvalidOperationException("Le banc exige une instance Revit vide ; aucun document utilisateur ne sera modifié.");
                var familyEdits = FamilyEditNativeTests.Run(ui);
#if FAMILY_EDIT_ONLY
                File.WriteAllText(Path.Combine(DirectoryPath, "result.json"), JsonConvert.SerializeObject(familyEdits, Formatting.Indented));
                if (ui.Application.Documents.Size == 0) ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
                return;
#endif
                var parameters = ValidateParameterReuse(ui);
                var categories = ValidateCategories(ui);
                var wallTemplate = Newtonsoft.Json.Linq.JObject.FromObject(CodexFamilyBuilder.TemplateInfo(ui,"wall"));
                var wall = wallTemplate["walls"].First();
                if (Math.Abs((double)wall["maximum_y_mm"]-(double)wall["minimum_y_mm"]-(double)wall["width_mm"])>0.01) throw new Exception("Host wall measurement inconsistent.");
                var report = Newtonsoft.Json.Linq.JObject.FromObject(CodexNativeValidation.Run(ui, message => File.WriteAllText(Path.Combine(DirectoryPath, "progress.txt"), DateTime.Now.ToString("O") + " " + message), RenderRepresentation));
                report["parameter_reuse_tests"] = Newtonsoft.Json.Linq.JObject.FromObject(parameters);
                report["category_tests"] = Newtonsoft.Json.Linq.JObject.FromObject(categories);
                report["family_edit_tests"] = Newtonsoft.Json.Linq.JObject.FromObject(familyEdits);
                report["wall_template_test"] = wallTemplate;
                var fixture = Assembly.GetExecutingAssembly().GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("polygon-profile.json"));
                if (fixture != null)
                {
                    Newtonsoft.Json.Linq.JObject design;
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(fixture))
                    using (var reader = new StreamReader(stream)) design = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
                    int count = 0;
                    var cancelBridge = new CodexRevitBridge(null) { ShareContext = true, AllowChanges = true, ApplyDirectly = true };
                    cancelBridge.AttachEvent(ExternalEvent.Create(cancelBridge));
                    using (var bridge = new CodexRevitBridge(null) { ShareContext = true, AllowChanges = true, ApplyDirectly = true })
                    {
                        bridge.AttachEvent(ExternalEvent.Create(bridge));
                        bridge.CreationProgress += message =>
                        {
                            count++;
                            if (ui.Application.Documents.Cast<Document>().Any(d => d.IsModifiable)) throw new Exception("Transaction left open between steps.");
                            File.WriteAllText(Path.Combine(DirectoryPath, "progress.txt"), message);
                        };
                        await bridge.CallAsync("revit_validate_parametric_family", design);
                        if (count < 5) throw new Exception("Creation did not yield between steps.");
                        // Check document cleanup in a fresh API callback below.
                    }
                    using (var bridge = cancelBridge)
                    {
                        int cancelledSteps = 0;
                        bridge.CreationProgress += message => { if (++cancelledSteps == 3) bridge.CancelPending(); };
                        bool cancelled = false;
                        try { await bridge.CallAsync("revit_validate_parametric_family", design); }
                        catch (OperationCanceledException) { cancelled = true; }
                        if (!cancelled) throw new Exception("Cancellation was ignored.");
                    }
                    report["staged_creation_tests"] = Newtonsoft.Json.Linq.JObject.FromObject(new { passed = true, steps = count, cancellation = true });
                }
                File.WriteAllText(Path.Combine(DirectoryPath, "result.json"), report.ToString(Formatting.Indented));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(DirectoryPath, "result.json"), JsonConvert.SerializeObject(new { error = ex.ToString() }, Formatting.Indented)); }
        }
        public Result OnShutdown(UIControlledApplication value) { if (application != null) application.Idling -= OnIdle; return Result.Succeeded; }

        private void RenderRepresentation(Document family)
        {
            Document project = null;
            try
            {
                project = family.Application.NewProjectDocument(UnitSystem.Metric);
                var loaded = family.LoadFamily(project);
                var symbol = loaded.GetFamilySymbolIds().Select(project.GetElement).OfType<FamilySymbol>().First();
                var views = new List<ElementId>();
                using (var t = new Transaction(project, "Test des représentations plan et 3D"))
                {
                    t.Start();
                    var level = Level.Create(project, 0);
                    symbol.Activate(); project.Regenerate();
                    project.Create.NewFamilyInstance(XYZ.Zero, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    var planType = new FilteredElementCollector(project).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.FloorPlan);
                    var plan = ViewPlan.Create(project, planType.Id, level.Id); plan.Name = "Representation - Plan";
                    var type3d = new FilteredElementCollector(project).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                    var iso = View3D.CreateIsometric(project, type3d.Id); iso.Name = "Representation - 3D";
                    var forward = new XYZ(-1,-1,-0.7).Normalize();
                    iso.SetOrientation(new ViewOrientation3D(new XYZ(15,15,12), XYZ.BasisZ.Subtract(forward.Multiply(XYZ.BasisZ.DotProduct(forward))).Normalize(), forward));
                    foreach (var view in new View[] { plan, iso })
                    {
                        view.DetailLevel = ViewDetailLevel.Fine; view.DisplayStyle = DisplayStyle.FlatColors;
                        views.Add(view.Id);
                    }
                    // A background cross makes the opacity of the filled/masking regions visible.
                    var work = SketchPlane.Create(project, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0,0,-0.01)));
                    project.Create.NewModelCurve(Line.CreateBound(new XYZ(-5,0,-0.01), new XYZ(7,0,-0.01)), work);
                    project.Create.NewModelCurve(Line.CreateBound(new XYZ(0,-5,-0.01), new XYZ(0,5,-0.01)), work);
                    if (t.Commit() != TransactionStatus.Committed) throw new Exception("Representation project transaction failed");
                }
                string folder = Path.Combine(DirectoryPath, "representation-" + Guid.NewGuid().ToString("N").Substring(0,8));
                Directory.CreateDirectory(folder);
                var export = new ImageExportOptions { ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, "view"),
                    HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                    ZoomType = ZoomFitType.FitToPage, PixelSize = 1000, FitDirection = FitDirectionType.Horizontal, ImageResolution = ImageResolution.DPI_150 };
                export.SetViewsAndSheets(views); project.ExportImage(export);
                if (Directory.GetFiles(folder, "*.png").Length != 2) throw new Exception("Missing plan/3D visual test exports");
            }
            finally { if (project != null && project.IsValidObject) project.Close(false); }
        }

        private static object ValidateCategories(UIApplication ui)
        {
            string template = Directory.EnumerateFiles(ui.Application.FamilyTemplatePath, "*.rft", SearchOption.AllDirectories).First(p =>
                new[] { "Modèle générique métrique", "Metric Generic Model" }.Contains(Path.GetFileNameWithoutExtension(p)));
            var doc = ui.Application.NewFamilyDocument(template);
            try
            {
                var placement = doc.OwnerFamily.FamilyPlacementType;
                int count = 0;
                foreach (var code in CodexFamilyDesign.Categories)
                {
                    var args = new Newtonsoft.Json.Linq.JObject { ["category"] = code };
                    CodexFamilyTools.SetCategory(doc, args, (title, detail) => true,
                        name => { var t = new Transaction(doc, name); t.Start(); return t; },
                        t => { if (t.Commit() != TransactionStatus.Committed) throw new Exception("Category transaction failed"); });
                    if (doc.OwnerFamily.FamilyCategory.Id != Category.GetCategory(doc, CodexFamilyBuilder.CategoryId(code)).Id ||
                        doc.OwnerFamily.FamilyPlacementType != placement)
                        throw new Exception("Category or hosting mismatch for " + code);
                    var read = Newtonsoft.Json.Linq.JObject.FromObject(CodexFamilyTools.Read(doc));
                    if ((string)read["category"] != code) throw new Exception("Read category mismatch");
                    count++;
                }
                bool rejected = false;
                try
                {
                    CodexFamilyTools.SetCategory(doc, new Newtonsoft.Json.Linq.JObject { ["category"] = "furniture" },
                        (title, detail) => false, name => throw new Exception("Rejected operation started a transaction"), t => { });
                }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("Refused category change was applied");
                return new { passed = true, categories = count, hosting_preserved = true, refusal_respected = true };
            }
            finally { doc.Close(false); }
        }

        private static object ValidateParameterReuse(UIApplication ui)
        {
            string root = ui.Application.FamilyTemplatePath;
            string template = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).First(p =>
                new[] { "Modèle générique métrique", "Metric Generic Model" }.Contains(Path.GetFileNameWithoutExtension(p)));
            var doc = ui.Application.NewFamilyDocument(template);
            try
            {
                using (var t = new Transaction(doc, "Test temporaire — réutiliser les paramètres"))
                {
                    t.Start(); var manager = doc.FamilyManager;
                    if (manager.CurrentType == null) manager.NewType("Standard");
                    var length = manager.AddParameter("Longueur", GroupTypeId.Geometry, SpecTypeId.Length, false);
                    var reused = CodexParameterBuilder.GetOrAdd(manager, "longueur", GroupTypeId.Geometry, SpecTypeId.Length, false);
                    if (length.Id != reused.Id) throw new Exception("Case-insensitive parameter reuse failed");
                    manager.Set(reused, 2);
                    var calculated = CodexParameterBuilder.NewInternal(manager, "Longueur", GroupTypeId.Geometry, SpecTypeId.Length, false);
                    if (calculated.Id == length.Id) throw new Exception("Internal parameter overwrote user parameter");
                    manager.SetFormula(calculated, CodexParameterBuilder.NativeFormula("longueur * 2", new Dictionary<string, FamilyParameter> { ["longueur"] = reused }));
                    doc.Regenerate();
                    if (Math.Abs(manager.CurrentType.AsDouble(calculated).Value - 4) > 1e-8) throw new Exception("Alias formula failed in Revit");
                    Reject(() => CodexParameterBuilder.GetOrAdd(manager, "Longueur", GroupTypeId.Geometry, SpecTypeId.Angle, false));
                    Reject(() => CodexParameterBuilder.GetOrAdd(manager, calculated.Definition.Name, GroupTypeId.Geometry, SpecTypeId.Length, false));
                    var registry = new CodexFamilyParameters();
                    var shared = new FamilyParameterSpec { Name = "TestPartage", Kind = "length", Instance = false,
                        SharedGuid = "6e53c585-11b2-4a5c-852a-b4a780d6caa1", Group = "geometry", Description = "Test temporaire", Value = FamilyValue.Numeric(400, 1) };
                    registry.Parameters.Add(shared); registry.Types.Add(new FamilyTypeSpec { Name = "Standard" });
                    var first = new CodexParameterBuilder(doc, registry).Parameters[shared.Name];
                    int count = manager.Parameters.Size;
                    shared.Name = "testpartage";
                    var second = new CodexParameterBuilder(doc, registry).Parameters[shared.Name];
                    if (first.Id != second.Id || manager.Parameters.Size != count) throw new Exception("Shared GUID reuse failed");
                    shared.SharedGuid = "d2381d2d-6db2-491c-83b7-39c10e0e543c";
                    Reject(() => new CodexParameterBuilder(doc, registry));
                    if (manager.Parameters.Size != count) throw new Exception("GUID conflict created a duplicate");
                    if (t.Commit() != TransactionStatus.Committed) throw new Exception("Parameter reuse transaction failed");
                }
                return new { passed = true, case_insensitive_reuse = true, formula_alias = true, internal_name_collision = true,
                    shared_guid_reuse = true, incompatible_kind_formula_guid_rejected = true };
            }
            finally { doc.Close(false); }
        }
        private static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new Exception("Expected incompatible parameter to be rejected");
        }
    }
}

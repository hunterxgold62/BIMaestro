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
            File.WriteAllText(Path.Combine(DirectoryPath, "started.txt"), Process.GetCurrentProcess().Id.ToString());
            return Result.Succeeded;
        }
        private void OnIdle(object sender, IdlingEventArgs args)
        {
            application.Idling -= OnIdle;
            try
            {
                var ui = sender as UIApplication ?? throw new InvalidOperationException("Contexte UIApplication absent.");
                if (ui.Application.Documents.Size != 0) throw new InvalidOperationException("Le banc exige une instance Revit vide ; aucun document utilisateur ne sera modifié.");
                var parameters = ValidateParameterReuse(ui);
                var report = Newtonsoft.Json.Linq.JObject.FromObject(CodexNativeValidation.Run(ui, message => File.WriteAllText(Path.Combine(DirectoryPath, "progress.txt"), DateTime.Now.ToString("O") + " " + message)));
                report["parameter_reuse_tests"] = Newtonsoft.Json.Linq.JObject.FromObject(parameters);
                File.WriteAllText(Path.Combine(DirectoryPath, "result.json"), report.ToString(Formatting.Indented));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(DirectoryPath, "result.json"), JsonConvert.SerializeObject(new { error = ex.ToString() }, Formatting.Indented)); }
        }
        public Result OnShutdown(UIControlledApplication value) { if (application != null) application.Idling -= OnIdle; return Result.Succeeded; }

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

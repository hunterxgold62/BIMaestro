using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BIMaestro.Codex
{
    internal static class CodexNativeValidation
    {
        // Every scenario creates and closes its own temporary family. No saved RFA,
        // no project load, no model turn and no access to the source model's geometry.
        internal static object Run(UIApplication app, Action<string> progress = null, Action<Document> inspect = null)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("BIMaestro.CodexTests.", StringComparison.Ordinal)).OrderBy(n => n).ToArray();
            if (names.Length == 0) throw new InvalidOperationException("Les scénarios de validation natifs ne sont pas inclus dans cette DLL.");
            var results = new List<object>(); int passed = 0;
            foreach (var name in names)
            {
                progress?.Invoke("Début : " + name);
                var watch = Stopwatch.StartNew();
                try
                {
                    using (var stream = assembly.GetManifestResourceStream(name))
                    using (var reader = new StreamReader(stream))
                    {
                        var source = JObject.Parse(reader.ReadToEnd());
                        source["load_into_project"] = false; source["place_at_origin"] = false;
                        var design = source["parameters"] == null ? null : CodexParametricDesign.Parse(source);
                        var metadata = design?.Metadata ?? CodexFamilyDesign.Parse(source);
                        var artifact = CodexFamilyBuilder.Create(app, null, metadata, true, design, testHostPlacement: true, inspect: metadata.Representation == null ? null : inspect);
                        results.Add(new { scenario = name, passed = true, seconds = watch.Elapsed.TotalSeconds, report = artifact.Report }); passed++;
                    }
                }
                catch (Exception ex) { results.Add(new { scenario = name, passed = false, seconds = watch.Elapsed.TotalSeconds, error = ex.Message, details = ex.ToString() }); }
                progress?.Invoke("Terminé : " + name);
            }
            var report = new { revit_version = app.Application.VersionNumber, assembly_version = assembly.GetName().Version.ToString(), build_id = assembly.ManifestModule.ModuleVersionId,
                utc = DateTime.UtcNow, passed, total = names.Length, saved_families = false, project_modified = false, results };
            string directory = Path.Combine(CodexClient.DataDirectory, "Validation"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "Revit-" + app.Application.VersionNumber + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
            return new { report_path = path, validation = report, next = "Ces tests portent sur les familles temporaires. Les scénarios host_opening vérifient aussi le chargement, le placement et la découpe après variation dans un projet temporaire non enregistré ; les vues en projet restent un contrôle séparé." };
        }
    }
}

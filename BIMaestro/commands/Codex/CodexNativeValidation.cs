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
        internal static object Run(UIApplication app, Action<string> progress = null)
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
                        var design = CodexParametricDesign.Parse(source);
                        var artifact = CodexFamilyBuilder.Create(app, null, design.Metadata, true, design);
                        results.Add(new { scenario = name, passed = true, seconds = watch.Elapsed.TotalSeconds, report = artifact.Report }); passed++;
                    }
                }
                catch (Exception ex) { results.Add(new { scenario = name, passed = false, seconds = watch.Elapsed.TotalSeconds, error = ex.Message }); }
                progress?.Invoke("Terminé : " + name);
            }
            var report = new { revit_version = app.Application.VersionNumber, assembly_version = assembly.GetName().Version.ToString(),
                utc = DateTime.UtcNow, passed, total = names.Length, saved_families = false, project_modified = false, results };
            string directory = Path.Combine(CodexClient.DataDirectory, "Validation"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "Revit-" + app.Application.VersionNumber + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
            return new { report_path = path, validation = report, next = "Ces tests portent sur les familles temporaires. Le chargement, les occurrences et les vues en projet restent des contrôles séparés." };
        }
    }
}

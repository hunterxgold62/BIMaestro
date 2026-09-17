using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;
using System.Reflection;

namespace BIMaestro.Codex
{
    internal static class CodexDiagnostics
    {
        // Only failed Revit operations: no authentication, conversation or reference images.
        internal static string RecordFailure(string tool, JObject arguments, Exception error)
        {
            try
            {
                string directory = Path.Combine(CodexClient.DataDirectory, "Diagnostics");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".json");
                var assembly = Assembly.GetExecutingAssembly();
                File.WriteAllText(path, JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, tool,
                    build_id = assembly.ManifestModule.ModuleVersionId, runtime = Environment.Version.ToString(),
                    arguments, context = error.Data, error = error.ToString() }, Formatting.Indented), new UTF8Encoding(false));
                return path;
            }
            catch { return null; } // Diagnostics must never hide the original operation failure.
        }
    }
}

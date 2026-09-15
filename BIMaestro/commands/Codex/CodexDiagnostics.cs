using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

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
                File.WriteAllText(path, JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, tool,
                    arguments, error = error.ToString() }, Formatting.Indented), new UTF8Encoding(false));
                return path;
            }
            catch { return null; } // Diagnostics must never hide the original operation failure.
        }
    }
}

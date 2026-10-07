using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace BIMaestro.Codex
{
    internal static class CodexPdfRenderIsolation
    {
        internal static (int Page, byte[] Png)[] Render(string path, long fileSize, int[] pages)
        {
#if REVIT2025_OR_GREATER
            // .NET 8 does not support secondary AppDomains. Framework support
            // assembly identity conflicts concern Revit 2023/2024 (.NET 4.8).
            var pdf = CodexPdfAttachment.FromFile(path);
            return pdf.RenderPagesLocal(pages);
#else
            string assemblyPath = typeof(CodexPdfRenderIsolation).Assembly.Location;
            string binaryDirectory = Path.GetDirectoryName(assemblyPath);
            string temporary = Path.Combine(Path.GetTempPath(), "BIMaestroPdf", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            AppDomain domain = null;
            try
            {
                // Revit's already-loaded System.Memory cannot satisfy PdfPig's
                // filter interface while the implementation uses another version.
                // Redirect only inside this domain, using the deployed DLL versions.
                var config = new StringBuilder("<configuration><runtime><assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">");
                foreach (string name in new[] { "System.Memory", "System.Buffers", "System.Runtime.CompilerServices.Unsafe", "System.Numerics.Vectors",
                    "System.Threading.Tasks.Extensions", "Microsoft.Bcl.AsyncInterfaces", "Microsoft.Bcl.HashCode" })
                {
                    string dll = Path.Combine(binaryDirectory, name + ".dll");
                    if (!File.Exists(dll)) continue;
                    var identity = AssemblyName.GetAssemblyName(dll);
                    string token = string.Concat(identity.GetPublicKeyToken().Select(b => b.ToString("x2")));
                    if (token.Length == 0) continue;
                    config.Append("<dependentAssembly><assemblyIdentity name=\"").Append(name).Append("\" publicKeyToken=\"").Append(token)
                        .Append("\" culture=\"neutral\"/><bindingRedirect oldVersion=\"0.0.0.0-65535.65535.65535.65535\" newVersion=\"")
                        .Append(identity.Version).Append("\"/></dependentAssembly>");
                }
                config.Append("</assemblyBinding></runtime></configuration>");
                string configurationFile = Path.Combine(temporary, "pdf.config");
                File.WriteAllText(configurationFile, config.ToString(), Encoding.UTF8);
                domain = AppDomain.CreateDomain("BIMaestro PDF " + Guid.NewGuid().ToString("N"), null,
                    new AppDomainSetup { ApplicationBase = binaryDirectory, ConfigurationFile = configurationFile, DisallowCodeDownload = true });
                // Use a framework interface across the boundary: Revit's LoadFrom
                // context must not resolve a second copy of the plugin's types.
                var worker = (IEnumerator)domain.CreateInstanceFromAndUnwrap(assemblyPath, "BIMaestro.Codex.CodexPdfRenderWorker", false,
                    BindingFlags.Public | BindingFlags.Instance, null, new object[] { path, fileSize, pages }, CultureInfo.InvariantCulture, null);
                if (!worker.MoveNext()) throw new InvalidOperationException("Le rendu PDF n'a retourné aucune image.");
                byte[][] images = (byte[][])worker.Current;
                return pages.Select((page, i) => (page, images[i])).ToArray();
            }
            finally
            {
                try { if (domain != null) AppDomain.Unload(domain); }
                finally { Directory.Delete(temporary, true); }
            }
#endif
        }
    }

#if !REVIT2025_OR_GREATER
    // Only strings, integers and PNG bytes cross the domain boundary. No PdfPig
    // or Skia objects (and no Revit model objects) enter the host AppDomain.
    public sealed class CodexPdfRenderWorker : MarshalByRefObject, IEnumerator
    {
        private readonly string path;
        private readonly long fileSize;
        private readonly int[] pages;
        private byte[][] result;
        public CodexPdfRenderWorker(string path, long fileSize, int[] pages) { this.path = path; this.fileSize = fileSize; this.pages = pages; }
        public object Current => result;
        public bool MoveNext()
        {
            if (result != null) return false;
            if (!File.Exists(path) || new FileInfo(path).Length != fileSize)
                throw new InvalidOperationException("Le PDF a changé depuis son ajout. Joignez-le à nouveau.");
            result = CodexPdfAttachment.FromFile(path).RenderPagesLocal(pages).Select(p => p.Png).ToArray();
            return true;
        }
        public void Reset() => throw new NotSupportedException();
        public override object InitializeLifetimeService() => null;
    }
#endif
}

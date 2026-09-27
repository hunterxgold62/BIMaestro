using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BIMaestro.Codex
{
    // Revit owns Revit.exe.config and does not redirect the NuGet support assemblies
    // used by PdfPig and Skia. Resolve only version mismatches requested by DLLs
    // installed beside BIMaestro; do not change binding for other add-ins.
    internal static class CodexPdfAssemblyResolver
    {
        private static readonly object Gate = new object();
        private static readonly string BinaryDirectory = Path.GetDirectoryName(typeof(CodexPdfAssemblyResolver).Assembly.Location);
        private static bool registered;
        [ThreadStatic] private static int activePdfOperations;
        private static readonly string[] SupportAssemblies =
        {
            "System.Memory", "System.Buffers", "System.Runtime.CompilerServices.Unsafe",
            "System.Numerics.Vectors", "System.Threading.Tasks.Extensions",
            "Microsoft.Bcl.AsyncInterfaces", "Microsoft.Bcl.HashCode"
        };

        internal static IDisposable EnterPdfOperation()
        {
            lock (Gate)
            {
                if (!registered)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                    registered = true;
                }
            }
            activePdfOperations++;
            return new PdfOperationScope();
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                if (activePdfOperations == 0 || string.IsNullOrWhiteSpace(BinaryDirectory)) return null;
                // The CLR reports RequestingAssembly=null for some PdfPig loads.
                // The thread-local scope still confines this handler to PDF work.
                if (args.RequestingAssembly != null)
                {
                    string requesterDirectory = Path.GetDirectoryName(args.RequestingAssembly.Location);
                    if (!string.Equals(requesterDirectory, BinaryDirectory, StringComparison.OrdinalIgnoreCase)) return null;
                }

                var requested = new AssemblyName(args.Name);
                if (!SupportAssemblies.Contains(requested.Name, StringComparer.OrdinalIgnoreCase)) return null;
                string path = Path.Combine(BinaryDirectory, requested.Name + ".dll");
                if (!File.Exists(path)) return null;
                var available = AssemblyName.GetAssemblyName(path);
                if (!string.Equals(available.Name, requested.Name, StringComparison.OrdinalIgnoreCase) ||
                    !available.GetPublicKeyToken().SequenceEqual(requested.GetPublicKeyToken())) return null;
                return Assembly.LoadFrom(path);
            }
            catch (Exception)
            {
                // Keep the original CLR load error; the MEP diagnostic records it.
                return null;
            }
        }

        private sealed class PdfOperationScope : IDisposable
        {
            public void Dispose() { if (activePdfOperations > 0) activePdfOperations--; }
        }
    }
}

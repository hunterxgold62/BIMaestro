using System;
using System.IO;

namespace BIMaestro.Navisworks.Services
{
    internal static class ReferencePath
    {
        public static string Normalize(string reference, string referringFile)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            try
            {
                Uri uri;
                if (Uri.TryCreate(reference, UriKind.Absolute, out uri) && !uri.IsFile) return null;
                if (uri != null && uri.IsFile) reference = uri.LocalPath;
                if (!Path.IsPathRooted(reference))
                {
                    if (string.IsNullOrEmpty(referringFile) || !Path.IsPathRooted(referringFile)) return null;
                    reference = Path.Combine(Path.GetDirectoryName(referringFile), reference);
                }
                return Path.GetFullPath(reference);
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        public static bool Same(string first, string second)
        {
            return first != null && second != null &&
                string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }
}

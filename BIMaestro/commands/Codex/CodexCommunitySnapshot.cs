using System;
using System.IO;

namespace BIMaestro.Codex
{
    internal static class CodexCommunitySnapshot
    {
        // Called synchronously on the UI thread before the network await: Revit can
        // keep its read/write handle, and the upload owns immutable saved bytes.
        internal static MemoryStream Read(string path, long maxBytes)
        {
            var before = File.GetLastWriteTimeUtc(path);
            byte[] bytes;
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long length = source.Length;
                if (length < 512 || length > maxBytes || length > int.MaxValue)
                    throw new InvalidOperationException("Le RFA doit faire entre 512 octets et 20 Mo.");
                bytes = new byte[(int)length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = source.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new IOException("Le RFA a changé pendant sa lecture. Réessayez après son enregistrement.");
                    offset += count;
                }
                if (source.Length != length || File.GetLastWriteTimeUtc(path) != before)
                    throw new IOException("Le RFA a changé pendant sa lecture. Réessayez après son enregistrement.");
            }
            return new MemoryStream(bytes, false);
        }
    }
}

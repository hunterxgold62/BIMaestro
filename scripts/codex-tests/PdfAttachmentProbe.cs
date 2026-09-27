using System;
using System.IO;
using SkiaSharp;

namespace BIMaestro.Codex
{
    // CodexPdfAttachment only stores this type; the probe does not create images.
    internal sealed class CodexImageAttachment { }

    internal static class PdfAttachmentProbe
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1 || !File.Exists(args[0]))
            {
                Console.Error.WriteLine("Pass one existing PDF path.");
                return 2;
            }

            try
            {
                var attachment = CodexPdfAttachment.FromFile(args[0]);
                Console.WriteLine("Pages: " + attachment.PageCount);
                Console.WriteLine("Extracted characters: " + attachment.Text.Length);
                Console.WriteLine("Input bytes: " + attachment.FileSizeBytes);
                if (attachment.PageCount < 1)
                    throw new InvalidOperationException("No pages found.");

                var images = attachment.RenderPages(new[] { 1 });
                if (images.Length != 1 || images[0].Page != 1 || images[0].Png.Length < 100)
                    throw new InvalidOperationException("First-page PNG is missing.");
                using (var bitmap = SKBitmap.Decode(images[0].Png))
                {
                    if (bitmap == null || bitmap.Width < 1 || bitmap.Height < 1)
                        throw new InvalidOperationException("First-page PNG could not be decoded.");
                    Console.WriteLine("Rendered first page: " + bitmap.Width + "x" + bitmap.Height + " px, " + images[0].Png.Length + " bytes");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}

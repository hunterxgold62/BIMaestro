using System;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Rendering.Skia;

namespace BIMaestro.Codex
{
    internal sealed class CodexPdfAttachment
    {
        internal string Name { get; private set; }
        internal string Text { get; private set; }
        internal string SourcePath { get; private set; }
        internal int PageCount { get; private set; }
        internal long FileSizeBytes { get; private set; }
        internal int[] VisualPages { get; set; } = Array.Empty<int>();
        internal CodexImageAttachment[] PageImages { get; set; } = Array.Empty<CodexImageAttachment>();

        internal static CodexPdfAttachment FromFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 20 * 1024 * 1024)
                throw new InvalidOperationException("PDF introuvable ou trop volumineux (20 Mo maximum).");
            using (var stream = info.OpenRead())
            {
                byte[] header = new byte[5];
                if (stream.Read(header, 0, header.Length) != 5 || Encoding.ASCII.GetString(header) != "%PDF-")
                    throw new InvalidOperationException("Le fichier choisi n'est pas un PDF valide.");
            }
            var text = new StringBuilder();
            bool truncated;
            int pageCount;
            using (var document = PdfDocument.Open(path))
            {
                pageCount = document.NumberOfPages;
                if (pageCount < 1) throw new InvalidOperationException("Le PDF ne contient aucune page.");
                int pages = Math.Min(document.NumberOfPages, 32);
                for (int pageNumber = 1; pageNumber <= pages && text.Length < 30000; pageNumber++)
                {
                    string page = ContentOrderTextExtractor.GetText(document.GetPage(pageNumber));
                    if (string.IsNullOrWhiteSpace(page)) continue;
                    text.Append("\nPage ").Append(pageNumber).Append(" :\n").Append(page);
                }
                truncated = document.NumberOfPages > pages || text.Length > 30000;
            }
            string extracted = text.ToString(0, Math.Min(text.Length, 30000));
            if (truncated) extracted += "\n[Extrait limité aux 32 premières pages et à 30 000 caractères.]";
            return new CodexPdfAttachment { Name = info.Name, SourcePath = info.FullName, PageCount = pageCount,
                FileSizeBytes = info.Length, Text = extracted };
        }

        internal (int Page, byte[] Png)[] RenderPages(int[] pageNumbers)
        {
            if (!File.Exists(SourcePath) || new FileInfo(SourcePath).Length != FileSizeBytes)
                throw new InvalidOperationException("Le PDF a changé depuis son ajout. Joignez-le à nouveau.");
            if (pageNumbers == null || pageNumbers.Length == 0 || pageNumbers.Length > 3 ||
                pageNumbers.Distinct().Count() != pageNumbers.Length || pageNumbers.Any(p => p < 1 || p > PageCount))
                throw new InvalidOperationException("Choisissez une à trois pages valides et distinctes.");
            using (var document = PdfDocument.Open(SourcePath, SkiaRenderingParsingOptions.Instance))
            {
                document.AddSkiaPageFactory();
                return pageNumbers.Select(number =>
                {
                    var page = document.GetPage(number);
                    double edge = Math.Max((double)page.Width, (double)page.Height);
                    if (edge <= 0) throw new InvalidOperationException("Dimensions de page PDF invalides.");
                    float scale = (float)Math.Min(2.0, 1600.0 / edge);
                    using (var bitmap = document.GetPageAsSKBitmap(number, scale, SKColors.White))
                    using (var image = SKImage.FromBitmap(bitmap))
                    using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
                    {
                        if (png.Size > 12 * 1024 * 1024)
                            throw new InvalidOperationException("Page PDF trop détaillée pour être envoyée en image.");
                        return (number, png.ToArray());
                    }
                }).ToArray();
            }
        }
    }
}

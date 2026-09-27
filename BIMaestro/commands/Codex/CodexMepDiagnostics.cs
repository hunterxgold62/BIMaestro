using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BIMaestro.Codex
{
    // Local, bounded diagnostics for one Assistant MEP IA window. Never pass prompts, PDF text,
    // image data, tool arguments, protocol messages or process stderr as event metadata.
    internal sealed class CodexMepDiagnostics
    {
        private const long MaxFileBytes = 2 * 1024 * 1024;
        private const int MaxFilesInDirectory = 60;
        private const int MaxExceptionCharacters = 32000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly object DirectoryGate = new object();
        private readonly object fileGate = new object();

        internal static string DirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BIMaestro", "Codex", "MepAssistant", "diagnostics");

        internal string SessionId { get; }
        internal string FilePath { get; }

        internal CodexMepDiagnostics()
        {
            SessionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            FilePath = Path.Combine(DirectoryPath, "mep-" + SessionId + ".jsonl");
            RecordEvent("session", "started");
        }

        // Returns an identifier that can be shown in the transcript even if disk logging fails.
        internal string RecordError(string stage, Exception error, CodexMepDiagnosticMetadata metadata = null)
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                Write(id, "error", stage, null, metadata, error == null ? "Unknown error" : Redact(error.ToString()));
            }
            catch { /* Logging must never change the result of the failed operation. */ }
            return id;
        }

        internal string RecordEvent(string stage, string outcome, CodexMepDiagnosticMetadata metadata = null)
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            try { Write(id, "event", stage, outcome, metadata, null); }
            catch { /* The assistant continues even if its diagnostic directory is unavailable. */ }
            return id;
        }

        private void Write(string id, string kind, string stage, string outcome,
            CodexMepDiagnosticMetadata metadata, string exception)
        {
            var entry = new Dictionary<string, object>
            {
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["session"] = SessionId,
                ["id"] = id,
                ["kind"] = kind,
                ["stage"] = Label(stage, 100)
            };
            if (outcome != null) entry["outcome"] = Label(outcome, 100);
            var fields = metadata?.SafeFields();
            if (fields != null && fields.Count > 0) entry["metadata"] = fields;
            if (exception != null) entry["exception"] = exception.Length > MaxExceptionCharacters
                ? exception.Substring(0, MaxExceptionCharacters) + "\n[diagnostic truncated]" : exception;
            string line = JsonConvert.SerializeObject(entry, Formatting.None) + Environment.NewLine;

            lock (fileGate)
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + Utf8.GetByteCount(line) > MaxFileBytes)
                    Rotate();
                File.AppendAllText(FilePath, line, Utf8);
            }
            PruneOldFiles();
        }

        private void Rotate()
        {
            string first = FilePath + ".1";
            string second = FilePath + ".2";
            if (File.Exists(second)) File.Delete(second);
            if (File.Exists(first)) File.Move(first, second);
            File.Move(FilePath, first);
        }

        private static void PruneOldFiles()
        {
            lock (DirectoryGate)
            {
                foreach (string path in Directory.EnumerateFiles(DirectoryPath, "mep-*.jsonl*")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Skip(MaxFilesInDirectory))
                {
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static string Label(string value, int maximum)
        {
            string safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length > maximum ? safe.Substring(0, maximum) : safe;
        }

        private static string Redact(string value)
        {
            // Exception messages from third-party tools can contain credentials. Keep their
            // exception type and stack, while masking the common token formats.
            string result = Regex.Replace(value, @"(?i)\b(authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret)\s*[:=]\s*\S+",
                "$1=[redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            result = Regex.Replace(result, @"(?i)\b(sk-[a-z0-9_-]{12,})\b", "[redacted]",
                RegexOptions.None, TimeSpan.FromMilliseconds(100));
            // ClaudeClient wraps process stderr in this prefix; its raw authentication output
            // must remain out of the log, as for CodexClient's deliberately discarded stderr.
            return Regex.Replace(result, @"(?m)(Claude Code : ).*$", "$1[provider detail omitted]",
                RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }

        internal sealed class CodexMepDiagnosticMetadata
        {
            internal string Provider { get; set; }
            internal string Tool { get; set; }
            internal string PdfFileName { get; set; }
            internal long? PdfFileSizeBytes { get; set; }
            internal int? PdfPageCount { get; set; }
            internal int? PdfTextCharacters { get; set; }
            internal int[] PdfVisualPages { get; set; }
            internal int? ImageCount { get; set; }

            internal Dictionary<string, object> SafeFields()
            {
                var fields = new Dictionary<string, object>();
                if (!string.IsNullOrWhiteSpace(Provider)) fields["provider"] = Label(Provider, 40);
                if (!string.IsNullOrWhiteSpace(Tool)) fields["tool"] = Label(Tool, 100);
                if (!string.IsNullOrWhiteSpace(PdfFileName)) fields["pdfFileName"] = Label(Path.GetFileName(PdfFileName), 160);
                if (PdfFileSizeBytes.HasValue) fields["pdfFileSizeBytes"] = PdfFileSizeBytes.Value;
                if (PdfPageCount.HasValue) fields["pdfPageCount"] = PdfPageCount.Value;
                if (PdfTextCharacters.HasValue) fields["pdfTextCharacters"] = PdfTextCharacters.Value;
                if (PdfVisualPages != null) fields["pdfVisualPages"] = PdfVisualPages.Take(3).ToArray();
                if (ImageCount.HasValue) fields["imageCount"] = ImageCount.Value;
                return fields;
            }
        }
    }
}

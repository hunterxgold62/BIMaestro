using Autodesk.Revit.DB;
using BIMaestro.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Analyse
{
    public static class SmartClashReport
    {
        public static string PreviewName(ModelIssue issue)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(issue.IssueKey + "|" + issue.Fingerprint)))
                    .Replace("-", "").ToLowerInvariant() + ".png";
        }
        public static string Html(string document, IEnumerable<ModelIssue> issues, SmartScanSession session, bool stale, string filter)
        {
            var list = issues.ToList(); var b = new StringBuilder();
            b.Append("<!doctype html><html lang='").Append(UiLanguage.CurrentLanguageCode).Append("'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>")
                .Append(UiLanguage.Get("SmartClashReport.Clash3DReport")).Append("</title>");
            b.Append("<style>body{font:15px system-ui,sans-serif;color:#172334;background:#f5f7fa;margin:0}main{max-width:1080px;margin:auto;padding:32px}header{background:#0f5132;color:white;border-radius:14px;padding:24px}h1{margin:0 0 8px}h2{font-size:18px}.card{background:white;border:1px solid #dce3ea;border-radius:12px;padding:20px;margin:16px 0}p{line-height:1.6}.muted{color:#536577}.warning{background:#fff4d6;padding:14px;border-radius:8px}.badge{display:inline-block;background:#edf4f0;padding:5px 9px;border-radius:6px;margin-right:6px}img{max-width:100%;max-height:400px;display:block;margin:16px 0}pre{white-space:pre-wrap;font:inherit}dl{display:grid;grid-template-columns:150px 1fr;gap:8px}dd{margin:0;overflow-wrap:anywhere}dt{color:#536577}@media print{body{background:white}main{padding:0}.card{break-inside:avoid}}@media(max-width:600px){main{padding:16px}dl{grid-template-columns:1fr}}</style><main><header><h1>Clash 3D</h1><div>");
            b.Append(E(document)).Append(" · ").Append(E(DateTime.Now.ToString("dd/MM/yyyy HH:mm"))).Append("</div></header>");
            b.Append("<p>").Append(list.Count).Append(UiLanguage.Get("SmartClashReport.ExportedResultS")).Append(E(filter)).Append("</p>");
            if (stale || session == null || session.Cancelled || session.Error != null)
                b.Append("<p class='warning'>").Append(UiLanguage.Get("SmartClashReport.PartialOrOutdatedResultsThisAnalysisDoesNot")).Append("</p>");
            if (session != null)
            {
                b.Append("<div class='card'><h2>").Append(UiLanguage.Get("SmartClashReport.ScopeAndMethod")).Append("</h2><p>").Append(E(session.Options.Scope.ToString()))
                    .Append(" · ").Append(session.SourceCount).Append(UiLanguage.Get("SmartClashReport.SourceElementS")).Append(session.TestedPairs)
                    .Append(UiLanguage.Get("SmartClashReport.PairSChecked")).Append(session.Seconds.ToString("F1", CultureInfo.InvariantCulture)).Append(" s</p><p>")
                    .Append(UiLanguage.Get("SmartClashReport.PhysicalIntersectionsThreshold")).Append(session.Options.MinimumVolumeMm3.ToString("G", CultureInfo.InvariantCulture))
                    .Append(UiLanguage.Get("SmartClashReport.MmInsulationAndLining")).Append(session.Options.IncludeInsulation ? UiLanguage.Get("SmartClashReport.Included") : UiLanguage.Get("SmartClashReport.Excluded"))
                    .Append(UiLanguage.Get("SmartClashReport.BoundingVolumesAreReportedAsSuspectedClashesDirect")).Append("</p>");
                if (session.Diagnostics.Count > 0) b.Append("<h2>").Append(UiLanguage.Get("SmartClashReport.AnalysisLimitations")).Append("</h2><pre>").Append(E(string.Join("\n", session.Diagnostics))).Append("</pre>");
                if (session.Error != null) b.Append("<p class='warning'>").Append(E(session.Error)).Append("</p>");
                b.Append("</div>");
            }
            foreach (var issue in list)
            {
                b.Append("<article class='card'><h2>").Append(E(issue.PairTitle)).Append("</h2><p class='muted'>").Append(E(issue.ContextText))
                    .Append("</p><span class='badge'>").Append(E(issue.ConfidenceText)).Append("</span><span class='badge'>").Append(E(issue.StatusText)).Append("</span>");
                if (!string.IsNullOrWhiteSpace(issue.ThumbnailPath) && File.Exists(issue.ThumbnailPath))
                    b.Append("<img alt='").Append(E(UiLanguage.Get("SmartClashReport.ClashArea"))).Append("' src='data:image/png;base64,").Append(Convert.ToBase64String(File.ReadAllBytes(issue.ThumbnailPath))).Append("'>");
                else if (issue.VisualScene?.HasGeometry == true)
                    b.Append("<div style='max-width:480px'>").Append(SmartVisualProjection.Svg(issue.VisualScene)).Append("</div><p class='muted'>").Append(UiLanguage.Get("SmartClashReport.LocalViewOfTheActualShapesMarkerCenter")).Append("</p>");
                if (!string.IsNullOrWhiteSpace(issue.VisualScene?.Notice)) b.Append("<p class='warning'>").Append(E(issue.VisualScene.Notice)).Append("</p>");
                b.Append("<p>").Append(E(issue.Message)).Append("</p><p>").Append(E(issue.AdviceText)).Append("</p><dl><dt>").Append(UiLanguage.Get("SmartClashReport.Element")).Append("</dt><dd>")
                    .Append(E(issue.ElementTypeName)).Append("</dd><dt>").Append(UiLanguage.Get("SmartClashReport.Obstacle")).Append("</dt><dd>").Append(E(issue.RelatedTypeName))
                    .Append("</dd><dt>").Append(UiLanguage.Get("SmartClashReport.LinkedObstacleID")).Append("</dt><dd>").Append(issue.LinkedElementId.GetIdLongValue())
                    .Append("</dd><dt>").Append(UiLanguage.Get("SmartClashReport.ReferenceVolume")).Append("</dt><dd>").Append(issue.IntersectionVolumeMm3.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" mm³</dd><dt>").Append(UiLanguage.Get("SmartClashReport.Decision")).Append("</dt><dd>").Append(E(issue.StatusUpdatedText)).Append("</dd></dl><pre>")
                    .Append(E(issue.StatusComment)).Append("</pre></article>");
            }
            b.Append("<p class='muted'>").Append(UiLanguage.Get("SmartClashReport.ResolvedAndIgnoreAreManualDecisionsANew")).Append("</p></main></html>");
            return b.ToString();
        }
        private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
        public static string TutorialHtml(string initialHtml, string finalHtml, string reservationId)
        {
            const string headerEnd = "</header>";
            int start = initialHtml.IndexOf(headerEnd, StringComparison.Ordinal) + headerEnd.Length;
            int end = initialHtml.LastIndexOf("</main>", StringComparison.Ordinal);
            if (start < headerEnd.Length || end < start) throw new InvalidOperationException(UiLanguage.Get("SmartClashReport.TheInitialTutorialReportIsIncomplete"));
            var comparison = "<section class='card'><h2>" + UiLanguage.Get("SmartClashReport.GuidedTourSummaryBeforeAfter") + "</h2>" +
                "<p>" + UiLanguage.Get("SmartClashReport.TwoIntersectionsWereConfirmedInitiallyThePipeCrossing") +
                UiLanguage.Get("SmartClashReport.TheCrossingPipeWasRaisedBy300Mm") + E(reservationId) +
                UiLanguage.Get("SmartClashReport.WasCreatedInTheWallANewAnalysis") + "</p>" +
                "<p class='muted'>" + UiLanguage.Get("SmartClashReport.TheInitialPreviewsAreKeptBelowCheckThe") + "</p></section>" +
                "<section><h2>" + UiLanguage.Get("SmartClashReport.BeforeCorrectionInitialStateKept") + "</h2>" + initialHtml.Substring(start, end - start) +
                "</section><section class='card'><h2>" + UiLanguage.Get("SmartClashReport.AfterCorrectionLatestAnalysis") + "</h2>" +
                "<p>" + UiLanguage.Get("SmartClashReport.TheResultsBelowDescribeTheScopeAnalyzedAfter") +
                UiLanguage.Get("SmartClashReport.TheAbsenceOfRemainingIntersectionsInThisScope") + "</p></section>";
            int insertion = finalHtml.IndexOf(headerEnd, StringComparison.Ordinal);
            if (insertion < 0) throw new InvalidOperationException(UiLanguage.Get("SmartClashReport.TheFinalTutorialReportIsIncomplete"));
            return finalHtml.Insert(insertion + headerEnd.Length, comparison);
        }

        internal static string CsvPhase(IEnumerable<ModelIssue> issues, string phase)
        {
            string header = Csv(Array.Empty<ModelIssue>());
            var b = new StringBuilder("Phase;" + header);
            foreach (var issue in issues)
                b.Append(CsvCell(phase)).Append(';').Append(Csv(new[] { issue }).Substring(header.Length));
            return b.ToString();
        }

        public static string TutorialCsv(string initialCsv, IEnumerable<ModelIssue> finalIssues)
        {
            var list = finalIssues.ToList();
            string final = CsvPhase(list, UiLanguage.Get("SmartClashReport.AfterCorrection"));
            int headerEnd = final.IndexOf("\r\n", StringComparison.Ordinal) + 2;
            if (list.Count == 0)
                final += CsvCell(UiLanguage.Get("SmartClashReport.AfterCorrectionNoIntersectionDetectedInTheAnalyzed")) +
                    string.Concat(Enumerable.Repeat(";\"\"", 16)) + "\r\n";
            return initialCsv + final.Substring(headerEnd);
        }

        public static string Csv(IEnumerable<ModelIssue> issues)
        {
            var b = new StringBuilder(UiLanguage.Get("SmartClashReport.ElementObstacleLevelLinkCheckConfidenceStatusElement") + "\r\n");
            foreach (var i in issues)
                b.AppendLine(string.Join(";", new[] { i.ElementLabel, i.ObstacleLabel, i.LevelName, i.LinkName, i.Category,
                    i.ConfidenceText, i.StatusText, i.ElementTypeName, i.RelatedTypeName, i.ElementIdValue.ToString(), i.RelatedId.GetIdLongValue().ToString(),
                    i.LinkedElementId.GetIdLongValue().ToString(), i.IntersectionVolumeMm3.ToString("R", CultureInfo.InvariantCulture), i.StatusComment,
                    i.StatusUser, i.StatusUpdatedUtc?.ToString("O") }.Select(CsvCell)));
            return b.ToString();
        }
        private static string CsvCell(string value)
        {
            value = value ?? "";
            // Spreadsheet formula injection protection, including leading whitespace.
            var trimmed = value.TrimStart();
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}

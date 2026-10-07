using Autodesk.Revit.DB;
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
            b.Append("<!doctype html><html lang='fr'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Clash 3D · Rapport</title>");
            b.Append("<style>body{font:15px system-ui,sans-serif;color:#172334;background:#f5f7fa;margin:0}main{max-width:1080px;margin:auto;padding:32px}header{background:#0f5132;color:white;border-radius:14px;padding:24px}h1{margin:0 0 8px}h2{font-size:18px}.card{background:white;border:1px solid #dce3ea;border-radius:12px;padding:20px;margin:16px 0}p{line-height:1.6}.muted{color:#536577}.warning{background:#fff4d6;padding:14px;border-radius:8px}.badge{display:inline-block;background:#edf4f0;padding:5px 9px;border-radius:6px;margin-right:6px}img{max-width:100%;max-height:400px;display:block;margin:16px 0}pre{white-space:pre-wrap;font:inherit}dl{display:grid;grid-template-columns:150px 1fr;gap:8px}dd{margin:0;overflow-wrap:anywhere}dt{color:#536577}@media print{body{background:white}main{padding:0}.card{break-inside:avoid}}@media(max-width:600px){main{padding:16px}dl{grid-template-columns:1fr}}</style><main><header><h1>Clash 3D</h1><div>");
            b.Append(E(document)).Append(" · ").Append(E(DateTime.Now.ToString("dd/MM/yyyy HH:mm"))).Append("</div></header>");
            b.Append("<p>").Append(list.Count).Append(" résultat(s) exporté(s) · ").Append(E(filter)).Append("</p>");
            if (stale || session == null || session.Cancelled || session.Error != null)
                b.Append("<p class='warning'>Résultats partiels ou anciens : cette analyse ne permet pas de conclure à l'absence de conflits.</p>");
            if (session != null)
            {
                b.Append("<div class='card'><h2>Périmètre et méthode</h2><p>").Append(E(session.Options.Scope.ToString()))
                    .Append(" · ").Append(session.SourceCount).Append(" objet(s) de départ · ").Append(session.TestedPairs)
                    .Append(" paire(s) examinée(s) · ").Append(session.Seconds.ToString("F1", CultureInfo.InvariantCulture)).Append(" s</p><p>")
                    .Append("Intersections physiques ; seuil : ").Append(session.Options.MinimumVolumeMm3.ToString("G", CultureInfo.InvariantCulture))
                    .Append(" mm³. Isolants et revêtements : ").Append(session.Options.IncludeInsulation ? "inclus" : "exclus")
                    .Append(". Les volumes englobants sont signalés comme suspicions. Les connexions directes entre réseaux sont exclues.</p>");
                if (session.Diagnostics.Count > 0) b.Append("<h2>Limites de l'analyse</h2><pre>").Append(E(string.Join("\n", session.Diagnostics))).Append("</pre>");
                if (session.Error != null) b.Append("<p class='warning'>").Append(E(session.Error)).Append("</p>");
                b.Append("</div>");
            }
            foreach (var issue in list)
            {
                b.Append("<article class='card'><h2>").Append(E(issue.PairTitle)).Append("</h2><p class='muted'>").Append(E(issue.ContextText))
                    .Append("</p><span class='badge'>").Append(E(issue.ConfidenceText)).Append("</span><span class='badge'>").Append(E(issue.StatusText)).Append("</span>");
                if (!string.IsNullOrWhiteSpace(issue.ThumbnailPath) && File.Exists(issue.ThumbnailPath))
                    b.Append("<img alt='Zone du conflit' src='data:image/png;base64,").Append(Convert.ToBase64String(File.ReadAllBytes(issue.ThumbnailPath))).Append("'>");
                else if (issue.VisualScene?.HasGeometry == true)
                    b.Append("<div style='max-width:480px'>").Append(SmartVisualProjection.Svg(issue.VisualScene)).Append("</div><p class='muted'>Vue locale des formes réelles ; repère : centre de la zone détectée.</p>");
                if (!string.IsNullOrWhiteSpace(issue.VisualScene?.Notice)) b.Append("<p class='warning'>").Append(E(issue.VisualScene.Notice)).Append("</p>");
                b.Append("<p>").Append(E(issue.Message)).Append("</p><p>").Append(E(issue.AdviceText)).Append("</p><dl><dt>Objet</dt><dd>")
                    .Append(E(issue.ElementTypeName)).Append("</dd><dt>Obstacle</dt><dd>").Append(E(issue.RelatedTypeName))
                    .Append("</dd><dt>Id obstacle lié</dt><dd>").Append(issue.LinkedElementId.GetIdLongValue())
                    .Append("</dd><dt>Volume témoin</dt><dd>").Append(issue.IntersectionVolumeMm3.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" mm³</dd><dt>Décision</dt><dd>").Append(E(issue.StatusUpdatedText)).Append("</dd></dl><pre>")
                    .Append(E(issue.StatusComment)).Append("</pre></article>");
            }
            b.Append("<p class='muted'>« Traité » et « À ignorer » sont des décisions manuelles. Une nouvelle analyse vérifie la présence des intersections dans le périmètre choisi.</p></main></html>");
            return b.ToString();
        }
        private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
        public static string Csv(IEnumerable<ModelIssue> issues)
        {
            var b = new StringBuilder("Objet;Obstacle;Niveau;Lien;Contrôle;Certitude;Statut;Type objet;Type obstacle;Id objet;Id obstacle;Id obstacle lié;Volume témoin mm3;Commentaire;Utilisateur;Date UTC\r\n");
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

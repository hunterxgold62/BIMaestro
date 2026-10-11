// Exercises the production serializer and renderer without launching Revit.
// These DTOs replace only the Revit-dependent inputs of the report.
using Analyse;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Autodesk.Revit.DB { public sealed class ElementId { public long Value; public long GetIdLongValue() => Value; } }
namespace BIMaestro.Localization { internal static class UiLanguage { public static string T(string text) => text; } }
namespace Analyse
{
    public sealed class ModelIssue
    {
        public string IssueKey, Fingerprint, PairTitle, ContextText, ConfidenceText, StatusText, ThumbnailPath, Message, AdviceText,
            ElementTypeName, RelatedTypeName, StatusUpdatedText, StatusComment, ElementLabel, ObstacleLabel, LevelName, LinkName,
            Category, StatusUser;
        public long ElementIdValue;
        public double IntersectionVolumeMm3;
        public DateTime? StatusUpdatedUtc;
        public Autodesk.Revit.DB.ElementId RelatedId = new(), LinkedElementId = new();
        public SmartVisualScene VisualScene;
    }
    public sealed class SmartScanSession
    {
        public bool Cancelled; public string Error;
        public int SourceCount = 3, TestedPairs = 2;
        public double Seconds = 1;
        public List<string> Diagnostics = new();
        public Options Options = new();
    }
    public sealed class Options { public string Scope = "Sélection"; public double MinimumVolumeMm3 = 10; public bool IncludeInsulation; }
}

internal static class ReportProbe
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string folder = args.Length > 0 ? args[0] : "tmp/clash-report-probe";
            Directory.CreateDirectory(folder);
            var first = new ModelIssue { PairTitle = "Tuyau A ↔ Tuyau B", ElementLabel = "Tuyau A", ObstacleLabel = "Tuyau B",
                ConfidenceText = "Intersection confirmée", StatusText = "Actif", IntersectionVolumeMm3 = 100,
                StatusComment = "  =HYPERLINK(\"url\")\nDeuxième ligne; commentaire" };
            var wall = new ModelIssue { PairTitle = "Tuyau C ↔ Mur", ElementLabel = "Tuyau C", ObstacleLabel = "Mur",
                ConfidenceText = "Intersection confirmée", StatusText = "Actif", IntersectionVolumeMm3 = 200 };
            var triangles = new List<SmartVisualTriangle> { new(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)) };
            var mesh = new SmartVisualMesh(triangles);
            first.VisualScene = new SmartVisualScene(mesh, mesh, new(0, 0, 0), 1, false, null);
            wall.VisualScene = first.VisualScene;
            var session = new SmartScanSession();
            string initialHtml = SmartClashReport.Html("Maquette <test>", new[] { first, wall }, session, false, "Tous");
            string initialCsv = SmartClashReport.CsvPhase(new[] { first, wall }, "Avant correction");
            first.IntersectionVolumeMm3 = 999; first.StatusComment = "Modifié après le scan";
            string finalHtml = SmartClashReport.Html("Maquette <test>", Array.Empty<ModelIssue>(), session, false, "Tous");
            string html = SmartClashReport.TutorialHtml(initialHtml, finalHtml, "1234");
            Check(html.Contains("Tuyau A") && html.Contains("Tuyau C") && html.Contains("100.0 mm³") && !html.Contains("999.0 mm³"), "Initial snapshot was lost or mutated.");
            Check(html.Contains("0 résultat(s)") && html.Contains("300 mm") && html.Contains("#1234") && html.Contains("Maquette &lt;test&gt;"), "Final summary or HTML escaping failed.");
            Check(Count(html, "<article ") == 2 && Count(html, "<svg") == 2 && Count(html, "<!doctype") == 1, "Initial previews or document composition failed.");
            File.WriteAllText(Path.Combine(folder, "tutorial-zero-final.html"), html);
            string csv = SmartClashReport.TutorialCsv(initialCsv, Array.Empty<ModelIssue>());
            var rows = ParseCsv(csv);
            Check(rows.Count == 4 && rows.All(r => r.Count == 17), "Zero-final CSV has wrong row or column count.");
            Check(rows[1][0] == "Avant correction" && rows[3][0].Contains("aucune intersection"), "CSV phases or zero-result summary missing.");
            Check(rows[1][14].StartsWith("'  =HYPERLINK") && rows[1][14].Contains("\nDeuxième ligne;"), "CSV formula protection or multiline escaping failed.");
            File.WriteAllText(Path.Combine(folder, "tutorial-zero-final.csv"), csv, new UTF8Encoding(true));
            var remaining = ParseCsv(SmartClashReport.TutorialCsv(initialCsv, new[] { wall }));
            Check(remaining.Count == 4 && remaining[3][0] == "Après correction" && remaining[3][2] == "Mur", "Remaining wall result was lost.");
            Check(!SmartClashReport.Csv(new[] { wall }).StartsWith("Phase;") && !finalHtml.Contains("Avant / après"), "Ordinary exports changed.");
            bool refused = false; try { SmartClashReport.TutorialHtml("missing snapshot", finalHtml, "1234"); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Malformed snapshot was accepted.");
            Console.WriteLine("PASS: immutable initial state, two SVG previews, zero and one final result, CSV phases/columns/multiline/formula protection, ordinary export preservation, malformed snapshot guard.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static int Count(string text, string token) => (text.Length - text.Replace(token, "").Length) / token.Length;
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && c == ';') { row.Add(cell.ToString()); cell.Clear(); }
            else if (!quoted && c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            { row.Add(cell.ToString()); rows.Add(row); row = new(); cell.Clear(); i++; }
            else cell.Append(c);
        }
        Check(!quoted && row.Count == 0 && cell.Length == 0, "Incomplete CSV record."); return rows;
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Analyse;

internal static class Program
{
    private static int Main(string[] args)
    {
        int checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
        HistoryOpeningClassification Evidence(string name, long category = 0) => new HistoryOpeningClassification {
            SourceElementId = 42, SourceUniqueId = "test-source-identity", Class = "Autodesk.Revit.DB." + name, CategoryId = category
        };
        Check(Evidence("Plumbing.PipeInsulation").IsExcluded(), "pipe insulation excluded by native class");
        Check(Evidence("Mechanical.DuctInsulation").IsExcluded(), "duct insulation excluded by native class");
        Check(Evidence("IndependentTag").IsExcluded(), "tags excluded");
        Check(Evidence("LinearDimension").IsExcluded(), "dimensions excluded");
        Check(Evidence("Element", -2008045).IsExcluded(), "pipe centerlines excluded");
        Check(Evidence("Element", -2008072).IsExcluded(), "fitting centerlines excluded");
        Check(Evidence("ExtensibleStorage.DataStorage").IsExcluded(), "storage dependencies excluded");
        Check(Evidence("Element").IsExcluded(), "internal non-geometric dependency excluded");
        var geometric = Evidence("Element"); geometric.HasBoundingBox = true;
        Check(!geometric.IsExcluded(), "unknown geometric element remains visible");
        Check(!Evidence("Plumbing.Pipe", -2008044).IsExcluded(), "ordinary pipes remain visible");
        var legacyWall = Evidence("Wall", -2000011);
        Check(legacyWall.IsDocumentElement == null && !legacyWall.IsExcluded(), "legacy wall with unknown document membership remains physical");
        var collectedWall = Evidence("Wall", -2000011); collectedWall.IsDocumentElement = true;
        Check(!collectedWall.IsExcluded(), "verified document wall remains physical");
        var internalWall = Evidence("Wall", -2000011); internalWall.IsDocumentElement = false;
        Check(internalWall.IsExcluded(), "resolved wall absent from verified document model collection is excluded");
        var restoredWall = JsonSerializer.Deserialize<HistoryOpeningClassification>(JsonSerializer.Serialize(internalWall));
        Check(restoredWall.IsDocumentElement == false && restoredWall.IsExcluded(), "document membership evidence survives serialization");
        Check(!Evidence("FamilyInstance", -2008049).IsExcluded(), "fittings remain visible");
        Check(!Evidence("FamilyInstance", -2008045).IsExcluded(), "an instance is never hidden by an axis label/category alone");
        Check(Evidence("Plumbing.PipingSystem").IsExcluded(), "logical systems are restored with their physical network, not as standalone objects");
        Check(Evidence("FamilySymbol", -2008049).IsExcluded(), "type definitions are not counted as physical objects");
        Check(Evidence("ModelArc", -2000045).IsExcluded(), "sketch curves are copied with their owner");
        Check(Evidence("RadialDimension", -2000261).IsExcluded(), "automatic sketch dimensions are excluded");
        var invalid = Evidence("Element", -2008045); invalid.SourceUniqueId = null;
        Check(!invalid.IsExcluded(), "missing source identity cannot hide a record");
        internalWall.SourceUniqueId = null;
        Check(!internalWall.IsExcluded(), "unverified identity cannot be excluded by document membership alone");
        if (args.Length > 0)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(args[0]));
            var rows = json.RootElement.EnumerateArray().ToList();
            int physical = 0, excluded = 0, residual = 0;
            foreach (var row in rows)
            {
                var classification = row.GetProperty("Classification").Deserialize<HistoryOpeningClassification>();
                Check(classification?.IsValid() == true, "verified opening identity " + row.GetProperty("Id").GetInt32());
                if (classification.IsExcluded()) excluded++;
                else if (row.GetProperty("Ready").GetBoolean()) physical++;
                else residual++;
            }
            Console.WriteLine($"Replay counts before assertions: ready={physical}, excluded={excluded}, non-direct={residual}, total={rows.Count}");
            if (rows.Count == 1291)
            {
                Check(physical == 536, "all 211 pipes and 325 fittings/accessories remain visible and ready");
                Check(excluded == 755, "insulation, non-model dependencies and generated logical system excluded");
                Check(residual == 0, "generated logical system is not a fake physical reconstruction failure");
            }
            else if (rows.Count == 817)
            {
                Check(physical + residual == 316, "mixed opening contains exactly 316 physical objects, including its terrain");
                Check(excluded == 501, "mixed opening excludes 501 type, sketch and annotation dependencies");
                Check(residual == 30, "30 source objects without a direct capture remain physical and require native copy");
            }
            Console.WriteLine($"Real opening replay: {physical} ready physical elements; {excluded} excluded dependencies; {residual} explicit remaining failure.");
        }
        Console.WriteLine(checks + " assertions passed."); return 0;
    }
}

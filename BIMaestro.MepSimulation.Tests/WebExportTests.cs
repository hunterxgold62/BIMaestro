using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Newtonsoft.Json.Linq;

namespace BIMaestro.VideoGames
{
    internal static class WebExportTests
    {
        public static void Run()
        {
            var scene = new GameSceneData();
            scene.MepGraph.Elements.Add(new GameMepElementData { Key = "pipe" });
            scene.MepGraph.Connectors.Add(new GameMepConnectorData { ElementKey = "pipe" });
            var mesh = new GameMeshData { HasCompleteNormals = false };
            // Two distant triangles must become separate spatial zones.
            foreach (int x in new[] { 0, 100 })
                foreach (var point in new[] { new Point3D(x, 0, 0), new Point3D(x + 1, 0, 0), new Point3D(x, 1, 0) })
                {
                    mesh.Indices.Add(mesh.Positions.Count); mesh.Positions.Add(point);
                    mesh.VertexColors.Add(Colors.White); mesh.ElementIndices.Add(7);
                }
            scene.Meshes.Add(mesh);
            GameMepWebPackage.PrepareStaticAssets(scene);
            if (scene.WebTiles.Count != 2 || scene.WebModelGlb.Length != 0) throw new Exception("Spatial partition failed");
            foreach (var tile in scene.WebTiles)
            {
                using var input = new MemoryStream(tile.Bytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(); gzip.CopyTo(output);
                var bytes = output.ToArray();
                if (bytes.Length != tile.DecodedBytes || BitConverter.ToUInt32(bytes, 0) != 0x46546C67 || !tile.Elements.SequenceEqual(new[] { 7 })) throw new Exception("Invalid GLB zone");
                using var hash = SHA256.Create();
                if (string.Concat(hash.ComputeHash(tile.Bytes).Select(b => b.ToString("x2"))) != tile.Sha256) throw new Exception("Invalid asset hash");
            }
            var package = GameMepWebPackage.Build(scene, "Test");
            var manifest = JObject.Parse(package.ManifestJson);
            if ((int)manifest["schemaVersion"] != 2 || manifest["tiles"][0]["bytes"] != null || (long)manifest["tiles"][0]["size"] <= 0) throw new Exception("Invalid manifest");
            using var archive = new ZipArchive(new MemoryStream(package.Bytes), ZipArchiveMode.Read);
            if (archive.GetEntry("model.glb") != null || archive.GetEntry("mep.json") == null) throw new Exception("Invalid metadata package");
            if (archive.GetEntry("overview.glb") == null || manifest["files"]["overview.glb"] == null) throw new Exception("Missing whole-model overview");
            var overviewJson = JObject.Parse(System.Text.Encoding.UTF8.GetString(scene.WebOverviewGlb, 20, BitConverter.ToInt32(scene.WebOverviewGlb, 12)));
            var overviewTiles = overviewJson["nodes"].Select(node => (string)node["extras"]["lodTileName"]).Distinct().ToArray();
            if (!overviewTiles.OrderBy(value => value).SequenceEqual(scene.WebTiles.Select(tile => tile.Name).OrderBy(value => value))) throw new Exception("Overview does not cover every tile");
            var grouped = new GameSceneData();
            for (int i = 0; i < 40; i++)
            {
                var small = new GameMeshData { HasCompleteNormals = false };
                foreach (var point in new[] { new Point3D(1, 1, i * .01), new Point3D(2, 1, i * .01), new Point3D(1, 2, i * .01) })
                { small.Indices.Add(small.Positions.Count); small.Positions.Add(point); small.ElementIndices.Add(i); small.VertexColors.Add(Colors.White); }
                grouped.Meshes.Add(small);
            }
            GameMepWebPackage.PrepareStaticAssets(grouped);
            if (grouped.WebTiles.Count != 1 || grouped.WebTiles[0].Elements.Length != 40) throw new Exception("Small spatial fragments were not grouped");
            var dense = new GameMeshData { HasCompleteNormals = false };
            for (int x = 0; x < 40; x++) for (int y = 0; y < 40; y++)
            foreach (var point in new[] { new Point3D(x, y, 0), new Point3D(x + 1, y, 0), new Point3D(x, y + 1, 0), new Point3D(x + 1, y, 0), new Point3D(x + 1, y + 1, 0), new Point3D(x, y + 1, 0) })
            { dense.Indices.Add(dense.Positions.Count); dense.Positions.Add(point); dense.ElementIndices.Add(1); dense.VertexColors.Add(Colors.White); }
            var simplified = GameMepWebPackage.SimplifyOverview(dense, 4);
            if (simplified.Indices.Count >= dense.Indices.Count / 2 || simplified.Indices.Count == 0 || simplified.Positions.Max(point => point.X) != 40 || simplified.Positions.Min(point => point.X) != 0) throw new Exception("Overview simplification changed model extent or did not reduce triangles");
            double area = 0;
            for (int i = 0; i < simplified.Indices.Count; i += 3)
            {
                var a = simplified.Positions[simplified.Indices[i]];
                var b = simplified.Positions[simplified.Indices[i + 1]];
                var c = simplified.Positions[simplified.Indices[i + 2]];
                area += Vector3D.CrossProduct(b - a, c - a).Length / 2;
            }
            if (Math.Abs(area - 1600) > .00001) throw new Exception("Simplified planar surface contains gaps or overlaps");
            string fixture = Environment.GetEnvironmentVariable("BIMAESTRO_LOD_FIXTURE");
            var detailScene = new GameSceneData();
            detailScene.Elements.Add(new GameElementData { WebElementIndex = 1, Category = "Portes" });
            detailScene.Elements.Add(new GameElementData { WebElementIndex = 2, Category = "Murs" });
            foreach (int element in new[] { 1, 2 })
            {
                var detail = new GameMeshData { HasCompleteNormals = false };
                foreach (var point in dense.Positions)
                {
                    detail.Positions.Add(new Point3D(point.X * .005, point.Y * .005, element));
                    detail.VertexColors.Add(Colors.White); detail.ElementIndices.Add(element);
                }
                foreach (int index in dense.Indices) detail.Indices.Add(index);
                detailScene.Meshes.Add(detail);
            }
            GameMepWebPackage.PrepareStaticAssets(detailScene);
            var originalTile = detailScene.WebTiles[0];
            detailScene.Meshes.Clear(); // Matches the native GPU scene lifecycle.
            var analysis = GameMepWebPackage.AnalyzeExport(detailScene, System.Threading.CancellationToken.None);
            if (analysis.Triangles[1] != 3200 || analysis.Triangles[2] != 3200 || analysis.EstimatedBytes[1] <= 0)
                throw new Exception("Export diagnostic failed after source geometry release");
            if (!ReferenceEquals(originalTile, detailScene.WebTiles[0]) || analysis.LighterScene.WebTiles.Sum(t => t.Size) >= detailScene.WebTiles.Sum(t => t.Size))
                throw new Exception("Detail optimization mutated the original or saved no bytes");
            var after = GameMepWebPackage.AnalyzeExport(analysis.LighterScene, System.Threading.CancellationToken.None);
            if (after.Triangles[2] != 3200 || after.Triangles[1] >= 3200 || after.Triangles[1] == 0)
                throw new Exception("Detail optimization changed walls or lost the door");
            var cancelled = new System.Threading.CancellationToken(true);
            try { GameMepWebPackage.AnalyzeExport(detailScene, cancelled); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Console.WriteLine("Heavy element diagnostic, measured savings, protected walls and cancellation passed.");
            if (!string.IsNullOrWhiteSpace(fixture)) File.WriteAllBytes(fixture, package.Bytes);
            Console.WriteLine("Global overview, grouping and simplification passed.");
            Console.WriteLine("Progressive web export: spatial zones, GLB, hashes and metadata passed.");
        }
    }
}

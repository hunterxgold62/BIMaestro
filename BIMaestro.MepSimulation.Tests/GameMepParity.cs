using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BIMaestro.VideoGames
{
    internal static class GameMepParity
    {
        public static string OutputPath;
        public static bool IncludeLargeGraphs;
        public static void ExportUiFixture(GameMepGraphData graph)
        {
            if (string.IsNullOrEmpty(OutputPath)) return;
            var scene = new GameSceneData { MepGraph = graph };
            int index = 0;
            foreach (var element in graph.Elements)
            {
                scene.Elements.Add(new GameElementData { Key = element.Key, Name = element.Name, ElementId = element.ElementId, WebElementIndex = index });
                var mesh = new GameMeshData();
                foreach (var point in new[] { new System.Windows.Media.Media3D.Point3D(index * 2, 0, 0), new System.Windows.Media.Media3D.Point3D(index * 2 + 1, 0, 0), new System.Windows.Media.Media3D.Point3D(index * 2, 0, 1) })
                { mesh.Indices.Add(mesh.Positions.Count); mesh.Positions.Add(point); mesh.ElementIndices.Add(index); mesh.VertexColors.Add(System.Windows.Media.Colors.LightBlue); }
                scene.Meshes.Add(mesh); index++;
            }
            GameMepWebPackage.PrepareStaticAssets(scene);
            var package = GameMepWebPackage.Build(scene, "Validation jalons MEP");
            var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(OutputPath)), "mep-ui-fixture");
            Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, "index.zip"), package.Bytes);
            foreach (var tile in scene.WebTiles) File.WriteAllBytes(Path.Combine(directory, tile.Name), tile.Bytes);
        }
        private static readonly List<object> Cases = new List<object>();
        public static void Record(GameMepGraphData graph)
        {
            if (string.IsNullOrEmpty(OutputPath) || (!IncludeLargeGraphs && graph.Elements.Count > 80) || graph.Elements.Count == 0) return;
            var caller = new StackTrace().GetFrames()?.Select(f => f.GetMethod()).FirstOrDefault(m => m.DeclaringType == typeof(Program));
            var request = new JObject { ["graph"] = JObject.FromObject(graph, JsonSerializer.Create(GameMepCalculation.JsonSettings)) };
            var expected = JObject.FromObject(Summary(graph), JsonSerializer.Create(GameMepCalculation.JsonSettings));
            Cases.Add(new { name = (caller?.Name ?? "graph") + "-" + Cases.Count, request, expected });
        }
        public static object Summary(GameMepGraphData graph) => new {
            states = graph.Elements.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new { e.Key, e.FlowState, e.ConnectedToInlet, e.ConnectedToReturn,
                paths = e.Paths.Select(p => new { p.FlowState, p.HasCirculation, p.FlowForward, p.DirectionState, p.DirectionReason, p.ConnectedToInlet, p.ConnectedToReturn }) }),
            valves = graph.Valves.OrderBy(v => v.ElementKey, StringComparer.Ordinal).Select(v => new { v.ElementKey, v.IsClosed, v.UpstreamState, v.DownstreamState }),
            report = graph.ImpactReport, reportText = GameMepImpactAnalyzer.ToText(graph)
        };
        public static void Save()
        {
            if (string.IsNullOrEmpty(OutputPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputPath)));
            File.WriteAllText(OutputPath, JsonConvert.SerializeObject(Cases, GameMepCalculation.JsonSettings));
            Console.WriteLine("Shared browser parity corpus: " + Cases.Count + " cases.");
        }
    }
}

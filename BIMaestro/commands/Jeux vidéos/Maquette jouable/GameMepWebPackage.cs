using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Linq;
using System.Threading;

namespace BIMaestro.VideoGames
{
    internal sealed class GameMepWebPackageResult
    {
        public IList<GameMepWebAsset> Assets { get; set; } = new List<GameMepWebAsset>();
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public string Sha256 { get; set; } = string.Empty;
        public string ManifestJson { get; set; } = "{}";
        public IList<string> ValveIds { get; set; } = new List<string>();
    }

    internal static class GameMepWebPackage
    {
        internal sealed class ExportAnalysis
        {
            public GameSceneData LighterScene = null!;
            public Dictionary<int, long> EstimatedBytes = new Dictionary<int, long>();
            public Dictionary<int, long> Triangles = new Dictionary<int, long>();
        }

        // Read one of our own GLB tiles at a time: native rendering has already
        // released its source meshes. No Revit API or native viewport is modified.
        internal static ExportAnalysis AnalyzeExport(GameSceneData scene, CancellationToken cancellation)
        {
            var result = new ExportAnalysis { LighterScene = scene.CopyForWebExport() };
            long removedTriangles = 0;
            var eligible = new HashSet<int>(scene.Elements.Where(e =>
                new[] { "portes", "doors", "garde-corps", "garde corps", "railings" }
                    .Contains(e.Category.Trim().ToLowerInvariant())).Select(e => e.WebElementIndex));
            for (int tileIndex = 0; tileIndex < scene.WebTiles.Count; tileIndex++)
            {
                cancellation.ThrowIfCancellationRequested();
                var tile = scene.WebTiles[tileIndex];
                byte[] bytes;
                using (var input = new MemoryStream(tile.Bytes))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream()) { gzip.CopyTo(output); bytes = output.ToArray(); }
                int jsonLength = BitConverter.ToInt32(bytes, 12), binaryStart = 28 + jsonLength;
                var json = JObject.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength));
                int Offset(JToken accessor) => binaryStart + (int)json["bufferViews"]![(int)accessor["bufferView"]!]!["byteOffset"]!;
                var entries = new List<Tuple<GameMeshData, GameDoorData?>>();
                var weights = new Dictionary<int, long>();
                long originalTriangles = 0;
                bool changed = false;
                foreach (var node in json["nodes"]!)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var primitive = json["meshes"]![(int)node["mesh"]!]!["primitives"]![0]!;
                    JToken Access(string key) => json["accessors"]![(int)primitive["attributes"]![key]!]!;
                    var pos = Access("POSITION");
                    int p = Offset(pos), n = Offset(Access("NORMAL")), c = Offset(Access("COLOR_0")), id = Offset(Access("_ELEMENT"));
                    var mesh = new GameMeshData { IsTransparent = (int)primitive["material"]! == 1 };
                    for (int v = 0; v < (int)pos["count"]!; v++)
                    {
                        mesh.Positions.Add(new Point3D(BitConverter.ToSingle(bytes, p + v * 12), -BitConverter.ToSingle(bytes, p + v * 12 + 8), BitConverter.ToSingle(bytes, p + v * 12 + 4)));
                        mesh.VertexNormals.Add(new Vector3D(BitConverter.ToSingle(bytes, n + v * 12), -BitConverter.ToSingle(bytes, n + v * 12 + 8), BitConverter.ToSingle(bytes, n + v * 12 + 4)));
                        mesh.VertexColors.Add(Color.FromArgb(bytes[c + v * 4 + 3], bytes[c + v * 4], bytes[c + v * 4 + 1], bytes[c + v * 4 + 2]));
                        int element = BitConverter.ToInt32(bytes, id + v * 4); mesh.ElementIndices.Add(element);
                        weights.TryGetValue(element, out long weight); weights[element] = weight + 32;
                    }
                    var indices = json["accessors"]![(int)primitive["indices"]!]!;
                    int start = Offset(indices);
                    originalTriangles += (int)indices["count"]! / 3;
                    for (int i = 0; i < (int)indices["count"]!; i++)
                    {
                        int vertex = BitConverter.ToInt32(bytes, start + i * 4); mesh.Indices.Add(vertex);
                        int element = mesh.ElementIndices[vertex]; weights[element] += 4;
                        if (i % 3 == 0) { result.Triangles.TryGetValue(element, out long count); result.Triangles[element] = count + 1; }
                    }
                    var parts = new List<GameMeshData>();
                    foreach (bool simplify in new[] { false, true })
                    {
                        var part = new GameMeshData { IsTransparent = mesh.IsTransparent };
                        var mapped = new Dictionary<int, int>();
                        for (int i = 0; i < mesh.Indices.Count; i += 3)
                        {
                            if (eligible.Contains(mesh.ElementIndices[mesh.Indices[i]]) != simplify) continue;
                            for (int j = 0; j < 3; j++)
                            {
                                int original = mesh.Indices[i + j];
                                if (!mapped.TryGetValue(original, out int target))
                                {
                                    target = part.Positions.Count; mapped.Add(original, target);
                                    part.Positions.Add(mesh.Positions[original]); part.VertexNormals.Add(mesh.VertexNormals[original]);
                                    part.VertexColors.Add(mesh.VertexColors[original]); part.ElementIndices.Add(mesh.ElementIndices[original]);
                                }
                                part.Indices.Add(target);
                            }
                        }
                        if (part.Indices.Count == 0) continue;
                        if (simplify && part.Indices.Count >= 3000)
                        {
                            var reduced = SimplifyOverview(part, 1.0 / 30.48); // Approximately 1 cm cells.
                            var retained = new HashSet<int>(reduced.Indices.Select(v => reduced.ElementIndices[v]));
                            if (reduced.Indices.Count > 0 && reduced.Indices.Count < part.Indices.Count &&
                                part.ElementIndices.Distinct().All(retained.Contains))
                            { part = reduced; changed = true; }
                        }
                        parts.Add(part);
                    }
                    string? doorKey = (string?)node["extras"]?["doorKey"];
                    foreach (var part in parts)
                        entries.Add(Tuple.Create(part, doorKey == null ? null : new GameDoorData(doorKey)));
                }
                long total = weights.Values.Sum();
                foreach (var weight in weights)
                { result.EstimatedBytes.TryGetValue(weight.Key, out long current); result.EstimatedBytes[weight.Key] = current + (long)Math.Round(tile.Size * (double)weight.Value / total); }
                if (!changed) continue;
                byte[] glb = BuildGlb(entries), compressed;
                using (var output = new MemoryStream())
                { using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(glb, 0, glb.Length); compressed = output.ToArray(); }
                if (compressed.Length < tile.Size)
                {
                    result.LighterScene.WebTiles[tileIndex] = new GameMepWebAsset { Name = tile.Name, Bytes = compressed, Sha256 = Hash(compressed), DecodedBytes = glb.Length, Bounds = tile.Bounds, Elements = tile.Elements };
                    removedTriangles += originalTriangles - entries.Sum(entry => (long)entry.Item1.Indices.Count / 3);
                }
            }
            result.LighterScene.OriginalRenderTriangleCount = checked((int)(result.Triangles.Values.Sum() - removedTriangles));
            return result;
        }

        public const int SchemaVersion = 2;
        private const double SpatialCellTripleSize = 240; // Three times an 80-foot cell (~24 m).
        private static readonly JsonSerializerSettings JsonSettings =
            new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Converters = { new WebColorJsonConverter() }
            };

        /// <summary>
        /// WPF Color peut être écrit par Json.NET sous la forme "sc#..." ou
        /// "#AARRGGBB" selon le runtime. Ces formats ne sont pas des couleurs CSS
        /// fiables. Le package web transporte donc toujours des octets RGBA explicites.
        /// </summary>
        private sealed class WebColorJsonConverter : JsonConverter
        {
            public override bool CanRead => false;

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Color);
            }

            public override void WriteJson(
                JsonWriter writer,
                object? value,
                JsonSerializer serializer)
            {
                Color color = value is Color typed ? typed : Colors.Transparent;
                writer.WriteStartObject();
                writer.WritePropertyName("r");
                writer.WriteValue(color.R);
                writer.WritePropertyName("g");
                writer.WriteValue(color.G);
                writer.WritePropertyName("b");
                writer.WriteValue(color.B);
                writer.WritePropertyName("a");
                writer.WriteValue(color.A);
                writer.WriteEndObject();
            }

            public override object ReadJson(
                JsonReader reader,
                Type objectType,
                object? existingValue,
                JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }
        }

        public static void PrepareStaticAssets(GameSceneData scene)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            scene.WebTiles.Clear();
            scene.WebOverviewGlb = Array.Empty<byte>();
            var pending = new Dictionary<Tuple<int, int, int>, List<Tuple<GameMeshData, GameDoorData?>>>();
            var sizes = new Dictionary<Tuple<int, int, int>, long>();
            var overview = new List<Tuple<GameMeshData, GameDoorData?>>();
            long pendingBytes = 0;
            void Flush(Tuple<int, int, int> key)
            {
                var batch = pending[key];
                var merged = new List<Tuple<GameMeshData, GameDoorData?>>();
                // Preserve door pivots; merge static meshes sharing their material.
                foreach (bool transparent in new[] { false, true })
                {
                    var parts = batch.Where(item => item.Item2 == null && item.Item1.IsTransparent == transparent).Select(item => item.Item1).ToList();
                    if (parts.Count > 0) merged.Add(Tuple.Create<GameMeshData, GameDoorData?>(MergeMeshes(parts), null));
                }
                merged.AddRange(batch.Where(item => item.Item2 != null));
                string tileName = "tile-" + scene.WebTiles.Count.ToString("D5") + ".glb.gz";
                byte[] glb = BuildGlb(merged);
                byte[] compressed;
                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(glb, 0, glb.Length);
                    compressed = output.ToArray();
                }
                var points = merged.SelectMany(item => item.Item1.Positions).Select(ToWebPoint).ToList();
                scene.WebTiles.Add(new GameMepWebAsset {
                    Name = tileName, Bytes = compressed, Sha256 = Hash(compressed), DecodedBytes = glb.LongLength,
                    Bounds = new[] { points.Min(v => v[0]), points.Min(v => v[1]), points.Min(v => v[2]), points.Max(v => v[0]), points.Max(v => v[1]), points.Max(v => v[2]) },
                    Elements = merged.SelectMany(item => item.Item1.ElementIndices).Where(i => i >= 0).Distinct().ToArray()
                });
                foreach (bool transparent in new[] { false, true })
                {
                    var parts = merged.Where(item => item.Item1.IsTransparent == transparent).Select(item => SimplifyOverview(item.Item1, 2)).Where(mesh => mesh.Indices.Count > 0).ToList();
                    if (parts.Count == 0) continue;
                    var coarse = MergeMeshes(parts); coarse.WebTileName = tileName;
                    overview.Add(Tuple.Create<GameMeshData, GameDoorData?>(coarse, null));
                }
                pendingBytes -= sizes[key]; sizes.Remove(key); pending.Remove(key);
            }
            foreach (var entry in SceneMeshes(scene))
            foreach (var mesh in SplitMesh(entry.Item1))
            {
                var a = mesh.Positions[mesh.Indices[0]]; var b = mesh.Positions[mesh.Indices[1]]; var c = mesh.Positions[mesh.Indices[2]];
                var key = Tuple.Create((int)Math.Floor((a.X + b.X + c.X) / SpatialCellTripleSize), (int)Math.Floor((a.Y + b.Y + c.Y) / SpatialCellTripleSize), (int)Math.Floor((a.Z + b.Z + c.Z) / SpatialCellTripleSize));
                long bytes = mesh.Positions.Count * 32L + mesh.Indices.Count * 4L;
                if (pending.ContainsKey(key) && sizes[key] + bytes > 8 * 1024 * 1024) Flush(key);
                if (!pending.ContainsKey(key)) { pending[key] = new List<Tuple<GameMeshData, GameDoorData?>>(); sizes[key] = 0; }
                pending[key].Add(Tuple.Create(mesh, entry.Item2)); sizes[key] += bytes; pendingBytes += bytes;
                if (pendingBytes > 64 * 1024 * 1024) Flush(sizes.OrderByDescending(item => item.Value).First().Key);
            }
            foreach (var key in pending.Keys.ToList()) Flush(key);
            // Keep the first download compact while preserving planar faces and
            // small solid elements. Coarse geometry is never used for wall cuts.
            for (double step = 4; (overview.Sum(item => (long)item.Item1.Indices.Count) > 1500000 || overview.Sum(item => item.Item1.Positions.Count * 32L + item.Item1.Indices.Count * 4L) > 32 * 1024 * 1024) && step <= 32; step *= 2)
                overview = overview.Select(item => { var coarse = SimplifyOverview(item.Item1, step); coarse.WebTileName = item.Item1.WebTileName; return Tuple.Create<GameMeshData, GameDoorData?>(coarse, null); }).ToList();
            scene.WebOverviewGlb = BuildGlb(overview);
            scene.WebModelGlb = Array.Empty<byte>();
            scene.WebPropertiesJson = JsonConvert.SerializeObject(
                scene.Elements
                    .Where(element => element.WebElementIndex >= 0)
                    .OrderBy(element => element.WebElementIndex)
                    .Select(element => new
                    {
                        index = element.WebElementIndex,
                        key = element.Key,
                        stableKey = element.StableKey,
                        center = element.HasBounds ? ToWebPoint(new Point3D(element.Bounds.X + element.Bounds.SizeX / 2, element.Bounds.Y + element.Bounds.SizeY / 2, element.Bounds.Z + element.Bounds.SizeZ / 2)) : null,
                        size = element.HasBounds ? new[] { element.Bounds.SizeX, element.Bounds.SizeZ, element.Bounds.SizeY } : null,
                        elementId = element.ElementId,
                        name = element.Name,
                        category = element.Category,
                        typeName = element.TypeName,
                        levelName = element.LevelName,
                        documentTitle = element.DocumentTitle,
                        selectionTargetKey = element.SelectionTargetKey,
                        properties = element.WebProperties
                    }),
                JsonSettings);
        }

        public static GameMepWebPackageResult Build(
            GameSceneData scene,
            string publicationName)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (!scene.MepGraph.HasData)
                throw new InvalidOperationException("Le graphe MEP est vide.");
            if (scene.WebTiles.Count == 0)
                throw new InvalidOperationException("La géométrie web n'est plus disponible.");

            byte[] mep = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
                GameMepReplayStore.Capture(
                    scene.MepGraph,
                    preserveElementPersistentIds: true),
                JsonSettings));
            byte[] properties = Encoding.UTF8.GetBytes(scene.WebPropertiesJson ?? "[]");
            byte[] viewer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
                new
                {
                    spawn = ToWebPoint(scene.SpawnFootPosition),
                    eyeHeight = 5.28,
                    initialYaw = scene.InitialYawRadians,
                    doors = scene.Doors.Select(door => new
                    {
                        key = door.Key,
                        center = ToWebPoint(door.Center),
                        hinge = ToWebPoint(door.Hinge),
                        secondHinge = ToWebPoint(door.SecondHinge)
                    })
                }, JsonSettings));
            byte[] thumbnail = Convert.FromBase64String(
                "UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEAAUAmJaQAA3AA/v3AgAA=");
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["mep.json"] = mep,
                ["properties.json"] = properties,
                ["viewer.json"] = viewer,
                ["thumbnail.webp"] = thumbnail
            };
            if (scene.WebOverviewGlb.Length > 0) files["overview.glb"] = scene.WebOverviewGlb;
            var manifest = new
            {
                schemaVersion = SchemaVersion,
                name = string.IsNullOrWhiteSpace(publicationName)
                    ? scene.MepGraph.DocumentTitle
                    : publicationName.Trim(),
                documentTitle = scene.MepGraph.DocumentTitle,
                viewName = scene.ViewName,
                createdUtc = DateTime.UtcNow,
                units = "revit-internal-feet",
                coordinateSystem = "right-handed-z-up",
                sourceDocumentId = scene.SourceDocumentId,
                sourceOrigin = new[] { scene.SourceOrigin.X, scene.SourceOrigin.Y, scene.SourceOrigin.Z },
                tiles = scene.WebTiles,
                triangleCount = scene.OriginalRenderTriangleCount,
                elementCount = scene.Elements.Count,
                mepElementCount = scene.MepGraph.Elements.Count,
                files = files.ToDictionary(
                    pair => pair.Key,
                    pair => new { bytes = pair.Value.LongLength, sha256 = Hash(pair.Value) })
            };
            string manifestJson = JsonConvert.SerializeObject(manifest, JsonSettings);
            files["manifest.json"] = Encoding.UTF8.GetBytes(manifestJson);

            byte[] package;
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (KeyValuePair<string, byte[]> file in files)
                    {
                        ZipArchiveEntry entry = archive.CreateEntry(
                            file.Key, CompressionLevel.Optimal);
                        using (Stream target = entry.Open())
                            target.Write(file.Value, 0, file.Value.Length);
                    }
                }
                package = output.ToArray();
            }

            return new GameMepWebPackageResult
            {
                Assets = scene.WebTiles,
                Bytes = package,
                Sha256 = Hash(package),
                ManifestJson = manifestJson,
                ValveIds = scene.MepGraph.Valves
                    .Where(valve => valve.IsEnabledAsValve)
                    .Select(valve => scene.MepGraph.FindElement(valve.ElementKey))
                    .Where(element => element != null)
                    .SelectMany(element => new[]
                    {
                        element!.Key,
                        element.PersistentId
                    })
                    .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            };
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes)
                    .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static List<Tuple<GameMeshData, GameDoorData?>> SceneMeshes(GameSceneData scene)
        {
            var meshes = new List<Tuple<GameMeshData, GameDoorData?>>();
            meshes.AddRange(scene.Meshes
                .Where(mesh => mesh.Indices.Count > 0)
                .Select(mesh => Tuple.Create<GameMeshData, GameDoorData?>(mesh, null)));
            foreach (GameDoorData door in scene.Doors)
            {
                if (door.OpaqueMesh.Indices.Count > 0)
                    meshes.Add(Tuple.Create<GameMeshData, GameDoorData?>(door.OpaqueMesh, door));
                if (door.TransparentMesh.Indices.Count > 0)
                    meshes.Add(Tuple.Create<GameMeshData, GameDoorData?>(door.TransparentMesh, door));
            }
            return meshes;
        }

        private static GameMeshData MergeMeshes(IList<GameMeshData> meshes)
        {
            var output = new GameMeshData { IsTransparent = meshes[0].IsTransparent, HasCompleteNormals = meshes.All(mesh => mesh.HasCompleteNormals && mesh.VertexNormals.Count == mesh.Positions.Count) };
            foreach (var mesh in meshes)
            {
                int offset = output.Positions.Count;
                foreach (var point in mesh.Positions) output.Positions.Add(point);
                foreach (var color in mesh.VertexColors) output.VertexColors.Add(color);
                foreach (int id in mesh.ElementIndices) output.ElementIndices.Add(id);
                if (output.HasCompleteNormals) foreach (var normal in mesh.VertexNormals) output.VertexNormals.Add(normal);
                foreach (int index in mesh.Indices) output.Indices.Add(index + offset);
            }
            return output;
        }

        internal static GameMeshData SimplifyOverview(GameMeshData source, double step)
        {
            var bounds = new Dictionary<int, Rect3D>();
            for (int i = 0; i < source.Positions.Count; i++)
            {
                int id = source.ElementIndices[i];
                if (!bounds.TryGetValue(id, out var box)) box = Rect3D.Empty;
                box.Union(source.Positions[i]); bounds[id] = box;
            }
            var result = new GameMeshData { IsTransparent = source.IsTransparent, HasCompleteNormals = false };
            var vertices = new Dictionary<Tuple<int, int, int, int, int, int>, int>();
            var triangles = new HashSet<Tuple<int, int, int>>();
            int Quantize(double value, double minimum, double extent)
            {
                if (Math.Abs(value - minimum) < .000001) return -1;
                if (Math.Abs(value - minimum - extent) < .000001) return int.MaxValue;
                return (int)Math.Floor((value - minimum) / Math.Max(.00001, Math.Min(step, extent / (step >= 16 ? 2 : 4))));
            }
            int Map(int index, int face)
            {
                var p = source.Positions[index]; int id = source.ElementIndices[index]; var box = bounds[id]; var color = source.VertexColors[index];
                // At least four cells across thin components: opposite faces of
                // a wall must never collapse onto each other.
                int x = Quantize(p.X, box.X, box.SizeX);
                int y = Quantize(p.Y, box.Y, box.SizeY);
                int z = Quantize(p.Z, box.Z, box.SizeZ);
                int rgb = (color.R >> 4) | ((color.G >> 4) << 4) | ((color.B >> 4) << 8);
                var key = Tuple.Create(id, x, y, z, step >= 16 ? 0 : face, rgb);
                if (vertices.TryGetValue(key, out int mapped)) return mapped;
                mapped = result.Positions.Count; vertices.Add(key, mapped);
                result.Positions.Add(p); result.VertexColors.Add(color); result.ElementIndices.Add(id); return mapped;
            }
            for (int t = 0; t < source.Indices.Count; t += 3)
            {
                int ia = source.Indices[t], ib = source.Indices[t + 1], ic = source.Indices[t + 2];
                var normal = Vector3D.CrossProduct(source.Positions[ib] - source.Positions[ia], source.Positions[ic] - source.Positions[ia]);
                int face = Math.Abs(normal.X) >= Math.Abs(normal.Y) && Math.Abs(normal.X) >= Math.Abs(normal.Z) ? (normal.X >= 0 ? 0 : 1) : Math.Abs(normal.Y) >= Math.Abs(normal.Z) ? (normal.Y >= 0 ? 2 : 3) : (normal.Z >= 0 ? 4 : 5);
                int a = Map(ia, face), b = Map(ib, face), c = Map(ic, face);
                if (a == b || b == c || a == c) continue;
                var key = a < b && a < c ? Tuple.Create(a, b, c) : b < c ? Tuple.Create(b, c, a) : Tuple.Create(c, a, b);
                if (!triangles.Add(key)) continue;
                result.Indices.Add(a); result.Indices.Add(b); result.Indices.Add(c);
            }
            return result;
        }

        private static IEnumerable<GameMeshData> SplitMesh(GameMeshData source)
        {
            const int maximumIndices = 90000;
            // Spatial cells (~24 m), followed by a triangle limit for dense equipment.
            var cells = Enumerable.Range(0, source.Indices.Count / 3).GroupBy(triangle => {
                var a = source.Positions[source.Indices[triangle * 3]];
                var b = source.Positions[source.Indices[triangle * 3 + 1]];
                var c = source.Positions[source.Indices[triangle * 3 + 2]];
                return Tuple.Create((int)Math.Floor((a.X + b.X + c.X) / SpatialCellTripleSize), (int)Math.Floor((a.Y + b.Y + c.Y) / SpatialCellTripleSize), (int)Math.Floor((a.Z + b.Z + c.Z) / SpatialCellTripleSize));
            });
            foreach (var cell in cells)
            {
            var sourceIndices = cell.SelectMany(triangle => new[] { source.Indices[triangle * 3], source.Indices[triangle * 3 + 1], source.Indices[triangle * 3 + 2] }).ToArray();
            for (int start = 0; start < sourceIndices.Length; start += maximumIndices)
            {
                var part = new GameMeshData { IsTransparent = source.IsTransparent, HasCompleteNormals = source.HasCompleteNormals };
                var indices = new Dictionary<int, int>();
                for (int i = start; i < Math.Min(sourceIndices.Length, start + maximumIndices); i++)
                {
                    int original = sourceIndices[i];
                    if (!indices.TryGetValue(original, out int mapped))
                    {
                        mapped = part.Positions.Count; indices.Add(original, mapped);
                        part.Positions.Add(source.Positions[original]);
                        part.VertexColors.Add(source.VertexColors[original]);
                        part.ElementIndices.Add(source.ElementIndices[original]);
                        if (original < source.VertexNormals.Count) part.VertexNormals.Add(source.VertexNormals[original]);
                    }
                    part.Indices.Add(mapped);
                }
                yield return part;
            }
            }
        }

        private static byte[] BuildGlb(List<Tuple<GameMeshData, GameDoorData?>> meshes)
        {
            var bufferViews = new List<object>();
            var accessors = new List<object>();
            var meshDefinitions = new List<object>();
            var nodes = new List<object>();
            using var binary = new MemoryStream();
            using var writer = new BinaryWriter(binary, Encoding.UTF8, true);

            foreach (Tuple<GameMeshData, GameDoorData?> entry in meshes)
            {
                GameMeshData mesh = entry.Item1;
                int position = WriteVectors(writer, binary, mesh.Positions, bufferViews, accessors, true);
                // Le viewport natif recalcule déjà les normales absentes dans
                // GameGpuSceneBuilder. L'export web doit appliquer exactement
                // le même garde-fou, sinon les faces concernées reçoivent toutes
                // UnitZ et la maquette apparaît comme un bloc de couleur uniforme.
                IEnumerable<Vector3D> webNormals =
                    mesh.HasCompleteNormals &&
                    mesh.VertexNormals.Count == mesh.Positions.Count
                        ? mesh.VertexNormals
                        : ComputeWebNormals(mesh);
                int normal = WriteVectors(writer, binary, webNormals, bufferViews, accessors, false);
                int color = WriteColors(writer, binary, mesh, bufferViews, accessors);
                int element = WriteElements(writer, binary, mesh, bufferViews, accessors);
                int indices = WriteIndices(writer, binary, mesh, bufferViews, accessors);
                int meshIndex = meshDefinitions.Count;
                meshDefinitions.Add(new
                {
                    primitives = new[]
                    {
                        new
                        {
                            attributes = new Dictionary<string, int>
                            {
                                ["POSITION"] = position,
                                ["NORMAL"] = normal,
                                ["COLOR_0"] = color,
                                ["_ELEMENT"] = element
                            },
                            indices,
                            material = mesh.IsTransparent ? 1 : 0,
                            mode = 4
                        }
                    }
                });
                GameDoorData? door = entry.Item2;
                nodes.Add(door == null
                    ? (object)new { mesh = meshIndex, extras = new { lodTileName = mesh.WebTileName } }
                    : new
                    {
                        mesh = meshIndex,
                        name = "BIMaestroDoor:" + door.Key,
                        extras = new { doorKey = door.Key }
                    });
            }

            byte[] binaryBytes = binary.ToArray();
            var gltf = new
            {
                asset = new { version = "2.0", generator = "BIMaestro" },
                scene = 0,
                scenes = new[] { new { nodes = Enumerable.Range(0, nodes.Count).ToArray() } },
                nodes,
                meshes = meshDefinitions,
                materials = new object[]
                {
                    new { name = "Opaque", pbrMetallicRoughness = new { metallicFactor = 0.12, roughnessFactor = 0.72 } },
                    new { name = "Transparent", alphaMode = "BLEND", doubleSided = true, pbrMetallicRoughness = new { metallicFactor = 0.05, roughnessFactor = 0.78 } }
                },
                buffers = new[] { new { byteLength = binaryBytes.Length } },
                bufferViews,
                accessors
            };
            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
                gltf, Formatting.None));
            json = Pad(json, 0x20);
            binaryBytes = Pad(binaryBytes, 0);
            using var glb = new MemoryStream();
            using var glbWriter = new BinaryWriter(glb);
            glbWriter.Write(0x46546C67);
            glbWriter.Write(2);
            glbWriter.Write(12 + 8 + json.Length + 8 + binaryBytes.Length);
            glbWriter.Write(json.Length);
            glbWriter.Write(0x4E4F534A);
            glbWriter.Write(json);
            glbWriter.Write(binaryBytes.Length);
            glbWriter.Write(0x004E4942);
            glbWriter.Write(binaryBytes);
            return glb.ToArray();
        }

        private static double[] ToWebPoint(Point3D point)
        {
            return new[] { point.X, point.Z, -point.Y };
        }

        private static int WriteVectors(
            BinaryWriter writer,
            MemoryStream stream,
            IEnumerable<Point3D> values,
            IList<object> views,
            IList<object> accessors,
            bool position)
        {
            Point3D[] items = values.ToArray();
            Align(stream, writer);
            int offset = (int)stream.Position;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (Point3D value in items)
            {
                float x = (float)value.X, y = (float)value.Z, z = (float)-value.Y;
                writer.Write(x); writer.Write(y); writer.Write(z);
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            }
            int view = views.Count;
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = items.Length * 12, target = 34962 });
            int accessor = accessors.Count;
            accessors.Add(position
                ? (object)new { bufferView = view, componentType = 5126, count = items.Length, type = "VEC3", min = new[] { minX, minY, minZ }, max = new[] { maxX, maxY, maxZ } }
                : new { bufferView = view, componentType = 5126, count = items.Length, type = "VEC3" });
            return accessor;
        }

        private static int WriteVectors(
            BinaryWriter writer,
            MemoryStream stream,
            IEnumerable<Vector3D> values,
            IList<object> views,
            IList<object> accessors,
            bool position)
        {
            Point3D[] points = values.Select(value => new Point3D(value.X, value.Y, value.Z)).ToArray();
            return WriteVectors(writer, stream, points, views, accessors, position);
        }

        private static int WriteColors(BinaryWriter writer, MemoryStream stream, GameMeshData mesh, IList<object> views, IList<object> accessors)
        {
            Align(stream, writer); int offset = (int)stream.Position;
            for (int index = 0; index < mesh.Positions.Count; index++)
            {
                var color = index < mesh.VertexColors.Count ? mesh.VertexColors[index] : System.Windows.Media.Colors.LightGray;
                writer.Write(color.R); writer.Write(color.G); writer.Write(color.B); writer.Write(color.A);
            }
            int view = views.Count; views.Add(new { buffer = 0, byteOffset = offset, byteLength = mesh.Positions.Count * 4, target = 34962 });
            int accessor = accessors.Count; accessors.Add(new { bufferView = view, componentType = 5121, normalized = true, count = mesh.Positions.Count, type = "VEC4" });
            return accessor;
        }

        private static IEnumerable<Vector3D> ComputeWebNormals(GameMeshData mesh)
        {
            var accumulated = new Vector3D[mesh.Positions.Count];
            for (int index = 0; index + 2 < mesh.Indices.Count; index += 3)
            {
                int indexA = mesh.Indices[index];
                int indexB = mesh.Indices[index + 1];
                int indexC = mesh.Indices[index + 2];
                if (indexA < 0 || indexB < 0 || indexC < 0 ||
                    indexA >= mesh.Positions.Count ||
                    indexB >= mesh.Positions.Count ||
                    indexC >= mesh.Positions.Count)
                {
                    continue;
                }

                Vector3D edgeA = mesh.Positions[indexB] - mesh.Positions[indexA];
                Vector3D edgeB = mesh.Positions[indexC] - mesh.Positions[indexA];
                Vector3D normal = Vector3D.CrossProduct(edgeA, edgeB);
                if (normal.LengthSquared < 1e-18)
                    continue;

                // Comme dans GameGpuSceneBuilder, la normale de face non
                // normalisée pondère naturellement le résultat par sa surface.
                accumulated[indexA] += normal;
                accumulated[indexB] += normal;
                accumulated[indexC] += normal;
            }

            for (int index = 0; index < accumulated.Length; index++)
            {
                Vector3D normal = accumulated[index];
                if (normal.LengthSquared < 1e-18)
                    normal = new Vector3D(0, 0, 1);
                else
                    normal.Normalize();
                accumulated[index] = normal;
            }
            return accumulated;
        }

        private static int WriteElements(BinaryWriter writer, MemoryStream stream, GameMeshData mesh, IList<object> views, IList<object> accessors)
        {
            Align(stream, writer); int offset = (int)stream.Position;
            for (int index = 0; index < mesh.Positions.Count; index++)
                writer.Write((uint)Math.Max(0, index < mesh.ElementIndices.Count ? mesh.ElementIndices[index] : 0));
            int view = views.Count; views.Add(new { buffer = 0, byteOffset = offset, byteLength = mesh.Positions.Count * 4, target = 34962 });
            int accessor = accessors.Count; accessors.Add(new { bufferView = view, componentType = 5125, count = mesh.Positions.Count, type = "SCALAR" });
            return accessor;
        }

        private static int WriteIndices(BinaryWriter writer, MemoryStream stream, GameMeshData mesh, IList<object> views, IList<object> accessors)
        {
            Align(stream, writer); int offset = (int)stream.Position;
            foreach (int index in mesh.Indices) writer.Write((uint)index);
            int view = views.Count; views.Add(new { buffer = 0, byteOffset = offset, byteLength = mesh.Indices.Count * 4, target = 34963 });
            int accessor = accessors.Count; accessors.Add(new { bufferView = view, componentType = 5125, count = mesh.Indices.Count, type = "SCALAR" });
            return accessor;
        }

        private static void Align(MemoryStream stream, BinaryWriter writer)
        {
            while ((stream.Position & 3) != 0) writer.Write((byte)0);
        }

        private static byte[] Pad(byte[] bytes, byte value)
        {
            int length = (bytes.Length + 3) & ~3;
            if (length == bytes.Length) return bytes;
            byte[] padded = Enumerable.Repeat(value, length).ToArray();
            Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
            return padded;
        }
    }
}

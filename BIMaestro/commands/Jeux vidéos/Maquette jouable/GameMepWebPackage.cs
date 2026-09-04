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

namespace BIMaestro.VideoGames
{
    internal sealed class GameMepWebPackageResult
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public string Sha256 { get; set; } = string.Empty;
        public string ManifestJson { get; set; } = "{}";
        public IList<string> ValveIds { get; set; } = new List<string>();
    }

    internal static class GameMepWebPackage
    {
        public const int SchemaVersion = 1;
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
            scene.WebModelGlb = BuildGlb(scene);
            scene.WebPropertiesJson = JsonConvert.SerializeObject(
                scene.Elements
                    .Where(element => element.WebElementIndex >= 0)
                    .OrderBy(element => element.WebElementIndex)
                    .Select(element => new
                    {
                        index = element.WebElementIndex,
                        key = element.Key,
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
            if (scene.WebModelGlb == null || scene.WebModelGlb.Length == 0)
                throw new InvalidOperationException("La géométrie web n'est plus disponible.");

            byte[] model = scene.WebModelGlb;
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
                ["model.glb"] = model,
                ["mep.json"] = mep,
                ["properties.json"] = properties,
                ["viewer.json"] = viewer,
                ["thumbnail.webp"] = thumbnail
            };
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

        private static byte[] BuildGlb(GameSceneData scene)
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
                    ? (object)new { mesh = meshIndex }
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

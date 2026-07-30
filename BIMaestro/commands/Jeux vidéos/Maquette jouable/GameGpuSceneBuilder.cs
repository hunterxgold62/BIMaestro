using System;
using System.Collections.Generic;
using HelixToolkit.Wpf.SharpDX;

namespace BIMaestro.VideoGames
{
    internal sealed class GameGpuRenderMesh
    {
        public MeshGeometry3D Geometry { get; set; } = new MeshGeometry3D();
        public bool IsTransparent { get; set; }
    }

    internal sealed class GameGpuDoorVertexRange
    {
        public MeshGeometry3D Geometry { get; set; } = new MeshGeometry3D();
        public int StartVertex { get; set; }
        public SharpDX.Vector3[] ClosedPositions { get; set; } = Array.Empty<SharpDX.Vector3>();
        public SharpDX.Vector3[] ClosedNormals { get; set; } = Array.Empty<SharpDX.Vector3>();
    }

    internal sealed class GameGpuDoorAnimation
    {
        public GameDoorData Door { get; set; } = null!;
        public IList<GameGpuDoorVertexRange> Ranges { get; } =
            new List<GameGpuDoorVertexRange>();
        public bool TargetOpen { get; set; }
        public double Progress { get; set; }
        public double OpenAngleDegrees { get; set; } = 92.0;
    }

    internal sealed class GameGpuSceneBuildResult
    {
        public IList<GameGpuRenderMesh> Meshes { get; } = new List<GameGpuRenderMesh>();
        public IList<GameGpuDoorAnimation> Doors { get; } =
            new List<GameGpuDoorAnimation>();
        public int TriangleCount { get; set; }
        public int VertexCount { get; set; }
    }

    /// <summary>
    /// Convertit une seule fois les collections WPF issues de Revit en buffers
    /// SharpDX. Aucun triangle n'est supprimé. Les portes restent regroupées par
    /// zone GPU, mais chaque plage de sommets peut être animée indépendamment.
    /// </summary>
    internal static class GameGpuSceneBuilder
    {
        private const double DoorChunkSize = 60.0;

        private sealed class PendingDoorRange
        {
            public GameDoorData Door { get; set; } = null!;
            public int StartVertex { get; set; }
            public int VertexCount { get; set; }
        }

        private sealed class DoorBucketBuilder
        {
            public DoorBucketBuilder(bool transparent)
            {
                Transparent = transparent;
            }

            public bool Transparent { get; }
            public Vector3Collection Positions { get; } = new Vector3Collection();
            public IntCollection Indices { get; } = new IntCollection();
            public Color4Collection Colors { get; } = new Color4Collection();
            public Vector3Collection Normals { get; } = new Vector3Collection();
            public bool HasCompleteNormals { get; set; } = true;
            public IList<PendingDoorRange> Ranges { get; } =
                new List<PendingDoorRange>();
        }

        public static GameGpuSceneBuildResult Build(GameSceneData scene)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));

            var result = new GameGpuSceneBuildResult();
            foreach (GameMeshData source in scene.Meshes)
            {
                MeshGeometry3D? geometry = BuildStaticGeometry(source);
                if (geometry == null)
                    continue;

                result.Meshes.Add(new GameGpuRenderMesh
                {
                    Geometry = geometry,
                    IsTransparent = source.IsTransparent
                });
                result.TriangleCount += geometry.TriangleIndices.Count / 3;
                result.VertexCount += geometry.Positions.Count;
            }

            BuildDoorBuckets(scene, result);

            scene.OriginalRenderTriangleCount = result.TriangleCount;
            scene.OptimizedRenderTriangleCount = result.TriangleCount;
            scene.RenderBucketCount = result.Meshes.Count;
            scene.RenderVertexCount = result.VertexCount;

            // Les buffers DirectX ont leur propre stockage compact en float.
            // Les anciennes collections WPF peuvent être libérées.
            scene.Meshes.Clear();
            foreach (GameDoorData door in scene.Doors)
                door.ReleaseSourceGeometry();
            return result;
        }

        private static void BuildDoorBuckets(
            GameSceneData scene,
            GameGpuSceneBuildResult result)
        {
            var buckets =
                new Dictionary<(int X, int Y, int Z, bool Transparent), DoorBucketBuilder>();

            foreach (GameDoorData door in scene.Doors)
            {
                AppendDoorMesh(door, door.OpaqueMesh, false, buckets);
                AppendDoorMesh(door, door.TransparentMesh, true, buckets);
            }

            var animations = new Dictionary<GameDoorData, GameGpuDoorAnimation>();
            foreach (DoorBucketBuilder bucket in buckets.Values)
            {
                if (bucket.Positions.Count == 0 || bucket.Indices.Count < 3)
                    continue;

                Vector3Collection normals = bucket.HasCompleteNormals
                    ? bucket.Normals
                    : ComputeNormals(bucket.Positions, bucket.Indices);
                var geometry = new MeshGeometry3D
                {
                    Positions = bucket.Positions,
                    TriangleIndices = bucket.Indices,
                    Normals = normals,
                    Colors = bucket.Colors,
                    IsDynamic = true,
                    PreDefinedVertexCount = bucket.Positions.Count,
                    PreDefinedIndexCount = bucket.Indices.Count
                };
                geometry.UpdateBounds();

                result.Meshes.Add(new GameGpuRenderMesh
                {
                    Geometry = geometry,
                    IsTransparent = bucket.Transparent
                });
                result.TriangleCount += bucket.Indices.Count / 3;
                result.VertexCount += bucket.Positions.Count;

                foreach (PendingDoorRange pending in bucket.Ranges)
                {
                    if (!animations.TryGetValue(
                        pending.Door,
                        out GameGpuDoorAnimation animation))
                    {
                        animation = new GameGpuDoorAnimation { Door = pending.Door };
                        animations.Add(pending.Door, animation);
                        result.Doors.Add(animation);
                    }

                    var closedPositions = new SharpDX.Vector3[pending.VertexCount];
                    var closedNormals = new SharpDX.Vector3[pending.VertexCount];
                    for (int index = 0; index < pending.VertexCount; index++)
                    {
                        int sourceIndex = pending.StartVertex + index;
                        closedPositions[index] = geometry.Positions[sourceIndex];
                        closedNormals[index] = geometry.Normals[sourceIndex];
                    }

                    animation.Ranges.Add(new GameGpuDoorVertexRange
                    {
                        Geometry = geometry,
                        StartVertex = pending.StartVertex,
                        ClosedPositions = closedPositions,
                        ClosedNormals = closedNormals
                    });
                }
            }
        }

        private static void AppendDoorMesh(
            GameDoorData door,
            GameMeshData source,
            bool transparent,
            IDictionary<(int X, int Y, int Z, bool Transparent), DoorBucketBuilder> buckets)
        {
            if (source.Positions.Count == 0 || source.Indices.Count < 3)
                return;

            var key = (
                ToDoorChunk(door.Center.X),
                ToDoorChunk(door.Center.Y),
                ToDoorChunk(door.Center.Z),
                transparent);
            if (!buckets.TryGetValue(key, out DoorBucketBuilder bucket))
            {
                bucket = new DoorBucketBuilder(transparent);
                buckets.Add(key, bucket);
            }

            int startVertex = bucket.Positions.Count;
            AppendSource(source, bucket.Positions, bucket.Indices, bucket.Colors, bucket.Normals);
            if (!source.HasCompleteNormals ||
                source.VertexNormals.Count != source.Positions.Count)
            {
                bucket.HasCompleteNormals = false;
            }

            bucket.Ranges.Add(new PendingDoorRange
            {
                Door = door,
                StartVertex = startVertex,
                VertexCount = source.Positions.Count
            });
        }

        private static MeshGeometry3D? BuildStaticGeometry(GameMeshData source)
        {
            if (source.Positions.Count == 0 || source.Indices.Count < 3)
                return null;

            var positions = new Vector3Collection(source.Positions.Count);
            var indices = new IntCollection(source.Indices.Count);
            var colors = new Color4Collection(source.Positions.Count);
            var normals = new Vector3Collection(source.Positions.Count);
            AppendSource(source, positions, indices, colors, normals);

            Vector3Collection finalNormals =
                source.HasCompleteNormals &&
                source.VertexNormals.Count == source.Positions.Count
                    ? normals
                    : ComputeNormals(positions, indices);
            var geometry = new MeshGeometry3D
            {
                Positions = positions,
                TriangleIndices = indices,
                Normals = finalNormals,
                Colors = colors,
                IsDynamic = false,
                PreDefinedVertexCount = positions.Count,
                PreDefinedIndexCount = indices.Count
            };
            geometry.UpdateBounds();
            return geometry;
        }

        private static void AppendSource(
            GameMeshData source,
            Vector3Collection positions,
            IntCollection indices,
            Color4Collection colors,
            Vector3Collection normals)
        {
            int baseVertex = positions.Count;
            for (int index = 0; index < source.Positions.Count; index++)
            {
                System.Windows.Media.Media3D.Point3D point = source.Positions[index];
                positions.Add(new SharpDX.Vector3(
                    (float)point.X,
                    (float)point.Y,
                    (float)point.Z));

                System.Windows.Media.Color color =
                    index < source.VertexColors.Count
                        ? source.VertexColors[index]
                        : System.Windows.Media.Color.FromRgb(190, 195, 202);
                colors.Add(new SharpDX.Color4(
                    color.R / 255f,
                    color.G / 255f,
                    color.B / 255f,
                    color.A / 255f));

                if (index < source.VertexNormals.Count)
                {
                    System.Windows.Media.Media3D.Vector3D normal =
                        source.VertexNormals[index];
                    normals.Add(new SharpDX.Vector3(
                        (float)normal.X,
                        (float)normal.Y,
                        (float)normal.Z));
                }
                else
                {
                    normals.Add(SharpDX.Vector3.UnitZ);
                }
            }

            for (int index = 0; index < source.Indices.Count; index++)
                indices.Add(baseVertex + source.Indices[index]);
        }

        private static int ToDoorChunk(double coordinate)
        {
            return (int)Math.Floor(coordinate / DoorChunkSize);
        }

        private static Vector3Collection ComputeNormals(
            Vector3Collection positions,
            IntCollection indices)
        {
            var accumulated = new SharpDX.Vector3[positions.Count];
            for (int index = 0; index + 2 < indices.Count; index += 3)
            {
                int indexA = indices[index];
                int indexB = indices[index + 1];
                int indexC = indices[index + 2];
                SharpDX.Vector3 edgeA = positions[indexB] - positions[indexA];
                SharpDX.Vector3 edgeB = positions[indexC] - positions[indexA];
                SharpDX.Vector3 normal = SharpDX.Vector3.Cross(edgeA, edgeB);
                if (normal.LengthSquared() < 1e-16f)
                    continue;

                // La pondération par la surface évite les artefacts des petites
                // facettes sans ajouter de calcul à l'exécution.
                accumulated[indexA] += normal;
                accumulated[indexB] += normal;
                accumulated[indexC] += normal;
            }

            var normals = new Vector3Collection(positions.Count);
            for (int index = 0; index < accumulated.Length; index++)
            {
                SharpDX.Vector3 normal = accumulated[index];
                if (normal.LengthSquared() < 1e-16f)
                    normal = SharpDX.Vector3.UnitZ;
                else
                    normal.Normalize();
                normals.Add(normal);
            }

            return normals;
        }
    }
}

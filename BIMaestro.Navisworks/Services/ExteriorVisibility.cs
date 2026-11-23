using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Media.Media3D;
using BIMaestro.VideoGames;

namespace BIMaestro.Navisworks.Services
{
    internal static class ExteriorVisibility
    {
        private const int Resolution = 384;

        public static (int Before, int After) Filter(GameSceneData scene, List<int> modelByElement,
            bool[] exteriorOnly, IProgress<string> progress, CancellationToken cancellation)
        {
            int before = scene.Meshes.Count;
            if (!exteriorOnly.Any(value => value)) return (before, before);

            var keep = new bool[before];
            var groups = new List<int>[exteriorOnly.Length];
            for (int i = 0; i < before; i++)
            {
                int model = modelByElement[scene.Meshes[i].ElementIndices[0]];
                if (!exteriorOnly[model]) keep[i] = true;
                else (groups[model] ??= new List<int>()).Add(i);
            }

            foreach (var group in groups)
            {
                if (group == null) continue;
                cancellation.ThrowIfCancellationRequested();
                var meshes = group.Select(i => scene.Meshes[i]).ToArray();
                var bounds = Rect3D.Empty;
                foreach (int element in meshes.Select(mesh => mesh.ElementIndices[0]).Distinct())
                    bounds.Union(scene.Elements[element].Bounds);
                string file = scene.Elements[meshes[0].ElementIndices[0]].DocumentTitle;
                var visible = FindVisible(meshes, bounds,
                    (view, total) => progress.Report($"Analyse extérieure de {file}… vue {view}/{total}"), cancellation);
                foreach (int id in visible) keep[group[id]] = true;
            }

            var retainedElements = new bool[scene.Elements.Count];
            for (int i = 0; i < before; i++)
                if (keep[i]) retainedElements[scene.Meshes[i].ElementIndices[0]] = true;

            var newIndex = new int[scene.Elements.Count];
            int next = 0;
            for (int i = 0; i < retainedElements.Length; i++)
            {
                if (!retainedElements[i]) continue;
                var element = scene.Elements[i];
                int model = modelByElement[i];
                if (exteriorOnly[model]) element.ResetBounds();
                newIndex[i] = next;
                element.WebElementIndex = next;
                scene.Elements[next] = element;
                modelByElement[next++] = model;
            }
            modelByElement.RemoveRange(next, retainedElements.Length - next);
            while (scene.Elements.Count > next) scene.Elements.RemoveAt(scene.Elements.Count - 1);

            int write = 0;
            int triangles = 0;
            for (int index = 0; index < before; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!keep[index]) continue;
                var mesh = scene.Meshes[index];
                int id = mesh.ElementIndices[0];
                int retainedId = newIndex[id];
                if (id != retainedId)
                    for (int i = 0; i < mesh.ElementIndices.Count; i++) mesh.ElementIndices[i] = retainedId;
                if (exteriorOnly[modelByElement[retainedId]]) scene.Elements[retainedId].Include(mesh.Positions);
                scene.Meshes[write++] = mesh;
                triangles += mesh.Indices.Count / 3;
            }
            while (scene.Meshes.Count > write) scene.Meshes.RemoveAt(scene.Meshes.Count - 1);
            scene.OriginalRenderTriangleCount = triangles;
            return (before, write);
        }

        private static HashSet<int> FindVisible(GameMeshData[] meshes, Rect3D bounds,
            Action<int, int> progress, CancellationToken cancellation)
        {
            var visible = new HashSet<int>();
            var sampled = new HashSet<int>();
            var opaque = Enumerable.Range(0, meshes.Length).Where(i => !meshes[i].IsTransparent).ToArray();
            var transparent = Enumerable.Range(0, meshes.Length).Where(i => meshes[i].IsTransparent).ToArray();
            var directions = new[]
            {
                new Vector3D(1,0,0), new Vector3D(-1,0,0), new Vector3D(0,1,0), new Vector3D(0,-1,0),
                new Vector3D(0,0,1), new Vector3D(0,0,-1),
                new Vector3D(1,1,1), new Vector3D(1,1,-1), new Vector3D(1,-1,1), new Vector3D(1,-1,-1),
                new Vector3D(-1,1,1), new Vector3D(-1,1,-1), new Vector3D(-1,-1,1), new Vector3D(-1,-1,-1)
            };
            var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            double diameter = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length;
            double scale = (Resolution - 4) / Math.Max(diameter, .001);
            var depth = new float[Resolution * Resolution];
            var owner = new int[depth.Length];

            for (int view = 0; view < directions.Length; view++)
            {
                cancellation.ThrowIfCancellationRequested();
                progress(view + 1, directions.Length);
                for (int pixel = 0; pixel < depth.Length; pixel++) { depth[pixel] = float.NegativeInfinity; owner[pixel] = -1; }

                var forward = directions[view];
                forward.Normalize();
                var reference = Math.Abs(forward.Z) > .9 ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1);
                var right = Vector3D.CrossProduct(reference, forward);
                right.Normalize();
                var up = Vector3D.CrossProduct(forward, right);

                Point3D Project(Point3D point)
                {
                    var local = point - center;
                    return new Point3D(Vector3D.DotProduct(local, right) * scale + Resolution / 2.0,
                        Vector3D.DotProduct(local, up) * scale + Resolution / 2.0,
                        Vector3D.DotProduct(local, forward));
                }

                void Draw(int id, bool isTransparent)
                {
                    var mesh = meshes[id];
                    if (isTransparent && visible.Contains(id)) return;
                    bool hit = false;
                    for (int i = 0; i < mesh.Indices.Count; i += 3)
                    {
                        if ((i & 2047) == 0) cancellation.ThrowIfCancellationRequested();
                        var a = Project(mesh.Positions[mesh.Indices[i]]);
                        var b = Project(mesh.Positions[mesh.Indices[i + 1]]);
                        var c = Project(mesh.Positions[mesh.Indices[i + 2]]);
                        hit |= Rasterize(a, b, c, id, isTransparent, depth, owner, out bool inFront);
                        if (isTransparent && inFront) { visible.Add(id); break; }
                    }
                    if (hit) sampled.Add(id);
                }

                foreach (var mesh in opaque) Draw(mesh, false);
                foreach (int id in owner)
                    if (id >= 0) visible.Add(id);
                foreach (var mesh in transparent) Draw(mesh, true);
            }

            // Keep pieces too thin to cover a sampled pixel in any of the exterior views.
            for (int id = 0; id < meshes.Length; id++)
                if (!sampled.Contains(id)) visible.Add(id);
            return visible;
        }

        private static bool Rasterize(Point3D a, Point3D b, Point3D c, int id, bool transparent,
            float[] depth, int[] owner, out bool inFront)
        {
            inFront = false;
            double bx = b.X - a.X, by = b.Y - a.Y, cx = c.X - a.X, cy = c.Y - a.Y;
            double area = bx * cy - by * cx;
            if (Math.Abs(area) < 1e-8) return false;

            int left = Math.Max(0, (int)Math.Ceiling(Math.Min(a.X, Math.Min(b.X, c.X)) - .5));
            int right = Math.Min(Resolution - 1, (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) - .5));
            int top = Math.Max(0, (int)Math.Ceiling(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - .5));
            int bottom = Math.Min(Resolution - 1, (int)Math.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - .5));
            double inv = 1 / area;
            bool sampled = false;
            for (int y = top; y <= bottom; y++)
            {
                double dy = y + .5 - a.Y;
                for (int x = left; x <= right; x++)
                {
                    double dx = x + .5 - a.X;
                    double u = (dx * cy - dy * cx) * inv, v = (bx * dy - by * dx) * inv;
                    if (u < -1e-9 || v < -1e-9 || u + v > 1 + 1e-9) continue;
                    sampled = true;
                    int pixel = y * Resolution + x;
                    float z = (float)(a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z));
                    if (transparent)
                    {
                        if (z >= depth[pixel]) { inFront = true; return true; }
                    }
                    else if (z > depth[pixel])
                    {
                        depth[pixel] = z;
                        owner[pixel] = id;
                    }
                }
            }
            return sampled;
        }
    }
}

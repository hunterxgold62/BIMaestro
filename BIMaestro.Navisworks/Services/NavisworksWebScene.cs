using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using BIMaestro.VideoGames;
using Media = System.Windows.Media;
using Media3D = System.Windows.Media.Media3D;

namespace BIMaestro.Navisworks.Services
{
    internal static class NavisworksWebScene
    {
        // Navisworks 2027 moves this option to export settings and only exposes the properties loaded in the model.
        private static readonly System.Reflection.PropertyInfo PreventPropertyExport =
            typeof(PublishProperties).GetProperty("PreventObjectPropertyExport");

        // Read the Navisworks document and COM fragments on its UI thread.
        public static async Task<(GameSceneData Scene, List<int> ModelIndexByElement)> CaptureAsync(Document document, string title,
            IReadOnlyList<Model> models, IReadOnlyList<WebModelScope> scopes, IProgress<string> progress, CancellationToken cancellation)
        {
            var scene = new GameSceneData { ViewName = "Navisworks", SourceDocumentId = "navisworks:" + HashKey(Path.GetFullPath(document.FileName).ToUpperInvariant()) };
            scene.MepGraph.DocumentTitle = title;
            scene.MepGraph.ScenarioCanPersist = true;
            double scale = UnitConversion.ScaleFactor(document.Units, Units.Feet);
            var view = document.CurrentViewpoint.Value;
            var eye = new Media3D.Point3D(view.Position.X * scale, view.Position.Y * scale, view.Position.Z * scale);
            var r = view.Rotation;
            var rotation = new Media3D.Quaternion(r.A, r.B, r.C, r.D);
            var camera = Media3D.Matrix3D.Identity;
            camera.Rotate(rotation);
            var forward = camera.Transform(new Media3D.Vector3D(0, 0, -1));

            var modelIndexByElement = new List<int>();
            var uiInterval = Stopwatch.StartNew();
            for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                var model = models[modelIndex];
                bool simplify = scopes[modelIndex] == WebModelScope.SimplifiedShell;
                bool indexVertices = scopes[modelIndex] == WebModelScope.SimplifiedModel;
                cancellation.ThrowIfCancellationRequested();
                var publish = model.PublishProperties;
                if (publish != null && !publish.AllowResave)
                    throw new InvalidOperationException("La sélection contient un NWD protégé qui ne peut pas être publié.");

                string lot = Path.GetFileName(model.FileName);
                int skippedFragments = 0;
                string modelKey = HashKey((model.FileName ?? lot ?? "").ToUpperInvariant());
                bool canExportProperties = publish == null || !Equals(PreventPropertyExport?.GetValue(publish), true);
                var stack = new Stack<(ModelItem Item, NavisworksBimMetadata Owner, string Level)>();
                stack.Push((model.RootItem, null, ""));
                while (stack.Count > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (uiInterval.ElapsedMilliseconds >= 100)
                    {
                        progress.Report($"Extraction de {lot}… {scene.Elements.Count:N0} objets · {scene.OriginalRenderTriangleCount:N0} triangles · {skippedFragments:N0} fragments hors objet ignorés");
                        await Task.Delay(1, cancellation);
                        uiInterval.Restart();
                    }

                    var (item, owner, level) = stack.Pop();
                    if (item.IsHidden) continue;

                    NavisworksBimMetadata metadata = null;
                    if (canExportProperties && (owner == null || item.IsComposite || item.IsInsert))
                    {
                        metadata = NavisworksBimMetadata.Read(item);
                        if (metadata.IsStorey)
                        {
                            level = metadata.Name.Length > 0 ? metadata.Name : metadata.Level;
                            owner = null;
                        }
                        else if ((metadata.HasObjectIdentity && (owner == null || metadata.SourceId != owner.SourceId))
                            || (owner == null && (metadata.IfcType.Length > 0
                                || (metadata.Category.Length > 0 && (item.IsComposite || item.IsInsert)))))
                            owner = metadata;
                        if (metadata.Level.Length > 0) level = metadata.Level;
                    }
                    foreach (var child in item.Children) stack.Push((child, owner, level));
                    if (!item.HasGeometry) continue;

                    var geometry = item.Geometry;
                    var c = geometry.ActiveColor;
                    double opacity = 1 - geometry.ActiveTransparency;
                    if (opacity <= 0) continue;
                    var color = Media.Color.FromArgb(Channel(opacity), Channel(c.R), Channel(c.G), Channel(c.B));
                    var path = ComApiBridge.ToInwOaPath(item);
                    if (path == null) continue;
                    var itemPath = path.ArrayData as Array;
                    var ownerData = owner ?? metadata ?? new NavisworksBimMetadata();
                    var element = ownerData.Element ?? ownerData.MakeElement(scene.Elements.Count, lot, level, modelKey);
                    var mesh = new GameMeshData { IsTransparent = color.A < 255 };
                    SimplifiedShellMesh shell = null;
                    var vertexIndices = indexVertices
                        ? new Dictionary<(Media3D.Point3D, Media3D.Vector3D), int>() : null;
                    foreach (InwOaFragment3 fragment in path.Fragments())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        // COM can enumerate fragments from other occurrences of a shared geometry.
                        if (!MatchesFragmentPath(itemPath, fragment.path?.ArrayData as Array))
                        {
                            skippedFragments++;
                            if (uiInterval.ElapsedMilliseconds >= 100)
                            {
                                progress.Report($"Extraction de {lot}… {scene.OriginalRenderTriangleCount:N0} triangles · {skippedFragments:N0} fragments hors objet ignorés");
                                await Task.Delay(1, cancellation);
                                uiInterval.Restart();
                            }
                            continue;
                        }
                        if (simplify && shell == null)
                        {
                            var bounds = item.BoundingBox();
                            var min = bounds.Min;
                            var max = bounds.Max;
                            shell = new SimplifiedShellMesh(mesh, new Media3D.Point3D(min.X * scale, min.Y * scale, min.Z * scale),
                                new Media3D.Point3D(max.X * scale, max.Y * scale, max.Z * scale), SimplifiedShellMesh.InitialSubdivisions, color, element.WebElementIndex);
                        }
                        var matrix = (Array)((InwLTransform3f3)fragment.GetLocalToWorldMatrix()).Matrix;
                        int first = matrix.GetLowerBound(0);
                        var values = new double[16];
                        for (int i = 0; i < values.Length; i++) values[i] = Convert.ToDouble(matrix.GetValue(first + i));
                        var transform = new Media3D.Matrix3D(values[0], values[1], values[2], values[3],
                            values[4], values[5], values[6], values[7], values[8], values[9], values[10], values[11],
                            values[12], values[13], values[14], values[15]);
                        var sink = new TriangleSink(mesh, transform, scale, color, element.WebElementIndex, cancellation, shell, vertexIndices);
                        fragment.GenerateSimplePrimitives(nwEVertexProperty.eNONE, sink);
                        sink.ThrowIfCanceled();
                    }
                    shell?.Complete(cancellation);
                    if (mesh.Indices.Count == 0) continue;
                    element.Include(mesh.Positions);
                    scene.Meshes.Add(mesh);
                    if (ownerData.Element == null)
                    {
                        ownerData.Element = element;
                        scene.Elements.Add(element);
                        modelIndexByElement.Add(modelIndex);
                    }
                    scene.OriginalRenderTriangleCount += mesh.Indices.Count / 3;
                }
            }

            if (scene.Meshes.Count == 0)
                throw new InvalidOperationException("Aucune géométrie 3D visible dans les maquettes sélectionnées.");
            scene.NormalizeCoordinates(eye, forward, useMeshBounds: true);
            return (scene, modelIndexByElement);
        }

        internal static bool MatchesFragmentPath(Array itemPath, Array fragmentPath)
        {
            // Keep the legacy behavior for a fragment without a usable path.
            if (itemPath == null || fragmentPath == null || itemPath.Length == 0 || fragmentPath.Length == 0)
                return true;
            if (itemPath.Length != fragmentPath.Length) return false;
            if (itemPath is int[] item && fragmentPath is int[] fragment)
            {
                for (int i = 0; i < item.Length; i++)
                    if (item[i] != fragment[i]) return false;
                return true;
            }

            int itemStart = itemPath.GetLowerBound(0);
            int fragmentStart = fragmentPath.GetLowerBound(0);
            for (int i = 0; i < itemPath.Length; i++)
                if (!Equals(itemPath.GetValue(itemStart + i), fragmentPath.GetValue(fragmentStart + i))) return false;
            return true;
        }

        internal static Media3D.Point3D FindSpawn(GameSceneData scene, CancellationToken cancellation)
        {
            var target = scene.ViewEye - new Media3D.Vector3D(0, 0, 5.28);
            var best = target;
            double bestScore = double.PositiveInfinity;
            foreach (var mesh in scene.Meshes)
            {
                cancellation.ThrowIfCancellationRequested();
                if (scene.Elements[mesh.ElementIndices[0]].Category != "Sols" || mesh.IsTransparent) continue;
                for (int i = 0; i < mesh.Indices.Count; i += 3)
                {
                    var a = mesh.Positions[mesh.Indices[i]];
                    var b = mesh.Positions[mesh.Indices[i + 1]];
                    var c = mesh.Positions[mesh.Indices[i + 2]];
                    var ab = b - a;
                    var ac = c - a;
                    var normal = Media3D.Vector3D.CrossProduct(ab, ac);
                    if (normal.Z <= 0.45 * normal.Length) continue;

                    var offset = target - a;
                    double u = (offset.X * ac.Y - offset.Y * ac.X) / normal.Z;
                    double v = (ab.X * offset.Y - ab.Y * offset.X) / normal.Z;
                    var point = u >= 0 && v >= 0 && u + v <= 1
                        ? a + u * ab + v * ac
                        : a + (ab + ac) / 3;
                    var delta = point - target;
                    double score = delta.X * delta.X + delta.Y * delta.Y + 4 * delta.Z * delta.Z;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = point + new Media3D.Vector3D(0, 0, .04);
                }
            }
            return best;
        }

        internal static string HashKey(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static byte Channel(double value) => (byte)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);

        private sealed class TriangleSink : InwSimplePrimitivesCB
        {
            private readonly GameMeshData _mesh;
            private readonly Media3D.Matrix3D _transform;
            private readonly double _scale;
            private readonly Media.Color _color;
            private readonly int _element;
            private readonly CancellationToken _cancellation;
            private readonly bool _mirrored;
            private readonly SimplifiedShellMesh _shell;
            private readonly Dictionary<(Media3D.Point3D, Media3D.Vector3D), int> _vertexIndices;

            public TriangleSink(GameMeshData mesh, Media3D.Matrix3D transform, double scale,
                Media.Color color, int element, CancellationToken cancellation, SimplifiedShellMesh shell,
                Dictionary<(Media3D.Point3D, Media3D.Vector3D), int> vertexIndices)
            {
                _mesh = mesh;
                _transform = transform;
                _scale = scale;
                _color = color;
                _element = element;
                _cancellation = cancellation;
                _mirrored = transform.Determinant < 0;
                _shell = shell;
                _vertexIndices = vertexIndices;
            }

            public void Triangle(InwSimpleVertex a, InwSimpleVertex b, InwSimpleVertex c)
            {
                if (_cancellation.IsCancellationRequested) return;
                var pa = Position(a);
                var pb = Position(_mirrored ? c : b);
                var pc = Position(_mirrored ? b : c);
                if (_shell != null) { _shell.AddTriangle(pa, pb, pc); return; }
                var normal = Media3D.Vector3D.CrossProduct(pb - pa, pc - pa);
                if (normal.LengthSquared < 1e-20) return;
                normal.Normalize();
                Add(pa, normal);
                Add(pb, normal);
                Add(pc, normal);
            }

            private Media3D.Point3D Position(InwSimpleVertex vertex)
            {
                var coordinate = (Array)vertex.coord;
                Media3D.Point3D local;
                if (coordinate is float[] single)
                    local = new Media3D.Point3D(single[0], single[1], single[2]);
                else if (coordinate is double[] wide)
                    local = new Media3D.Point3D(wide[0], wide[1], wide[2]);
                else
                {
                    int first = coordinate.GetLowerBound(0);
                    local = new Media3D.Point3D(Convert.ToDouble(coordinate.GetValue(first)),
                        Convert.ToDouble(coordinate.GetValue(first + 1)),
                        Convert.ToDouble(coordinate.GetValue(first + 2)));
                }
                var point = _transform.Transform(local);
                return new Media3D.Point3D(point.X * _scale, point.Y * _scale, point.Z * _scale);
            }

            private void Add(Media3D.Point3D position, Media3D.Vector3D normal)
            {
                int index = _mesh.Positions.Count;
                if (_vertexIndices != null)
                {
                    var key = (position, normal);
                    if (_vertexIndices.TryGetValue(key, out int shared))
                    {
                        _mesh.Indices.Add(shared);
                        return;
                    }
                    _vertexIndices.Add(key, index);
                }
                _mesh.Indices.Add(index);
                _mesh.Positions.Add(position);
                _mesh.VertexNormals.Add(normal);
                _mesh.VertexColors.Add(_color);
                _mesh.ElementIndices.Add(_element);
            }

            public void ThrowIfCanceled() => _cancellation.ThrowIfCancellationRequested();
            public void Line(InwSimpleVertex a, InwSimpleVertex b) { }
            public void Point(InwSimpleVertex a) { }
            public void SnapPoint(InwSimpleVertex a) { }
        }
    }
}

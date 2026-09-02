using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using Color = System.Windows.Media.Color;
using Transform = Autodesk.Revit.DB.Transform;

namespace BIMaestro.VideoGames
{
    /// <summary>
    /// Exporte la vue 3D Revit directement vers des maillages destinés au GPU.
    /// Le CustomExporter respecte la visibilité, la boîte de coupe, les liens et
    /// les couleurs de matériau/surcharges réellement affichées dans la vue.
    /// </summary>
    internal sealed class RevitGameExportContext : IExportContext
    {
        private const double RenderChunkSize = 60.0; // pieds, environ 18 mètres
        private const double InsulationOpacity = 0.60; // 40 % transparent

        private readonly Document _rootDocument;
        private readonly GameSceneData _scene = new GameSceneData();
        private readonly Dictionary<(int X, int Y, int Z, bool Transparent), GameMeshData> _renderBuckets
            = new Dictionary<(int, int, int, bool), GameMeshData>();
        private readonly Stack<Transform> _transforms = new Stack<Transform>();
        private readonly Stack<Document> _documents = new Stack<Document>();
        private readonly Stack<bool> _preferredWalkable = new Stack<bool>();
        private readonly Stack<bool> _collidable = new Stack<bool>();
        private readonly Stack<GameDoorData?> _doors = new Stack<GameDoorData?>();
        private readonly Stack<GameElementData?> _elements =
            new Stack<GameElementData?>();
        private readonly HashSet<string> _visibleElements = new HashSet<string>(StringComparer.Ordinal);

        private Color _currentColor = Color.FromRgb(190, 195, 202);
        private double _currentOpacity = 1.0;
        private int _nextWebElementIndex;

        public RevitGameExportContext(Document document)
        {
            _rootDocument = document ?? throw new ArgumentNullException(nameof(document));
        }

        public GameSceneData Scene => _scene;

        public bool Start()
        {
            _transforms.Clear();
            _documents.Clear();
            _preferredWalkable.Clear();
            _collidable.Clear();
            _doors.Clear();
            _elements.Clear();
            _visibleElements.Clear();
            _nextWebElementIndex = 0;
            _transforms.Push(Transform.Identity);
            _documents.Push(_rootDocument);
            return true;
        }

        public void Finish()
        {
            _scene.VisibleElementCount = _visibleElements.Count;
            int renderTriangles = 0;
            foreach (GameMeshData mesh in _scene.Meshes)
                renderTriangles += mesh.Indices.Count / 3;
            foreach (GameDoorData door in _scene.Doors)
            {
                renderTriangles += door.OpaqueMesh.Indices.Count / 3;
                renderTriangles += door.TransparentMesh.Indices.Count / 3;
            }
            _scene.OriginalRenderTriangleCount = renderTriangles;
        }

        public bool IsCanceled() => false;

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            // 0..15 : 8 conserve des courbes propres. Le moteur DirectX accepte
            // la géométrie détaillée sans la simplification destructive de WPF 3D.
            try { node.LevelOfDetail = 8; } catch { }
            return RenderNodeAction.Proceed;
        }

        public void OnViewEnd(ElementId elementId) { }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            bool preferred = false;
            bool collidable = true;
            GameDoorData? door = null;
            GameElementData? elementData = null;
            Document document = _documents.Count > 0 ? _documents.Peek() : _rootDocument;

            try
            {
                Element element = document.GetElement(elementId);
                string elementKey = CreateElementKey(document, elementId);
                elementData = CreateElementData(document, element, elementKey);
                elementData.WebElementIndex = _nextWebElementIndex++;
                if (element?.Category != null)
                {
                    int categoryId = element.Category.Id.GetIdValue();
                    preferred = IsPreferredWalkableCategory(categoryId);
                    collidable = categoryId != (int)BuiltInCategory.OST_Doors;
                    if (!collidable)
                        door = new GameDoorData(elementKey);
                }

                _visibleElements.Add(elementKey);
            }
            catch
            {
                _visibleElements.Add(document.GetHashCode() + "|" + elementId.GetIdLongValue());
            }

            _preferredWalkable.Push(preferred);
            _collidable.Push(collidable);
            _doors.Push(door);
            _elements.Push(elementData);
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            if (_preferredWalkable.Count > 0)
                _preferredWalkable.Pop();
            if (_collidable.Count > 0)
                _collidable.Pop();
            if (_doors.Count > 0)
            {
                GameDoorData? door = _doors.Pop();
                if (door != null && door.FinalizeGeometry())
                    _scene.Doors.Add(door);
            }
            if (_elements.Count > 0)
            {
                GameElementData? element = _elements.Pop();
                if (element != null && element.HasBounds)
                    _scene.Elements.Add(element);
            }
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            Transform parent = _transforms.Count > 0 ? _transforms.Peek() : Transform.Identity;
            _transforms.Push(parent.Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            if (_transforms.Count > 1)
                _transforms.Pop();
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            Transform parent = _transforms.Count > 0
                ? _transforms.Peek()
                : Transform.Identity;
            Transform linkTransform = Transform.Identity;
            try
            {
                linkTransform = node.GetTransform() ?? Transform.Identity;
            }
            catch { }
            _transforms.Push(parent.Multiply(linkTransform));

            try
            {
                Document linkedDocument = node.GetDocument();
                _documents.Push(linkedDocument ?? _documents.Peek());
            }
            catch
            {
                _documents.Push(_documents.Count > 0 ? _documents.Peek() : _rootDocument);
            }

            // Le polymesh d'un lien est exprimé dans son repère propre. La
            // transformation du LinkNode restitue son placement origine à origine,
            // par coordonnées partagées, ainsi que rotation et dénivelé.
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            if (_documents.Count > 1)
                _documents.Pop();
            if (_transforms.Count > 1)
                _transforms.Pop();
        }

        public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
        public void OnFaceEnd(FaceNode node) { }

        public void OnMaterial(MaterialNode node)
        {
            Autodesk.Revit.DB.Color revitColor = node.Color;
            if (revitColor != null && revitColor.IsValid)
                _currentColor = Color.FromRgb(revitColor.Red, revitColor.Green, revitColor.Blue);
            else
                _currentColor = Color.FromRgb(190, 195, 202);

            try
            {
                _currentOpacity = Math.Max(0.08, Math.Min(1.0, 1.0 - node.Transparency));
            }
            catch
            {
                _currentOpacity = 1.0;
            }
        }

        public void OnPolymesh(PolymeshTopology polymesh)
        {
            IList<XYZ> points = polymesh.GetPoints();
            IList<PolymeshFacet> facets = polymesh.GetFacets();

            Transform transform = _transforms.Count > 0 ? _transforms.Peek() : Transform.Identity;
            bool preferred = _preferredWalkable.Count > 0 && _preferredWalkable.Peek();
            bool collidable = _collidable.Count == 0 || _collidable.Peek();
            GameDoorData? door = _doors.Count > 0 ? _doors.Peek() : null;
            GameElementData? elementData =
                _elements.Count > 0 ? _elements.Peek() : null;

            // Un polymesh Revit partage ses sommets entre plusieurs facettes.
            // Les ajouter une fois au lieu de trois fois par triangle réduit
            // couramment la mémoire et le transfert WPF de 50 à 70 %.
            var transformedPoints = new Point3D[points.Count];
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double minZ = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            double maxZ = double.MinValue;
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                Point3D point = ToPoint3D(transform.OfPoint(points[pointIndex]));
                transformedPoints[pointIndex] = point;
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                minZ = Math.Min(minZ, point.Z);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
                maxZ = Math.Max(maxZ, point.Z);
            }

            if (transformedPoints.Length == 0)
                return;
            elementData?.Include(transformedPoints);

            int chunkX = ToRenderChunk((minX + maxX) * 0.5);
            int chunkY = ToRenderChunk((minY + maxY) * 0.5);
            int chunkZ = ToRenderChunk((minZ + maxZ) * 0.5);
            double effectiveOpacity =
                elementData != null &&
                !string.IsNullOrWhiteSpace(elementData.SelectionTargetKey)
                    ? Math.Min(_currentOpacity, InsulationOpacity)
                    : _currentOpacity;
            bool transparent = effectiveOpacity < 0.995;
            GameMeshData mesh = door != null
                ? door.GetMesh(transparent)
                : GetCurrentMesh(chunkX, chunkY, chunkZ, transparent);
            int renderBaseIndex = mesh.Positions.Count;
            IList<XYZ> revitNormals = null;
            bool hasPointNormals = false;
            try
            {
                hasPointNormals =
                    polymesh.DistributionOfNormals == DistributionOfNormals.AtEachPoint &&
                    polymesh.NumberOfNormals == points.Count;
                if (hasPointNormals)
                    revitNormals = polymesh.GetNormals();
            }
            catch
            {
                hasPointNormals = false;
            }

            byte alpha = (byte)Math.Round(effectiveOpacity * 255.0);
            Color vertexColor = Color.FromArgb(
                alpha,
                _currentColor.R,
                _currentColor.G,
                _currentColor.B);
            foreach (Point3D point in transformedPoints)
            {
                mesh.Positions.Add(point);
                mesh.VertexColors.Add(vertexColor);
                mesh.ElementIndices.Add(elementData?.WebElementIndex ?? -1);
            }

            if (hasPointNormals && revitNormals != null)
            {
                for (int pointIndex = 0; pointIndex < revitNormals.Count; pointIndex++)
                {
                    XYZ transformedNormal = transform.OfVector(revitNormals[pointIndex]);
                    var normal = new Vector3D(
                        transformedNormal.X,
                        transformedNormal.Y,
                        transformedNormal.Z);
                    if (normal.LengthSquared < 1e-18)
                    {
                        mesh.HasCompleteNormals = false;
                        normal = new Vector3D(0, 0, 1);
                    }
                    else
                    {
                        normal.Normalize();
                    }
                    mesh.VertexNormals.Add(normal);
                }
            }
            else
            {
                mesh.HasCompleteNormals = false;
                for (int pointIndex = 0; pointIndex < transformedPoints.Length; pointIndex++)
                    mesh.VertexNormals.Add(new Vector3D(0, 0, 1));
            }

            for (int facetIndex = 0; facetIndex < facets.Count; facetIndex++)
            {
                PolymeshFacet facet = facets[facetIndex];
                Point3D a = transformedPoints[facet.V1];
                Point3D b = transformedPoints[facet.V2];
                Point3D c = transformedPoints[facet.V3];

                Vector3D faceNormal = Vector3D.CrossProduct(b - a, c - a);
                if (faceNormal.LengthSquared < 1e-18)
                    continue;
                faceNormal.Normalize();

                mesh.Indices.Add(renderBaseIndex + facet.V1);
                mesh.Indices.Add(renderBaseIndex + facet.V2);
                mesh.Indices.Add(renderBaseIndex + facet.V3);

                // Les portes restent intégralement visibles, mais leurs vantaux
                // et cadres ne ferment pas artificiellement les circulations.
                // L'ouverture découpée dans le mur reste donc franchissable.
                var selectionTriangle =
                    new GameTriangle(a, b, c, faceNormal, preferred)
                {
                    IsCollisionGeometry = collidable
                };
                elementData?.SelectionTriangles.Add(selectionTriangle);
                if (collidable)
                {
                    _scene.Triangles.Add(selectionTriangle);
                }
            }
        }

        public void OnCurve(CurveNode node) { }
        public void OnPolyline(PolylineNode node) { }
        public void OnRPC(RPCNode node) { }
        public void OnLight(LightNode node) { }

        private GameMeshData GetCurrentMesh(
            int chunkX,
            int chunkY,
            int chunkZ,
            bool transparent)
        {
            // Une seule géométrie par zone et par passe opaque/transparente.
            // Les couleurs exactes restent portées par les sommets : beaucoup
            // moins d'appels GPU, sans quantification des couleurs Revit.
            var key = (chunkX, chunkY, chunkZ, transparent);
            if (_renderBuckets.TryGetValue(key, out GameMeshData mesh))
                return mesh;

            mesh = new GameMeshData
            {
                IsTransparent = transparent
            };
            _renderBuckets.Add(key, mesh);
            _scene.Meshes.Add(mesh);
            return mesh;
        }

        private static int ToRenderChunk(double coordinate)
        {
            return (int)Math.Floor(coordinate / RenderChunkSize);
        }

        private static Point3D ToPoint3D(XYZ point)
        {
            return new Point3D(point.X, point.Y, point.Z);
        }

        private static string CreateElementKey(Document document, ElementId elementId)
        {
            string documentKey = document.PathName;
            if (string.IsNullOrWhiteSpace(documentKey))
                documentKey = document.Title;
            return documentKey + "|" + elementId.GetIdLongValue();
        }

        private static GameElementData CreateElementData(
            Document document,
            Element? element,
            string key)
        {
            var data = new GameElementData
            {
                Key = key,
                ElementId = element?.Id.GetIdLongValue() ?? 0L,
                Name = SafeText(() => element?.Name),
                Category = SafeText(() => element?.Category?.Name),
                DocumentTitle = document.Title ?? string.Empty
            };

            if (element == null)
                return data;

            // Le calorifuge reste affiché, mais il n'a aucune utilité dans
            // la fiche fonctionnelle. Un clic sur son volume doit donc viser
            // directement la canalisation ou l'accessoire qu'il enveloppe.
            if (element is InsulationLiningBase insulation)
            {
                try
                {
                    ElementId hostId = insulation.HostElementId;
                    if (hostId != null && hostId != ElementId.InvalidElementId)
                        data.SelectionTargetKey = CreateElementKey(document, hostId);
                }
                catch { }
            }

            try
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                    data.TypeName = document.GetElement(typeId)?.Name ?? string.Empty;
            }
            catch { }

            try
            {
                ElementId levelId = element.LevelId;
                if (levelId != ElementId.InvalidElementId)
                    data.LevelName = document.GetElement(levelId)?.Name ?? string.Empty;
            }
            catch { }

            if (string.IsNullOrWhiteSpace(data.LevelName))
            {
                try
                {
                    Parameter levelParameter =
                        element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ??
                        element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    data.LevelName = levelParameter?.AsValueString() ?? string.Empty;
                }
                catch { }
            }

            AddWebProperty(data, "Catégorie", data.Category);
            AddWebProperty(data, "Type", data.TypeName);
            AddWebProperty(data, "Niveau", data.LevelName);
            foreach (Parameter parameter in element.Parameters)
            {
                string name = SafeText(() => parameter.Definition?.Name);
                if (!IsAllowedWebParameter(name))
                    continue;
                string value = SafeText(() => parameter.AsValueString());
                if (string.IsNullOrWhiteSpace(value))
                    value = SafeText(() => parameter.AsString());
                AddWebProperty(data, name, value);
            }

            return data;
        }

        private static void AddWebProperty(
            GameElementData data,
            string name,
            string value)
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
                data.WebProperties[name.Trim()] = value.Trim();
        }

        private static bool IsAllowedWebParameter(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string normalized = name.ToLowerInvariant();
            string[] allowed =
            {
                "diam", "débit", "debit", "flow", "pression", "pressure",
                "système", "system", "fabricant", "manufacturer", "matériau",
                "materiau", "material", "repère", "repere", "mark", "comment",
                "taille", "size", "classification"
            };
            return allowed.Any(candidate => normalized.Contains(candidate));
        }

        private static string SafeText(Func<string?> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static bool IsPreferredWalkableCategory(int categoryId)
        {
            return categoryId == (int)BuiltInCategory.OST_Floors
                || categoryId == (int)BuiltInCategory.OST_Stairs
                || categoryId == (int)BuiltInCategory.OST_StairsRuns
                || categoryId == (int)BuiltInCategory.OST_StairsLandings
                || categoryId == (int)BuiltInCategory.OST_Ramps
                || categoryId == (int)BuiltInCategory.OST_Topography
                || categoryId == (int)BuiltInCategory.OST_Toposolid
                || categoryId == (int)BuiltInCategory.OST_BuildingPad
                || categoryId == (int)BuiltInCategory.OST_Site;
        }
    }

    internal static class RevitGameSceneExporter
    {
        public static GameSceneData Export(Document document, View3D view)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (view == null) throw new ArgumentNullException(nameof(view));

            var context = new RevitGameExportContext(document);
            using (var exporter = new CustomExporter(document, context))
            {
                exporter.IncludeGeometricObjects = false;
                exporter.ShouldStopOnError = false;
                exporter.Export(view);
            }

            GameSceneData scene = context.Scene;
            scene.ViewName = view.Name;

            // Les connecteurs Revit doivent être lus tant que la commande est
            // encore sur le thread Revit. La fenêtre WPF ne recevra ensuite que
            // le graphe immuable et les états de simulation temporaires.
            try
            {
                GameRuntimeDiagnostics.Write("Extraction MEP - début");
                scene.MepGraph = RevitGameMepExtractor.Extract(document, scene);
                GameRuntimeDiagnostics.Write(
                    "Extraction MEP terminée : " +
                    scene.MepGraph.Elements.Count + " élément(s), " +
                    scene.MepGraph.Connectors.Count + " connecteur(s), " +
                    scene.MepGraph.Connections.Count + " liaison(s)");
            }
            catch (Exception exception)
            {
                // Le mode MEP enrichit la maquette jouable, mais une famille
                // incompatible avec une version de Revit ne doit jamais bloquer
                // l'ouverture de la scène principale.
                GameRuntimeDiagnostics.Write("Extraction MEP interrompue", exception);
                scene.MepGraph = new GameMepGraphData
                {
                    ExtractionError = exception.Message,
                    DocumentTitle = SafeDocumentTitle(document)
                };
            }

            // La persistance est une fonction annexe. Une incompatibilité de
            // stockage ne doit surtout pas remplacer un graphe MEP valide par
            // un graphe vide, notamment lors du passage de Revit 2023 à 2024.
            if (scene.MepGraph.HasData)
            {
                try
                {
                    GameMepScenarioRestoreResult restore =
                        GameMepScenarioStore.Restore(scene.MepGraph);
                    GameRuntimeDiagnostics.Write(
                        "Scénario MEP restauré : " +
                        restore.RestoredSources + " source(s), " +
                        restore.RestoredValves + " vanne(s), " +
                        restore.SkippedEntries + " entrée(s) ignorée(s)");
                }
                catch (Exception exception)
                {
                    scene.MepGraph.ScenarioPersistenceError = exception.Message;
                    GameRuntimeDiagnostics.Write(
                        "Restauration MEP ignorée, graphe conservé",
                        exception);
                }
            }

            ViewOrientation3D orientation = view.GetOrientation();
            XYZ eye = orientation.EyePosition;
            XYZ forward = orientation.ForwardDirection;
            scene.NormalizeCoordinates(
                new Point3D(eye.X, eye.Y, eye.Z),
                new Vector3D(forward.X, forward.Y, forward.Z));
            GameMepWebPackage.PrepareStaticAssets(scene);

            return scene;
        }

        private static string SafeDocumentTitle(Document document)
        {
            try { return document.Title ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}

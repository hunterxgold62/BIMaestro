using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;

namespace BIMaestro.VideoGames
{
    /// <summary>
    /// Superposition GPU dédiée au MEP. Les lignes ne sont reconstruites
    /// qu'après une action fonctionnelle ; seules les positions du pool de
    /// particules sont modifiées pendant le rendu.
    /// </summary>
    internal sealed class GameMepFlowRenderer
    {
        private const double ParticleUpdateInterval = 1.0 / 30.0;
        private const double MaximumRenderDistance = 420.0;
        private const double ParticleSpacing = 18.0;
        private const double FlowSpeed = 2.4;
        private const double ArrowLength = 0.90;
        private const double ArrowHeadLength = 0.30;
        private const double ArrowHeadWidth = 0.22;
        private const int MinimumParticleBudget = 350;
        private const int MaximumParticleBudget = 4000;
        private const int MaximumLineSegmentCount = 150000;
        private const int MaximumValveMarkerCount = 2500;
        private const int ValveRingSegmentCount = 16;

        private sealed class Particle
        {
            public GameMepPathData? Path { get; set; }
            public Point3D FixedPosition { get; set; }
            public double Phase { get; set; }
            public SharpDX.Color4 Color { get; set; }
        }

        private readonly GameMepGraphData _graph;
        private readonly Viewport3DX _viewport;
        private readonly LineGeometryModel3D _lineModel;
        private readonly LineGeometryModel3D _arrowModel;
        private readonly LineGeometryModel3D _valveMarkerModel;
        private readonly LineGeometryModel3D _highlightModel;
        private readonly LineGeometryModel3D _hoverModel;
        private readonly LineGeometryModel3D _directionPreviewModel;
        private readonly LineGeometryModel3D _traceModel;
        private readonly IList<Particle> _particles = new List<Particle>();
        private LineGeometry3D _lineGeometry = new LineGeometry3D();
        private LineGeometry3D _arrowGeometry = new LineGeometry3D();
        private bool _lineModelAttached;
        private bool _arrowModelAttached;
        private bool _valveMarkerModelAttached;
        private bool _highlightModelAttached;
        private bool _hoverModelAttached;
        private bool _directionPreviewModelAttached;
        private bool _traceModelAttached;
        private int _particleBudget = 2000;
        private double _lastParticleUpdateSeconds = double.MinValue;
        private Point3D _lastParticleCameraPosition;
        private Point3D _lastValveCameraPosition;
        private bool _particleDefinitionsDirty = true;
        private string _highlightedElementKey = string.Empty;
        private string _hoveredElementKey = string.Empty;
        private GameMepNetworkTraceResult? _networkTrace;

        public GameMepFlowRenderer(GameMepGraphData graph, Viewport3DX viewport)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));

            _lineModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 4.2,
                Smoothness = 1.0,
                // Le flux est au centre du tuyau. Le biais constant le fait
                // ressortir de la paroi et le biais de pente évite sa
                // disparition lorsque la canalisation est vue en angle rasant.
                DepthBias = -30000,
                SlopeScaledDepthBias = -3.5,
                RenderOrder = 2000,
                FixedSize = true,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };
            _arrowModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 3.2,
                Smoothness = 1.0,
                FixedSize = true,
                DepthBias = -34000,
                SlopeScaledDepthBias = -3.5,
                RenderOrder = 2001,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };
            _valveMarkerModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 4.6,
                Smoothness = 1.0,
                FixedSize = true,
                DepthBias = -39000,
                SlopeScaledDepthBias = -4.0,
                RenderOrder = 2002,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };
            _highlightModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 11.0,
                Smoothness = 1.0,
                FixedSize = true,
                DepthBias = -43000,
                SlopeScaledDepthBias = -4.5,
                RenderOrder = 2003,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };
            _hoverModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 6.0,
                Smoothness = 1.0,
                FixedSize = true,
                DepthBias = -44000,
                SlopeScaledDepthBias = -4.5,
                RenderOrder = 2004,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };
            _directionPreviewModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 8.0,
                Smoothness = 1.0,
                FixedSize = true,
                DepthBias = -45000,
                SlopeScaledDepthBias = -5.0,
                RenderOrder = 2005,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };
            _traceModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 8.5,
                Smoothness = 1.0,
                FixedSize = true,
                DepthBias = -41000,
                SlopeScaledDepthBias = -4.2,
                RenderOrder = 2003,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };

            // Les modèles ne sont pas encore attachés. HelixToolkit 2023 peut
            // tenter d'initialiser immédiatement un modèle dont Geometry est
            // null, même si IsRendering vaut false. Ils seront ajoutés après la
            // construction d'un buffer réellement exploitable.
        }

        public bool Enabled { get; private set; }
        public bool ValveMarkersEnabled { get; private set; }
        public bool Paused { get; set; }
        public int ParticleCount => _particles.Count;
        public double LastAnimationMilliseconds { get; private set; }
        public void SetEnabled(bool enabled, Point3D cameraPosition)
        {
            Enabled = enabled && _graph.HasData;
            if (!Enabled)
            {
                _lineModel.IsRendering = false;
                _arrowModel.IsRendering = false;
                return;
            }

            // Construire d'abord les géométries, puis seulement autoriser leur
            // rendu. Helix/DirectX ne doit jamais recevoir un buffer vide actif.
            RefreshState(cameraPosition);
            AttachReadyModels();
        }

        public void SetValveMarkersEnabled(bool enabled, Point3D cameraPosition)
        {
            ValveMarkersEnabled = enabled && _graph.HasData;
            if (!ValveMarkersEnabled)
            {
                _valveMarkerModel.IsRendering = false;
                return;
            }
            RebuildValveMarkers(cameraPosition);
            AttachReadyModels();
            _viewport.InvalidateRender();
        }

        public void SetHighlightedElement(string elementKey, Point3D cameraPosition)
        {
            string next = elementKey ?? string.Empty;
            if (string.Equals(_highlightedElementKey, next, StringComparison.Ordinal))
                return;
            _highlightedElementKey = next;
            RebuildHighlightedElement();
            if (ValveMarkersEnabled)
            {
                RebuildValveMarkers(cameraPosition);
                AttachReadyModels();
                _viewport.InvalidateRender();
            }
            AttachReadyModels();
            _viewport.InvalidateRender();
        }

        public bool SetHoveredElement(string elementKey)
        {
            string next = elementKey ?? string.Empty;
            if (string.Equals(_hoveredElementKey, next, StringComparison.Ordinal))
                return _hoverModel.IsRendering;
            _hoveredElementKey = next;
            bool visible = RebuildHoveredElement();
            AttachReadyModels();
            _viewport.InvalidateRender();
            return visible;
        }

        public bool SetDirectionPreview(
            string elementKey,
            bool? forward,
            Point3D cameraPosition)
        {
            GameMepElementData? element = _graph.FindElement(elementKey ?? string.Empty);
            GameMepPathData? path = element?.Paths.FirstOrDefault(candidate =>
                candidate.StartConnector >= 0 &&
                candidate.EndConnector >= 0 &&
                candidate.StartConnector != candidate.EndConnector &&
                candidate.Points.Count >= 2);
            if (path == null)
            {
                _directionPreviewModel.IsRendering = false;
                return false;
            }

            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var colors = new Color4Collection();
            var aColor = new SharpDX.Color4(0.22f, 1.0f, 0.55f, 1.0f);
            var bColor = new SharpDX.Color4(0.20f, 0.78f, 1.0f, 1.0f);
            var arrowColor = new SharpDX.Color4(1.0f, 0.76f, 0.12f, 1.0f);
            Point3D a = path.Points[0];
            Point3D b = path.Points[path.Points.Count - 1];
            AppendEndpointMarker(a, 'A', cameraPosition, aColor,
                positions, indices, colors);
            AppendEndpointMarker(b, 'B', cameraPosition, bColor,
                positions, indices, colors);

            if (forward.HasValue)
            {
                IList<Point3D> points = forward.Value
                    ? path.Points
                    : path.Points.Reverse().ToList();
                for (int index = 1; index < points.Count; index++)
                {
                    AppendMarkerSegment(points[index - 1], points[index], arrowColor,
                        positions, indices, colors);
                }
                AppendPreviewArrowHead(points, cameraPosition, arrowColor,
                    positions, indices, colors);
            }

            var geometry = new LineGeometry3D
            {
                Positions = positions,
                Indices = indices,
                Colors = colors,
                IsDynamic = false,
                PreDefinedVertexCount = positions.Count,
                PreDefinedIndexCount = indices.Count
            };
            geometry.UpdateBounds();
            _directionPreviewModel.Geometry = geometry;
            _directionPreviewModel.IsRendering = positions.Count >= 2;
            AttachReadyModels();
            _viewport.InvalidateRender();
            return _directionPreviewModel.IsRendering;
        }

        public void ClearDirectionPreview()
        {
            if (!_directionPreviewModel.IsRendering)
                return;
            _directionPreviewModel.IsRendering = false;
            _viewport.InvalidateRender();
        }

        public void SetNetworkTrace(
            GameMepNetworkTraceResult? trace,
            Point3D cameraPosition)
        {
            _networkTrace = trace != null && trace.ElementKeys.Count > 0
                ? trace
                : null;
            RebuildLines(cameraPosition);
            RebuildNetworkTrace();
            _particleDefinitionsDirty = true;
            if (Enabled)
                RebuildParticles(cameraPosition);
            AttachReadyModels();
            _viewport.InvalidateRender();
        }

        public void RefreshState(Point3D cameraPosition)
        {
            if (!_graph.HasData)
            {
                _lineModel.IsRendering = false;
                _arrowModel.IsRendering = false;
                _valveMarkerModel.IsRendering = false;
                _traceModel.IsRendering = false;
                return;
            }
            RebuildLines(cameraPosition);
            RebuildNetworkTrace();
            if (ValveMarkersEnabled)
                RebuildValveMarkers(cameraPosition);
            _particleDefinitionsDirty = true;
            if (Enabled)
                RebuildParticles(cameraPosition);
            AttachReadyModels();
            _viewport.InvalidateRender();
        }

        public void Update(double totalSeconds, Point3D cameraPosition)
        {
            if ((!Enabled && !ValveMarkersEnabled) || Paused ||
                totalSeconds - _lastParticleUpdateSeconds < ParticleUpdateInterval)
            {
                return;
            }

            long startTimestamp = Stopwatch.GetTimestamp();
            _lastParticleUpdateSeconds = totalSeconds;
            if (ValveMarkersEnabled &&
                (cameraPosition - _lastValveCameraPosition).LengthSquared > 64.0)
            {
                RebuildValveMarkers(cameraPosition);
            }
            if (!Enabled)
                return;
            bool cameraMoved =
                (cameraPosition - _lastParticleCameraPosition).LengthSquared > 900.0;
            if (_particleDefinitionsDirty || cameraMoved)
            {
                RebuildParticles(cameraPosition);
            }

            for (int index = 0; index < _particles.Count; index++)
            {
                Particle particle = _particles[index];
                if (particle.Path == null)
                {
                    WriteFixedMarker(index, particle.FixedPosition);
                }
                else
                {
                    GameMepPathData path = particle.Path;
                    double direction = path.FlowForward ? 1.0 : -1.0;
                    double normalizedSpeed = path.Length <= 1e-6
                        ? 0.0
                        : FlowSpeed / path.Length;
                    double progress = particle.Phase +
                        totalSeconds * normalizedSpeed * direction;
                    progress -= Math.Floor(progress);
                    WriteMovingArrow(index, path, progress, cameraPosition);
                }
            }

            if (_particles.Count > 0)
            {
                _arrowGeometry.UpdateVertices();
                _arrowGeometry.UpdateBounds();
            }
            LastAnimationMilliseconds =
                (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 /
                Stopwatch.Frequency;
        }

        public void AdaptParticleBudget(double framesPerSecond)
        {
            int previous = _particleBudget;
            if (framesPerSecond > 0.0 && framesPerSecond < 48.0)
                _particleBudget = Math.Max(MinimumParticleBudget, (int)(_particleBudget * 0.78));
            else if (framesPerSecond >= 68.0)
                _particleBudget = Math.Min(MaximumParticleBudget, _particleBudget + 160);

            if (_particleBudget != previous &&
                (_particles.Count > _particleBudget || _particles.Count >= previous))
            {
                _particleDefinitionsDirty = true;
            }
        }

        public void Dispose()
        {
            if (_lineModelAttached)
            {
                try { _viewport.Items.Remove(_lineModel); } catch { }
                _lineModelAttached = false;
            }
            if (_arrowModelAttached)
            {
                try { _viewport.Items.Remove(_arrowModel); } catch { }
                _arrowModelAttached = false;
            }
            if (_valveMarkerModelAttached)
            {
                try { _viewport.Items.Remove(_valveMarkerModel); } catch { }
                _valveMarkerModelAttached = false;
            }
            if (_highlightModelAttached)
            {
                try { _viewport.Items.Remove(_highlightModel); } catch { }
                _highlightModelAttached = false;
            }
            if (_hoverModelAttached)
            {
                try { _viewport.Items.Remove(_hoverModel); } catch { }
                _hoverModelAttached = false;
            }
            if (_directionPreviewModelAttached)
            {
                try { _viewport.Items.Remove(_directionPreviewModel); } catch { }
                _directionPreviewModelAttached = false;
            }
            if (_traceModelAttached)
            {
                try { _viewport.Items.Remove(_traceModel); } catch { }
                _traceModelAttached = false;
            }
        }

        private void AttachReadyModels()
        {
            if (!_lineModelAttached && _lineModel.Geometry != null)
            {
                _viewport.Items.Add(_lineModel);
                _lineModelAttached = true;
            }
            if (!_arrowModelAttached && _arrowModel.Geometry != null)
            {
                _viewport.Items.Add(_arrowModel);
                _arrowModelAttached = true;
            }
            if (!_valveMarkerModelAttached && _valveMarkerModel.Geometry != null)
            {
                _viewport.Items.Add(_valveMarkerModel);
                _valveMarkerModelAttached = true;
            }
            if (!_highlightModelAttached && _highlightModel.Geometry != null)
            {
                _viewport.Items.Add(_highlightModel);
                _highlightModelAttached = true;
            }
            if (!_hoverModelAttached && _hoverModel.Geometry != null)
            {
                _viewport.Items.Add(_hoverModel);
                _hoverModelAttached = true;
            }
            if (!_directionPreviewModelAttached && _directionPreviewModel.Geometry != null)
            {
                _viewport.Items.Add(_directionPreviewModel);
                _directionPreviewModelAttached = true;
            }
            if (!_traceModelAttached && _traceModel.Geometry != null)
            {
                _viewport.Items.Add(_traceModel);
                _traceModelAttached = true;
            }
        }

        private void RebuildNetworkTrace()
        {
            if (_networkTrace == null || _networkTrace.ElementKeys.Count == 0)
            {
                _traceModel.IsRendering = false;
                return;
            }

            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var colors = new Color4Collection();
            SharpDX.Color4 color = GetTraceColor(_networkTrace.Mode,
                !string.IsNullOrWhiteSpace(
                    _networkTrace.SelectedBranchElementKey));
            int segmentCount = 0;
            foreach (GameMepPathData path in _graph.Elements
                .Where(element => _networkTrace.ElementKeys.Contains(element.Key))
                .SelectMany(element => element.Paths)
                .Where(path => path.IsVisible &&
                    path.Points.Count >= 2 &&
                    (_graph.FindSystem(path.SystemKey)?.IsVisible ?? true))
                .OrderBy(path => path.ElementKey, StringComparer.Ordinal))
            {
                for (int index = 1; index < path.Points.Count; index++)
                {
                    if (segmentCount >= MaximumLineSegmentCount)
                        break;
                    if (!IsFinite(path.Points[index - 1]) ||
                        !IsFinite(path.Points[index]))
                    {
                        continue;
                    }
                    AppendMarkerSegment(
                        path.Points[index - 1],
                        path.Points[index],
                        color,
                        positions,
                        indices,
                        colors);
                    segmentCount++;
                }
                if (segmentCount >= MaximumLineSegmentCount)
                    break;
            }

            if (positions.Count < 2)
            {
                _traceModel.IsRendering = false;
                return;
            }
            var geometry = new LineGeometry3D
            {
                Positions = positions,
                Indices = indices,
                Colors = colors,
                IsDynamic = false,
                PreDefinedVertexCount = positions.Count,
                PreDefinedIndexCount = indices.Count
            };
            geometry.UpdateBounds();
            _traceModel.Geometry = geometry;
            _traceModel.IsRendering = true;
        }

        private void RebuildHighlightedElement()
        {
            if (string.IsNullOrWhiteSpace(_highlightedElementKey))
            {
                _highlightModel.IsRendering = false;
                return;
            }
            GameMepElementData? element = _graph.FindElement(_highlightedElementKey);
            if (element == null || !element.IsVisible)
            {
                _highlightModel.IsRendering = false;
                return;
            }

            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var colors = new Color4Collection();
            var color = new SharpDX.Color4(1.0f, 0.08f, 0.82f, 1.0f);
            var beaconColor = new SharpDX.Color4(0.20f, 1.0f, 1.0f, 1.0f);
            var referencePoints = new List<Point3D>();
            foreach (GameMepPathData path in element.Paths)
            {
                foreach (Point3D point in path.Points.Where(IsFinite))
                    referencePoints.Add(point);
                for (int index = 1; index < path.Points.Count; index++)
                {
                    AppendMarkerSegment(
                        path.Points[index - 1],
                        path.Points[index],
                        color,
                        positions,
                        indices,
                        colors);
                }
            }
            if (positions.Count == 0)
            {
                IList<Point3D> connectorPoints = element.ConnectorIndices
                    .Where(index => index >= 0 && index < _graph.Connectors.Count)
                    .Select(index => _graph.Connectors[index].Position)
                    .ToList();
                foreach (Point3D point in connectorPoints.Where(IsFinite))
                    referencePoints.Add(point);
                if (connectorPoints.Count > 0)
                {
                    var center = new Point3D(
                        connectorPoints.Average(point => point.X),
                        connectorPoints.Average(point => point.Y),
                        connectorPoints.Average(point => point.Z));
                    const double radius = 0.75;
                    AppendMarkerSegment(
                        center + new Vector3D(-radius, 0, 0),
                        center + new Vector3D(radius, 0, 0),
                        color, positions, indices, colors);
                    AppendMarkerSegment(
                        center + new Vector3D(0, -radius, 0),
                        center + new Vector3D(0, radius, 0),
                        color, positions, indices, colors);
                    AppendMarkerSegment(
                        center + new Vector3D(0, 0, -radius),
                        center + new Vector3D(0, 0, radius),
                        color, positions, indices, colors);
                }
            }

            // Un simple trait se perd facilement au milieu des couleurs MEP.
            // Le balisage combine trois anneaux et une balise verticale de
            // taille minimale importante, toujours rendus au premier plan.
            if (referencePoints.Count == 0)
            {
                referencePoints.AddRange(element.ConnectorIndices
                    .Where(index => index >= 0 && index < _graph.Connectors.Count)
                    .Select(index => _graph.Connectors[index].Position)
                    .Where(IsFinite));
            }
            if (referencePoints.Count > 0)
            {
                var center = new Point3D(
                    referencePoints.Average(point => point.X),
                    referencePoints.Average(point => point.Y),
                    referencePoints.Average(point => point.Z));
                double extent = referencePoints.Max(point =>
                    (point - center).Length);
                double radius = Math.Max(2.5, Math.Min(6.0, extent + 1.25));
                AppendDiagnosticRing(center, radius, 0, 1, color,
                    positions, indices, colors);
                AppendDiagnosticRing(center, radius, 0, 2, color,
                    positions, indices, colors);
                AppendDiagnosticRing(center, radius, 1, 2, color,
                    positions, indices, colors);
                AppendMarkerSegment(
                    center + new Vector3D(0, 0, -radius * 1.7),
                    center + new Vector3D(0, 0, radius * 2.2),
                    beaconColor, positions, indices, colors);
                AppendMarkerSegment(
                    center + new Vector3D(-radius * 1.35, 0, 0),
                    center + new Vector3D(radius * 1.35, 0, 0),
                    beaconColor, positions, indices, colors);
                AppendMarkerSegment(
                    center + new Vector3D(0, -radius * 1.35, 0),
                    center + new Vector3D(0, radius * 1.35, 0),
                    beaconColor, positions, indices, colors);
            }

            if (positions.Count < 2)
            {
                _highlightModel.IsRendering = false;
                return;
            }
            var geometry = new LineGeometry3D
            {
                Positions = positions,
                Indices = indices,
                Colors = colors,
                IsDynamic = false,
                PreDefinedVertexCount = positions.Count,
                PreDefinedIndexCount = indices.Count
            };
            geometry.UpdateBounds();
            _highlightModel.Geometry = geometry;
            _highlightModel.IsRendering = true;
        }

        private bool RebuildHoveredElement()
        {
            if (string.IsNullOrWhiteSpace(_hoveredElementKey))
            {
                _hoverModel.IsRendering = false;
                return false;
            }
            GameMepElementData? element = _graph.FindElement(_hoveredElementKey);
            if (element == null || !element.IsVisible)
            {
                _hoverModel.IsRendering = false;
                return false;
            }

            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var colors = new Color4Collection();
            var color = new SharpDX.Color4(0.48f, 1.0f, 0.72f, 1.0f);
            foreach (GameMepPathData path in element.Paths)
            {
                for (int index = 1; index < path.Points.Count; index++)
                {
                    AppendMarkerSegment(path.Points[index - 1], path.Points[index],
                        color, positions, indices, colors);
                }
            }
            if (positions.Count < 2)
            {
                _hoverModel.IsRendering = false;
                return false;
            }
            var geometry = new LineGeometry3D
            {
                Positions = positions,
                Indices = indices,
                Colors = colors,
                IsDynamic = false,
                PreDefinedVertexCount = positions.Count,
                PreDefinedIndexCount = indices.Count
            };
            geometry.UpdateBounds();
            _hoverModel.Geometry = geometry;
            _hoverModel.IsRendering = true;
            return true;
        }

        private static void AppendEndpointMarker(
            Point3D center,
            char label,
            Point3D cameraPosition,
            SharpDX.Color4 color,
            Vector3Collection positions,
            IntCollection indices,
            Color4Collection colors)
        {
            const double radius = 0.42;
            AppendMarkerSegment(center + new Vector3D(-radius, 0, 0),
                center + new Vector3D(radius, 0, 0), color, positions, indices, colors);
            AppendMarkerSegment(center + new Vector3D(0, -radius, 0),
                center + new Vector3D(0, radius, 0), color, positions, indices, colors);
            AppendMarkerSegment(center + new Vector3D(0, 0, -radius),
                center + new Vector3D(0, 0, radius), color, positions, indices, colors);

            Vector3D view = cameraPosition - center;
            if (view.LengthSquared < 1e-8)
                view = new Vector3D(0, -1, 0);
            view.Normalize();
            Vector3D right = Vector3D.CrossProduct(new Vector3D(0, 0, 1), view);
            if (right.LengthSquared < 1e-8)
                right = new Vector3D(1, 0, 0);
            right.Normalize();
            Vector3D up = Vector3D.CrossProduct(view, right);
            up.Normalize();
            const double halfWidth = 0.36;
            const double halfHeight = 0.54;
            Point3D labelCenter = center + up * 0.82;
            if (label == 'A')
            {
                Point3D left = labelCenter - right * halfWidth - up * halfHeight;
                Point3D top = labelCenter + up * halfHeight;
                Point3D rightBottom = labelCenter + right * halfWidth - up * halfHeight;
                AppendMarkerSegment(left, top, color, positions, indices, colors);
                AppendMarkerSegment(top, rightBottom, color, positions, indices, colors);
                AppendMarkerSegment(labelCenter - right * 0.20,
                    labelCenter + right * 0.20, color, positions, indices, colors);
            }
            else
            {
                Point3D bottomLeft = labelCenter - right * halfWidth - up * halfHeight;
                Point3D topLeft = labelCenter - right * halfWidth + up * halfHeight;
                Point3D middleLeft = labelCenter - right * halfWidth;
                Point3D topRight = labelCenter + right * halfWidth + up * 0.28;
                Point3D bottomRight = labelCenter + right * halfWidth - up * 0.28;
                AppendMarkerSegment(bottomLeft, topLeft, color, positions, indices, colors);
                AppendMarkerSegment(topLeft, topRight, color, positions, indices, colors);
                AppendMarkerSegment(topRight, middleLeft, color, positions, indices, colors);
                AppendMarkerSegment(middleLeft, bottomRight, color, positions, indices, colors);
                AppendMarkerSegment(bottomRight, bottomLeft, color, positions, indices, colors);
            }
        }

        private static void AppendPreviewArrowHead(
            IList<Point3D> points,
            Point3D cameraPosition,
            SharpDX.Color4 color,
            Vector3Collection positions,
            IntCollection indices,
            Color4Collection colors)
        {
            if (points.Count < 2)
                return;
            int segment = Math.Max(1, points.Count / 2);
            Point3D tip = points[segment];
            Vector3D direction = tip - points[segment - 1];
            if (direction.LengthSquared < 1e-8)
                return;
            direction.Normalize();
            Vector3D view = cameraPosition - tip;
            if (view.LengthSquared < 1e-8)
                view = new Vector3D(0, 0, 1);
            view.Normalize();
            Vector3D side = Vector3D.CrossProduct(direction, view);
            if (side.LengthSquared < 1e-8)
                side = Vector3D.CrossProduct(direction, new Vector3D(0, 0, 1));
            if (side.LengthSquared < 1e-8)
                side = new Vector3D(1, 0, 0);
            side.Normalize();
            Point3D basePoint = tip - direction * 0.7;
            AppendMarkerSegment(tip, basePoint + side * 0.34, color,
                positions, indices, colors);
            AppendMarkerSegment(tip, basePoint - side * 0.34, color,
                positions, indices, colors);
        }

        private static void AppendDiagnosticRing(
            Point3D center,
            double radius,
            int firstAxis,
            int secondAxis,
            SharpDX.Color4 color,
            Vector3Collection positions,
            IntCollection indices,
            Color4Collection colors)
        {
            const int segmentCount = 20;
            Point3D previous = RingPoint(
                center, radius, firstAxis, secondAxis, 0.0);
            for (int segment = 1; segment <= segmentCount; segment++)
            {
                double angle = Math.PI * 2.0 * segment / segmentCount;
                Point3D next = RingPoint(
                    center, radius, firstAxis, secondAxis, angle);
                AppendMarkerSegment(previous, next, color,
                    positions, indices, colors);
                previous = next;
            }
        }

        private static Point3D RingPoint(
            Point3D center,
            double radius,
            int firstAxis,
            int secondAxis,
            double angle)
        {
            double[] coordinates = { center.X, center.Y, center.Z };
            coordinates[firstAxis] += Math.Cos(angle) * radius;
            coordinates[secondAxis] += Math.Sin(angle) * radius;
            return new Point3D(coordinates[0], coordinates[1], coordinates[2]);
        }

        private void RebuildValveMarkers(Point3D cameraPosition)
        {
            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var colors = new Color4Collection();
            var candidates = _graph.Valves
                .Where(valve => valve.IsEnabledAsValve)
                .Select(valve => new
                {
                    Valve = valve,
                    Element = _graph.FindElement(valve.ElementKey)
                })
                .Where(item => item.Element != null && item.Element.IsVisible &&
                    (_graph.FindSystem(item.Element.SystemKey)?.IsVisible ?? true))
                .Select(item => new
                {
                    item.Valve,
                    Element = item.Element!,
                    Center = GetElementCenter(item.Element!),
                })
                .Where(item => IsFinite(item.Center))
                .Select(item => new
                {
                    item.Valve,
                    item.Element,
                    item.Center,
                    Distance = (item.Center - cameraPosition).LengthSquared
                })
                .Where(item => item.Distance <=
                    MaximumRenderDistance * MaximumRenderDistance)
                .OrderBy(item => item.Distance)
                .Take(MaximumValveMarkerCount);

            foreach (var item in candidates)
            {
                Vector3D view = cameraPosition - item.Center;
                double distance = view.Length;
                if (distance < 1e-5)
                    view = new Vector3D(1, 0, 0);
                else
                    view.Normalize();
                Vector3D right = Vector3D.CrossProduct(new Vector3D(0, 0, 1), view);
                if (right.LengthSquared < 1e-8)
                    right = new Vector3D(1, 0, 0);
                else
                    right.Normalize();
                Vector3D up = Vector3D.CrossProduct(view, right);
                up.Normalize();
                double radius = Math.Max(0.42, Math.Min(1.65, distance * 0.008));
                SharpDX.Color4 color = GetValveMarkerColor(item.Valve, item.Element);

                for (int segment = 0; segment < ValveRingSegmentCount; segment++)
                {
                    double firstAngle = segment * Math.PI * 2.0 /
                        ValveRingSegmentCount;
                    double secondAngle = (segment + 1) * Math.PI * 2.0 /
                        ValveRingSegmentCount;
                    AppendMarkerSegment(
                        item.Center + right * (Math.Cos(firstAngle) * radius) +
                            up * (Math.Sin(firstAngle) * radius),
                        item.Center + right * (Math.Cos(secondAngle) * radius) +
                            up * (Math.Sin(secondAngle) * radius),
                        color,
                        positions,
                        indices,
                        colors);
                }

                if (item.Valve.IsClosed)
                {
                    double crossRadius = radius * 0.72;
                    AppendMarkerSegment(
                        item.Center - right * crossRadius - up * crossRadius,
                        item.Center + right * crossRadius + up * crossRadius,
                        color, positions, indices, colors);
                    AppendMarkerSegment(
                        item.Center - right * crossRadius + up * crossRadius,
                        item.Center + right * crossRadius - up * crossRadius,
                        color, positions, indices, colors);
                }
            }

            var geometry = new LineGeometry3D
            {
                Positions = positions,
                Indices = indices,
                Colors = colors,
                IsDynamic = false,
                PreDefinedVertexCount = positions.Count,
                PreDefinedIndexCount = indices.Count
            };
            if (positions.Count >= 2 && indices.Count >= 2)
            {
                geometry.UpdateBounds();
                _valveMarkerModel.Geometry = geometry;
                _valveMarkerModel.IsRendering = ValveMarkersEnabled;
            }
            else
            {
                _valveMarkerModel.IsRendering = false;
            }
            _lastValveCameraPosition = cameraPosition;
        }

        private SharpDX.Color4 GetValveMarkerColor(
            GameMepValveData valve,
            GameMepElementData element)
        {
            if (string.Equals(
                    valve.ElementKey,
                    _highlightedElementKey,
                    StringComparison.Ordinal))
            {
                return new SharpDX.Color4(0.92f, 0.98f, 1.0f, 1.0f);
            }
            if (valve.IsClosed)
                return new SharpDX.Color4(1.0f, 0.08f, 0.05f, 1.0f);
            if (valve.Confidence == GameMepConfidence.Low &&
                !valve.WasManuallyOverridden)
            {
                return new SharpDX.Color4(1.0f, 0.66f, 0.10f, 1.0f);
            }
            if (element.FlowState == GameMepFlowState.Isolated)
                return new SharpDX.Color4(0.48f, 0.54f, 0.62f, 0.96f);
            return new SharpDX.Color4(0.16f, 1.0f, 0.42f, 1.0f);
        }

        private static void AppendMarkerSegment(
            Point3D first,
            Point3D second,
            SharpDX.Color4 color,
            Vector3Collection positions,
            IntCollection indices,
            Color4Collection colors)
        {
            int start = positions.Count;
            positions.Add(ToVector(first));
            positions.Add(ToVector(second));
            indices.Add(start);
            indices.Add(start + 1);
            colors.Add(color);
            colors.Add(color);
        }

        private void RebuildLines(Point3D cameraPosition)
        {
            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var colors = new Color4Collection();
            var paths = _graph.Elements
                .SelectMany(element => element.Paths)
                .Where(path =>
                    path.IsVisible &&
                    path.Points.Count >= 2 &&
                    IsFinite(path.MidPoint) &&
                    (_graph.FindSystem(path.SystemKey)?.IsVisible ?? true))
                .Select(path => new
                {
                    Path = path,
                    Distance = (path.MidPoint - cameraPosition).LengthSquared
                })
                .OrderBy(item => IsTraced(item.Path) ? 0 : 1)
                .ThenBy(item => item.Distance);

            int segmentCount = 0;
            foreach (var item in paths)
            {
                GameMepPathData path = item.Path;
                GameMepSystemData? system = _graph.FindSystem(path.SystemKey);
                SharpDX.Color4 color = GetPathColor(path, system);
                for (int pointIndex = 1; pointIndex < path.Points.Count; pointIndex++)
                {
                    if (segmentCount >= MaximumLineSegmentCount)
                        break;
                    if (!IsFinite(path.Points[pointIndex - 1]) ||
                        !IsFinite(path.Points[pointIndex]))
                    {
                        continue;
                    }
                    int first = positions.Count;
                    positions.Add(ToVector(path.Points[pointIndex - 1]));
                    positions.Add(ToVector(path.Points[pointIndex]));
                    indices.Add(first);
                    indices.Add(first + 1);
                    colors.Add(color);
                    colors.Add(color);
                    segmentCount++;
                }
                if (segmentCount >= MaximumLineSegmentCount)
                    break;
            }

            _lineGeometry = new LineGeometry3D
            {
                Positions = positions,
                Indices = indices,
                Colors = colors,
                IsDynamic = false,
                PreDefinedVertexCount = positions.Count,
                PreDefinedIndexCount = indices.Count
            };
            if (positions.Count >= 2 && indices.Count >= 2)
            {
                _lineGeometry.UpdateBounds();
                _lineModel.Geometry = _lineGeometry;
                _lineModel.IsRendering = Enabled;
            }
            else
            {
                _lineModel.IsRendering = false;
            }
        }

        private void RebuildParticles(Point3D cameraPosition)
        {
            _particles.Clear();
            var candidates = _graph.Elements
                .SelectMany(element => element.Paths)
                .Where(path =>
                    path.IsVisible &&
                    path.FlowState == GameMepFlowState.Supplied &&
                    path.HasCirculation &&
                    path.DirectionState == GameMepDirectionState.Resolved &&
                    path.Length > 0.02 &&
                    !double.IsNaN(path.Length) &&
                    !double.IsInfinity(path.Length) &&
                    IsFinite(path.MidPoint) &&
                    (_graph.FindSystem(path.SystemKey)?.IsVisible ?? true))
                .Select(path => new
                {
                    Path = path,
                    Distance = (path.MidPoint - cameraPosition).LengthSquared
                })
                .Where(item => item.Distance <= MaximumRenderDistance * MaximumRenderDistance)
                .OrderBy(item => IsTraced(item.Path) ? 0 : 1)
                .ThenBy(item => item.Distance);

            foreach (var candidate in candidates)
            {
                int count = Math.Max(1, Math.Min(10,
                    (int)Math.Ceiling(candidate.Path.Length / ParticleSpacing)));
                GameMepSystemData? system = _graph.FindSystem(candidate.Path.SystemKey);
                SharpDX.Color4 color = ToColor4(
                    system?.Color ?? Color.FromRgb(44, 207, 214),
                    _networkTrace == null || IsTraced(candidate.Path)
                        ? 1.0f
                        : 0.14f);
                for (int index = 0; index < count; index++)
                {
                    if (_particles.Count >= _particleBudget)
                        break;
                    _particles.Add(new Particle
                    {
                        Path = candidate.Path,
                        Phase = (double)index / count,
                        Color = color
                    });
                }
                if (_particles.Count >= _particleBudget)
                    break;
            }

            if (_particles.Count == 0)
            {
                _arrowModel.IsRendering = false;
                _lastParticleCameraPosition = cameraPosition;
                _particleDefinitionsDirty = false;
                return;
            }

            var positions = new Vector3Collection(_particles.Count * 6);
            var indices = new IntCollection(_particles.Count * 6);
            var colors = new Color4Collection(_particles.Count * 6);
            for (int index = 0; index < _particles.Count; index++)
            {
                Particle particle = _particles[index];
                Point3D initial = particle.Path == null
                    ? particle.FixedPosition
                    : particle.Path.Sample(particle.Phase);
                int first = positions.Count;
                for (int vertex = 0; vertex < 6; vertex++)
                {
                    positions.Add(ToVector(initial));
                    colors.Add(particle.Color);
                }
                indices.Add(first);
                indices.Add(first + 1);
                indices.Add(first + 2);
                indices.Add(first + 3);
                indices.Add(first + 4);
                indices.Add(first + 5);
            }

            _arrowGeometry = new LineGeometry3D
            {
                Positions = positions,
                Indices = indices,
                Colors = colors,
                IsDynamic = true,
                PreDefinedVertexCount = positions.Count,
                PreDefinedIndexCount = indices.Count
            };
            for (int index = 0; index < _particles.Count; index++)
            {
                Particle particle = _particles[index];
                if (particle.Path == null)
                    WriteFixedMarker(index, particle.FixedPosition);
                else
                    WriteMovingArrow(index, particle.Path, particle.Phase, cameraPosition);
            }
            _arrowGeometry.UpdateBounds();
            _arrowModel.Geometry = _arrowGeometry;
            _arrowModel.IsRendering = Enabled;
            _lastParticleCameraPosition = cameraPosition;
            _particleDefinitionsDirty = false;
        }

        private void WriteMovingArrow(
            int particleIndex,
            GameMepPathData path,
            double progress,
            Point3D cameraPosition)
        {
            if (path.Length <= 1e-6)
                return;

            double normalizedLength = Math.Min(0.45, ArrowLength / path.Length);
            double tipProgress = progress;
            double tailProgress = path.FlowForward
                ? Math.Max(0.0, progress - normalizedLength)
                : Math.Min(1.0, progress + normalizedLength);
            if (Math.Abs(tipProgress - tailProgress) < normalizedLength * 0.25)
            {
                if (path.FlowForward)
                {
                    tailProgress = progress;
                    tipProgress = Math.Min(1.0, progress + normalizedLength);
                }
                else
                {
                    tailProgress = progress;
                    tipProgress = Math.Max(0.0, progress - normalizedLength);
                }
            }

            Point3D tail = path.Sample(tailProgress);
            Point3D tip = path.Sample(tipProgress);
            Vector3D direction = tip - tail;
            double visibleLength = direction.Length;
            if (!IsFinite(tail) || !IsFinite(tip) || visibleLength < 1e-5)
                return;
            direction.Normalize();

            // La tête reste dans un plan lisible depuis la caméra, y
            // compris sur les tronçons verticaux ou vus presque de face.
            Vector3D view = cameraPosition - tip;
            Vector3D side = Vector3D.CrossProduct(direction, view);
            if (side.LengthSquared < 1e-8)
                side = Vector3D.CrossProduct(direction, new Vector3D(0, 0, 1));
            if (side.LengthSquared < 1e-8)
                side = Vector3D.CrossProduct(direction, new Vector3D(0, 1, 0));
            side.Normalize();

            double headLength = Math.Min(ArrowHeadLength, visibleLength * 0.48);
            double headWidth = Math.Min(ArrowHeadWidth, headLength * 0.90);
            Point3D headBase = tip - direction * headLength;
            Point3D headA = headBase + side * headWidth;
            Point3D headB = headBase - side * headWidth;
            int first = particleIndex * 6;
            _arrowGeometry.Positions[first] = ToVector(tail);
            _arrowGeometry.Positions[first + 1] = ToVector(tip);
            _arrowGeometry.Positions[first + 2] = ToVector(tip);
            _arrowGeometry.Positions[first + 3] = ToVector(headA);
            _arrowGeometry.Positions[first + 4] = ToVector(tip);
            _arrowGeometry.Positions[first + 5] = ToVector(headB);
        }

        private void WriteFixedMarker(int particleIndex, Point3D position)
        {
            if (!IsFinite(position))
                return;
            const double radius = 0.24;
            int first = particleIndex * 6;
            _arrowGeometry.Positions[first] = ToVector(
                position + new Vector3D(-radius, 0, 0));
            _arrowGeometry.Positions[first + 1] = ToVector(
                position + new Vector3D(radius, 0, 0));
            _arrowGeometry.Positions[first + 2] = ToVector(
                position + new Vector3D(0, -radius, 0));
            _arrowGeometry.Positions[first + 3] = ToVector(
                position + new Vector3D(0, radius, 0));
            _arrowGeometry.Positions[first + 4] = ToVector(
                position + new Vector3D(0, 0, -radius));
            _arrowGeometry.Positions[first + 5] = ToVector(
                position + new Vector3D(0, 0, radius));
        }

        private SharpDX.Color4 GetPathColor(
            GameMepPathData path,
            GameMepSystemData? system)
        {
            if (_networkTrace != null && !IsTraced(path))
                return new SharpDX.Color4(0.10f, 0.13f, 0.17f, 0.20f);
            if (path.FlowState == GameMepFlowState.Supplied &&
                !path.HasCirculation)
            {
                // Portion atteignable depuis une arrivée mais sans débouché
                // vers un retour : elle reste lisible, sans simuler un débit.
                return ToColor4(system?.Color ?? Color.FromRgb(44, 207, 214), 0.58f);
            }
            if (path.FlowState == GameMepFlowState.Supplied &&
                path.DirectionState != GameMepDirectionState.Resolved)
            {
                return new SharpDX.Color4(1.0f, 0.66f, 0.18f, 0.90f);
            }
            switch (path.FlowState)
            {
                case GameMepFlowState.Supplied:
                    return ToColor4(system?.Color ?? Color.FromRgb(44, 207, 214), 0.96f);
                case GameMepFlowState.Isolated:
                    return new SharpDX.Color4(0.16f, 0.20f, 0.24f, 0.78f);
                default:
                    return new SharpDX.Color4(1.0f, 0.66f, 0.18f, 0.84f);
            }
        }

        private bool IsTraced(GameMepPathData path)
        {
            return _networkTrace != null &&
                _networkTrace.ElementKeys.Contains(path.ElementKey);
        }

        private static SharpDX.Color4 GetTraceColor(
            GameMepTraceMode mode,
            bool branchSelected)
        {
            if (branchSelected)
                return new SharpDX.Color4(1.0f, 0.36f, 0.92f, 1.0f);
            switch (mode)
            {
                case GameMepTraceMode.Upstream:
                    return new SharpDX.Color4(0.16f, 0.90f, 1.0f, 1.0f);
                case GameMepTraceMode.Downstream:
                    return new SharpDX.Color4(0.28f, 1.0f, 0.48f, 1.0f);
                default:
                    return new SharpDX.Color4(1.0f, 0.78f, 0.12f, 1.0f);
            }
        }

        private Point3D GetElementCenter(GameMepElementData element)
        {
            if (element.ConnectorIndices.Count == 0)
                return new Point3D();
            double x = 0.0;
            double y = 0.0;
            double z = 0.0;
            int validCount = 0;
            foreach (int index in element.ConnectorIndices)
            {
                Point3D point = _graph.Connectors[index].Position;
                if (!IsFinite(point))
                    continue;
                x += point.X;
                y += point.Y;
                z += point.Z;
                validCount++;
            }
            if (validCount == 0)
                return new Point3D(double.NaN, double.NaN, double.NaN);
            double count = validCount;
            return new Point3D(x / count, y / count, z / count);
        }

        private static bool IsFinite(Point3D point)
        {
            return !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
                !double.IsNaN(point.Y) && !double.IsInfinity(point.Y) &&
                !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);
        }

        private static SharpDX.Vector3 ToVector(Point3D point)
        {
            return new SharpDX.Vector3((float)point.X, (float)point.Y, (float)point.Z);
        }

        private static SharpDX.Color4 ToColor4(Color color, float alpha)
        {
            return new SharpDX.Color4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                alpha);
        }
    }
}

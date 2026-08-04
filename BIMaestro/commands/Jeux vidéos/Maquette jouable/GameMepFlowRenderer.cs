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
        private readonly IList<Particle> _particles = new List<Particle>();
        private LineGeometry3D _lineGeometry = new LineGeometry3D();
        private LineGeometry3D _arrowGeometry = new LineGeometry3D();
        private bool _lineModelAttached;
        private bool _arrowModelAttached;
        private int _particleBudget = 2000;
        private double _lastParticleUpdateSeconds = double.MinValue;
        private Point3D _lastParticleCameraPosition;
        private bool _particleDefinitionsDirty = true;

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

            // Les modèles ne sont pas encore attachés. HelixToolkit 2023 peut
            // tenter d'initialiser immédiatement un modèle dont Geometry est
            // null, même si IsRendering vaut false. Ils seront ajoutés après la
            // construction d'un buffer réellement exploitable.
        }

        public bool Enabled { get; private set; }
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

        public void RefreshState(Point3D cameraPosition)
        {
            if (!_graph.HasData)
            {
                _lineModel.IsRendering = false;
                _arrowModel.IsRendering = false;
                return;
            }
            RebuildLines(cameraPosition);
            _particleDefinitionsDirty = true;
            if (Enabled)
                RebuildParticles(cameraPosition);
            AttachReadyModels();
            _viewport.InvalidateRender();
        }

        public void Update(double totalSeconds, Point3D cameraPosition)
        {
            if (!Enabled || Paused ||
                totalSeconds - _lastParticleUpdateSeconds < ParticleUpdateInterval)
            {
                return;
            }

            long startTimestamp = Stopwatch.GetTimestamp();
            _lastParticleUpdateSeconds = totalSeconds;
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
                .OrderBy(item => item.Distance);

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
                .OrderBy(item => item.Distance);

            foreach (var candidate in candidates)
            {
                int count = Math.Max(1, Math.Min(10,
                    (int)Math.Ceiling(candidate.Path.Length / ParticleSpacing)));
                GameMepSystemData? system = _graph.FindSystem(candidate.Path.SystemKey);
                SharpDX.Color4 color = ToColor4(
                    system?.Color ?? Color.FromRgb(44, 207, 214),
                    1.0f);
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

            foreach (GameMepValveData valve in _graph.Valves)
            {
                if (!valve.IsEnabledAsValve || !valve.IsClosed ||
                    _particles.Count >= _particleBudget)
                {
                    continue;
                }
                GameMepElementData? element = _graph.FindElement(valve.ElementKey);
                if (element == null || !element.IsVisible)
                    continue;
                Point3D position = GetElementCenter(element);
                if (!IsFinite(position) ||
                    (position - cameraPosition).LengthSquared >
                    MaximumRenderDistance * MaximumRenderDistance)
                {
                    continue;
                }
                _particles.Add(new Particle
                {
                    FixedPosition = position,
                    Color = new SharpDX.Color4(1.0f, 0.12f, 0.08f, 1.0f)
                });
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

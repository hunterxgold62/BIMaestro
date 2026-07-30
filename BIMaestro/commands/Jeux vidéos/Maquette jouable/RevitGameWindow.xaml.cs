using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.Wpf.SharpDX.Utilities;
using SharpDX.Direct3D11;
using HxMeshGeometry3D = HelixToolkit.Wpf.SharpDX.MeshGeometry3D;
using HxPerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace BIMaestro.VideoGames
{
    public partial class RevitGameWindow : Window
    {
        private const double PhysicsStepInterval = 1.0 / 120.0;
        private const int MaximumPhysicsStepsPerFrame = 8;
        private const double PlayerRadius = 0.76;       // 23 cm
        private const double PlayerHeight = 5.80;       // 1,77 m
        private const double EyeHeight = 5.28;          // 1,61 m
        private const double MaximumStepHeight = 0.86;  // 26 cm
        private const double WalkSpeed = 7.2;           // 2,19 m/s
        private const double SprintSpeed = 18.0;        // 5,49 m/s
        private const double FlySpeed = 15.0;
        private const double JumpSpeed = 11.2;
        private const double Gravity = 28.0;
        private const double GroundOffset = 0.04;
        private const double DoorInteractionDistance = 8.0; // 2,44 m
        private const double DoorAnimationSpeed = 2.8;

        private readonly GameSceneData _scene;
        private GameCollisionWorld _world = null!;
        private readonly IList<GameGpuDoorAnimation> _doors =
            new List<GameGpuDoorAnimation>();
        private readonly HxPerspectiveCamera _camera;
        private readonly DefaultEffectsManager _effectsManager;
        private readonly HashSet<Key> _pressedKeys = new HashSet<Key>();
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
        private readonly DispatcherTimer _toastTimer;

        private Point3D _footPosition;
        private Point3D _previousFootPosition;
        private Point3D _renderFootPosition;
        private double _yaw;
        private double _pitch;
        private double _verticalVelocity;
        private double _walkCycle;
        private double _lastFrameSeconds;
        private double _simulationAccumulator;
        private double _lastSpeed;
        private double _frameTimeTotalMs;
        private double _frameTimeWorstMs;
        private double _physicsTimeTotalMs;
        private double _physicsTimeWorstMs;
        private int _frameSamples;
        private int _frameSpikes;
        private bool _grounded;
        private bool _flyMode;
        private bool _mouseLookActive;
        private MouseButton? _lookButton;
        private Point _lastMousePosition;
        private bool _hasUsedMouseLook;
        private bool _realisticLight = true;
        private bool _isClosing;
        private bool _scenePrepared;
        private bool _readyToPlay;
        private bool _loadingFailed;
        private bool _loadingGateDismissed;
        private int _renderWarmupFrames;

        internal RevitGameWindow(GameSceneData scene)
        {
            _scene = scene ?? throw new ArgumentNullException(nameof(scene));

            InitializeComponent();

            _effectsManager = CreateBestEffectsManager();
            GameViewport.EffectsManager = _effectsManager;
            ConfigureGpuQuality(scene);

            _camera = new HxPerspectiveCamera
            {
                FieldOfView = 72.0,
                NearPlaneDistance = 0.06,
                FarPlaneDistance = CalculateFarPlane(scene.Bounds),
                UpDirection = new Vector3D(0, 0, 1)
            };
            GameViewport.Camera = _camera;

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                ToastBorder.Visibility = Visibility.Collapsed;
            };

            UpdateSceneLabels();
            LoadingMetricsText.Text =
                _scene.OriginalRenderTriangleCount.ToString("N0") +
                " triangles haute qualité";

            Loaded += RevitGameWindow_Loaded;
            Closed += RevitGameWindow_Closed;
            Deactivated += (s, e) =>
            {
                _pressedKeys.Clear();
                ReleaseMouseLook();
            };
            PreviewKeyDown += RevitGameWindow_PreviewKeyDown;
            PreviewKeyUp += RevitGameWindow_PreviewKeyUp;
            GameViewport.MouseLeftButtonDown += GameViewport_MouseLeftButtonDown;
            GameViewport.MouseLeftButtonUp += GameViewport_MouseLeftButtonUp;
            GameViewport.MouseRightButtonDown += GameViewport_MouseRightButtonDown;
            GameViewport.MouseRightButtonUp += GameViewport_MouseRightButtonUp;
            GameViewport.MouseMove += GameViewport_MouseMove;
            GameViewport.OnRendered += GameViewport_OnRendered;
            GameViewport.RenderExceptionOccurred += GameViewport_RenderExceptionOccurred;
        }

        private static DefaultEffectsManager CreateBestEffectsManager()
        {
            try
            {
                int bestAdapterIndex = 0;
                long bestDedicatedMemory = -1;
                using (var factory = new SharpDX.DXGI.Factory1())
                {
                    SharpDX.DXGI.Adapter1[] adapters = factory.Adapters1;
                    for (int index = 0; index < adapters.Length; index++)
                    {
                        using (SharpDX.DXGI.Adapter1 adapter = adapters[index])
                        {
                            SharpDX.DXGI.AdapterDescription1 description = adapter.Description1;
                            if ((description.Flags & SharpDX.DXGI.AdapterFlags.Software) != 0)
                                continue;

                            long dedicatedMemory = (long)description.DedicatedVideoMemory;
                            if (dedicatedMemory <= bestDedicatedMemory)
                                continue;
                            bestDedicatedMemory = dedicatedMemory;
                            bestAdapterIndex = index;
                        }
                    }
                }

                return new DefaultEffectsManager(bestAdapterIndex);
            }
            catch
            {
                return new DefaultEffectsManager();
            }
        }

        private void ConfigureGpuQuality(GameSceneData scene)
        {
            int triangleCount = scene.OriginalRenderTriangleCount > 0
                ? scene.OriginalRenderTriangleCount
                : scene.TriangleCount;
            if (triangleCount <= 900_000)
            {
                GameViewport.MSAA = MSAALevel.Four;
                GameViewport.FXAALevel = FXAALevel.None;
            }
            else if (triangleCount <= 3_000_000)
            {
                GameViewport.MSAA = MSAALevel.Two;
                GameViewport.FXAALevel = FXAALevel.None;
            }
            else
            {
                GameViewport.MSAA = MSAALevel.Disable;
                GameViewport.FXAALevel = FXAALevel.Low;
            }

            bool hasTransparentGeometry = false;
            foreach (GameMeshData mesh in scene.Meshes)
            {
                if (!mesh.IsTransparent)
                    continue;
                hasTransparentGeometry = true;
                break;
            }
            GameViewport.OITRenderMode = hasTransparentGeometry
                ? OITRenderType.SinglePassWeighted
                : OITRenderType.None;
        }

        private void RevitGameWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            Activate();

            // Laisse WPF afficher le sas avant de construire l'index de collision
            // et les ressources 3D. L'utilisateur ne peut pas entrer trop tôt.
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(PrepareScene));
        }

        private void RevitGameWindow_Closed(object sender, EventArgs e)
        {
            _isClosing = true;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            GameViewport.OnRendered -= GameViewport_OnRendered;
            GameViewport.RenderExceptionOccurred -= GameViewport_RenderExceptionOccurred;
            ReleaseMouseLook();
            _pressedKeys.Clear();
            try { GameViewport.Dispose(); } catch { }
            try { _effectsManager.Dispose(); } catch { }
        }

        private void PrepareScene()
        {
            if (_isClosing || _scenePrepared || _loadingFailed)
                return;

            try
            {
                SetLoadingStatus("Construction des collisions…");
                _world = new GameCollisionWorld(_scene);

                SetLoadingStatus("Création des buffers DirectX haute qualité…");
                GameGpuSceneBuildResult gpuScene = GameGpuSceneBuilder.Build(_scene);
                LoadingMetricsText.Text =
                    gpuScene.TriangleCount.ToString("N0") + " triangles conservés  •  " +
                    gpuScene.Meshes.Count.ToString("N0") + " zones GPU  •  " +
                    gpuScene.Doors.Count.ToString("N0") + " portes interactives";

                SetLoadingStatus("Transfert de la maquette vers DirectX 11…");
                BuildSceneModel(gpuScene.Meshes);
                foreach (GameGpuDoorAnimation door in gpuScene.Doors)
                    _doors.Add(door);
                UpdateSceneLabels();
                SetLightMode(true, false);
                ResetPlayer(false);

                SetLoadingStatus("Nettoyage de la mémoire avant le démarrage…");
                try
                {
                    GCSettings.LargeObjectHeapCompactionMode =
                        GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                }
                catch { }

                _scenePrepared = true;
                _renderWarmupFrames = 0;
                SetLoadingStatus("Finalisation des buffers sur la carte graphique…");
                GameViewport.InvalidateRender();
            }
            catch (Exception exception)
            {
                _loadingFailed = true;
                LoadingProgress.Visibility = Visibility.Collapsed;
                LoadingTitleText.Text = "CHARGEMENT IMPOSSIBLE";
                LoadingStatusText.Text = exception.Message;
                LoadingCloseButton.Content = "Fermer";
            }
        }

        private void SetLoadingStatus(string message)
        {
            LoadingStatusText.Text = message;
            try
            {
                Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            }
            catch { }
        }

        private void CompleteLoading()
        {
            if (_readyToPlay || !_scenePrepared)
                return;

            _readyToPlay = true;
            _lastFrameSeconds = _frameClock.Elapsed.TotalSeconds;
            _simulationAccumulator = 0.0;
            MouseGuide.Visibility = Visibility.Visible;
            ControlsHud.Visibility = Visibility.Collapsed;
            Keyboard.Focus(GameViewport);
            ShowToast("Maquette entièrement chargée — vous pouvez entrer");
        }

        private void BuildSceneModel(IList<GameGpuRenderMesh> renderMeshes)
        {
            var opaqueMaterial = CreateVertexColorMaterial("Couleurs Revit opaques");
            var transparentMaterial = CreateVertexColorMaterial("Couleurs Revit transparentes");

            foreach (GameGpuRenderMesh mesh in renderMeshes)
            {
                if (mesh.Geometry.Positions.Count == 0 || mesh.Geometry.TriangleIndices.Count == 0)
                    continue;

                var model = new MeshGeometryModel3D
                {
                    Geometry = mesh.Geometry,
                    Material = mesh.IsTransparent ? transparentMaterial : opaqueMaterial,
                    IsTransparent = mesh.IsTransparent,
                    CullMode = CullMode.Back,
                    EnableViewFrustumCheck = true,
                    IsThrowingShadow = false,
                    IsHitTestVisible = false
                };
                GameViewport.Items.Add(model);
            }
        }

        private static PhongMaterial CreateVertexColorMaterial(string name)
        {
            return new PhongMaterial
            {
                Name = name,
                AmbientColor = new SharpDX.Color4(0.28f, 0.28f, 0.28f, 1f),
                DiffuseColor = new SharpDX.Color4(1f, 1f, 1f, 1f),
                SpecularColor = new SharpDX.Color4(0.10f, 0.10f, 0.10f, 1f),
                SpecularShininess = 18f,
                VertexColorBlendingFactor = 1.0,
                EnableFlatShading = false
            };
        }

        private void SetLightMode(bool realistic, bool announce = true)
        {
            _realisticLight = realistic;
            if (realistic)
            {
                AmbientLight.Color = Color.FromRgb(92, 104, 120);
                SunLight.IsRendering = true;
                FillLight.IsRendering = true;
                HeadLight.IsRendering = true;
            }
            else
            {
                AmbientLight.Color = Color.FromRgb(255, 255, 255);
                SunLight.IsRendering = false;
                FillLight.IsRendering = false;
                HeadLight.IsRendering = false;
            }

            if (announce)
            {
                ShowToast(realistic
                    ? "Éclairage réaliste activé"
                    : "Éclairage uniforme activé — couleurs Revit pures");
            }
        }

        private void GameViewport_OnRendered(object sender, EventArgs e)
        {
            if (_isClosing || !_scenePrepared || _readyToPlay || _loadingFailed)
                return;

            _renderWarmupFrames++;
            if (_renderWarmupFrames >= 3)
            {
                if (!_loadingGateDismissed)
                {
                    // Le retrait du sas agrandit le viewport. On attend encore
                    // trois images après la recréation du swap chain final.
                    _loadingGateDismissed = true;
                    LoadingGate.Visibility = Visibility.Collapsed;
                    UpdateLayout();
                    _renderWarmupFrames = 0;
                    GameViewport.InvalidateRender();
                }
                else
                {
                    CompleteLoading();
                }
                return;
            }

            // Force plusieurs rendus complets : le sas ne s'ouvre qu'après la
            // création effective des buffers DirectX, pas après leur simple ajout.
            GameViewport.InvalidateRender();
        }

        private void GameViewport_RenderExceptionOccurred(
            object sender,
            RelayExceptionEventArgs e)
        {
            e.Handled = true;
            _loadingFailed = true;
            _readyToPlay = false;
            LoadingGate.Visibility = Visibility.Visible;
            LoadingProgress.Visibility = Visibility.Collapsed;
            LoadingTitleText.Text = "RENDU DIRECTX IMPOSSIBLE";
            LoadingStatusText.Text =
                e.Exception?.Message ??
                "La carte graphique n'a pas pu initialiser le moteur DirectX 11.";
            LoadingCloseButton.Content = "Fermer";
        }

        private void ResetPlayer(bool announce)
        {
            _footPosition = _scene.SpawnFootPosition;
            _yaw = _scene.InitialYawRadians;
            _pitch = 0.0;
            _verticalVelocity = 0.0;
            _flyMode = false;
            _grounded = _world.TryFindGround(
                _footPosition.X,
                _footPosition.Y,
                _footPosition.Z + MaximumStepHeight,
                _footPosition.Z - 3.0,
                out double ground);

            if (_grounded)
                _footPosition.Z = ground + GroundOffset;

            _previousFootPosition = _footPosition;
            _renderFootPosition = _footPosition;
            ModeText.Text = "MARCHE";
            UpdateCamera(_renderFootPosition);
            if (announce)
                ShowToast("Retour au point de départ");
        }

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_isClosing)
                return;

            if (!_scenePrepared)
                return;

            if (!_readyToPlay)
                return;

            double now = _frameClock.Elapsed.TotalSeconds;
            double elapsed = Math.Min(0.05, Math.Max(0.0001, now - _lastFrameSeconds));
            _lastFrameSeconds = now;

            double frameTimeMs = elapsed * 1000.0;
            _frameSamples++;
            _frameTimeTotalMs += frameTimeMs;
            _frameTimeWorstMs = Math.Max(_frameTimeWorstMs, frameTimeMs);
            if (frameTimeMs > 25.0)
                _frameSpikes++;

            _simulationAccumulator = Math.Min(
                _simulationAccumulator + elapsed,
                PhysicsStepInterval * MaximumPhysicsStepsPerFrame);
            int simulationSteps = Math.Min(
                MaximumPhysicsStepsPerFrame,
                (int)(_simulationAccumulator / PhysicsStepInterval));

            long physicsStart = Stopwatch.GetTimestamp();
            for (int step = 0; step < simulationSteps; step++)
            {
                _previousFootPosition = _footPosition;
                UpdatePlayer(PhysicsStepInterval);
            }
            _simulationAccumulator -= simulationSteps * PhysicsStepInterval;
            double physicsTimeMs =
                (Stopwatch.GetTimestamp() - physicsStart) * 1000.0 / Stopwatch.Frequency;
            _physicsTimeTotalMs += physicsTimeMs;
            _physicsTimeWorstMs = Math.Max(_physicsTimeWorstMs, physicsTimeMs);

            double interpolation = Clamp(
                _simulationAccumulator / PhysicsStepInterval,
                0.0,
                1.0);
            _renderFootPosition = Interpolate(
                _previousFootPosition,
                _footPosition,
                interpolation);
            UpdateDoorAnimations(elapsed);
            UpdateCamera(_renderFootPosition);
            UpdatePerformanceHud();
        }

        private void UpdatePlayer(double deltaTime)
        {
            double forwardInput = Axis(
                IsDown(Key.Z) || IsDown(Key.W) || IsDown(Key.Up),
                IsDown(Key.S) || IsDown(Key.Down));
            double rightInput = Axis(
                IsDown(Key.D) || IsDown(Key.Right),
                IsDown(Key.Q) || IsDown(Key.A) || IsDown(Key.Left));

            var forward = new Vector3D(Math.Cos(_yaw), Math.Sin(_yaw), 0);
            // Repère Revit/DirectX droit avec Z vertical : forward × up donne
            // la droite. L'ancien signe inversait Q/D et les flèches gauche/droite.
            var right = new Vector3D(Math.Sin(_yaw), -Math.Cos(_yaw), 0);
            Vector3D movement = forward * forwardInput + right * rightInput;
            if (movement.LengthSquared > 1.0)
                movement.Normalize();

            bool sprint = IsDown(Key.LeftShift) || IsDown(Key.RightShift);
            double speed = sprint ? SprintSpeed : WalkSpeed;
            _lastSpeed = movement.Length * speed;

            if (_flyMode)
            {
                double verticalInput = Axis(IsDown(Key.Space), IsDown(Key.LeftCtrl) || IsDown(Key.C));
                Vector3D flyMovement = movement * FlySpeed;
                flyMovement.Z = verticalInput * FlySpeed;
                _footPosition += flyMovement * deltaTime;
                _grounded = false;
                _verticalVelocity = 0.0;
                return;
            }

            if (movement.LengthSquared > 1e-8)
            {
                Vector3D displacement = movement * speed * deltaTime;
                TryMoveHorizontal(displacement.X, 0.0);
                TryMoveHorizontal(0.0, displacement.Y);
                if (_grounded)
                    _walkCycle += deltaTime * (sprint ? 12.5 : 9.0);
            }

            if (_grounded)
            {
                if (_world.TryFindGround(
                    _footPosition.X,
                    _footPosition.Y,
                    _footPosition.Z + MaximumStepHeight,
                    _footPosition.Z - 2.4,
                    out double ground))
                {
                    _footPosition.Z = ground + GroundOffset;
                    _verticalVelocity = 0.0;
                }
                else
                {
                    _grounded = false;
                }
            }

            if (!_grounded)
            {
                _verticalVelocity -= Gravity * deltaTime;
                double nextZ = _footPosition.Z + _verticalVelocity * deltaTime;

                if (_verticalVelocity > 0.0)
                {
                    double currentHead = _footPosition.Z + PlayerHeight;
                    double nextHead = nextZ + PlayerHeight;
                    if (_world.TryFindCeiling(
                        _footPosition.X,
                        _footPosition.Y,
                        currentHead,
                        nextHead + 0.1,
                        out double ceiling))
                    {
                        nextZ = Math.Min(nextZ, ceiling - PlayerHeight - 0.06);
                        _verticalVelocity = 0.0;
                    }
                }
                else if (_world.TryFindGround(
                    _footPosition.X,
                    _footPosition.Y,
                    _footPosition.Z + 0.15,
                    nextZ - 0.5,
                    out double landing) &&
                    nextZ <= landing + GroundOffset)
                {
                    nextZ = landing + GroundOffset;
                    _verticalVelocity = 0.0;
                    _grounded = true;
                }

                _footPosition.Z = nextZ;
            }

            if (_footPosition.Z < _scene.Bounds.Z - Math.Max(30.0, _scene.Bounds.SizeZ))
                ResetPlayer(true);
        }

        private void TryMoveHorizontal(double deltaX, double deltaY)
        {
            if (Math.Abs(deltaX) < 1e-10 && Math.Abs(deltaY) < 1e-10)
                return;

            var candidate = new Point3D(
                _footPosition.X + deltaX,
                _footPosition.Y + deltaY,
                _footPosition.Z);

            if (_grounded &&
                _world.TryFindGround(
                    candidate.X,
                    candidate.Y,
                    _footPosition.Z + MaximumStepHeight,
                    _footPosition.Z - 2.4,
                    out double candidateGround) &&
                candidateGround <= _footPosition.Z + MaximumStepHeight)
            {
                candidate.Z = candidateGround + GroundOffset;
            }

            if (!_world.IsBodyBlocked(candidate, PlayerRadius, PlayerHeight, MaximumStepHeight))
                _footPosition = candidate;
        }

        private void UpdateCamera(Point3D cameraFootPosition)
        {
            double headBob = 0.0;
            if (_grounded && _lastSpeed > 0.1 && !_flyMode)
                headBob = Math.Sin(_walkCycle) * 0.035;

            double cosPitch = Math.Cos(_pitch);
            var look = new Vector3D(
                Math.Cos(_yaw) * cosPitch,
                Math.Sin(_yaw) * cosPitch,
                Math.Sin(_pitch));
            look.Normalize();

            _camera.Position = new Point3D(
                cameraFootPosition.X,
                cameraFootPosition.Y,
                cameraFootPosition.Z + EyeHeight + headBob);
            _camera.LookDirection = look * 10.0;
            _camera.UpDirection = new Vector3D(0, 0, 1);

            if (_realisticLight)
                HeadLight.Direction = look;
        }

        private void UpdatePerformanceHud()
        {
            if (_fpsClock.Elapsed.TotalSeconds >= 0.45)
            {
                double measuredSeconds = _fpsClock.Elapsed.TotalSeconds;
                double fps = _frameSamples / measuredSeconds;
                double averageFrameMs = _frameSamples > 0
                    ? _frameTimeTotalMs / _frameSamples
                    : 0.0;
                double averagePhysicsMs = _frameSamples > 0
                    ? _physicsTimeTotalMs / _frameSamples
                    : 0.0;
                FpsText.Text =
                    Math.Round(fps).ToString("0") + " FPS  •  " +
                    averageFrameMs.ToString("0.0") + " ms" +
                    (_frameSpikes > 0
                        ? "  •  pic " + _frameTimeWorstMs.ToString("0") + " ms"
                        : string.Empty);
                SpeedText.Text = (_lastSpeed * 0.3048).ToString("0.0") + " m/s";
                FpsText.ToolTip = "Physique moyenne : " +
                    averagePhysicsMs.ToString("0.00") + " ms • maximum : " +
                    _physicsTimeWorstMs.ToString("0.00") + " ms";

                _frameSamples = 0;
                _frameSpikes = 0;
                _frameTimeTotalMs = 0.0;
                _frameTimeWorstMs = 0.0;
                _physicsTimeTotalMs = 0.0;
                _physicsTimeWorstMs = 0.0;
                _fpsClock.Restart();
            }
        }

        private void UpdateSceneLabels()
        {
            ViewNameText.Text = "Vue Revit : " + _scene.ViewName;
            int renderedTriangles = _scene.OptimizedRenderTriangleCount > 0
                ? _scene.OptimizedRenderTriangleCount
                : (_scene.OriginalRenderTriangleCount > 0
                    ? _scene.OriginalRenderTriangleCount
                    : _scene.TriangleCount);
            SceneStatsText.Text =
                _scene.VisibleElementCount.ToString("N0") + " éléments  •  " +
                renderedTriangles.ToString("N0") + " triangles GPU  •  " +
                _scene.RenderBucketCount.ToString("N0") + " zones DirectX  •  " +
                _scene.Doors.Count.ToString("N0") + " portes";
        }

        private void RevitGameWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_mouseLookActive)
                    ReleaseMouseLook();
                else
                    Close();
                e.Handled = true;
                return;
            }

            if (!_readyToPlay)
            {
                e.Handled = true;
                return;
            }

            bool firstPress = _pressedKeys.Add(e.Key);
            if (!firstPress)
                return;

            if (e.Key == Key.Space && !_flyMode && _grounded)
            {
                _verticalVelocity = JumpSpeed;
                _grounded = false;
                e.Handled = true;
            }
            else if (e.Key == Key.E)
            {
                ToggleNearestDoor();
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                _flyMode = !_flyMode;
                _grounded = false;
                _verticalVelocity = 0.0;
                ModeText.Text = _flyMode ? "VOL LIBRE" : "MARCHE";
                ShowToast(_flyMode
                    ? "Mode vol libre — Espace monte, Ctrl/C descend"
                    : "Collisions et gravité réactivées");
                e.Handled = true;
            }
            else if (e.Key == Key.R)
            {
                ResetPlayer(true);
                e.Handled = true;
            }
        }

        private void ToggleNearestDoor()
        {
            GameGpuDoorAnimation? nearestDoor = null;
            double nearestDistanceSquared =
                DoorInteractionDistance * DoorInteractionDistance;
            var playerEye = new Point3D(
                _footPosition.X,
                _footPosition.Y,
                _footPosition.Z + EyeHeight);

            foreach (GameGpuDoorAnimation door in _doors)
            {
                Vector3D offset = door.Door.Center - playerEye;
                // Évite d'actionner une porte située à l'étage supérieur même si
                // elle partage presque les mêmes coordonnées en plan.
                if (Math.Abs(offset.Z) > PlayerHeight)
                    continue;

                double distanceSquared = offset.LengthSquared;
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestDoor = door;
                }
            }

            if (nearestDoor == null)
            {
                ShowToast("Aucune porte assez proche");
                return;
            }

            nearestDoor.TargetOpen = !nearestDoor.TargetOpen;
            if (nearestDoor.TargetOpen)
            {
                nearestDoor.OpenAngleDegrees =
                    ChooseDoorOpeningDirection(nearestDoor.Door) * 92.0;
                ShowToast("Ouverture de la porte");
            }
            else
            {
                ShowToast("Fermeture de la porte");
            }
        }

        private double ChooseDoorOpeningDirection(GameDoorData door)
        {
            double radialX = door.Center.X - door.Hinge.X;
            double radialY = door.Center.Y - door.Hinge.Y;
            if (radialX * radialX + radialY * radialY < 1e-8)
                return 1.0;

            double plusX = door.Hinge.X - radialY;
            double plusY = door.Hinge.Y + radialX;
            double minusX = door.Hinge.X + radialY;
            double minusY = door.Hinge.Y - radialX;
            double plusDistance =
                Square(plusX - _footPosition.X) +
                Square(plusY - _footPosition.Y);
            double minusDistance =
                Square(minusX - _footPosition.X) +
                Square(minusY - _footPosition.Y);

            // Le vantail s'ouvre du côté qui l'éloigne le plus du joueur.
            return plusDistance >= minusDistance ? 1.0 : -1.0;
        }

        private void UpdateDoorAnimations(double elapsed)
        {
            HashSet<HxMeshGeometry3D>? dirtyGeometries = null;
            foreach (GameGpuDoorAnimation door in _doors)
            {
                double target = door.TargetOpen ? 1.0 : 0.0;
                double previousProgress = door.Progress;
                door.Progress = MoveTowards(
                    door.Progress,
                    target,
                    DoorAnimationSpeed * elapsed);
                if (Math.Abs(door.Progress - previousProgress) < 1e-10)
                    continue;

                double eased = door.Progress * door.Progress *
                    (3.0 - 2.0 * door.Progress);
                double angleRadians =
                    door.OpenAngleDegrees * eased * Math.PI / 180.0;
                float cosine = (float)Math.Cos(angleRadians);
                float sine = (float)Math.Sin(angleRadians);
                float hingeX = (float)door.Door.Hinge.X;
                float hingeY = (float)door.Door.Hinge.Y;

                foreach (GameGpuDoorVertexRange range in door.Ranges)
                {
                    for (int index = 0; index < range.ClosedPositions.Length; index++)
                    {
                        SharpDX.Vector3 closed = range.ClosedPositions[index];
                        float relativeX = closed.X - hingeX;
                        float relativeY = closed.Y - hingeY;
                        range.Geometry.Positions[range.StartVertex + index] =
                            new SharpDX.Vector3(
                                hingeX + relativeX * cosine - relativeY * sine,
                                hingeY + relativeX * sine + relativeY * cosine,
                                closed.Z);

                        SharpDX.Vector3 normal = range.ClosedNormals[index];
                        range.Geometry.Normals[range.StartVertex + index] =
                            new SharpDX.Vector3(
                                normal.X * cosine - normal.Y * sine,
                                normal.X * sine + normal.Y * cosine,
                                normal.Z);
                    }

                    if (dirtyGeometries == null)
                        dirtyGeometries = new HashSet<HxMeshGeometry3D>();
                    dirtyGeometries.Add(range.Geometry);
                }
            }

            if (dirtyGeometries == null)
                return;

            // Une seule synchronisation GPU par zone, même si plusieurs vantaux
            // de la même zone bougent au cours de cette image.
            foreach (HxMeshGeometry3D geometry in dirtyGeometries)
            {
                geometry.UpdateVertices();
                geometry.UpdateBounds();
            }
        }

        private static double MoveTowards(double current, double target, double maximumDelta)
        {
            if (Math.Abs(target - current) <= maximumDelta)
                return target;
            return current + Math.Sign(target - current) * maximumDelta;
        }

        private static double Square(double value)
        {
            return value * value;
        }

        private void RevitGameWindow_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (!_readyToPlay)
                return;

            _pressedKeys.Remove(e.Key);
        }

        private void GameViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginMouseLook(MouseButton.Left, e);
            e.Handled = true;
        }

        private void GameViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndMouseLook(MouseButton.Left);
            e.Handled = true;
        }

        private void GameViewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginMouseLook(MouseButton.Right, e);
            e.Handled = true;
        }

        private void GameViewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndMouseLook(MouseButton.Right);
            e.Handled = true;
        }

        private void BeginMouseLook(MouseButton button, MouseButtonEventArgs e)
        {
            if (!_readyToPlay || _mouseLookActive || !IsVisible)
                return;

            _mouseLookActive = true;
            _lookButton = button;
            _lastMousePosition = e.GetPosition(GameViewport);
            _hasUsedMouseLook = true;
            MouseGuide.Visibility = Visibility.Collapsed;
            ControlsHud.Visibility = Visibility.Visible;
            GameViewport.Cursor = Cursors.ScrollAll;
            Mouse.Capture(GameViewport, CaptureMode.Element);
            Keyboard.Focus(GameViewport);
        }

        private void EndMouseLook(MouseButton button)
        {
            if (!_mouseLookActive || _lookButton != button)
                return;

            ReleaseMouseLook();
        }

        private void ReleaseMouseLook()
        {
            if (!_mouseLookActive)
                return;

            _mouseLookActive = false;
            _lookButton = null;
            Mouse.Capture(null);
            GameViewport.Cursor = Cursors.Arrow;
            if (!_isClosing && _readyToPlay && !_hasUsedMouseLook)
                MouseGuide.Visibility = Visibility.Visible;
        }

        private void GameViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseLookActive || GameViewport.ActualWidth < 10 || GameViewport.ActualHeight < 10)
                return;

            Point position = e.GetPosition(GameViewport);
            double deltaX = position.X - _lastMousePosition.X;
            double deltaY = position.Y - _lastMousePosition.Y;
            _lastMousePosition = position;

            bool buttonStillDown =
                (_lookButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed) ||
                (_lookButton == MouseButton.Right && e.RightButton == MouseButtonState.Pressed);
            if (!buttonStillDown)
            {
                ReleaseMouseLook();
                return;
            }

            if (Math.Abs(deltaX) < 0.1 && Math.Abs(deltaY) < 0.1)
                return;

            _yaw += deltaX * 0.00245;
            // Visée non inversée dans la caméra DirectX.
            _pitch = Clamp(_pitch + deltaY * 0.00225, -1.48, 1.48);
            e.Handled = true;
        }

        private void ShowToast(string message)
        {
            if (!IsInitialized)
                return;

            ToastText.Text = message;
            ToastBorder.Visibility = Visibility.Visible;
            ToastBorder.Opacity = 1.0;
            ToastBorder.BeginAnimation(OpacityProperty, null);
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private bool IsDown(Key key) => _pressedKeys.Contains(key);

        private static double Axis(bool positive, bool negative)
        {
            return (positive ? 1.0 : 0.0) - (negative ? 1.0 : 0.0);
        }

        private static double CalculateFarPlane(Rect3D bounds)
        {
            double diagonal = Math.Sqrt(
                bounds.SizeX * bounds.SizeX +
                bounds.SizeY * bounds.SizeY +
                bounds.SizeZ * bounds.SizeZ);
            return Math.Max(5000.0, diagonal * 5.0);
        }

        private static Point3D Interpolate(Point3D from, Point3D to, double amount)
        {
            return new Point3D(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

    }
}

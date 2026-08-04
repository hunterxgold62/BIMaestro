using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Threading.Tasks;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.Wpf.SharpDX.Elements2D;
using HelixToolkit.Wpf.SharpDX.Utilities;
using SharpDX.Direct3D11;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;
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
        private const double CrouchPlayerHeight = 3.35; // 1,02 m
        private const double CrouchEyeHeight = 2.72;    // 0,83 m
        private const double CrouchSpeed = 4.2;         // 1,28 m/s
        private const double CrouchTransitionSpeed = 10.0;
        private const double MaximumStepHeight = 0.86;  // 26 cm
        private const double WalkSpeed = 7.2;           // 2,19 m/s
        private const double SprintSpeed = 18.0;        // 5,49 m/s
        private const double FlySpeed = 15.0;
        private const double FlySprintSpeed = 26.24672; // 8,00 m/s
        private const double JumpSpeed = 11.2;
        private const double Gravity = 28.0;
        private const double GroundOffset = 0.04;
        private const double DoorInteractionDistance = 8.0; // 2,44 m
        private const double DoorAnimationSpeed = 2.8;
        private const int MiniMapImageSize = 208;
        private const double MiniMapOverlayInset = 12.0;
        private const double MiniMapLevelChangeHeight = 8.0; // 2,44 m
        private const double MiniMapRebuildCooldownSeconds = 0.28;
        private const double ForwardDoubleTapSeconds = 0.34;

        private readonly GameSceneData _scene;
        private GameCollisionWorld _world = null!;
        private readonly IList<GameGpuDoorAnimation> _doors =
            new List<GameGpuDoorAnimation>();
        private readonly ObservableCollection<GameMepSystemItem> _mepSystemItems =
            new ObservableCollection<GameMepSystemItem>();
        private readonly ObservableCollection<GameMepSourceItem> _mepSourceItems =
            new ObservableCollection<GameMepSourceItem>();
        private readonly HxPerspectiveCamera _camera;
        private readonly DefaultEffectsManager _effectsManager;
        private readonly HashSet<Key> _pressedKeys = new HashSet<Key>();
        private readonly ObservableCollection<GameSelectedElementItem>
            _selectedElementHistory =
                new ObservableCollection<GameSelectedElementItem>();
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
        private readonly DispatcherTimer _toastTimer;
        private MemoryStream? _miniMapImageStream;

        private Point3D _footPosition;
        private Point3D _previousFootPosition;
        private Point3D _renderFootPosition;
        private double _yaw;
        private double _pitch;
        private double _verticalVelocity;
        private double _currentEyeHeight = EyeHeight;
        private double _lastFrameSeconds;
        private double _simulationAccumulator;
        private double _lastSpeed;
        private int _frameSamples;
        private bool _grounded;
        private bool _flyMode;
        private bool _isCrouching;
        private bool _mouseLookActive;
        private MouseButton? _lookButton;
        private Point _lastMousePosition;
        private Point _rightMouseDownPosition;
        private bool _rightGestureMoved;
        private bool _realisticLight = true;
        private bool _isClosing;
        private bool _scenePrepared;
        private bool _readyToPlay;
        private bool _loadingFailed;
        private bool _loadingGateDismissed;
        private int _renderWarmupFrames;
        private double _miniMapSliceZ;
        private double _miniMapWorldMinimumX;
        private double _miniMapWorldMaximumY;
        private double _miniMapWorldScale;
        private double _miniMapPixelOffsetX;
        private double _miniMapPixelOffsetY;
        private double _lastMiniMapBuildSeconds = double.MinValue;
        private bool _miniMapBuildInProgress;
        private bool _miniMapRebuildQueued;
        private bool _miniMapReady;
        private double _lastForwardTapSeconds = double.MinValue;
        private Key _lastForwardTapKey = Key.None;
        private Key _doubleTapSprintKey = Key.None;
        private bool _doubleTapSprintActive;
        private GameMepSimulationEngine? _mepSimulation;
        private GameMepFlowRenderer? _mepRenderer;
        private bool _mepFlowEnabled;
        private bool _mepRecalculationRunning;
        private bool _mepRecalculationQueued;
        private string _mepRuntimeError = string.Empty;

        internal RevitGameWindow(GameSceneData scene)
        {
            _scene = scene ?? throw new ArgumentNullException(nameof(scene));

            InitializeComponent();
            InitializeMiniMapPlaceholder();
            SelectedElementsList.ItemsSource = _selectedElementHistory;
            MepSystemsList.ItemsSource = _mepSystemItems;
            MepSourcesList.ItemsSource = _mepSourceItems;
            InitializeMepItems();
            UpdateSelectionHistoryUi();

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
                ClearDoubleTapSprint();
                ReleaseMouseLook();
            };
            PreviewKeyDown += RevitGameWindow_PreviewKeyDown;
            PreviewKeyUp += RevitGameWindow_PreviewKeyUp;
            GameViewport.MouseLeftButtonDown += GameViewport_MouseLeftButtonDown;
            GameViewport.MouseLeftButtonUp += GameViewport_MouseLeftButtonUp;
            GameViewport.MouseRightButtonDown += GameViewport_MouseRightButtonDown;
            GameViewport.MouseRightButtonUp += GameViewport_MouseRightButtonUp;
            GameViewport.MouseMove += GameViewport_MouseMove;
            GameViewport.SizeChanged += GameViewport_SizeChanged;
            GameViewport.OnRendered += GameViewport_OnRendered;
            GameViewport.RenderExceptionOccurred += GameViewport_RenderExceptionOccurred;
            Dispatcher.UnhandledException += GameDispatcher_UnhandledException;
            GameRuntimeDiagnostics.Write("Fenêtre construite");
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
            GameRuntimeDiagnostics.Write("Fenêtre chargée - lancement du sas");
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            PositionMiniMapOverlay();
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
            // Garantit que la toute dernière action est écrite même si Revit
            // est fermé avant la fin d'une sauvegarde asynchrone précédente.
            GameMepScenarioStore.SaveNow(_scene.MepGraph);
            _miniMapRebuildQueued = false;
            _mepRecalculationQueued = false;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            GameViewport.SizeChanged -= GameViewport_SizeChanged;
            GameViewport.OnRendered -= GameViewport_OnRendered;
            GameViewport.RenderExceptionOccurred -= GameViewport_RenderExceptionOccurred;
            Dispatcher.UnhandledException -= GameDispatcher_UnhandledException;
            ReleaseMouseLook();
            _pressedKeys.Clear();
            try { _mepRenderer?.Dispose(); } catch { }
            _mepRenderer = null;
            try { GameViewport.Dispose(); } catch { }
            try { _effectsManager.Dispose(); } catch { }
            try { _miniMapImageStream?.Dispose(); } catch { }
            _miniMapImageStream = null;
        }

        private void PrepareScene()
        {
            if (_isClosing || _scenePrepared || _loadingFailed)
                return;

            try
            {
                GameRuntimeDiagnostics.Write("PrepareScene - début");
                SetLoadingStatus("Construction des collisions…");
                _world = new GameCollisionWorld(_scene);
                GameRuntimeDiagnostics.Write("PrepareScene - collisions terminées");

                SetLoadingStatus("Calcul de la continuité des réseaux MEP…");
                _mepSimulation = new GameMepSimulationEngine(_scene.MepGraph);
                _mepSimulation.Recalculate();
                GameRuntimeDiagnostics.Write("PrepareScene - graphe MEP calculé");

                SetLoadingStatus("Création des buffers DirectX haute qualité…");
                GameGpuSceneBuildResult gpuScene = GameGpuSceneBuilder.Build(_scene);
                GameRuntimeDiagnostics.Write("PrepareScene - scène GPU construite");
                LoadingMetricsText.Text =
                    gpuScene.TriangleCount.ToString("N0") + " triangles conservés  •  " +
                    gpuScene.Meshes.Count.ToString("N0") + " zones GPU  •  " +
                    gpuScene.Doors.Count.ToString("N0") + " portes interactives  •  " +
                    _scene.MepGraph.Elements.Count.ToString("N0") + " éléments MEP";

                SetLoadingStatus("Transfert de la maquette vers DirectX 11…");
                BuildSceneModel(gpuScene.Meshes);
                GameRuntimeDiagnostics.Write("PrepareScene - modèles ajoutés au viewport");
                foreach (GameGpuDoorAnimation door in gpuScene.Doors)
                    _doors.Add(door);

                // Ne rien attacher au viewport pour le MEP pendant le sas de
                // chargement. Sous Revit 2023, l'ajout de modèles Helix vides
                // pendant la finalisation du swap-chain pouvait déclencher une
                // exception différée impossible à contenir ici. Le renderer est
                // créé uniquement lors d'un clic explicite sur « Activer ».
                SetLoadingStatus("Préparation du panneau Fluides MEP…");
                _mepRenderer = null;
                UpdateMepUi();
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
                GameRuntimeDiagnostics.Write("PrepareScene - attente du premier rendu");
                GameViewport.InvalidateRender();
            }
            catch (Exception exception)
            {
                GameRuntimeDiagnostics.Write("PrepareScene - exception contenue", exception);
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
            GameRuntimeDiagnostics.Write("Chargement terminé - jeu prêt");
            _lastFrameSeconds = _frameClock.Elapsed.TotalSeconds;
            _simulationAccumulator = 0.0;
            ControlsHud.Visibility = Visibility.Visible;
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
            if (!Dispatcher.CheckAccess())
            {
                GameRuntimeDiagnostics.Write(
                    "OnRendered reçu hors thread WPF - remarshal");
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(() => GameViewport_OnRendered(sender, e)));
                return;
            }

            if (_isClosing || !_scenePrepared || _readyToPlay || _loadingFailed)
                return;

            _renderWarmupFrames++;
            GameRuntimeDiagnostics.Write(
                "OnRendered - image de chauffe " + _renderWarmupFrames);
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
            Exception exception = e.Exception;
            GameRuntimeDiagnostics.Write("Exception DirectX signalée par Helix", exception);
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => HandleViewportRenderException(exception)));
                return;
            }

            HandleViewportRenderException(exception);
        }

        private void HandleViewportRenderException(Exception exception)
        {
            _loadingFailed = true;
            _readyToPlay = false;
            LoadingGate.Visibility = Visibility.Visible;
            LoadingProgress.Visibility = Visibility.Collapsed;
            LoadingTitleText.Text = "RENDU DIRECTX IMPOSSIBLE";
            LoadingStatusText.Text =
                exception?.Message ??
                "La carte graphique n'a pas pu initialiser le moteur DirectX 11.";
            LoadingCloseButton.Content = "Fermer";
        }

        private void GameDispatcher_UnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs e)
        {
            if (_isClosing || !IsGameRuntimeException(e.Exception))
                return;

            e.Handled = true;
            GameRuntimeDiagnostics.Write(
                "Exception Dispatcher interceptée avant Revit",
                e.Exception);
            try
            {
                // Une erreur différée de binding dans le panneau MEP doit
                // fermer uniquement ce panneau. La visite reste jouable.
                if (_readyToPlay && MepPanel.Visibility == Visibility.Visible)
                {
                    _mepRuntimeError = e.Exception.Message;
                    _mepFlowEnabled = false;
                    try { _mepRenderer?.SetEnabled(false, _camera.Position); } catch { }
                    MepPanel.Visibility = Visibility.Collapsed;
                    LoadingGate.Visibility = Visibility.Collapsed;
                    _pressedKeys.Clear();
                    ClearDoubleTapSprint();
                    GameViewport.Focus();
                    ShowToast("Panneau Fluides fermé après une erreur contenue");
                    return;
                }

                _loadingFailed = true;
                _readyToPlay = false;
                _mepFlowEnabled = false;
                LoadingGate.Visibility = Visibility.Visible;
                LoadingProgress.Visibility = Visibility.Collapsed;
                LoadingTitleText.Text = "ERREUR CONTENUE";
                LoadingStatusText.Text =
                    "La visite a été arrêtée sans fermer Revit.\n\n" +
                    e.Exception.Message;
                LoadingCloseButton.Content = "Fermer la visite";
            }
            catch (Exception displayException)
            {
                GameRuntimeDiagnostics.Write(
                    "Impossible d'afficher l'erreur contenue",
                    displayException);
            }
        }

        private static bool IsGameRuntimeException(Exception exception)
        {
            string details = exception?.ToString() ?? string.Empty;
            return details.IndexOf("BIMaestro.VideoGames", StringComparison.Ordinal) >= 0 ||
                   details.IndexOf("HelixToolkit", StringComparison.Ordinal) >= 0 ||
                   details.IndexOf("SharpDX", StringComparison.Ordinal) >= 0;
        }

        private void ResetPlayer(bool announce)
        {
            _footPosition = _scene.SpawnFootPosition;
            _yaw = _scene.InitialYawRadians;
            _pitch = 0.0;
            _verticalVelocity = 0.0;
            _flyMode = false;
            _isCrouching = false;
            _currentEyeHeight = EyeHeight;
            ClearDoubleTapSprint();
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
            RebuildMiniMap(true);
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

            _frameSamples++;

            _simulationAccumulator = Math.Min(
                _simulationAccumulator + elapsed,
                PhysicsStepInterval * MaximumPhysicsStepsPerFrame);
            int simulationSteps = Math.Min(
                MaximumPhysicsStepsPerFrame,
                (int)(_simulationAccumulator / PhysicsStepInterval));

            for (int step = 0; step < simulationSteps; step++)
            {
                _previousFootPosition = _footPosition;
                UpdatePlayer(PhysicsStepInterval);
            }
            _simulationAccumulator -= simulationSteps * PhysicsStepInterval;

            double interpolation = Clamp(
                _simulationAccumulator / PhysicsStepInterval,
                0.0,
                1.0);
            _renderFootPosition = Interpolate(
                _previousFootPosition,
                _footPosition,
                interpolation);
            UpdateDoorAnimations(elapsed);
            UpdateCrouchCamera(elapsed);
            UpdateCamera(_renderFootPosition);
            if (_mepRenderer != null && string.IsNullOrWhiteSpace(_mepRuntimeError))
            {
                try
                {
                    _mepRenderer.Update(now, _camera.Position);
                }
                catch (Exception mepException)
                {
                    DisableMepRenderingAfterError(mepException);
                }
            }
            UpdateMiniMap();
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

            if (!_flyMode)
                UpdateCrouchState();

            bool sprint =
                IsDown(Key.LeftShift) ||
                IsDown(Key.RightShift) ||
                (_doubleTapSprintActive && IsDown(_doubleTapSprintKey));
            double speed = _isCrouching
                ? CrouchSpeed
                : (sprint ? SprintSpeed : WalkSpeed);
            _lastSpeed = movement.Length * speed;

            if (_flyMode)
            {
                double verticalInput = Axis(IsDown(Key.Space), IsDown(Key.LeftCtrl) || IsDown(Key.C));
                var flyDirection = new Vector3D(
                    movement.X,
                    movement.Y,
                    verticalInput);
                if (flyDirection.LengthSquared > 1.0)
                    flyDirection.Normalize();

                double effectiveFlySpeed = sprint ? FlySprintSpeed : FlySpeed;
                _lastSpeed = flyDirection.Length * effectiveFlySpeed;
                _footPosition += flyDirection * effectiveFlySpeed * deltaTime;
                _grounded = false;
                _verticalVelocity = 0.0;
                return;
            }

            if (movement.LengthSquared > 1e-8)
            {
                Vector3D displacement = movement * speed * deltaTime;
                // Sur terrain libre, une seule requête remplace les deux requêtes
                // X/Y. Le repli séparé conserve le glissement le long des murs.
                if (!TryMoveHorizontal(displacement.X, displacement.Y))
                {
                    TryMoveHorizontal(displacement.X, 0.0);
                    TryMoveHorizontal(0.0, displacement.Y);
                }
            }

            if (_grounded)
            {
                if (_world.TryFindGround(
                    _footPosition.X,
                    _footPosition.Y,
                    _footPosition.Z + MaximumStepHeight,
                    _footPosition.Z - 2.4,
                    out double ground,
                    out double groundNormalZ))
                {
                    _footPosition.Z = ResolveGroundHeight(
                        ground,
                        groundNormalZ,
                        _footPosition.Z);
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
                    double activeHeight = ActivePlayerHeight;
                    double currentHead = _footPosition.Z + activeHeight;
                    double nextHead = nextZ + activeHeight;
                    if (_world.TryFindCeiling(
                        _footPosition.X,
                        _footPosition.Y,
                        currentHead,
                        nextHead + 0.1,
                        out double ceiling))
                    {
                        nextZ = Math.Min(nextZ, ceiling - activeHeight - 0.06);
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

        private bool TryMoveHorizontal(double deltaX, double deltaY)
        {
            if (Math.Abs(deltaX) < 1e-10 && Math.Abs(deltaY) < 1e-10)
                return true;

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
                    out double candidateGround,
                    out double candidateGroundNormalZ) &&
                candidateGround <= _footPosition.Z + MaximumStepHeight)
            {
                candidate.Z = ResolveGroundHeight(
                    candidateGround,
                    candidateGroundNormalZ,
                    _footPosition.Z);
            }

            if (!_world.IsBodyBlocked(
                candidate,
                PlayerRadius,
                ActivePlayerHeight,
                MaximumStepHeight))
            {
                _footPosition = candidate;
                return true;
            }
            return false;
        }

        private void UpdateCamera(Point3D cameraFootPosition)
        {
            double cosPitch = Math.Cos(_pitch);
            var look = new Vector3D(
                Math.Cos(_yaw) * cosPitch,
                Math.Sin(_yaw) * cosPitch,
                Math.Sin(_pitch));
            look.Normalize();

            _camera.Position = new Point3D(
                cameraFootPosition.X,
                cameraFootPosition.Y,
                cameraFootPosition.Z + _currentEyeHeight);
            _camera.LookDirection = look * 10.0;
            _camera.UpDirection = new Vector3D(0, 0, 1);

            if (_realisticLight)
                HeadLight.Direction = look;
        }

        private double ActivePlayerHeight =>
            _isCrouching ? CrouchPlayerHeight : PlayerHeight;

        private void UpdateCrouchState()
        {
            bool crouchRequested =
                IsDown(Key.LeftCtrl) || IsDown(Key.RightCtrl);
            if (crouchRequested)
            {
                if (!_isCrouching)
                {
                    _isCrouching = true;
                    ModeText.Text = "ACCROUPI";
                }
                return;
            }

            if (!_isCrouching)
                return;

            double crouchedHead = _footPosition.Z + CrouchPlayerHeight;
            double standingHead = _footPosition.Z + PlayerHeight + 0.08;
            bool ceilingBlocksStanding =
                _world.TryFindCeiling(
                    _footPosition.X,
                    _footPosition.Y,
                    crouchedHead,
                    standingHead,
                    out double ceiling) &&
                ceiling <= standingHead;
            if (ceilingBlocksStanding)
                return;

            _isCrouching = false;
            ModeText.Text = "MARCHE";
        }

        private void UpdateCrouchCamera(double elapsed)
        {
            double targetEyeHeight =
                _isCrouching ? CrouchEyeHeight : EyeHeight;
            _currentEyeHeight = MoveTowards(
                _currentEyeHeight,
                targetEyeHeight,
                CrouchTransitionSpeed * elapsed);
        }

        private static double ResolveGroundHeight(
            double ground,
            double groundNormalZ,
            double currentFootZ)
        {
            double targetFootZ = ground + GroundOffset;
            // Les raccords entre facettes coplanaires peuvent différer de quelques
            // centimètres (dalles superposées, finitions ou maillages liés).
            // Sur une surface réellement horizontale, ces raccords ne doivent
            // jamais faire osciller la caméra. Les vraies marches restent très
            // au-dessus de ce seuil.
            if (groundNormalZ >= 0.99998 &&
                Math.Abs(targetFootZ - currentFootZ) <= 0.10)
            {
                return currentFootZ;
            }
            return targetFootZ;
        }

        private void UpdatePerformanceHud()
        {
            if (_fpsClock.Elapsed.TotalSeconds >= 0.45)
            {
                double measuredSeconds = _fpsClock.Elapsed.TotalSeconds;
                double fps = _frameSamples / measuredSeconds;
                _mepRenderer?.AdaptParticleBudget(fps);
                FpsText.Text = Math.Round(fps).ToString("0") + " FPS";
                SpeedText.Text = (_lastSpeed * 0.3048).ToString("0.0") + " m/s";
                FpsText.ToolTip = null;

                _frameSamples = 0;
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
                _scene.Doors.Count.ToString("N0") + " portes" +
                (_scene.MepGraph.HasData
                    ? "  •  " + _scene.MepGraph.Systems.Count.ToString("N0") +
                        " réseaux MEP"
                    : string.Empty);
        }

        private void RevitGameWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (ObjectInfoPanel.Visibility == Visibility.Visible)
                    CloseSelectionPanel();
                else if (MepPanel.Visibility == Visibility.Visible)
                    CloseMepPanel();
                else if (_mouseLookActive)
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

            RegisterForwardDoubleTap(e.Key);

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
            else if (e.Key == Key.M)
            {
                MiniMapOverlay2D.Visibility =
                    MiniMapOverlay2D.Visibility == Visibility.Visible
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                if (MiniMapOverlay2D.Visibility == Visibility.Visible)
                {
                    if (_miniMapReady)
                        UpdateMiniMap();
                    else
                        RebuildMiniMap(true);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.P)
            {
                ToggleMepPanel();
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                _flyMode = !_flyMode;
                _isCrouching = false;
                ClearDoubleTapSprint();
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
                _footPosition.Z + _currentEyeHeight);

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
            if (_doubleTapSprintActive && e.Key == _doubleTapSprintKey)
            {
                _doubleTapSprintActive = false;
                _doubleTapSprintKey = Key.None;
            }
        }

        private void RegisterForwardDoubleTap(Key key)
        {
            if (key != Key.Z && key != Key.W && key != Key.Up)
                return;

            double now = _frameClock.Elapsed.TotalSeconds;
            bool isDoubleTap =
                key == _lastForwardTapKey &&
                now - _lastForwardTapSeconds <= ForwardDoubleTapSeconds;
            if (isDoubleTap)
            {
                _doubleTapSprintActive = true;
                _doubleTapSprintKey = key;
                _lastForwardTapSeconds = double.MinValue;
                _lastForwardTapKey = Key.None;
                return;
            }

            _lastForwardTapSeconds = now;
            _lastForwardTapKey = key;
        }

        private void ClearDoubleTapSprint()
        {
            _doubleTapSprintActive = false;
            _doubleTapSprintKey = Key.None;
            _lastForwardTapSeconds = double.MinValue;
            _lastForwardTapKey = Key.None;
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
            bool inspect =
                _mouseLookActive &&
                _lookButton == MouseButton.Right &&
                !_rightGestureMoved;
            EndMouseLook(MouseButton.Right);
            if (inspect)
                SelectObjectAtScreenPoint(_rightMouseDownPosition);
            e.Handled = true;
        }

        private void BeginMouseLook(MouseButton button, MouseButtonEventArgs e)
        {
            if (!_readyToPlay || _mouseLookActive || !IsVisible)
                return;

            _mouseLookActive = true;
            _lookButton = button;
            _lastMousePosition = e.GetPosition(GameViewport);
            if (button == MouseButton.Right)
            {
                _rightMouseDownPosition = _lastMousePosition;
                _rightGestureMoved = false;
            }
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
        }

        private void GameViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseLookActive || GameViewport.ActualWidth < 10 || GameViewport.ActualHeight < 10)
                return;

            Point position = e.GetPosition(GameViewport);
            if (_lookButton == MouseButton.Right &&
                (position - _rightMouseDownPosition).Length > 9.0)
            {
                _rightGestureMoved = true;
            }
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

        private void SelectObjectAtScreenPoint(Point screenPoint)
        {
            Point3D origin = _camera.Position;
            Vector3D forward = _camera.LookDirection;
            if (forward.LengthSquared < 1e-10 ||
                GameViewport.ActualWidth < 10.0 ||
                GameViewport.ActualHeight < 10.0)
            {
                return;
            }
            forward.Normalize();

            Vector3D right = Vector3D.CrossProduct(
                forward,
                _camera.UpDirection);
            if (right.LengthSquared < 1e-10)
                return;
            right.Normalize();
            Vector3D screenUp = Vector3D.CrossProduct(right, forward);
            screenUp.Normalize();

            double normalizedX =
                screenPoint.X / GameViewport.ActualWidth * 2.0 - 1.0;
            double normalizedY =
                1.0 - screenPoint.Y / GameViewport.ActualHeight * 2.0;
            double horizontalScale =
                Math.Tan(_camera.FieldOfView * Math.PI / 360.0);
            double verticalScale =
                horizontalScale *
                GameViewport.ActualHeight /
                GameViewport.ActualWidth;
            Vector3D direction =
                forward +
                right * (normalizedX * horizontalScale) +
                screenUp * (normalizedY * verticalScale);
            direction.Normalize();

            GameElementData? selected = null;
            double nearestDistance = 300.0;
            GameElementData? boundsFallback = null;
            double nearestFallbackDistance = 300.0;
            var selectableByKey = new Dictionary<string, GameElementData>(
                StringComparer.Ordinal);
            foreach (GameElementData element in _scene.Elements)
            {
                if (!string.IsNullOrWhiteSpace(element.Key))
                    selectableByKey[element.Key] = element;
            }
            foreach (GameElementData element in _scene.Elements)
            {
                Rect3D bounds = element.Bounds;
                if (bounds.IsEmpty ||
                    !TryIntersectRayBounds(origin, direction, bounds, out double boundsDistance) ||
                    boundsDistance >= nearestDistance)
                {
                    continue;
                }

                GameElementData candidate = element;
                if (!string.IsNullOrWhiteSpace(element.SelectionTargetKey))
                {
                    if (!selectableByKey.TryGetValue(
                        element.SelectionTargetKey,
                        out candidate))
                    {
                        // L'enveloppe ne doit jamais masquer un autre objet si
                        // son porteur n'est pas présent dans la vue exportée.
                        continue;
                    }
                }

                if (element.SelectionTriangles.Count == 0)
                {
                    // Quelques objets volontairement non collisionnels (par
                    // exemple les portes) n'ont pas de triangles indexés.
                    // Ils restent sélectionnables en repli, sans pouvoir
                    // masquer une surface réellement touchée.
                    if (boundsDistance < nearestFallbackDistance)
                    {
                        nearestFallbackDistance = boundsDistance;
                        boundsFallback = candidate;
                    }
                    continue;
                }

                foreach (GameTriangle triangle in element.SelectionTriangles)
                {
                    if (TryIntersectRayTriangle(
                            origin,
                            direction,
                            triangle,
                            out double triangleDistance) &&
                        triangleDistance < nearestDistance)
                    {
                        nearestDistance = triangleDistance;
                        selected = candidate;
                    }
                }
            }

            if (selected == null)
                selected = boundsFallback;

            if (selected == null)
            {
                ShowToast("Aucun objet identifié dans le viseur");
                return;
            }

            AddSelectedElement(selected);
            // Les deux panneaux partagent la colonne latérale afin qu'aucun
            // contrôle WPF ne recouvre le swap-chain DirectX.
            MepPanel.Visibility = Visibility.Collapsed;
            ObjectInfoPanel.Visibility = Visibility.Visible;
        }

        private void AddSelectedElement(GameElementData element)
        {
            var item = new GameSelectedElementItem(element, _scene.MepGraph);
            for (int index = _selectedElementHistory.Count - 1;
                index >= 0;
                index--)
            {
                if (_selectedElementHistory[index].UniqueKey == item.UniqueKey)
                    _selectedElementHistory.RemoveAt(index);
            }

            // La dernière inspection reste immédiatement visible en haut.
            _selectedElementHistory.Insert(0, item);
            const int maximumHistoryCount = 100;
            while (_selectedElementHistory.Count > maximumHistoryCount)
                _selectedElementHistory.RemoveAt(_selectedElementHistory.Count - 1);

            UpdateSelectionHistoryUi();
            SelectedElementsList.ScrollIntoView(item);
        }

        private void UpdateSelectionHistoryUi()
        {
            int count = _selectedElementHistory.Count;
            SelectedElementCountText.Text = count == 1
                ? "1 élément inspecté"
                : count + " éléments inspectés";
            EmptySelectionHistoryText.Visibility = count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SelectedElementsList.Visibility = count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            ClearSelectionHistoryButton.IsEnabled = count > 0;
        }

        private void InitializeMepItems()
        {
            _mepSystemItems.Clear();
            foreach (GameMepSystemData system in _scene.MepGraph.Systems
                .OrderBy(system => system.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _mepSystemItems.Add(new GameMepSystemItem(system));
            }

            _mepSourceItems.Clear();
            foreach (GameMepSourceData source in _scene.MepGraph.Sources
                .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _mepSourceItems.Add(new GameMepSourceItem(
                    source,
                    _scene.MepGraph.FindSystem(source.SystemKey)));
            }
            UpdateMepUi();
        }

        private void UpdateMepUi()
        {
            GameMepGraphData graph = _scene.MepGraph;
            MepFlowToggleButton.IsEnabled = graph.HasData && !_mepRecalculationRunning;
            MepFlowToggleButton.Content = _mepFlowEnabled
                ? "Désactiver les flux"
                : "Activer les flux";
            MepStatsText.Text = graph.HasData
                ? graph.Systems.Count.ToString("N0") + " systèmes  •  " +
                    graph.Elements.Count.ToString("N0") + " éléments  •  " +
                    graph.Valves.Count(valve => valve.IsEnabledAsValve).ToString("N0") +
                    " vannes"
                : !string.IsNullOrWhiteSpace(graph.ExtractionError)
                    ? "Analyse MEP indisponible pour cette maquette"
                    : "Aucun réseau de canalisation détecté";

            int activeSources = graph.Sources.Count(source =>
                source.IsActive &&
                source.BoundaryKind == GameMepBoundaryKind.Inlet);
            int activeReturns = graph.Sources.Count(source =>
                source.IsActive &&
                source.BoundaryKind == GameMepBoundaryKind.Outlet);
            if (!string.IsNullOrWhiteSpace(_mepRuntimeError))
                MepStatusText.Text = "Affichage des fluides désactivé sans fermer le jeu : " +
                    _mepRuntimeError;
            else if (!string.IsNullOrWhiteSpace(graph.ExtractionError))
                MepStatusText.Text = "La maquette jouable reste disponible. Détail MEP : " +
                    graph.ExtractionError;
            else if (!graph.HasData)
                MepStatusText.Text = "Le document actif ne contient aucun connecteur de canalisation exploitable.";
            else if (activeSources == 0)
                MepStatusText.Text = "Source principale à définir : fais un clic droit sur la canalisation d'arrivée puis choisis son sens.";
            else if (_mepRecalculationRunning)
                MepStatusText.Text = "Recalcul de la continuité du réseau…";
            else
                MepStatusText.Text = activeSources +
                    (activeSources == 1 ? " arrivée active" : " arrivées actives") +
                    "  •  " + activeReturns +
                    (activeReturns == 1 ? " retour" : " retours") +
                    "  •  " + graph.DirectionConflictCount + " conflit(s) de sens" +
                    "  •  calcul " + graph.LastCalculationMilliseconds.ToString("0.0") + " ms";

            MepNoSourceText.Visibility = activeSources == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            string diagnostics = !string.IsNullOrWhiteSpace(graph.ExtractionError)
                ? "Extraction MEP interrompue sans fermer la scène."
                : graph.OpenConnectorCount.ToString("N0") + " connecteurs ouverts  •  " +
                    graph.UncertainValveCount.ToString("N0") + " vannes à valider  •  " +
                    "analyse " + graph.ExtractionMilliseconds.ToString("0") + " ms";
            if (graph.RestoredSourceCount > 0 || graph.RestoredValveCount > 0)
            {
                diagnostics += "  •  restauré : " +
                    graph.RestoredSourceCount + " source(s), " +
                    graph.RestoredValveCount + " vanne(s)";
            }
            if (graph.RestoredDirectionConstraintCount > 0)
            {
                diagnostics += "  •  " + graph.RestoredDirectionConstraintCount +
                    " sens de pompe restauré(s)";
            }
            if (graph.SkippedScenarioEntryCount > 0)
            {
                diagnostics += "  •  " + graph.SkippedScenarioEntryCount +
                    " ancien(s) réglage(s) ignoré(s)";
            }
            if (!string.IsNullOrWhiteSpace(graph.ScenarioPersistenceError))
                diagnostics += "  •  sauvegarde locale indisponible";
            MepDiagnosticsText.Text = diagnostics;

            foreach (GameMepSystemItem item in _mepSystemItems)
                item.Refresh();
            foreach (GameMepSourceItem item in _mepSourceItems)
                item.Refresh();
        }

        private void MepPanelButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMepPanel();
        }

        private void ToggleMepPanel()
        {
            if (MepPanel.Visibility == Visibility.Visible)
            {
                CloseMepPanel();
                return;
            }

            _pressedKeys.Clear();
            ClearDoubleTapSprint();
            ReleaseMouseLook();
            ObjectInfoPanel.Visibility = Visibility.Collapsed;
            MepPanel.Visibility = Visibility.Visible;
            try
            {
                UpdateMepUi();
            }
            catch (Exception exception)
            {
                // Le panneau est une fonction auxiliaire : une donnée MEP
                // atypique ne doit jamais fermer la fenêtre ni Revit.
                _mepRuntimeError = exception.Message;
                MepFlowToggleButton.IsEnabled = false;
                MepStatusText.Text =
                    "Panneau Fluides indisponible pour cette maquette : " +
                    exception.Message;
                ShowToast("Fluides MEP indisponibles, la visite reste active");
            }
        }

        private void MepCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseMepPanel();
        }

        private void CloseMepPanel()
        {
            MepPanel.Visibility = Visibility.Collapsed;
            _pressedKeys.Clear();
            ClearDoubleTapSprint();
            GameViewport.Focus();
        }

        private void MepFlowToggleButton_Click(object sender, RoutedEventArgs e)
        {
            GameRuntimeDiagnostics.Write(
                "Commande Fluides MEP : " +
                _scene.MepGraph.Elements.Count + " élément(s), " +
                _scene.MepGraph.Elements.Sum(element => element.Paths.Count) +
                " chemin(s), " +
                _scene.MepGraph.Sources.Count(source => source.IsActive) +
                " source(s) active(s)");
            if (!_scene.MepGraph.HasData)
            {
                if (!string.IsNullOrWhiteSpace(_scene.MepGraph.ExtractionError))
                {
                    GameRuntimeDiagnostics.Write(
                        "Fluides MEP indisponibles : " +
                        _scene.MepGraph.ExtractionError);
                }
                ShowToast("Aucun réseau MEP exploitable dans le document actif");
                return;
            }

            bool enable = !_mepFlowEnabled;
            try
            {
                if (enable && _mepRenderer == null)
                    _mepRenderer = new GameMepFlowRenderer(
                        _scene.MepGraph,
                        GameViewport);

                if (_mepRenderer == null)
                    throw new InvalidOperationException(
                        "Le moteur graphique MEP n'a pas pu être initialisé.");

                _mepRenderer.SetEnabled(enable, _camera.Position);
                _mepFlowEnabled = enable;
                _mepRuntimeError = string.Empty;
                GameRuntimeDiagnostics.Write(
                    enable
                        ? "Rendu des flux MEP activé"
                        : "Rendu des flux MEP désactivé");
            }
            catch (Exception exception)
            {
                GameRuntimeDiagnostics.Write(
                    "Activation du rendu des flux MEP impossible",
                    exception);
                DisableMepRenderingAfterError(exception);
                ShowToast("Flux MEP désactivés : " + exception.Message);
                return;
            }
            UpdateMepUi();
            ShowToast(_mepFlowEnabled
                ? "Flux MEP activés"
                : "Flux MEP masqués");
        }

        private void MepSystemFilter_Changed(object sender, RoutedEventArgs e)
        {
            // Une modification de filtre ne doit toucher aux buffers DirectX
            // que si la couche de flux est réellement affichée.
            if (_mepRenderer == null || !_mepFlowEnabled)
            {
                UpdateMepUi();
                return;
            }
            try
            {
                _mepRenderer.RefreshState(_camera.Position);
            }
            catch (Exception exception)
            {
                DisableMepRenderingAfterError(exception);
            }
            UpdateMepUi();
        }

        private void MepSource_Changed(object sender, RoutedEventArgs e)
        {
            if (_mepSimulation == null || !_scenePrepared)
                return;
            SaveMepScenario();
            RecalculateMepAsync("Sources de fluide mises à jour");
        }

        private void MepResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mepRecalculationRunning)
            {
                ShowToast("Attendez la fin du calcul MEP en cours");
                return;
            }
            foreach (GameMepValveData valve in _scene.MepGraph.Valves)
            {
                valve.IsClosed = false;
                valve.IsEnabledAsValve = valve.InitiallyEnabledAsValve;
                valve.WasManuallyOverridden = false;
            }
            foreach (GameMepSourceData source in _scene.MepGraph.Sources
                .Where(source => !source.IsUserCreated))
            {
                source.IsActive = source.InitiallyActive;
                source.WasManuallyOverridden = false;
            }
            foreach (GameMepSourceData source in _scene.MepGraph.Sources
                .Where(source => source.IsUserCreated)
                .ToList())
            {
                _scene.MepGraph.Sources.Remove(source);
            }
            foreach (GameMepSourceItem item in _mepSourceItems
                .Where(item => item.Data.IsUserCreated)
                .ToList())
            {
                _mepSourceItems.Remove(item);
            }
            _scene.MepGraph.DirectionConstraints.Clear();
            foreach (GameMepSystemData system in _scene.MepGraph.Systems)
                system.IsVisible = true;

            SaveMepScenario();
            RecalculateMepAsync("Scénario MEP réinitialisé");
        }

        private void ValveActionButton_Click(object sender, RoutedEventArgs e)
        {
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepValveData? valve = _scene.MepGraph.FindValve(key);
            if (valve == null || !valve.IsEnabledAsValve)
                return;

            valve.IsClosed = !valve.IsClosed;
            valve.WasManuallyOverridden = true;
            SaveMepScenario();
            RecalculateMepAsync(valve.IsClosed
                ? "Vanne fermée : calcul des zones isolées"
                : "Vanne ouverte : continuité restaurée");
        }

        private void ValveOverrideButton_Click(object sender, RoutedEventArgs e)
        {
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepValveData? valve = _scene.MepGraph.FindValve(key);
            if (valve == null)
                return;

            valve.IsEnabledAsValve = !valve.IsEnabledAsValve;
            valve.WasManuallyOverridden = true;
            if (!valve.IsEnabledAsValve)
                valve.IsClosed = false;
            SaveMepScenario();
            RecalculateMepAsync(valve.IsEnabledAsValve
                ? "L'accessoire est maintenant traité comme une vanne"
                : "L'accessoire n'est plus traité comme une vanne");
        }

        private void SourceOverrideButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mepRecalculationRunning)
            {
                ShowToast("Attendez la fin du calcul MEP en cours");
                return;
            }
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepElementData? element = _scene.MepGraph.FindElement(key);
            if (element == null)
                return;

            GameMepSourceData? source = _scene.MepGraph.Sources.FirstOrDefault(
                candidate => string.Equals(
                    candidate.ElementKey,
                    key,
                    StringComparison.Ordinal) &&
                    candidate.BoundaryKind == GameMepBoundaryKind.Inlet);
            if (source == null)
            {
                source = new GameMepSourceData
                {
                    ElementKey = element.Key,
                    SystemKey = element.SystemKey,
                    Name = string.IsNullOrWhiteSpace(element.Name)
                        ? "Source #" + element.ElementId
                        : element.Name,
                    Confidence = GameMepConfidence.Low,
                    IsActive = true,
                    InitiallyActive = false,
                    WasManuallyOverridden = true,
                    IsUserCreated = true,
                    BoundaryKind = GameMepBoundaryKind.Inlet
                };
                _scene.MepGraph.Sources.Add(source);
                _mepSourceItems.Add(new GameMepSourceItem(
                    source,
                    _scene.MepGraph.FindSystem(source.SystemKey)));
            }
            else
            {
                source.IsActive = !source.IsActive;
                source.WasManuallyOverridden = true;
            }

            SaveMepScenario();
            RecalculateMepAsync(source.IsActive
                ? "Source de fluide activée"
                : "Source de fluide désactivée");
        }

        private void SourceForwardButton_Click(object sender, RoutedEventArgs e)
        {
            SetDirectionalPipeBoundary(
                sender as FrameworkElement,
                true,
                GameMepBoundaryKind.Inlet);
        }

        private void SourceReverseButton_Click(object sender, RoutedEventArgs e)
        {
            SetDirectionalPipeBoundary(
                sender as FrameworkElement,
                false,
                GameMepBoundaryKind.Inlet);
        }

        private void ReturnForwardButton_Click(object sender, RoutedEventArgs e)
        {
            SetDirectionalPipeBoundary(
                sender as FrameworkElement,
                true,
                GameMepBoundaryKind.Outlet);
        }

        private void ReturnReverseButton_Click(object sender, RoutedEventArgs e)
        {
            SetDirectionalPipeBoundary(
                sender as FrameworkElement,
                false,
                GameMepBoundaryKind.Outlet);
        }

        private void SetDirectionalPipeBoundary(
            FrameworkElement? sender,
            bool forward,
            GameMepBoundaryKind boundaryKind)
        {
            if (_mepRecalculationRunning)
            {
                ShowToast("Attendez la fin du calcul MEP en cours");
                return;
            }

            string key = sender?.Tag as string ?? string.Empty;
            GameMepElementData? element = _scene.MepGraph.FindElement(key);
            GameMepPathData? path = element?.Paths.FirstOrDefault(candidate =>
                candidate.StartConnector >= 0 &&
                candidate.EndConnector >= 0 &&
                candidate.StartConnector != candidate.EndConnector);
            if (element == null || path == null)
            {
                ShowToast("Le sens de cette canalisation ne peut pas être déterminé");
                return;
            }

            int entry = forward ? path.StartConnector : path.EndConnector;
            int exit = forward ? path.EndConnector : path.StartConnector;
            GameMepSourceData? source = _scene.MepGraph.Sources.FirstOrDefault(
                candidate => string.Equals(
                    candidate.ElementKey,
                    key,
                    StringComparison.Ordinal) &&
                    candidate.BoundaryKind == boundaryKind);
            if (source == null)
            {
                source = new GameMepSourceData
                {
                    ElementKey = element.Key,
                    SystemKey = element.SystemKey,
                    Name = string.IsNullOrWhiteSpace(element.Name)
                        ? "Canalisation #" + element.ElementId
                        : element.Name,
                    Confidence = GameMepConfidence.High,
                    IsActive = true,
                    InitiallyActive = false,
                    WasManuallyOverridden = true,
                    IsUserCreated = true,
                    BoundaryKind = boundaryKind
                };
                _scene.MepGraph.Sources.Add(source);
                _mepSourceItems.Add(new GameMepSourceItem(
                    source,
                    _scene.MepGraph.FindSystem(source.SystemKey)));
            }

            source.EntryConnectorIndex = entry;
            source.ExitConnectorIndex = exit;
            source.IsActive = true;
            source.WasManuallyOverridden = true;
            SaveMepScenario();
            RecalculateMepAsync(forward
                ? (boundaryKind == GameMepBoundaryKind.Inlet
                    ? "Arrivée définie : début vers fin"
                    : "Retour défini : début vers fin")
                : (boundaryKind == GameMepBoundaryKind.Inlet
                    ? "Arrivée définie : fin vers début"
                    : "Retour défini : fin vers début"));
        }

        private void ConstraintForwardButton_Click(object sender, RoutedEventArgs e)
        {
            SetDirectionConstraint(sender as FrameworkElement, true);
        }

        private void ConstraintReverseButton_Click(object sender, RoutedEventArgs e)
        {
            SetDirectionConstraint(sender as FrameworkElement, false);
        }

        private void SetDirectionConstraint(FrameworkElement? sender, bool forward)
        {
            if (_mepRecalculationRunning)
                return;
            string key = sender?.Tag as string ?? string.Empty;
            GameMepElementData? element = _scene.MepGraph.FindElement(key);
            GameMepPathData? path = element?.Paths.FirstOrDefault(candidate =>
                candidate.StartConnector >= 0 &&
                candidate.EndConnector >= 0 &&
                candidate.StartConnector != candidate.EndConnector);
            if (element == null || path == null)
                return;

            int entry = forward ? path.StartConnector : path.EndConnector;
            int exit = forward ? path.EndConnector : path.StartConnector;
            GameMepDirectionConstraintData? constraint =
                _scene.MepGraph.DirectionConstraints.FirstOrDefault(candidate =>
                    string.Equals(candidate.ElementKey, key, StringComparison.Ordinal));
            if (constraint != null && constraint.EntryConnectorIndex == entry &&
                constraint.ExitConnectorIndex == exit)
            {
                _scene.MepGraph.DirectionConstraints.Remove(constraint);
                SaveMepScenario();
                RecalculateMepAsync("Sens imposé retiré");
                return;
            }
            if (constraint == null)
            {
                constraint = new GameMepDirectionConstraintData
                {
                    ElementKey = key
                };
                _scene.MepGraph.DirectionConstraints.Add(constraint);
            }
            constraint.EntryConnectorIndex = entry;
            constraint.ExitConnectorIndex = exit;
            constraint.IsActive = true;
            constraint.WasManuallyOverridden = true;
            SaveMepScenario();
            RecalculateMepAsync(forward
                ? "Sens de pompe imposé : début vers fin"
                : "Sens de pompe imposé : fin vers début");
        }

        private void SaveMepScenario()
        {
            GameMepScenarioStore.QueueSave(_scene.MepGraph);
        }

        private async void RecalculateMepAsync(string completionMessage)
        {
            if (_mepSimulation == null || _isClosing)
                return;

            _mepRecalculationQueued = true;
            if (_mepRecalculationRunning)
                return;

            _mepRecalculationRunning = true;
            if (_mepRenderer != null)
                _mepRenderer.Paused = true;
            UpdateMepUi();
            try
            {
                do
                {
                    _mepRecalculationQueued = false;
                    await Task.Run(() => _mepSimulation.Recalculate());
                }
                while (_mepRecalculationQueued && !_isClosing);

                if (_isClosing)
                    return;
                if (_mepRenderer != null)
                {
                    try
                    {
                        _mepRenderer.RefreshState(_camera.Position);
                    }
                    catch (Exception renderException)
                    {
                        DisableMepRenderingAfterError(renderException);
                    }
                }
                RefreshSelectionHistoryItems();
                ShowToast(completionMessage);
            }
            catch (Exception exception)
            {
                ShowToast("Calcul MEP impossible : " + exception.Message);
            }
            finally
            {
                _mepRecalculationRunning = false;
                if (_mepRenderer != null)
                    _mepRenderer.Paused = false;
                if (!_isClosing)
                    UpdateMepUi();
            }
        }

        private void RefreshSelectionHistoryItems()
        {
            for (int index = 0; index < _selectedElementHistory.Count; index++)
            {
                GameElementData element = _selectedElementHistory[index].Element;
                _selectedElementHistory[index] =
                    new GameSelectedElementItem(element, _scene.MepGraph);
            }
            UpdateSelectionHistoryUi();
        }

        private void DisableMepRenderingAfterError(Exception exception)
        {
            _mepRuntimeError = exception?.Message ?? "Erreur graphique inconnue";
            _mepFlowEnabled = false;
            try { _mepRenderer?.SetEnabled(false, _camera.Position); } catch { }
            if (!_isClosing)
                UpdateMepUi();
        }

        private void CloseSelectionPanelButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            CloseSelectionPanel();
        }

        private void CloseSelectionPanel()
        {
            ObjectInfoPanel.Visibility = Visibility.Collapsed;
            _pressedKeys.Clear();
            ClearDoubleTapSprint();
            GameViewport.Focus();
        }

        private void ClearSelectionHistoryButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            _selectedElementHistory.Clear();
            UpdateSelectionHistoryUi();
            ShowToast("Historique des sélections effacé");
        }

        private static bool TryIntersectRayBounds(
            Point3D origin,
            Vector3D direction,
            Rect3D bounds,
            out double distance)
        {
            if (origin.X >= bounds.X && origin.X <= bounds.X + bounds.SizeX &&
                origin.Y >= bounds.Y && origin.Y <= bounds.Y + bounds.SizeY &&
                origin.Z >= bounds.Z && origin.Z <= bounds.Z + bounds.SizeZ)
            {
                distance = 0.0;
                return false;
            }

            double minimum = 0.03;
            double maximum = 300.0;
            if (!UpdateRayInterval(
                    origin.X,
                    direction.X,
                    bounds.X,
                    bounds.X + bounds.SizeX,
                    ref minimum,
                    ref maximum) ||
                !UpdateRayInterval(
                    origin.Y,
                    direction.Y,
                    bounds.Y,
                    bounds.Y + bounds.SizeY,
                    ref minimum,
                    ref maximum) ||
                !UpdateRayInterval(
                    origin.Z,
                    direction.Z,
                    bounds.Z,
                    bounds.Z + bounds.SizeZ,
                    ref minimum,
                    ref maximum))
            {
                distance = 0.0;
                return false;
            }

            distance = minimum;
            return minimum >= 0.03 && minimum <= maximum;
        }

        private static bool TryIntersectRayTriangle(
            Point3D origin,
            Vector3D direction,
            GameTriangle triangle,
            out double distance)
        {
            // Möller-Trumbore, double face : la sélection doit fonctionner
            // quelle que soit l'orientation des normales exportées par Revit.
            Vector3D edge1 = triangle.B - triangle.A;
            Vector3D edge2 = triangle.C - triangle.A;
            Vector3D perpendicular = Vector3D.CrossProduct(direction, edge2);
            double determinant = Vector3D.DotProduct(edge1, perpendicular);
            if (Math.Abs(determinant) < 1e-10)
            {
                distance = 0.0;
                return false;
            }

            double inverseDeterminant = 1.0 / determinant;
            Vector3D fromA = origin - triangle.A;
            double barycentricU =
                Vector3D.DotProduct(fromA, perpendicular) * inverseDeterminant;
            if (barycentricU < -1e-8 || barycentricU > 1.0 + 1e-8)
            {
                distance = 0.0;
                return false;
            }

            Vector3D cross = Vector3D.CrossProduct(fromA, edge1);
            double barycentricV =
                Vector3D.DotProduct(direction, cross) * inverseDeterminant;
            if (barycentricV < -1e-8 ||
                barycentricU + barycentricV > 1.0 + 1e-8)
            {
                distance = 0.0;
                return false;
            }

            distance = Vector3D.DotProduct(edge2, cross) * inverseDeterminant;
            return distance >= 0.03 && distance <= 300.0;
        }

        private static bool UpdateRayInterval(
            double origin,
            double direction,
            double minimumBound,
            double maximumBound,
            ref double minimum,
            ref double maximum)
        {
            if (Math.Abs(direction) < 1e-10)
                return origin >= minimumBound && origin <= maximumBound;

            double first = (minimumBound - origin) / direction;
            double second = (maximumBound - origin) / direction;
            if (first > second)
            {
                double swap = first;
                first = second;
                second = swap;
            }

            minimum = Math.Max(minimum, first);
            maximum = Math.Min(maximum, second);
            return minimum <= maximum;
        }

        private static string ValueOrFallback(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private void GameViewport_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            PositionMiniMapOverlay();
        }

        private void PositionMiniMapOverlay()
        {
            double viewportWidth = GameViewport.ActualWidth;
            double viewportHeight = GameViewport.ActualHeight;
            if (viewportWidth <= 0.0 || viewportHeight <= 0.0)
                return;

            // Canvas2D retourne une taille desiree nulle par conception. Sans
            // taille explicite, Helix peut donc arranger tout le HUD sur 0 x 0
            // selon le chemin de rendu utilise par le swap chain.
            GameHudCanvas2D.Width = viewportWidth;
            GameHudCanvas2D.Height = viewportHeight;
            Canvas2D.SetLeft(MiniMapOverlay2D, MiniMapOverlayInset);
            Canvas2D.SetTop(MiniMapOverlay2D, MiniMapOverlayInset);
            Canvas2D.SetLeft(Crosshair2D, (viewportWidth - 28.0) * 0.5);
            Canvas2D.SetTop(Crosshair2D, (viewportHeight - 28.0) * 0.5);
        }

        private void InitializeMiniMapPlaceholder()
        {
            try
            {
                var emptySegments = new List<GameMapSegment>();
                MiniMapProjection projection = CreateMiniMapProjection(
                    emptySegments,
                    emptySegments,
                    _scene.Bounds);
                double footZ = _scene.SpawnFootPosition.Z;
                MemoryStream imageStream = CreateMiniMapImage(
                    projection,
                    emptySegments,
                    emptySegments,
                    footZ);

                _miniMapSliceZ = footZ;
                _miniMapWorldMinimumX = projection.MinimumX;
                _miniMapWorldMaximumY = projection.MaximumY;
                _miniMapWorldScale = projection.Scale;
                _miniMapPixelOffsetX = projection.PixelOffsetX;
                _miniMapPixelOffsetY = projection.PixelOffsetY;
                _miniMapImageStream = imageStream;
                MiniMapImage2D.ImageStream = imageStream;
                _miniMapReady = true;
                GameViewport.InvalidateRender();
            }
            catch (Exception exception)
            {
                // Le fond Direct2D du XAML reste visible et la reconstruction
                // complete sera retentee apres la creation du monde de collision.
                Debug.WriteLine(
                    "Mini-carte initiale non creee : " + exception.Message);
            }
        }

        private void UpdateMiniMap()
        {
            if (MiniMapOverlay2D.Visibility != Visibility.Visible)
                return;

            Vector3D look = _camera.LookDirection;
            double horizontalLengthSquared =
                look.X * look.X + look.Y * look.Y;
            if (horizontalLengthSquared > 1e-8)
            {
                double heading = Math.Atan2(look.Y, look.X);
                MiniMapPlayerRotation2D.Angle =
                    -heading * 180.0 / Math.PI;
            }

            if (!_miniMapReady)
                return;

            double now = _frameClock.Elapsed.TotalSeconds;
            bool changedLevel =
                Math.Abs(_footPosition.Z - _miniMapSliceZ) >
                    MiniMapLevelChangeHeight;
            if (changedLevel &&
                now - _lastMiniMapBuildSeconds >= MiniMapRebuildCooldownSeconds)
            {
                RebuildMiniMap(false);
            }

            // Le plan complet du niveau reste fixe. Seule la flèche se déplace
            // dans sa projection, avec la position interpolée de la caméra.
            double playerPixelX = Clamp(
                _miniMapPixelOffsetX +
                    (_renderFootPosition.X - _miniMapWorldMinimumX) *
                    _miniMapWorldScale,
                6.0,
                MiniMapImageSize - 6.0);
            double playerPixelY = Clamp(
                _miniMapPixelOffsetY +
                    (_miniMapWorldMaximumY - _renderFootPosition.Y) *
                    _miniMapWorldScale,
                6.0,
                MiniMapImageSize - 6.0);
            // Une transformation ne relance pas l'arrangement du Canvas2D a
            // chaque image, contrairement a Canvas2D.SetLeft/SetTop. Le
            // marqueur suit ainsi directement la position interpolee du joueur.
            MiniMapPlayerTranslation2D.X = playerPixelX;
            MiniMapPlayerTranslation2D.Y = playerPixelY;
        }

        private async void RebuildMiniMap(bool force)
        {
            if (_world == null ||
                MiniMapOverlay2D.Visibility != Visibility.Visible)
            {
                return;
            }

            if (_miniMapBuildInProgress)
            {
                _miniMapRebuildQueued |= force;
                return;
            }

            double now = _frameClock.Elapsed.TotalSeconds;
            if (!force &&
                now - _lastMiniMapBuildSeconds <
                    MiniMapRebuildCooldownSeconds)
            {
                return;
            }

            double footZ = _footPosition.Z;
            double planSliceZ =
                footZ + Clamp(_currentEyeHeight * 0.62, 1.5, 4.0);
            Rect3D sceneBounds = _scene.Bounds;
            double planCenterX = sceneBounds.IsEmpty
                ? _footPosition.X
                : sceneBounds.X + sceneBounds.SizeX * 0.5;
            double planCenterY = sceneBounds.IsEmpty
                ? _footPosition.Y
                : sceneBounds.Y + sceneBounds.SizeY * 0.5;
            double planRadius = sceneBounds.IsEmpty
                ? 45.0
                : Math.Max(sceneBounds.SizeX, sceneBounds.SizeY) * 0.5 + 2.0;
            var doorSegments = new List<GameMapSegment>();
            foreach (GameDoorData door in _scene.Doors)
            {
                if (Math.Abs(door.Center.Z - planSliceZ) >
                        _currentEyeHeight + 2.0)
                {
                    continue;
                }

                doorSegments.Add(new GameMapSegment(
                    door.Hinge.X,
                    door.Hinge.Y,
                    door.SecondHinge.X,
                    door.SecondHinge.Y));
            }

            _miniMapBuildInProgress = true;
            _lastMiniMapBuildSeconds = now;
            try
            {
                MiniMapBuildResult result = await Task.Run(() =>
                {
                    IList<GameMapSegment> wallSegments =
                        _world.GetMiniMapSegments(
                            planCenterX,
                            planCenterY,
                            planSliceZ,
                            planRadius,
                            12000);
                    MiniMapProjection projection =
                        CreateMiniMapProjection(
                            wallSegments,
                            doorSegments,
                            sceneBounds);
                    MemoryStream imageStream = CreateMiniMapImage(
                        projection,
                        wallSegments,
                        doorSegments,
                        footZ);
                    return new MiniMapBuildResult(
                        footZ,
                        imageStream,
                        projection);
                });

                if (_isClosing)
                {
                    result.ImageStream.Dispose();
                    return;
                }

                _miniMapSliceZ = result.SliceZ;
                _miniMapWorldMinimumX = result.Projection.MinimumX;
                _miniMapWorldMaximumY = result.Projection.MaximumY;
                _miniMapWorldScale = result.Projection.Scale;
                _miniMapPixelOffsetX = result.Projection.PixelOffsetX;
                _miniMapPixelOffsetY = result.Projection.PixelOffsetY;
                _miniMapReady = true;
                MemoryStream? previousStream = _miniMapImageStream;
                _miniMapImageStream = result.ImageStream;
                MiniMapImage2D.ImageStream = null;
                MiniMapImage2D.ImageStream = result.ImageStream;
                try { previousStream?.Dispose(); } catch { }
                UpdateMiniMap();
                GameViewport.InvalidateRender();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "Mini-carte non reconstruite : " + exception.Message);
            }
            finally
            {
                _miniMapBuildInProgress = false;
                if (!_isClosing && _miniMapRebuildQueued)
                {
                    _miniMapRebuildQueued = false;
                    RebuildMiniMap(true);
                }
            }
        }

        private static MiniMapProjection CreateMiniMapProjection(
            IEnumerable<GameMapSegment> wallSegments,
            IEnumerable<GameMapSegment> doorSegments,
            Rect3D fallbackBounds)
        {
            double minimumX = double.MaxValue;
            double minimumY = double.MaxValue;
            double maximumX = double.MinValue;
            double maximumY = double.MinValue;

            foreach (GameMapSegment segment in wallSegments)
            {
                minimumX = Math.Min(minimumX, Math.Min(segment.X1, segment.X2));
                minimumY = Math.Min(minimumY, Math.Min(segment.Y1, segment.Y2));
                maximumX = Math.Max(maximumX, Math.Max(segment.X1, segment.X2));
                maximumY = Math.Max(maximumY, Math.Max(segment.Y1, segment.Y2));
            }

            foreach (GameMapSegment segment in doorSegments)
            {
                minimumX = Math.Min(minimumX, Math.Min(segment.X1, segment.X2));
                minimumY = Math.Min(minimumY, Math.Min(segment.Y1, segment.Y2));
                maximumX = Math.Max(maximumX, Math.Max(segment.X1, segment.X2));
                maximumY = Math.Max(maximumY, Math.Max(segment.Y1, segment.Y2));
            }

            if (minimumX == double.MaxValue)
            {
                if (fallbackBounds.IsEmpty)
                {
                    minimumX = -45.0;
                    minimumY = -45.0;
                    maximumX = 45.0;
                    maximumY = 45.0;
                }
                else
                {
                    minimumX = fallbackBounds.X;
                    minimumY = fallbackBounds.Y;
                    maximumX = fallbackBounds.X + fallbackBounds.SizeX;
                    maximumY = fallbackBounds.Y + fallbackBounds.SizeY;
                }
            }

            double width = Math.Max(0.5, maximumX - minimumX);
            double height = Math.Max(0.5, maximumY - minimumY);
            double margin = Math.Max(0.5, Math.Max(width, height) * 0.035);
            minimumX -= margin;
            minimumY -= margin;
            maximumX += margin;
            maximumY += margin;
            width = maximumX - minimumX;
            height = maximumY - minimumY;

            const double imagePadding = 8.0;
            double drawableSize = MiniMapImageSize - imagePadding * 2.0;
            double scale = Math.Min(drawableSize / width, drawableSize / height);
            double pixelOffsetX =
                (MiniMapImageSize - width * scale) * 0.5;
            double pixelOffsetY =
                (MiniMapImageSize - height * scale) * 0.5;
            return new MiniMapProjection(
                minimumX,
                minimumY,
                maximumX,
                maximumY,
                scale,
                pixelOffsetX,
                pixelOffsetY);
        }

        private static MemoryStream CreateMiniMapImage(
            MiniMapProjection projection,
            IEnumerable<GameMapSegment> wallSegments,
            IEnumerable<GameMapSegment> doorSegments,
            double footZ)
        {
            const int textureWidth = 238;
            const int textureHeight = 262;
            const float cardLeft = 12f;
            const float cardTop = 12f;
            const float cardWidth = 226f;
            const float cardHeight = 250f;
            const float mapLeft = 21f;
            const float mapTop = 45f;
            const float mapSize = MiniMapImageSize;
            double gridSpacing = 10.0; // 3,05 m au minimum
            while (gridSpacing * projection.Scale < 18.0)
                gridSpacing *= 2.0;

            var bitmap = new Drawing.Bitmap(
                textureWidth,
                textureHeight,
                DrawingImaging.PixelFormat.Format32bppPArgb);
            using (bitmap)
            using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap))
            using (var cardBrush =
                new Drawing.SolidBrush(Drawing.Color.FromArgb(239, 23, 32, 43)))
            using (var mapBrush =
                new Drawing.SolidBrush(Drawing.Color.FromArgb(245, 14, 20, 28)))
            using (var titleBrush =
                new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 244, 248, 252)))
            using (var captionBrush =
                new Drawing.SolidBrush(Drawing.Color.FromArgb(235, 208, 218, 230)))
            using (var gridPen =
                new Drawing.Pen(Drawing.Color.FromArgb(34, 151, 174, 197), 1f))
            using (var majorGridPen =
                new Drawing.Pen(Drawing.Color.FromArgb(58, 151, 174, 197), 1f))
            using (var borderPen =
                new Drawing.Pen(Drawing.Color.FromArgb(160, 123, 145, 170), 1f))
            using (var wallPen =
                new Drawing.Pen(Drawing.Color.FromArgb(245, 195, 213, 229), 1.2f))
            using (var doorPen =
                new Drawing.Pen(Drawing.Color.FromArgb(255, 72, 218, 241), 1.9f))
            using (var titleFont = new Drawing.Font(
                "Segoe UI",
                8.5f,
                Drawing.FontStyle.Bold,
                Drawing.GraphicsUnit.Point))
            using (var captionFont = new Drawing.Font(
                "Segoe UI",
                8f,
                Drawing.FontStyle.Regular,
                Drawing.GraphicsUnit.Point))
            {
                graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
                graphics.Clear(Drawing.Color.Transparent);
                graphics.FillRectangle(
                    cardBrush,
                    cardLeft,
                    cardTop,
                    cardWidth,
                    cardHeight);
                graphics.DrawRectangle(
                    borderPen,
                    cardLeft + 0.5f,
                    cardTop + 0.5f,
                    cardWidth - 1f,
                    cardHeight - 1f);
                graphics.DrawString(
                    "PLAN DU NIVEAU",
                    titleFont,
                    titleBrush,
                    cardLeft + 10f,
                    cardTop + 8f);
                string elevation =
                    "Z " + (footZ * 0.3048).ToString("0.0") + " m";
                Drawing.SizeF elevationSize =
                    graphics.MeasureString(elevation, captionFont);
                graphics.DrawString(
                    elevation,
                    captionFont,
                    captionBrush,
                    cardLeft + cardWidth - elevationSize.Width - 10f,
                    cardTop + 8f);
                graphics.FillRectangle(
                    mapBrush,
                    mapLeft,
                    mapTop,
                    mapSize,
                    mapSize);
                graphics.SetClip(new Drawing.RectangleF(
                    mapLeft,
                    mapTop,
                    mapSize,
                    mapSize));

                double firstGridX = Math.Ceiling(
                    projection.MinimumX / gridSpacing) *
                    gridSpacing;
                for (double worldX = firstGridX;
                    worldX <= projection.MaximumX;
                    worldX += gridSpacing)
                {
                    float x = mapLeft + (float)(
                        projection.PixelOffsetX +
                        (worldX - projection.MinimumX) * projection.Scale);
                    bool major = IsMajorMiniMapGridLine(worldX, gridSpacing);
                    graphics.DrawLine(
                        major ? majorGridPen : gridPen,
                        x,
                        mapTop,
                        x,
                        mapTop + mapSize);
                }

                double firstGridY = Math.Ceiling(
                    projection.MinimumY / gridSpacing) *
                    gridSpacing;
                for (double worldY = firstGridY;
                    worldY <= projection.MaximumY;
                    worldY += gridSpacing)
                {
                    float y = mapTop + (float)(
                        projection.PixelOffsetY +
                        (projection.MaximumY - worldY) * projection.Scale);
                    bool major = IsMajorMiniMapGridLine(worldY, gridSpacing);
                    graphics.DrawLine(
                        major ? majorGridPen : gridPen,
                        mapLeft,
                        y,
                        mapLeft + mapSize,
                        y);
                }

                wallPen.StartCap = Drawing2D.LineCap.Round;
                wallPen.EndCap = Drawing2D.LineCap.Round;
                doorPen.StartCap = Drawing2D.LineCap.Round;
                doorPen.EndCap = Drawing2D.LineCap.Round;

                foreach (GameMapSegment segment in wallSegments)
                {
                    float x1 = mapLeft + (float)(projection.PixelOffsetX +
                        (segment.X1 - projection.MinimumX) * projection.Scale);
                    float y1 = mapTop + (float)(projection.PixelOffsetY +
                        (projection.MaximumY - segment.Y1) * projection.Scale);
                    float x2 = mapLeft + (float)(projection.PixelOffsetX +
                        (segment.X2 - projection.MinimumX) * projection.Scale);
                    float y2 = mapTop + (float)(projection.PixelOffsetY +
                        (projection.MaximumY - segment.Y2) * projection.Scale);
                    graphics.DrawLine(wallPen, x1, y1, x2, y2);
                }

                foreach (GameMapSegment segment in doorSegments)
                {
                    float x1 = mapLeft + (float)(projection.PixelOffsetX +
                        (segment.X1 - projection.MinimumX) * projection.Scale);
                    float y1 = mapTop + (float)(projection.PixelOffsetY +
                        (projection.MaximumY - segment.Y1) * projection.Scale);
                    float x2 = mapLeft + (float)(projection.PixelOffsetX +
                        (segment.X2 - projection.MinimumX) * projection.Scale);
                    float y2 = mapTop + (float)(projection.PixelOffsetY +
                        (projection.MaximumY - segment.Y2) * projection.Scale);
                    graphics.DrawLine(doorPen, x1, y1, x2, y2);
                }
                graphics.ResetClip();
                graphics.DrawRectangle(
                    borderPen,
                    mapLeft + 0.5f,
                    mapTop + 0.5f,
                    mapSize - 1f,
                    mapSize - 1f);
                Drawing.SizeF northSize =
                    graphics.MeasureString("N", titleFont);
                graphics.DrawString(
                    "N",
                    titleFont,
                    titleBrush,
                    mapLeft + (mapSize - northSize.Width) * 0.5f,
                    mapTop + 2f);

                var stream = new MemoryStream(32 * 1024);
                bitmap.Save(stream, DrawingImaging.ImageFormat.Png);
                stream.Position = 0;
                return stream;
            }
        }

        private static bool IsMajorMiniMapGridLine(
            double coordinate,
            double gridSpacing)
        {
            double majorSpacing = gridSpacing * 5.0;
            return Math.Abs(
                coordinate / majorSpacing -
                Math.Round(coordinate / majorSpacing)) < 1e-6;
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

        private sealed class GameSelectedElementItem
        {
            public GameSelectedElementItem(
                GameElementData element,
                GameMepGraphData mepGraph)
            {
                Element = element;
                UniqueKey = string.IsNullOrWhiteSpace(element.Key)
                    ? element.DocumentTitle + "|" + element.ElementId
                    : element.Key;
                Name = ValueOrFallback(element.Name, "Élément sans nom");
                IdText = "#" + element.ElementId;
                CategoryText =
                    "Catégorie : " +
                    ValueOrFallback(element.Category, "Non renseignée");
                TypeText =
                    "Type : " +
                    ValueOrFallback(element.TypeName, "Non renseigné");
                LevelText =
                    "Niveau : " +
                    ValueOrFallback(element.LevelName, "Non renseigné");
                ModelText =
                    "Maquette : " +
                    ValueOrFallback(element.DocumentTitle, "Document actif");

                GameMepElementData? mepElement = mepGraph.FindElement(UniqueKey);
                // Repli utile pour les documents migrés vers Revit 2024+ :
                // le chemin d'un document cloud ou détaché peut changer
                // entre l'export 3D et l'analyse MEP, alors que l'ElementId
                // reste stable dans le document actif.
                if (mepElement == null &&
                    string.Equals(
                        element.DocumentTitle,
                        mepGraph.DocumentTitle,
                        StringComparison.CurrentCultureIgnoreCase))
                {
                    mepElement = mepGraph.FindElement(element.ElementId);
                    if (mepElement != null)
                        UniqueKey = mepElement.Key;
                }
                if (mepElement == null)
                {
                    MepVisibility = Visibility.Collapsed;
                    ValveActionVisibility = Visibility.Collapsed;
                    ValveOverrideVisibility = Visibility.Collapsed;
                    SourceActionVisibility = Visibility.Collapsed;
                    SourceDirectionVisibility = Visibility.Collapsed;
                    ReturnDirectionVisibility = Visibility.Collapsed;
                    ConstraintDirectionVisibility = Visibility.Collapsed;
                    return;
                }

                MepVisibility = Visibility.Visible;
                SystemText = "Réseau MEP : " +
                    ValueOrFallback(mepElement.SystemName, "Non affecté") +
                    (string.IsNullOrWhiteSpace(mepElement.Classification)
                        ? string.Empty
                        : "  •  " + mepElement.Classification);
                GameMepPathData? representativePath = mepElement.Paths.FirstOrDefault();
                FlowText = "État : " + ToFrenchFlowState(mepElement.FlowState) +
                    (representativePath == null
                        ? string.Empty
                        : "\nSens : " + ToFrenchDirection(representativePath));

                GameMepSourceData? source = mepGraph.Sources.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.ElementKey,
                        UniqueKey,
                        StringComparison.Ordinal) &&
                        candidate.BoundaryKind == GameMepBoundaryKind.Inlet);
                GameMepSourceData? outlet = mepGraph.Sources.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.ElementKey,
                        UniqueKey,
                        StringComparison.Ordinal) &&
                        candidate.BoundaryKind == GameMepBoundaryKind.Outlet);
                GameMepDirectionConstraintData? constraint =
                    mepGraph.DirectionConstraints.FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.ElementKey,
                            UniqueKey,
                            StringComparison.Ordinal));
                GameMepPathData? directionalPath = mepElement.Paths.FirstOrDefault(path =>
                    path.StartConnector >= 0 &&
                    path.EndConnector >= 0 &&
                    path.StartConnector != path.EndConnector);
                bool supportsDirection =
                    mepElement.IsPipeCurve &&
                    mepElement.ConnectorIndices.Count == 2 &&
                    directionalPath != null;
                SourceDirectionVisibility = supportsDirection
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ReturnDirectionVisibility = supportsDirection
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                bool supportsConstraint =
                    !mepElement.IsPipeCurve &&
                    mepElement.ConnectorIndices.Count == 2 &&
                    directionalPath != null;
                ConstraintDirectionVisibility = supportsConstraint
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                SourceActionVisibility = source != null || !supportsDirection
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                SourceActionText = source == null
                    ? "Définir comme source de fluide"
                    : source.IsActive
                        ? "Désactiver cette source"
                        : "Activer cette source";
                SourceForwardActionText = source != null &&
                    directionalPath != null &&
                    source.IsActive &&
                    source.EntryConnectorIndex == directionalPath.StartConnector
                        ? "✓ Sens début → fin"
                        : "Choisir le sens début → fin";
                SourceReverseActionText = source != null &&
                    directionalPath != null &&
                    source.IsActive &&
                    source.EntryConnectorIndex == directionalPath.EndConnector
                        ? "✓ Sens fin → début"
                        : "Choisir le sens fin → début";
                ReturnForwardActionText = outlet != null &&
                    directionalPath != null && outlet.IsActive &&
                    outlet.EntryConnectorIndex == directionalPath.StartConnector
                        ? "✓ Retour début → fin"
                        : "Définir retour début → fin";
                ReturnReverseActionText = outlet != null &&
                    directionalPath != null && outlet.IsActive &&
                    outlet.EntryConnectorIndex == directionalPath.EndConnector
                        ? "✓ Retour fin → début"
                        : "Définir retour fin → début";
                ConstraintForwardActionText = constraint != null &&
                    directionalPath != null && constraint.IsActive &&
                    constraint.EntryConnectorIndex == directionalPath.StartConnector
                        ? "✓ Pompe début → fin (retirer)"
                        : "Imposer pompe début → fin";
                ConstraintReverseActionText = constraint != null &&
                    directionalPath != null && constraint.IsActive &&
                    constraint.EntryConnectorIndex == directionalPath.EndConnector
                        ? "✓ Pompe fin → début (retirer)"
                        : "Imposer pompe fin → début";

                GameMepValveData? valve = mepGraph.FindValve(UniqueKey);
                if (valve == null)
                {
                    ValveActionVisibility = Visibility.Collapsed;
                    ValveOverrideVisibility = Visibility.Collapsed;
                    return;
                }

                ValveOverrideVisibility = Visibility.Visible;
                ValveActionVisibility = valve.IsEnabledAsValve
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ValveText = valve.IsEnabledAsValve
                    ? "Vanne " + (valve.IsClosed ? "fermée" : "ouverte") +
                        "  •  confiance " + ToFrenchConfidence(valve.Confidence) +
                        "\nAmont : " + ToFrenchFlowState(valve.UpstreamState) +
                        "  •  Aval : " + ToFrenchFlowState(valve.DownstreamState) +
                        "\nDétection : " + valve.DetectionReason
                    : "Accessoire potentiellement commandable  •  confiance " +
                        ToFrenchConfidence(valve.Confidence);
                ValveActionText = valve.IsClosed
                    ? "Ouvrir la vanne"
                    : "Fermer la vanne";
                ValveOverrideActionText = valve.IsEnabledAsValve
                    ? "Ne plus traiter comme vanne"
                    : "Marquer comme vanne";
            }

            public GameElementData Element { get; }
            public string UniqueKey { get; }
            public string Name { get; }
            public string IdText { get; }
            public string CategoryText { get; }
            public string TypeText { get; }
            public string LevelText { get; }
            public string ModelText { get; }
            public Visibility MepVisibility { get; } = Visibility.Collapsed;
            public string SystemText { get; } = string.Empty;
            public string FlowText { get; } = string.Empty;
            public string ValveText { get; } = string.Empty;
            public string ValveActionText { get; } = string.Empty;
            public string ValveOverrideActionText { get; } = string.Empty;
            public Visibility ValveActionVisibility { get; } = Visibility.Collapsed;
            public Visibility ValveOverrideVisibility { get; } = Visibility.Collapsed;
            public string SourceActionText { get; } = string.Empty;
            public Visibility SourceActionVisibility { get; } = Visibility.Collapsed;
            public string SourceForwardActionText { get; } = string.Empty;
            public string SourceReverseActionText { get; } = string.Empty;
            public Visibility SourceDirectionVisibility { get; } = Visibility.Collapsed;
            public string ReturnForwardActionText { get; } = string.Empty;
            public string ReturnReverseActionText { get; } = string.Empty;
            public Visibility ReturnDirectionVisibility { get; } = Visibility.Collapsed;
            public string ConstraintForwardActionText { get; } = string.Empty;
            public string ConstraintReverseActionText { get; } = string.Empty;
            public Visibility ConstraintDirectionVisibility { get; } = Visibility.Collapsed;

            private static string ToFrenchDirection(GameMepPathData path)
            {
                switch (path.DirectionState)
                {
                    case GameMepDirectionState.Resolved:
                        return path.DirectionReason;
                    case GameMepDirectionState.Conflict:
                        return "conflit — flèches arrêtées";
                    default:
                        return "indéterminé";
                }
            }

            private static string ToFrenchFlowState(GameMepFlowState state)
            {
                switch (state)
                {
                    case GameMepFlowState.Supplied: return "alimenté";
                    case GameMepFlowState.Isolated: return "isolé";
                    default: return "indéterminé (source manquante)";
                }
            }

            private static string ToFrenchConfidence(GameMepConfidence confidence)
            {
                switch (confidence)
                {
                    case GameMepConfidence.High: return "élevée";
                    case GameMepConfidence.Medium: return "moyenne";
                    default: return "faible — à valider";
                }
            }
        }

        private sealed class MiniMapBuildResult
        {
            public MiniMapBuildResult(
                double sliceZ,
                MemoryStream imageStream,
                MiniMapProjection projection)
            {
                SliceZ = sliceZ;
                ImageStream = imageStream;
                Projection = projection;
            }

            public double SliceZ { get; }
            public MemoryStream ImageStream { get; }
            public MiniMapProjection Projection { get; }
        }

        private sealed class MiniMapProjection
        {
            public MiniMapProjection(
                double minimumX,
                double minimumY,
                double maximumX,
                double maximumY,
                double scale,
                double pixelOffsetX,
                double pixelOffsetY)
            {
                MinimumX = minimumX;
                MinimumY = minimumY;
                MaximumX = maximumX;
                MaximumY = maximumY;
                Scale = scale;
                PixelOffsetX = pixelOffsetX;
                PixelOffsetY = pixelOffsetY;
            }

            public double MinimumX { get; }
            public double MinimumY { get; }
            public double MaximumX { get; }
            public double MaximumY { get; }
            public double Scale { get; }
            public double PixelOffsetX { get; }
            public double PixelOffsetY { get; }
        }

    }
}

using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const double ForwardDoubleTapSeconds = 0.34;
        private const double HoverQueryIntervalSeconds = 1.0 / 15.0;

        private readonly GameSceneData _scene;
        private GameCollisionWorld _world = null!;
        private GameSelectionIndex _selectionIndex = null!;
        private readonly LineGeometryModel3D _hoverBoundsModel;
        private bool _hoverBoundsModelAttached;
        private readonly LineGeometryModel3D _selectedBoundsModel;
        private bool _selectedBoundsModelAttached;
        private readonly IList<GameGpuDoorAnimation> _doors =
            new List<GameGpuDoorAnimation>();
        private readonly ObservableCollection<GameMepSystemItem> _mepSystemItems =
            new ObservableCollection<GameMepSystemItem>();
        private readonly ObservableCollection<GameMepSourceItem> _mepSourceItems =
            new ObservableCollection<GameMepSourceItem>();
        private readonly ObservableCollection<GameMepDiagnosticItem>
            _mepDiagnosticItems =
                new ObservableCollection<GameMepDiagnosticItem>();
        private readonly ObservableCollection<GameMepDiagnosticFilterOption>
            _mepDiagnosticSystemOptions =
                new ObservableCollection<GameMepDiagnosticFilterOption>();
        private readonly HxPerspectiveCamera _camera;
        private readonly DefaultEffectsManager _effectsManager;
        private readonly HashSet<Key> _pressedKeys = new HashSet<Key>();
        private readonly ObservableCollection<GameSelectedElementItem>
            _selectedElementHistory =
                new ObservableCollection<GameSelectedElementItem>();
        private readonly ObservableCollection<GameSelectedElementItem>
            _currentSelectedElement =
                new ObservableCollection<GameSelectedElementItem>();
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
        private readonly DispatcherTimer _toastTimer;
        private readonly GameMepScenarioHistory _mepScenarioHistory =
            new GameMepScenarioHistory();
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
        private Point _leftMouseDownPosition;
        private bool _leftGestureMoved;
        private bool _realisticLight = true;
        private bool _isClosing;
        private bool _scenePrepared;
        private bool _readyToPlay;
        private bool _loadingFailed;
        private bool _loadingGateDismissed;
        private int _renderWarmupFrames;
        private double _lastForwardTapSeconds = double.MinValue;
        private Key _lastForwardTapKey = Key.None;
        private Key _doubleTapSprintKey = Key.None;
        private bool _doubleTapSprintActive;
        private GameMepSimulationEngine? _mepSimulation;
        private GameMepFlowRenderer? _mepRenderer;
        private bool _mepFlowEnabled;
        private bool _mepValveMarkersEnabled;
        private bool _mepRecalculationRunning;
        private bool _mepRecalculationQueued;
        private bool _bindingMepDiagnosticFilters;
        private string _mepRuntimeError = string.Empty;
        private GameMepNetworkTraceResult? _mepNetworkTrace;
        private GameSelectionHit? _hoverHit;
        private string _hoveredElementKey = string.Empty;
        private double _lastHoverQuerySeconds = double.MinValue;
        private string _directionPickerElementKey = string.Empty;
        private GameMepBoundaryKind? _directionPickerBoundaryKind;

        internal RevitGameWindow(GameSceneData scene)
        {
            _scene = scene ?? throw new ArgumentNullException(nameof(scene));

            InitializeComponent();
            SelectedElementsList.ItemsSource = _currentSelectedElement;
            SelectedElementHistoryList.ItemsSource = _selectedElementHistory;
            MepSystemsList.ItemsSource = _mepSystemItems;
            MepSourcesList.ItemsSource = _mepSourceItems;
            MepDiagnosticsList.ItemsSource = _mepDiagnosticItems;
            InitializeMepDiagnosticFilters();
            InitializeMepItems();
            UpdateSelectionHistoryUi();
            UpdateMepHistoryUi();

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
            _hoverBoundsModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 4.5,
                Smoothness = 1.0,
                FixedSize = true,
                DepthBias = -42000,
                SlopeScaledDepthBias = -4.0,
                RenderOrder = 2003,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };
            _selectedBoundsModel = new LineGeometryModel3D
            {
                Color = Colors.White,
                Thickness = 6.0,
                Smoothness = 1.0,
                FixedSize = true,
                DepthBias = -42500,
                SlopeScaledDepthBias = -4.2,
                RenderOrder = 2003,
                EnableViewFrustumCheck = true,
                IsHitTestVisible = false,
                IsRendering = false
            };

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
            GameViewport.MouseLeave += GameViewport_MouseLeave;
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
            PositionHudOverlay();
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
            _mepRecalculationQueued = false;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            GameViewport.SizeChanged -= GameViewport_SizeChanged;
            GameViewport.MouseLeave -= GameViewport_MouseLeave;
            GameViewport.OnRendered -= GameViewport_OnRendered;
            GameViewport.RenderExceptionOccurred -= GameViewport_RenderExceptionOccurred;
            Dispatcher.UnhandledException -= GameDispatcher_UnhandledException;
            ReleaseMouseLook();
            _pressedKeys.Clear();
            try { _mepRenderer?.Dispose(); } catch { }
            _mepRenderer = null;
            if (_hoverBoundsModelAttached)
            {
                try { GameViewport.Items.Remove(_hoverBoundsModel); } catch { }
                _hoverBoundsModelAttached = false;
            }
            if (_selectedBoundsModelAttached)
            {
                try { GameViewport.Items.Remove(_selectedBoundsModel); } catch { }
                _selectedBoundsModelAttached = false;
            }
            try { GameViewport.Dispose(); } catch { }
            try { _effectsManager.Dispose(); } catch { }
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

                SetLoadingStatus("Indexation des objets sélectionnables…");
                _selectionIndex = new GameSelectionIndex(_scene.Elements);
                GameRuntimeDiagnostics.Write(
                    "PrepareScene - index de sélection : " +
                    _selectionIndex.ElementCount + " éléments");

                SetLoadingStatus("Calcul de la continuité des réseaux MEP…");
                _mepSimulation = new GameMepSimulationEngine(_scene.MepGraph);
                _mepSimulation.Recalculate();
                RefreshMepDiagnosticItems();
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
                    _mepValveMarkersEnabled = false;
                    try { _mepRenderer?.SetEnabled(false, _camera.Position); } catch { }
                    try { _mepRenderer?.SetValveMarkersEnabled(false, _camera.Position); } catch { }
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
                _mepValveMarkersEnabled = false;
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
            // Le joueur peut terminer une rotation sans déplacer à nouveau la
            // souris. Le plafond interne de 15 Hz garde cette requête légère.
            if (!_mouseLookActive && GameViewport.IsMouseOver)
                UpdateHoveredElement(Mouse.GetPosition(GameViewport), false);
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
                (!_flyMode &&
                    (IsDown(Key.LeftShift) || IsDown(Key.RightShift))) ||
                (_doubleTapSprintActive && IsDown(_doubleTapSprintKey));
            double speed = _isCrouching
                ? CrouchSpeed
                : (sprint ? SprintSpeed : WalkSpeed);
            _lastSpeed = movement.Length * speed;

            if (_flyMode)
            {
                double verticalInput = Axis(
                    IsDown(Key.Space),
                    IsDown(Key.LeftShift) || IsDown(Key.RightShift));
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
                if (_directionPickerBoundaryKind.HasValue)
                    CancelDirectionPicker(true);
                else if (ControlsHud.Visibility == Visibility.Visible)
                    ControlsHud.Visibility = Visibility.Collapsed;
                else if (ObjectInfoPanel.Visibility == Visibility.Visible)
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

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                e.Key == Key.Z &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                MepUndoButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                (e.Key == Key.Y ||
                 (e.Key == Key.Z &&
                  (Keyboard.Modifiers & ModifierKeys.Shift) != 0)))
            {
                MepRedoButton_Click(this, new RoutedEventArgs());
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
                    ? "Mode vol libre — Espace monte, Maj descend"
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
            _leftMouseDownPosition = e.GetPosition(GameViewport);
            _leftGestureMoved = false;
            BeginMouseLook(MouseButton.Left, e);
            e.Handled = true;
        }

        private void GameViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Point releasedPosition = e.GetPosition(GameViewport);
            bool wasCameraGesture = _leftGestureMoved;
            EndMouseLook(MouseButton.Left);
            if (_readyToPlay && !wasCameraGesture)
                SelectObjectAtScreenPoint(releasedPosition);
            else
                ScheduleHoverRefresh(releasedPosition);
            e.Handled = true;
        }

        private void GameViewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Le clic droit est exclusivement réservé aux actions contextuelles
            // et ne déplace jamais la caméra ni la sélection courante.
            e.Handled = true;
        }

        private void GameViewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            Point releasedPosition = e.GetPosition(GameViewport);
            if (_readyToPlay && !_mouseLookActive)
                ShowValveContextAction(releasedPosition);
            e.Handled = true;
        }

        private void ScheduleHoverRefresh(Point screenPoint)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    if (!_isClosing && !_mouseLookActive && GameViewport.IsMouseOver)
                        UpdateHoveredElement(screenPoint, true);
                }));
        }

        private void BeginMouseLook(MouseButton button, MouseButtonEventArgs e)
        {
            if (!_readyToPlay || _mouseLookActive || !IsVisible)
                return;

            _mouseLookActive = true;
            ClearHoveredElement();
            _lookButton = button;
            _lastMousePosition = e.GetPosition(GameViewport);
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
            Point position = e.GetPosition(GameViewport);
            if (!_mouseLookActive)
            {
                UpdateHoveredElement(position, false);
                return;
            }
            if (GameViewport.ActualWidth < 10 || GameViewport.ActualHeight < 10)
                return;

            bool buttonStillDown =
                _lookButton == MouseButton.Left &&
                e.LeftButton == MouseButtonState.Pressed;
            if (!buttonStillDown)
            {
                ReleaseMouseLook();
                return;
            }

            double deltaX = position.X - _lastMousePosition.X;
            double deltaY = position.Y - _lastMousePosition.Y;
            Vector totalDrag = position - _leftMouseDownPosition;
            if (Math.Abs(totalDrag.X) >= 3.0 || Math.Abs(totalDrag.Y) >= 3.0)
                _leftGestureMoved = true;
            _lastMousePosition = position;

            if (Math.Abs(deltaX) < 0.1 && Math.Abs(deltaY) < 0.1)
                return;

            _yaw += deltaX * 0.00245;
            // Visée non inversée dans la caméra DirectX.
            _pitch = Clamp(_pitch + deltaY * 0.00225, -1.48, 1.48);
            e.Handled = true;
        }

        private void GameViewport_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_mouseLookActive)
                ClearHoveredElement();
        }

        private void SelectObjectAtScreenPoint(Point screenPoint)
        {
            GameSelectionHit? hit = FindSelectionHitAtScreenPoint(screenPoint);
            if (hit == null)
            {
                ShowToast("Aucun objet identifié dans le viseur");
                return;
            }

            ClearHoveredElement();
            AddSelectedElement(hit.Element);
            // Les deux panneaux partagent la colonne latérale afin qu'aucun
            // contrôle WPF ne recouvre le swap-chain DirectX.
            MepPanel.Visibility = Visibility.Collapsed;
            ObjectInfoPanel.Visibility = Visibility.Visible;
        }

        private GameSelectionHit? FindSelectionHitAtScreenPoint(Point screenPoint)
        {
            if (!TryCreateSelectionRay(
                    screenPoint,
                    out Point3D origin,
                    out Vector3D direction))
            {
                return null;
            }
            return _selectionIndex?.FindNearest(origin, direction, 300.0);
        }

        private void ShowValveContextAction(Point screenPoint)
        {
            GameSelectionHit? hit = FindSelectionHitAtScreenPoint(screenPoint);
            if (hit == null)
                return;

            GameMepValveData? valve =
                _scene.MepGraph.FindValve(hit.Element.Key);
            if (valve == null || !valve.IsEnabledAsValve ||
                valve.Kind != GameMepFlowControlKind.IsolationValve)
            {
                return;
            }

            string valveKey = valve.ElementKey;
            var action = new System.Windows.Controls.MenuItem
            {
                Header = valve.IsClosed
                    ? "Ouvrir la vanne"
                    : "Fermer la vanne",
                Foreground = (Brush)FindResource("GameText"),
                Background = (Brush)FindResource("GameBrandDark"),
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 13.0,
                Padding = new Thickness(16.0, 10.0, 16.0, 10.0),
                Cursor = Cursors.Hand
            };
            action.Click += (_, __) => ToggleIsolationValve(valveKey);

            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = GameViewport,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                Background = (Brush)FindResource("GameSurfaceRaised"),
                BorderBrush = (Brush)FindResource("GameBrand"),
                BorderThickness = new Thickness(1.0),
                Padding = new Thickness(3.0)
            };
            menu.Items.Add(action);
            menu.IsOpen = true;
        }

        private void UpdateHoveredElement(Point screenPoint, bool force)
        {
            if (!_readyToPlay || _selectionIndex == null ||
                _directionPickerBoundaryKind.HasValue ||
                Mouse.LeftButton == MouseButtonState.Pressed ||
                Mouse.RightButton == MouseButtonState.Pressed)
            {
                return;
            }
            double now = _frameClock.Elapsed.TotalSeconds;
            if (!force && now - _lastHoverQuerySeconds < HoverQueryIntervalSeconds)
                return;
            _lastHoverQuerySeconds = now;
            if (!TryCreateSelectionRay(
                    screenPoint,
                    out Point3D origin,
                    out Vector3D direction))
            {
                ClearHoveredElement();
                return;
            }

            GameSelectionHit? hit = _selectionIndex.FindNearest(origin, direction, 300.0);
            string nextKey = hit?.Element.Key ?? string.Empty;
            if (string.Equals(_hoveredElementKey, nextKey, StringComparison.Ordinal))
            {
                if (hit != null)
                {
                    PositionHoverLabel(screenPoint);
                    try
                    {
                        if ((_mepRenderer?.SetHoveredElement(nextKey) ?? false))
                            HideHoverBounds();
                    }
                    catch { }
                }
                return;
            }

            _hoverHit = hit;
            _hoveredElementKey = nextKey;
            if (hit == null)
            {
                ClearHoveredElement();
                return;
            }

            bool mepPathVisible = false;
            try
            {
                mepPathVisible = _mepRenderer?.SetHoveredElement(nextKey) ?? false;
            }
            catch { }
            if (mepPathVisible)
                HideHoverBounds();
            else
                ShowHoverBounds(hit.Element.Bounds);

            string category = string.IsNullOrWhiteSpace(hit.Element.Category)
                ? "Élément Revit"
                : hit.Element.Category;
            HoverLabel2D.Text = (string.IsNullOrWhiteSpace(hit.Element.Name)
                    ? "Élément sans nom"
                    : hit.Element.Name) + "  •  " + category;
            HoverLabel2D.Visibility = Visibility.Visible;
            PositionHoverLabel(screenPoint);
        }

        private void PositionHoverLabel(Point screenPoint)
        {
            double left = Math.Max(8.0, Math.Min(
                GameViewport.ActualWidth - HoverLabel2D.Width - 8.0,
                screenPoint.X + 16.0));
            double top = Math.Max(8.0, Math.Min(
                GameViewport.ActualHeight - HoverLabel2D.Height - 8.0,
                screenPoint.Y + 18.0));
            Canvas2D.SetLeft(HoverLabel2D, left);
            Canvas2D.SetTop(HoverLabel2D, top);
        }

        private void ClearHoveredElement()
        {
            _hoverHit = null;
            _hoveredElementKey = string.Empty;
            HoverLabel2D.Visibility = Visibility.Collapsed;
            HideHoverBounds();
            try { _mepRenderer?.SetHoveredElement(string.Empty); } catch { }
        }

        private void ShowHoverBounds(Rect3D bounds)
        {
            if (bounds.IsEmpty)
            {
                HideHoverBounds();
                return;
            }
            LineGeometry3D geometry = BuildBoundsGeometry(
                bounds,
                new SharpDX.Color4(0.48f, 1.0f, 0.72f, 1.0f));
            _hoverBoundsModel.Geometry = geometry;
            _hoverBoundsModel.IsRendering = true;
            if (!_hoverBoundsModelAttached)
            {
                GameViewport.Items.Add(_hoverBoundsModel);
                _hoverBoundsModelAttached = true;
            }
            GameViewport.InvalidateRender();
        }

        private void ShowSelectedBounds(Rect3D bounds)
        {
            if (bounds.IsEmpty)
            {
                _selectedBoundsModel.IsRendering = false;
                return;
            }
            _selectedBoundsModel.Geometry = BuildBoundsGeometry(
                bounds,
                new SharpDX.Color4(1.0f, 0.78f, 0.16f, 1.0f));
            _selectedBoundsModel.IsRendering = true;
            if (!_selectedBoundsModelAttached)
            {
                GameViewport.Items.Add(_selectedBoundsModel);
                _selectedBoundsModelAttached = true;
            }
            GameViewport.InvalidateRender();
        }

        private static LineGeometry3D BuildBoundsGeometry(
            Rect3D bounds,
            SharpDX.Color4 color)
        {
            var positions = new Vector3Collection();
            var indices = new IntCollection();
            var colors = new Color4Collection();
            Point3D[] corners =
            {
                new Point3D(bounds.X, bounds.Y, bounds.Z),
                new Point3D(bounds.X + bounds.SizeX, bounds.Y, bounds.Z),
                new Point3D(bounds.X + bounds.SizeX, bounds.Y + bounds.SizeY, bounds.Z),
                new Point3D(bounds.X, bounds.Y + bounds.SizeY, bounds.Z),
                new Point3D(bounds.X, bounds.Y, bounds.Z + bounds.SizeZ),
                new Point3D(bounds.X + bounds.SizeX, bounds.Y, bounds.Z + bounds.SizeZ),
                new Point3D(bounds.X + bounds.SizeX, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ),
                new Point3D(bounds.X, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ)
            };
            int[,] edges =
            {
                {0,1},{1,2},{2,3},{3,0},{4,5},{5,6},{6,7},{7,4},
                {0,4},{1,5},{2,6},{3,7}
            };
            for (int edge = 0; edge < edges.GetLength(0); edge++)
            {
                int start = positions.Count;
                positions.Add(new SharpDX.Vector3(
                    (float)corners[edges[edge, 0]].X,
                    (float)corners[edges[edge, 0]].Y,
                    (float)corners[edges[edge, 0]].Z));
                positions.Add(new SharpDX.Vector3(
                    (float)corners[edges[edge, 1]].X,
                    (float)corners[edges[edge, 1]].Y,
                    (float)corners[edges[edge, 1]].Z));
                indices.Add(start);
                indices.Add(start + 1);
                colors.Add(color);
                colors.Add(color);
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
            return geometry;
        }

        private void HideHoverBounds()
        {
            if (_hoverBoundsModel != null)
                _hoverBoundsModel.IsRendering = false;
        }

        private void HideSelectedBounds()
        {
            _selectedBoundsModel.IsRendering = false;
        }

        private bool TryCreateSelectionRay(
            Point screenPoint,
            out Point3D origin,
            out Vector3D direction)
        {
            origin = _camera.Position;
            direction = _camera.LookDirection;
            if (GameViewport.ActualWidth < 10.0 ||
                GameViewport.ActualHeight < 10.0)
            {
                return false;
            }

            // Utiliser exactement les matrices de projection du viewport évite
            // le décalage entre l'objet affiché et le rayon calculé à la main,
            // surtout près des bords d'un écran large.
            try
            {
                SharpDX.Ray ray = GameViewport.UnProject(screenPoint);
                origin = new Point3D(
                    ray.Position.X,
                    ray.Position.Y,
                    ray.Position.Z);
                direction = new Vector3D(
                    ray.Direction.X,
                    ray.Direction.Y,
                    ray.Direction.Z);
                if (direction.LengthSquared >= 1e-10)
                {
                    direction.Normalize();
                    return true;
                }
            }
            catch
            {
                // Repli uniquement pendant l'initialisation du swap-chain.
            }

            Vector3D forward = _camera.LookDirection;
            if (forward.LengthSquared < 1e-10)
                return false;
            forward.Normalize();
            Vector3D right = Vector3D.CrossProduct(forward, _camera.UpDirection);
            if (right.LengthSquared < 1e-10)
                return false;
            right.Normalize();
            Vector3D screenUp = Vector3D.CrossProduct(right, forward);
            screenUp.Normalize();
            double normalizedX = screenPoint.X / GameViewport.ActualWidth * 2.0 - 1.0;
            double normalizedY = 1.0 - screenPoint.Y / GameViewport.ActualHeight * 2.0;
            double horizontalScale = Math.Tan(_camera.FieldOfView * Math.PI / 360.0);
            double verticalScale = horizontalScale *
                GameViewport.ActualHeight / GameViewport.ActualWidth;
            direction = forward + right * (normalizedX * horizontalScale) +
                screenUp * (normalizedY * verticalScale);
            direction.Normalize();
            return true;
        }

        private void AddSelectedElement(GameElementData element)
        {
            var item = new GameSelectedElementItem(
                element,
                _scene.MepGraph,
                _mepNetworkTrace);
            ShowSelectedBounds(element.Bounds);
            try
            {
                _mepRenderer?.SetHighlightedElement(item.UniqueKey, _camera.Position);
            }
            catch
            {
                // Le surlignage est décoratif et ne doit jamais empêcher
                // l'ouverture de la fiche de l'objet sélectionné.
            }
            for (int index = _selectedElementHistory.Count - 1;
                index >= 0;
                index--)
            {
                if (_selectedElementHistory[index].UniqueKey == item.UniqueKey)
                    _selectedElementHistory.RemoveAt(index);
            }

            if (_currentSelectedElement.Count > 0 &&
                !string.Equals(
                    _currentSelectedElement[0].UniqueKey,
                    item.UniqueKey,
                    StringComparison.Ordinal))
            {
                _selectedElementHistory.Insert(0, _currentSelectedElement[0]);
            }
            _currentSelectedElement.Clear();
            _currentSelectedElement.Add(item);
            const int maximumHistoryCount = 100;
            while (_selectedElementHistory.Count > maximumHistoryCount)
                _selectedElementHistory.RemoveAt(_selectedElementHistory.Count - 1);

            UpdateSelectionHistoryUi();
            SelectedElementsList.ScrollIntoView(item);
        }

        private void UpdateSelectionHistoryUi()
        {
            int historyCount = _selectedElementHistory.Count;
            bool hasCurrent = _currentSelectedElement.Count > 0;
            SelectedElementCountText.Text = hasCurrent
                ? "1 élément sélectionné" +
                    (historyCount > 0 ? "  •  " + historyCount + " précédent(s)" : string.Empty)
                : "Aucun élément sélectionné";
            EmptySelectionHistoryText.Visibility = hasCurrent
                ? Visibility.Collapsed
                : Visibility.Visible;
            SelectedElementsList.Visibility = hasCurrent
                ? Visibility.Visible
                : Visibility.Collapsed;
            SelectedElementHistoryExpander.Header =
                "Historique (" + historyCount + ")";
            SelectedElementHistoryExpander.Visibility = historyCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ClearSelectionHistoryButton.IsEnabled = historyCount > 0;
        }

        private void RestoreSelectionHistoryButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            GameSelectedElementItem? restored =
                (sender as FrameworkElement)?.Tag as GameSelectedElementItem;
            if (restored == null)
                return;
            _selectedElementHistory.Remove(restored);
            if (_currentSelectedElement.Count > 0 &&
                !string.Equals(
                    _currentSelectedElement[0].UniqueKey,
                    restored.UniqueKey,
                    StringComparison.Ordinal))
            {
                _selectedElementHistory.Insert(0, _currentSelectedElement[0]);
            }
            _currentSelectedElement.Clear();
            _currentSelectedElement.Add(new GameSelectedElementItem(
                restored.Element,
                _scene.MepGraph,
                _mepNetworkTrace));
            try
            {
                _mepRenderer?.SetHighlightedElement(restored.UniqueKey, _camera.Position);
            }
            catch { }
            ShowSelectedBounds(restored.Element.Bounds);
            SelectedElementHistoryExpander.IsExpanded = false;
            UpdateSelectionHistoryUi();
        }

        private void InitializeMepItems()
        {
            _mepSystemItems.Clear();
            foreach (GameMepSystemData system in _scene.MepGraph.Systems
                .OrderBy(system => system.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _mepSystemItems.Add(new GameMepSystemItem(system));
            }

            RebuildMepSourceItems();
            UpdateMepUi();
        }

        private void RebuildMepSourceItems()
        {
            _mepSourceItems.Clear();
            foreach (GameMepSourceData source in _scene.MepGraph.Sources
                .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _mepSourceItems.Add(new GameMepSourceItem(
                    source,
                    _scene.MepGraph.FindSystem(source.SystemKey)));
            }
        }

        private void InitializeMepDiagnosticFilters()
        {
            _bindingMepDiagnosticFilters = true;
            MepDiagnosticSeverityFilter.ItemsSource = new[]
            {
                new GameMepDiagnosticFilterOption("Toutes les gravités"),
                new GameMepDiagnosticFilterOption(
                    "Critiques",
                    GameMepDiagnosticSeverity.Critical),
                new GameMepDiagnosticFilterOption(
                    "Avertissements",
                    GameMepDiagnosticSeverity.Warning),
                new GameMepDiagnosticFilterOption(
                    "Informations",
                    GameMepDiagnosticSeverity.Information)
            };
            MepDiagnosticTypeFilter.ItemsSource = new[]
            {
                new GameMepDiagnosticFilterOption("Tous les types"),
                new GameMepDiagnosticFilterOption("Conflits de sens", GameMepDiagnosticKind.DirectionConflict),
                new GameMepDiagnosticFilterOption("Clapets sans sens", GameMepDiagnosticKind.CheckValveDirectionMissing),
                new GameMepDiagnosticFilterOption("Vannes et accessoires ambigus", GameMepDiagnosticKind.AmbiguousFlowControl),
                new GameMepDiagnosticFilterOption("Composants non classés", GameMepDiagnosticKind.UnknownPassThroughComponent),
                new GameMepDiagnosticFilterOption("Branches sans source", GameMepDiagnosticKind.BranchWithoutSource),
                new GameMepDiagnosticFilterOption("Systèmes différents", GameMepDiagnosticKind.IncompatibleSystems),
                new GameMepDiagnosticFilterOption("Éléments déconnectés", GameMepDiagnosticKind.DisconnectedElement),
                new GameMepDiagnosticFilterOption("Connecteurs ouverts", GameMepDiagnosticKind.OpenConnector),
                new GameMepDiagnosticFilterOption("Réglages sauvegardés invalides", GameMepDiagnosticKind.InvalidSavedSetting)
            };
            MepDiagnosticSystemFilter.ItemsSource = _mepDiagnosticSystemOptions;
            MepDiagnosticSeverityFilter.SelectedIndex = 0;
            MepDiagnosticTypeFilter.SelectedIndex = 0;
            RefreshMepDiagnosticSystemOptions(string.Empty);
            MepDiagnosticSystemFilter.SelectedIndex = 0;
            _bindingMepDiagnosticFilters = false;
        }

        private void RefreshMepDiagnosticSystemOptions(string selectedSystemKey)
        {
            bool previousBindingState = _bindingMepDiagnosticFilters;
            _bindingMepDiagnosticFilters = true;
            _mepDiagnosticSystemOptions.Clear();
            _mepDiagnosticSystemOptions.Add(
                new GameMepDiagnosticFilterOption("Tous les systèmes", string.Empty));
            foreach (GameMepSystemData system in _scene.MepGraph.Systems
                .OrderBy(system => system.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _mepDiagnosticSystemOptions.Add(
                    new GameMepDiagnosticFilterOption(
                        string.IsNullOrWhiteSpace(system.Name) ? system.Key : system.Name,
                        system.Key));
            }
            GameMepDiagnosticFilterOption selected = _mepDiagnosticSystemOptions
                .FirstOrDefault(option => string.Equals(
                    option.SystemKey,
                    selectedSystemKey,
                    StringComparison.Ordinal)) ?? _mepDiagnosticSystemOptions[0];
            MepDiagnosticSystemFilter.SelectedItem = selected;
            _bindingMepDiagnosticFilters = previousBindingState;
        }

        private void RefreshMepDiagnosticItems()
        {
            if (MepDiagnosticsList == null)
                return;
            string selectedSystem =
                (MepDiagnosticSystemFilter.SelectedItem as
                    GameMepDiagnosticFilterOption)?.SystemKey ?? string.Empty;
            RefreshMepDiagnosticSystemOptions(selectedSystem);

            bool showAll = MepDiagnosticShowAllCheckBox.IsChecked == true;
            IEnumerable<GameMepDiagnosticData> modeItems =
                _scene.MepGraph.Diagnostics.Where(diagnostic =>
                    showAll
                        ? !diagnostic.IsAggregate
                        : diagnostic.ShowInSmartMode);
            IList<GameMepDiagnosticData> modeSnapshot = modeItems.ToList();
            GameMepDiagnosticFilterOption? severityOption =
                MepDiagnosticSeverityFilter.SelectedItem as
                    GameMepDiagnosticFilterOption;
            GameMepDiagnosticFilterOption? typeOption =
                MepDiagnosticTypeFilter.SelectedItem as
                    GameMepDiagnosticFilterOption;
            GameMepDiagnosticFilterOption? systemOption =
                MepDiagnosticSystemFilter.SelectedItem as
                    GameMepDiagnosticFilterOption;
            IEnumerable<GameMepDiagnosticData> filtered = modeSnapshot;
            if (severityOption?.Severity != null)
                filtered = filtered.Where(item => item.Severity == severityOption.Severity);
            if (typeOption?.Kind != null)
                filtered = filtered.Where(item => item.Kind == typeOption.Kind);
            if (!string.IsNullOrWhiteSpace(systemOption?.SystemKey))
            {
                filtered = filtered.Where(item => string.Equals(
                    item.SystemKey,
                    systemOption.SystemKey,
                    StringComparison.Ordinal));
            }

            _mepDiagnosticItems.Clear();
            foreach (GameMepDiagnosticData diagnostic in filtered
                .OrderByDescending(item => item.Severity)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Key, StringComparer.Ordinal))
            {
                _mepDiagnosticItems.Add(
                    new GameMepDiagnosticItem(diagnostic, _scene.MepGraph));
            }

            int critical = modeSnapshot.Count(item =>
                item.Severity == GameMepDiagnosticSeverity.Critical);
            int warnings = modeSnapshot.Count(item =>
                item.Severity == GameMepDiagnosticSeverity.Warning);
            int information = modeSnapshot.Count(item =>
                item.Severity == GameMepDiagnosticSeverity.Information);
            MepDiagnosticSummaryText.Text = critical + " critique(s)  •  " +
                warnings + " avertissement(s)  •  " +
                information + " information(s)";
            MepDiagnosticsTabButton.Content =
                "Diagnostics (" + modeSnapshot.Count + ")";
            MepDiagnosticEmptyText.Visibility = _mepDiagnosticItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            MepDiagnosticsList.Visibility = _mepDiagnosticItems.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void MepNetworkTabButton_Click(object sender, RoutedEventArgs e)
        {
            MepNetworkContentPanel.Visibility = Visibility.Visible;
            MepDiagnosticsContentPanel.Visibility = Visibility.Collapsed;
            UpdateMepTabStyles(false);
        }

        private void MepDiagnosticsTabButton_Click(object sender, RoutedEventArgs e)
        {
            MepNetworkContentPanel.Visibility = Visibility.Collapsed;
            MepDiagnosticsContentPanel.Visibility = Visibility.Visible;
            UpdateMepTabStyles(true);
            RefreshMepDiagnosticItems();
        }

        private void MepAdvancedToggleButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = MepAdvancedTabs.Visibility != Visibility.Visible;
            MepAdvancedTabs.Visibility = show
                ? Visibility.Visible
                : Visibility.Collapsed;
            MepAdvancedFooter.Visibility = show
                ? Visibility.Visible
                : Visibility.Collapsed;
            MepNetworkContentPanel.Visibility = show
                ? Visibility.Visible
                : Visibility.Collapsed;
            MepDiagnosticsContentPanel.Visibility = Visibility.Collapsed;
            MepPanel.VerticalAlignment = show
                ? VerticalAlignment.Stretch
                : VerticalAlignment.Top;
            MepAdvancedToggleButton.Content = show
                ? "Masquer les réglages  −"
                : "Réglages et analyses  +";
            UpdateMepTabStyles(false);
        }

        private void UpdateMepTabStyles(bool diagnosticsSelected)
        {
            Brush selected = (Brush)FindResource("GameBrandDark");
            Brush normal = new SolidColorBrush(Color.FromArgb(0x99, 0x17, 0x23, 0x1E));
            try { normal.Freeze(); } catch { }
            MepNetworkTabButton.Background = diagnosticsSelected ? normal : selected;
            MepDiagnosticsTabButton.Background = diagnosticsSelected ? selected : normal;
        }

        private void MepDiagnosticFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (_bindingMepDiagnosticFilters)
                return;
            RefreshMepDiagnosticItems();
        }

        private void MepDiagnosticsList_SelectionChanged(
            object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            GameMepDiagnosticItem? item =
                MepDiagnosticsList.SelectedItem as GameMepDiagnosticItem;
            if (item == null)
                return;
            if (string.IsNullOrWhiteSpace(item.Data.ElementKey))
            {
                try { _mepRenderer?.SetHighlightedElement(string.Empty, _camera.Position); }
                catch { }
                return;
            }
            HighlightMepDiagnosticElement(item.Data.ElementKey);
        }

        private void HighlightMepDiagnosticElement(string elementKey)
        {
            try
            {
                if (_mepRenderer == null && _scenePrepared)
                {
                    _mepRenderer = new GameMepFlowRenderer(
                        _scene.MepGraph,
                        GameViewport);
                }
                _mepRenderer?.SetHighlightedElement(elementKey, _camera.Position);
            }
            catch (Exception exception)
            {
                GameRuntimeDiagnostics.Write(
                    "Surlignage d'un diagnostic MEP impossible",
                    exception);
                ShowToast("Surlignage indisponible, le diagnostic reste consultable");
            }
        }

        private void MepDiagnosticNavigateButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepDiagnosticItem? item = _mepDiagnosticItems.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.Ordinal));
            if (item == null || !item.CanNavigate || !_scenePrepared || !_readyToPlay)
            {
                ShowToast("Cet élément ne peut pas être rejoint dans la vue actuelle");
                return;
            }
            HighlightMepDiagnosticElement(item.Data.ElementKey);
            if (!TryCreateDiagnosticFlightViewpoint(
                    item.Data.Position,
                    out Point3D foot))
            {
                ShowToast("Position du diagnostic inexploitable");
                return;
            }

            _footPosition = foot;
            _previousFootPosition = foot;
            _renderFootPosition = foot;
            _verticalVelocity = 0.0;
            _grounded = false;
            _flyMode = true;
            _isCrouching = false;
            _currentEyeHeight = EyeHeight;
            Vector3D toTarget = item.Data.Position -
                new Point3D(foot.X, foot.Y, foot.Z + EyeHeight);
            if (toTarget.LengthSquared > 1e-8)
            {
                _yaw = Math.Atan2(toTarget.Y, toTarget.X);
                double horizontal = Math.Sqrt(
                    toTarget.X * toTarget.X + toTarget.Y * toTarget.Y);
                _pitch = Math.Max(-1.25, Math.Min(1.25,
                    Math.Atan2(toTarget.Z, Math.Max(0.001, horizontal))));
            }
            ModeText.Text = "VOL LIBRE";
            UpdateCamera(_renderFootPosition);
            CloseMepPanel();
            ShowToast("Rejoint en vol : " + item.ReferenceShortText);
        }

        private bool TryCreateDiagnosticFlightViewpoint(
            Point3D target,
            out Point3D foot)
        {
            foot = new Point3D();
            if (double.IsNaN(target.X) || double.IsInfinity(target.X) ||
                double.IsNaN(target.Y) || double.IsInfinity(target.Y) ||
                double.IsNaN(target.Z) || double.IsInfinity(target.Z))
            {
                return false;
            }

            Vector3D fromTarget = _camera.Position - target;
            // Conserver le côté depuis lequel l'utilisateur regardait la
            // maquette rend la téléportation plus facile à comprendre.
            // Aucun test de collision n'est volontairement effectué : le mode
            // vol libre permet ensuite de traverser parois et équipements.
            if (fromTarget.LengthSquared < 1e-6)
            {
                fromTarget = new Vector3D(
                    -Math.Cos(_yaw),
                    -Math.Sin(_yaw),
                    0.28);
            }
            fromTarget.Normalize();
            Point3D eye = target + fromTarget * 14.0 +
                new Vector3D(0, 0, 2.5);
            foot = new Point3D(eye.X, eye.Y, eye.Z - EyeHeight);
            return true;
        }

        private void UpdateMepUi()
        {
            GameMepGraphData graph = _scene.MepGraph;
            MepFlowToggleButton.IsEnabled = graph.HasData && !_mepRecalculationRunning;
            MepValveMarkersToggleButton.IsEnabled =
                graph.HasData &&
                graph.Valves.Any(valve => valve.IsEnabledAsValve) &&
                !_mepRecalculationRunning;
            MepFlowToggleButton.Content = _mepFlowEnabled
                ? "Flux  •  actifs"
                : "Flux  •  arrêtés";
            MepValveMarkersToggleButton.Content = _mepValveMarkersEnabled
                ? "Vannes  •  visibles"
                : "Vannes  •  masquées";
            Brush activeBrush = (Brush)FindResource("GameBrandDark");
            Brush inactiveBrush = new SolidColorBrush(
                Color.FromArgb(0x99, 0x17, 0x23, 0x1E));
            try { inactiveBrush.Freeze(); } catch { }
            MepFlowToggleButton.Background = _mepFlowEnabled
                ? activeBrush
                : inactiveBrush;
            MepValveMarkersToggleButton.Background = _mepValveMarkersEnabled
                ? activeBrush
                : inactiveBrush;
            int enabledIsolationValves = graph.Valves.Count(valve =>
                valve.IsEnabledAsValve &&
                valve.Kind == GameMepFlowControlKind.IsolationValve);
            int enabledCheckValves = graph.Valves.Count(valve =>
                valve.IsEnabledAsValve &&
                valve.Kind == GameMepFlowControlKind.CheckValve);
            MepStatsText.Text = graph.HasData
                ? graph.Systems.Count.ToString("N0") + " systèmes  •  " +
                    graph.Elements.Count.ToString("N0") + " éléments  •  " +
                    enabledIsolationValves.ToString("N0") + " vannes  •  " +
                    enabledCheckValves.ToString("N0") + " clapets"
                : !string.IsNullOrWhiteSpace(graph.ExtractionError)
                    ? "Analyse MEP indisponible pour cette maquette"
                    : "Aucun réseau de canalisation détecté";

            int activeSources = graph.Sources.Count(source =>
                source.IsActive &&
                source.BoundaryKind == GameMepBoundaryKind.Inlet);
            int activeReturns = graph.Sources.Count(source =>
                source.IsActive &&
                source.BoundaryKind == GameMepBoundaryKind.Outlet);
            MepArrivalSummaryText.Text = activeSources.ToString("N0") +
                (activeSources == 1 ? " active" : " actives");
            MepReturnSummaryText.Text = activeReturns.ToString("N0") +
                (activeReturns == 1 ? " actif" : " actifs");
            if (!string.IsNullOrWhiteSpace(_mepRuntimeError))
                MepStatusText.Text = "Affichage des fluides désactivé sans fermer la maquette BIM : " +
                    _mepRuntimeError;
            else if (!string.IsNullOrWhiteSpace(graph.ExtractionError))
                MepStatusText.Text = "La maquette BIM reste disponible. Détail MEP : " +
                    graph.ExtractionError;
            else if (!graph.HasData)
                MepStatusText.Text = "Le document actif ne contient aucun connecteur de canalisation exploitable.";
            else if (activeSources == 0)
                MepStatusText.Text = "Source principale à définir : fais un clic gauche sur la canalisation d'arrivée puis choisis son sens.";
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

        private void ControlsHelpButton_Click(object sender, RoutedEventArgs e)
        {
            ControlsHud.Visibility = ControlsHud.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            _pressedKeys.Clear();
            ClearDoubleTapSprint();
            ReleaseMouseLook();
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
                MepValveMarkersToggleButton.IsEnabled = false;
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
            if (!_scene.MepGraph.HasData ||
                !_scene.MepGraph.Valves.Any(valve => valve.IsEnabledAsValve))
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

        private void MepValveMarkersToggleButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!_scene.MepGraph.HasData)
            {
                ShowToast("Aucune vanne MEP exploitable dans cette maquette");
                return;
            }

            bool enable = !_mepValveMarkersEnabled;
            try
            {
                if (_mepRenderer == null)
                    _mepRenderer = new GameMepFlowRenderer(
                        _scene.MepGraph,
                        GameViewport);
                _mepRenderer.SetValveMarkersEnabled(enable, _camera.Position);
                _mepValveMarkersEnabled = enable;
                _mepRuntimeError = string.Empty;
            }
            catch (Exception exception)
            {
                GameRuntimeDiagnostics.Write(
                    "Affichage des repères de vannes impossible",
                    exception);
                DisableMepRenderingAfterError(exception);
                ShowToast("Repères de vannes désactivés : " + exception.Message);
                return;
            }
            UpdateMepUi();
            ShowToast(enable
                ? "Repères de vannes affichés"
                : "Repères de vannes masqués");
        }

        private void MepSystemFilter_Changed(object sender, RoutedEventArgs e)
        {
            // Une modification de filtre touche aussi les repères de vannes,
            // même lorsque les flèches de fluide sont masquées.
            if (_mepRenderer == null ||
                (!_mepFlowEnabled && !_mepValveMarkersEnabled &&
                 _mepNetworkTrace == null))
            {
                UpdateMepUi();
                return;
            }
            try
            {
                RebuildActiveNetworkTrace();
                _mepRenderer.SetNetworkTrace(
                    _mepNetworkTrace,
                    _camera.Position);
                _mepRenderer.RefreshState(_camera.Position);
            }
            catch (Exception exception)
            {
                DisableMepRenderingAfterError(exception);
            }
            UpdateMepUi();
            RefreshSelectionHistoryItems();
        }

        private void MepSource_Changed(object sender, RoutedEventArgs e)
        {
            if (_mepSimulation == null || !_scenePrepared)
                return;
            GameMepSourceItem? item =
                (sender as FrameworkElement)?.DataContext as GameMepSourceItem;
            if (item == null)
                return;
            bool activate = !item.Data.IsActive;
            ExecuteMepScenarioMutation(
                activate ? "Activer une source" : "Désactiver une source",
                activate ? "Source de fluide activée" : "Source de fluide désactivée",
                () =>
                {
                    item.Data.IsActive = activate;
                    item.Data.WasManuallyOverridden = true;
                });
        }

        private void MepResetButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteMepScenarioMutation(
                "Réinitialiser le scénario",
                "Scénario MEP réinitialisé — cette action peut être annulée",
                () =>
                {
                    GameMepScenarioReset.ResetValvesToInitial(
                        _scene.MepGraph.Valves);
                    GameMepScenarioReset.ResetSourcesAndDirections(
                        _scene.MepGraph,
                        element => true);
                });
            foreach (GameMepSystemData system in _scene.MepGraph.Systems)
                system.IsVisible = true;
        }

        private void MepResetValvesButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteMepScenarioMutation(
                "Réinitialiser les vannes et clapets",
                "Vannes et clapets remis à leur état initial",
                () => GameMepScenarioReset.ResetValvesToInitial(
                    _scene.MepGraph.Valves));
        }

        private void MepResetSourcesButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteMepScenarioMutation(
                "Réinitialiser les sources et les sens",
                "Sources, retours et corrections de sens réinitialisés",
                () => GameMepScenarioReset.ResetSourcesAndDirections(
                    _scene.MepGraph,
                    element => true));
        }

        private void ResetElementSystemButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepElementData? selected = _scene.MepGraph.FindElement(key);
            if (selected == null || string.IsNullOrWhiteSpace(selected.SystemKey))
            {
                ShowToast("Cet élément n'est associé à aucun système");
                return;
            }
            string systemKey = selected.SystemKey;
            string systemName = _scene.MepGraph.FindSystem(systemKey)?.Name ??
                selected.SystemName;
            ExecuteMepScenarioMutation(
                "Réinitialiser un système",
                "Système réinitialisé : " +
                    ValueOrFallback(systemName, systemKey),
                () =>
                {
                    GameMepScenarioReset.ResetValvesToInitial(
                        _scene.MepGraph.Valves.Where(valve =>
                    {
                        GameMepElementData? element =
                            _scene.MepGraph.FindElement(valve.ElementKey);
                        return element != null &&
                            GameMepScenarioReset.ElementBelongsToSystem(
                                _scene.MepGraph, element, systemKey);
                    }));
                    GameMepScenarioReset.ResetSourcesAndDirections(
                        _scene.MepGraph,
                        element => GameMepScenarioReset.ElementBelongsToSystem(
                            _scene.MepGraph, element, systemKey));
                });
        }

        private void ValveActionButton_Click(object sender, RoutedEventArgs e)
        {
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            ToggleIsolationValve(key);
        }

        private void ToggleIsolationValve(string key)
        {
            GameMepValveData? valve = _scene.MepGraph.FindValve(key);
            if (valve == null || !valve.IsEnabledAsValve ||
                valve.Kind != GameMepFlowControlKind.IsolationValve)
                return;

            bool close = !valve.IsClosed;
            ExecuteMepScenarioMutation(
                close ? "Fermer une vanne" : "Ouvrir une vanne",
                close
                    ? "Vanne fermée : calcul des zones isolées"
                    : "Vanne ouverte : continuité restaurée",
                () =>
                {
                    valve.IsClosed = close;
                    valve.WasManuallyOverridden = true;
                });
        }

        private void ValveOverrideButton_Click(object sender, RoutedEventArgs e)
        {
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepValveData? valve = _scene.MepGraph.FindValve(key);
            if (valve == null)
                return;

            bool enable = !valve.IsEnabledAsValve;
            string actionLabel = valve.Kind == GameMepFlowControlKind.CheckValve
                ? (enable ? "Activer un clapet" : "Ignorer un clapet")
                : (enable ? "Classer comme vanne" : "Refuser comme vanne");
            ExecuteMepScenarioMutation(
                actionLabel,
                enable
                ? (valve.Kind == GameMepFlowControlKind.CheckValve
                    ? "Le clapet anti-retour est actif"
                    : "L'accessoire est maintenant traité comme une vanne")
                : (valve.Kind == GameMepFlowControlKind.CheckValve
                    ? "Le clapet anti-retour est ignoré"
                    : "L'accessoire n'est plus traité comme une vanne"),
                () =>
                {
                    valve.IsEnabledAsValve = enable;
                    valve.WasManuallyOverridden = true;
                    if (!enable)
                        valve.IsClosed = false;
                });
        }

        private void CheckValveDirectionButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_mepRecalculationRunning)
                return;
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepValveData? valve = _scene.MepGraph.FindValve(key);
            GameMepElementData? element = _scene.MepGraph.FindElement(key);
            GameMepPathData? path = element?.Paths.FirstOrDefault(candidate =>
                candidate.StartConnector >= 0 &&
                candidate.EndConnector >= 0 &&
                candidate.StartConnector != candidate.EndConnector);
            if (valve == null || path == null ||
                valve.Kind != GameMepFlowControlKind.CheckValve)
            {
                return;
            }

            ExecuteMepScenarioMutation(
                valve.HasExplicitDirection
                    ? "Inverser un clapet"
                    : "Définir le sens d'un clapet",
                "Sens du clapet anti-retour mis à jour",
                () =>
                {
                    if (valve.HasExplicitDirection)
                    {
                        int previousEntry = valve.EntryConnectorIndex;
                        valve.EntryConnectorIndex = valve.ExitConnectorIndex;
                        valve.ExitConnectorIndex = previousEntry;
                    }
                    else
                    {
                        valve.EntryConnectorIndex = path.FlowForward
                            ? path.StartConnector
                            : path.EndConnector;
                        valve.ExitConnectorIndex = path.FlowForward
                            ? path.EndConnector
                            : path.StartConnector;
                    }
                    valve.IsEnabledAsValve = true;
                    valve.IsClosed = false;
                    valve.WasManuallyOverridden = true;
                });
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
            bool activate = source == null || !source.IsActive;
            ExecuteMepScenarioMutation(
                source == null
                    ? "Ajouter une source"
                    : activate ? "Activer une source" : "Désactiver une source",
                activate ? "Source de fluide activée" : "Source de fluide désactivée",
                () =>
                {
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
                    }
                    else
                    {
                        source.IsActive = activate;
                        source.WasManuallyOverridden = true;
                    }
                });
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

        private void BeginSourceDirectionPicker_Click(
            object sender,
            RoutedEventArgs e)
        {
            BeginDirectionPicker(sender as FrameworkElement,
                GameMepBoundaryKind.Inlet);
        }

        private void BeginReturnDirectionPicker_Click(
            object sender,
            RoutedEventArgs e)
        {
            BeginDirectionPicker(sender as FrameworkElement,
                GameMepBoundaryKind.Outlet);
        }

        private void BeginDirectionPicker(
            FrameworkElement? sender,
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
                candidate.StartConnector != candidate.EndConnector &&
                candidate.Points.Count >= 2);
            if (element == null || path == null ||
                !element.IsPipeCurve || element.ConnectorIndices.Count != 2)
            {
                ShowToast("Cette canalisation ne possède pas deux extrémités exploitables");
                return;
            }

            try
            {
                if (_mepRenderer == null)
                    _mepRenderer = new GameMepFlowRenderer(_scene.MepGraph, GameViewport);
                _mepRenderer.SetDirectionPreview(key, null, _camera.Position);
            }
            catch (Exception exception)
            {
                GameRuntimeDiagnostics.Write("Aperçu A/B indisponible", exception);
                ShowToast("Impossible d'afficher l'aperçu du sens");
                return;
            }

            ClearHoveredElement();
            _directionPickerElementKey = key;
            _directionPickerBoundaryKind = boundaryKind;
            DirectionPickerTitleText.Text = boundaryKind == GameMepBoundaryKind.Inlet
                ? "Définir le sens de l’arrivée"
                : "Définir le sens du retour";
            SelectedElementsList.Visibility = Visibility.Collapsed;
            SelectedElementHistoryExpander.Visibility = Visibility.Collapsed;
            DirectionPickerPanel.Visibility = Visibility.Visible;
        }

        private void DirectionForwardButton_MouseEnter(
            object sender,
            MouseEventArgs e)
        {
            PreviewDirectionChoice(true);
        }

        private void DirectionReverseButton_MouseEnter(
            object sender,
            MouseEventArgs e)
        {
            PreviewDirectionChoice(false);
        }

        private void DirectionChoiceButton_MouseLeave(
            object sender,
            MouseEventArgs e)
        {
            if (_directionPickerBoundaryKind.HasValue)
            {
                try
                {
                    _mepRenderer?.SetDirectionPreview(
                        _directionPickerElementKey,
                        null,
                        _camera.Position);
                }
                catch { }
            }
        }

        private void PreviewDirectionChoice(bool forward)
        {
            if (!_directionPickerBoundaryKind.HasValue)
                return;
            try
            {
                _mepRenderer?.SetDirectionPreview(
                    _directionPickerElementKey,
                    forward,
                    _camera.Position);
            }
            catch { }
        }

        private void DirectionForwardButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmDirectionPicker(true);
        }

        private void DirectionReverseButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmDirectionPicker(false);
        }

        private void ConfirmDirectionPicker(bool forward)
        {
            if (!_directionPickerBoundaryKind.HasValue)
                return;
            string key = _directionPickerElementKey;
            GameMepBoundaryKind boundaryKind = _directionPickerBoundaryKind.Value;
            CancelDirectionPicker(false);
            SetDirectionalPipeBoundary(key, forward, boundaryKind);
        }

        private void CancelDirectionPickerButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            CancelDirectionPicker(true);
        }

        private void CancelDirectionPicker(bool announce)
        {
            if (!_directionPickerBoundaryKind.HasValue &&
                DirectionPickerPanel.Visibility != Visibility.Visible)
            {
                return;
            }
            _directionPickerElementKey = string.Empty;
            _directionPickerBoundaryKind = null;
            try { _mepRenderer?.ClearDirectionPreview(); } catch { }
            DirectionPickerPanel.Visibility = Visibility.Collapsed;
            SelectedElementsList.Visibility = _currentSelectedElement.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SelectedElementHistoryExpander.Visibility =
                _selectedElementHistory.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (announce)
                ShowToast("Choix du sens annulé");
        }

        private void RemoveSourceButton_Click(object sender, RoutedEventArgs e)
        {
            RemoveDirectionalBoundary(
                sender as FrameworkElement,
                GameMepBoundaryKind.Inlet);
        }

        private void RemoveReturnButton_Click(object sender, RoutedEventArgs e)
        {
            RemoveDirectionalBoundary(
                sender as FrameworkElement,
                GameMepBoundaryKind.Outlet);
        }

        private void RemoveDirectionalBoundary(
            FrameworkElement? sender,
            GameMepBoundaryKind boundaryKind)
        {
            string key = sender?.Tag as string ?? string.Empty;
            GameMepSourceData? source = _scene.MepGraph.Sources.FirstOrDefault(
                candidate => candidate.IsUserCreated &&
                    candidate.BoundaryKind == boundaryKind &&
                    string.Equals(candidate.ElementKey, key, StringComparison.Ordinal));
            if (source == null)
                return;
            ExecuteMepScenarioMutation(
                boundaryKind == GameMepBoundaryKind.Inlet
                    ? "Retirer une source"
                    : "Retirer un retour",
                boundaryKind == GameMepBoundaryKind.Inlet
                    ? "Source retirée"
                    : "Retour retiré",
                () => _scene.MepGraph.Sources.Remove(source));
        }

        private void SetDirectionalPipeBoundary(
            FrameworkElement? sender,
            bool forward,
            GameMepBoundaryKind boundaryKind)
        {
            SetDirectionalPipeBoundary(
                sender?.Tag as string ?? string.Empty,
                forward,
                boundaryKind);
        }

        private void SetDirectionalPipeBoundary(
            string key,
            bool forward,
            GameMepBoundaryKind boundaryKind)
        {
            if (_mepRecalculationRunning)
            {
                ShowToast("Attendez la fin du calcul MEP en cours");
                return;
            }

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
            string completionMessage =
                (boundaryKind == GameMepBoundaryKind.Inlet
                    ? "Arrivée définie : "
                    : "Retour défini : ") +
                (forward ? "A vers B" : "B vers A");
            ExecuteMepScenarioMutation(
                boundaryKind == GameMepBoundaryKind.Inlet
                    ? (source == null ? "Ajouter une source orientée" : "Modifier le sens d'une source")
                    : (source == null ? "Ajouter un retour orienté" : "Modifier le sens d'un retour"),
                completionMessage,
                () =>
                {
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
                    }
                    source.EntryConnectorIndex = entry;
                    source.ExitConnectorIndex = exit;
                    source.IsActive = true;
                    source.WasManuallyOverridden = true;
                });
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
                    candidate.Scope ==
                        GameMepDirectionConstraintScope.EquipmentPressureRise &&
                    string.Equals(candidate.ElementKey, key, StringComparison.Ordinal));
            if (constraint != null && constraint.EntryConnectorIndex == entry &&
                constraint.ExitConnectorIndex == exit)
            {
                ExecuteMepScenarioMutation(
                    "Retirer une contrainte de pompe",
                    "Sens imposé retiré",
                    () => _scene.MepGraph.DirectionConstraints.Remove(constraint));
                return;
            }
            ExecuteMepScenarioMutation(
                constraint == null
                    ? "Ajouter une contrainte de pompe"
                    : "Modifier une contrainte de pompe",
                forward
                    ? "Sens de pompe imposé : début vers fin"
                    : "Sens de pompe imposé : fin vers début",
                () =>
                {
                    if (constraint == null)
                    {
                        constraint = new GameMepDirectionConstraintData
                        {
                            ElementKey = key,
                            Scope = GameMepDirectionConstraintScope.EquipmentPressureRise
                        };
                        _scene.MepGraph.DirectionConstraints.Add(constraint);
                    }
                    constraint.EntryConnectorIndex = entry;
                    constraint.ExitConnectorIndex = exit;
                    constraint.IsActive = true;
                    constraint.WasManuallyOverridden = true;
                });
        }

        private void ReverseElementDirectionButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_mepRecalculationRunning)
                return;

            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepElementData? element = _scene.MepGraph.FindElement(key);
            GameMepPathData? path = element?.Paths.FirstOrDefault(candidate =>
                candidate.StartConnector >= 0 &&
                candidate.EndConnector >= 0 &&
                candidate.StartConnector != candidate.EndConnector);
            if (element == null || path == null)
            {
                ShowToast("Le sens de cet élément ne peut pas être inversé");
                return;
            }

            GameMepDirectionConstraintData? constraint =
                _scene.MepGraph.DirectionConstraints.FirstOrDefault(candidate =>
                    candidate.Scope == GameMepDirectionConstraintScope.LocalOverride &&
                    string.Equals(candidate.ElementKey, key, StringComparison.Ordinal));
            if (constraint != null)
            {
                ExecuteMepScenarioMutation(
                    "Retirer une correction locale",
                    "Correction locale retirée : retour au calcul automatique",
                    () => _scene.MepGraph.DirectionConstraints.Remove(constraint));
                return;
            }

            bool newForward = !path.FlowForward;
            int entry = newForward ? path.StartConnector : path.EndConnector;
            int exit = newForward ? path.EndConnector : path.StartConnector;
            ExecuteMepScenarioMutation(
                "Corriger localement un sens",
                "Sens inversé uniquement sur ce tronçon et mémorisé",
                () => _scene.MepGraph.DirectionConstraints.Add(
                    new GameMepDirectionConstraintData
                    {
                        ElementKey = key,
                        Scope = GameMepDirectionConstraintScope.LocalOverride,
                        EntryConnectorIndex = entry,
                        ExitConnectorIndex = exit,
                        IsActive = true,
                        WasManuallyOverridden = true
                    }));
        }

        private void IsolateElementSystemButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            GameMepElementData? element = _scene.MepGraph.FindElement(key);
            if (element == null || string.IsNullOrWhiteSpace(element.SystemKey))
            {
                ShowToast("Cet élément n'est associé à aucun système");
                return;
            }

            GameMepSystemData? target = _scene.MepGraph.FindSystem(element.SystemKey);
            if (target == null)
            {
                ShowToast("Le système de cet élément est introuvable");
                return;
            }

            bool alreadyIsolated = target.IsVisible &&
                _scene.MepGraph.Systems.All(system =>
                    ReferenceEquals(system, target) || !system.IsVisible);
            foreach (GameMepSystemData system in _scene.MepGraph.Systems)
            {
                system.IsVisible = alreadyIsolated || ReferenceEquals(system, target);
            }

            RebuildActiveNetworkTrace();

            if (_mepRenderer != null &&
                (_mepFlowEnabled || _mepValveMarkersEnabled ||
                 _mepNetworkTrace != null))
            {
                try
                {
                    _mepRenderer.SetNetworkTrace(
                        _mepNetworkTrace,
                        _camera.Position);
                    _mepRenderer.RefreshState(_camera.Position);
                }
                catch (Exception exception)
                {
                    DisableMepRenderingAfterError(exception);
                }
            }
            UpdateMepUi();
            RefreshSelectionHistoryItems();
            ShowToast(alreadyIsolated
                ? "Tous les systèmes sont de nouveau visibles"
                : "Système isolé : " + target.Name);
        }

        private void TraceUpstreamButton_Click(object sender, RoutedEventArgs e)
        {
            BeginNetworkTrace(sender, GameMepTraceMode.Upstream);
        }

        private void TraceDownstreamButton_Click(object sender, RoutedEventArgs e)
        {
            BeginNetworkTrace(sender, GameMepTraceMode.Downstream);
        }

        private void TraceFullBranchButton_Click(object sender, RoutedEventArgs e)
        {
            BeginNetworkTrace(sender, GameMepTraceMode.FullBranch);
        }

        private void BeginNetworkTrace(object sender, GameMepTraceMode mode)
        {
            string key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
            ApplyNetworkTrace(key, mode, string.Empty);
        }

        private void TraceBranchButton_Click(object sender, RoutedEventArgs e)
        {
            GameMepTraceBranchItem? branch =
                (sender as FrameworkElement)?.Tag as GameMepTraceBranchItem;
            if (branch == null)
                return;
            ApplyNetworkTrace(
                branch.StartElementKey,
                branch.Mode,
                branch.BranchElementKey);
        }

        private void ApplyNetworkTrace(
            string startElementKey,
            GameMepTraceMode mode,
            string branchElementKey)
        {
            if (_mepRecalculationRunning)
            {
                ShowToast("Suivi indisponible pendant le calcul MEP");
                return;
            }
            try
            {
                GameMepNetworkTraceResult trace = GameMepNetworkTracer.Build(
                    _scene.MepGraph,
                    startElementKey,
                    mode,
                    branchElementKey);
                if (trace.ElementKeys.Count == 0)
                {
                    ShowToast(trace.Summary);
                    return;
                }
                if (_mepRenderer == null)
                {
                    _mepRenderer = new GameMepFlowRenderer(
                        _scene.MepGraph,
                        GameViewport);
                }
                _mepNetworkTrace = trace;
                _mepRenderer.SetNetworkTrace(trace, _camera.Position);
                RefreshSelectionHistoryItems();
                ShowToast(trace.Summary);
            }
            catch (Exception exception)
            {
                GameRuntimeDiagnostics.Write(
                    "Suivi visuel du réseau MEP impossible",
                    exception);
                ShowToast("Suivi du réseau indisponible : " + exception.Message);
            }
        }

        private void ExitNetworkTraceButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            _mepNetworkTrace = null;
            try
            {
                _mepRenderer?.SetNetworkTrace(null, _camera.Position);
            }
            catch (Exception exception)
            {
                GameRuntimeDiagnostics.Write(
                    "Arrêt du suivi visuel MEP impossible",
                    exception);
            }
            RefreshSelectionHistoryItems();
            ShowToast("Suivi du réseau terminé");
        }

        private void RebuildActiveNetworkTrace()
        {
            if (_mepNetworkTrace == null)
                return;
            _mepNetworkTrace = GameMepNetworkTracer.Build(
                _scene.MepGraph,
                _mepNetworkTrace.StartElementKey,
                _mepNetworkTrace.Mode,
                _mepNetworkTrace.SelectedBranchElementKey);
        }

        private void SaveMepScenario()
        {
            GameMepScenarioStore.QueueSave(_scene.MepGraph);
        }

        private bool ExecuteMepScenarioMutation(
            string actionLabel,
            string completionMessage,
            Action mutation)
        {
            if (_mepRecalculationRunning)
            {
                ShowToast("Action refusée : calcul MEP en cours");
                return false;
            }
            bool changed = _mepScenarioHistory.Execute(
                _scene.MepGraph,
                actionLabel,
                mutation,
                calculationInProgress: false);
            if (!changed)
            {
                RebuildMepSourceItems();
                UpdateMepHistoryUi();
                ShowToast("Aucune modification à enregistrer");
                return false;
            }
            RebuildMepSourceItems();
            UpdateMepHistoryUi();
            RecalculateMepAsync(completionMessage, saveScenarioAfterCalculation: true);
            return true;
        }

        private void MepUndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mepRecalculationRunning)
            {
                ShowToast("Annulation refusée : calcul MEP en cours");
                return;
            }
            if (!_mepScenarioHistory.TryUndo(
                    _scene.MepGraph,
                    calculationInProgress: false,
                    out string label))
            {
                ShowToast("Aucune action MEP à annuler");
                return;
            }
            RebuildMepSourceItems();
            UpdateMepHistoryUi();
            RecalculateMepAsync(
                "Annulé : " + label,
                saveScenarioAfterCalculation: true);
        }

        private void MepRedoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mepRecalculationRunning)
            {
                ShowToast("Rétablissement refusé : calcul MEP en cours");
                return;
            }
            if (!_mepScenarioHistory.TryRedo(
                    _scene.MepGraph,
                    calculationInProgress: false,
                    out string label))
            {
                ShowToast("Aucune action MEP à rétablir");
                return;
            }
            RebuildMepSourceItems();
            UpdateMepHistoryUi();
            RecalculateMepAsync(
                "Rétabli : " + label,
                saveScenarioAfterCalculation: true);
        }

        private void UpdateMepHistoryUi()
        {
            if (MepUndoButton == null || MepRedoButton == null)
                return;
            MepUndoButton.IsEnabled =
                _mepScenarioHistory.CanUndo && !_mepRecalculationRunning;
            MepRedoButton.IsEnabled =
                _mepScenarioHistory.CanRedo && !_mepRecalculationRunning;
            MepUndoButton.ToolTip = _mepScenarioHistory.CanUndo
                ? "Annuler : " + _mepScenarioHistory.UndoLabel
                : "Aucune action à annuler";
            MepRedoButton.ToolTip = _mepScenarioHistory.CanRedo
                ? "Rétablir : " + _mepScenarioHistory.RedoLabel
                : "Aucune action à rétablir";
        }

        private async void RecalculateMepAsync(
            string completionMessage,
            bool saveScenarioAfterCalculation = false)
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
            UpdateMepHistoryUi();
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
                if (saveScenarioAfterCalculation)
                    SaveMepScenario();
                RefreshMepDiagnosticItems();
                RebuildActiveNetworkTrace();
                if (_mepRenderer != null)
                {
                    try
                    {
                        _mepRenderer.SetNetworkTrace(
                            _mepNetworkTrace,
                            _camera.Position);
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
                {
                    UpdateMepUi();
                    UpdateMepHistoryUi();
                }
            }
        }

        private void RefreshSelectionHistoryItems()
        {
            if (_currentSelectedElement.Count > 0)
            {
                GameElementData current = _currentSelectedElement[0].Element;
                _currentSelectedElement[0] = new GameSelectedElementItem(
                    current,
                    _scene.MepGraph,
                    _mepNetworkTrace);
            }
            for (int index = 0; index < _selectedElementHistory.Count; index++)
            {
                GameElementData element = _selectedElementHistory[index].Element;
                _selectedElementHistory[index] =
                    new GameSelectedElementItem(
                        element,
                        _scene.MepGraph,
                        _mepNetworkTrace);
            }
            UpdateSelectionHistoryUi();
        }

        private void DisableMepRenderingAfterError(Exception exception)
        {
            _mepRuntimeError = exception?.Message ?? "Erreur graphique inconnue";
            _mepFlowEnabled = false;
            _mepValveMarkersEnabled = false;
            try { _mepRenderer?.SetEnabled(false, _camera.Position); } catch { }
            try { _mepRenderer?.SetValveMarkersEnabled(false, _camera.Position); } catch { }
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
            CancelDirectionPicker(false);
            ObjectInfoPanel.Visibility = Visibility.Collapsed;
            try
            {
                _mepRenderer?.SetHighlightedElement(string.Empty, _camera.Position);
            }
            catch { }
            HideSelectedBounds();
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
            ShowToast("Historique des éléments précédents effacé");
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
                // Un gros équipement ou une enveloppe peut contenir la caméra
                // dans sa boîte englobante. Il faut malgré tout tester ses
                // triangles pour sélectionner la première surface devant nous.
                return true;
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
            PositionHudOverlay();
        }

        private void PositionHudOverlay()
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
            Canvas2D.SetLeft(Crosshair2D, (viewportWidth - 28.0) * 0.5);
            Canvas2D.SetTop(Crosshair2D, (viewportHeight - 28.0) * 0.5);
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
                GameMepGraphData mepGraph,
                GameMepNetworkTraceResult? networkTrace)
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
                string flowStateText = mepElement.FlowState == GameMepFlowState.Supplied &&
                    representativePath != null && !representativePath.HasCirculation
                        ? "sous pression, fluide stagnant"
                        : ToFrenchFlowState(mepElement.FlowState);
                FlowText = "État : " + flowStateText +
                    (representativePath == null
                        ? string.Empty
                        : "\nSens : " + ToFrenchDirection(representativePath));
                if (representativePath != null)
                {
                    DirectionExplanationVisibility = Visibility.Visible;
                    DirectionQualityText = ToFrenchReliability(
                        representativePath.DirectionExplanation.Reliability);
                    DirectionQualityBrush = ReliabilityBrush(
                        representativePath.DirectionExplanation.Reliability);
                    DirectionExplanationText = BuildDirectionExplanationText(
                        representativePath.DirectionExplanation);
                }

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
                        candidate.Scope ==
                            GameMepDirectionConstraintScope.EquipmentPressureRise &&
                        string.Equals(
                            candidate.ElementKey,
                            UniqueKey,
                            StringComparison.Ordinal));
                GameMepPathData? directionalPath = mepElement.Paths.FirstOrDefault(path =>
                    path.StartConnector >= 0 &&
                    path.EndConnector >= 0 &&
                    path.StartConnector != path.EndConnector);
                NetworkTraceVisibility = mepElement.ConnectorIndices.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                NetworkTraceExitVisibility = networkTrace != null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (networkTrace != null && string.Equals(
                        networkTrace.StartElementKey,
                        UniqueKey,
                        StringComparison.Ordinal))
                {
                    NetworkTraceStatusText = networkTrace.Summary;
                    NetworkTraceStatusVisibility = Visibility.Visible;
                    foreach (GameMepTraceBranchData branch in networkTrace.Branches)
                    {
                        NetworkTraceBranches.Add(new GameMepTraceBranchItem(
                            networkTrace.StartElementKey,
                            branch.ElementKey,
                            branch.Name,
                            networkTrace.Mode,
                            string.Equals(
                                networkTrace.SelectedBranchElementKey,
                                branch.ElementKey,
                                StringComparison.Ordinal)));
                    }
                    NetworkTraceBranchesVisibility =
                        NetworkTraceBranches.Count > 1
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                }
                bool hasLocalDirectionOverride =
                    mepGraph.DirectionConstraints.Any(candidate =>
                        candidate.Scope ==
                            GameMepDirectionConstraintScope.LocalOverride &&
                        candidate.IsActive &&
                        string.Equals(
                            candidate.ElementKey,
                            UniqueKey,
                            StringComparison.Ordinal));
                ReverseDirectionVisibility = directionalPath != null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ReverseDirectionActionText = directionalPath != null
                    ? (hasLocalDirectionOverride
                        ? "Retirer la correction locale"
                        : "Inverser uniquement ce tronçon")
                    : string.Empty;
                GameMepSystemData? selectedSystem =
                    mepGraph.FindSystem(mepElement.SystemKey);
                SystemIsolationVisibility = selectedSystem != null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                bool systemAlreadyIsolated = selectedSystem != null &&
                    selectedSystem.IsVisible &&
                    mepGraph.Systems.All(system =>
                        ReferenceEquals(system, selectedSystem) || !system.IsVisible);
                SystemIsolationActionText = systemAlreadyIsolated
                    ? "Afficher tous les systèmes"
                    : "Isoler ce système";
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
                SourceRemoveVisibility = source != null && source.IsUserCreated
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ReturnRemoveVisibility = outlet != null && outlet.IsUserCreated
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                SourcePickerActionText = source == null
                    ? "Définir comme arrivée"
                    : "Modifier le sens de l’arrivée";
                ReturnPickerActionText = outlet == null
                    ? "Définir comme retour"
                    : "Modifier le sens du retour";
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
                bool isCheckValve =
                    valve.Kind == GameMepFlowControlKind.CheckValve;
                if (isCheckValve)
                    ReverseDirectionVisibility = Visibility.Collapsed;
                ValveActionVisibility = valve.IsEnabledAsValve && !isCheckValve
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                CheckValveDirectionVisibility = valve.IsEnabledAsValve &&
                    isCheckValve && directionalPath != null
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                ValveText = isCheckValve
                    ? (valve.IsEnabledAsValve
                        ? "Clapet anti-retour  •  confiance " +
                            ToFrenchConfidence(valve.Confidence) +
                            "\nPassage autorisé : " +
                            (valve.HasExplicitDirection
                                ? "sens indiqué par la flèche violette"
                                : "sens à confirmer") +
                            "\nDétection : " + valve.DetectionReason
                        : "Clapet anti-retour ignoré")
                    : valve.IsEnabledAsValve
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
                CheckValveDirectionActionText = isCheckValve
                    ? (valve.HasExplicitDirection
                        ? "Inverser le sens du clapet"
                        : "Valider le sens affiché")
                    : string.Empty;
                ValveOverrideActionText = isCheckValve
                    ? (valve.IsEnabledAsValve
                        ? "Ignorer ce clapet"
                        : "Activer ce clapet")
                    : (valve.IsEnabledAsValve
                        ? "Ne plus traiter comme vanne"
                        : "Marquer comme vanne");
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
            public string DirectionQualityText { get; } = string.Empty;
            public string DirectionExplanationText { get; } = string.Empty;
            public Brush DirectionQualityBrush { get; } =
                new SolidColorBrush(Color.FromRgb(245, 170, 55));
            public Visibility DirectionExplanationVisibility { get; } =
                Visibility.Collapsed;
            public Visibility NetworkTraceVisibility { get; } = Visibility.Collapsed;
            public string NetworkTraceStatusText { get; } = string.Empty;
            public Visibility NetworkTraceStatusVisibility { get; } =
                Visibility.Collapsed;
            public Visibility NetworkTraceExitVisibility { get; } =
                Visibility.Collapsed;
            public Visibility NetworkTraceBranchesVisibility { get; } =
                Visibility.Collapsed;
            public IList<GameMepTraceBranchItem> NetworkTraceBranches { get; } =
                new List<GameMepTraceBranchItem>();
            public string ReverseDirectionActionText { get; } = string.Empty;
            public Visibility ReverseDirectionVisibility { get; } = Visibility.Collapsed;
            public string SystemIsolationActionText { get; } = string.Empty;
            public Visibility SystemIsolationVisibility { get; } = Visibility.Collapsed;
            public string ValveText { get; } = string.Empty;
            public string ValveActionText { get; } = string.Empty;
            public string ValveOverrideActionText { get; } = string.Empty;
            public string CheckValveDirectionActionText { get; } = string.Empty;
            public Visibility ValveActionVisibility { get; } = Visibility.Collapsed;
            public Visibility ValveOverrideVisibility { get; } = Visibility.Collapsed;
            public Visibility CheckValveDirectionVisibility { get; } = Visibility.Collapsed;
            public string SourceActionText { get; } = string.Empty;
            public Visibility SourceActionVisibility { get; } = Visibility.Collapsed;
            public Visibility SourceRemoveVisibility { get; } = Visibility.Collapsed;
            public string SourcePickerActionText { get; } = string.Empty;
            public string SourceForwardActionText { get; } = string.Empty;
            public string SourceReverseActionText { get; } = string.Empty;
            public Visibility SourceDirectionVisibility { get; } = Visibility.Collapsed;
            public string ReturnForwardActionText { get; } = string.Empty;
            public string ReturnReverseActionText { get; } = string.Empty;
            public string ReturnPickerActionText { get; } = string.Empty;
            public Visibility ReturnDirectionVisibility { get; } = Visibility.Collapsed;
            public Visibility ReturnRemoveVisibility { get; } = Visibility.Collapsed;
            public string ConstraintForwardActionText { get; } = string.Empty;
            public string ConstraintReverseActionText { get; } = string.Empty;
            public Visibility ConstraintDirectionVisibility { get; } = Visibility.Collapsed;

            private static string ToFrenchDirection(GameMepPathData path)
            {
                if (path.FlowState == GameMepFlowState.Supplied &&
                    !path.HasCirculation)
                {
                    return ValueOrFallback(
                        path.DirectionReason,
                        "aucune circulation vers un retour") +
                        " — flèches arrêtées";
                }
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

            private static string BuildDirectionExplanationText(
                GameMepDirectionExplanationData explanation)
            {
                var lines = new List<string>();
                lines.Add("Règle : " + ValueOrFallback(
                    explanation.Rule, "aucune règle concluante"));
                lines.Add("Source principale : " + ValueOrFallback(
                    explanation.PrimarySourceName, "aucune source accessible"));
                if (explanation.AlternativeSourceNames.Count > 0)
                {
                    lines.Add("Sources alternatives : " + string.Join(
                        ", ", explanation.AlternativeSourceNames.Take(4)) +
                        (explanation.AlternativeSourceNames.Count > 4
                            ? "…"
                            : string.Empty));
                }
                if (!string.IsNullOrWhiteSpace(explanation.InfluencingReturnName))
                    lines.Add("Retour influençant : " +
                        explanation.InfluencingReturnName);
                if (explanation.UpstreamElementNames.Count > 0)
                {
                    lines.Add("Chemin amont : " + string.Join(
                        "  →  ", explanation.UpstreamElementNames.Take(7)) +
                        (explanation.UpstreamElementNames.Count > 7
                            ? "  →  …"
                            : string.Empty));
                }
                if (explanation.LimitingControls.Count > 0)
                    lines.Add("Limites : " + string.Join(
                        " ; ", explanation.LimitingControls.Take(4)));
                if (explanation.HasAlternativeRoute)
                    lines.Add("Une route alternative ou une autre source peut aussi atteindre ce tronçon.");
                lines.Add(explanation.IsManual
                    ? "Résultat issu d'une correction manuelle enregistrée."
                    : "Résultat calculé automatiquement sur le graphe MEP.");
                return string.Join("\n", lines);
            }

            private static string ToFrenchReliability(
                GameMepDirectionReliability reliability)
            {
                switch (reliability)
                {
                    case GameMepDirectionReliability.Reliable:
                        return "FIABLE";
                    case GameMepDirectionReliability.Inferred:
                        return "DÉDUIT";
                    case GameMepDirectionReliability.Manual:
                        return "CORRIGÉ MANUELLEMENT";
                    default:
                        return "AMBIGU";
                }
            }

            private static Brush ReliabilityBrush(
                GameMepDirectionReliability reliability)
            {
                switch (reliability)
                {
                    case GameMepDirectionReliability.Reliable:
                        return new SolidColorBrush(Color.FromRgb(75, 225, 135));
                    case GameMepDirectionReliability.Inferred:
                        return new SolidColorBrush(Color.FromRgb(90, 215, 245));
                    case GameMepDirectionReliability.Manual:
                        return new SolidColorBrush(Color.FromRgb(225, 105, 255));
                    default:
                        return new SolidColorBrush(Color.FromRgb(255, 178, 60));
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

        private sealed class GameMepTraceBranchItem
        {
            public GameMepTraceBranchItem(
                string startElementKey,
                string branchElementKey,
                string name,
                GameMepTraceMode mode,
                bool selected)
            {
                StartElementKey = startElementKey;
                BranchElementKey = branchElementKey;
                Mode = mode;
                ActionText = selected
                    ? "✓ " + name
                    : "Afficher : " + name;
            }

            public string StartElementKey { get; }
            public string BranchElementKey { get; }
            public GameMepTraceMode Mode { get; }
            public string ActionText { get; }
        }

        private sealed class GameMepDiagnosticItem
        {
            public GameMepDiagnosticItem(
                GameMepDiagnosticData data,
                GameMepGraphData graph)
            {
                Data = data ?? throw new ArgumentNullException(nameof(data));
                Key = data.Key;
                Title = data.Title;
                Detail = data.Explanation +
                    (data.OccurrenceCount > 1
                        ? "  (" + data.OccurrenceCount + " occurrences)"
                        : string.Empty);
                GameMepSystemData? system = graph.FindSystem(data.SystemKey);
                SystemText = system == null
                    ? (string.IsNullOrWhiteSpace(data.SystemKey)
                        ? "Tous systèmes / diagnostic global"
                        : data.SystemKey)
                    : system.Name;
                GameMepElementData? element = graph.FindElement(data.ElementKey);
                CanNavigate = data.HasPosition && element != null && element.IsVisible;
                if (element != null)
                {
                    ReferenceShortText = string.IsNullOrWhiteSpace(element.Name)
                        ? "élément #" + element.ElementId
                        : element.Name;
                    ReferenceText = "Objet : " + ReferenceShortText +
                        (string.IsNullOrWhiteSpace(element.Category)
                            ? string.Empty
                            : "  •  " + element.Category) +
                        (element.ElementId == 0
                            ? string.Empty
                            : "  •  #" + element.ElementId) +
                        (data.ConnectorIndex < 0
                            ? string.Empty
                            : "  •  connecteur " + data.ConnectorIndex);
                    ReferenceVisibility = Visibility.Visible;
                }
                else
                {
                    ReferenceShortText = "diagnostic global";
                }
                switch (data.Severity)
                {
                    case GameMepDiagnosticSeverity.Critical:
                        SeverityBrush = new SolidColorBrush(Color.FromRgb(255, 72, 72));
                        break;
                    case GameMepDiagnosticSeverity.Warning:
                        SeverityBrush = new SolidColorBrush(Color.FromRgb(255, 184, 72));
                        break;
                    default:
                        SeverityBrush = new SolidColorBrush(Color.FromRgb(80, 190, 230));
                        break;
                }
                if (SeverityBrush.CanFreeze)
                    SeverityBrush.Freeze();
            }

            public GameMepDiagnosticData Data { get; }
            public string Key { get; }
            public string Title { get; }
            public string Detail { get; }
            public string ReferenceText { get; } = string.Empty;
            public string ReferenceShortText { get; } = string.Empty;
            public Visibility ReferenceVisibility { get; } = Visibility.Collapsed;
            public string SystemText { get; }
            public Brush SeverityBrush { get; }
            public bool CanNavigate { get; }
        }

        private sealed class GameMepDiagnosticFilterOption
        {
            public GameMepDiagnosticFilterOption(string label)
            {
                Label = label;
            }

            public GameMepDiagnosticFilterOption(
                string label,
                GameMepDiagnosticSeverity severity)
                : this(label)
            {
                Severity = severity;
            }

            public GameMepDiagnosticFilterOption(
                string label,
                GameMepDiagnosticKind kind)
                : this(label)
            {
                Kind = kind;
            }

            public GameMepDiagnosticFilterOption(string label, string systemKey)
                : this(label)
            {
                SystemKey = systemKey ?? string.Empty;
            }

            public string Label { get; }
            public GameMepDiagnosticSeverity? Severity { get; }
            public GameMepDiagnosticKind? Kind { get; }
            public string SystemKey { get; } = string.Empty;

            public override string ToString()
            {
                return Label;
            }
        }

    }
}

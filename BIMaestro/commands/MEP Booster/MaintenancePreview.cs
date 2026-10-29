using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using DbTransform = Autodesk.Revit.DB.Transform;
using MediaColor = System.Windows.Media.Color;
using MediaMaterial = System.Windows.Media.Media3D.Material;
using WpfPoint = System.Windows.Point;
using Grid = System.Windows.Controls.Grid;
using BIMaestro.Localization;

namespace BIMaestro.MepBooster
{
    // Corners use the same bit convention as MotorEnvelope: X=1, Y=2, Z=4.
    // Coordinates and radii are Revit internal feet, in document coordinates.
    internal sealed class MaintenancePreviewBox
    {
        internal XYZ[] Corners;
        internal MediaColor Color;
        internal double Opacity;
        // Optional visual-only shape; the checked collision envelope stays Corners.
        internal string Shape;
    }

    internal sealed class MaintenancePreviewLine
    {
        internal XYZ A, B;
        internal double Radius;
        internal MediaColor Color;
        internal bool Static;
    }

    /// <summary>
    /// Read-only WPF view of a bounded geometry snapshot. All Revit geometry is
    /// read synchronously in the constructor, while the caller is in API context.
    /// SetScene, orbit, zoom and Fit subsequently use only detached WPF geometry.
    /// </summary>
    internal sealed class MaintenancePreview : UserControl
    {
        private const int MaxSelectedTriangles = 45000;
        private const int MaxContextTriangles = 180000;
        private const int MaxContextElements = 360;
        private const int MaxElementTriangles = 16000;
        private const double ContextMargin = 500 / 304.8;
        private const int MaxOverlayBoxes = 128, MaxOverlayLines = 512;
        private readonly XYZ _origin;
        private readonly Viewport3D _viewport;
        private readonly Border _inputSurface;
        private readonly OrthographicCamera _camera;
        private readonly System.Windows.Threading.DispatcherTimer _wheelQualityTimer = new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(160)};
        private EdgeMode _idleEdgeMode;
        private bool _motionActive, _wheelActive;
        private readonly ModelVisual3D _overlayOpaqueVisual = new ModelVisual3D();
        private readonly ModelVisual3D _overlayTransparentVisual = new ModelVisual3D();
        private readonly ModelVisual3D _selectedVisual = new ModelVisual3D();
        private readonly ModelVisual3D _contextVisual = new ModelVisual3D();
        private readonly ModelVisual3D _blockedVisual = new ModelVisual3D();
        private readonly List<ContextEntry> _contextEntries = new List<ContextEntry>();
        private List<MaintenanceAccessObstacle> _blockedElements = new List<MaintenanceAccessObstacle>();
        private Model3D _selectedBlockedSnapshot, _blockedSnapshot;
        private long _sourceId;
        private readonly HashSet<long> _sourceComponentIds = new HashSet<long>();
        private string _blockedSignature;
        private int _missingBlocked;
        private bool _outsideCapture;
        private Model3D _selectedSnapshot, _contextSnapshot;
        private TextBlock _legendLabel;
        private bool _demonstration;
        private string _modelCaptureNote;
        private int _requestedBoxes, _displayedBoxes, _requestedLines, _displayedLines, _hiddenConflicts;
        private int _noteRequestedBoxes=-1, _noteDisplayedBoxes=-1, _noteRequestedLines=-1, _noteDisplayedLines=-1, _noteHiddenConflicts=-1;
        private bool _noteDemonstration;
        private readonly List<GeometryModel3D> _transparentFaces = new List<GeometryModel3D>();
        private Model3DGroup _transparentGroup;
        private List<TransparentFace> _transparentOrder;
        private bool _transparentStructureDirty;
        private long _transparencySorts, _transparencyOrderChanges;
        private TimingWindow _sceneIntervals, _renderIntervals, _inputLatencies;
        private long _lastSceneCommit, _lastRenderingCallback, _pendingInput;
        private List<BoxSlot> _boxSlots;
        private List<LineSlot> _lineSlots;
        private Model3DGroup _dynamicOpaque;
        private Model3DGroup _staticLineModels;
        private long _staticBatchBuilds;
        private int _staticLineCount;
        private Dictionary<long, DiffuseMaterial> _materials;
        private long _lastTransparencySort, _sceneUpdates, _geometryBuilds;
        private double _meanUpdateMilliseconds;
        private bool _transparencyDirty, _renderingAttached;
        private long _renderWindowStart;
        private int _renderWindowFrames;
        private TimeSpan _lastRenderTime;
        private long _lastVisualActivity;
        private long _poseRateStart;
        private int _poseRateFrames;
        private static readonly MeshGeometry3D TubePrimitive = CreateTubePrimitive();
        private static readonly MeshGeometry3D JointPrimitive = CreateJointPrimitive();
        private static readonly ConditionalWeakTable<MeshGeometry3D,MeshGeometry3D[]> SnapshotChunks = new ConditionalWeakTable<MeshGeometry3D,MeshGeometry3D[]>();
        private readonly TextBlock _captureLabel;
        private Rect3D _sourceBounds = Rect3D.Empty, _overlayBounds = Rect3D.Empty;
        private Point3D _target;
        private double _yaw = -1.05, _pitch = .5, _width = 10, _distance = 30;
        private bool _dragging, _firstScene = true;
        private WpfPoint _previous;
        private int _contextElements, _contextTriangles, _failedElements, _unloadedLinks, _unknownGeometry;
        private bool _truncated, _sourceMissing;
        private Point3D _clipMin, _clipMax;

        internal string CaptureNote { get; private set; }
        internal double LastUpdateMilliseconds { get; private set; }
        internal double AverageUpdateMilliseconds => _meanUpdateMilliseconds;
        internal long SceneUpdateCount => _sceneUpdates;
        internal long CachedModelBuildCount => _geometryBuilds;
        internal int StaticLineCount => _staticLineCount;
        internal int StaticBatchModelCount => _staticLineModels?.Children.Count ?? 0;
        internal long StaticBatchBuildCount => _staticBatchBuilds;
        internal int ContextTriangleCount => _contextTriangles;
        internal int MaxVisualMeshPositions => VisualMeshes().Select(m=>m.Positions.Count).DefaultIfEmpty().Max();
        internal int MaxVisualMeshIndices => VisualMeshes().Select(m=>m.TriangleIndices.Count).DefaultIfEmpty().Max();
        internal double SceneIntervalP95Milliseconds => _sceneIntervals?.Percentile(.95) ?? 0;
        internal double SceneIntervalP99Milliseconds => _sceneIntervals?.Percentile(.99) ?? 0;
        internal double RenderIntervalP95Milliseconds => _renderIntervals?.Percentile(.95) ?? 0;
        internal double RenderIntervalP99Milliseconds => _renderIntervals?.Percentile(.99) ?? 0;
        internal double InputToRenderP95Milliseconds => _inputLatencies?.Percentile(.95) ?? 0;
        internal double InputToRenderP99Milliseconds => _inputLatencies?.Percentile(.99) ?? 0;
        internal int InputLatencySampleCount => _inputLatencies?.Count ?? 0;
        internal long TransparencySortCount => _transparencySorts;
        internal long TransparencyOrderChangeCount => _transparencyOrderChanges;
        internal double LastTransparencySortMilliseconds { get; private set; }
        internal string TimingScope => UiLanguage.T("Derniers 240 échantillons ; pauses > 250 ms exclues des intervalles. Entrée → prochain callback de rendu WPF : estimation, pas une mesure de présentation à l’écran. Aucun échantillon ne signifie pas une latence nulle.", "Last 240 samples; pauses > 250 ms excluded from intervals. Input → next WPF render callback: an estimate, not a measurement of on-screen presentation. No samples does not mean zero latency.");
        internal double CaptureMilliseconds { get; private set; }
        internal double RenderFramesPerSecond { get; private set; }
        internal double SceneUpdatesPerSecond { get; private set; }
        internal int RenderingTier => RenderCapability.Tier >> 16;
        internal bool IsFastRenderingActive => RenderOptions.GetEdgeMode(_viewport)==EdgeMode.Aliased;
        internal bool UsesGeometryHitTesting => _viewport.IsHitTestVisible;
        internal string PreviewTiming => UiLanguage.T("Pose ", "Pose ") + LastUpdateMilliseconds.ToString("0.00") + UiLanguage.T(" ms (moy. ", " ms (avg. ")
            + _meanUpdateMilliseconds.ToString("0.00") + UiLanguage.T(") ; animation ", "); animation ") + SceneUpdatesPerSecond.ToString("0.0")
            + UiLanguage.T(" poses/s ; cadence WPF ", " poses/s; WPF frame rate ") + RenderFramesPerSecond.ToString("0.0")
            + UiLanguage.T(" i/s (niveau ", " fps (tier ") + RenderingTier + UiLanguage.T(") ; intervalles pose P95/P99 ", "); pose intervals P95/P99 ") + SceneIntervalP95Milliseconds.ToString("0.0") + "/" + SceneIntervalP99Milliseconds.ToString("0.0")
            + UiLanguage.T(" ms ; entrée→rendu ", " ms; input→render ") + (InputLatencySampleCount==0?UiLanguage.T("non mesurée", "not measured"):InputToRenderP95Milliseconds.ToString("0.0")+"/"+InputToRenderP99Milliseconds.ToString("0.0")+" ms")
            + " ; " + _sceneUpdates + UiLanguage.T(" poses, ", " poses, ") + _geometryBuilds + UiLanguage.T(" constructions de modèles. ", " model builds. ") + TimingScope;

        internal MaintenancePreview(Document doc, Element source) : this(doc, source, null, null) { }

        internal MaintenancePreview(Document doc, Element source, XYZ[] interventionCorners,
            IEnumerable<MaintenanceAccessObstacle> blocked = null)
        {
            long captureStarted = Stopwatch.GetTimestamp();
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (source == null) throw new ArgumentNullException(nameof(source));
            var sourceBox = source.get_BoundingBox(null);
            var sourceMin = new XYZ(0, 0, 0);
            var sourceMax = new XYZ(1, 1, 1);
            if (sourceBox != null)
            {
                var corners = BoundingCorners(sourceBox, DbTransform.Identity);
                sourceMin = Minimum(corners); sourceMax = Maximum(corners);
            }
            else if (source.Location is LocationPoint location)
            { sourceMin = location.Point - new XYZ(.5, .5, .5); sourceMax = location.Point + new XYZ(.5, .5, .5); }
            _origin = (sourceMin + sourceMax) / 2;
            _sourceId = BoosterIds.Value(source.Id);
            _sourceComponentIds.Add(_sourceId);
            _blockedElements = (blocked ?? Enumerable.Empty<MaintenanceAccessObstacle>()).Where(o => o != null).ToList();
            var region = new List<XYZ> { sourceMin, sourceMax };
            if (interventionCorners != null) region.AddRange(interventionCorners.Where(p => p != null && Finite(new Point3D(p.X, p.Y, p.Z))));
            _clipMin = Local(Minimum(region) - new XYZ(ContextMargin, ContextMargin, ContextMargin));
            _clipMax = Local(Maximum(region) + new XYZ(ContextMargin, ContextMargin, ContextMargin));

            MinHeight = 310;
            ClipToBounds = true;
            var layout = new Grid
            {
                Background = new LinearGradientBrush(MediaColor.FromRgb(246, 249, 251), MediaColor.FromRgb(221, 231, 236), 90)
            };
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _viewport = new Viewport3D { ClipToBounds = true, IsHitTestVisible = false };
            _idleEdgeMode = RenderOptions.GetEdgeMode(_viewport);
            _camera = new OrthographicCamera { NearPlaneDistance = .001, FarPlaneDistance = 100000 };
            _viewport.Camera = _camera;
            layout.Children.Add(_viewport);
            // Orbit/zoom only need a 2D pointer position. A transparent input
            // plane avoids WPF's software ray hit-test against every 3D mesh.
            _inputSurface = new Border {Background=Brushes.Transparent,Focusable=true};
            layout.Children.Add(_inputSurface);
            var lighting = new Model3DGroup();
            lighting.Children.Add(new AmbientLight(MediaColor.FromRgb(130, 130, 130)));
            lighting.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-.4, .5, -1)));
            lighting.Children.Add(new DirectionalLight(MediaColor.FromRgb(170, 184, 200), new Vector3D(.7, -.3, -.5)));
            lighting.Freeze();
            _viewport.Children.Add(new ModelVisual3D { Content = lighting });

            var selected = new Model3DGroup();
            var context = new Model3DGroup();
            var selectedMesh = new MeshGeometry3D();
            try
            {
                CaptureElement(source, DbTransform.Identity, selectedMesh, MaxSelectedTriangles, false);
                _sourceBounds = selectedMesh.Bounds;
            }
            catch (Exception ex) when (Recoverable(ex)) { _failedElements++; }
            if (selectedMesh.Positions.Count > 0)
            {
                selected.Children.Add(Model(selectedMesh, MediaColor.FromRgb(30, 121, 203), 1));
                _selectedBlockedSnapshot = Model(selectedMesh, Colors.OrangeRed, 1);
            }
            else _sourceMissing = true;
            if (_sourceBounds.IsEmpty)
                _sourceBounds = new Rect3D(Local(sourceMin), new Size3D(sourceMax.X-sourceMin.X, sourceMax.Y-sourceMin.Y, sourceMax.Z-sourceMin.Z));
            try { CaptureContext(doc, source); }
            catch (Exception ex) when (Recoverable(ex)) { _failedElements++; }
            if (selected.CanFreeze) selected.Freeze();
            if (context.CanFreeze) context.Freeze();
            // WPF transparent surfaces write depth. Opaque geometry must be
            // submitted first, otherwise an access envelope hides its own person.
            _selectedSnapshot=selected;_contextSnapshot=context;
            _selectedVisual.Content=selected;_contextVisual.Content=context;
            AddSnapshotVisuals();

            var legend = new TextBlock
            {
                Text = UiLanguage.T("Équipement bleu · Environnement gris · Obstacle rouge", "Equipment blue · Surroundings grey · Obstacle red"),
                Foreground = new SolidColorBrush(MediaColor.FromRgb(44, 65, 80)), FontSize = 11,
                Margin = new Thickness(10, 8, 10, 0), IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left
            };
            _legendLabel=legend;
            layout.Children.Add(legend);
            var controls = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(225, 255, 255, 255)),
                Padding = new Thickness(9, 6, 9, 6), VerticalAlignment = VerticalAlignment.Bottom
            };
            var controlGrid = new Grid();
            controlGrid.ColumnDefinitions.Add(new ColumnDefinition());
            controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var captions = new StackPanel();
            captions.Children.Add(new TextBlock
            {
                Text = UiLanguage.T("Glisser : tourner · Molette : zoomer", "Drag: rotate · Wheel: zoom"), FontSize = 11,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(39, 64, 80))
            });
            CaptureNote = UiLanguage.T("Coupe locale : ", "Local section box: ") + _contextElements + UiLanguage.T(" éléments, ", " elements, ") + _contextTriangles.ToString("N0")
                + UiLanguage.T(" triangles ; bloc équipement, intervention et poste + 50 cm de chaque côté.", " triangles; equipment, intervention and position block + 50 cm on each side.")
                + (_truncated ? UiLanguage.T(" Capture limitée : certains détails ou éléments sont omis.", " Limited capture: some details or elements are omitted.") : "")
                + (_sourceMissing ? UiLanguage.T(" Équipement sans surface lisible dans cet aperçu.", " Equipment has no readable surface in this preview.") : "")
                + (_failedElements > 0 ? " " + _failedElements + UiLanguage.T(" géométrie(s) illisible(s).", " unreadable geometry(ies).") : "")
                + (_unknownGeometry > 0 ? " " + _unknownGeometry + UiLanguage.T(" fragment(s) sans géométrie exploitable ; aperçu incomplet.", " fragment(s) without usable geometry; incomplete preview.") : "")
                + (_unloadedLinks > 0 ? " " + _unloadedLinks + UiLanguage.T(" lien(s) non chargé(s).", " link(s) not loaded.") : "")
                + UiLanguage.T(" Instantané visuel, distinct du calcul de collision.", " Visual snapshot, separate from the collision calculation.");
            _modelCaptureNote=CaptureNote;
            _captureLabel = new TextBlock
            {
                Text = CaptureNote, TextWrapping = TextWrapping.Wrap, FontSize = 10,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(78, 92, 101)), Margin = new Thickness(0, 3, 8, 0)
            };
            captions.Children.Add(_captureLabel);
            controlGrid.Children.Add(captions);
            var fit = new Button { Content = "Recentrer", Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
            fit.Click += (s, e) => Fit();
            Grid.SetColumn(fit, 1); controlGrid.Children.Add(fit);
            controls.Child = controlGrid; Grid.SetRow(controls, 1); layout.Children.Add(controls);
            Content = layout;
            SetBlockedElements(_blockedElements);
            _inputSurface.MouseDown += OnMouseDown;
            _inputSurface.MouseMove += OnMouseMove;
            _inputSurface.MouseUp += OnMouseUp;
            _inputSurface.LostMouseCapture += (s, e) => { _dragging = false; _inputSurface.Cursor = Cursors.Arrow; UpdateRenderQuality(); };
            _inputSurface.MouseWheel += (s, e) =>
            {
                RecordInput(e.Timestamp);_wheelActive=true;UpdateRenderQuality();
                _wheelQualityTimer.Stop();_wheelQualityTimer.Start();
                _width = Clamp(_width * Math.Exp(-e.Delta / 900.0), .1, 10000); UpdateCamera(); e.Handled = true;
            };
            _wheelQualityTimer.Tick += (s,e) => { _wheelQualityTimer.Stop();_wheelActive=false;UpdateRenderQuality(); };
            SizeChanged += (s, e) => UpdateCamera();
            Loaded += (s, e) =>
            {
                AttachRendering();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => { if (IsLoaded) Fit(); }));
            };
            Unloaded += (s, e) => { _wheelQualityTimer.Stop();_wheelActive=false;_motionActive=false;_dragging=false;UpdateRenderQuality();DetachRendering(); };
            Fit();
            CaptureMilliseconds = (Stopwatch.GetTimestamp() - captureStarted) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>
        /// Demonstration inputs use synthetic local coordinates near XYZ.Zero.
        /// This only switches detached WPF visuals; it never reads Revit, places
        /// an operator in the document or validates a work position.
        /// </summary>
        internal void SetDemonstration(bool enabled)
        {
            if(_demonstration==enabled)return;
            _demonstration=enabled;
            _selectedVisual.Content=enabled?null:SelectedSnapshot();
            _contextVisual.Content=enabled?null:_contextSnapshot;
            _blockedVisual.Content=enabled?null:_blockedSnapshot;
            if(_legendLabel!=null)_legendLabel.Text=enabled?UiLanguage.T("Démonstration du geste · hors maquette", "Gesture demonstration · outside the model"):UiLanguage.T("Équipement bleu · Environnement gris · Obstacle rouge", "Equipment blue · Surroundings grey · Obstacle red");
            RefreshCaptureNote(true);
            _firstScene=true;
            Fit();
        }

        private void AddSnapshotVisuals()
        {
            _viewport.Children.Add(_selectedVisual);
            _viewport.Children.Add(_overlayOpaqueVisual);
            _viewport.Children.Add(_blockedVisual);
            _viewport.Children.Add(_contextVisual);
            _viewport.Children.Add(_overlayTransparentVisual);
        }

        // Visual quality only: collision geometry, captured context and motion
        // samples are identical in both modes. Restore antialiasing at rest.
        internal void SetMotionActive(bool active)
        {
            if(_motionActive==active)return;
            _motionActive=active;UpdateRenderQuality();
        }
        private void UpdateRenderQuality()
        {
            var edgeMode=_motionActive||_dragging||_wheelActive?EdgeMode.Aliased:_idleEdgeMode;
            if(RenderOptions.GetEdgeMode(_viewport)!=edgeMode)RenderOptions.SetEdgeMode(_viewport,edgeMode);
        }

        // Recolors detached meshes only. Call after a new analysis, not per pose;
        // no document, element or geometry API is used after the constructor.
        internal void SetBlockedElements(IEnumerable<MaintenanceAccessObstacle> blocked)
        {
            var incoming = (blocked ?? Enumerable.Empty<MaintenanceAccessObstacle>()).Where(o => o != null)
                .GroupBy(o => new { o.ElementId, o.LinkId, o.LinkName }).Select(g => g.First()).ToList();
            string signature = string.Join(";", incoming.Select(o => o.LinkId + "|" + o.LinkName + "|" + o.ElementId).Distinct().OrderBy(s => s));
            if (_blockedSignature == signature) return;
            _blockedSignature = signature; _blockedElements = incoming;
            var gray = new Model3DGroup(); var red = new Model3DGroup();
            foreach (var entry in _contextEntries)
                if (IsBlocked(entry.ElementId, entry.LinkId, entry.LinkName)) red.Children.Add(entry.Blocked);
                else gray.Children.Add(entry.Normal);
            if (gray.CanFreeze) gray.Freeze(); if (red.CanFreeze) red.Freeze();
            _contextSnapshot = gray; _blockedSnapshot = red;
            _missingBlocked = _blockedElements.Count(o => !SourceContains(o)
                && !_contextEntries.Any(e => Matches(o, e.ElementId, e.LinkId, e.LinkName)));
            _selectedVisual.Content = _demonstration ? null : SelectedSnapshot();
            _contextVisual.Content = _demonstration ? null : _contextSnapshot;
            _blockedVisual.Content = _demonstration ? null : _blockedSnapshot;
            RefreshCaptureNote(true);
            _lastVisualActivity = Stopwatch.GetTimestamp();
        }

        private Model3D SelectedSnapshot()
            => _selectedBlockedSnapshot != null && _blockedElements.Any(SourceContains) ? _selectedBlockedSnapshot : _selectedSnapshot;
        private bool SourceContains(MaintenanceAccessObstacle obstacle)
            => IsHost(obstacle) && (obstacle.ElementId == _sourceId || _sourceComponentIds != null && _sourceComponentIds.Contains(obstacle.ElementId));
        private bool IsBlocked(long elementId, long linkId, string linkName)
            => _blockedElements.Any(o => Matches(o, elementId, linkId, linkName));
        private static bool IsHost(MaintenanceAccessObstacle obstacle)
            => obstacle.LinkId <= 0 && string.IsNullOrEmpty(obstacle.LinkName);
        private static bool Matches(MaintenanceAccessObstacle obstacle, long elementId, long linkId, string linkName)
        {
            if (obstacle.ElementId != elementId) return false;
            if (obstacle.LinkId > 0) return obstacle.LinkId == linkId;
            if (!string.IsNullOrEmpty(obstacle.LinkName)) return string.Equals(obstacle.LinkName, linkName, StringComparison.Ordinal);
            return linkId <= 0;
        }

        internal void SetScene(IEnumerable<MaintenancePreviewBox> boxes, IEnumerable<MaintenancePreviewLine> lines)
        {
            long started = Stopwatch.GetTimestamp();
            EnsureSceneCache();
            bool structureChanged = false;
            bool staticChanged = false;
            int boxCount = 0, lineCount = 0;
            var boxInput=boxes as IList<MaintenancePreviewBox>??(boxes??Enumerable.Empty<MaintenancePreviewBox>()).ToList();
            var lineInput=lines as IList<MaintenancePreviewLine>??(lines??Enumerable.Empty<MaintenancePreviewLine>()).ToList();
            var budgetedBoxes=PrioritizeBoxes(boxInput);
            var budgetedLines=PrioritizeLines(lineInput);
            _requestedBoxes=boxInput.Count;_requestedLines=lineInput.Count;
            _hiddenConflicts=boxInput.Count>MaxOverlayBoxes
                ?boxInput.Count(b=>BoxPriority(b)==2)-budgetedBoxes.Count(b=>BoxPriority(b)==2):0;
            _overlayBounds = Rect3D.Empty;
            foreach (var box in budgetedBoxes)
            {
                if (box?.Corners == null || box.Corners.Length != 8 || box.Corners.Any(p => p == null)) continue;
                var a = Local(box.Corners[0]);
                var x = Local(box.Corners[1])-a; var y = Local(box.Corners[2])-a; var z = Local(box.Corners[4])-a;
                double sx = x.Length, sy = y.Length, sz = z.Length;
                if (!Finite(a) || sx < 1e-9 || sy < 1e-9 || sz < 1e-9
                    || double.IsNaN(sx+sy+sz) || double.IsInfinity(sx+sy+sz)) continue;
                double opacity = Clamp(box.Opacity, 0, 1);
                BoxSlot slot = boxCount < _boxSlots.Count ? _boxSlots[boxCount] : null;
                if (slot == null || Math.Abs(slot.SX-sx)>1e-5 || Math.Abs(slot.SY-sy)>1e-5 || Math.Abs(slot.SZ-sz)>1e-5
                    || slot.Color!=box.Color || slot.Opacity!=opacity || !string.Equals(slot.Shape,box.Shape,StringComparison.Ordinal))
                {
                    slot = CreateBoxSlot(sx,sy,sz,box.Color,opacity,box.Shape);
                    if (boxCount < _boxSlots.Count) _boxSlots[boxCount]=slot; else _boxSlots.Add(slot);
                    structureChanged=true;
                }
                var placement = new Matrix3D(x.X/sx,x.Y/sx,x.Z/sx,0,y.X/sy,y.Y/sy,y.Z/sy,0,z.X/sz,z.Y/sz,z.Z/sz,0,a.X,a.Y,a.Z,1);
                if (!slot.Placement.Matrix.Equals(placement) || slot.Bounds.IsEmpty)
                {
                    slot.Placement.Matrix=placement;
                    slot.Bounds=Rect3D.Empty;
                    for(int i=0;i<8;i++) slot.Bounds.Union(Local(box.Corners[i]));
                    if(slot.Transparent.Count>0) _transparencyDirty=true;
                }
                _overlayBounds.Union(slot.Bounds);
                boxCount++;
            }
            foreach (var line in budgetedLines)
            {
                if (line?.A == null || line.B == null) continue;
                var a = Local(line.A); var b = Local(line.B);
                if(!Finite(a)||!Finite(b)||double.IsNaN(line.Radius)||double.IsInfinity(line.Radius))continue;
                double radius = Math.Max(.003, line.Radius);
                LineSlot slot = lineCount<_lineSlots.Count ? _lineSlots[lineCount] : null;
                if(slot==null||slot.Color!=line.Color||Math.Abs(slot.Radius-radius)>1e-9||slot.Static!=line.Static)
                {
                    if(line.Static||slot!=null&&slot.Static)staticChanged=true;
                    slot=CreateLineSlot(line.Color,radius);slot.Static=line.Static;
                    if(lineCount<_lineSlots.Count)_lineSlots[lineCount]=slot;else _lineSlots.Add(slot);
                    structureChanged=true;
                }
                if(slot.Bounds.IsEmpty||slot.A!=a||slot.B!=b)
                {
                    if(slot.Static)staticChanged=true;
                    UpdateLine(slot,a,b);
                    slot.A=a;slot.B=b;
                }
                _overlayBounds.Union(slot.Bounds);
                lineCount++;
            }
            if(_boxSlots.Count>boxCount){_boxSlots.RemoveRange(boxCount,_boxSlots.Count-boxCount);structureChanged=true;}
            if(_lineSlots.Count>lineCount)
            {
                if(_lineSlots.Skip(lineCount).Any(s=>s.Static))staticChanged=true;
                _lineSlots.RemoveRange(lineCount,_lineSlots.Count-lineCount);structureChanged=true;
            }
            if(staticChanged)RebuildStaticLineBatch();
            _displayedBoxes=boxCount;_displayedLines=lineCount;
            RefreshCaptureNote();
            if(structureChanged)
            {
                _dynamicOpaque.Children.Clear();_transparentFaces.Clear();
                foreach(var slot in _boxSlots){_dynamicOpaque.Children.Add(slot.Opaque);_transparentFaces.AddRange(slot.Transparent);}
                _dynamicOpaque.Children.Add(_staticLineModels);
                foreach(var slot in _lineSlots)if(!slot.Static)_dynamicOpaque.Children.Add(slot.Opaque);
                _transparencyDirty=true;
                _transparentStructureDirty=true;
            }
            SortTransparentFaces(structureChanged);
            bool outside = !_demonstration && !_overlayBounds.IsEmpty
                && (_overlayBounds.X < _clipMin.X - .01 || _overlayBounds.Y < _clipMin.Y - .01 || _overlayBounds.Z < _clipMin.Z - .01
                    || _overlayBounds.X + _overlayBounds.SizeX > _clipMax.X + .01
                    || _overlayBounds.Y + _overlayBounds.SizeY > _clipMax.Y + .01
                    || _overlayBounds.Z + _overlayBounds.SizeZ > _clipMax.Z + .01);
            if (outside != _outsideCapture) { _outsideCapture = outside; RefreshCaptureNote(true); }
            // Switching back to an unconfigured model view supplies an empty
            // scene: fit the selected equipment rather than the previous demo.
            if (_firstScene) { _firstScene = false; Fit(); }
            LastUpdateMilliseconds=(Stopwatch.GetTimestamp()-started)*1000.0/Stopwatch.Frequency;
            _meanUpdateMilliseconds=_sceneUpdates==0?LastUpdateMilliseconds:_meanUpdateMilliseconds*.9+LastUpdateMilliseconds*.1;
            _sceneUpdates++;
            _lastVisualActivity=Stopwatch.GetTimestamp();
            RecordInterval(ref _sceneIntervals, ref _lastSceneCommit, _lastVisualActivity);
            if(_poseRateStart==0)_poseRateStart=_lastVisualActivity;
            _poseRateFrames++;
            double poseSeconds=(_lastVisualActivity-_poseRateStart)/(double)Stopwatch.Frequency;
            if(poseSeconds>=.75){SceneUpdatesPerSecond=_poseRateFrames/poseSeconds;_poseRateFrames=0;_poseRateStart=_lastVisualActivity;}
            if(_captureLabel!=null&&IsLoaded&&!_renderingAttached)AttachRendering();
        }

        private static int BoxPriority(MaintenancePreviewBox box)
        {
            if(box==null)return -1;
            if(string.Equals(box.Shape,"head",StringComparison.OrdinalIgnoreCase)
                ||string.Equals(box.Shape,"torso",StringComparison.OrdinalIgnoreCase)
                ||string.Equals(box.Shape,"hand",StringComparison.OrdinalIgnoreCase))return 3;
            // Existing callers identify conflicts with orange/red, independent
            // of opacity (conflicts are usually wire-only).
            if(box.Color.R>=150&&box.Color.R>box.Color.G*1.2&&box.Color.R>box.Color.B*1.2)return 2;
            if(box.Color.B>box.Color.R*1.2&&box.Color.B>=box.Color.G*.9)return 1;
            return 0;
        }
        private static IList<MaintenancePreviewBox> PrioritizeBoxes(IList<MaintenancePreviewBox> input)
        {
            if(input.Count<=MaxOverlayBoxes)return input;
            var result=new List<MaintenancePreviewBox>(MaxOverlayBoxes);
            for(int priority=3;priority>=0;priority--)
                foreach(var box in input)
                {
                    if(BoxPriority(box)!=priority)continue;
                    result.Add(box);if(result.Count==MaxOverlayBoxes)return result;
                }
            return result;
        }
        private static int LinePriority(MaintenancePreviewLine line)
        {
            if(line==null)return -1;
            if(line.Color.G>line.Color.R*1.15&&line.Color.G>=line.Color.B*.9)return 2;
            if(line.Color.R>line.Color.G*1.2&&line.Color.B>line.Color.G*1.2)return 1;
            return 0;
        }
        private static IList<MaintenancePreviewLine> PrioritizeLines(IList<MaintenancePreviewLine> input)
        {
            if(input.Count<=MaxOverlayLines)return input;
            var result=new List<MaintenancePreviewLine>(MaxOverlayLines);
            for(int priority=2;priority>=0;priority--)
                foreach(var line in input)
                {
                    if(LinePriority(line)!=priority)continue;
                    result.Add(line);if(result.Count==MaxOverlayLines)return result;
                }
            return result;
        }
        private void RefreshCaptureNote(bool force=false)
        {
            if(!force&&CaptureNote!=null&&_noteRequestedBoxes==_requestedBoxes&&_noteDisplayedBoxes==_displayedBoxes
                &&_noteRequestedLines==_requestedLines&&_noteDisplayedLines==_displayedLines
                &&_noteHiddenConflicts==_hiddenConflicts&&_noteDemonstration==_demonstration)return;
            _noteRequestedBoxes=_requestedBoxes;_noteDisplayedBoxes=_displayedBoxes;
            _noteRequestedLines=_requestedLines;_noteDisplayedLines=_displayedLines;
            _noteHiddenConflicts=_hiddenConflicts;_noteDemonstration=_demonstration;
            string note=_demonstration
                ?UiLanguage.T("Démonstration du geste — sol et position illustratifs, aucun poste dans la maquette validé.", "Gesture demonstration — illustrative floor and position, no position in the model validated.")
                :_modelCaptureNote??"";
            if (!_demonstration && _missingBlocked > 0)
                note += UiLanguage.T(" Capture incomplète : ", " Incomplete capture: ") + _missingBlocked + UiLanguage.T(" obstacle(s) signalé(s) sans surface capturée ; consultez les conflits et la maquette.", " obstacle(s) reported without captured surface; review the conflicts and the model.");
            if (!_demonstration && _outsideCapture)
                note += UiLanguage.T(" Geste hors de la coupe initiale : contexte incomplet hors du bloc ; rouvrez la préparation avec les nouveaux réglages.", " Gesture outside the initial section box: incomplete context outside the block; reopen the preparation with the new settings.");
            if(_requestedBoxes>MaxOverlayBoxes||_requestedLines>MaxOverlayLines)
            {
                note+=UiLanguage.T(" Aperçu limité : ", " Limited preview: ")+_displayedBoxes+UiLanguage.T(" volumes sur ", " volumes out of ")+_requestedBoxes;
                if(_requestedLines>MaxOverlayLines)note+=", "+_displayedLines+UiLanguage.T(" segments sur ", " segments out of ")+_requestedLines;
                note+=UiLanguage.T(". Corps et conflits affichés en priorité.", ". Body and conflicts displayed first.");
                if(_hiddenConflicts>0)note+=" "+_hiddenConflicts+UiLanguage.T(" volume(s) de conflit non affiché(s).", " conflict volume(s) not displayed.");
            }
            CaptureNote=note;
            if(_captureLabel!=null)_captureLabel.Text=note;
        }

        private sealed class BoxSlot
        {
            internal double SX,SY,SZ,Opacity;
            internal MediaColor Color;
            internal string Shape;
            internal readonly MatrixTransform3D Placement=new MatrixTransform3D();
            internal readonly Model3DGroup Opaque=new Model3DGroup();
            internal readonly List<GeometryModel3D> Transparent=new List<GeometryModel3D>();
            internal Rect3D Bounds=Rect3D.Empty;
        }
        private sealed class LineSlot
        {
            internal bool Static;
            internal double Radius;
            internal MediaColor Color;
            internal Point3D A,B;
            internal Rect3D Bounds=Rect3D.Empty;
            internal readonly MatrixTransform3D Tube=new MatrixTransform3D(),JointA=new MatrixTransform3D(),JointB=new MatrixTransform3D();
            internal readonly Model3DGroup Opaque=new Model3DGroup();
        }
        private void EnsureSceneCache()
        {
            if(_boxSlots!=null)return;
            _boxSlots=new List<BoxSlot>();_lineSlots=new List<LineSlot>();
            _materials=new Dictionary<long,DiffuseMaterial>();_dynamicOpaque=new Model3DGroup();
            _staticLineModels=new Model3DGroup();
            _overlayOpaqueVisual.Content=_dynamicOpaque;
        }

        private void RebuildStaticLineBatch()
        {
            // Preserve the exact tube and joint primitives, including triangle
            // order and material. Only bake their rigid placements once. Split
            // chunks before 20k positions to keep WPF's mesh buffers modest.
            const int maxPositions=18000;
            var chunks=new List<Tuple<MediaMaterial,MeshGeometry3D>>();
            var current=new Dictionary<MediaMaterial,MeshGeometry3D>();
            _staticLineCount=0;
            foreach(var slot in _lineSlots)
            {
                if(!slot.Static)continue;
                _staticLineCount++;
                foreach(GeometryModel3D model in slot.Opaque.Children)
                {
                    var source=(MeshGeometry3D)model.Geometry;
                    if(!current.TryGetValue(model.Material,out var mesh)||mesh.Positions.Count+source.Positions.Count>maxPositions)
                    {
                        mesh=new MeshGeometry3D();current[model.Material]=mesh;
                        chunks.Add(Tuple.Create(model.Material,mesh));
                    }
                    int offset=mesh.Positions.Count;
                    foreach(var point in source.Positions)mesh.Positions.Add(model.Transform.Transform(point));
                    foreach(int index in source.TriangleIndices)mesh.TriangleIndices.Add(offset+index);
                }
            }
            _staticLineModels.Children.Clear();
            foreach(var chunk in chunks)
            {
                chunk.Item2.Freeze();
                var model=new GeometryModel3D(chunk.Item2,chunk.Item1){BackMaterial=chunk.Item1};
                model.Freeze();_staticLineModels.Children.Add(model);
            }
            _staticBatchBuilds++;
        }
        private BoxSlot CreateBoxSlot(double sx,double sy,double sz,MediaColor color,double opacity,string shape)
        {
            var slot=new BoxSlot{SX=sx,SY=sy,SZ=sz,Color=color,Opacity=opacity,Shape=shape};
            var points=new Point3D[8];
            for(int i=0;i<8;i++)points[i]=new Point3D((i&1)==0?0:sx,(i&2)==0?0:sy,(i&4)==0?0:sz);
            bool rounded=string.Equals(shape,"head",StringComparison.OrdinalIgnoreCase)||string.Equals(shape,"hand",StringComparison.OrdinalIgnoreCase);
            bool torso=string.Equals(shape,"torso",StringComparison.OrdinalIgnoreCase);
            if(torso)points=TorsoPoints(points);
            if(opacity>0&&color.A>0)
            {
                bool opaque=opacity*color.A/255.0>=.999;
                if(rounded||opaque)
                {
                    var mesh=new MeshGeometry3D();
                    if(rounded)AddEllipsoid(mesh,points);else foreach(var face in BoxFaces)AddQuad(mesh,points,face);
                    var model=DynamicModel(mesh,color,opacity,slot.Placement);
                    if(opaque)slot.Opaque.Children.Add(model);else slot.Transparent.Add(model);
                }
                else foreach(var face in BoxFaces)
                {var mesh=new MeshGeometry3D();AddQuad(mesh,points,face);slot.Transparent.Add(DynamicModel(mesh,color,opacity,slot.Placement));}
            }
            if(!rounded&&!torso)
            {
                var edges=new MeshGeometry3D();double radius=Math.Max(.004,Math.Min(.015,_sourceBounds.SizeX/240));
                for(int i=0;i<8;i++)for(int bit=1;bit<=4;bit<<=1)if((i&bit)==0)AddTube(edges,points[i],points[i|bit],radius,6);
                slot.Opaque.Children.Add(DynamicModel(edges,OpaqueColor(color),1,slot.Placement));
            }
            _geometryBuilds++;return slot;
        }
        private LineSlot CreateLineSlot(MediaColor color,double radius)
        {
            var slot=new LineSlot{Color=color,Radius=radius};color=OpaqueColor(color);
            slot.Opaque.Children.Add(DynamicModel(TubePrimitive,color,1,slot.Tube));
            slot.Opaque.Children.Add(DynamicModel(JointPrimitive,color,1,slot.JointA));
            slot.Opaque.Children.Add(DynamicModel(JointPrimitive,color,1,slot.JointB));
            _geometryBuilds++;return slot;
        }
        private static void UpdateLine(LineSlot slot,Point3D a,Point3D b)
        {
            var axis=b-a;double radius=slot.Radius;
            if(axis.LengthSquared<1e-16)slot.Tube.Matrix=new Matrix3D(0,0,0,0,0,0,0,0,0,0,0,0,a.X,a.Y,a.Z,1);
            else
            {
                var direction=axis;direction.Normalize();
                var u=Vector3D.CrossProduct(direction,Math.Abs(direction.Z)<.85?new Vector3D(0,0,1):new Vector3D(0,1,0));u.Normalize();
                var v=Vector3D.CrossProduct(direction,u);
                slot.Tube.Matrix=new Matrix3D(u.X*radius,u.Y*radius,u.Z*radius,0,v.X*radius,v.Y*radius,v.Z*radius,0,axis.X,axis.Y,axis.Z,0,a.X,a.Y,a.Z,1);
            }
            double r=radius*1.04;
            slot.JointA.Matrix=new Matrix3D(r,0,0,0,0,r,0,0,0,0,r,0,a.X,a.Y,a.Z,1);
            if(axis.LengthSquared<1e-16)r=0;
            slot.JointB.Matrix=new Matrix3D(r,0,0,0,0,r,0,0,0,0,r,0,b.X,b.Y,b.Z,1);
            slot.Bounds=new Rect3D(new Point3D(Math.Min(a.X,b.X)-radius,Math.Min(a.Y,b.Y)-radius,Math.Min(a.Z,b.Z)-radius),
                new Size3D(Math.Abs(b.X-a.X)+2*radius,Math.Abs(b.Y-a.Y)+2*radius,Math.Abs(b.Z-a.Z)+2*radius));
        }
        private GeometryModel3D DynamicModel(MeshGeometry3D mesh,MediaColor color,double opacity,Transform3D placement)
        {
            if(!mesh.IsFrozen)mesh.Freeze();
            byte alpha=(byte)Math.Round(Clamp(opacity,0,1)*color.A);
            long key=((long)alpha<<24)|((long)color.R<<16)|((long)color.G<<8)|color.B;
            if(!_materials.TryGetValue(key,out var material))
            {
                var brush=new SolidColorBrush(MediaColor.FromArgb(alpha,color.R,color.G,color.B));brush.Freeze();
                material=new DiffuseMaterial(brush);material.Freeze();_materials[key]=material;
            }
            return new GeometryModel3D(mesh,material){BackMaterial=material,Transform=placement};
        }
        private static MeshGeometry3D CreateTubePrimitive()
        {var mesh=new MeshGeometry3D();AddTube(mesh,new Point3D(),new Point3D(0,0,1),1,8);mesh.Freeze();return mesh;}
        private static MeshGeometry3D CreateJointPrimitive()
        {var mesh=new MeshGeometry3D();AddSphere(mesh,new Point3D(),1);mesh.Freeze();return mesh;}

        internal void Fit()
        {
            var bounds = _demonstration?Rect3D.Empty:_sourceBounds;
            if (!_demonstration) bounds.Union(new Rect3D(_clipMin,
                new Size3D(_clipMax.X - _clipMin.X, _clipMax.Y - _clipMin.Y, _clipMax.Z - _clipMin.Z)));
            if (!_overlayBounds.IsEmpty) bounds.Union(_overlayBounds);
            if (bounds.IsEmpty) bounds = new Rect3D(-1, -1, -1, 2, 2, 2);
            _target = new Point3D(bounds.X + bounds.SizeX/2, bounds.Y + bounds.SizeY/2, bounds.Z + bounds.SizeZ/2);
            double diameter = Math.Max(1, new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length);
            // The footer occupies its own row, so only the actual 3D surface
            // determines the visible vertical span. Before layout use the
            // control dimensions until Loaded performs the definitive fit.
            double viewWidth = _viewport.ActualWidth > 1 ? _viewport.ActualWidth : ActualWidth;
            double viewHeight = _viewport.ActualHeight > 1 ? _viewport.ActualHeight : ActualHeight;
            double aspect = viewWidth > 1 && viewHeight > 1 ? Math.Max(.01, viewWidth / viewHeight) : 1.2;
            var look = -new Vector3D(Math.Cos(_yaw)*Math.Cos(_pitch), Math.Sin(_yaw)*Math.Cos(_pitch), Math.Sin(_pitch));
            var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1)); right.Normalize();
            var up = Vector3D.CrossProduct(right, look); up.Normalize();
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Point3D(bounds.X + ((i & 1) == 0 ? 0 : bounds.SizeX),
                    bounds.Y + ((i & 2) == 0 ? 0 : bounds.SizeY), bounds.Z + ((i & 4) == 0 ? 0 : bounds.SizeZ));
                var relative = corner - _target;
                double x = Vector3D.DotProduct(relative, right), y = Vector3D.DotProduct(relative, up);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            _target += right * ((minX + maxX) / 2) + up * ((minY + maxY) / 2);
            _width = Math.Max(.1, Math.Max(maxX - minX, (maxY - minY) * aspect) * 1.10);
            _distance = diameter * 4 + 10;
            UpdateCamera();
        }

        private void UpdateCamera()
        {
            var offset = new Vector3D(Math.Cos(_yaw)*Math.Cos(_pitch), Math.Sin(_yaw)*Math.Cos(_pitch), Math.Sin(_pitch)) * _distance;
            _camera.Position = _target + offset;
            _camera.LookDirection = -offset;
            _camera.UpDirection = new Vector3D(0, 0, 1);
            _camera.Width = _width;
            _lastVisualActivity=Stopwatch.GetTimestamp();
            if(_captureLabel!=null&&IsLoaded&&!_renderingAttached)AttachRendering();
            _transparencyDirty=true;
            SortTransparentFaces();
        }

        private void SortTransparentFaces(bool force=false)
        {
            if(!_transparencyDirty&&!force)return;
            long now=Stopwatch.GetTimestamp();
            if(_transparentGroup==null)
            {
                _transparentGroup=new Model3DGroup();_transparentOrder=new List<TransparentFace>();
                _overlayTransparentVisual.Content=_transparentGroup;_transparentStructureDirty=true;
            }
            if(_transparentStructureDirty)
            {
                _transparentOrder.Clear();
                foreach(var face in _transparentFaces)_transparentOrder.Add(new TransparentFace {Model=face, StableIndex=_transparentOrder.Count});
            }
            // Cached order changes only when faces actually cross in depth.
            // Insertion sort is linear for the usual unchanged/nearly-sorted
            // pose, and reuses both model group and sorting records.
            foreach(var face in _transparentOrder)
            {
                var b=face.Model.Bounds;
                var center = new Point3D(b.X+b.SizeX/2, b.Y+b.SizeY/2, b.Z+b.SizeZ/2);
                face.Depth=Vector3D.DotProduct(center-_camera.Position,_camera.LookDirection);
            }
            bool changed=_transparentStructureDirty;
            for(int i=1;i<_transparentOrder.Count;i++)
            {
                var item=_transparentOrder[i];int j=i-1;
                while(j>=0&&(_transparentOrder[j].Depth<item.Depth
                    ||_transparentOrder[j].Depth==item.Depth&&_transparentOrder[j].StableIndex>item.StableIndex))
                {_transparentOrder[j+1]=_transparentOrder[j];j--;changed=true;}
                _transparentOrder[j+1]=item;
            }
            if(changed)
            {
                _transparentGroup.Children.Clear();
                foreach(var face in _transparentOrder)_transparentGroup.Children.Add(face.Model);
                _transparencyOrderChanges++;
            }
            _transparentStructureDirty=false;_transparencySorts++;
            _lastTransparencySort=now;_transparencyDirty=false;
            LastTransparencySortMilliseconds=(Stopwatch.GetTimestamp()-now)*1000.0/Stopwatch.Frequency;
        }

        private sealed class TransparentFace
        {internal GeometryModel3D Model;internal double Depth;internal int StableIndex;}
        private sealed class TimingWindow
        {
            private readonly double[] _samples=new double[240];private int _next;
            internal int Count {get;private set;}
            internal void Add(double value)
            {_samples[_next]=value;_next=(_next+1)%_samples.Length;if(Count<_samples.Length)Count++;}
            internal double Percentile(double percentile)
            {
                if(Count==0)return 0;
                var values=new double[Count];Array.Copy(_samples,values,Count);Array.Sort(values);
                return values[Math.Min(values.Length-1,(int)Math.Ceiling(percentile*values.Length)-1)];
            }
        }
        private static void RecordInterval(ref TimingWindow window,ref long previous,long now)
        {
            if(previous!=0)
            {double ms=(now-previous)*1000.0/Stopwatch.Frequency;if(ms>0&&ms<=250)(window??(window=new TimingWindow())).Add(ms);}
            previous=now;
        }
        private void RecordInput(int eventTimestamp)
        {
            long now=Stopwatch.GetTimestamp();int queued=unchecked(Environment.TickCount-eventTimestamp);
            long timestamp=now-(queued>=0&&queued<1000?(long)(queued*Stopwatch.Frequency/1000.0):0);
            if(_pendingInput==0)_pendingInput=timestamp;
        }

        private void AttachRendering()
        {
            if(_renderingAttached)return;
            _renderingAttached=true;_renderWindowStart=Stopwatch.GetTimestamp();_renderWindowFrames=0;_lastVisualActivity=_renderWindowStart;
            CompositionTarget.Rendering+=OnRendering;
        }
        private void DetachRendering()
        {
            if(!_renderingAttached)return;
            CompositionTarget.Rendering-=OnRendering;_renderingAttached=false;
        }
        private void OnRendering(object sender,EventArgs e)
        {
            if(!IsVisible)return;
            if(e is RenderingEventArgs rendering)
            {
                if(rendering.RenderingTime==_lastRenderTime)return;
                _lastRenderTime=rendering.RenderingTime;
            }
            long now=Stopwatch.GetTimestamp();
            RecordInterval(ref _renderIntervals,ref _lastRenderingCallback,now);
            if(_pendingInput!=0)
            {( _inputLatencies??(_inputLatencies=new TimingWindow())).Add(Math.Max(0,(now-_pendingInput)*1000.0/Stopwatch.Frequency));_pendingInput=0;}
            // Diagnostic frame counting must not keep an idle viewer rendering.
            if((now-_lastVisualActivity)/(double)Stopwatch.Frequency>2){DetachRendering();return;}
            _renderWindowFrames++;
            double seconds=(now-_renderWindowStart)/(double)Stopwatch.Frequency;
            if(seconds<.75)return;
            RenderFramesPerSecond=_renderWindowFrames/seconds;
            _renderWindowStart=now;_renderWindowFrames=0;
            if(_captureLabel!=null)_captureLabel.ToolTip=PreviewTiming;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if(e.ClickCount==2){RecordInput(e.Timestamp);Fit();e.Handled=true;return;}
            _previous = e.GetPosition(_inputSurface); _dragging = true;UpdateRenderQuality();
            _inputSurface.CaptureMouse(); _inputSurface.Cursor = Cursors.SizeAll; e.Handled = true;
        }
        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            RecordInput(e.Timestamp);
            var p = e.GetPosition(_inputSurface);
            _yaw -= (p.X - _previous.X) * .009;
            _pitch = Clamp(_pitch + (p.Y - _previous.Y) * .009, -1.42, 1.42);
            _previous = p; UpdateCamera(); e.Handled = true;
        }
        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        { if (!_dragging) return; _dragging = false; _inputSurface.ReleaseMouseCapture(); _inputSurface.Cursor = Cursors.Arrow;UpdateRenderQuality(); SortTransparentFaces(true); e.Handled = true; }

        private sealed class Candidate
        {
            internal Element Element;
            internal DbTransform Transform;
            internal double Distance;
            internal long ElementId, LinkId;
            internal string LinkName;
            internal bool Blocked;
        }

        private sealed class ContextEntry
        {
            internal long ElementId, LinkId;
            internal string LinkName;
            internal Model3D Normal, Blocked;
        }

        private void CaptureContext(Document doc, Element source)
        {
            var excluded = new HashSet<ElementId> { source.Id };
            if (source is FamilyInstance family)
            {
                var queue = new Queue<ElementId>(family.GetSubComponentIds());
                while (queue.Count > 0)
                {
                    var id = queue.Dequeue(); if (!excluded.Add(id)) continue;
                    _sourceComponentIds.Add(BoosterIds.Value(id));
                    if (doc.GetElement(id) is FamilyInstance child) foreach (var sub in child.GetSubComponentIds()) queue.Enqueue(sub);
                }
            }
            var candidates = new List<Candidate>();
            Collect(doc, DbTransform.Identity, excluded, candidates, -1, null);
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                try
                {
                    var linked = link.GetLinkDocument();
                    if (linked == null) { _unloadedLinks++; continue; }
                    Collect(linked, link.GetTotalTransform(), null, candidates, BoosterIds.Value(link.Id), link.Name);
                }
                catch (Exception ex) when (Recoverable(ex)) { _failedElements++; }
            }
            var watch = Stopwatch.StartNew();
            int visited = 0;
            // Give every nearby element a fair mesh budget before fine details
            // consume the capture. Confirmed blockers are always visited first.
            int fairShare = Math.Max(1000, Math.Min(MaxElementTriangles,
                MaxContextTriangles / Math.Max(1, Math.Min(MaxContextElements, candidates.Count))));
            foreach (var candidate in candidates.OrderByDescending(c => c.Blocked).ThenBy(c => c.Distance))
            {
                if (visited++ >= MaxContextElements || _contextTriangles >= MaxContextTriangles || watch.ElapsedMilliseconds > 8000)
                { _truncated = true; break; }
                var context = new MeshGeometry3D();
                try
                {
                    int available = Math.Min(candidate.Blocked ? MaxElementTriangles : fairShare, MaxContextTriangles - _contextTriangles);
                    CaptureElement(candidate.Element, candidate.Transform, context, available, true);
                }
                catch (Exception ex) when (Recoverable(ex)) { _failedElements++; }
                int added = context.TriangleIndices.Count/3;
                if (added > 0)
                {
                    _contextElements++; _contextTriangles += added;
                    _contextEntries.Add(new ContextEntry { ElementId = candidate.ElementId, LinkId = candidate.LinkId, LinkName = candidate.LinkName,
                        Normal = ContextModel(context, false), Blocked = ContextModel(context, true) });
                }
            }
        }

        private void Collect(Document doc, DbTransform transform, HashSet<ElementId> excluded, List<Candidate> candidates, long linkId, string linkName)
        {
            var inverse = transform.Inverse;
            var localRegion = new List<XYZ>();
            for (int i=0; i<8; i++) localRegion.Add(inverse.OfPoint(_origin + new XYZ(
                (i&1)==0 ? _clipMin.X : _clipMax.X, (i&2)==0 ? _clipMin.Y : _clipMax.Y, (i&4)==0 ? _clipMin.Z : _clipMax.Z)));
            using (var outline = new Outline(Minimum(localRegion), Maximum(localRegion)))
            using (var filter = new BoundingBoxIntersectsFilter(outline))
            using (var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter))
            {
                foreach (var element in collector)
                {
                    if (element is RevitLinkInstance || element is SpatialElement || element is View || element is BasePoint || element is SiteLocation || element is ProjectLocation || element is ProjectInfo || element is Level || element is Autodesk.Revit.DB.Grid
                        || excluded != null && excluded.Contains(element.Id)
                        || element.Category == null || element.Category.CategoryType != CategoryType.Model) continue;
                    try
                    {
                        var box = element.get_BoundingBox(null); if (box == null) continue;
                        var points = BoundingCorners(box, transform).ToArray();
                        var lo = Minimum(points); var hi = Maximum(points);
                        // Distance to the nearest point avoids ranking a long
                        // wall/floor last merely because its centre is far away.
                        var near = new XYZ(Clamp(_origin.X, lo.X, hi.X), Clamp(_origin.Y, lo.Y, hi.Y), Clamp(_origin.Z, lo.Z, hi.Z));
                        long id = BoosterIds.Value(element.Id);
                        candidates.Add(new Candidate { Element = element, Transform = transform, Distance = near.DistanceTo(_origin),
                            ElementId = id, LinkId = linkId, LinkName = linkName, Blocked = IsBlocked(id, linkId, linkName) });
                    }
                    catch (Exception ex) when (Recoverable(ex)) { _failedElements++; }
                }
            }
        }

        private void CaptureElement(Element element, DbTransform transform, MeshGeometry3D target, int triangleLimit, bool clip)
        {
            if (element == null || transform == null || target == null) { _unknownGeometry++; return; }
            int visitedTriangles = 0;
            int visitLimit = clip ? 250000 : MaxSelectedTriangles * 2;
            var captureWatch = Stopwatch.StartNew();
            using (var options = new Options { DetailLevel = clip ? ViewDetailLevel.Coarse : ViewDetailLevel.Fine, IncludeNonVisibleObjects = false, ComputeReferences = false })
            using (var geometry = element.get_Geometry(options))
            {
                if (geometry != null) Read(geometry, transform, 0);
                else _unknownGeometry++;
            }
            void Read(GeometryElement geometry, DbTransform world, int depth)
            {
                if (geometry == null || world == null) { _unknownGeometry++; return; }
                if (depth > 12) { _truncated = true; return; }
                foreach (var item in geometry)
                {
                    if (AtLimit()) return;
                    if (item == null) { _unknownGeometry++; continue; }
                    if (item is GeometryInstance instance)
                    {
                        using (var expanded = instance.GetSymbolGeometry())
                        {
                            var placement = instance.Transform;
                            if (expanded != null && placement != null) Read(expanded, world.Multiply(placement), depth+1);
                            else _unknownGeometry++;
                        }
                    }
                    else if (item is Solid solid)
                    {
                        if (solid.Faces == null) { _unknownGeometry++; continue; }
                        foreach (Face face in solid.Faces)
                        {
                            if (AtLimit()) return;
                            if (face == null) { _unknownGeometry++; continue; }
                            try { using (var mesh = face.Triangulate(clip ? .15 : .5)) AddMesh(mesh, world); }
                            catch (Exception ex) when (Recoverable(ex)) { _unknownGeometry++; }
                        }
                    }
                    else if (item is Mesh mesh) AddMesh(mesh, world);
                }
            }
            void AddMesh(Mesh mesh, DbTransform world)
            {
                // Some imported/native Revit faces return no tessellation. Keep
                // the rest of the snapshot and report the missing fragment.
                if (mesh == null || world == null) { _unknownGeometry++; return; }
                for (int i=0; i<mesh.NumTriangles; i++)
                {
                    if (AtLimit()) return;
                    visitedTriangles++;
                    var triangle = mesh.get_Triangle(i);
                    if (triangle == null) { _unknownGeometry++; continue; }
                    var va = triangle.get_Vertex(0); var vb = triangle.get_Vertex(1); var vc = triangle.get_Vertex(2);
                    if (va == null || vb == null || vc == null) { _unknownGeometry++; continue; }
                    var a = Local(world.OfPoint(va));
                    var b = Local(world.OfPoint(vb));
                    var c = Local(world.OfPoint(vc));
                    if (!Finite(a) || !Finite(b) || !Finite(c)) { _unknownGeometry++; continue; }
                    if (clip) AddClipped(target, a, b, c, triangleLimit);
                    else AddTriangle(target, a, b, c);
                }
            }
            bool AtLimit()
            {
                bool reached = target.TriangleIndices.Count/3 >= triangleLimit || visitedTriangles >= visitLimit
                    || captureWatch.ElapsedMilliseconds > (clip ? 1200 : 2500);
                if (reached) _truncated = true;
                return reached;
            }
        }

        // Clip large floors/walls to the capture region, so one long wall does not
        // obscure the work position or make camera fitting depend on the building.
        private void AddClipped(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, int limit)
        {
            double loX = Math.Min(a.X, Math.Min(b.X, c.X)), hiX = Math.Max(a.X, Math.Max(b.X, c.X));
            double loY = Math.Min(a.Y, Math.Min(b.Y, c.Y)), hiY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            double loZ = Math.Min(a.Z, Math.Min(b.Z, c.Z)), hiZ = Math.Max(a.Z, Math.Max(b.Z, c.Z));
            if (hiX < _clipMin.X || loX > _clipMax.X || hiY < _clipMin.Y || loY > _clipMax.Y || hiZ < _clipMin.Z || loZ > _clipMax.Z) return;
            if (loX >= _clipMin.X && hiX <= _clipMax.X && loY >= _clipMin.Y && hiY <= _clipMax.Y && loZ >= _clipMin.Z && hiZ <= _clipMax.Z)
            {
                if (mesh.TriangleIndices.Count / 3 >= limit) { _truncated = true; return; }
                AddTriangle(mesh, a, b, c); return;
            }
            var polygon = new List<Point3D> { a, b, c };
            for (int axis=0; axis<3 && polygon.Count>0; axis++)
            {
                polygon = ClipPolygon(polygon, axis, Coordinate(_clipMin, axis), true);
                polygon = ClipPolygon(polygon, axis, Coordinate(_clipMax, axis), false);
            }
            for (int i=1; i+1<polygon.Count; i++)
            {
                if (mesh.TriangleIndices.Count/3 >= limit) { _truncated = true; return; }
                AddTriangle(mesh, polygon[0], polygon[i], polygon[i+1]);
            }
        }
        private static List<Point3D> ClipPolygon(List<Point3D> polygon, int axis, double plane, bool above)
        {
            var result = new List<Point3D>();
            if (polygon.Count==0) return result;
            var previous = polygon[polygon.Count-1];
            double previousD = Coordinate(previous,axis)-plane;
            bool previousIn = above ? previousD>=-1e-8 : previousD<=1e-8;
            foreach (var current in polygon)
            {
                double currentD=Coordinate(current,axis)-plane;
                bool currentIn=above ? currentD>=-1e-8 : currentD<=1e-8;
                if (currentIn!=previousIn)
                {
                    double t=previousD/(previousD-currentD);
                    result.Add(previous+(current-previous)*t);
                }
                if(currentIn) result.Add(current);
                previous=current; previousD=currentD; previousIn=currentIn;
            }
            return result;
        }

        private Point3D Local(XYZ p) => _demonstration?new Point3D(p.X,p.Y,p.Z):new Point3D(p.X-_origin.X, p.Y-_origin.Y, p.Z-_origin.Z);
        private static double Coordinate(Point3D p,int axis) => axis==0 ? p.X : axis==1 ? p.Y : p.Z;
        private static bool Finite(Point3D p) => !double.IsNaN(p.X) && !double.IsInfinity(p.X)
            && !double.IsNaN(p.Y) && !double.IsInfinity(p.Y) && !double.IsNaN(p.Z) && !double.IsInfinity(p.Z);
        private static XYZ Minimum(IEnumerable<XYZ> points) => new XYZ(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z));
        private static XYZ Maximum(IEnumerable<XYZ> points) => new XYZ(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z));
        private static IEnumerable<XYZ> BoundingCorners(BoundingBoxXYZ box,DbTransform world)
        {
            var transform=world.Multiply(box.Transform);
            for(int i=0;i<8;i++) yield return transform.OfPoint(new XYZ((i&1)==0?box.Min.X:box.Max.X,(i&2)==0?box.Min.Y:box.Max.Y,(i&4)==0?box.Min.Z:box.Max.Z));
        }
        private static bool Recoverable(Exception ex) => ex is Autodesk.Revit.Exceptions.ApplicationException
            || ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException;
        private static double Clamp(double value,double min,double max) => Math.Max(min,Math.Min(max,value));
        private static MediaColor OpaqueColor(MediaColor color) => MediaColor.FromRgb(color.R,color.G,color.B);
        private static void AddQuad(MeshGeometry3D mesh,Point3D[] points,int[] face)
        {
            AddTriangle(mesh,points[face[0]],points[face[1]],points[face[2]]);
            AddTriangle(mesh,points[face[0]],points[face[2]],points[face[3]]);
        }
        private IEnumerable<MeshGeometry3D> VisualMeshes()
            => Meshes(_selectedSnapshot).Concat(Meshes(_contextSnapshot)).Concat(Meshes(_blockedSnapshot)).Concat(Meshes(_dynamicOpaque));
        private static IEnumerable<MeshGeometry3D> Meshes(Model3D model)
        {
            if(model is GeometryModel3D geometry&&geometry.Geometry is MeshGeometry3D mesh)yield return mesh;
            else if(model is Model3DGroup group)
                foreach(var child in group.Children)foreach(var childMesh in Meshes(child))yield return childMesh;
        }
        // Blocking context remains visible without hiding the selected mechanism
        // or the opaque person behind it. These frozen snapshots are made once;
        // playback only reuses them, just like ordinary translucent context.
        private static Model3D ContextModel(MeshGeometry3D mesh,bool blocked)
            => Model(mesh,blocked?Colors.OrangeRed:MediaColor.FromRgb(125,139,149),blocked?.45:.23);
        private static Model3D Model(MeshGeometry3D mesh,MediaColor color,double opacity)
        {
            var brush=new SolidColorBrush(MediaColor.FromArgb((byte)Math.Round(Clamp(opacity,0,1)*color.A),color.R,color.G,color.B));
            brush.Freeze();
            var material=new DiffuseMaterial(brush); material.Freeze();
            var chunks=SnapshotChunks.GetValue(mesh,SplitSnapshotMesh);
            GeometryModel3D Make(MeshGeometry3D part)
            {var model=new GeometryModel3D(part,material){BackMaterial=material};model.Freeze();return model;}
            if(chunks.Length==1)return Make(chunks[0]);
            var group=new Model3DGroup();foreach(var part in chunks)group.Children.Add(Make(part));group.Freeze();return group;
        }
        private static MeshGeometry3D[] SplitSnapshotMesh(MeshGeometry3D source)
        {
            const int maxPositions=18000,maxIndices=60000;
            if(source.Positions.Count<=maxPositions&&source.TriangleIndices.Count<=maxIndices)
            {if(!source.IsFrozen)source.Freeze();return new[]{source};}
            var result=new List<MeshGeometry3D>();
            var part=new MeshGeometry3D();var map=new Dictionary<int,int>();
            bool normals=source.Normals.Count==source.Positions.Count,texture=source.TextureCoordinates.Count==source.Positions.Count;
            int count=source.TriangleIndices.Count>0?source.TriangleIndices.Count:source.Positions.Count-source.Positions.Count%3;
            int Index(int i)=>source.TriangleIndices.Count>0?source.TriangleIndices[i]:i;
            for(int i=0;i+2<count;i+=3)
            {
                int a=Index(i),b=Index(i+1),c=Index(i+2);
                int added=(map.ContainsKey(a)?0:1)+(b==a||map.ContainsKey(b)?0:1)+(c==a||c==b||map.ContainsKey(c)?0:1);
                if(part.Positions.Count+added>maxPositions||part.TriangleIndices.Count+3>maxIndices)
                {part.Freeze();result.Add(part);part=new MeshGeometry3D();map.Clear();}
                void Add(int original)
                {
                    if(!map.TryGetValue(original,out int local))
                    {
                        local=part.Positions.Count;map.Add(original,local);part.Positions.Add(source.Positions[original]);
                        if(normals)part.Normals.Add(source.Normals[original]);if(texture)part.TextureCoordinates.Add(source.TextureCoordinates[original]);
                    }
                    part.TriangleIndices.Add(local);
                }
                Add(a);Add(b);Add(c);
            }
            if(part.TriangleIndices.Count>0){part.Freeze();result.Add(part);}
            if(!source.IsFrozen)source.Freeze();return result.ToArray();
        }
        private static void AddTriangle(MeshGeometry3D mesh,Point3D a,Point3D b,Point3D c)
        {
            if (Vector3D.CrossProduct(b-a,c-a).LengthSquared<1e-18) return;
            int index=mesh.Positions.Count;
            mesh.Positions.Add(a);mesh.Positions.Add(b);mesh.Positions.Add(c);
            mesh.TriangleIndices.Add(index);mesh.TriangleIndices.Add(index+1);mesh.TriangleIndices.Add(index+2);
        }
        private static void AddTube(MeshGeometry3D mesh,Point3D a,Point3D b,double radius,int sides)
        {
            var axis=b-a;if(axis.LengthSquared<1e-16)return;axis.Normalize();
            var u=Vector3D.CrossProduct(axis,Math.Abs(axis.Z)<.85?new Vector3D(0,0,1):new Vector3D(0,1,0));u.Normalize();
            var v=Vector3D.CrossProduct(axis,u);
            for(int i=0;i<sides;i++)
            {
                double t=2*Math.PI*i/sides,next=2*Math.PI*(i+1)/sides;
                var r=(u*Math.Cos(t)+v*Math.Sin(t))*radius;
                var s=(u*Math.Cos(next)+v*Math.Sin(next))*radius;
                AddTriangle(mesh,a+r,b+r,b+s);AddTriangle(mesh,a+r,b+s,a+s);
                AddTriangle(mesh,a,a+s,a+r);AddTriangle(mesh,b,b+r,b+s);
            }
        }
        private static void AddSphere(MeshGeometry3D mesh,Point3D center,double radius)
        {
            const int latitude=6,longitude=10;
            Point3D Point(int i,int j)
            {
                double theta=Math.PI*i/latitude,phi=2*Math.PI*j/longitude;
                return center+new Vector3D(Math.Sin(theta)*Math.Cos(phi),Math.Sin(theta)*Math.Sin(phi),Math.Cos(theta))*radius;
            }
            for(int i=0;i<latitude;i++)for(int j=0;j<longitude;j++)
            {var a=Point(i,j);var b=Point(i,j+1);var c=Point(i+1,j+1);var d=Point(i+1,j);AddTriangle(mesh,a,b,c);AddTriangle(mesh,a,c,d);}
        }
        private static Point3D[] TorsoPoints(Point3D[] envelope)
        {
            var origin=envelope[0];
            var x=envelope[1]-origin;var y=envelope[2]-origin;var z=envelope[4]-origin;
            var points=new Point3D[8];
            for(int i=0;i<8;i++)
            {
                bool top=(i&4)!=0;
                double side=top ? .02 : .18;
                points[i]=origin+x*((i&1)==0?side:1-side)+y*((i&2)==0?.08:.92)+z*(top?1:0);
            }
            return points;
        }
        private static void AddEllipsoid(MeshGeometry3D mesh,Point3D[] envelope)
        {
            var x=(envelope[1]-envelope[0])*.5;
            var y=(envelope[2]-envelope[0])*.5;
            var z=(envelope[4]-envelope[0])*.5;
            var center=envelope[0]+x+y+z;
            const int latitude=12,longitude=20;
            Point3D Point(int i,int j)
            {
                double theta=Math.PI*i/latitude,phi=2*Math.PI*j/longitude;
                return center+x*(Math.Sin(theta)*Math.Cos(phi))+y*(Math.Sin(theta)*Math.Sin(phi))+z*Math.Cos(theta);
            }
            for(int i=0;i<latitude;i++)for(int j=0;j<longitude;j++)
            {var a=Point(i,j);var b=Point(i,j+1);var c=Point(i+1,j+1);var d=Point(i+1,j);AddTriangle(mesh,a,b,c);AddTriangle(mesh,a,c,d);}
        }
        private static readonly int[][] BoxFaces =
        {
            new[]{0,2,3,1},new[]{4,5,7,6},new[]{0,1,5,4},new[]{2,6,7,3},new[]{0,4,6,2},new[]{1,3,7,5}
        };
    }
}

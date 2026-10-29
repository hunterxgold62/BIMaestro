using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Licensing;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using Transform = Autodesk.Revit.DB.Transform;
using BIMaestro.Localization;

namespace BIMaestro.MepBooster
{
    [Transaction(TransactionMode.Manual)]
    public sealed class MaintenanceWorkbenchCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "MaintenanceWorkbench";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui == null || ui.Document.IsFamilyDocument) return Result.Cancelled;
            var start = new MaintenanceStartWindow(data.Application.MainWindowHandle);
            if (start.ShowDialog() != true) return Result.Cancelled;
            if (start.Scenario == 0) return MepMotorExtractionCommand.RunWorkflow(data, ref message);
            try
            {
                var existing = ui.Selection.GetElementIds().Select(ui.Document.GetElement).OfType<FamilyInstance>().ToList();
                var source = existing.Count == 1 && AccessSelection.Accept(existing[0]) ? existing[0]
                    : ui.Document.GetElement(ui.Selection.PickObject(ObjectType.Element, new AccessSelection(),
                        start.Scenario == 2 ? UiLanguage.T("Sélectionnez le filtre à entretenir.", "Select the filter to service.") : start.Scenario == 3 ? UiLanguage.T("Sélectionnez la vanne dont l’actionneur doit être déposé.", "Select the valve whose actuator must be removed.") : UiLanguage.T("Sélectionnez la vanne à ouvrir ou fermer manuellement.", "Select the valve to open or close manually."))) as FamilyInstance;
                if (source == null) return Result.Cancelled;
                var state = new MaintenanceWorkbenchState { Operation = start.Scenario == 2 ? MaintenanceAccessOperation.Filter : start.Scenario == 3 ? MaintenanceAccessOperation.Actuator : MaintenanceAccessOperation.HandwheelValve };
                Prepare(false);
                while (true)
                {
                    var window = new MaintenanceWorkbenchWindow(ui.Document, source, state, data.Application.MainWindowHandle);
                    window.ShowDialog();
                    if (window.PickRequest == null) break;
                    try
                    {
                        if (window.PickRequest == "auto") { Prepare(false); continue; }
                        if (window.PickRequest == "position") { Prepare(true); continue; }
                        if (window.PickRequest == "command")
                        {
                            var reference = ui.Selection.PickObject(ObjectType.PointOnElement, new AccessSelection(source.Id),
                                UiLanguage.T("Cliquez le centre du volant, l’axe du levier, la roue de chaîne ou le capot à retirer.", "Click the handwheel center, the lever axis, the chainwheel or the cover to remove."));
                            state.CommandPoint = reference.GlobalPoint;
                            state.CorrectionApplied=false;state.CorrectionNote=UiLanguage.T("Centre corrigé manuellement ; mémorisez les réglages pour réutiliser cette correction.", "Center corrected manually; save the settings to reuse this correction.");
                            state.Measured=false;state.Manufacturer=false;
                            state.FeatureWarnings.Add(UiLanguage.T("Centre pointé manuellement : vérifiez le repère, le sens et l’origine des cotes avant le contrôle.", "Center picked manually: check the reference frame, direction and dimension origin before the check."));
                            var nearby=state.Candidates.Where(c=>c.CommandPoint!=null&&c.Axis!=null)
                                .OrderBy(c=>(c.CommandPoint-reference.GlobalPoint).GetLength()).FirstOrDefault();
                            if(nearby!=null&&(nearby.CommandPoint-reference.GlobalPoint).GetLength()<Math.Max(nearby.Radius,50/304.8))
                                MaintenanceAutoconfiguration.ApplyCandidate(state,nearby);
                            state.Confirmed = false;
                            Prepare(true);
                        }
                        else if(window.PickRequest=="axis")
                        {
                            var center=ui.Selection.PickObject(ObjectType.PointOnElement,new AccessSelection(source.Id),UiLanguage.T("Cliquez le centre du capot ou de la commande.", "Click the center of the cover or the operator.")).GlobalPoint;
                            var end=ui.Selection.PickObject(ObjectType.PointOnElement,UiLanguage.T("Cliquez un point indiquant le sens de retrait ou l’axe de commande.", "Click a point indicating the removal direction or the operating axis.")).GlobalPoint;
                            if((end-center).GetLength()<2/304.8)throw new ArgumentException(UiLanguage.T("Les deux points doivent être distincts.", "The two points must be different."));
                            MaintenanceAutoconfiguration.ApplyCandidate(state,new MaintenanceFeatureCandidate {CommandPoint=center,Axis=(end-center).Normalize()});
                            Prepare(true);
                        }
                        else if(window.PickRequest=="handle")
                        {
                            if(state.CommandPoint==null)throw new ArgumentException(UiLanguage.T("Précisez le pivot avant de pointer la poignée.", "Define the pivot before picking the handle."));
                            var end=ui.Selection.PickObject(ObjectType.PointOnElement,new AccessSelection(source.Id),UiLanguage.T("Cliquez l’extrémité de la poignée, à l’endroit de la prise de main.", "Click the end of the handle, where it is gripped.")).GlobalPoint;
                            var profile=MaintenanceAutoconfiguration.ReadSavedProfile(source,state);
                            XYZ z=profile.Frame.BasisZ,radial=end-state.CommandPoint;
                            radial-=z*radial.DotProduct(z);
                            if(radial.GetLength()<2/304.8)throw new ArgumentException(UiLanguage.T("L’extrémité doit être éloignée de l’axe du pivot.", "The end point must be away from the pivot axis."));
                            var frame=Transform.Identity;frame.Origin=state.CommandPoint;frame.BasisX=radial.Normalize();frame.BasisY=z.CrossProduct(frame.BasisX).Normalize();frame.BasisZ=z;
                            MaintenanceAutoconfiguration.ApplyCandidate(state,new MaintenanceFeatureCandidate {CommandPoint=state.CommandPoint,Axis=z,Frame=frame});
                            state.Fields["Radius"]=(radial.GetLength()*304.8).ToString("0.###",CultureInfo.CurrentCulture);
                            state.Fields["InitialAngle"]="0";
                            Prepare(true);
                        }
                        else
                        {
                            state.FootPoint = ui.Selection.PickObject(ObjectType.PointOnElement, new WalkingSurfaceSelection(ui.Document),
                                UiLanguage.T("Cliquez un sol praticable au centre des pieds. Les tuyaux et équipements ne sont pas des appuis.", "Click a walkable floor at the center of the feet. Pipes and equipment are not valid supports.")).GlobalPoint;
                            state.Confirmed = false;
                            state.SupportVerified = false;
                            state.SupportNote = UiLanguage.T("Sol pointé manuellement ; appuis et posture à vérifier.", "Floor picked manually; supports and posture to be checked.");
                            var profile = MaintenanceAutoconfiguration.ReadSavedProfile(source,state);
                            var placement = MaintenanceOperatorPlacement.Validate(ui.Document,source,profile);
                            MaintenanceAutoconfiguration.ApplyPlacementResult(state,placement);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                    catch(ArgumentException error){TaskDialog.Show("Repère à préciser",error.Message);}
                }
                return Result.Succeeded;
                void Prepare(bool placementOnly)
                {
                    var label = new TextBlock { Text = UiLanguage.T("Préparation de l’intervention…", "Preparing the intervention…"), Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };
                    var wait = new Window { Title = UiLanguage.T("Maintenance MEP", "MEP Maintenance"), Content = label, Width = 420, Height = 120, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    new WindowInteropHelper(wait).Owner = data.Application.MainWindowHandle;
                    wait.Show();
                    try
                    {
                        void Progress(string text)
                        {
                            label.Text=text;
                            var frame=new DispatcherFrame();wait.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
                        }
                        if(placementOnly)MaintenanceAutoconfiguration.Reposition(ui.Document,source,state,Progress);
                        else MaintenanceAutoconfiguration.Suggest(ui.Document,source,state,Progress);
                    }
                    catch(Exception ex)
                    {state.FootPoint=null;state.SupportVerified=false;state.NoOperatorReason=UiLanguage.T("Proposition automatique indisponible : ", "Automatic proposal unavailable: ")+ex.Message;state.PlacementWarnings.Add(state.NoOperatorReason);}
                    finally {wait.Close();}
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
        private sealed class WalkingSurfaceSelection : ISelectionFilter
        {
            private readonly Document _doc;
            internal WalkingSurfaceSelection(Document doc) { _doc=doc; }
            public bool AllowElement(Element e) => e is RevitLinkInstance || MaintenanceOperatorPlacement.IsWalkingSurface(e);
            public bool AllowReference(Reference r, XYZ p)
            {
                Element e=_doc.GetElement(r.ElementId);
                if(e is RevitLinkInstance link)e=link.GetLinkDocument()?.GetElement(r.LinkedElementId);
                return e!=null&&MaintenanceOperatorPlacement.IsWalkingSurface(e);
            }
        }
        private sealed class AccessSelection : ISelectionFilter
        {
            private readonly ElementId _only;
            internal AccessSelection(ElementId only = null) { _only = only; }
            internal static bool Accept(Element element)
            {
                if (!(element is FamilyInstance) || element.Category == null) return false;
                long category = BoosterIds.Value(element.Category.Id);
                return category == (long)BuiltInCategory.OST_PipeAccessory || category == (long)BuiltInCategory.OST_PipeFitting
                    || category == (long)BuiltInCategory.OST_MechanicalEquipment || category == (long)BuiltInCategory.OST_GenericModel;
            }
            public bool AllowElement(Element element) => _only != null ? element.Id == _only : Accept(element);
            public bool AllowReference(Reference reference, XYZ point) => _only == null || reference.ElementId == _only;
        }
    }

    internal sealed class MaintenanceStartWindow : Window
    {
        internal int Scenario;
        internal MaintenanceStartWindow(IntPtr owner)
        {
            Title = UiLanguage.T("Maintenance MEP", "MEP Maintenance"); Width = 680; Height = 640; MinHeight = 450;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            new WindowInteropHelper(this).Owner = owner; BoosterTheme.Attach(this);
            var body = new StackPanel { Margin = new Thickness(26) };
            body.Children.Add(new TextBlock { Text = UiLanguage.T("Peut-on réellement intervenir ici ?", "Can the work really be carried out here?"), FontSize = 25, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = UiLanguage.T("Choisissez une intervention et l’équipement. La commande et un poste au sol sont proposés automatiquement quand la géométrie le permet.", "Choose an intervention and the equipment. The operator and a floor working position are proposed automatically when the geometry allows it."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18), FontSize = 14 });
            Add(0, "↗", UiLanguage.T("Sortir un moteur de pompe", "Remove a pump motor"), UiLanguage.T("Volume du moteur, virages et portes jusqu’à la sortie.", "Motor volume, turns and doors up to the exit."));
            Add(1, "↻", UiLanguage.T("Ouvrir / fermer une vanne manuellement", "Open / close a valve manually"), UiLanguage.T("Volant, levier ou chaîne réellement présent ; opérateur et mouvement des mains. La commande électrique n’est pas simulée.", "Handwheel, lever or chain actually present; operator and hand movement. Electric actuation is not simulated."));
            Add(3, "⇧", UiLanguage.T("Déposer un actionneur", "Remove an actuator"), UiLanguage.T("Retrait du moteur de la vanne pour maintenance ; volume, course de retrait et portée de l’opérateur.", "Removal of the valve motor for maintenance; volume, withdrawal travel and operator reach."));
            Add(2, "⇥", UiLanguage.T("Entretenir un filtre", "Service a filter"), UiLanguage.T("Retrait du tamis ou de la cartouche, place de l’opérateur et des outils.", "Removal of the screen or cartridge, space for the operator and tools."));
            body.Children.Add(new TextBlock { Text = UiLanguage.T("Aperçus temporaires : aucun équipement n’est déplacé. Les repères et les cotes de la notice doivent être confirmés avant de conclure.", "Temporary previews: no equipment is moved. Reference points and manufacturer dimensions must be confirmed before concluding."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 0) });
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            void Add(int scenario, string symbol, string title, string description)
            {
                var row = new Grid { Margin = new Thickness(4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = symbol, FontSize = 34, Foreground = Brushes.SeaGreen, VerticalAlignment = VerticalAlignment.Center });
                var text = new StackPanel(); Grid.SetColumn(text, 1);
                text.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = description, FontSize = 13, TextWrapping = TextWrapping.Wrap }); row.Children.Add(text);
                var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(12), Style = null, Background = Brushes.White, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) };
                button.Click += (_, __) => { Scenario = scenario; DialogResult = true; }; body.Children.Add(button);
            }
        }
    }

    internal sealed class MaintenanceWorkbenchState
    {
        internal MaintenanceAccessOperation Operation;
        internal XYZ CommandPoint, FootPoint;
        internal int Axis = 4;
        internal bool Confirmed, Manufacturer;
        internal bool Measured, SupportVerified;
        internal bool CorrectionApplied, RotationAssumed;
        internal string CorrectionNote = "";
        internal Transform DetectedFrame;
        internal MaintenancePostureSearch PostureSearch = MaintenancePostureSearch.Auto;
        internal MaintenanceOperatorPosture OperatorPosture = MaintenanceOperatorPosture.Standing, RejectedPosture;
        internal double BodyYawDegrees, RejectedBodyYawDegrees;
        internal XYZ RejectedFoot;
        internal string DetectionNote = "", SupportNote = "", NoOperatorReason = "";
        internal readonly List<string> FeatureWarnings = new List<string>(), PlacementWarnings = new List<string>();
        internal readonly List<string> PostureWarnings = new List<string>();
        internal readonly List<string> PlacementDiagnostics = new List<string>();
        internal readonly List<MaintenanceAccessObstacle> BlockingObstacles = new List<MaintenanceAccessObstacle>();
        internal bool WholeGestureClear, WholeGestureChecked, SearchLimited;
        internal readonly List<string> GestureUnknown = new List<string>();
        internal readonly List<MaintenanceAccessObstacle> GestureObstacles = new List<MaintenanceAccessObstacle>();
        internal readonly List<MaintenanceAccessObstacle> FixedObstacles = new List<MaintenanceAccessObstacle>();
        internal readonly List<MaintenanceFeatureCandidate> Candidates = new List<MaintenanceFeatureCandidate>();
        internal string Notice = "";
        internal readonly Dictionary<string, string> Fields = new Dictionary<string, string>();
    }

    internal sealed class MaintenanceWorkbenchWindow : Window
    {
        private static readonly string[] OperationNames = { UiLanguage.T("Manœuvre — levier manuel", "Operation — manual lever"), UiLanguage.T("Manœuvre — volant manuel", "Operation — manual handwheel"), UiLanguage.T("Manœuvre — volant avec chaîne", "Operation — chain-operated handwheel"), UiLanguage.T("Maintenance — dépose de l’actionneur", "Maintenance — actuator removal"), UiLanguage.T("Retrait du tamis / de la cartouche", "Screen / cartridge removal") };
        private readonly Document _doc;
        private readonly ScrollViewer _settings;
        private readonly FamilyInstance _source;
        private readonly Transform _sourceFrame;
        private readonly XYZ _sourceCenter;
        private readonly MaintenanceWorkbenchState _state;
        private readonly MaintenancePreview _preview;
        private readonly Dictionary<string, TextBox> _fields = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, FrameworkElement> _rows = new Dictionary<string, FrameworkElement>();
        private readonly ComboBox _operation = new ComboBox { Style = null, MinHeight = 36, FontSize = 14 };
        private readonly ComboBox _axis = new ComboBox { Style = null, MinHeight = 36, FontSize = 14, ItemsSource = new[] { "+X local", "−X local", "+Y local", "−Y local", "+Z local", "−Z local", UiLanguage.T("Axe détecté dans la famille", "Axis detected in the family") } };
        private readonly CheckBox _confirmed = new CheckBox { Content = UiLanguage.T("Repère, sens et cotes vérifiés sur l’équipement / la notice", "Reference, direction and dimensions checked on the equipment / datasheet"), Margin = new Thickness(0, 9, 0, 5) };
        private readonly CheckBox _showVolumes = new CheckBox { Content = UiLanguage.T("Afficher les volumes contrôlés", "Show checked volumes"), IsChecked = false };
        private readonly ComboBox _mode = new ComboBox {Style=null,MinHeight=34,ItemsSource=new[] {UiLanguage.T("Dans la maquette", "In the model"),UiLanguage.T("Démonstration du geste", "Gesture demonstration")}};
        private readonly TextBlock _modeNote = new TextBlock {TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,6,0,0),Foreground=Brushes.DarkOrange};
        private readonly ComboBox _candidates = new ComboBox {Style=null,MinHeight=34};
        private readonly ComboBox _posture = new ComboBox {Style=null,MinHeight=34,ItemsSource=new[] {UiLanguage.T("Automatique : debout, de profil ou accroupi", "Automatic: standing, side-on or crouching"),UiLanguage.T("Debout face à la commande", "Standing facing the operator"),UiLanguage.T("De profil — côté gauche", "Side-on — left side"),UiLanguage.T("De profil — côté droit", "Side-on — right side"),UiLanguage.T("Accroupi", "Crouching")}};
        private readonly CheckBox _showBlockedPosture = new CheckBox {Content=UiLanguage.T("Montrer le poste refusé et ses obstacles", "Show the rejected position and its obstacles"),IsChecked=true};
        private readonly Button _play = new Button {Content=UiLanguage.T("▶ Lire le geste", "▶ Play gesture"),MinHeight=36,Style=null,Width=150,HorizontalAlignment=HorizontalAlignment.Left};
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 17, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _noOperatorReason = new TextBlock { TextWrapping=TextWrapping.Wrap,FontSize=13,Margin=new Thickness(0,6,0,3),Foreground=Brushes.DarkOrange };
        private readonly Button _suggest = new Button { Style=null,MinHeight=36,Margin=new Thickness(0,0,0,6),HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(8) };
        private readonly TextBlock _details = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly TextBlock _help = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
        private readonly TextBlock _hypothesisSummary = new TextBlock { TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DarkSlateGray,FontSize=11,Margin=new Thickness(0,4,0,0) };
        private readonly TextBlock _detected = new TextBlock { TextWrapping=TextWrapping.Wrap, FontSize=12, Margin=new Thickness(0,0,0,8) };
        private readonly TextBlock _memoryNote = new TextBlock { TextWrapping=TextWrapping.Wrap, FontSize=12, Margin=new Thickness(0,6,0,4), Foreground=Brushes.DarkSlateGray };
        private readonly Button _remember = new Button { Content=UiLanguage.T("Mémoriser ces réglages pour les équipements compatibles", "Save these settings for compatible equipment"), Style=null, MinHeight=36, Margin=new Thickness(0,6,0,4), HorizontalContentAlignment=HorizontalAlignment.Left, Padding=new Thickness(8) };
        private readonly Button _forget = new Button { Content=UiLanguage.T("Oublier la correction mémorisée pour cet équipement", "Forget the saved correction for this equipment"), Style=null, MinHeight=32, Margin=new Thickness(0,0,0,4), HorizontalContentAlignment=HorizontalAlignment.Left, Padding=new Thickness(8) };
        private readonly Button _handleDirection = new Button { Content=UiLanguage.T("Pointer l’extrémité de la poignée…", "Pick the end of the handle…"), Style=null, MinHeight=36, Margin=new Thickness(0,0,0,6), HorizontalContentAlignment=HorizontalAlignment.Left, Padding=new Thickness(8) };
        private readonly Slider _phase = new Slider { Minimum = 0, Maximum = 100, Margin = new Thickness(12, 4, 12, 4) };
        private readonly Stopwatch _animationClock = new Stopwatch();
        private readonly DispatcherTimer _frameTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        private bool _playing, _advancingPhase;
        private bool _playbackDriverChosen, _preferTimerPlayback, _usingTimerPlayback;
        private double _renderProbeStart = -1, _measuredRenderFps;
        private int _renderProbeIntervals;
        private double _animationStartPhase;
        private TimeSpan _lastRenderTime = TimeSpan.MinValue;
        private const double AnimationDurationSeconds = 3.3;
        private readonly DispatcherTimer _inputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        private readonly Button _analyze = new Button { Content = UiLanguage.T("Vérifier l’intervention", "Check the intervention"), MinHeight = 42 };
        private readonly Button _cancel = new Button { Content = UiLanguage.T("Annuler le contrôle", "Cancel the check"), Visibility = System.Windows.Visibility.Collapsed };
        private readonly TextBox _notice = new TextBox { Style = null, MinHeight = 34, Padding = new Thickness(6), ToolTip = UiLanguage.T("Référence produit / notice et origine des cotes", "Product / datasheet reference and dimension origin") };
        private readonly ComboBox _provenance = new ComboBox { Style = null, MinHeight = 34, ItemsSource = new[] { UiLanguage.T("Hypothèses réglables", "Adjustable assumptions"), UiLanguage.T("Cotes reportées d’une notice constructeur", "Dimensions taken from a manufacturer datasheet"), UiLanguage.T("Cotes mesurées dans la famille", "Dimensions measured in the family") } };
        private MaintenanceAccessScene _lastScene;
        private MaintenanceAccessProfile _poseProfile;
        private MaintenanceAccessGeometry.PreparedMotion _preparedMotion;
        private MaintenanceAccessScene _controlScene;
        private MaintenanceAccessReport _report;
        private bool _ready, _running, _cancelRequested;
        internal string PickRequest { get; private set; }
        internal MaintenanceWorkbenchWindow(Document doc, FamilyInstance source, MaintenanceWorkbenchState state, IntPtr owner)
        {
            _doc = doc; _source = source; _state = state;
            _sourceFrame=source.GetTransform();
            using(var bounds=source.get_BoundingBox(null))_sourceCenter=bounds==null?_sourceFrame.Origin:(bounds.Min+bounds.Max)/2;
            Title = UiLanguage.T("Maintenance MEP · ", "MEP Maintenance · ")+MaintenanceBuildIdentity.Display; Width = 1180; Height = 850; MinWidth = 940; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            new WindowInteropHelper(this).Owner = owner; BoosterTheme.Attach(this);
            var layout = new Grid { Background = Brushes.WhiteSmoke };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
            var settings = new StackPanel { Margin = new Thickness(18) };
            _settings = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; layout.Children.Add(_settings);
            var right = new DockPanel { Margin = new Thickness(0, 16, 16, 16) }; Grid.SetColumn(right, 1); layout.Children.Add(right);
            var card = new StackPanel { Margin = new Thickness(12) }; var result = new Border { Child = card, Background = Brushes.White, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(4) };
            DockPanel.SetDock(result, Dock.Top); right.Children.Add(result); card.Children.Add(_status);
            card.Children.Add(_noOperatorReason);card.Children.Add(_mode);card.Children.Add(_modeNote);
            _mode.SelectedIndex=0;
            card.Children.Add(_cancel);
            var details = new Expander { Header = UiLanguage.T("Détails, hypothèses et éléments à confirmer", "Details, assumptions and items to confirm"), Content = new ScrollViewer { Content = _details, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new Thickness(0, 8, 0, 0) }; card.Children.Add(details);
            var actions = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); right.Children.Add(actions);
            actions.Children.Add(_play); actions.Children.Add(_phase); actions.Children.Add(_showVolumes);actions.Children.Add(_showBlockedPosture);
            actions.Children.Add(new TextBlock { Text = UiLanguage.T("Glisser : tourner la vue · molette : zoomer · double-clic : recadrer\nBleu : équipement · vert : opérateur / mouvement · flèche violette : axe choisi\nRouge : collision · gris : environnement", "Drag: rotate view · wheel: zoom · double-click: reframe\nBlue: equipment · green: operator / movement · purple arrow: selected axis\nRed: collision · grey: surroundings"), Foreground = Brushes.DimGray, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
            _preview = new MaintenancePreview(doc, source, CapturePoints(), state.BlockingObstacles.Concat(state.FixedObstacles).Concat(state.GestureObstacles)); right.Children.Add(_preview); Content = layout;
            settings.Children.Add(new TextBlock { Text = UiLanguage.T("Préparer l’intervention", "Prepare the intervention"), FontSize = 22, FontWeight = FontWeights.SemiBold });
            settings.Children.Add(new TextBlock { Text = source.Symbol.Family.Name+" · "+source.Name + " · " + BoosterIds.Value(source.Id), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 15) });
            Heading(settings, UiLanguage.T("1  Quel geste faut-il réaliser ?", "1  Which gesture must be performed?"));
            _operation.ItemsSource = OperationNames; _operation.SelectedIndex = (int)state.Operation; settings.Children.Add(_operation);
            settings.Children.Add(_help);settings.Children.Add(_hypothesisSummary);
            Heading(settings, UiLanguage.T("2  Vérifier la proposition automatique", "2  Check the automatic proposal"));
            settings.Children.Add(_detected);
            settings.Children.Add(new TextBlock {Text=UiLanguage.T("Posture à essayer", "Posture to try"),Margin=new Thickness(0,2,0,4)});
            _posture.SelectedIndex=(int)state.PostureSearch;settings.Children.Add(_posture);
            _suggest.Click+=(_,__)=>{PickRequest=_state.CommandPoint==null?"auto":"position";Close();};settings.Children.Add(_suggest);
            if(state.CommandPoint==null&&state.Candidates.Count>0)
            {
                _candidates.ItemsSource=state.Candidates.Select((c,i)=>UiLanguage.T("Repère ", "Marker ")+(i+1)+UiLanguage.T(" · rayon ", " · radius ")+(c.Radius*304.8).ToString("0")+" mm").ToArray();
                _candidates.SelectedIndex=0;settings.Children.Add(_candidates);
                var use=new Button {Content=UiLanguage.T("Utiliser le repère surligné…", "Use the highlighted marker…"),Style=null,MinHeight=36,Margin=new Thickness(0,5,0,5)};
                use.Click+=(_,__)=>UseCandidate();settings.Children.Add(use);
            }
            var manual=new StackPanel {Margin=new Thickness(0,8,0,8)};
            Pick(manual, state.CommandPoint == null ? UiLanguage.T("Pointer la commande / le capot…", "Pick the operator / cover…") : UiLanguage.T("Corriger le centre de commande…", "Correct the operator center…"), "command");
            Pick(manual, state.FootPoint == null ? UiLanguage.T("Choisir un sol praticable…", "Choose a walkable floor…") : UiLanguage.T("Corriger le poste au sol…", "Correct the floor position…"), "operator");
            if(state.CommandPoint!=null)Pick(manual,UiLanguage.T("Relancer la détection des repères…", "Rerun marker detection…"),"auto");
            Pick(manual,UiLanguage.T("Définir un axe incliné par deux points…", "Define an inclined axis from two points…"),"axis");
            _handleDirection.Click+=(_,__)=>{PickRequest="handle";Close();};manual.Children.Add(_handleDirection);
            manual.Children.Add(new TextBlock { Text = UiLanguage.T("Axe de rotation ou sens de retrait", "Rotation axis or removal direction"), Margin = new Thickness(0, 9, 0, 5) });
            _axis.SelectedIndex = state.Axis; manual.Children.Add(_axis);
            var reverse=new Button {Content=UiLanguage.T("↔ Inverser le sens", "↔ Reverse direction"),Style=null,MinHeight=32,Margin=new Thickness(0,5,0,0)};
            reverse.Click+=(_,__)=>
            {
                if(_axis.SelectedIndex==6&&_state.DetectedFrame!=null)
                {
                    var previous=_state.DetectedFrame;var flipped=Transform.Identity;
                    flipped.Origin=previous.Origin;flipped.BasisX=previous.BasisX;flipped.BasisY=-previous.BasisY;flipped.BasisZ=-previous.BasisZ;
                    _state.DetectedFrame=flipped;InputChanged();
                }
                else _axis.SelectedIndex=_axis.SelectedIndex<6?(_axis.SelectedIndex^1):4;
            };manual.Children.Add(reverse);
            _remember.Content=new TextBlock {Text=UiLanguage.T("Mémoriser ces réglages pour les équipements compatibles", "Save these settings for compatible equipment"),TextWrapping=TextWrapping.Wrap};
            _forget.Content=new TextBlock {Text=UiLanguage.T("Oublier la correction mémorisée pour cet équipement", "Forget the saved correction for this equipment"),TextWrapping=TextWrapping.Wrap};
            manual.Children.Add(_remember);manual.Children.Add(_forget);manual.Children.Add(_memoryNote);
            manual.Children.Add(new TextBlock {Text=UiLanguage.T("Le geste est réutilisé si la géométrie correspond. Sol, posture et obstacles sont recalculés à chaque intervention.", "The gesture is reused if the geometry matches. Floor, posture and obstacles are recalculated for each intervention."),TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brushes.DimGray});
            settings.Children.Add(new Expander {Header=UiLanguage.T("Corriger un repère ou le poste…", "Correct a marker or the position…"),Content=manual,Margin=new Thickness(0,8,0,0)});
            var dimensions=new StackPanel {Margin=new Thickness(0,7,0,7)};
            Field(dimensions, "Radius", UiLanguage.T("Rayon du volant / longueur du levier", "Handwheel radius / lever length"), 200);
            Field(dimensions, "Angle", UiLanguage.T("Angle de manœuvre (°)", "Operating angle (°)"), state.Operation == MaintenanceAccessOperation.LeverValve ? 90 : 360);
            Field(dimensions, "ChainDrop", UiLanguage.T("Descente de chaîne depuis la roue", "Chain drop from the wheel"), 2000);
            Field(dimensions, "ChainSpacing", UiLanguage.T("Écartement des deux brins", "Spacing between the two chain strands"), 400);
            Field(dimensions, "RX", UiLanguage.T("Largeur de la pièce à retirer", "Width of the part to remove"), 250);
            Field(dimensions, "RY", UiLanguage.T("Profondeur de la pièce à retirer", "Depth of the part to remove"), 250);
            Field(dimensions, "RZ", UiLanguage.T("Longueur de la pièce à retirer", "Length of the part to remove"), 500);
            Field(dimensions, "Travel", UiLanguage.T("Course de retrait depuis le capot", "Withdrawal travel from the cover"), 700);
            dimensions.Children.Add(_provenance); _provenance.SelectedIndex = state.Manufacturer ? 1 : state.Measured ? 2 : 0;
            dimensions.Children.Add(new TextBlock { Text = UiLanguage.T("Référence de la notice / origine des cotes", "Datasheet reference / dimension origin"), Margin = new Thickness(0, 8, 0, 3) });
            _notice.Text = state.Notice; dimensions.Children.Add(_notice);
            settings.Children.Add(new Expander {Header=UiLanguage.T("Cotes du geste et origine des valeurs…", "Gesture dimensions and value sources…"),Content=dimensions,Margin=new Thickness(0,10,0,0)});
            Heading(settings, UiLanguage.T("3  Vérifier dans la maquette", "3  Check in the model"));
            _confirmed.Content = new TextBlock { Text = UiLanguage.T("Repère, sens et cotes vérifiés sur l’équipement / la notice", "Reference, direction and dimensions checked on the equipment / datasheet"), TextWrapping = TextWrapping.Wrap };
            _confirmed.IsChecked = state.Confirmed; settings.Children.Add(_confirmed);
            _analyze.Style = (Style)FindResource("PrimaryButton"); settings.Children.Add(_analyze);
            var advanced = new StackPanel { Margin = new Thickness(0, 8, 0, 5) };
            Field(advanced, "Height", UiLanguage.T("Taille de l’opérateur", "Operator height"), 1700); Field(advanced, "Width", UiLanguage.T("Largeur de confort (indicative)", "Comfort width (indicative)"), 650); Field(advanced, "Depth", UiLanguage.T("Profondeur de confort (indicative)", "Comfort depth (indicative)"), 650);
            Field(advanced, "Grip", UiLanguage.T("Dégagement des mains / outils", "Hand / tool clearance"), 70); Field(advanced, "Margin", UiLanguage.T("Marge supplémentaire", "Additional margin"), 10);
            Field(advanced, "Thickness", UiLanguage.T("Épaisseur de la couronne / poignée", "Rim / handle thickness"), 35);
            Field(advanced, "InitialAngle", UiLanguage.T("Angle initial du levier / volant (°)", "Initial lever / handwheel angle (°)"), 0);
            Field(advanced, "Pull", UiLanguage.T("Course de traction de la chaîne", "Chain pull travel"), 350);
            Field(advanced, "WorkHeight", UiLanguage.T("Hauteur de prise de chaîne / sol", "Chain grip height / floor"), 1200);
            Field(advanced, "OffsetX", UiLanguage.T("Décalage pièce X / capot", "Part offset X / cover"), 0); Field(advanced, "OffsetY", UiLanguage.T("Décalage pièce Y / capot", "Part offset Y / cover"), 0); Field(advanced, "OffsetZ", UiLanguage.T("Décalage pièce Z / capot", "Part offset Z / cover"), -250);
            advanced.Children.Add(new TextBlock { Text = "Longueurs en mm. La largeur et la profondeur de confort tracent un repère indicatif ; elles ne bloquent pas le corps et ne sont pas des minima réglementaires. Le mannequin articulé reste un gabarit réglable : effort, durée et ergonomie doivent être évalués séparément.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            settings.Children.Add(new Expander { Header = UiLanguage.T("Réglages du gabarit et de la pièce", "Template and part settings"), Content = advanced, Margin = new Thickness(0, 12, 0, 0) });
            settings.Children.Add(new Expander { Header = UiLanguage.T("Références métier et limites", "Industry references and limitations"), Content = new TextBlock { Text = MaintenanceAccessRules.References, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, Margin = new Thickness(0, 12, 0, 0) });
            var close = new Button { Content = "Fermer", IsCancel = true, Margin = new Thickness(0, 14, 0, 0) }; close.Click += (_, __) => Close(); settings.Children.Add(close);
            _operation.SelectionChanged += (_, __) =>
            {
                UpdateOperation();if(!_ready)return;
                _state.Operation=(MaintenanceAccessOperation)_operation.SelectedIndex;
                _state.CommandPoint=null;_state.FootPoint=null;_state.DetectedFrame=null;_state.SupportVerified=false;_state.Measured=false;_state.Manufacturer=false;
                _state.DetectionNote=UiLanguage.T("Intervention modifiée : relancez la détection pour proposer les nouveaux repères.", "Intervention changed: rerun detection to propose new markers.");_state.SupportNote="";
                _state.NoOperatorReason=UiLanguage.T("Intervention modifiée : détectez la commande du geste choisi pour rechercher un personnage.", "Intervention changed: detect the operator for the selected gesture to search for a figure.");
                _state.CorrectionApplied=false;_state.CorrectionNote="";_state.RotationAssumed=_operation.SelectedIndex==0;_memoryNote.Text="";
                _state.FeatureWarnings.Clear();_state.FeatureWarnings.Add(UiLanguage.T("Les repères précédents ne sont pas utilisés pour cette nouvelle intervention.", "Previous markers are not used for this new intervention."));_state.PlacementWarnings.Clear();_state.Candidates.Clear();_candidates.ItemsSource=null;
                _state.PostureWarnings.Clear();_state.PlacementDiagnostics.Clear();_state.BlockingObstacles.Clear();_state.RejectedFoot=null;
                MaintenanceAutoconfiguration.ClearGestureState(_state);
                _provenance.SelectedIndex=0;_notice.Clear();_axis.SelectedIndex=4;
                foreach(var pair in new Dictionary<string,double>{{"Radius",200},{"Thickness",35},{"WorkHeight",1200},{"Angle",_operation.SelectedIndex==0?90:360},{"InitialAngle",0},{"ChainDrop",2000},{"ChainSpacing",400},{"RX",250},{"RY",250},{"RZ",500},{"Travel",700},{"OffsetX",0},{"OffsetY",0},{"OffsetZ",-250}})
                    _fields[pair.Key].Text=pair.Value.ToString("0.#",CultureInfo.CurrentCulture);
                InputChanged();
                _mode.SelectedIndex=0;
            };
            _axis.SelectionChanged += (_, __) => InputChanged();
            _remember.Click+=(_,__)=>RememberCorrection();
            _forget.Click+=(_,__)=>ForgetCorrection();
            foreach (var input in _fields.Values) input.TextChanged += (_, __) => InputChanged();
            _confirmed.Checked += (_, __) => Invalidate(); _confirmed.Unchecked += (_, __) => Invalidate();
            _provenance.SelectionChanged += (_, __) => InputChanged(); _notice.TextChanged += (_, __) => InputChanged();
            _showVolumes.Checked += (_, __) => {StopPlayback();ResetPlaybackSelection();Draw();};
            _showVolumes.Unchecked += (_, __) => {StopPlayback();ResetPlaybackSelection();Draw();};
            _showBlockedPosture.Checked+=(_,__)=>RefreshBlockers();_showBlockedPosture.Unchecked+=(_,__)=>RefreshBlockers();
            _posture.SelectionChanged+=(_,__)=>
            {
                if(!_ready||_running)return;
                _state.PostureSearch=(MaintenancePostureSearch)_posture.SelectedIndex;
                _state.FootPoint=null;_state.SupportVerified=false;_confirmed.IsChecked=false;
                PickRequest="position";Close();
            };
            _phase.ValueChanged += (_, __) =>
            {
                if(_playing&&!_advancingPhase)
                {_animationStartPhase=_phase.Value;_animationClock.Restart();_renderProbeStart=-1;_renderProbeIntervals=0;}
                Draw();
            }; _analyze.Click += (_, __) => Analyze(); _cancel.Click += (_, __) => _cancelRequested = true;
            _candidates.SelectionChanged+=(_,__)=>{if(!_ready)return;_mode.SelectedIndex=0;Draw();};
            _mode.SelectionChanged+=(_,__)=>{if(!_ready||_running)return;Invalidate();_preview.Fit();};
            _play.Click += (_, __) =>
            {
                if(_playing){StopPlayback();return;}
                if(_phase.Value>=100)_phase.Value=0;
                _animationStartPhase=_phase.Value;_animationClock.Restart();_playing=true;
                _preview.SetMotionActive(true);
                _lastRenderTime=TimeSpan.MinValue;_renderProbeStart=-1;_renderProbeIntervals=0;_play.Content="❚❚ Pause";
                if(_playbackDriverChosen&&_preferTimerPlayback)StartTimerPlayback();
                else CompositionTarget.Rendering+=AdvancePlayback;
            };
            _frameTimer.Tick+=(_,__)=>AdvancePlayback(this,EventArgs.Empty);
            Closing += (_, e) => { if (_running) { _cancelRequested = true; e.Cancel = true; } };
            _inputTimer.Tick += (_,__)=>{_inputTimer.Stop();Invalidate();};
            Closed += (_, __) => { StopPlayback(); _inputTimer.Stop(); Save(); };
            _ready = true; UpdateOperation(); Invalidate();
        }
        private void AdvancePlayback(object sender,EventArgs args)
        {
            if(!_playing||!IsVisible)return;
            if(args is RenderingEventArgs rendering)
            {
                if(rendering.RenderingTime==_lastRenderTime)return;
                _lastRenderTime=rendering.RenderingTime;
            }
            _advancingPhase=true;
            try{_phase.Value=Math.Min(100,_animationStartPhase+100*_animationClock.Elapsed.TotalSeconds/AnimationDurationSeconds);}
            finally{_advancingPhase=false;}
            if(_phase.Value>=100){StopPlayback();return;}
            if(!_preferTimerPlayback&&args is RenderingEventArgs)
            {
                double now=_animationClock.Elapsed.TotalSeconds;
                if(_renderProbeStart<0)_renderProbeStart=now;
                else _renderProbeIntervals++;
                double elapsed=now-_renderProbeStart;
                if(elapsed>=.6&&_renderProbeIntervals>=5)
                {
                    _measuredRenderFps=_renderProbeIntervals/elapsed;
                    _playbackDriverChosen=true;_preferTimerPlayback=_measuredRenderFps<27;
                    if(_preferTimerPlayback)StartTimerPlayback();
                    else {_renderProbeStart=now;_renderProbeIntervals=0;}
                }
            }
        }
        private void StartTimerPlayback()
        {
            if(_usingTimerPlayback)return;
            CompositionTarget.Rendering-=AdvancePlayback;
            _usingTimerPlayback=true;_frameTimer.Start();
        }
        private void ResetPlaybackSelection()
        {
            _playbackDriverChosen=false;_preferTimerPlayback=false;_measuredRenderFps=0;
            _renderProbeStart=-1;_renderProbeIntervals=0;
        }
        private void StopPlayback()
        {
            _playing=false;_frameTimer.Stop();_usingTimerPlayback=false;_animationClock.Stop();CompositionTarget.Rendering-=AdvancePlayback;
            _preview?.SetMotionActive(false);
            _play.Content=UiLanguage.T("▶ Lire le geste", "▶ Play gesture");
        }
        private static void Heading(StackPanel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 14, 0, 8) });
        private void Pick(StackPanel panel, string text, string kind)
        {
            var button = new Button { Content = text, Style = null, MinHeight = 36, Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8) };
            button.Click += (_, __) => { PickRequest = kind; Close(); }; panel.Children.Add(button);
        }
        private void Field(StackPanel panel, string key, string title, double value)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            row.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var input = new TextBox { Style = null, Text = _state.Fields.TryGetValue(key, out string saved) ? saved : value.ToString("0.#", CultureInfo.CurrentCulture), MinHeight = 36, FontSize = 15, Padding = new Thickness(7, 5, 7, 5), VerticalContentAlignment = VerticalAlignment.Center, Foreground = Brushes.Black, Background = Brushes.White, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1) };
            Grid.SetColumn(input, 1); row.Children.Add(input); panel.Children.Add(row); _fields[key] = input; _rows[key] = row;
        }
        private void UpdateOperation()
        {
            var op = (MaintenanceAccessOperation)_operation.SelectedIndex;
            bool removal = op == MaintenanceAccessOperation.Filter || op == MaintenanceAccessOperation.Actuator;
            _handleDirection.Visibility=op==MaintenanceAccessOperation.LeverValve?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
            foreach (string key in new[] { "Radius", "Angle", "InitialAngle" }) _rows[key].Visibility = removal ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            foreach (string key in new[] { "ChainDrop", "ChainSpacing", "Pull", "WorkHeight" }) _rows[key].Visibility = op == MaintenanceAccessOperation.ChainwheelValve ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            foreach (string key in new[] { "RX", "RY", "RZ", "Travel", "OffsetX", "OffsetY", "OffsetZ" }) _rows[key].Visibility = removal ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            _help.Text = op == MaintenanceAccessOperation.ChainwheelValve ? UiLanguage.T("Deux brins sous la roue ; le mannequin tire la chaîne à sa hauteur de prise. Confirmez la descente réelle.", "Two strands below the wheel; the figure pulls the chain at grip height. Confirm the actual drop.")
                : op == MaintenanceAccessOperation.Filter ? UiLanguage.T("Vérifiez le capot et le sens de retrait proposés. Reportez la course constructeur et le volume de la pièce, puis regardez son retrait.", "Check the proposed cover and removal direction. Enter the manufacturer travel and part volume, then watch its removal.")
                : op == MaintenanceAccessOperation.Actuator ? UiLanguage.T("Dépose de l’actionneur : volume et course de retrait. La commande électrique et le secours manuel ne sont pas simulés.", "Actuator removal: volume and withdrawal travel. Electric actuation and manual override are not simulated.")
                : UiLanguage.T("Manœuvre manuelle : utilisez le volant ou le levier réellement présent, y compris un secours manuel identifié. La présence d’un moteur ne déclenche pas sa dépose. La commande électrique n’est pas simulée.", "Manual operation: use the handwheel or lever actually present, including an identified manual override. The presence of a motor does not trigger its removal. Electric actuation is not simulated.");
        }
        private double Number(string key)
        {
            var text = _fields[key].Text.Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
                && !double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new ArgumentException(UiLanguage.T("Valeur incorrecte : ", "Invalid value: ") + key);
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(UiLanguage.T("Valeur non finie : ", "Non-finite value: ") + key);
            return value;
        }
        private MaintenanceAccessProfile Read()
            => MaintenanceAutoconfiguration.CreateProfile(_sourceFrame,_sourceCenter,_state,(MaintenanceAccessOperation)_operation.SelectedIndex,_axis.SelectedIndex,Number);
        private XYZ[] CapturePoints()
        {
            if(_state.CommandPoint==null)return Array.Empty<XYZ>();
            try
            {
                var profile=MaintenanceAutoconfiguration.ReadSavedProfile(_source,_state);
                if(profile.OperatorOrigin==null&&_state.RejectedFoot!=null)
                {profile.OperatorOrigin=_state.RejectedFoot;profile.OperatorPosture=_state.RejectedPosture;profile.BodyYawDegrees=_state.RejectedBodyYawDegrees;}
                bool body=profile.OperatorOrigin!=null;
                return MaintenanceAccessGeometry.Build(profile,0).CollisionBoxes.Where(b=>body||!b.DependsOnOperator)
                    .SelectMany(b=>b.BoundsCorners()).ToArray();
            }
            catch(ArgumentException){return Array.Empty<XYZ>();}
        }
        private bool Demonstration => _mode.SelectedIndex==1;
        private void UseCandidate()
        {
            if(_candidates.SelectedIndex<0||_candidates.SelectedIndex>=_state.Candidates.Count)return;
            var candidate=_state.Candidates[_candidates.SelectedIndex];
            MaintenanceAutoconfiguration.ApplyCandidate(_state,candidate);_axis.SelectedIndex=6;
            _fields["Radius"].Text=((candidate.CenterlineRadius>0?candidate.CenterlineRadius:candidate.Radius)*304.8).ToString("0.###",CultureInfo.CurrentCulture);
            if(candidate.Frame!=null)_fields["InitialAngle"].Text="0";
            if(candidate.HandleThickness>0)_fields["Thickness"].Text=(candidate.HandleThickness*304.8).ToString("0.###",CultureInfo.CurrentCulture);
            _provenance.SelectedIndex=0;_confirmed.IsChecked=false;PickRequest="position";Close();
        }
        private void Save()
        {
            _state.Operation = (MaintenanceAccessOperation)_operation.SelectedIndex; _state.Axis = _axis.SelectedIndex;
            _state.Confirmed = _confirmed.IsChecked == true; _state.Manufacturer = _provenance.SelectedIndex == 1; _state.Notice = _notice.Text;
            _state.Measured = _provenance.SelectedIndex == 2;
            _state.PostureSearch=(MaintenancePostureSearch)_posture.SelectedIndex;
            foreach (var field in _fields) _state.Fields[field.Key] = field.Value.Text;
        }
        private void RememberCorrection()
        {
            if(Demonstration||_running)return;
            try
            {
                Read().Validate();Save();
                bool saved=MaintenanceCorrectionMemory.Save(_doc,_source,_state,out string note);
                _state.CorrectionNote=note;_state.CorrectionApplied=false;
                _memoryNote.Text=note;_memoryNote.Foreground=saved?Brushes.DarkSlateGray:Brushes.DarkOrange;
            }
            catch(ArgumentException error){_memoryNote.Text=UiLanguage.T("Réglages non mémorisés : ", "Settings not saved: ")+error.Message;_memoryNote.Foreground=Brushes.DarkOrange;}
        }
        private void ForgetCorrection()
        {
            if(_running)return;
            MaintenanceCorrectionMemory.Remove(_doc,_source,(MaintenanceAccessOperation)_operation.SelectedIndex,out string note);
            _state.CorrectionApplied=false;_state.CorrectionNote=note;_memoryNote.Text=note;
        }
        private string NoOperatorReason()
        {
            if(!string.IsNullOrWhiteSpace(_state.NoOperatorReason))return _state.NoOperatorReason;
            return _state.CommandPoint==null?UiLanguage.T("Commande du geste non identifiée : détectez ou précisez le volant, le levier, la chaîne ou le point de dépose réel.", "Gesture operator not identified: detect or specify the actual handwheel, lever, chain or removal point.")
                :_state.PlacementWarnings.FirstOrDefault()??UiLanguage.T("Aucun poste n’est actuellement contrôlé : lancez la recherche pour tout le geste.", "No position is currently checked: run the search for the whole gesture.");
        }
        private void Invalidate()
        {
            if (!_ready || _running) return;
            StopPlayback(); ResetPlaybackSelection(); _report = null;
            _controlScene = null;
            _poseProfile = null; _preparedMotion=null;
            _status.Foreground = Brushes.DarkOrange;
            _suggest.Content=_state.CommandPoint==null?UiLanguage.T("Détecter la commande et proposer le poste…", "Detect the operator and propose a position…"):UiLanguage.T("Rechercher un poste pour tout le geste…", "Search for a position for the whole gesture…");
            _detected.Text=(_state.CommandPoint==null?UiLanguage.T("Commande à préciser.", "Operator to be specified."):_state.CorrectionApplied?UiLanguage.T("Repère issu d’une correction mémorisée.", "Marker from a saved correction."):_state.DetectedFrame==null?UiLanguage.T("Centre renseigné · axe à vérifier.", "Center set · axis to check."):UiLanguage.T("Centre et axe proposés depuis la famille.", "Center and axis proposed from the family."))
                +"\n"+(_state.FootPoint==null?NoOperatorReason():(_state.SupportVerified?UiLanguage.T("Poste proposé : ", "Proposed position: "):UiLanguage.T("Poste provisoire : ", "Provisional position: "))+_state.SupportNote.Split('\n')[0]);
            if(_state.FootPoint!=null)_detected.Text+="\n"+PostureLabel(_state.OperatorPosture,_state.BodyYawDegrees);
            if(_state.CorrectionApplied)_detected.Text+=UiLanguage.T("\nCorrection mémorisée réutilisée · implantation recontrôlée ici.", "\nSaved correction reused · placement rechecked here.");
            _memoryNote.Text=_state.CorrectionNote;
            _remember.IsEnabled=!Demonstration&&_state.CommandPoint!=null;
            _remember.ToolTip=Demonstration?UiLanguage.T("La démonstration ne mémorise aucun repère réel.", "The demonstration does not save any real marker."):UiLanguage.T("Enregistre le repère et les cotes du geste ; aucune posture ni validation de collision n’est enregistrée.", "Saves the gesture marker and dimensions; no posture or collision validation is saved.");
            _forget.IsEnabled=!Demonstration;
            _status.Text = Demonstration?UiLanguage.T("Démonstration du geste · implantation réelle à vérifier.", "Gesture demonstration · actual placement to be checked.")
                : _state.CommandPoint == null ? UiLanguage.T("Commande du geste non identifiée · aucun personnage placé.", "Gesture operator not identified · no figure placed.")
                : _state.FootPoint == null ? UiLanguage.T("Aucun personnage proposé pour le geste complet.", "No figure proposed for the complete gesture.")
                : _state.FixedObstacles.Count>0 ? UiLanguage.T("Poste trouvé · le mécanisme ou le retrait rencontre un obstacle.", "Position found · the mechanism or removal hits an obstacle.")
                : _state.GestureObstacles.Count>0 ? UiLanguage.T("Poste proposé · un obstacle subsiste sur le geste.", "Position proposed · an obstacle remains along the gesture.")
                : _state.SearchLimited ? UiLanguage.T("Recherche limitée · le geste complet reste à vérifier.", "Limited search · the complete gesture remains to be checked.")
                : _state.WholeGestureClear&&!_state.SupportVerified ? UiLanguage.T("Geste complet sans conflit détecté · proposition provisoire en ambre.", "Complete gesture with no conflict detected · provisional proposal in amber.")
                : !_state.SupportVerified?UiLanguage.T("Poste provisoire en ambre · géométrie ou réglages à confirmer.", "Provisional position in amber · geometry or settings to confirm.")
                : _state.WholeGestureClear ? UiLanguage.T("Poste proposé · geste complet sans conflit détecté avec ces réglages.", "Position proposed · complete gesture with no conflict detected with these settings.")
                : UiLanguage.T("Proposition prête · contrôlez le repère et le poste, puis vérifiez l’intervention.", "Proposal ready · review the marker and position, then check the intervention.");
            _noOperatorReason.Text=!Demonstration&&_state.FootPoint==null?NoOperatorReason():"";
            _noOperatorReason.Visibility=string.IsNullOrEmpty(_noOperatorReason.Text)?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible;
            _details.Text = _state.DetectionNote+"\n"+_state.SupportNote+"\n"+string.Join("\n",_state.FeatureWarnings.Concat(_state.PlacementWarnings).Concat(_state.PostureWarnings).Concat(_state.PlacementDiagnostics).Concat(_state.GestureUnknown))
                +"\n"+string.Join("\n",_state.FixedObstacles.Concat(_state.GestureObstacles).Take(14).Select(o=>"• "+o.VolumeLabel+" : "+o.Label))
                + UiLanguage.T("\nRepères et cotes à confirmer avant de conclure. ", "\nMarkers and dimensions to confirm before concluding. ") + MaintenanceAccessRules.Scope + "\n" + _preview.CaptureNote;
            _analyze.IsEnabled = !Demonstration&&_state.CommandPoint != null && _state.FootPoint != null;
            _analyze.ToolTip=Demonstration?UiLanguage.T("Passez à « Dans la maquette » pour contrôler le vrai poste.", "Switch to “In the model” to check the actual position."):_state.FootPoint==null?NoOperatorReason():UiLanguage.T("Contrôle du poste, des bras et du mouvement contre la maquette.", "Checks the position, arms and movement against the model.");
            UpdateBlockedElements();Draw();
        }
        private void InputChanged()
        {
            if (!_ready) return;
            if(_state.CorrectionApplied)
            {_state.CorrectionApplied=false;_state.CorrectionNote=UiLanguage.T("Réglages modifiés depuis la correction mémorisée. Mémorisez-les à nouveau pour les réutiliser.", "Settings changed since the saved correction. Save them again to reuse them.");}
            _confirmed.IsChecked = false;
            _state.SupportVerified = false;
            _state.NoOperatorReason=UiLanguage.T("Réglages modifiés : relancez la recherche d’un poste pour tout le geste.", "Settings changed: rerun the position search for the whole gesture.");
            _state.PlacementWarnings.Clear();_state.SupportNote=UiLanguage.T("Poste à recontrôler après modification des réglages.", "Position to be rechecked after settings changes.");
            _state.RejectedFoot=null;_state.BlockingObstacles.Clear();_state.PostureWarnings.Clear();_state.PlacementDiagnostics.Clear();
            MaintenanceAutoconfiguration.ClearGestureState(_state);
            _preview.SetBlockedElements(Array.Empty<MaintenanceAccessObstacle>());
            StopPlayback(); ResetPlaybackSelection(); _report=null; _controlScene=null; _poseProfile=null;_preparedMotion=null;
            _status.Text=UiLanguage.T("Réglages modifiés · contrôle à relancer.", "Settings changed · check to be rerun.");
            _status.Foreground=Brushes.DarkOrange;
            _inputTimer.Stop(); _inputTimer.Start();
        }
        private void Draw()
        {
            if (!_ready) return;
            try
            {
                bool demo=Demonstration;_preview.SetDemonstration(demo);
                bool rejected=!demo&&_state.FootPoint==null&&_state.RejectedFoot!=null&&_showBlockedPosture.IsChecked==true;
                _showBlockedPosture.IsEnabled=!demo&&_state.RejectedFoot!=null;
                _modeNote.Text=demo?UiLanguage.T("DÉMONSTRATION : sol et personnage illustratifs. Les cotes non mesurées sont provisoires. Aucun poste de la maquette n’est validé.", "DEMONSTRATION: illustrative floor and figure. Unmeasured dimensions are provisional. No position in the model is validated.")
                    :_state.CommandPoint==null?UiLanguage.T("La démonstration reste disponible ; elle ne valide aucune commande ni posture dans la maquette.", "The demonstration remains available; it does not validate any operator or posture in the model.")
                    :_state.FootPoint==null?UiLanguage.T("Le mécanisme reste animé ; le personnage illustratif est disponible en démonstration.", "The mechanism remains animated; the illustrative figure is available in demonstration mode.")
                    :_state.FixedObstacles.Count>0?UiLanguage.T("POSTE EN AMBRE : le corps tient, mais le mécanisme ou la pièce retirée rencontre les éléments rouges. Changer la posture ne résout pas ce conflit avec les cotes actuelles.", "POSITION IN AMBER: the body fits, but the mechanism or removed part hits the red elements. Changing the posture does not resolve this conflict with the current dimensions.")
                    :_state.GestureObstacles.Count>0?UiLanguage.T("POSTE EN AMBRE : un conflit de bras ou de main subsiste pendant le geste. Les éléments concernés sont rouges ; ce poste ne valide pas l’intervention.", "POSITION IN AMBER: an arm or hand conflict remains during the gesture. The affected elements are red; this position does not validate the intervention.")
                    :!_state.SupportVerified?UiLanguage.T("PERSONNAGE EN AMBRE : appuis proposés sur le sol réel, contrôle incomplet ou réglages à recontrôler. Consultez les détails avant de conclure.", "FIGURE IN AMBER: supports proposed on the actual floor, incomplete check or settings to recheck. Review the details before concluding.")
                    :_state.WholeGestureClear?UiLanguage.T("Poste et geste complet contrôlés sur un sol réel ; confirmez les repères et les cotes avant de conclure.", "Position and complete gesture checked on an actual floor; confirm markers and dimensions before concluding.")
                    :UiLanguage.T("Poste proposé sur un sol réel ; lancez le contrôle pour vérifier le mouvement.", "Position proposed on an actual floor; run the check to verify the movement.");
                if(_showVolumes.IsChecked==true)_modeNote.Text+=UiLanguage.T(" Enveloppes affichées par échantillons ; le calcul porte sur tout le mouvement. Rectangle gris : confort indicatif, sans effet bloquant.", " Envelopes are displayed as samples; the calculation covers the whole movement. Grey rectangle: indicative comfort zone, non-blocking.");
                if(rejected)_modeNote.Text+=UiLanguage.T(" POSTE REFUSÉ EN ROUGE : le personnage illustre la position qui rencontre les éléments rouges ; ce n’est pas un poste accepté.", " REJECTED POSITION IN RED: the figure shows the position that hits the red elements; it is not an accepted position.");
                if(!demo&&_state.FootPoint!=null&&_state.PostureWarnings.Count>0)_modeNote.Text+=" "+string.Join(" ",_state.PostureWarnings);
                _play.IsEnabled=(demo||_state.CommandPoint!=null)&&!_running;
                _phase.IsEnabled=demo||_state.CommandPoint!=null;_showVolumes.IsEnabled=demo||_state.CommandPoint!=null;
                if(_state.CommandPoint==null&&!demo)
                {
                    _lastScene=null;_preview.SetScene(Array.Empty<MaintenancePreviewBox>(),CandidateLines());
                    return;
                }
                var profile = _poseProfile ?? (_poseProfile=demo?MaintenanceDemonstration.Create(Read(),_state.CommandPoint!=null):Read());
                bool removal=profile.Operation==MaintenanceAccessOperation.Filter||profile.Operation==MaintenanceAccessOperation.Actuator;
                _hypothesisSummary.Visibility=removal||profile.Operation==MaintenanceAccessOperation.LeverValve?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
                if(removal)_hypothesisSummary.Text=(_provenance.SelectedIndex==0?UiLanguage.T("Hypothèse de retrait : ", "Removal assumption: "):UiLanguage.T("Retrait renseigné : ", "Specified removal: "))
                    +(profile.RemovalSize.X*304.8).ToString("0")+" × "+(profile.RemovalSize.Y*304.8).ToString("0")+" × "+(profile.RemovalSize.Z*304.8).ToString("0")
                    +UiLanguage.T(" mm ; course ", " mm; travel ")+(profile.RemovalTravel*304.8).ToString("0")+UiLanguage.T(" mm. Vérifier la pièce et la notice.", " mm. Check the part and the datasheet.");
                else if(profile.Operation==MaintenanceAccessOperation.LeverValve)
                    _hypothesisSummary.Text=UiLanguage.T("Levier : ", "Lever: ")+(profile.HandleRadius*304.8).ToString("0.#")+UiLanguage.T(" mm · rotation ", " mm · rotation ")+profile.RotationDegrees.ToString("0.#")
                        +"°"+(_state.RotationAssumed?UiLanguage.T(" supposée ; sens et butées à confirmer.", " assumed; direction and end stops to confirm."):UiLanguage.T(" renseignée ; sens et butées à vérifier.", " specified; direction and end stops to check."));
                if(rejected){profile.OperatorOrigin=_state.RejectedFoot;profile.OperatorPosture=_state.RejectedPosture;profile.BodyYawDegrees=_state.RejectedBodyYawDegrees;}
                if(demo&&profile.Operation==MaintenanceAccessOperation.ChainwheelValve)_modeNote.Text+=UiLanguage.T(" Prise illustrative : ", " Illustrative grip: ")+(profile.WorkHeight*304.8).ToString("0")+UiLanguage.T(" mm au-dessus du sol fictif.", " mm above the notional floor.");
                var prepared=_preparedMotion??(_preparedMotion=MaintenanceAccessGeometry.Prepare(profile));
                _lastScene = MaintenanceAccessGeometry.PosePrepared(prepared, _phase.Value / 100);
                if (_controlScene == null) _controlScene = MaintenanceAccessGeometry.BuildPrepared(prepared, 0);
                bool showHuman=demo||_state.FootPoint!=null||rejected;
                bool provisional=!demo&&showHuman&&(!_state.SupportVerified||_state.WholeGestureChecked&&!_state.WholeGestureClear);
                var shown = _lastScene.Boxes.Where(b => !b.CheckCollision && (showHuman || b.Role!=MaintenanceAccessRole.HumanBody && b.Role!=MaintenanceAccessRole.HumanArm));
                if (_showVolumes.IsChecked == true) shown = shown.Concat(PreviewVolumes(_controlScene.CollisionBoxes.Where(b => showHuman||!b.DependsOnOperator)));
                var boxes = shown.Select(b => new MaintenancePreviewBox { Corners = b.Corners(), Color = b.Role == MaintenanceAccessRole.EquipmentGhost ? Colors.DeepSkyBlue
                    :rejected&&(b.Role==MaintenanceAccessRole.HumanBody||b.Role==MaintenanceAccessRole.HumanArm||b.Role==MaintenanceAccessRole.OperatorSpace)?Colors.Firebrick
                    :provisional&&(b.Role==MaintenanceAccessRole.HumanBody||b.Role==MaintenanceAccessRole.HumanArm||b.Role==MaintenanceAccessRole.OperatorSpace)?Colors.DarkOrange:Colors.MediumSeaGreen,
                    Opacity = b.CheckCollision ? .025 : b.Role == MaintenanceAccessRole.HumanBody || b.Role == MaintenanceAccessRole.HumanArm ? 1 : .35,
                    Shape = !b.CheckCollision && b.Label.StartsWith("Tête", StringComparison.Ordinal) ? "head" : !b.CheckCollision && b.Label.StartsWith("Buste", StringComparison.Ordinal) ? "torso" : !b.CheckCollision && b.Label.StartsWith("Main", StringComparison.Ordinal) ? "hand" : null }).ToList();
                if(demo)
                {
                    XYZ shift=profile.FollowRemoval&&(profile.Operation==MaintenanceAccessOperation.Filter||profile.Operation==MaintenanceAccessOperation.Actuator)
                        ?profile.Frame.OfVector(profile.RemovalDirection).Normalize()*profile.RemovalTravel:XYZ.Zero;
                    var size=new XYZ(2.2/.3048+Math.Abs(shift.X),2.2/.3048+Math.Abs(shift.Y),.02/.3048);
                    var floor=new MaintenanceAccessBox {Frame=Transform.Identity,Center=new XYZ(shift.X/2,shift.Y/2,-size.Z/2),Size=size};
                    boxes.Add(new MaintenancePreviewBox {Corners=floor.Corners(),Color=Colors.SlateGray,Opacity=.22});
                }
                if (_report != null&&!demo)
                {
                    var area = _controlScene.CollisionBoxes.SelectMany(b => b.BoundsCorners()).ToArray();
                    var min = new XYZ(area.Min(p => p.X) - 1, area.Min(p => p.Y) - 1, area.Min(p => p.Z) - .1);
                    var max = new XYZ(area.Max(p => p.X) + 1, area.Max(p => p.Y) + 1, area.Max(p => p.Z) + 1);
                    foreach (var collision in _report.Obstacles)
                    {
                        if (collision.Box == null || collision.Box.Length == 0) continue;
                        var lo = new XYZ(Math.Max(min.X, collision.Box.Min(p => p.X)), Math.Max(min.Y, collision.Box.Min(p => p.Y)), Math.Max(min.Z, collision.Box.Min(p => p.Z)));
                        var hi = new XYZ(Math.Min(max.X, collision.Box.Max(p => p.X)), Math.Min(max.Y, collision.Box.Max(p => p.Y)), Math.Min(max.Z, collision.Box.Max(p => p.Z)));
                        if (lo.X >= hi.X || lo.Y >= hi.Y || lo.Z >= hi.Z) continue;
                        boxes.Add(new MaintenancePreviewBox { Corners = Enumerable.Range(0, 8).Select(i => new XYZ((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z)).ToArray(), Color = Colors.OrangeRed, Opacity = 0 });
                    }
                }
                var lines = new List<MaintenancePreviewLine>();
                if(demo&&(profile.Operation==MaintenanceAccessOperation.Filter||profile.Operation==MaintenanceAccessOperation.Actuator))
                {
                    for(int i=0;i<40;i++)
                    {
                        double a=i*Math.PI/20,b=(i+1)*Math.PI/20;
                        lines.Add(new MaintenancePreviewLine {A=profile.Frame.OfPoint(new XYZ(profile.HandleRadius*Math.Cos(a),profile.HandleRadius*Math.Sin(a),0)),
                            B=profile.Frame.OfPoint(new XYZ(profile.HandleRadius*Math.Cos(b),profile.HandleRadius*Math.Sin(b),0)),Radius=5/304.8,Color=Colors.DarkSlateGray,Static=true});
                    }
                }
                var origin = profile.WorldCenter; var end = origin + profile.Frame.BasisZ * (450 / 304.8);
                lines.Add(new MaintenancePreviewLine { A = origin, B = end, Radius = 7 / 304.8, Color = Colors.DarkViolet,Static=true });
                lines.Add(new MaintenancePreviewLine { A = end, B = end - profile.Frame.BasisZ * (80 / 304.8) + profile.Frame.BasisX * (35 / 304.8), Radius = 7 / 304.8, Color = Colors.DarkViolet,Static=true });
                lines.Add(new MaintenancePreviewLine { A = end, B = end - profile.Frame.BasisZ * (80 / 304.8) - profile.Frame.BasisX * (35 / 304.8), Radius = 7 / 304.8, Color = Colors.DarkViolet,Static=true });
                foreach (var line in _lastScene.Lines)
                {
                    if(line.Role==MaintenanceAccessRole.Reference&&(_showVolumes.IsChecked!=true||!showHuman))continue;
                    if(!showHuman&&(line.Role==MaintenanceAccessRole.HumanBody||line.Role==MaintenanceAccessRole.HumanArm))continue;
                    for (int i = 1; i < line.Points.Length; i++) lines.Add(new MaintenancePreviewLine { A = line.Points[i - 1], B = line.Points[i], Radius = line.Thickness / 2,
                        Static=line.Role==MaintenanceAccessRole.Chain||line.Role==MaintenanceAccessRole.Handle&&line.Label=="Volant",
                        Color = line.Role == MaintenanceAccessRole.Reference ? Colors.SlateGray:line.Role == MaintenanceAccessRole.Chain ? Colors.Goldenrod : line.Role == MaintenanceAccessRole.Handle ? Colors.DodgerBlue
                            :rejected&&(line.Role==MaintenanceAccessRole.HumanBody||line.Role==MaintenanceAccessRole.HumanArm)?Colors.Firebrick
                            :provisional&&(line.Role==MaintenanceAccessRole.HumanBody||line.Role==MaintenanceAccessRole.HumanArm)?Colors.DarkOrange:Colors.SeaGreen });
                }
                _preview.SetScene(boxes, lines);
            }
            catch (Exception ex) { _preview.SetScene(Array.Empty<MaintenancePreviewBox>(),Array.Empty<MaintenancePreviewLine>()); _status.Text = UiLanguage.T("Ajustez les paramètres : ", "Adjust the parameters: ") + ex.Message; _status.Foreground = Brushes.DarkOrange; }
        }
        private IEnumerable<MaintenancePreviewLine> CandidateLines()
        {
            if(_candidates.SelectedIndex<0||_candidates.SelectedIndex>=_state.Candidates.Count)yield break;
            var c=_state.Candidates[_candidates.SelectedIndex];
            if(c.CommandPoint==null||c.Axis==null)yield break;
            XYZ z=c.Axis.Normalize(),reference=Math.Abs(z.Z)<.9?XYZ.BasisZ:XYZ.BasisX;
            XYZ x=reference.CrossProduct(z).Normalize(),y=z.CrossProduct(x).Normalize();
            int count=40;double radius=Math.Max(c.Radius,40/304.8);
            for(int i=0;i<count;i++)
            {
                double a=i*Math.PI*2/count,b=(i+1)*Math.PI*2/count;
                yield return new MaintenancePreviewLine {A=c.CommandPoint+radius*(x*Math.Cos(a)+y*Math.Sin(a)),B=c.CommandPoint+radius*(x*Math.Cos(b)+y*Math.Sin(b)),Radius=5/304.8,Color=Colors.DarkOrange};
            }
            yield return new MaintenancePreviewLine {A=c.CommandPoint,B=c.CommandPoint+z*(350/304.8),Radius=6/304.8,Color=Colors.DarkViolet};
        }
        private static IEnumerable<MaintenanceAccessBox> PreviewVolumes(IEnumerable<MaintenanceAccessBox> volumes)
        {
            foreach(var group in volumes.GroupBy(v=>new {v.Role,v.Label}))
            {
                var items=group.ToArray();int count=Math.Min(6,items.Length);
                for(int i=0;i<count;i++)
                {
                    var item=items[count==1?0:(int)Math.Round(i*(items.Length-1.0)/(count-1))];
                    yield return item;
                    if(item.Translation.GetLength()>1e-8)
                        yield return new MaintenanceAccessBox {Frame=item.Frame,Center=item.Center+item.Frame.Inverse.OfVector(item.Translation),Size=item.Size,
                            Role=item.Role,Label=item.Label,CheckCollision=item.CheckCollision,CheckSource=item.CheckSource,DependsOnOperator=item.DependsOnOperator};
                }
            }
        }
        private static string PostureLabel(MaintenanceOperatorPosture posture,double yaw)
            => posture==MaintenanceOperatorPosture.Crouched?UiLanguage.T("Accroupi · posture contraignante à évaluer.", "Crouching · awkward posture to assess.")
                : Math.Abs(yaw)>1?UiLanguage.T("Corps tourné de ", "Body turned by ")+yaw.ToString("0",CultureInfo.CurrentCulture)+UiLanguage.T("° · posture de profil à évaluer.", "° · side-on posture to assess.")
                : UiLanguage.T("Debout face à la commande · gabarit réglable, sans certification ergonomique.", "Standing facing the operator · adjustable template, no ergonomic certification.");
        private void Progress(string text)
        {
            _status.Text = text;
            var frame = new DispatcherFrame(); Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
            if (_cancelRequested) throw new OperationCanceledException();
        }
        private void RefreshBlockers()
        {
            StopPlayback();ResetPlaybackSelection();
            _poseProfile=null;_controlScene=null;_preparedMotion=null;
            UpdateBlockedElements();Draw();
        }
        private void UpdateBlockedElements()
        {
            _preview.SetBlockedElements((_showBlockedPosture.IsChecked==true?_state.BlockingObstacles:Enumerable.Empty<MaintenanceAccessObstacle>())
                .Concat(_state.FixedObstacles).Concat(_state.GestureObstacles)
                .Concat(_report?.Obstacles??Enumerable.Empty<MaintenanceAccessObstacle>()));
        }
        private void Analyze()
        {
            if(Demonstration){_report=null;_status.Text=UiLanguage.T("Démonstration uniquement · choisissez « Dans la maquette » pour vérifier le poste réel.", "Demonstration only · choose “In the model” to check the actual position.");return;}
            _inputTimer.Stop();
            StopPlayback(); ResetPlaybackSelection(); _report = null; _running = true; _cancelRequested = false; _cancel.Visibility = System.Windows.Visibility.Visible; _settings.IsEnabled = false;
            UpdateBlockedElements();
            _status.Foreground = Brushes.DarkOrange;_mode.IsEnabled=false;_play.IsEnabled=false;
            try
            {
                var profile=Read();
                var placement=MaintenanceOperatorPlacement.ValidateControlled(_doc,_source,profile,Progress,()=>_cancelRequested);
                MaintenanceAutoconfiguration.ApplyPlacementResult(_state,placement);
                UpdateBlockedElements();
                if(placement.FootPoint==null)
                {
                    _state.FootPoint=null;_poseProfile=null;_controlScene=null;_preparedMotion=null;Draw();
                    _analyze.IsEnabled=false;
                    _status.Text=UiLanguage.T("Aucun personnage proposé pour le geste complet.", "No figure proposed for the complete gesture.");
                    _noOperatorReason.Text=NoOperatorReason();_noOperatorReason.Visibility=System.Windows.Visibility.Visible;
                    _details.Text=string.Join("\n",placement.Warnings)+"\n"+MaintenanceAccessRules.Scope;
                    return;
                }
                _poseProfile=profile;_preparedMotion=MaintenanceAccessGeometry.Prepare(profile);
                var scene = _controlScene = MaintenanceAccessGeometry.BuildPrepared(_preparedMotion, 0);
                _report = MaintenanceAccessScanner.Run(_doc, _source, scene, Progress,()=>_cancelRequested);
                bool incomplete = _report.Unknown.Count > 0 || scene.Warnings.Count > 0 || !scene.Reachable || _confirmed.IsChecked != true
                    || _provenance.SelectedIndex == 0 || _provenance.SelectedIndex==1&&string.IsNullOrWhiteSpace(_notice.Text)
                    || !_state.SupportVerified || _state.FeatureWarnings.Count>0 || _state.PlacementWarnings.Count>0;
                _status.Foreground = _report.Obstacles.Count > 0 ? Brushes.Firebrick : incomplete ? Brushes.DarkOrange : scene.ConstraintNotes.Count>0?Brushes.DarkGoldenrod:Brushes.SeaGreen;
                _status.Text = _report.Obstacles.Count > 0 ? UiLanguage.T("Obstacle détecté · ", "Obstacle detected · ") + _report.Obstacles.Count + UiLanguage.T(" conflit(s) sur l’intervention.", " conflict(s) on the intervention.")
                    : incomplete ? UiLanguage.T("À confirmer · aucun obstacle détecté avec ces hypothèses.", "To be confirmed · no obstacle detected with these assumptions.")
                    : scene.ConstraintNotes.Count>0?UiLanguage.T("Geste possible géométriquement · posture contraignante, ergonomie à évaluer.", "Gesture geometrically possible · awkward posture, ergonomics to assess.")
                    : UiLanguage.T("Libre géométriquement avec les repères et cotes déclarés.", "Geometrically clear with the declared markers and dimensions.");
                _details.Text = string.Join("\n", _report.Obstacles.Take(14).Select(o => "• " + o.VolumeLabel + " : " + o.Label))
                    + "\n" + string.Join("\n", scene.Warnings.Concat(_report.Unknown).Concat(_state.FeatureWarnings).Concat(_state.PlacementWarnings).Distinct().Take(14).Select(s => "• " + s))
                    + "\n" + (_confirmed.IsChecked == true ? UiLanguage.T("Repères déclarés vérifiés par l’utilisateur.", "Markers declared as checked by the user.") : UiLanguage.T("Repères et cotes non confirmés.", "Markers and dimensions not confirmed."))
                    + UiLanguage.T("\nCotes : ", "\nDimensions: ") + (_provenance.SelectedIndex == 1 ? UiLanguage.T("notice déclarée — ", "declared datasheet — ") + _notice.Text : _provenance.SelectedIndex==2 ? UiLanguage.T("géométrie et paramètres de la famille ; courses non documentées à confirmer", "family geometry and parameters; undocumented travels to confirm") : UiLanguage.T("hypothèses réglables, à mesurer", "adjustable assumptions, to be measured"))
                    + "\n" + string.Join("\n", scene.Assumptions) + "\n" + MaintenanceAccessRules.Scope + UiLanguage.T("\nAperçu : ", "\nPreview: ") + _preview.CaptureNote;
                _details.Text+="\n"+string.Join("\n",scene.ConstraintNotes.Concat(_state.PostureWarnings).Concat(_state.PlacementDiagnostics));
                UpdateBlockedElements();
                Draw();
            }
            catch (OperationCanceledException) { _report=null;_status.Text = UiLanguage.T("Contrôle annulé · aucun résultat valide.", "Check cancelled · no valid result.");_details.Text=UiLanguage.T("Le contrôle a été interrompu. Aucun résultat précédent n’est conservé.", "The check was interrupted. No previous result is kept.");UpdateBlockedElements();Draw(); }
            catch (Exception ex) { _report=null;_status.Text = UiLanguage.T("Contrôle impossible : ", "Check failed: ") + ex.Message;_details.Text=UiLanguage.T("Aucun résultat valide. ", "No valid result. ")+ex.Message;UpdateBlockedElements();Draw(); }
            finally { _running = false; _cancel.Visibility = System.Windows.Visibility.Collapsed; _settings.IsEnabled = true;_mode.IsEnabled=true;_play.IsEnabled=_state.CommandPoint!=null; }
        }
    }
    internal static class MaintenanceBuildIdentity
    {
        internal const string Revision="23.10.2026-r11";
        internal static readonly string LoadedId=typeof(MaintenanceBuildIdentity).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0,8).ToUpperInvariant();
        internal static string Display=>Revision+" · "+LoadedId;
    }
    internal static class MaintenanceAccessRules
    {
        internal static string Scope => UiLanguage.T("Contrôle géométrique du corps articulé et du mouvement renseignés. Une posture de profil ou accroupie peut être possible tout en restant contraignante. Aucun seuil universel de passage ne permet ici de conclure à une conformité. Trajet d’approche, effort/couple, durée, consignation, température, masse, levage et procédure réelle restent à évaluer.", "Geometric check of the articulated body and the specified movement. A side-on or crouching posture may be possible while remaining awkward. No universal clearance threshold allows compliance to be concluded here. Approach route, force/torque, duration, lockout, temperature, mass, lifting and the actual procedure remain to be assessed.");
        internal static string References => UiLanguage.T("INRS — Conception des lieux et situations de travail : prévoir accès, volumes d’intervention et commandes accessibles.\nhttps://www.inrs.fr/demarche/conception-lieux-situations-travail/dix-points-cles.html\n\nRotork PUB148-015 (06/2023) — boucle continue, guides de chaîne, face de roue vers l’opérateur. La fixation et l’état de la chaîne restent à vérifier.\nhttps://www.rotork.com/en/products/gearbox-accessories/chainwheels\n\nSpirax Sarco Fig 34HP TI-P168-01 — cotes de retrait propres au modèle et au DN. Respecter l’origine des cotes du dessin, sans appliquer un dégagement universel.\nhttps://www.spiraxsarco.com\n\nSAMSON EB8015, chapitre 5 — accès à la commande et espace pour dépose / entretien.\nhttps://www.samsongroup.com/document/e80150en.pdf\n\nISO 14738:2002 exclut les besoins spécifiques de maintenance. ISO 11226 ne valide pas le couple de manœuvre. Aucun seuil d’effort ni aucune conformité normative n’est déduit de ces volumes.\nhttps://www.iso.org/standard/27556.html\nhttps://www.iso.org/standard/25573.html", "INRS — Design of workplaces and work situations: provide access, intervention volumes and accessible controls.\nhttps://www.inrs.fr/demarche/conception-lieux-situations-travail/dix-points-cles.html\n\nRotork PUB148-015 (06/2023) — continuous loop, chain guides, wheel face towards the operator. Chain fixing and condition remain to be checked.\nhttps://www.rotork.com/en/products/gearbox-accessories/chainwheels\n\nSpirax Sarco Fig 34HP TI-P168-01 — withdrawal dimensions specific to the model and DN. Follow the dimension origin of the drawing, without applying a universal clearance.\nhttps://www.spiraxsarco.com\n\nSAMSON EB8015, chapter 5 — access to the operator and space for removal / maintenance.\nhttps://www.samsongroup.com/document/e80150en.pdf\n\nISO 14738:2002 excludes specific maintenance needs. ISO 11226 does not validate operating torque. No force threshold or standards compliance is inferred from these volumes.\nhttps://www.iso.org/standard/27556.html\nhttps://www.iso.org/standard/25573.html");
    }
}

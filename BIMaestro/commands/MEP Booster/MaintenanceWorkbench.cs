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
                        start.Scenario == 2 ? "Sélectionnez le filtre à entretenir." : start.Scenario == 3 ? "Sélectionnez la vanne dont l’actionneur doit être déposé." : "Sélectionnez la vanne à ouvrir ou fermer manuellement.")) as FamilyInstance;
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
                                "Cliquez le centre du volant, l’axe du levier, la roue de chaîne ou le capot à retirer.");
                            state.CommandPoint = reference.GlobalPoint;
                            state.CorrectionApplied=false;state.CorrectionNote="Centre corrigé manuellement ; mémorisez les réglages pour réutiliser cette correction.";
                            state.Measured=false;state.Manufacturer=false;
                            state.FeatureWarnings.Add("Centre pointé manuellement : vérifiez le repère, le sens et l’origine des cotes avant le contrôle.");
                            var nearby=state.Candidates.Where(c=>c.CommandPoint!=null&&c.Axis!=null)
                                .OrderBy(c=>(c.CommandPoint-reference.GlobalPoint).GetLength()).FirstOrDefault();
                            if(nearby!=null&&(nearby.CommandPoint-reference.GlobalPoint).GetLength()<Math.Max(nearby.Radius,50/304.8))
                                MaintenanceAutoconfiguration.ApplyCandidate(state,nearby);
                            state.Confirmed = false;
                            Prepare(true);
                        }
                        else if(window.PickRequest=="axis")
                        {
                            var center=ui.Selection.PickObject(ObjectType.PointOnElement,new AccessSelection(source.Id),"Cliquez le centre du capot ou de la commande.").GlobalPoint;
                            var end=ui.Selection.PickObject(ObjectType.PointOnElement,"Cliquez un point indiquant le sens de retrait ou l’axe de commande.").GlobalPoint;
                            if((end-center).GetLength()<2/304.8)throw new ArgumentException("Les deux points doivent être distincts.");
                            MaintenanceAutoconfiguration.ApplyCandidate(state,new MaintenanceFeatureCandidate {CommandPoint=center,Axis=(end-center).Normalize()});
                            Prepare(true);
                        }
                        else if(window.PickRequest=="handle")
                        {
                            if(state.CommandPoint==null)throw new ArgumentException("Précisez le pivot avant de pointer la poignée.");
                            var end=ui.Selection.PickObject(ObjectType.PointOnElement,new AccessSelection(source.Id),"Cliquez l’extrémité de la poignée, à l’endroit de la prise de main.").GlobalPoint;
                            var profile=MaintenanceAutoconfiguration.ReadSavedProfile(source,state);
                            XYZ z=profile.Frame.BasisZ,radial=end-state.CommandPoint;
                            radial-=z*radial.DotProduct(z);
                            if(radial.GetLength()<2/304.8)throw new ArgumentException("L’extrémité doit être éloignée de l’axe du pivot.");
                            var frame=Transform.Identity;frame.Origin=state.CommandPoint;frame.BasisX=radial.Normalize();frame.BasisY=z.CrossProduct(frame.BasisX).Normalize();frame.BasisZ=z;
                            MaintenanceAutoconfiguration.ApplyCandidate(state,new MaintenanceFeatureCandidate {CommandPoint=state.CommandPoint,Axis=z,Frame=frame});
                            state.Fields["Radius"]=(radial.GetLength()*304.8).ToString("0.###",CultureInfo.CurrentCulture);
                            state.Fields["InitialAngle"]="0";
                            Prepare(true);
                        }
                        else
                        {
                            state.FootPoint = ui.Selection.PickObject(ObjectType.PointOnElement, new WalkingSurfaceSelection(ui.Document),
                                "Cliquez un sol praticable au centre des pieds. Les tuyaux et équipements ne sont pas des appuis.").GlobalPoint;
                            state.Confirmed = false;
                            state.SupportVerified = false;
                            state.SupportNote = "Sol pointé manuellement ; appuis et posture à vérifier.";
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
                    var label = new TextBlock { Text = "Préparation de l’intervention…", Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };
                    var wait = new Window { Title = "Maintenance MEP", Content = label, Width = 420, Height = 120, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
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
                    {state.FootPoint=null;state.SupportVerified=false;state.NoOperatorReason="Proposition automatique indisponible : "+ex.Message;state.PlacementWarnings.Add(state.NoOperatorReason);}
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
            Title = "Maintenance MEP"; Width = 680; Height = 640; MinHeight = 450;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            new WindowInteropHelper(this).Owner = owner; BoosterTheme.Attach(this);
            var body = new StackPanel { Margin = new Thickness(26) };
            body.Children.Add(new TextBlock { Text = "Peut-on réellement intervenir ici ?", FontSize = 25, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = "Choisissez une intervention et l’équipement. La commande et un poste au sol sont proposés automatiquement quand la géométrie le permet.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18), FontSize = 14 });
            Add(0, "↗", "Sortir un moteur de pompe", "Volume du moteur, virages et portes jusqu’à la sortie.");
            Add(1, "↻", "Ouvrir / fermer une vanne manuellement", "Volant, levier ou chaîne réellement présent ; opérateur et mouvement des mains. La commande électrique n’est pas simulée.");
            Add(3, "⇧", "Déposer un actionneur", "Retrait du moteur de la vanne pour maintenance ; volume, course de retrait et portée de l’opérateur.");
            Add(2, "⇥", "Entretenir un filtre", "Retrait du tamis ou de la cartouche, place de l’opérateur et des outils.");
            body.Children.Add(new TextBlock { Text = "Aperçus temporaires : aucun équipement n’est déplacé. Les repères et les cotes de la notice doivent être confirmés avant de conclure.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 0) });
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
        private static readonly string[] OperationNames = { "Manœuvre — levier manuel", "Manœuvre — volant manuel", "Manœuvre — volant avec chaîne", "Maintenance — dépose de l’actionneur", "Retrait du tamis / de la cartouche" };
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
        private readonly ComboBox _axis = new ComboBox { Style = null, MinHeight = 36, FontSize = 14, ItemsSource = new[] { "+X local", "−X local", "+Y local", "−Y local", "+Z local", "−Z local", "Axe détecté dans la famille" } };
        private readonly CheckBox _confirmed = new CheckBox { Content = "Repère, sens et cotes vérifiés sur l’équipement / la notice", Margin = new Thickness(0, 9, 0, 5) };
        private readonly CheckBox _showVolumes = new CheckBox { Content = "Afficher les volumes contrôlés", IsChecked = false };
        private readonly ComboBox _mode = new ComboBox {Style=null,MinHeight=34,ItemsSource=new[] {"Dans la maquette","Démonstration du geste"}};
        private readonly TextBlock _modeNote = new TextBlock {TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,6,0,0),Foreground=Brushes.DarkOrange};
        private readonly ComboBox _candidates = new ComboBox {Style=null,MinHeight=34};
        private readonly ComboBox _posture = new ComboBox {Style=null,MinHeight=34,ItemsSource=new[] {"Automatique : debout, de profil ou accroupi","Debout face à la commande","De profil — côté gauche","De profil — côté droit","Accroupi"}};
        private readonly CheckBox _showBlockedPosture = new CheckBox {Content="Montrer le poste refusé et ses obstacles",IsChecked=true};
        private readonly Button _play = new Button {Content="▶ Lire le geste",MinHeight=36,Style=null,Width=150,HorizontalAlignment=HorizontalAlignment.Left};
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 17, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _noOperatorReason = new TextBlock { TextWrapping=TextWrapping.Wrap,FontSize=13,Margin=new Thickness(0,6,0,3),Foreground=Brushes.DarkOrange };
        private readonly Button _suggest = new Button { Style=null,MinHeight=36,Margin=new Thickness(0,0,0,6),HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(8) };
        private readonly TextBlock _details = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly TextBlock _help = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
        private readonly TextBlock _hypothesisSummary = new TextBlock { TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DarkSlateGray,FontSize=11,Margin=new Thickness(0,4,0,0) };
        private readonly TextBlock _detected = new TextBlock { TextWrapping=TextWrapping.Wrap, FontSize=12, Margin=new Thickness(0,0,0,8) };
        private readonly TextBlock _memoryNote = new TextBlock { TextWrapping=TextWrapping.Wrap, FontSize=12, Margin=new Thickness(0,6,0,4), Foreground=Brushes.DarkSlateGray };
        private readonly Button _remember = new Button { Content="Mémoriser ces réglages pour les équipements compatibles", Style=null, MinHeight=36, Margin=new Thickness(0,6,0,4), HorizontalContentAlignment=HorizontalAlignment.Left, Padding=new Thickness(8) };
        private readonly Button _forget = new Button { Content="Oublier la correction mémorisée pour cet équipement", Style=null, MinHeight=32, Margin=new Thickness(0,0,0,4), HorizontalContentAlignment=HorizontalAlignment.Left, Padding=new Thickness(8) };
        private readonly Button _handleDirection = new Button { Content="Pointer l’extrémité de la poignée…", Style=null, MinHeight=36, Margin=new Thickness(0,0,0,6), HorizontalContentAlignment=HorizontalAlignment.Left, Padding=new Thickness(8) };
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
        private readonly Button _analyze = new Button { Content = "Vérifier l’intervention", MinHeight = 42 };
        private readonly Button _cancel = new Button { Content = "Annuler le contrôle", Visibility = System.Windows.Visibility.Collapsed };
        private readonly TextBox _notice = new TextBox { Style = null, MinHeight = 34, Padding = new Thickness(6), ToolTip = "Référence produit / notice et origine des cotes" };
        private readonly ComboBox _provenance = new ComboBox { Style = null, MinHeight = 34, ItemsSource = new[] { "Hypothèses réglables", "Cotes reportées d’une notice constructeur", "Cotes mesurées dans la famille" } };
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
            Title = "Maintenance MEP · "+MaintenanceBuildIdentity.Display; Width = 1180; Height = 850; MinWidth = 940; MinHeight = 620;
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
            var details = new Expander { Header = "Détails, hypothèses et éléments à confirmer", Content = new ScrollViewer { Content = _details, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new Thickness(0, 8, 0, 0) }; card.Children.Add(details);
            var actions = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); right.Children.Add(actions);
            actions.Children.Add(_play); actions.Children.Add(_phase); actions.Children.Add(_showVolumes);actions.Children.Add(_showBlockedPosture);
            actions.Children.Add(new TextBlock { Text = "Glisser : tourner la vue · molette : zoomer · double-clic : recadrer\nBleu : équipement · vert : opérateur / mouvement · flèche violette : axe choisi\nRouge : collision · gris : environnement", Foreground = Brushes.DimGray, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
            _preview = new MaintenancePreview(doc, source, CapturePoints(), state.BlockingObstacles.Concat(state.FixedObstacles).Concat(state.GestureObstacles)); right.Children.Add(_preview); Content = layout;
            settings.Children.Add(new TextBlock { Text = "Préparer l’intervention", FontSize = 22, FontWeight = FontWeights.SemiBold });
            settings.Children.Add(new TextBlock { Text = source.Symbol.Family.Name+" · "+source.Name + " · " + BoosterIds.Value(source.Id), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 15) });
            Heading(settings, "1  Quel geste faut-il réaliser ?");
            _operation.ItemsSource = OperationNames; _operation.SelectedIndex = (int)state.Operation; settings.Children.Add(_operation);
            settings.Children.Add(_help);settings.Children.Add(_hypothesisSummary);
            Heading(settings, "2  Vérifier la proposition automatique");
            settings.Children.Add(_detected);
            settings.Children.Add(new TextBlock {Text="Posture à essayer",Margin=new Thickness(0,2,0,4)});
            _posture.SelectedIndex=(int)state.PostureSearch;settings.Children.Add(_posture);
            _suggest.Click+=(_,__)=>{PickRequest=_state.CommandPoint==null?"auto":"position";Close();};settings.Children.Add(_suggest);
            if(state.CommandPoint==null&&state.Candidates.Count>0)
            {
                _candidates.ItemsSource=state.Candidates.Select((c,i)=>"Repère "+(i+1)+" · rayon "+(c.Radius*304.8).ToString("0")+" mm").ToArray();
                _candidates.SelectedIndex=0;settings.Children.Add(_candidates);
                var use=new Button {Content="Utiliser le repère surligné…",Style=null,MinHeight=36,Margin=new Thickness(0,5,0,5)};
                use.Click+=(_,__)=>UseCandidate();settings.Children.Add(use);
            }
            var manual=new StackPanel {Margin=new Thickness(0,8,0,8)};
            Pick(manual, state.CommandPoint == null ? "Pointer la commande / le capot…" : "Corriger le centre de commande…", "command");
            Pick(manual, state.FootPoint == null ? "Choisir un sol praticable…" : "Corriger le poste au sol…", "operator");
            if(state.CommandPoint!=null)Pick(manual,"Relancer la détection des repères…","auto");
            Pick(manual,"Définir un axe incliné par deux points…","axis");
            _handleDirection.Click+=(_,__)=>{PickRequest="handle";Close();};manual.Children.Add(_handleDirection);
            manual.Children.Add(new TextBlock { Text = "Axe de rotation ou sens de retrait", Margin = new Thickness(0, 9, 0, 5) });
            _axis.SelectedIndex = state.Axis; manual.Children.Add(_axis);
            var reverse=new Button {Content="↔ Inverser le sens",Style=null,MinHeight=32,Margin=new Thickness(0,5,0,0)};
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
            _remember.Content=new TextBlock {Text="Mémoriser ces réglages pour les équipements compatibles",TextWrapping=TextWrapping.Wrap};
            _forget.Content=new TextBlock {Text="Oublier la correction mémorisée pour cet équipement",TextWrapping=TextWrapping.Wrap};
            manual.Children.Add(_remember);manual.Children.Add(_forget);manual.Children.Add(_memoryNote);
            manual.Children.Add(new TextBlock {Text="Le geste est réutilisé si la géométrie correspond. Sol, posture et obstacles sont recalculés à chaque intervention.",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brushes.DimGray});
            settings.Children.Add(new Expander {Header="Corriger un repère ou le poste…",Content=manual,Margin=new Thickness(0,8,0,0)});
            var dimensions=new StackPanel {Margin=new Thickness(0,7,0,7)};
            Field(dimensions, "Radius", "Rayon du volant / longueur du levier", 200);
            Field(dimensions, "Angle", "Angle de manœuvre (°)", state.Operation == MaintenanceAccessOperation.LeverValve ? 90 : 360);
            Field(dimensions, "ChainDrop", "Descente de chaîne depuis la roue", 2000);
            Field(dimensions, "ChainSpacing", "Écartement des deux brins", 400);
            Field(dimensions, "RX", "Largeur de la pièce à retirer", 250);
            Field(dimensions, "RY", "Profondeur de la pièce à retirer", 250);
            Field(dimensions, "RZ", "Longueur de la pièce à retirer", 500);
            Field(dimensions, "Travel", "Course de retrait depuis le capot", 700);
            dimensions.Children.Add(_provenance); _provenance.SelectedIndex = state.Manufacturer ? 1 : state.Measured ? 2 : 0;
            dimensions.Children.Add(new TextBlock { Text = "Référence de la notice / origine des cotes", Margin = new Thickness(0, 8, 0, 3) });
            _notice.Text = state.Notice; dimensions.Children.Add(_notice);
            settings.Children.Add(new Expander {Header="Cotes du geste et origine des valeurs…",Content=dimensions,Margin=new Thickness(0,10,0,0)});
            Heading(settings, "3  Vérifier dans la maquette");
            _confirmed.Content = new TextBlock { Text = "Repère, sens et cotes vérifiés sur l’équipement / la notice", TextWrapping = TextWrapping.Wrap };
            _confirmed.IsChecked = state.Confirmed; settings.Children.Add(_confirmed);
            _analyze.Style = (Style)FindResource("PrimaryButton"); settings.Children.Add(_analyze);
            var advanced = new StackPanel { Margin = new Thickness(0, 8, 0, 5) };
            Field(advanced, "Height", "Taille de l’opérateur", 1700); Field(advanced, "Width", "Largeur de confort (indicative)", 650); Field(advanced, "Depth", "Profondeur de confort (indicative)", 650);
            Field(advanced, "Grip", "Dégagement des mains / outils", 70); Field(advanced, "Margin", "Marge supplémentaire", 10);
            Field(advanced, "Thickness", "Épaisseur de la couronne / poignée", 35);
            Field(advanced, "InitialAngle", "Angle initial du levier / volant (°)", 0);
            Field(advanced, "Pull", "Course de traction de la chaîne", 350);
            Field(advanced, "WorkHeight", "Hauteur de prise de chaîne / sol", 1200);
            Field(advanced, "OffsetX", "Décalage pièce X / capot", 0); Field(advanced, "OffsetY", "Décalage pièce Y / capot", 0); Field(advanced, "OffsetZ", "Décalage pièce Z / capot", -250);
            advanced.Children.Add(new TextBlock { Text = "Longueurs en mm. La largeur et la profondeur de confort tracent un repère indicatif ; elles ne bloquent pas le corps et ne sont pas des minima réglementaires. Le mannequin articulé reste un gabarit réglable : effort, durée et ergonomie doivent être évalués séparément.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            settings.Children.Add(new Expander { Header = "Réglages du gabarit et de la pièce", Content = advanced, Margin = new Thickness(0, 12, 0, 0) });
            settings.Children.Add(new Expander { Header = "Références métier et limites", Content = new TextBlock { Text = MaintenanceAccessRules.References, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, Margin = new Thickness(0, 12, 0, 0) });
            var close = new Button { Content = "Fermer", IsCancel = true, Margin = new Thickness(0, 14, 0, 0) }; close.Click += (_, __) => Close(); settings.Children.Add(close);
            _operation.SelectionChanged += (_, __) =>
            {
                UpdateOperation();if(!_ready)return;
                _state.Operation=(MaintenanceAccessOperation)_operation.SelectedIndex;
                _state.CommandPoint=null;_state.FootPoint=null;_state.DetectedFrame=null;_state.SupportVerified=false;_state.Measured=false;_state.Manufacturer=false;
                _state.DetectionNote="Intervention modifiée : relancez la détection pour proposer les nouveaux repères.";_state.SupportNote="";
                _state.NoOperatorReason="Intervention modifiée : détectez la commande du geste choisi pour rechercher un personnage.";
                _state.CorrectionApplied=false;_state.CorrectionNote="";_state.RotationAssumed=_operation.SelectedIndex==0;_memoryNote.Text="";
                _state.FeatureWarnings.Clear();_state.FeatureWarnings.Add("Les repères précédents ne sont pas utilisés pour cette nouvelle intervention.");_state.PlacementWarnings.Clear();_state.Candidates.Clear();_candidates.ItemsSource=null;
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
            _play.Content="▶ Lire le geste";
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
            _help.Text = op == MaintenanceAccessOperation.ChainwheelValve ? "Deux brins sous la roue ; le mannequin tire la chaîne à sa hauteur de prise. Confirmez la descente réelle."
                : op == MaintenanceAccessOperation.Filter ? "Vérifiez le capot et le sens de retrait proposés. Reportez la course constructeur et le volume de la pièce, puis regardez son retrait."
                : op == MaintenanceAccessOperation.Actuator ? "Dépose de l’actionneur : volume et course de retrait. La commande électrique et le secours manuel ne sont pas simulés."
                : "Manœuvre manuelle : utilisez le volant ou le levier réellement présent, y compris un secours manuel identifié. La présence d’un moteur ne déclenche pas sa dépose. La commande électrique n’est pas simulée.";
        }
        private double Number(string key)
        {
            var text = _fields[key].Text.Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
                && !double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("Valeur incorrecte : " + key);
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Valeur non finie : " + key);
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
            catch(ArgumentException error){_memoryNote.Text="Réglages non mémorisés : "+error.Message;_memoryNote.Foreground=Brushes.DarkOrange;}
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
            return _state.CommandPoint==null?"Commande du geste non identifiée : détectez ou précisez le volant, le levier, la chaîne ou le point de dépose réel."
                :_state.PlacementWarnings.FirstOrDefault()??"Aucun poste n’est actuellement contrôlé : lancez la recherche pour tout le geste.";
        }
        private void Invalidate()
        {
            if (!_ready || _running) return;
            StopPlayback(); ResetPlaybackSelection(); _report = null;
            _controlScene = null;
            _poseProfile = null; _preparedMotion=null;
            _status.Foreground = Brushes.DarkOrange;
            _suggest.Content=_state.CommandPoint==null?"Détecter la commande et proposer le poste…":"Rechercher un poste pour tout le geste…";
            _detected.Text=(_state.CommandPoint==null?"Commande à préciser.":_state.CorrectionApplied?"Repère issu d’une correction mémorisée.":_state.DetectedFrame==null?"Centre renseigné · axe à vérifier.":"Centre et axe proposés depuis la famille.")
                +"\n"+(_state.FootPoint==null?NoOperatorReason():(_state.SupportVerified?"Poste proposé : ":"Poste provisoire : ")+_state.SupportNote.Split('\n')[0]);
            if(_state.FootPoint!=null)_detected.Text+="\n"+PostureLabel(_state.OperatorPosture,_state.BodyYawDegrees);
            if(_state.CorrectionApplied)_detected.Text+="\nCorrection mémorisée réutilisée · implantation recontrôlée ici.";
            _memoryNote.Text=_state.CorrectionNote;
            _remember.IsEnabled=!Demonstration&&_state.CommandPoint!=null;
            _remember.ToolTip=Demonstration?"La démonstration ne mémorise aucun repère réel.":"Enregistre le repère et les cotes du geste ; aucune posture ni validation de collision n’est enregistrée.";
            _forget.IsEnabled=!Demonstration;
            _status.Text = Demonstration?"Démonstration du geste · implantation réelle à vérifier."
                : _state.CommandPoint == null ? "Commande du geste non identifiée · aucun personnage placé."
                : _state.FootPoint == null ? "Aucun personnage proposé pour le geste complet."
                : _state.FixedObstacles.Count>0 ? "Poste trouvé · le mécanisme ou le retrait rencontre un obstacle."
                : _state.GestureObstacles.Count>0 ? "Poste proposé · un obstacle subsiste sur le geste."
                : _state.SearchLimited ? "Recherche limitée · le geste complet reste à vérifier."
                : _state.WholeGestureClear&&!_state.SupportVerified ? "Geste complet sans conflit détecté · proposition provisoire en ambre."
                : !_state.SupportVerified?"Poste provisoire en ambre · géométrie ou réglages à confirmer."
                : _state.WholeGestureClear ? "Poste proposé · geste complet sans conflit détecté avec ces réglages."
                : "Proposition prête · contrôlez le repère et le poste, puis vérifiez l’intervention.";
            _noOperatorReason.Text=!Demonstration&&_state.FootPoint==null?NoOperatorReason():"";
            _noOperatorReason.Visibility=string.IsNullOrEmpty(_noOperatorReason.Text)?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible;
            _details.Text = _state.DetectionNote+"\n"+_state.SupportNote+"\n"+string.Join("\n",_state.FeatureWarnings.Concat(_state.PlacementWarnings).Concat(_state.PostureWarnings).Concat(_state.PlacementDiagnostics).Concat(_state.GestureUnknown))
                +"\n"+string.Join("\n",_state.FixedObstacles.Concat(_state.GestureObstacles).Take(14).Select(o=>"• "+o.VolumeLabel+" : "+o.Label))
                + "\nRepères et cotes à confirmer avant de conclure. " + MaintenanceAccessRules.Scope + "\n" + _preview.CaptureNote;
            _analyze.IsEnabled = !Demonstration&&_state.CommandPoint != null && _state.FootPoint != null;
            _analyze.ToolTip=Demonstration?"Passez à « Dans la maquette » pour contrôler le vrai poste.":_state.FootPoint==null?NoOperatorReason():"Contrôle du poste, des bras et du mouvement contre la maquette.";
            UpdateBlockedElements();Draw();
        }
        private void InputChanged()
        {
            if (!_ready) return;
            if(_state.CorrectionApplied)
            {_state.CorrectionApplied=false;_state.CorrectionNote="Réglages modifiés depuis la correction mémorisée. Mémorisez-les à nouveau pour les réutiliser.";}
            _confirmed.IsChecked = false;
            _state.SupportVerified = false;
            _state.NoOperatorReason="Réglages modifiés : relancez la recherche d’un poste pour tout le geste.";
            _state.PlacementWarnings.Clear();_state.SupportNote="Poste à recontrôler après modification des réglages.";
            _state.RejectedFoot=null;_state.BlockingObstacles.Clear();_state.PostureWarnings.Clear();_state.PlacementDiagnostics.Clear();
            MaintenanceAutoconfiguration.ClearGestureState(_state);
            _preview.SetBlockedElements(Array.Empty<MaintenanceAccessObstacle>());
            StopPlayback(); ResetPlaybackSelection(); _report=null; _controlScene=null; _poseProfile=null;_preparedMotion=null;
            _status.Text="Réglages modifiés · contrôle à relancer.";
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
                _modeNote.Text=demo?"DÉMONSTRATION : sol et personnage illustratifs. Les cotes non mesurées sont provisoires. Aucun poste de la maquette n’est validé."
                    :_state.CommandPoint==null?"La démonstration reste disponible ; elle ne valide aucune commande ni posture dans la maquette."
                    :_state.FootPoint==null?"Le mécanisme reste animé ; le personnage illustratif est disponible en démonstration."
                    :_state.FixedObstacles.Count>0?"POSTE EN AMBRE : le corps tient, mais le mécanisme ou la pièce retirée rencontre les éléments rouges. Changer la posture ne résout pas ce conflit avec les cotes actuelles."
                    :_state.GestureObstacles.Count>0?"POSTE EN AMBRE : un conflit de bras ou de main subsiste pendant le geste. Les éléments concernés sont rouges ; ce poste ne valide pas l’intervention."
                    :!_state.SupportVerified?"PERSONNAGE EN AMBRE : appuis proposés sur le sol réel, contrôle incomplet ou réglages à recontrôler. Consultez les détails avant de conclure."
                    :_state.WholeGestureClear?"Poste et geste complet contrôlés sur un sol réel ; confirmez les repères et les cotes avant de conclure."
                    :"Poste proposé sur un sol réel ; lancez le contrôle pour vérifier le mouvement.";
                if(_showVolumes.IsChecked==true)_modeNote.Text+=" Enveloppes affichées par échantillons ; le calcul porte sur tout le mouvement. Rectangle gris : confort indicatif, sans effet bloquant.";
                if(rejected)_modeNote.Text+=" POSTE REFUSÉ EN ROUGE : le personnage illustre la position qui rencontre les éléments rouges ; ce n’est pas un poste accepté.";
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
                if(removal)_hypothesisSummary.Text=(_provenance.SelectedIndex==0?"Hypothèse de retrait : ":"Retrait renseigné : ")
                    +(profile.RemovalSize.X*304.8).ToString("0")+" × "+(profile.RemovalSize.Y*304.8).ToString("0")+" × "+(profile.RemovalSize.Z*304.8).ToString("0")
                    +" mm ; course "+(profile.RemovalTravel*304.8).ToString("0")+" mm. Vérifier la pièce et la notice.";
                else if(profile.Operation==MaintenanceAccessOperation.LeverValve)
                    _hypothesisSummary.Text="Levier : "+(profile.HandleRadius*304.8).ToString("0.#")+" mm · rotation "+profile.RotationDegrees.ToString("0.#")
                        +"°"+(_state.RotationAssumed?" supposée ; sens et butées à confirmer.":" renseignée ; sens et butées à vérifier.");
                if(rejected){profile.OperatorOrigin=_state.RejectedFoot;profile.OperatorPosture=_state.RejectedPosture;profile.BodyYawDegrees=_state.RejectedBodyYawDegrees;}
                if(demo&&profile.Operation==MaintenanceAccessOperation.ChainwheelValve)_modeNote.Text+=" Prise illustrative : "+(profile.WorkHeight*304.8).ToString("0")+" mm au-dessus du sol fictif.";
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
            catch (Exception ex) { _preview.SetScene(Array.Empty<MaintenancePreviewBox>(),Array.Empty<MaintenancePreviewLine>()); _status.Text = "Ajustez les paramètres : " + ex.Message; _status.Foreground = Brushes.DarkOrange; }
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
            => posture==MaintenanceOperatorPosture.Crouched?"Accroupi · posture contraignante à évaluer."
                : Math.Abs(yaw)>1?"Corps tourné de "+yaw.ToString("0",CultureInfo.CurrentCulture)+"° · posture de profil à évaluer."
                : "Debout face à la commande · gabarit réglable, sans certification ergonomique.";
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
            if(Demonstration){_report=null;_status.Text="Démonstration uniquement · choisissez « Dans la maquette » pour vérifier le poste réel.";return;}
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
                    _status.Text="Aucun personnage proposé pour le geste complet.";
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
                _status.Text = _report.Obstacles.Count > 0 ? "Obstacle détecté · " + _report.Obstacles.Count + " conflit(s) sur l’intervention."
                    : incomplete ? "À confirmer · aucun obstacle détecté avec ces hypothèses."
                    : scene.ConstraintNotes.Count>0?"Geste possible géométriquement · posture contraignante, ergonomie à évaluer."
                    : "Libre géométriquement avec les repères et cotes déclarés.";
                _details.Text = string.Join("\n", _report.Obstacles.Take(14).Select(o => "• " + o.VolumeLabel + " : " + o.Label))
                    + "\n" + string.Join("\n", scene.Warnings.Concat(_report.Unknown).Concat(_state.FeatureWarnings).Concat(_state.PlacementWarnings).Distinct().Take(14).Select(s => "• " + s))
                    + "\n" + (_confirmed.IsChecked == true ? "Repères déclarés vérifiés par l’utilisateur." : "Repères et cotes non confirmés.")
                    + "\nCotes : " + (_provenance.SelectedIndex == 1 ? "notice déclarée — " + _notice.Text : _provenance.SelectedIndex==2 ? "géométrie et paramètres de la famille ; courses non documentées à confirmer" : "hypothèses réglables, à mesurer")
                    + "\n" + string.Join("\n", scene.Assumptions) + "\n" + MaintenanceAccessRules.Scope + "\nAperçu : " + _preview.CaptureNote;
                _details.Text+="\n"+string.Join("\n",scene.ConstraintNotes.Concat(_state.PostureWarnings).Concat(_state.PlacementDiagnostics));
                UpdateBlockedElements();
                Draw();
            }
            catch (OperationCanceledException) { _report=null;_status.Text = "Contrôle annulé · aucun résultat valide.";_details.Text="Le contrôle a été interrompu. Aucun résultat précédent n’est conservé.";UpdateBlockedElements();Draw(); }
            catch (Exception ex) { _report=null;_status.Text = "Contrôle impossible : " + ex.Message;_details.Text="Aucun résultat valide. "+ex.Message;UpdateBlockedElements();Draw(); }
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
        internal const string Scope = "Contrôle géométrique du corps articulé et du mouvement renseignés. Une posture de profil ou accroupie peut être possible tout en restant contraignante. Aucun seuil universel de passage ne permet ici de conclure à une conformité. Trajet d’approche, effort/couple, durée, consignation, température, masse, levage et procédure réelle restent à évaluer.";
        internal const string References = "INRS — Conception des lieux et situations de travail : prévoir accès, volumes d’intervention et commandes accessibles.\nhttps://www.inrs.fr/demarche/conception-lieux-situations-travail/dix-points-cles.html\n\nRotork PUB148-015 (06/2023) — boucle continue, guides de chaîne, face de roue vers l’opérateur. La fixation et l’état de la chaîne restent à vérifier.\nhttps://www.rotork.com/en/products/gearbox-accessories/chainwheels\n\nSpirax Sarco Fig 34HP TI-P168-01 — cotes de retrait propres au modèle et au DN. Respecter l’origine des cotes du dessin, sans appliquer un dégagement universel.\nhttps://www.spiraxsarco.com\n\nSAMSON EB8015, chapitre 5 — accès à la commande et espace pour dépose / entretien.\nhttps://www.samsongroup.com/document/e80150en.pdf\n\nISO 14738:2002 exclut les besoins spécifiques de maintenance. ISO 11226 ne valide pas le couple de manœuvre. Aucun seuil d’effort ni aucune conformité normative n’est déduit de ces volumes.\nhttps://www.iso.org/standard/27556.html\nhttps://www.iso.org/standard/25573.html";
    }
}

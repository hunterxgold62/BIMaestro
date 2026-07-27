using Autodesk.Revit.DB;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Modification
{
    public partial class ReservationAutoV3Window : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/modification?outil=auto-reservation";
        public enum HostTarget { Mur, Sol }
        public enum ShapeTarget { Rectangulaire, Circulaire }
        public enum ObjectType { Canalisation, Gaine, Porte, Fenetre, Autre }
        public enum PipeSource { Maquette, LienIFC, LienRVT }

        public HostTarget SelectedHost { get; private set; }
        public ShapeTarget SelectedShape { get; private set; }
        public ObjectType SelectedObject { get; private set; }
        public PipeSource SelectedPipeSource { get; private set; }
        public bool DoubleLinkEnabled { get; private set; }
        public bool AutomatiqueEnabled { get; private set; }
        public bool MultiEnabled { get; private set; }
        public bool NormeEnabled { get; private set; }
        public bool DynamoAutoEnabled { get; private set; }
        public ProfileConfig SelectedExecutionProfile { get; private set; }

        public ReservationAutoV3Config Config { get; private set; }

        private readonly Document _doc;
        private readonly HashSet<string> _allParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ReservationAutoV3PersoConfig _persoConfig;
        private readonly Dictionary<string, ShapeOptionItem> _shapeOptionByLabel = new Dictionary<string, ShapeOptionItem>(StringComparer.OrdinalIgnoreCase);

        private HostTarget _selectedHost = HostTarget.Mur;
        private ShapeTarget _selectedShapeBase = ShapeTarget.Rectangulaire;

        private bool _rectShapeAvailable;
        private bool _circShapeAvailable;
        private bool _isRefreshingShapeOptions;
        private bool _isRefreshingPlacementUi;
        private bool _isRefreshingConfigurationContext;
        private bool _isRestoringLastSelection;

        private GifPlaybackData _murGifData;
        private GifPlaybackData _solGifData;
        private BitmapFrame _murFirstFrame;
        private BitmapFrame _solFirstFrame;

        private GifPlaybackData _shapeRectGifData;
        private GifPlaybackData _shapeCircGifData;
        private BitmapFrame _shapeRectFirstFrame;
        private BitmapFrame _shapeCircFirstFrame;

        private GifPlaybackData _objPipeGifData;
        private GifPlaybackData _objDuctGifData;
        private GifPlaybackData _objDoorGifData;
        private GifPlaybackData _objWindowGifData;
        private GifPlaybackData _objOtherGifData;
        private BitmapFrame _objPipeFirstFrame;
        private BitmapFrame _objDuctFirstFrame;
        private BitmapFrame _objDoorFirstFrame;
        private BitmapFrame _objWindowFirstFrame;
        private BitmapFrame _objOtherFirstFrame;

        private List<LoadedTypeItem> _loadedTypes = new List<LoadedTypeItem>();

        public class LoadedTypeItem
        {
            public FamilySymbol Symbol { get; }
            public string Display { get; }
            public bool IsGenericModel { get; }

            public LoadedTypeItem(FamilySymbol s)
            {
                Symbol = s;
                IsGenericModel = s?.Category?.Id?.IntegerValue == (int)BuiltInCategory.OST_GenericModel;
                string category = s?.Category?.Name ?? "Catégorie inconnue";
                Display = $"{s?.Family?.Name} — {s?.Name}  ·  {category}";
            }
        }

        private class ShapeOptionItem
        {
            public string Label { get; set; }
            public ShapeTarget Shape { get; set; }
            public ProfileConfig Profile { get; set; }
        }

        private class GifPlaybackData
        {
            public List<BitmapSource> Frames { get; } = new List<BitmapSource>();
            public List<TimeSpan> Delays { get; } = new List<TimeSpan>();
        }

        public ReservationAutoV3Window(Document doc, ReservationAutoV3Config cfg)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            _doc = doc;
            Config = cfg ?? new ReservationAutoV3Config();
            _persoConfig = ReservationAutoV3PersoConfigStore.LoadOrDefault();
            _persoConfig.EnsureInitialized();

            comboObjectType.SelectedIndex = 0;
            comboPipeSource.SelectedIndex = 0;
            cbTargetProfile.SelectedIndex = 0;

            chkDefaultNorme.IsChecked = Config.DefaultNormeEnabled;
            chkDefaultDynamo.IsChecked = Config.DefaultDynamoAutoEnabled;
            tbDynamoPath.Text = Config.DynamoPath ?? "";

            chkNorme.IsChecked = Config.DefaultNormeEnabled;
            chkDynamo.IsChecked = Config.DefaultDynamoAutoEnabled;

            tbRfaPath.Text = Config.LastRfaPath ?? "";

            ScheduleGifSelectorLoading();

            RestoreLastExecutionCriteria();

            RefreshConfigurationContext();
            RefreshProfilesSummary();
            RefreshShapeOptions();
            RestoreLastShapeOption();
            UpdateHostSelectorUi();
            UpdateShapeSelectorUi();
            UpdateObjectSelectorUi();
            UpdateMappingPanels();
            RefreshVerticalPlacementUiFromCurrentProfile();
            OnCriteriaChanged(null, null);
        }

        private void RefreshProfilesSummary()
        {
            if (txtConfigProgress == null || txtSelectedConfigStatus == null)
                return;

            var loadedSymbols = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s?.Family != null)
                .ToList();

            var profiles = new[]
            {
                (Profile: _persoConfig.Get(HostTarget.Mur, ShapeTarget.Rectangulaire, false), Unhosted: false),
                (Profile: _persoConfig.Get(HostTarget.Mur, ShapeTarget.Rectangulaire, true), Unhosted: true),
                (Profile: _persoConfig.Get(HostTarget.Mur, ShapeTarget.Circulaire, false), Unhosted: false),
                (Profile: _persoConfig.Get(HostTarget.Mur, ShapeTarget.Circulaire, true), Unhosted: true),
                (Profile: _persoConfig.Get(HostTarget.Sol, ShapeTarget.Rectangulaire, false), Unhosted: false),
                (Profile: _persoConfig.Get(HostTarget.Sol, ShapeTarget.Rectangulaire, true), Unhosted: true),
                (Profile: _persoConfig.Get(HostTarget.Sol, ShapeTarget.Circulaire, false), Unhosted: false),
                (Profile: _persoConfig.Get(HostTarget.Sol, ShapeTarget.Circulaire, true), Unhosted: true)
            };

            bool IsReady(ProfileConfig profile, bool unhosted)
            {
                if (profile?.IsConfigured != true)
                    return false;

                FamilyPlacementType expected = unhosted
                    ? FamilyPlacementType.OneLevelBased
                    : FamilyPlacementType.OneLevelBasedHosted;

                return loadedSymbols.Any(s =>
                    string.Equals(s.Family.Name, profile.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                    s.Family.FamilyPlacementType == expected);
            }

            int ready = profiles.Count(x => IsReady(x.Profile, x.Unhosted));
            txtConfigProgress.Text = $"{ready} configuration{(ready > 1 ? "s" : "")} prête{(ready > 1 ? "s" : "")} sur 8";

            ProfileConfig selected = GetSelectedTargetProfileConfig();
            if (selected == null || !selected.IsConfigured)
            {
                txtSelectedConfigStatus.Text = "Aucune famille utilisateur configurée pour ce cas.";
            }
            else if (!loadedSymbols.Any(s => string.Equals(s.Family.Name, selected.FamilyName, StringComparison.OrdinalIgnoreCase)))
            {
                txtSelectedConfigStatus.Text = $"Configurée mais non chargée dans ce projet : {selected.FamilyName}";
            }
            else if (!IsReady(selected, SelectedConfigUnhosted))
            {
                txtSelectedConfigStatus.Text = $"Chargée mais incompatible avec ce mode d'hébergement : {selected.FamilyName}";
            }
            else
            {
                string type = string.IsNullOrWhiteSpace(selected.TypeName) ? "type automatique" : selected.TypeName;
                txtSelectedConfigStatus.Text = $"Prête : {selected.FamilyName} — {type}";
            }
        }

        public void OnCriteriaChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateObjectSelectorUi();

            string obj = (comboObjectType.SelectedItem as ComboBoxItem)?.Content as string ?? "Canalisation";
            bool isCanal = obj == "Canalisation";
            bool isMepCurve = obj == "Canalisation" || obj == "Gaine";
            bool auto = chkAutomatique?.IsChecked == true;
            bool doubleLinkAvailable = isMepCurve && !auto;

            chkDoubleLink.IsEnabled = doubleLinkAvailable;
            if (!doubleLinkAvailable)
                chkDoubleLink.IsChecked = false;

            bool doubleLink = doubleLinkAvailable && chkDoubleLink.IsChecked == true;
            comboPipeSource.IsEnabled = isMepCurve && !auto && !doubleLink;

            if (!isMepCurve || auto)
                comboPipeSource.SelectedIndex = 0;

            string shape = (comboShape.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            bool isRect = shape.IndexOf("Rectangulaire", StringComparison.OrdinalIgnoreCase) >= 0;

            chkMulti.IsEnabled = !auto && isCanal && isRect && comboShape.IsEnabled;
            if (!chkMulti.IsEnabled) chkMulti.IsChecked = false;
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (_isRestoringLastSelection)
                return;

            OnCriteriaChanged(null, null);
        }

        private void RestoreLastExecutionCriteria()
        {
            _isRestoringLastSelection = true;
            try
            {
                if (Enum.TryParse(Config.LastHostTarget, true, out HostTarget host))
                    _selectedHost = host;

                if (Enum.TryParse(Config.LastShapeTarget, true, out ShapeTarget shape))
                    _selectedShapeBase = shape;

                if (Enum.TryParse(Config.LastObjectType, true, out ObjectType objectType))
                {
                    comboObjectType.SelectedIndex = objectType switch
                    {
                        ObjectType.Canalisation => 0,
                        ObjectType.Gaine => 1,
                        ObjectType.Porte => 2,
                        ObjectType.Fenetre => 3,
                        _ => 4
                    };
                }

                if (Enum.TryParse(Config.LastPipeSource, true, out PipeSource pipeSource))
                {
                    comboPipeSource.SelectedIndex = pipeSource switch
                    {
                        PipeSource.LienIFC => 1,
                        PipeSource.LienRVT => 2,
                        _ => 0
                    };
                }

                chkAutomatique.IsChecked = Config.LastAutomaticEnabled;
                chkDoubleLink.IsChecked = Config.LastDoubleLinkEnabled;
                chkMulti.IsChecked = Config.LastMultiEnabled;
            }
            finally
            {
                _isRestoringLastSelection = false;
            }
        }

        private void RestoreLastShapeOption()
        {
            string savedLabel = Config.LastShapeOptionLabel;
            if (string.IsNullOrWhiteSpace(savedLabel) || !comboShape.IsEnabled)
                return;

            _isRestoringLastSelection = true;
            try
            {
                for (int i = 0; i < comboShape.Items.Count; i++)
                {
                    if (!string.Equals(
                            (comboShape.Items[i] as ComboBoxItem)?.Content as string,
                            savedLabel,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    comboShape.SelectedIndex = i;
                    comboShape.Text = savedLabel;
                    break;
                }
            }
            finally
            {
                _isRestoringLastSelection = false;
            }
        }

        private void RefreshShapeOptions()
        {
            string previous = (comboShape.SelectedItem as ComboBoxItem)?.Content as string;

            RefreshShapeAvailability();

            _isRefreshingShapeOptions = true;
            try
            {
                if (!_rectShapeAvailable && !_circShapeAvailable)
                {
                    _selectedShapeBase = ShapeTarget.Rectangulaire;
                    _shapeOptionByLabel.Clear();
                    comboShape.Items.Clear();
                    comboShape.Items.Add(new ComboBoxItem { Content = "(Aucune famille disponible)" });
                    comboShape.SelectedIndex = 0;
                    comboShape.IsEnabled = false;
                    comboShape.Text = string.Empty;
                    UpdateShapeSelectorUi();
                    OnCriteriaChanged(null, null);
                    return;
                }

                if (_selectedShapeBase == ShapeTarget.Rectangulaire && !_rectShapeAvailable)
                    _selectedShapeBase = ShapeTarget.Circulaire;
                else if (_selectedShapeBase == ShapeTarget.Circulaire && !_circShapeAvailable)
                    _selectedShapeBase = ShapeTarget.Rectangulaire;

                _shapeOptionByLabel.Clear();
                comboShape.Items.Clear();

                foreach (var option in GetAvailableShapeOptions(_selectedHost, _selectedShapeBase))
                {
                    _shapeOptionByLabel[option.Label] = option;
                    comboShape.Items.Add(new ComboBoxItem { Content = option.Label });
                }

                if (comboShape.Items.Count == 0)
                {
                    comboShape.Items.Add(new ComboBoxItem { Content = "(Aucune famille disponible)" });
                    comboShape.SelectedIndex = 0;
                    comboShape.IsEnabled = false;
                    comboShape.Text = string.Empty;
                }
                else
                {
                    comboShape.IsEnabled = true;

                    int idx = 0;
                    if (!string.IsNullOrWhiteSpace(previous))
                    {
                        for (int i = 0; i < comboShape.Items.Count; i++)
                        {
                            if ((comboShape.Items[i] as ComboBoxItem)?.Content as string == previous)
                            {
                                idx = i;
                                break;
                            }
                        }
                    }

                    comboShape.SelectedIndex = idx;

                    if (comboShape.SelectedItem is ComboBoxItem selectedItem &&
                        selectedItem.Content is string selectedLabel)
                    {
                        comboShape.Text = selectedLabel;
                    }
                    else
                    {
                        comboShape.Text = string.Empty;
                    }
                }
            }
            finally
            {
                _isRefreshingShapeOptions = false;
            }

            UpdateShapeSelectorUi();
            OnCriteriaChanged(null, null);
        }

        private void RefreshShapeAvailability()
        {
            _rectShapeAvailable = HasAnyLoadedProfile(_selectedHost, ShapeTarget.Rectangulaire);
            _circShapeAvailable = HasAnyLoadedProfile(_selectedHost, ShapeTarget.Circulaire);
        }

        private bool HasAnyLoadedProfile(HostTarget host, ShapeTarget shape)
        {
            return GetAvailableShapeOptions(host, shape).Any();
        }

        private IEnumerable<ShapeOptionItem> GetAvailableShapeOptions(HostTarget host, ShapeTarget shape)
        {
            foreach (var option in CreateShapeOptionItems(host, shape))
            {
                if (option?.Profile == null) continue;
                if (string.IsNullOrWhiteSpace(option.Profile.FamilyName)) continue;
                if (!IsProfileLoadedInProject(option.Profile)) continue;
                yield return option;
            }
        }

        private IEnumerable<ShapeOptionItem> CreateShapeOptionItems(HostTarget host, ShapeTarget shape)
        {
            yield return new ShapeOptionItem
            {
                Label = $"{GetShapePrefix(shape)} - Ma famille avec hôte",
                Shape = shape,
                Profile = _persoConfig.Get(host, shape, false)
            };

            yield return new ShapeOptionItem
            {
                Label = $"{GetShapePrefix(shape)} - Ma famille sans hôte",
                Shape = shape,
                Profile = _persoConfig.Get(host, shape, true)
            };

            yield return new ShapeOptionItem
            {
                Label = $"{GetShapePrefix(shape)} - BIMaestro avec hôte",
                Shape = shape,
                Profile = FindBuiltInProfile(host, shape, isV2: false)
            };

            yield return new ShapeOptionItem
            {
                Label = $"{GetShapePrefix(shape)} - BIMaestro sans hôte",
                Shape = shape,
                Profile = FindBuiltInProfile(host, shape, isV2: true)
            };
        }

        private static string GetShapePrefix(ShapeTarget shape)
            => shape == ShapeTarget.Circulaire ? "Circulaire" : "Rectangulaire";

        private bool IsProfileLoadedInProject(ProfileConfig profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.FamilyName)) return false;

            return new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Any(s => s?.Family?.Name != null &&
                          string.Equals(s.Family.Name, profile.FamilyName, StringComparison.OrdinalIgnoreCase));
        }

        private FamilySymbol FindProfileSymbol(ProfileConfig profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.FamilyName))
                return null;

            var symbols = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s?.Family?.Name != null &&
                            string.Equals(s.Family.Name, profile.FamilyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!string.IsNullOrWhiteSpace(profile.TypeName))
            {
                var exact = symbols.FirstOrDefault(s =>
                    string.Equals(s.Name, profile.TypeName, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                    return exact;
            }

            return symbols.FirstOrDefault();
        }

        private bool IsSelectedProfileUnhosted()
        {
            FamilySymbol symbol = FindProfileSymbol(SelectedExecutionProfile);
            return symbol?.Family?.FamilyPlacementType == FamilyPlacementType.OneLevelBased;
        }

        private ProfileConfig FindBuiltInProfile(HostTarget host, ShapeTarget shape, bool isV2)
        {
            string[] candidates;

            if (host == HostTarget.Mur && shape == ShapeTarget.Rectangulaire)
                candidates = isV2
                    ? new[] { "CML_Réservation rectangulaire verticale", "CML_Réservation rectangulaire murale" }
                    : new[] { "Réservation rectangulaire murale" };
            else if (host == HostTarget.Sol && shape == ShapeTarget.Rectangulaire)
                candidates = isV2
                    ? new[] { "CML_Réservation rectangulaire horizontale", "CML_Réservation rectangulaire sol" }
                    : new[] { "Réservation rectangulaire sol" };
            else if (host == HostTarget.Mur && shape == ShapeTarget.Circulaire)
                candidates = isV2
                    ? new[] { "CML_Réservation circulaire verticale", "CML_Réservation circulaire murale" }
                    : new[] { "Réservation circulaire murale" };
            else
                candidates = isV2
                    ? new[] { "CML_Réservation circulaire horizontale", "CML_Réservation circulaire sol" }
                    : new[] { "Réservation circulaire sol" };

            foreach (var candidate in candidates)
            {
                var symbol = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => s?.Family?.Name != null &&
                                         FamilyNameContains(s.Family.Name, candidate));
                if (symbol == null) continue;

                return new ProfileConfig
                {
                    FamilyName = symbol.Family.Name,
                    TypeName = symbol.Name,
                    ParamLength = shape == ShapeTarget.Rectangulaire
                        ? (host == HostTarget.Mur ? Config.WallRect.ParamLength : Config.FloorRect.ParamLength)
                        : "",
                    ParamHeight = host == HostTarget.Mur && shape == ShapeTarget.Rectangulaire ? Config.WallRect.ParamHeight : "",
                    ParamWidth = host == HostTarget.Sol && shape == ShapeTarget.Rectangulaire ? Config.FloorRect.ParamWidth : "",
                    ParamDiameter = shape == ShapeTarget.Circulaire
                        ? (host == HostTarget.Mur ? Config.WallCirc.ParamDiameter : Config.FloorCirc.ParamDiameter)
                        : "",
                    ParamDepth = shape == ShapeTarget.Circulaire
                        ? (host == HostTarget.Mur ? Config.WallCirc.ParamDepth : Config.FloorCirc.ParamDepth)
                        : (host == HostTarget.Mur ? Config.WallRect.ParamDepth : Config.FloorRect.ParamDepth),
                    VerticalPlacementMode = GetExistingPlacementMode(host, shape),
                    VerticalPlacementOffsetMm = GetExistingPlacementOffset(host, shape)
                };
            }

            return null;
        }

        private VerticalPlacementMode GetExistingPlacementMode(HostTarget host, ShapeTarget shape)
        {
            var existing = GetCurrentProfileFromConfig(host, shape);
            return existing?.VerticalPlacementMode ?? VerticalPlacementMode.Auto;
        }

        private double GetExistingPlacementOffset(HostTarget host, ShapeTarget shape)
        {
            var existing = GetCurrentProfileFromConfig(host, shape);
            return existing?.VerticalPlacementOffsetMm ?? 0.0;
        }

        private ProfileConfig GetCurrentProfileFromConfig(HostTarget host, ShapeTarget shape)
        {
            return (host, shape) switch
            {
                (HostTarget.Mur, ShapeTarget.Rectangulaire) => Config.WallRect,
                (HostTarget.Mur, ShapeTarget.Circulaire) => Config.WallCirc,
                (HostTarget.Sol, ShapeTarget.Rectangulaire) => Config.FloorRect,
                (HostTarget.Sol, ShapeTarget.Circulaire) => Config.FloorCirc,
                _ => null
            };
        }

        private static bool FamilyNameContains(string currentFamilyName, string expectedName)
        {
            string current = RemoveCmlPrefix(currentFamilyName);
            string expected = RemoveCmlPrefix(expectedName);
            return current.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RemoveCmlPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.StartsWith("CML_", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(4)
                : value;
        }

        private void ScheduleGifSelectorLoading()
        {
            ContentRendered += LoadGifSelectorsAfterFirstRender;
        }

        private async void LoadGifSelectorsAfterFirstRender(object sender, EventArgs e)
        {
            ContentRendered -= LoadGifSelectorsAfterFirstRender;

            string murResource = FindBestGifResource("mur", null);
            string solResource = FindBestGifResource("sol", murResource);

            await Task.WhenAll(
                LoadGifSelectorAsync(imgHostMur, txtHostMurFallback, murResource,
                    (data, frame) => { _murGifData = data; _murFirstFrame = frame; }),
                LoadGifSelectorAsync(imgHostSol, txtHostSolFallback, solResource,
                    (data, frame) => { _solGifData = data; _solFirstFrame = frame; }),
                LoadGifSelectorAsync(imgShapeRect, txtShapeRectFallback, FindBestGifResource("rect", null),
                    (data, frame) => { _shapeRectGifData = data; _shapeRectFirstFrame = frame; }),
                LoadGifSelectorAsync(imgShapeCirc, txtShapeCircFallback, FindBestGifResource("circ", null),
                    (data, frame) => { _shapeCircGifData = data; _shapeCircFirstFrame = frame; }),
                LoadGifSelectorAsync(imgObjPipe, txtObjPipeFallback, FindBestGifResource("cana", null),
                    (data, frame) => { _objPipeGifData = data; _objPipeFirstFrame = frame; }),
                LoadGifSelectorAsync(imgObjDuct, txtObjDuctFallback, FindBestGifResource("gaine", null),
                    (data, frame) => { _objDuctGifData = data; _objDuctFirstFrame = frame; }),
                LoadGifSelectorAsync(imgObjDoor, txtObjDoorFallback, FindBestGifResource("porte", null),
                    (data, frame) => { _objDoorGifData = data; _objDoorFirstFrame = frame; }),
                LoadGifSelectorAsync(imgObjWindow, txtObjWindowFallback, FindBestGifResource("fenetre", null),
                    (data, frame) => { _objWindowGifData = data; _objWindowFirstFrame = frame; }),
                LoadGifSelectorAsync(imgObjOther, txtObjOtherFallback, FindBestGifResource("autre", null),
                    (data, frame) => { _objOtherGifData = data; _objOtherFirstFrame = frame; }));
        }

        private string FindBestGifResource(string keyword, string excludedResource)
        {
            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames();

            return names.FirstOrDefault(n =>
                n.IndexOf(".Resources.", StringComparison.OrdinalIgnoreCase) >= 0 &&
                n.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) &&
                n.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 &&
                !string.Equals(n, excludedResource, StringComparison.OrdinalIgnoreCase));
        }

        private async Task LoadGifSelectorAsync(
            Image image,
            TextBlock fallbackText,
            string resourceName,
            Action<GifPlaybackData, BitmapFrame> assign)
        {
            if (image == null || string.IsNullOrWhiteSpace(resourceName))
                return;

            try
            {
                var result = await Task.Run(() => LoadGifResource(resourceName));
                if (result.Data?.Frames == null || result.Data.Frames.Count == 0 || !IsLoaded)
                    return;

                assign?.Invoke(result.Data, result.FirstFrame);
                image.Source = result.FirstFrame;
                if (fallbackText != null)
                    fallbackText.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch
            {
            }
        }

        private static (GifPlaybackData Data, BitmapFrame FirstFrame) LoadGifResource(string resourceName)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (Stream stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    return (null, null);

                GifPlaybackData data = BuildGifPlaybackData(stream);
                if (data?.Frames == null || data.Frames.Count == 0)
                    return (null, null);

                BitmapFrame firstFrame = BitmapFrame.Create(data.Frames[0]);
                if (firstFrame.CanFreeze)
                    firstFrame.Freeze();

                return (data, firstFrame);
            }
        }

        private static GifPlaybackData BuildGifPlaybackData(Stream stream)
        {
            var data = new GifPlaybackData();
            using (var gif = System.Drawing.Image.FromStream(stream, true, true))
            {
                var frameDimension = new System.Drawing.Imaging.FrameDimension(gif.FrameDimensionsList[0]);
                int frameCount = gif.GetFrameCount(frameDimension);
                var delays = ExtractFrameDelays(gif, frameCount);

                for (int i = 0; i < frameCount; i++)
                {
                    gif.SelectActiveFrame(frameDimension, i);
                    using (var bmp = new System.Drawing.Bitmap(gif.Width, gif.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.Clear(System.Drawing.Color.Transparent);
                        g.DrawImage(gif, 0, 0, gif.Width, gif.Height);

                        var source = ToBitmapSource(bmp);
                        data.Frames.Add(source);
                        data.Delays.Add(delays[i]);
                    }
                }
            }

            return data;
        }

        private static List<TimeSpan> ExtractFrameDelays(System.Drawing.Image gif, int frameCount)
        {
            const int FrameDelayPropertyId = 0x5100;
            var result = Enumerable.Repeat(TimeSpan.FromMilliseconds(100), frameCount).ToList();

            try
            {
                var item = gif.GetPropertyItem(FrameDelayPropertyId);
                if (item?.Value == null || item.Value.Length < 4) return result;

                for (int i = 0; i < frameCount; i++)
                {
                    int offset = i * 4;
                    if (offset + 3 >= item.Value.Length) break;
                    int delayCs = BitConverter.ToInt32(item.Value, offset);
                    if (delayCs <= 0) delayCs = 10;
                    result[i] = TimeSpan.FromMilliseconds(delayCs * 10);
                }
            }
            catch
            {
            }

            return result;
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        private static void PlayGifOnce(Image image, GifPlaybackData gifData, BitmapFrame firstFrame)
        {
            if (image == null || gifData?.Frames == null || gifData.Frames.Count == 0) return;

            image.BeginAnimation(Image.SourceProperty, null);
            if (firstFrame != null) image.Source = firstFrame;

            var animation = new ObjectAnimationUsingKeyFrames();
            TimeSpan total = TimeSpan.Zero;
            for (int i = 0; i < gifData.Frames.Count; i++)
            {
                var frame = gifData.Frames[i];
                var delay = i < gifData.Delays.Count ? gifData.Delays[i] : TimeSpan.FromMilliseconds(100);
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frame, KeyTime.FromTimeSpan(total)));
                total += delay;
            }

            if (total <= TimeSpan.Zero) total = TimeSpan.FromSeconds(1);

            animation.Duration = total;
            animation.RepeatBehavior = new RepeatBehavior(1);
            animation.FillBehavior = FillBehavior.Stop;
            animation.Completed += (s, e) =>
            {
                image.BeginAnimation(Image.SourceProperty, null);
                if (firstFrame != null)
                    image.Source = firstFrame;
            };

            image.BeginAnimation(Image.SourceProperty, animation);
        }

        private void SelectHost(HostTarget host)
        {
            _selectedHost = host;
            UpdateHostSelectorUi();
            RefreshShapeOptions();
            OnCriteriaChanged(null, null);
        }

        private void SelectShapeBase(ShapeTarget shape)
        {
            if (shape == ShapeTarget.Rectangulaire && !_rectShapeAvailable) return;
            if (shape == ShapeTarget.Circulaire && !_circShapeAvailable) return;

            _selectedShapeBase = shape;
            UpdateShapeSelectorUi();
            RefreshShapeOptions();
            OnCriteriaChanged(null, null);
        }

        private void SelectObjectType(ObjectType objectType)
        {
            comboObjectType.SelectedIndex = objectType switch
            {
                ObjectType.Canalisation => 0,
                ObjectType.Gaine => 1,
                ObjectType.Porte => 2,
                ObjectType.Fenetre => 3,
                _ => 4
            };

            UpdateObjectSelectorUi();
            OnCriteriaChanged(null, null);
        }

        private void UpdateHostSelectorUi()
        {
            bool murSelected = _selectedHost == HostTarget.Mur;
            hostMurCard.BorderBrush = murSelected ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
            hostSolCard.BorderBrush = !murSelected ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
        }

        private void UpdateShapeSelectorUi()
        {
            ApplyShapeCardState(shapeRectCard, _selectedShapeBase == ShapeTarget.Rectangulaire, _rectShapeAvailable);
            ApplyShapeCardState(shapeCircCard, _selectedShapeBase == ShapeTarget.Circulaire, _circShapeAvailable);
        }

        private void ApplyShapeCardState(Border card, bool isSelected, bool isEnabled)
        {
            if (card == null) return;

            Brush selectedBrush = new SolidColorBrush(Color.FromRgb(53, 182, 121));
            Brush normalBorder = (Brush)FindResource("Border");
            Brush disabledBorder = (Brush)FindResource("Divider");
            Brush enabledBackground = (Brush)FindResource("Surface");
            Brush disabledBackground = (Brush)FindResource("Surface.Subtle");

            card.BorderBrush = !isEnabled ? disabledBorder : (isSelected ? selectedBrush : normalBorder);
            card.Background = isEnabled ? enabledBackground : disabledBackground;
            card.Opacity = isEnabled ? 1.0 : 0.45;
            card.IsHitTestVisible = isEnabled;
            card.Cursor = isEnabled ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        }

        private void UpdateObjectSelectorUi()
        {
            var selected = (comboObjectType.SelectedItem as ComboBoxItem)?.Content as string ?? "Canalisation";

            objPipeCard.BorderBrush = selected == "Canalisation" ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
            objDuctCard.BorderBrush = selected == "Gaine" ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
            objDoorCard.BorderBrush = selected == "Porte" ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
            objWindowCard.BorderBrush = selected == "Fenêtre" ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
            objOtherCard.BorderBrush = selected == "Autre" ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
        }

        private void OnMurHostClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectHost(HostTarget.Mur);
            PlayGifOnce(imgHostMur, _murGifData, _murFirstFrame);
        }

        private void OnSolHostClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectHost(HostTarget.Sol);
            PlayGifOnce(imgHostSol, _solGifData, _solFirstFrame);
        }

        private void OnMurGifHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(hostMurCard);
            PlayGifOnce(imgHostMur, _murGifData, _murFirstFrame);
        }

        private void OnSolGifHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(hostSolCard);
            PlayGifOnce(imgHostSol, _solGifData, _solFirstFrame);
        }

        private void OnShapeRectClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_rectShapeAvailable) return;
            SelectShapeBase(ShapeTarget.Rectangulaire);
            PlayGifOnce(imgShapeRect, _shapeRectGifData, _shapeRectFirstFrame);
        }

        private void OnShapeCircClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_circShapeAvailable) return;
            SelectShapeBase(ShapeTarget.Circulaire);
            PlayGifOnce(imgShapeCirc, _shapeCircGifData, _shapeCircFirstFrame);
        }

        private void OnShapeRectHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_rectShapeAvailable) return;
            StartPulse(shapeRectCard);
            PlayGifOnce(imgShapeRect, _shapeRectGifData, _shapeRectFirstFrame);
        }

        private void OnShapeCircHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_circShapeAvailable) return;
            StartPulse(shapeCircCard);
            PlayGifOnce(imgShapeCirc, _shapeCircGifData, _shapeCircFirstFrame);
        }

        private void OnShapeVariantChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingShapeOptions || _isRestoringLastSelection)
                return;

            string label = (comboShape.SelectedItem as ComboBoxItem)?.Content as string;
            if (string.IsNullOrWhiteSpace(label))
                return;

            if (label.IndexOf("Circulaire", StringComparison.OrdinalIgnoreCase) >= 0 && _selectedShapeBase != ShapeTarget.Circulaire)
            {
                _selectedShapeBase = ShapeTarget.Circulaire;
                UpdateShapeSelectorUi();
            }
            else if (label.IndexOf("Rectangulaire", StringComparison.OrdinalIgnoreCase) >= 0 && _selectedShapeBase != ShapeTarget.Rectangulaire)
            {
                _selectedShapeBase = ShapeTarget.Rectangulaire;
                UpdateShapeSelectorUi();
            }

            OnCriteriaChanged(null, null);
        }

        private void OnObjPipeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectObjectType(ObjectType.Canalisation);
            PlayGifOnce(imgObjPipe, _objPipeGifData, _objPipeFirstFrame);
        }

        private void OnObjDuctClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectObjectType(ObjectType.Gaine);
            PlayGifOnce(imgObjDuct, _objDuctGifData, _objDuctFirstFrame);
        }

        private void OnObjDoorClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectObjectType(ObjectType.Porte);
            PlayGifOnce(imgObjDoor, _objDoorGifData, _objDoorFirstFrame);
        }

        private void OnObjWindowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectObjectType(ObjectType.Fenetre);
            PlayGifOnce(imgObjWindow, _objWindowGifData, _objWindowFirstFrame);
        }

        private void OnObjOtherClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectObjectType(ObjectType.Autre);
            PlayGifOnce(imgObjOther, _objOtherGifData, _objOtherFirstFrame);
        }

        private void OnObjPipeHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(objPipeCard);
            PlayGifOnce(imgObjPipe, _objPipeGifData, _objPipeFirstFrame);
        }

        private void OnObjDuctHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(objDuctCard);
            PlayGifOnce(imgObjDuct, _objDuctGifData, _objDuctFirstFrame);
        }

        private void OnObjDoorHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(objDoorCard);
            PlayGifOnce(imgObjDoor, _objDoorGifData, _objDoorFirstFrame);
        }

        private void OnObjWindowHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(objWindowCard);
            PlayGifOnce(imgObjWindow, _objWindowGifData, _objWindowFirstFrame);
        }

        private void OnObjOtherHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(objOtherCard);
            PlayGifOnce(imgObjOther, _objOtherGifData, _objOtherFirstFrame);
        }

        private static void StartPulse(Border border)
        {
            if (border == null) return;

            var pulse = new DoubleAnimation
            {
                From = 1.0,
                To = 0.88,
                Duration = TimeSpan.FromMilliseconds(140),
                AutoReverse = true,
                FillBehavior = FillBehavior.Stop
            };
            border.BeginAnimation(UIElement.OpacityProperty, pulse);
        }

        private void OnBrowseRfa(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Famille Revit (*.rfa)|*.rfa",
                Title = "Sélectionner une famille de réservation (.rfa)"
            };

            if (!string.IsNullOrWhiteSpace(tbRfaPath.Text))
            {
                try
                {
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(tbRfaPath.Text);
                }
                catch
                {
                }
            }

            if (dlg.ShowDialog() == true)
            {
                tbRfaPath.Text = dlg.FileName;
                Config.LastRfaPath = dlg.FileName;
            }
        }

        private void OnBrowseDynamo(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Script Dynamo (*.dyn)|*.dyn",
                Title = "Sélectionner le script Dynamo"
            };

            string currentPath = tbDynamoPath?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                try
                {
                    string folder = System.IO.Path.GetDirectoryName(currentPath);
                    if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                        dlg.InitialDirectory = folder;
                }
                catch
                {
                }
            }

            if (dlg.ShowDialog() == true)
                tbDynamoPath.Text = dlg.FileName;
        }

        private void OnLoadRfa(object sender, RoutedEventArgs e)
        {
            string path = tbRfaPath.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("Sélectionne un fichier .RFA valide.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                using (var t = new Transaction(_doc, "Charger famille réservation (V3)"))
                {
                    t.Start();

                    if (!_doc.LoadFamily(path, new NoPromptFamilyLoadOptions(), out var fam))
                    {
                        t.RollBack();
                        MessageBox.Show("Impossible de charger la famille (LoadFamily a échoué).", "BIMaestro",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    t.Commit();

                    var familySymbols = GetSymbolsFromFamily(_doc, fam);
                    var preferred = familySymbols.FirstOrDefault(s => IsCompatibleWithSelectedHosting(s));
                    CollectAllParameterNames(path, familySymbols);
                    RefreshAvailableFamilyTypes(preferred);

                    RefreshProfilesSummary();
                    RefreshShapeOptions();
                    RefreshVerticalPlacementUiFromCurrentProfile();
                    FillParamCombosFromSelectedSymbol();

                    if (preferred == null)
                    {
                        string expected = SelectedConfigUnhosted ? "sans hôte, basée sur un niveau" : "hébergée par un niveau";
                        MessageBox.Show($"La famille est chargée, mais aucun de ses types n'est compatible avec le mode {expected}.",
                            "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show("Famille chargée. Vérifie les paramètres puis applique la configuration.",
                            "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur chargement famille : " + ex.Message, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static List<FamilySymbol> GetSymbolsFromFamily(Document doc, Family fam)
        {
            var list = new List<FamilySymbol>();
            if (doc == null || fam == null) return list;

            foreach (var id in fam.GetFamilySymbolIds())
            {
                if (doc.GetElement(id) is FamilySymbol fs)
                    list.Add(fs);
            }
            return list;
        }

        private void OnTargetProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingConfigurationContext)
                return;

            UpdateMappingPanels();
            RefreshVerticalPlacementUiFromCurrentProfile();
        }

        private HostTarget SelectedConfigHost
            => cbConfigSupport?.SelectedIndex == 1 ? HostTarget.Sol : HostTarget.Mur;

        private ShapeTarget SelectedConfigShape
            => cbConfigShape?.SelectedIndex == 1 ? ShapeTarget.Circulaire : ShapeTarget.Rectangulaire;

        private bool SelectedConfigUnhosted
            => cbConfigHosting?.SelectedIndex == 1;

        private void OnConfigContextChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingConfigurationContext || cbTargetProfile == null || cbLoadedType == null)
                return;

            RefreshConfigurationContext();
        }

        private void RefreshConfigurationContext()
        {
            if (cbTargetProfile == null || cbLoadedType == null)
                return;

            _isRefreshingConfigurationContext = true;
            try
            {
                int targetIndex = SelectedConfigHost == HostTarget.Mur
                    ? (SelectedConfigShape == ShapeTarget.Rectangulaire ? 0 : 1)
                    : (SelectedConfigShape == ShapeTarget.Rectangulaire ? 2 : 3);

                cbTargetProfile.SelectedIndex = targetIndex;
                RefreshAvailableFamilyTypes();
                UpdateMappingPanels();
                RefreshVerticalPlacementUiFromCurrentProfile();
                RefreshProfilesSummary();
            }
            finally
            {
                _isRefreshingConfigurationContext = false;
            }
        }

        private void RefreshAvailableFamilyTypes(FamilySymbol preferredSymbol = null)
        {
            if (_doc == null || cbLoadedType == null)
                return;

            _loadedTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(IsCompatibleWithSelectedHosting)
                .Select(s => new LoadedTypeItem(s))
                .OrderBy(x => x.IsGenericModel ? 0 : 1)
                .ThenBy(x => x.Symbol?.Category?.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Symbol?.Family?.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Symbol?.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            cbLoadedType.ItemsSource = null;
            cbLoadedType.ItemsSource = _loadedTypes;

            int selectedIndex = preferredSymbol == null
                ? ResolvePreferredLoadedTypeIndex(_loadedTypes, GetSelectedTargetProfileConfig())
                : _loadedTypes.FindIndex(x => x.Symbol?.Id == preferredSymbol.Id);

            cbLoadedType.SelectedIndex = selectedIndex >= 0 ? selectedIndex : (_loadedTypes.Count > 0 ? 0 : -1);
        }

        private bool IsCompatibleWithSelectedHosting(FamilySymbol symbol)
        {
            if (symbol?.Family == null)
                return false;

            FamilyPlacementType placement = symbol.Family.FamilyPlacementType;
            return SelectedConfigUnhosted
                ? placement == FamilyPlacementType.OneLevelBased
                : placement == FamilyPlacementType.OneLevelBasedHosted;
        }

        private void OnLoadedTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            FillParamCombosFromSelectedSymbol();
            SuggestMappingsForSelectedSymbol();
        }

        private void OnVerticalPlacementChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingPlacementUi)
                return;
        }

        private void UpdateMappingPanels()
        {
            panelMapRectWall.Visibility = System.Windows.Visibility.Collapsed;
            panelMapCircWall.Visibility = System.Windows.Visibility.Collapsed;
            panelMapRectFloor.Visibility = System.Windows.Visibility.Collapsed;
            panelMapCircFloor.Visibility = System.Windows.Visibility.Collapsed;

            int idx = cbTargetProfile.SelectedIndex;
            switch (idx)
            {
                case 0:
                    panelMapRectWall.Visibility = System.Windows.Visibility.Visible;
                    break;
                case 1:
                    panelMapCircWall.Visibility = System.Windows.Visibility.Visible;
                    break;
                case 2:
                    panelMapRectFloor.Visibility = System.Windows.Visibility.Visible;
                    break;
                case 3:
                    panelMapCircFloor.Visibility = System.Windows.Visibility.Visible;
                    break;
            }
            ApplyMappingFromSelectedProfileToUi();
            FillParamCombosFromSelectedSymbol();
            SuggestMappingsForSelectedSymbol();
        }

        private void FillParamCombosFromSelectedSymbol()
        {
            var it = cbLoadedType.SelectedItem as LoadedTypeItem;
            var sym = it?.Symbol;
            _allParameterNames.Clear();

            if (sym != null)
            {
                CollectParameterNamesFromElement(sym, _allParameterNames);

                foreach (var familySymbol in GetSymbolsFromFamily(_doc, sym.Family))
                    CollectParameterNamesFromElement(familySymbol, _allParameterNames);

                foreach (var instance in new FilteredElementCollector(_doc)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(x => x.Symbol?.Family?.Id == sym.Family.Id))
                {
                    CollectParameterNamesFromElement(instance, _allParameterNames);
                }
            }

            var names = _allParameterNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n)
                .ToList();

            void fill(ComboBox cb)
            {
                if (cb == null) return;
                string previous = cb.Text;
                cb.ItemsSource = names;
                if (!string.IsNullOrWhiteSpace(previous) && names.Contains(previous))
                    cb.Text = previous;
            }

            fill(cbMapWallLen);
            fill(cbMapWallHeight);
            fill(cbMapWallDepth);
            fill(cbMapWallDiam);
            fill(cbMapWallDepth2);

            fill(cbMapFloorLen);
            fill(cbMapFloorWidth);
            fill(cbMapFloorDepth);
            fill(cbMapFloorDiam);
            fill(cbMapFloorDepth2);
        }

        private void SuggestMappingsForSelectedSymbol()
        {
            if (_allParameterNames.Count == 0)
                return;

            void suggest(ComboBox combo, params string[] candidates)
            {
                if (combo == null || !string.IsNullOrWhiteSpace(combo.Text))
                    return;

                string exact = _allParameterNames.FirstOrDefault(name =>
                    candidates.Any(candidate => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)));
                string partial = exact ?? _allParameterNames.FirstOrDefault(name =>
                    candidates.Any(candidate => name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0));

                if (!string.IsNullOrWhiteSpace(partial))
                    combo.Text = partial;
            }

            int idx = cbTargetProfile.SelectedIndex;
            if (idx == 0)
            {
                suggest(cbMapWallLen, "Longueur", "Length");
                suggest(cbMapWallHeight, "Hauteur", "Height");
                suggest(cbMapWallDepth, "Profondeur", "Depth", "Épaisseur", "Epaisseur", "Thickness");
            }
            else if (idx == 1)
            {
                suggest(cbMapWallDiam, "Diamètre", "Diametre", "Diameter", "Diam");
                suggest(cbMapWallDepth2, "Profondeur", "Depth", "Épaisseur", "Epaisseur", "Thickness");
            }
            else if (idx == 2)
            {
                suggest(cbMapFloorLen, "Longueur", "Length");
                suggest(cbMapFloorWidth, "Largeur", "Width");
                suggest(cbMapFloorDepth, "Profondeur", "Depth", "Épaisseur", "Epaisseur", "Thickness");
            }
            else if (idx == 3)
            {
                suggest(cbMapFloorDiam, "Diamètre", "Diametre", "Diameter", "Diam");
                suggest(cbMapFloorDepth2, "Profondeur", "Depth", "Épaisseur", "Epaisseur", "Thickness");
            }
        }

        private int ResolvePreferredLoadedTypeIndex(List<LoadedTypeItem> loadedTypes, ProfileConfig profile)
        {
            if (loadedTypes == null || loadedTypes.Count == 0)
                return -1;

            if (profile == null || string.IsNullOrWhiteSpace(profile.FamilyName))
                return 0;

            int sameFamilyAndType = loadedTypes.FindIndex(x =>
                x?.Symbol?.Family?.Name.Equals(profile.FamilyName, StringComparison.OrdinalIgnoreCase) == true &&
                (string.IsNullOrWhiteSpace(profile.TypeName) ||
                 x.Symbol.Name.Equals(profile.TypeName, StringComparison.OrdinalIgnoreCase)));
            if (sameFamilyAndType >= 0)
                return sameFamilyAndType;

            int sameFamily = loadedTypes.FindIndex(x =>
                x?.Symbol?.Family?.Name.Equals(profile.FamilyName, StringComparison.OrdinalIgnoreCase) == true);
            return sameFamily >= 0 ? sameFamily : 0;
        }

        private void ApplyMappingFromSelectedProfileToUi()
        {
            var profile = GetSelectedTargetProfileConfig();
            if (profile == null)
                return;

            int idx = cbTargetProfile.SelectedIndex;
            if (idx == 0)
            {
                cbMapWallLen.Text = profile.ParamLength ?? "";
                cbMapWallHeight.Text = profile.ParamHeight ?? "";
                cbMapWallDepth.Text = profile.ParamDepth ?? "";
            }
            else if (idx == 1)
            {
                cbMapWallDiam.Text = profile.ParamDiameter ?? "";
                cbMapWallDepth2.Text = profile.ParamDepth ?? "";
            }
            else if (idx == 2)
            {
                cbMapFloorLen.Text = profile.ParamLength ?? "";
                cbMapFloorWidth.Text = profile.ParamWidth ?? "";
                cbMapFloorDepth.Text = profile.ParamDepth ?? "";
            }
            else if (idx == 3)
            {
                cbMapFloorDiam.Text = profile.ParamDiameter ?? "";
                cbMapFloorDepth2.Text = profile.ParamDepth ?? "";
            }
        }
        private void CollectAllParameterNames(string rfaPath, IEnumerable<FamilySymbol> symbols)
        {
            _allParameterNames.Clear();

            foreach (var symbol in symbols ?? Enumerable.Empty<FamilySymbol>())
                CollectParameterNamesFromElement(symbol, _allParameterNames);

            foreach (var name in GetParameterNamesFromFamilyDocument(rfaPath))
                _allParameterNames.Add(name);
        }

        private static void CollectParameterNamesFromElement(Element element, ISet<string> output)
        {
            if (element == null || output == null) return;

            foreach (Parameter p in element.Parameters)
            {
                string name = p?.Definition?.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    output.Add(name);
            }
        }

        private IEnumerable<string> GetParameterNamesFromFamilyDocument(string rfaPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(rfaPath) || !System.IO.File.Exists(rfaPath))
                return result;

            Document familyDoc = null;
            try
            {
                familyDoc = _doc.Application.OpenDocumentFile(rfaPath);
                if (!familyDoc.IsFamilyDocument)
                    return result;

                var manager = familyDoc.FamilyManager;
                if (manager == null)
                    return result;

                foreach (FamilyParameter p in manager.Parameters)
                {
                    if (!string.IsNullOrWhiteSpace(p?.Definition?.Name))
                        result.Add(p.Definition.Name);
                }
            }
            catch
            {
            }
            finally
            {
                familyDoc?.Close(false);
            }

            return result;
        }

        private void RefreshVerticalPlacementUiFromCurrentProfile()
        {
            var profile = GetSelectedTargetProfileConfig();
            _isRefreshingPlacementUi = true;
            try
            {
                cbVerticalReference.SelectedIndex = profile?.VerticalPlacementMode switch
                {
                    VerticalPlacementMode.Center => 1,
                    VerticalPlacementMode.Bottom => 2,
                    VerticalPlacementMode.Top => 3,
                    _ => 0
                };

                tbVerticalOffset.Text = (profile?.VerticalPlacementOffsetMm ?? 0.0).ToString("0.##");
            }
            finally
            {
                _isRefreshingPlacementUi = false;
            }
        }

        private ProfileConfig GetSelectedTargetProfileConfig()
        {
            return _persoConfig.Get(SelectedConfigHost, SelectedConfigShape, SelectedConfigUnhosted);
        }

        private void ApplyVerticalPlacementUiToProfile(ProfileConfig profile)
        {
            if (profile == null) return;

            profile.VerticalPlacementMode = cbVerticalReference.SelectedIndex switch
            {
                1 => VerticalPlacementMode.Center,
                2 => VerticalPlacementMode.Bottom,
                3 => VerticalPlacementMode.Top,
                _ => VerticalPlacementMode.Auto
            };

            if (!double.TryParse(tbVerticalOffset.Text?.Trim(), out var offset))
                offset = 0.0;

            profile.VerticalPlacementOffsetMm = offset;
        }

        private void OnApplyMapping(object sender, RoutedEventArgs e)
        {
            var it = cbLoadedType.SelectedItem as LoadedTypeItem;
            var sym = it?.Symbol;

            if (sym == null)
            {
                MessageBox.Show("Choisis une famille compatible chargée dans le projet.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Config.DynamoPath = tbDynamoPath.Text ?? "";
            Config.DefaultNormeEnabled = chkDefaultNorme.IsChecked == true;
            Config.DefaultDynamoAutoEnabled = chkDefaultDynamo.IsChecked == true;

            int idx = cbTargetProfile.SelectedIndex;
            ProfileConfig p = GetSelectedTargetProfileConfig();
            if (p == null)
                return;

            p.FamilyName = sym.Family.Name;
            p.TypeName = sym.Name;

            if (idx == 0)
            {
                p.ParamLength = cbMapWallLen?.Text ?? "";
                p.ParamHeight = cbMapWallHeight?.Text ?? "";
                p.ParamDepth = cbMapWallDepth?.Text ?? "";
            }
            else if (idx == 1)
            {
                p.ParamDiameter = cbMapWallDiam?.Text ?? "";
                p.ParamDepth = cbMapWallDepth2?.Text ?? "";
            }
            else if (idx == 2)
            {
                p.ParamLength = cbMapFloorLen?.Text ?? "";
                p.ParamWidth = cbMapFloorWidth?.Text ?? "";
                p.ParamDepth = cbMapFloorDepth?.Text ?? "";
            }
            else if (idx == 3)
            {
                p.ParamDiameter = cbMapFloorDiam?.Text ?? "";
                p.ParamDepth = cbMapFloorDepth2?.Text ?? "";
            }

            ApplyVerticalPlacementUiToProfile(p);

            if (ReservationAutoV3ConfigStore.Save(Config, out var err))
            {
                if (!ReservationAutoV3PersoConfigStore.Save(_persoConfig, out var persoErr))
                {
                    MessageBox.Show("Famille configurée mais erreur de sauvegarde : " + persoErr, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                RefreshProfilesSummary();
                RefreshShapeOptions();
                RefreshVerticalPlacementUiFromCurrentProfile();
                MessageBox.Show("Famille utilisateur configurée et sauvegardée.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Erreur sauvegarde : " + err, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSaveOnly(object sender, RoutedEventArgs e)
        {
            Config.DynamoPath = tbDynamoPath.Text ?? "";
            Config.DefaultNormeEnabled = chkDefaultNorme.IsChecked == true;
            Config.DefaultDynamoAutoEnabled = chkDefaultDynamo.IsChecked == true;

            var selectedProfile = GetSelectedTargetProfileConfig();
            ApplyVerticalPlacementUiToProfile(selectedProfile);

            if (!ReservationAutoV3ConfigStore.Save(Config, out var err))
            {
                MessageBox.Show("Erreur sauvegarde : " + err, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!ReservationAutoV3PersoConfigStore.Save(_persoConfig, out var persoErr))
            {
                MessageBox.Show("Erreur sauvegarde familles : " + persoErr, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RefreshProfilesSummary();
            MessageBox.Show("Configuration sauvegardée.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (!comboShape.IsEnabled || !_shapeOptionByLabel.Any())
            {
                MessageBox.Show("Aucune famille de réservation disponible pour le support sélectionné.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedHost = _selectedHost;
            SelectedShape = _selectedShapeBase;

            string shapeLabel = (comboShape.SelectedItem as ComboBoxItem)?.Content as string;
            if (!string.IsNullOrWhiteSpace(shapeLabel) && _shapeOptionByLabel.TryGetValue(shapeLabel, out var shapeOption))
                SelectedExecutionProfile = shapeOption.Profile;
            else
                SelectedExecutionProfile = null;

            var obj = (comboObjectType.SelectedItem as ComboBoxItem)?.Content as string ?? "Canalisation";
            SelectedObject = obj switch
            {
                "Canalisation" => ObjectType.Canalisation,
                "Gaine" => ObjectType.Gaine,
                "Porte" => ObjectType.Porte,
                "Fenêtre" => ObjectType.Fenetre,
                _ => ObjectType.Autre
            };

            var src = (comboPipeSource.SelectedItem as ComboBoxItem)?.Content as string ?? "Maquette";
            SelectedPipeSource = src switch
            {
                "Lien IFC" => PipeSource.LienIFC,
                "Lien RVT" => PipeSource.LienRVT,
                _ => PipeSource.Maquette
            };

            AutomatiqueEnabled = chkAutomatique.IsChecked == true;
            DoubleLinkEnabled = chkDoubleLink.IsChecked == true;
            MultiEnabled = chkMulti.IsChecked == true;
            NormeEnabled = chkNorme.IsChecked == true;
            DynamoAutoEnabled = chkDynamo.IsChecked == true;

            bool selectedProfileIsUnhosted = IsSelectedProfileUnhosted();
            if (DoubleLinkEnabled && !selectedProfileIsUnhosted)
            {
                MessageBox.Show(
                    "Le mode Double lien nécessite une famille sans hôte.\n\n" +
                    "Le mur ou le sol appartient à une maquette liée et ne peut pas héberger une famille créée dans votre projet.\n\n" +
                    "Choisissez une option « Ma famille sans hôte » ou « BIMaestro sans hôte » dans la liste Forme.",
                    "Famille sans hôte requise",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            bool usesModelLink = SelectedPipeSource == PipeSource.LienIFC
                                 || SelectedPipeSource == PipeSource.LienRVT;
            if (usesModelLink && !DoubleLinkEnabled)
            {
                string selectedMode = selectedProfileIsUnhosted ? "SANS HÔTE" : "AVEC HÔTE";
                string suitableDirection = selectedProfileIsUnhosted
                    ? "réseau de votre maquette → mur ou sol lié"
                    : "réseau lié → mur ou sol de votre maquette";
                string otherDirection = selectedProfileIsUnhosted
                    ? "Pour un réseau lié vers un support de votre maquette, choisissez une famille avec hôte."
                    : "Pour un réseau de votre maquette vers un support lié, choisissez une famille sans hôte.";

                var answer = MessageBox.Show(
                    $"La famille sélectionnée est {selectedMode}.\n\n" +
                    $"Elle convient au cas :\n{suitableDirection}.\n\n" +
                    $"{otherDirection}\n\nContinuer avec cette famille ?",
                    "Vérification du lien",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information,
                    MessageBoxResult.Cancel);

                if (answer != MessageBoxResult.OK)
                    return;
            }

            Config.DefaultNormeEnabled = NormeEnabled;
            Config.DefaultDynamoAutoEnabled = DynamoAutoEnabled;
            Config.LastHostTarget = SelectedHost.ToString();
            Config.LastShapeTarget = SelectedShape.ToString();
            Config.LastShapeOptionLabel = shapeLabel ?? "";
            Config.LastObjectType = SelectedObject.ToString();
            Config.LastPipeSource = SelectedPipeSource.ToString();
            Config.LastAutomaticEnabled = AutomatiqueEnabled;
            Config.LastDoubleLinkEnabled = DoubleLinkEnabled;
            Config.LastMultiEnabled = MultiEnabled;
            ReservationAutoV3ConfigStore.Save(Config, out _);

            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir l'aide en ligne : {ex.Message}", "Aide", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private class NoPromptFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return true;
            }
        }
    }
}

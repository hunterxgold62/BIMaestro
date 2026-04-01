using Autodesk.Revit.DB;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        public enum HostTarget { Mur, Sol }
        public enum ShapeTarget { Rectangulaire, Circulaire }
        public enum ObjectType { Canalisation, Gaine, Porte, Fenetre, Autre }
        public enum PipeSource { Maquette, LienIFC, LienRVT }

        public HostTarget SelectedHost { get; private set; }
        public ShapeTarget SelectedShape { get; private set; }
        public ObjectType SelectedObject { get; private set; }
        public PipeSource SelectedPipeSource { get; private set; }
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
            public LoadedTypeItem(FamilySymbol s)
            {
                Symbol = s;
                Display = $"{s.Family.Name} — {s.Name}";
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

            comboObjectType.SelectedIndex = 0;
            comboPipeSource.SelectedIndex = 0;

            cbTargetProfile.SelectedIndex = 0;

            // Defaults UI
            chkDefaultNorme.IsChecked = Config.DefaultNormeEnabled;
            chkDefaultDynamo.IsChecked = Config.DefaultDynamoAutoEnabled;
            tbOversize.Text = Config.OversizeMm_PipeDuct.ToString("0");
            tbDynamoPath.Text = Config.DynamoPath ?? "";

            chkNorme.IsChecked = Config.DefaultNormeEnabled;
            chkDynamo.IsChecked = Config.DefaultDynamoAutoEnabled;

            tbRfaPath.Text = Config.LastRfaPath ?? "";

            InitializeHostGifSelectors();
            InitializeShapeGifSelectors();
            InitializeObjectGifSelectors();
            RefreshProfilesSummary();
            RefreshShapeOptions();
            UpdateShapeSelectorUi();
            UpdateObjectSelectorUi();
            UpdateMappingPanels();
            OnCriteriaChanged(null, null);
        }

        private void RefreshProfilesSummary()
        {
            txtWallRect.Text = DescribeProfilesByVariant(HostTarget.Mur, ShapeTarget.Rectangulaire, "Longueur/Hauteur/Profondeur");
            txtWallCirc.Text = DescribeProfilesByVariant(HostTarget.Mur, ShapeTarget.Circulaire, "Diamètre/Profondeur");
            txtFloorRect.Text = DescribeProfilesByVariant(HostTarget.Sol, ShapeTarget.Rectangulaire, "Longueur/Largeur/Profondeur");
            txtFloorCirc.Text = DescribeProfilesByVariant(HostTarget.Sol, ShapeTarget.Circulaire, "Diamètre/Profondeur");
        }

        private string DescribeProfile(ProfileConfig p, string expected)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.FamilyName))
                return $"(Non configuré) — attendu : {expected}";

            string type = string.IsNullOrWhiteSpace(p.TypeName) ? "(type auto)" : p.TypeName;
            return $"{p.FamilyName} — {type}";
        }

        private string DescribeProfilesByVariant(HostTarget host, ShapeTarget shape, string expected)
        {
            string DescribeAvailable(ProfileConfig p)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.FamilyName))
                    return "(absent)";

                return IsProfileLoadedInProject(p) ? p.FamilyName : $"{p.FamilyName} (non chargée)";
            }

            var v1 = FindBuiltInProfile(host, shape, isV2: false);
            var v2 = FindBuiltInProfile(host, shape, isV2: true);
            var perso = _persoConfig.Get(host, shape);

            if (v1 == null && v2 == null && (perso == null || string.IsNullOrWhiteSpace(perso.FamilyName)))
                return $"(Non configuré) — attendu : {expected}";

            return $"V1: {DescribeAvailable(v1)} | V2: {DescribeAvailable(v2)} | Perso: {DescribeAvailable(perso)}";
        }

        public void OnCriteriaChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateObjectSelectorUi();

            string obj = (comboObjectType.SelectedItem as ComboBoxItem)?.Content as string ?? "Canalisation";
            bool isCanal = obj == "Canalisation";
            comboPipeSource.IsEnabled = isCanal;

            string shape = (comboShape.SelectedItem as ComboBoxItem)?.Content as string ?? "Rectangulaire V1";
            bool isRect = shape.IndexOf("Rectangulaire", StringComparison.OrdinalIgnoreCase) >= 0;

            chkMulti.IsEnabled = isCanal && isRect;
            if (!chkMulti.IsEnabled) chkMulti.IsChecked = false;
        }

        private void RefreshShapeOptions()
        {
            string previous = (comboShape.SelectedItem as ComboBoxItem)?.Content as string;

            _shapeOptionByLabel.Clear();
            comboShape.Items.Clear();

            HostTarget host = _selectedHost;

            TryAddShapeOption(host, _selectedShapeBase, $"{GetShapePrefix(_selectedShapeBase)} V1", FindBuiltInProfile(host, _selectedShapeBase, isV2: false));
            TryAddShapeOption(host, _selectedShapeBase, $"{GetShapePrefix(_selectedShapeBase)} V2", FindBuiltInProfile(host, _selectedShapeBase, isV2: true));
            TryAddShapeOption(host, _selectedShapeBase, $"{GetShapePrefix(_selectedShapeBase)} perso", _persoConfig.Get(host, _selectedShapeBase));

            if (comboShape.Items.Count == 0)
            {
                comboShape.Items.Add(new ComboBoxItem { Content = "(Aucune famille disponible)" });
                comboShape.SelectedIndex = 0;
                comboShape.IsEnabled = false;
                return;
            }

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
        }

        private static string GetShapePrefix(ShapeTarget shape) => shape == ShapeTarget.Circulaire ? "Circulaire" : "Rectangulaire";

        private void TryAddShapeOption(HostTarget host, ShapeTarget shape, string label, ProfileConfig profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.FamilyName)) return;
            if (!IsProfileLoadedInProject(profile)) return;

            _shapeOptionByLabel[label] = new ShapeOptionItem
            {
                Label = label,
                Shape = shape,
                Profile = profile
            };

            comboShape.Items.Add(new ComboBoxItem { Content = label });
        }

        private bool IsProfileLoadedInProject(ProfileConfig profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.FamilyName)) return false;

            return new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Any(s => s?.Family?.Name != null && string.Equals(s.Family.Name, profile.FamilyName, StringComparison.OrdinalIgnoreCase));
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
                        : (host == HostTarget.Mur ? Config.WallRect.ParamDepth : Config.FloorRect.ParamDepth)
                };
            }

            return null;
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
        private void InitializeHostGifSelectors()
        {
            var murResource = FindBestGifResource("mur", null);
            var solResource = FindBestGifResource("sol", murResource);

            TryLoadGif(imgHostMur, txtHostMurFallback, murResource, out _murGifData, out _murFirstFrame);
            TryLoadGif(imgHostSol, txtHostSolFallback, solResource, out _solGifData, out _solFirstFrame);
            UpdateHostSelectorUi();
        }

        private void InitializeShapeGifSelectors()
        {
            TryLoadGif(imgShapeRect, txtShapeRectFallback, FindBestGifResource("rect", null), out _shapeRectGifData, out _shapeRectFirstFrame);
            TryLoadGif(imgShapeCirc, txtShapeCircFallback, FindBestGifResource("circ", null), out _shapeCircGifData, out _shapeCircFirstFrame);
        }

        private void InitializeObjectGifSelectors()
        {
            TryLoadGif(imgObjPipe, txtObjPipeFallback, FindBestGifResource("cana", null), out _objPipeGifData, out _objPipeFirstFrame);
            TryLoadGif(imgObjDuct, txtObjDuctFallback, FindBestGifResource("gaine", null), out _objDuctGifData, out _objDuctFirstFrame);
            TryLoadGif(imgObjDoor, txtObjDoorFallback, FindBestGifResource("porte", null), out _objDoorGifData, out _objDoorFirstFrame);
            TryLoadGif(imgObjWindow, txtObjWindowFallback, FindBestGifResource("fenetre", null), out _objWindowGifData, out _objWindowFirstFrame);
            TryLoadGif(imgObjOther, txtObjOtherFallback, FindBestGifResource("autre", null), out _objOtherGifData, out _objOtherFirstFrame);
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

        private static bool TryLoadGif(Image image, TextBlock fallbackText, string resourceName, out GifPlaybackData gifData, out BitmapFrame firstFrame)
        {
            gifData = null;
            firstFrame = null;

            if (image == null || string.IsNullOrWhiteSpace(resourceName))
                return false;

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return false;
                    gifData = BuildGifPlaybackData(stream);
                    if (gifData?.Frames == null || gifData.Frames.Count == 0)
                        return false;

                    firstFrame = BitmapFrame.Create(gifData.Frames[0]);
                    image.Source = firstFrame;
                    if (fallbackText != null)
                        fallbackText.Visibility = System.Windows.Visibility.Collapsed;
                    return true;
                }
            }
            catch
            {
                return false;
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
                    int delayCs = BitConverter.ToInt32(item.Value, offset); // centiseconds
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
            bool rectSelected = _selectedShapeBase == ShapeTarget.Rectangulaire;
            shapeRectCard.BorderBrush = rectSelected ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
            shapeCircCard.BorderBrush = !rectSelected ? new SolidColorBrush(Color.FromRgb(53, 182, 121)) : (Brush)FindResource("Border");
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
            SelectShapeBase(ShapeTarget.Rectangulaire);
            PlayGifOnce(imgShapeRect, _shapeRectGifData, _shapeRectFirstFrame);
        }

        private void OnShapeCircClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectShapeBase(ShapeTarget.Circulaire);
            PlayGifOnce(imgShapeCirc, _shapeCircGifData, _shapeCircFirstFrame);
        }

        private void OnShapeRectHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(shapeRectCard);
            PlayGifOnce(imgShapeRect, _shapeRectGifData, _shapeRectFirstFrame);
        }

        private void OnShapeCircHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            StartPulse(shapeCircCard);
            PlayGifOnce(imgShapeCirc, _shapeCircGifData, _shapeCircFirstFrame);
        }

        private void OnShapeVariantChanged(object sender, SelectionChangedEventArgs e)
        {
            string label = (comboShape.SelectedItem as ComboBoxItem)?.Content as string ?? comboShape.Text ?? "";
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

        private void OnObjPipeClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { SelectObjectType(ObjectType.Canalisation); PlayGifOnce(imgObjPipe, _objPipeGifData, _objPipeFirstFrame); }
        private void OnObjDuctClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { SelectObjectType(ObjectType.Gaine); PlayGifOnce(imgObjDuct, _objDuctGifData, _objDuctFirstFrame); }
        private void OnObjDoorClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { SelectObjectType(ObjectType.Porte); PlayGifOnce(imgObjDoor, _objDoorGifData, _objDoorFirstFrame); }
        private void OnObjWindowClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { SelectObjectType(ObjectType.Fenetre); PlayGifOnce(imgObjWindow, _objWindowGifData, _objWindowFirstFrame); }
        private void OnObjOtherClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { SelectObjectType(ObjectType.Autre); PlayGifOnce(imgObjOther, _objOtherGifData, _objOtherFirstFrame); }
        private void OnObjPipeHover(object sender, System.Windows.Input.MouseEventArgs e) { StartPulse(objPipeCard); PlayGifOnce(imgObjPipe, _objPipeGifData, _objPipeFirstFrame); }
        private void OnObjDuctHover(object sender, System.Windows.Input.MouseEventArgs e) { StartPulse(objDuctCard); PlayGifOnce(imgObjDuct, _objDuctGifData, _objDuctFirstFrame); }
        private void OnObjDoorHover(object sender, System.Windows.Input.MouseEventArgs e) { StartPulse(objDoorCard); PlayGifOnce(imgObjDoor, _objDoorGifData, _objDoorFirstFrame); }
        private void OnObjWindowHover(object sender, System.Windows.Input.MouseEventArgs e) { StartPulse(objWindowCard); PlayGifOnce(imgObjWindow, _objWindowGifData, _objWindowFirstFrame); }
        private void OnObjOtherHover(object sender, System.Windows.Input.MouseEventArgs e) { StartPulse(objOtherCard); PlayGifOnce(imgObjOther, _objOtherGifData, _objOtherFirstFrame); }

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
                catch { }
            }

            if (dlg.ShowDialog() == true)
            {
                tbRfaPath.Text = dlg.FileName;
                Config.LastRfaPath = dlg.FileName;
            }
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
                // Charge la famille dans le projet sans popup
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

                    // Récupère tous les types de cette famille
                    _loadedTypes = GetSymbolsFromFamily(_doc, fam)
                        .Select(s => new LoadedTypeItem(s))
                        .ToList();

                    CollectAllParameterNames(path, _loadedTypes.Select(x => x.Symbol));

                    cbLoadedType.ItemsSource = _loadedTypes;
                    cbLoadedType.SelectedIndex = _loadedTypes.Any() ? 0 : -1;

                    MessageBox.Show("Famille chargée ✅\nChoisis ensuite le profil (mur/sol + forme) et mappe les paramètres.",
                        "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
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
            UpdateMappingPanels();
        }

        private void OnLoadedTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            FillParamCombosFromSelectedSymbol();
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
                case 0: panelMapRectWall.Visibility = System.Windows.Visibility.Visible; break;  // Mur Rect
                case 1: panelMapCircWall.Visibility = System.Windows.Visibility.Visible; break;  // Mur Circ
                case 2: panelMapRectFloor.Visibility = System.Windows.Visibility.Visible; break; // Sol Rect
                case 3: panelMapCircFloor.Visibility = System.Windows.Visibility.Visible; break; // Sol Circ
            }

            FillParamCombosFromSelectedSymbol();
        }

        private void FillParamCombosFromSelectedSymbol()
        {
            var it = cbLoadedType.SelectedItem as LoadedTypeItem;
            var sym = it?.Symbol;
            if (sym != null)
            {
                CollectParameterNamesFromElement(sym, _allParameterNames);

                foreach (var familySymbol in GetSymbolsFromFamily(_doc, sym.Family))
                    CollectParameterNamesFromElement(familySymbol, _allParameterNames);
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
                // On garde la liste provenant du projet si ouverture RFA impossible.
            }
            finally
            {
                familyDoc?.Close(false);
            }

            return result;
        }

        private void OnApplyMapping(object sender, RoutedEventArgs e)
        {
            var it = cbLoadedType.SelectedItem as LoadedTypeItem;
            var sym = it?.Symbol;

            if (sym == null)
            {
                MessageBox.Show("Charge une famille (.RFA) et choisis un type.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Mise à jour des options globales
            if (!double.TryParse(tbOversize.Text?.Trim(), out var ov)) ov = 50.0;
            Config.OversizeMm_PipeDuct = Math.Max(0.0, ov);
            Config.DynamoPath = tbDynamoPath.Text ?? "";
            Config.DefaultNormeEnabled = chkDefaultNorme.IsChecked == true;
            Config.DefaultDynamoAutoEnabled = chkDefaultDynamo.IsChecked == true;

            int idx = cbTargetProfile.SelectedIndex;
            ProfileConfig p = idx switch
            {
                0 => Config.WallRect,
                1 => Config.WallCirc,
                2 => Config.FloorRect,
                3 => Config.FloorCirc,
                _ => Config.WallRect
            };

            p.FamilyName = sym.Family.Name;
            p.TypeName = sym.Name;

            // Mapping selon profil
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

            if (ReservationAutoV3ConfigStore.Save(Config, out var err))
            {
                var host = (idx == 0 || idx == 1) ? HostTarget.Mur : HostTarget.Sol;
                var shape = (idx == 0 || idx == 2) ? ShapeTarget.Rectangulaire : ShapeTarget.Circulaire;
                var targetPersoProfile = _persoConfig.Get(host, shape);
                if (targetPersoProfile != null)
                {
                    targetPersoProfile.FamilyName = p.FamilyName;
                    targetPersoProfile.TypeName = p.TypeName;
                    targetPersoProfile.ParamLength = p.ParamLength;
                    targetPersoProfile.ParamWidth = p.ParamWidth;
                    targetPersoProfile.ParamHeight = p.ParamHeight;
                    targetPersoProfile.ParamDiameter = p.ParamDiameter;
                    targetPersoProfile.ParamDepth = p.ParamDepth;
                }

                if (!ReservationAutoV3PersoConfigStore.Save(_persoConfig, out var persoErr))
                {
                    MessageBox.Show("Profil configuré mais erreur sauvegarde perso : " + persoErr, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                RefreshProfilesSummary();
                RefreshShapeOptions();
                MessageBox.Show("Profil configuré + sauvegardé ✅", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Erreur sauvegarde : " + err, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSaveOnly(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(tbOversize.Text?.Trim(), out var ov)) ov = 50.0;
            Config.OversizeMm_PipeDuct = Math.Max(0.0, ov);

            Config.DynamoPath = tbDynamoPath.Text ?? "";
            Config.DefaultNormeEnabled = chkDefaultNorme.IsChecked == true;
            Config.DefaultDynamoAutoEnabled = chkDefaultDynamo.IsChecked == true;

            if (ReservationAutoV3ConfigStore.Save(Config, out var err))
                MessageBox.Show("Configuration sauvegardée ✅", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("Erreur sauvegarde : " + err, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MultiEnabled = chkMulti.IsChecked == true;

            NormeEnabled = chkNorme.IsChecked == true;
            DynamoAutoEnabled = chkDynamo.IsChecked == true;

            // persist defaults if user changed them in exec
            Config.DefaultNormeEnabled = NormeEnabled;
            Config.DefaultDynamoAutoEnabled = DynamoAutoEnabled;
            ReservationAutoV3ConfigStore.Save(Config, out _);

            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private class NoPromptFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                // Important : pas de popup. On ne force pas l’écrasement des paramètres.
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                // Idem : pas de popup, pas d'écrasement.
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return true;
            }
        }
    }
}

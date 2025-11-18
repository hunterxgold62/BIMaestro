using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Xceed.Wpf.Toolkit;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Size = System.Windows.Size;

namespace Modification
{
    public partial class ColorPickerWindow : Window
    {
        public List<View> SelectedViews { get; private set; } = new List<View>();
        public bool HideInView { get; private set; }
        public Color? SelectedColor { get; private set; }
        public ElementId SelectedSurfacePatternId { get; private set; }
        public ElementId SelectedCutPatternId { get; private set; }
        public int SelectedTransparency { get; private set; }
        public bool ApplyHalftone { get; private set; }
        public bool ModifyLineColor { get; private set; }
        public Color? SelectedLineColor { get; private set; }
        public ElementId SelectedProjectionLinePatternId { get; private set; }
        public int SelectedProjectionLineWeight { get; private set; }
        public bool IsResetRequested { get; private set; } = false;

        private readonly UIApplication _uiapp;
        private readonly UIDocument _uidoc;
        private readonly Document _document;
        private List<View> _allViews;

        private readonly bool _allowOverrideEditing;

        private readonly List<FillPatternOption> _surfacePatternOptions = new List<FillPatternOption>();
        private readonly List<FillPatternOption> _cutPatternOptions = new List<FillPatternOption>();
        private readonly List<LinePatternOption> _linePatternOptions = new List<LinePatternOption>();

        public ColorPickerWindow(UIApplication uiapp, bool allowOverrideEditing = true)
        {
            InitializeComponent();
            _uiapp = uiapp;
            _uidoc = uiapp?.ActiveUIDocument;
            _document = _uidoc?.Document;
            _allowOverrideEditing = allowOverrideEditing;

            if (!_allowOverrideEditing)
            {
                Title = "Copier les graphismes existants";
            }

            // Valeurs par défaut
            SelectedColor = Colors.Red;
            SelectedLineColor = Colors.Blue;
            SelectedSurfacePatternId = ElementId.InvalidElementId;
            SelectedCutPatternId = ElementId.InvalidElementId;
            SelectedProjectionLinePatternId = ElementId.InvalidElementId;
            SelectedProjectionLineWeight = 1;
            SelectedTransparency = 0;
            ApplyHalftone = false;
            HideInView = false;
            ModifyLineColor = false;

            // Événements
            ColorPickerControl.SelectedColorChanged += (s, e) =>
            {
                SelectedColor = e.NewValue;
                UpdateFillPreview();
            };
            LineColorPicker.SelectedColorChanged += (s, e) =>
            {
                SelectedLineColor = e.NewValue;
                UpdateLinePreview(e.NewValue);
            };
            Loaded += ColorPickerWindow_Loaded;

            UpdateFillPreview();
            UpdateLinePreview(SelectedLineColor);
        }

        private void ColorPickerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_uidoc == null || _document == null)
            {
                MessageBox.Show("Impossible de récupérer le document actif.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            var activeViewId = _uidoc.ActiveView.Id;
            if (!_allowOverrideEditing)
            {
                GeneralOptionsTab.IsEnabled = false;
                ColorsTab.IsEnabled = false;
                LinesTab.IsEnabled = false;
                ViewsTab.IsSelected = true;
            }

            // Remplir les combobox de motifs
            var fillPatterns = new FilteredElementCollector(_document)
                               .OfClass(typeof(FillPatternElement))
                               .Cast<FillPatternElement>()
                               .OrderBy(p => p.Name)
                               .ToList();

            _surfacePatternOptions.Clear();
            _cutPatternOptions.Clear();

            var neutralForeground = Color.FromRgb(64, 70, 82);
            var neutralBackground = Color.FromRgb(253, 254, 255);

            var defaultPreview = CreateFillPatternPreview(null, null, neutralForeground, neutralBackground);
            _surfacePatternOptions.Add(new FillPatternOption("(Aucun)", ElementId.InvalidElementId, null, null, defaultPreview));
            _cutPatternOptions.Add(new FillPatternOption("(Aucun)", ElementId.InvalidElementId, null, null, defaultPreview));

            foreach (var patt in fillPatterns)
            {
                var pattern = patt.GetFillPattern();
                var previewBrush = CreateFillPatternPreview(pattern, patt, neutralForeground, neutralBackground);
                var option = new FillPatternOption(patt.Name, patt.Id, pattern, patt, previewBrush);
                _surfacePatternOptions.Add(option);
                _cutPatternOptions.Add(new FillPatternOption(patt.Name, patt.Id, pattern, patt, previewBrush));
            }

            SurfacePatternComboBox.ItemsSource = _surfacePatternOptions;
            SurfacePatternComboBox.SelectedIndex = 0;
            CutPatternComboBox.ItemsSource = _cutPatternOptions;
            CutPatternComboBox.SelectedIndex = 0;

            SurfacePatternComboBox.SelectionChanged += (_, __) => UpdateFillPreview();
            CutPatternComboBox.SelectionChanged += (_, __) => UpdateFillPreview();

            SurfaceForegroundCheckBox.Checked += FillPatternOptionChanged;
            SurfaceForegroundCheckBox.Unchecked += FillPatternOptionChanged;
            SurfaceBackgroundCheckBox.Checked += FillPatternOptionChanged;
            SurfaceBackgroundCheckBox.Unchecked += FillPatternOptionChanged;
            CutForegroundCheckBox.Checked += FillPatternOptionChanged;
            CutForegroundCheckBox.Unchecked += FillPatternOptionChanged;
            CutBackgroundCheckBox.Checked += FillPatternOptionChanged;
            CutBackgroundCheckBox.Unchecked += FillPatternOptionChanged;

            var linePatterns = new FilteredElementCollector(_document)
                               .OfClass(typeof(LinePatternElement))
                               .Cast<LinePatternElement>()
                               .OrderBy(lp => lp.Name)
                               .ToList();

            _linePatternOptions.Clear();
            _linePatternOptions.Add(new LinePatternOption("(Par défaut)", ElementId.InvalidElementId, null, null));

            foreach (var lp in linePatterns)
            {
                var pattern = lp.GetLinePattern();
                var dashArray = CreateDashArray(pattern);
                _linePatternOptions.Add(new LinePatternOption(lp.Name, lp.Id, pattern, dashArray));
            }

            ProjectionLinePatternComboBox.ItemsSource = _linePatternOptions;
            ProjectionLinePatternComboBox.SelectedIndex = 0;
            ProjectionLinePatternComboBox.SelectionChanged += (_, __) => RefreshLinePatternPreview();

            // Charger et grouper les vues (hors templates et SystemBrowser)
            _allViews = new FilteredElementCollector(_document)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && !v.ViewType.ToString().Contains("Browser"))
                        .OrderBy(v => v.Name)
                        .ToList();

            ViewsTreeView.Items.Clear();
            foreach (var group in _allViews.GroupBy(v => v.ViewType).OrderBy(g => g.Key.ToString()))
            {
                var headerCb = new CheckBox { Content = TraduireViewType(group.Key), IsChecked = false };
                var groupItem = new TreeViewItem { Header = headerCb, IsExpanded = true };
                headerCb.Checked += (s, args) => SetChildCheckBoxes(groupItem, true);
                headerCb.Unchecked += (s, args) => SetChildCheckBoxes(groupItem, false);

                foreach (var v in group)
                {
                    var cb = new CheckBox { Content = v.Name, Tag = v.Id, IsChecked = false };
                    if (v.Id == activeViewId) cb.Background = Brushes.LightBlue;
                    groupItem.Items.Add(new TreeViewItem { Header = cb });
                }

                ViewsTreeView.Items.Add(groupItem);
            }

            UpdateFillPreview();
            RefreshLinePatternPreview();
        }

        private static string TraduireViewType(ViewType vt)
        {
            switch (vt)
            {
                case ViewType.FloorPlan: return "Plan d'étage";
                case ViewType.CeilingPlan: return "Plan de plafond";
                case ViewType.ThreeD: return "3D";
                case ViewType.Elevation: return "Élévation";
                case ViewType.Section: return "Coupe";
                case ViewType.Detail: return "Détail";
                case ViewType.DrawingSheet: return "Feuille";
                case ViewType.Legend: return "Légende";
                case ViewType.DraftingView: return "Croquis";
                case ViewType.EngineeringPlan: return "Plan d'ingénierie";
                case ViewType.Schedule: return "Planning";
                default: return vt.ToString();
            }
        }

        private void SelectAllViewsButton_Click(object sender, RoutedEventArgs e)
            => SetAllCheckBoxesInTreeView(ViewsTreeView, true);

        private void DeselectAllViewsButton_Click(object sender, RoutedEventArgs e)
            => SetAllCheckBoxesInTreeView(ViewsTreeView, false);

        private void SetChildCheckBoxes(TreeViewItem parent, bool isChecked)
        {
            foreach (TreeViewItem child in parent.Items)
            {
                if (child.Header is CheckBox cb) cb.IsChecked = isChecked;
                SetChildCheckBoxes(child, isChecked);
            }
        }

        private void SetAllCheckBoxesInTreeView(ItemsControl parent, bool isChecked)
        {
            foreach (TreeViewItem child in parent.Items)
            {
                if (child.Header is CheckBox cb) cb.IsChecked = isChecked;
                SetAllCheckBoxesInTreeView(child, isChecked);
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            SelectedSurfacePatternId = ElementId.InvalidElementId;
            if (SurfacePatternComboBox.SelectedItem is FillPatternOption surfaceOption)
                SelectedSurfacePatternId = surfaceOption.Id;

            SelectedCutPatternId = ElementId.InvalidElementId;
            if (CutPatternComboBox.SelectedItem is FillPatternOption cutOption)
                SelectedCutPatternId = cutOption.Id;

            SelectedTransparency = (int)TransparencySlider.Value;
            ApplyHalftone = HalftoneCheckBox.IsChecked ?? false;
            ModifyLineColor = ModifyLineColorCheckBox.IsChecked ?? false;

            SelectedProjectionLinePatternId = ElementId.InvalidElementId;
            if (ProjectionLinePatternComboBox.SelectedItem is LinePatternOption lineOption)
                SelectedProjectionLinePatternId = lineOption.Id;

            SelectedProjectionLineWeight = (int)System.Math.Round(ProjectionLineWeightSlider.Value);

            HideInView = HideInViewCheckBox.IsChecked ?? false;

            SelectedViews.Clear();
            TraverseTreeAndCollect(ViewsTreeView.Items);
            if (SelectedViews.Count == 0)
            {
                MessageBox.Show("Veuillez sélectionner au moins une vue.", "Erreur",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (_uidoc == null || _document == null)
            {
                MessageBox.Show("Impossible de récupérer le document actif.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var selectedElementIds = _uidoc.Selection.GetElementIds();

            if (selectedElementIds.Count == 0)
            {
                MessageBox.Show("Sélectionnez d’abord au moins un élément à réinitialiser.", "Erreur",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SelectedViews.Clear();
            TraverseTreeAndCollect(ViewsTreeView.Items);
            if (SelectedViews.Count == 0)
            {
                MessageBox.Show("Veuillez sélectionner au moins une vue pour la réinitialisation.", "Erreur",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Demande de réaffichage des masqués
            var answer = MessageBox.Show(
                "Voulez-vous réafficher les éléments cachés dans ces vues ?",
                "Réafficher éléments cachés",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            using (var tx = new Transaction(_document, "Reset Overrides and Unhide"))
            {
                tx.Start();
                foreach (var view in SelectedViews)
                {
                    foreach (var id in selectedElementIds)
                    {
                        try { view.SetElementOverrides(id, new OverrideGraphicSettings()); } catch { }
                        if (answer == MessageBoxResult.Yes)
                        {
                            try { view.UnhideElements(new List<ElementId> { id }); } catch { }
                        }
                    }
                }
                tx.Commit();
            }

            IsResetRequested = true;
            DialogResult = true;
            Close();
        }

        private void TraverseTreeAndCollect(ItemCollection items)
        {
            foreach (var item in items)
            {
                if (item is TreeViewItem tvi && tvi.Header is CheckBox cb && cb.IsChecked == true && cb.Tag is ElementId vid)
                {
                    var view = _allViews.FirstOrDefault(v => v.Id == vid);
                    if (view != null && !SelectedViews.Contains(view))
                        SelectedViews.Add(view);
                }
                if (item is ItemsControl ic) TraverseTreeAndCollect(ic.Items);
            }
        }


        public OverrideGraphicSettings GetOverrideGraphicSettings()
        {
            var ogs = new OverrideGraphicSettings();

            // Couleur & motifs
            if (SelectedColor.HasValue)
            {
                var c = new Autodesk.Revit.DB.Color(
                    SelectedColor.Value.R,
                    SelectedColor.Value.G,
                    SelectedColor.Value.B);

                if (SurfaceForegroundCheckBox.IsChecked == true)
                {
                    ogs.SetSurfaceForegroundPatternId(SelectedSurfacePatternId);
                    if (SelectedSurfacePatternId != ElementId.InvalidElementId)
                    {
                        ogs.SetSurfaceForegroundPatternColor(c);
                    }
                }
                if (SurfaceBackgroundCheckBox.IsChecked == true)
                {
                    ogs.SetSurfaceBackgroundPatternId(SelectedSurfacePatternId);
                    if (SelectedSurfacePatternId != ElementId.InvalidElementId)
                    {
                        ogs.SetSurfaceBackgroundPatternColor(c);
                    }
                }
                if (CutForegroundCheckBox.IsChecked == true)
                {
                    ogs.SetCutForegroundPatternId(SelectedCutPatternId);
                    if (SelectedCutPatternId != ElementId.InvalidElementId)
                    {
                        ogs.SetCutForegroundPatternColor(c);
                    }
                }
                if (CutBackgroundCheckBox.IsChecked == true)
                {
                    ogs.SetCutBackgroundPatternId(SelectedCutPatternId);
                    if (SelectedCutPatternId != ElementId.InvalidElementId)
                    {
                        ogs.SetCutBackgroundPatternColor(c);
                    }
                }
            }

            // Lignes & contours (optionnel)
            if (ModifyLineColor && SelectedLineColor.HasValue)
            {
                var lc = new Autodesk.Revit.DB.Color(
                    SelectedLineColor.Value.R,
                    SelectedLineColor.Value.G,
                    SelectedLineColor.Value.B);

                ogs.SetProjectionLineColor(lc);
                ogs.SetProjectionLinePatternId(SelectedProjectionLinePatternId);
                ogs.SetProjectionLineWeight(SelectedProjectionLineWeight);

                if (SelectedCutPatternId != ElementId.InvalidElementId)
                {
                    ogs.SetCutForegroundPatternColor(lc);
                    ogs.SetCutBackgroundPatternColor(lc);
                }
            }

            // Transparence & demi-teinte
            ogs.SetSurfaceTransparency(SelectedTransparency);
            ogs.SetHalftone(ApplyHalftone);

            return ogs;
        }
        private void UpdateFillPreview()
        {
            if (SurfacePreviewRectangle == null || CutPreviewRectangle == null)
                return;

            var baseColor = SelectedColor ?? Color.FromRgb(120, 126, 138);

            var surfaceOption = SurfacePatternComboBox?.SelectedItem as FillPatternOption;
            var cutOption = CutPatternComboBox?.SelectedItem as FillPatternOption;

            SurfacePreviewRectangle.Fill = BuildFillPreviewBrush(
                surfaceOption,
                SurfaceForegroundCheckBox.IsChecked == true,
                SurfaceBackgroundCheckBox.IsChecked == true,
                baseColor);

            CutPreviewRectangle.Fill = BuildFillPreviewBrush(
                cutOption,
                CutForegroundCheckBox.IsChecked == true,
                CutBackgroundCheckBox.IsChecked == true,
                baseColor);
        }

        private Brush BuildFillPreviewBrush(
            FillPatternOption option,
            bool showForeground,
            bool showBackground,
            Color baseColor)
        {
            var neutralBackground = Color.FromRgb(253, 254, 255);

            if (option == null || option.Pattern == null)
            {
                if (showForeground && showBackground)
                {
                    return CreateSolidBrush(baseColor, 0.85);
                }

                if (showForeground)
                {
                    return CreateSolidBrush(baseColor, 0.75);
                }

                if (showBackground)
                {
                    return CreateSolidBrush(AdjustBackgroundColor(baseColor));
                }

                return CreateSolidBrush(neutralBackground);
            }

            if (!showForeground)
            {
                if (showBackground)
                {
                    return CreateSolidBrush(AdjustBackgroundColor(baseColor));
                }

                return CreateSolidBrush(neutralBackground);
            }

            var backgroundColor = showBackground ? (Color?)AdjustBackgroundColor(baseColor) : neutralBackground;
            return CreateFillPatternPreview(option.Pattern, option.Element, baseColor, backgroundColor);
        }

        private Brush CreateFillPatternPreview(
            FillPattern pattern,
            FillPatternElement element,
            Color? foregroundColor,
            Color? backgroundColor)
        {
            var brush = TryCreatePatternPreviewBrush(element, foregroundColor, backgroundColor);
            if (brush != null)
                return brush;

            return CreateFallbackFillPatternBrush(pattern, foregroundColor, backgroundColor);
        }

        private Brush TryCreatePatternPreviewBrush(
    FillPatternElement element,
    Color? foregroundColor,
    Color? backgroundColor)
        {
            if (element == null || foregroundColor == null)
                return null;

            var methods = element.GetType()
                                 .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                 .Where(m => m.Name == "GetPreviewImage");

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                try
                {
                    Bitmap bitmap = null;
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Size))
                    {
                        bitmap = method.Invoke(element, new object[] { new Size(96, 96) }) as Bitmap;
                    }
                    else if (parameters.Length == 3 &&
                             parameters[0].ParameterType == typeof(Size) &&
                             parameters[1].ParameterType == typeof(Autodesk.Revit.DB.Color) &&
                             parameters[2].ParameterType == typeof(Autodesk.Revit.DB.Color))
                    {
                        var fg = ToRevitColor(foregroundColor.Value);
                        var bgColor = backgroundColor ?? Colors.White; // Color (non-nullable)
                        var bg = ToRevitColor(bgColor);                // pas .Value
                        bitmap = method.Invoke(element, new object[] { new Size(96, 96), fg, bg }) as Bitmap;
                    }

                    if (bitmap != null)
                    {
                        using (bitmap)
                        {
                            var brush = CreateBrushFromBitmap(bitmap);
                            if (brush != null)
                                return brush;
                        }
                    }
                }
                catch
                {
                    // ignore et essaie l’overload suivant
                }
            }

            return null;
        }


        private static Brush CreateBrushFromBitmap(Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                var brush = new ImageBrush(image)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };

                if (brush.CanFreeze)
                    brush.Freeze();

                return brush;
            }
        }

        private static Autodesk.Revit.DB.Color ToRevitColor(Color color)
            => new Autodesk.Revit.DB.Color(color.R, color.G, color.B);

        private static Brush CreateSolidBrush(Color color, double opacity = 1.0)
        {
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }

        private void FillPatternOptionChanged(object sender, RoutedEventArgs e)
            => UpdateFillPreview();

        private void UpdateLinePreview(Color? color)
        {
            if (LinePreview == null)
                return;

            var lineColor = color ?? SelectedLineColor ?? Color.FromRgb(42, 46, 55);
            LinePreview.Stroke = new SolidColorBrush(lineColor);
            RefreshLinePatternPreview();
        }

        private void RefreshLinePatternPreview()
        {
            if (LinePreview == null)
                return;

            if (ProjectionLinePatternComboBox?.SelectedItem is LinePatternOption option && option.DashArray != null)
            {
                LinePreview.StrokeDashArray = new DoubleCollection(option.DashArray);
            }
            else
            {
                LinePreview.StrokeDashArray = null;
            }
        }

        private static Brush CreateFallbackFillPatternBrush(
            FillPattern pattern,
            Color? foregroundColor,
            Color? backgroundColor)
        {
            var neutralBackground = backgroundColor ?? Color.FromRgb(253, 254, 255);

            if (pattern == null)
            {
                return CreateSolidBrush(neutralBackground);
            }

            if (pattern.IsSolidFill)
            {
                var fillColor = (backgroundColor ?? foregroundColor) ?? Color.FromRgb(180, 184, 193);
                return CreateSolidBrush(fillColor);
            }

            if (foregroundColor == null)
            {
                return CreateSolidBrush(neutralBackground);
            }

            const double tile = 48.0;
            var drawingGroup = new DrawingGroup();

            var backgroundBrush = new SolidColorBrush(neutralBackground);
            if (backgroundBrush.CanFreeze)
                backgroundBrush.Freeze();
            drawingGroup.Children.Add(new GeometryDrawing(backgroundBrush, null, new RectangleGeometry(new Rect(0, 0, tile, tile))));

            var grids = pattern.GetFillGrids();
            if (grids == null || grids.Count == 0)
            {
                return CreateSolidBrush(neutralBackground);
            }

            var strokeBrush = new SolidColorBrush(foregroundColor.Value);
            if (strokeBrush.CanFreeze)
                strokeBrush.Freeze();

            foreach (var grid in grids)
            {
                var geometryGroup = new GeometryGroup();
                var spacing = System.Math.Max(GetFillGridSpacing(grid), 1.0);
                var lineCount = (int)(tile / spacing) + 4;

                for (var i = -lineCount; i <= lineCount; i++)
                {
                    var offset = i * spacing + GetFillGridShift(grid);
                    var start = new System.Windows.Point(-tile, offset);
                    var end = new System.Windows.Point(tile * 2, offset);

                    var matrix = new Matrix();
                    matrix.Rotate(GetFillGridAngle(grid) * 180 / System.Math.PI);
                    matrix.Translate(tile / 2.0, tile / 2.0);

                    start = matrix.Transform(start);
                    end = matrix.Transform(end);

                    geometryGroup.Children.Add(new LineGeometry(start, end));
                }

                var pen = new System.Windows.Media.Pen(strokeBrush, 0.9);
                if (pen.CanFreeze)
                    pen.Freeze();
                drawingGroup.Children.Add(new GeometryDrawing(null, pen, geometryGroup));
            }

            var drawingBrush = new DrawingBrush(drawingGroup)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tile, tile),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, tile, tile),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };

            if (drawingBrush.CanFreeze)
                drawingBrush.Freeze();

            return drawingBrush;
        }

        private static DoubleCollection CreateDashArray(LinePattern pattern)
        {
            if (pattern == null)
                return null;

            var values = new List<double>();
            foreach (var segment in pattern.GetSegments())
            {
                var length = System.Math.Max(GetLinePatternSegmentLength(segment), 1e-6);
                var dipLength = FeetToDiu(length);
                switch (GetLinePatternSegmentType(segment))
                {
                    case LinePatternSegmentType.Dash:
                        values.Add(System.Math.Max(dipLength, 2.0));
                        break;
                    case LinePatternSegmentType.Space:
                        values.Add(System.Math.Max(dipLength, 2.0));
                        break;
                    case LinePatternSegmentType.Dot:
                        values.Add(2.0);
                        values.Add(System.Math.Max(dipLength, 2.0));
                        break;
                    default:
                        values.Add(System.Math.Max(dipLength, 2.0));
                        break;
                }
            }

            if (values.Count == 0)
            {
                values.Add(16);
                values.Add(10);
            }
            else if (values.Count % 2 == 1)
            {
                values.Add(values.Last());
            }

            const double previewScale = 1.6;
            var scaled = values.Select(v => System.Math.Min(System.Math.Max(v * previewScale, 1.5), 240)).ToList();

            return new DoubleCollection(scaled);
        }

        private static double FeetToDiu(double feet)
            => feet * 12.0 * 96.0;

        private static double GetFillGridSpacing(FillGrid grid)
            => GetDoubleValue(grid, 1.0, "LineSpacing", "Spacing", "GetLineSpacing", "GetSpacing");

        private static double GetFillGridShift(FillGrid grid)
            => GetDoubleValue(grid, 0.0, "Shift", "GetShift");

        private static double GetFillGridAngle(FillGrid grid)
            => GetDoubleValue(grid, 0.0, "Angle", "GetAngle");

        private static double GetLinePatternSegmentLength(LinePatternSegment segment)
            => GetDoubleValue(segment, 1.0, "Length", "GetLength");

        private static LinePatternSegmentType GetLinePatternSegmentType(LinePatternSegment segment)
        {
            if (segment == null) return LinePatternSegmentType.Dash;

            var typeMember = segment.GetType().GetProperty("SegmentType", BindingFlags.Public | BindingFlags.Instance);
            if (typeMember != null)
            {
                var raw = typeMember.GetValue(segment);
                if (raw is LinePatternSegmentType value)
                    return value;
                if (raw is int enumInt)
                    return (LinePatternSegmentType)enumInt;
            }

            var method = segment.GetType().GetMethod("GetSegmentType", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method != null)
            {
                var raw = method.Invoke(segment, null);
                if (raw is LinePatternSegmentType methodValue)
                {
                    return methodValue;
                }
                if (raw is int enumInt)
                {
                    return (LinePatternSegmentType)enumInt;
                }
            }

            return LinePatternSegmentType.Dash;
        }

        private static double GetDoubleValue(object source, double fallback, params string[] memberNames)
        {
            if (source == null || memberNames == null)
                return fallback;

            var type = source.GetType();
            foreach (var name in memberNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && (property.PropertyType == typeof(double) || property.PropertyType == typeof(float)))
                {
                    try
                    {
                        var raw = property.GetValue(source);
                        if (raw is double d) return d;
                        if (raw is float f) return f;
                    }
                    catch
                    {
                        // ignore and try next
                    }
                }

                var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null && (method.ReturnType == typeof(double) || method.ReturnType == typeof(float)))
                {
                    try
                    {
                        var raw = method.Invoke(source, null);
                        if (raw is double d) return d;
                        if (raw is float f) return f;
                    }
                    catch
                    {
                        // ignore and try next
                    }
                }
            }

            return fallback;
        }

        private class FillPatternOption
        {
            public FillPatternOption(string name, ElementId id, FillPattern pattern, FillPatternElement element, Brush previewBrush)
            {
                Name = name;
                Id = id;
                Pattern = pattern;
                Element = element;
                if (previewBrush != null)
                {
                    var clone = previewBrush.IsFrozen ? (Brush)previewBrush.Clone() : previewBrush.CloneCurrentValue();
                    if (clone.CanFreeze)
                        clone.Freeze();
                    PreviewBrush = clone;
                }
            }

            public string Name { get; }
            public ElementId Id { get; }
            public FillPattern Pattern { get; }
            public FillPatternElement Element { get; }
            public Brush PreviewBrush { get; }
        }

        private class LinePatternOption
        {
            public LinePatternOption(string name, ElementId id, LinePattern pattern, DoubleCollection dashArray)
            {
                Name = name;
                Id = id;
                Pattern = pattern;
                if (dashArray != null)
                {
                    var clone = new DoubleCollection(dashArray);
                    if (clone.CanFreeze)
                        clone.Freeze();
                    DashArray = clone;
                }
            }

            public string Name { get; }
            public ElementId Id { get; }
            public LinePattern Pattern { get; }
            public DoubleCollection DashArray { get; }
        }

        private static Color AdjustBackgroundColor(Color color)
        {
            const double blendFactor = 0.65;
            byte Blend(byte component)
            {
                return (byte)(component * blendFactor + 255 * (1 - blendFactor));
            }

            return Color.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B));
        }
    }
}
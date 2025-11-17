using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Xceed.Wpf.Toolkit;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

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
        private List<View> _allViews;

        private readonly bool _allowOverrideEditing;

        public ColorPickerWindow(UIApplication uiapp, bool allowOverrideEditing = true)
        {
            InitializeComponent();
            _uiapp = uiapp;
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
            ColorPickerControl.SelectedColorChanged += (s, e) => SelectedColor = e.NewValue;
            LineColorPicker.SelectedColorChanged += (s, e) => SelectedLineColor = e.NewValue;
            Loaded += ColorPickerWindow_Loaded;
        }

        private void ColorPickerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var doc = _uiapp.ActiveUIDocument.Document;
            var activeViewId = _uiapp.ActiveUIDocument.ActiveView.Id;
            if (!_allowOverrideEditing)
            {
                GeneralOptionsTab.IsEnabled = false;
                ColorsTab.IsEnabled = false;
                LinesTab.IsEnabled = false;
                ViewsTab.IsSelected = true;
            }

            // Remplir les combobox de motifs
            var fillPatterns = new FilteredElementCollector(doc)
                               .OfClass(typeof(FillPatternElement))
                               .Cast<FillPatternElement>();
            SurfacePatternComboBox.Items.Clear();
            CutPatternComboBox.Items.Clear();
            foreach (var patt in fillPatterns)
            {
                SurfacePatternComboBox.Items.Add(new ComboBoxItem { Content = patt.Name, Tag = patt.Id });
                CutPatternComboBox.Items.Add(new ComboBoxItem { Content = patt.Name, Tag = patt.Id });
            }

            var linePatterns = new FilteredElementCollector(doc)
                               .OfClass(typeof(LinePatternElement))
                               .Cast<LinePatternElement>();
            ProjectionLinePatternComboBox.Items.Clear();
            foreach (var lp in linePatterns)
            {
                ProjectionLinePatternComboBox.Items.Add(new ComboBoxItem { Content = lp.Name, Tag = lp.Id });
            }

            // Charger et grouper les vues (hors templates et SystemBrowser)
            _allViews = new FilteredElementCollector(doc)
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
            if (SurfacePatternComboBox.SelectedItem is ComboBoxItem spi)
                SelectedSurfacePatternId = (ElementId)spi.Tag;
            if (CutPatternComboBox.SelectedItem is ComboBoxItem cpi)
                SelectedCutPatternId = (ElementId)cpi.Tag;

            SelectedTransparency = (int)TransparencySlider.Value;
            ApplyHalftone = HalftoneCheckBox.IsChecked ?? false;
            ModifyLineColor = ModifyLineColorCheckBox.IsChecked ?? false;

            if (ProjectionLinePatternComboBox.SelectedItem is ComboBoxItem pli)
                SelectedProjectionLinePatternId = (ElementId)pli.Tag;
            if (ProjectionLineWeightComboBox.SelectedItem is ComboBoxItem pw)
                SelectedProjectionLineWeight = int.Parse(pw.Content.ToString());

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
            var uidoc = _uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
            var selIds = uidoc.Selection.GetElementIds();

            if (selIds.Count == 0)
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

            using (var tx = new Transaction(doc, "Reset Overrides and Unhide"))
            {
                tx.Start();
                foreach (var view in SelectedViews)
                {
                    foreach (var id in selIds)
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
                    ogs.SetSurfaceForegroundPatternColor(c);
                    ogs.SetSurfaceForegroundPatternId(SelectedSurfacePatternId);
                }
                if (SurfaceBackgroundCheckBox.IsChecked == true)
                {
                    ogs.SetSurfaceBackgroundPatternColor(c);
                    ogs.SetSurfaceBackgroundPatternId(SelectedSurfacePatternId);
                }
                if (CutForegroundCheckBox.IsChecked == true)
                {
                    ogs.SetCutForegroundPatternColor(c);
                    ogs.SetCutForegroundPatternId(SelectedCutPatternId);
                }
                if (CutBackgroundCheckBox.IsChecked == true)
                {
                    ogs.SetCutBackgroundPatternColor(c);
                    ogs.SetCutBackgroundPatternId(SelectedCutPatternId);
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

                ogs.SetCutForegroundPatternColor(lc);
                ogs.SetCutBackgroundPatternColor(lc);
            }

            // Transparence & demi-teinte
            ogs.SetSurfaceTransparency(SelectedTransparency);
            ogs.SetHalftone(ApplyHalftone);

            return ogs;
        }
    }
}

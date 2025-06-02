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
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace Modification
{
    public partial class ColorPickerWindow : Window
    {
        // --- PROPRIÉTÉS EXPOSÉES VERS LE PLUGIN ---
        public List<View> SelectedViews { get; private set; } = new List<View>();
        public bool HideInView { get; private set; }

        // Couleur et motifs
        public Color? SelectedColor { get; private set; }
        public ElementId SelectedSurfacePatternId { get; private set; }
        public ElementId SelectedCutPatternId { get; private set; }

        // Transparence / demi-teinte
        public int SelectedTransparency { get; private set; }
        public bool ApplyHalftone { get; private set; }

        // Lignes et contours
        public bool ModifyLineColor { get; private set; }
        public Color? SelectedLineColor { get; private set; }
        public ElementId SelectedProjectionLinePatternId { get; private set; }
        public int SelectedProjectionLineWeight { get; private set; }

        public bool IsResetRequested { get; private set; } = false;

        private readonly UIApplication _uiapp;
        private List<View> _allViews;

        public ColorPickerWindow(UIApplication uiapp)
        {
            InitializeComponent();
            _uiapp = uiapp;

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

            // Événements couleur
            ColorPickerControl.SelectedColorChanged += ColorPickerControl_SelectedColorChanged;
            LineColorPicker.SelectedColorChanged += LineColorPicker_SelectedColorChanged;
            this.Loaded += ColorPickerWindow_Loaded;
        }

        private void ColorPickerControl_SelectedColorChanged(object sender, RoutedPropertyChangedEventArgs<Color?> e)
        {
            SelectedColor = e.NewValue;
        }

        private void LineColorPicker_SelectedColorChanged(object sender, RoutedPropertyChangedEventArgs<Color?> e)
        {
            SelectedLineColor = e.NewValue;
        }

        private void ColorPickerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var doc = _uiapp.ActiveUIDocument.Document;

            // 1) Charger FillPatternElement
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

            // 2) Charger LinePatternElement
            var linePatterns = new FilteredElementCollector(doc)
                               .OfClass(typeof(LinePatternElement))
                               .Cast<LinePatternElement>();
            ProjectionLinePatternComboBox.Items.Clear();
            foreach (var lp in linePatterns)
            {
                ProjectionLinePatternComboBox.Items.Add(new ComboBoxItem { Content = lp.Name, Tag = lp.Id });
            }

            // 3) Charger toutes les vues non-template et les grouper
            _allViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate)
                        .OrderBy(v => v.Name)
                        .ToList();

            ViewsTreeView.Items.Clear();
            foreach (var group in _allViews.GroupBy(v => v.ViewType)
                                           .OrderBy(g => g.Key.ToString()))
            {
                string headerText = TraduireViewType(group.Key);
                var headerCb = new CheckBox { Content = headerText, IsChecked = false };
                var groupItem = new TreeViewItem { Header = headerCb, IsExpanded = true };

                headerCb.Checked += (s, args) => SetChildCheckBoxes(groupItem, true);
                headerCb.Unchecked += (s, args) => SetChildCheckBoxes(groupItem, false);

                foreach (var v in group)
                {
                    var cb = new CheckBox { Content = v.Name, Tag = v.Id, IsChecked = false };
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
                // ajoutez d'autres cas si nécessaire
                default: return vt.ToString();
            }
        }

        private void SetChildCheckBoxes(TreeViewItem parent, bool isChecked)
        {
            foreach (var item in parent.Items)
            {
                if (item is TreeViewItem child)
                {
                    if (child.Header is CheckBox cb)
                        cb.IsChecked = isChecked;
                    SetChildCheckBoxes(child, isChecked);
                }
            }
        }

        private void SelectAllViewsButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllCheckBoxesInTreeView(ViewsTreeView, true);
        }

        private void DeselectAllViewsButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllCheckBoxesInTreeView(ViewsTreeView, false);
        }

        private void SetAllCheckBoxesInTreeView(ItemsControl parent, bool isChecked)
        {
            foreach (var item in parent.Items)
            {
                if (item is TreeViewItem child)
                {
                    if (child.Header is CheckBox cb)
                        cb.IsChecked = isChecked;
                    SetAllCheckBoxesInTreeView(child, isChecked);
                }
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // Motifs
            if (SurfacePatternComboBox.SelectedItem is ComboBoxItem spi)
                SelectedSurfacePatternId = (ElementId)spi.Tag;
            if (CutPatternComboBox.SelectedItem is ComboBoxItem cpi)
                SelectedCutPatternId = (ElementId)cpi.Tag;

            // Transparence & demi-teinte
            SelectedTransparency = (int)TransparencySlider.Value;
            ApplyHalftone = HalftoneCheckBox.IsChecked ?? false;

            // Lignes & contours
            ModifyLineColor = ModifyLineColorCheckBox.IsChecked ?? false;
            if (ProjectionLinePatternComboBox.SelectedItem is ComboBoxItem pli)
                SelectedProjectionLinePatternId = (ElementId)pli.Tag;
            if (ProjectionLineWeightComboBox.SelectedItem is ComboBoxItem pw)
                SelectedProjectionLineWeight = int.Parse(pw.Content.ToString());

            // Masquage
            HideInView = HideInViewCheckBox.IsChecked ?? false;

            // Avertissement si "Hide in View" est coché
            if (HideInView)
            {
                var result = System.Windows.Forms.MessageBox.Show(
    "Attention : Le masquage ne pourra pas être réinitialisé automatiquement.\n" +
    "Vous devrez réafficher manuellement les éléments dans chaque vue.\n\n" +
    "Souhaitez-vous continuer ?",
    "Avertissement",
    System.Windows.Forms.MessageBoxButtons.YesNo,
    System.Windows.Forms.MessageBoxIcon.Warning,
    System.Windows.Forms.MessageBoxDefaultButton.Button2);

                if (result == System.Windows.Forms.DialogResult.No)
                {
                    // L'utilisateur annule, on remet HideInView à false
                    HideInView = false;
                }
            }

            // Collecte des vues cochées
            SelectedViews.Clear();
            TraverseTreeAndCollect(ViewsTreeView.Items);
            if (SelectedViews.Count == 0)
            {
                MessageBox.Show("Veuillez sélectionner au moins une vue.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            this.DialogResult = true;
            this.Close();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            // Marque qu’on veut faire un reset
            IsResetRequested = true;

            // Exécution de la logique de reset ici même
            var uidoc = _uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
            var selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                MessageBox.Show("Sélectionnez d’abord au moins un élément pour le réinitialiser.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Toutes les vues non-template
            var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate);

            using (var tx = new Transaction(doc, "Reset Graphic Overrides"))
            {
                tx.Start();
                foreach (var view in views)
                {
                    foreach (var id in selectedIds)
                    {
                        try
                        {
                            view.SetElementOverrides(id, new OverrideGraphicSettings());
                        }
                        catch { /* on ignore les vues non supportées */ }
                    }
                }
                tx.Commit();
            }

            this.DialogResult = true;
            this.Close();
        }
    

        private void TraverseTreeAndCollect(ItemCollection items)
        {
            foreach (var item in items)
            {
                if (item is TreeViewItem tvi &&
                    tvi.Header is CheckBox cb &&
                    cb.IsChecked == true)
                {
                    var vid = (ElementId)cb.Tag;
                    var view = _allViews.FirstOrDefault(v => v.Id == vid);
                    if (view != null && !SelectedViews.Contains(view))
                        SelectedViews.Add(view);
                }
                if (item is ItemsControl ic)
                    TraverseTreeAndCollect(ic.Items);
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

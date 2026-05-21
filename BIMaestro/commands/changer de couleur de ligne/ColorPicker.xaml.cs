using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;

namespace Modification
{
    public partial class GraphicOverrideWindow : Window
    {
        public List<View> SelectedViews { get; private set; } = new List<View>();

        public bool HideInView { get; private set; }

        public int SelectedTransparency { get; private set; }

        public bool ApplyHalftone { get; private set; }

        public bool IsResetRequested { get; private set; }

        public bool UnhideElements { get; private set; }

        private readonly UIApplication _uiapp;
        private readonly UIDocument _uidoc;
        private readonly Document _document;

        private readonly bool _optionsEnabled;
        private readonly bool _copyExistingOverridesMode;

        private List<View> _allGraphicViews = new List<View>();
        private List<ViewSheet> _allSheets = new List<ViewSheet>();

        private const string HelpUrl = "https://bimaestro.fr";

        public GraphicOverrideWindow(
            UIApplication uiapp,
            bool optionsEnabled = true,
            bool copyExistingOverridesMode = false)
        {
            ThemeManager.EnsureThemeLoaded();

            InitializeComponent();

            _uiapp = uiapp;
            _uidoc = _uiapp?.ActiveUIDocument;
            _document = _uidoc?.Document;

            _optionsEnabled = optionsEnabled;
            _copyExistingOverridesMode = copyExistingOverridesMode;

            Loaded += GraphicOverrideWindow_Loaded;
        }

        private void GraphicOverrideWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_uidoc == null || _document == null)
            {
                MessageBox.Show(
                    "Impossible de récupérer le document actif.",
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                DialogResult = false;
                Close();
                return;
            }

            ApplyModeToInterface();

            LoadViewsAndSheets();
            BuildTree();
            SelectActiveView();
            UpdateTransparencyText();
        }

        private void ApplyModeToInterface()
        {
            if (_optionsEnabled)
                return;

            HalftoneCheckBox.IsEnabled = false;
            HideInViewCheckBox.IsEnabled = false;
            TransparencySlider.IsEnabled = false;

            if (_copyExistingOverridesMode)
            {
                Title = "BIMaestro - Copier les surcharges existantes";
                ApplyButton.Content = "Copier";

                MessageBox.Show(
                    "Mode copie activé.\n\nLes options générales sont désactivées, car BIMaestro va copier la surcharge graphique existante détectée dans la vue active.",
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = HelpUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                MessageBox.Show(
                    "Impossible d’ouvrir la page d’aide.",
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void LoadViewsAndSheets()
        {
            _allGraphicViews = new FilteredElementCollector(_document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(IsUsableGraphicView)
                .OrderBy(v => GetViewTypeLabel(v.ViewType))
                .ThenBy(v => v.Name)
                .ToList();

            _allSheets = new FilteredElementCollector(_document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => s != null && !s.IsTemplate)
                .OrderBy(s => s.SheetNumber)
                .ThenBy(s => s.Name)
                .ToList();
        }

        private void BuildTree()
        {
            ViewsTreeView.Items.Clear();

            View activeView = _uidoc.ActiveView;

            TreeViewItem activeGroup = CreateGroup("Vue active");

            if (IsUsableGraphicView(activeView))
            {
                activeGroup.Items.Add(CreateViewItem(activeView, true));
            }
            else
            {
                activeGroup.Items.Add(CreateInfoItem("La vue active ne supporte pas les surcharges graphiques."));
            }

            ViewsTreeView.Items.Add(activeGroup);

            TreeViewItem sheetsGroup = CreateGroup("Feuilles - appliquer aux vues placées");

            foreach (ViewSheet sheet in _allSheets)
            {
                List<View> placedViews = GetPlacedGraphicViews(sheet);

                if (placedViews.Count == 0)
                    continue;

                string sheetLabel = $"{sheet.SheetNumber} - {sheet.Name}";

                CheckBox sheetCheckBox = new CheckBox
                {
                    Content = sheetLabel,
                    IsChecked = false
                };

                TreeViewItem sheetItem = new TreeViewItem
                {
                    Header = sheetCheckBox,
                    IsExpanded = false
                };

                sheetCheckBox.Checked += (s, e) => SetChildCheckBoxes(sheetItem, true);
                sheetCheckBox.Unchecked += (s, e) => SetChildCheckBoxes(sheetItem, false);

                foreach (View view in placedViews)
                {
                    sheetItem.Items.Add(CreateViewItem(view, false));
                }

                sheetsGroup.Items.Add(sheetItem);
            }

            ViewsTreeView.Items.Add(sheetsGroup);

            TreeViewItem viewsGroup = CreateGroup("Toutes les vues du projet");

            IEnumerable<View> viewsWithoutActiveView = _allGraphicViews;

            if (activeView != null)
            {
                viewsWithoutActiveView = viewsWithoutActiveView
                    .Where(v => v.Id != activeView.Id);
            }

            foreach (IGrouping<ViewType, View> group in viewsWithoutActiveView
                         .GroupBy(v => v.ViewType)
                         .OrderBy(g => GetViewTypeLabel(g.Key)))
            {
                CheckBox typeCheckBox = new CheckBox
                {
                    Content = GetViewTypeLabel(group.Key),
                    IsChecked = false
                };

                TreeViewItem typeItem = new TreeViewItem
                {
                    Header = typeCheckBox,
                    IsExpanded = false
                };

                typeCheckBox.Checked += (s, e) => SetChildCheckBoxes(typeItem, true);
                typeCheckBox.Unchecked += (s, e) => SetChildCheckBoxes(typeItem, false);

                foreach (View view in group.OrderBy(v => v.Name))
                {
                    typeItem.Items.Add(CreateViewItem(view, false));
                }

                viewsGroup.Items.Add(typeItem);
            }

            ViewsTreeView.Items.Add(viewsGroup);
        }

        private TreeViewItem CreateGroup(string title)
        {
            TextBlock header = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 6, 2, 6)
            };

            return new TreeViewItem
            {
                Header = header,
                IsExpanded = true
            };
        }

        private TreeViewItem CreateViewItem(View view, bool highlight)
        {
            CheckBox checkBox = new CheckBox
            {
                Content = $"{GetViewTypeLabel(view.ViewType)} - {view.Name}",
                Tag = view.UniqueId,
                IsChecked = false
            };

            if (highlight)
            {
                checkBox.Background = Brushes.LightBlue;
            }

            return new TreeViewItem
            {
                Header = checkBox,
                IsExpanded = false
            };
        }

        private TreeViewItem CreateInfoItem(string text)
        {
            TextBlock textBlock = new TextBlock
            {
                Text = text,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6)
            };

            return new TreeViewItem
            {
                Header = textBlock,
                IsEnabled = false
            };
        }

        private List<View> GetPlacedGraphicViews(ViewSheet sheet)
        {
            List<View> result = new List<View>();

            if (sheet == null)
                return result;

            ICollection<ElementId> placedViewIds;

            try
            {
                placedViewIds = sheet.GetAllPlacedViews();
            }
            catch
            {
                return result;
            }

            HashSet<string> uniqueIds = new HashSet<string>();

            foreach (ElementId viewId in placedViewIds)
            {
                View view = _document.GetElement(viewId) as View;

                if (!IsUsableGraphicView(view))
                    continue;

                if (uniqueIds.Add(view.UniqueId))
                {
                    result.Add(view);
                }
            }

            return result
                .OrderBy(v => GetViewTypeLabel(v.ViewType))
                .ThenBy(v => v.Name)
                .ToList();
        }

        private void SelectActiveViewButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllCheckBoxes(ViewsTreeView.Items, false);
            SelectActiveView();
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllCheckBoxes(ViewsTreeView.Items, true);
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllCheckBoxes(ViewsTreeView.Items, false);
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            IsResetRequested = false;
            UnhideElements = false;

            HideInView = HideInViewCheckBox.IsChecked == true;
            ApplyHalftone = HalftoneCheckBox.IsChecked == true;
            SelectedTransparency = Convert.ToInt32(Math.Round(TransparencySlider.Value));

            SelectedViews = CollectSelectedViews();

            if (SelectedViews.Count == 0)
            {
                MessageBox.Show(
                    "Sélectionne au moins une vue ou une feuille contenant des vues.",
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedViews = CollectSelectedViews();

            if (SelectedViews.Count == 0)
            {
                MessageBox.Show(
                    "Sélectionne au moins une vue ou une feuille contenant des vues à réinitialiser.",
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                "Voulez-vous aussi réafficher les éléments masqués dans les vues sélectionnées ?",
                "Réinitialisation",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
                return;

            IsResetRequested = true;
            UnhideElements = answer == MessageBoxResult.Yes;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void HideInViewCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (!_optionsEnabled)
                return;

            TransparencySlider.IsEnabled = false;
            HalftoneCheckBox.IsEnabled = false;
        }

        private void HideInViewCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_optionsEnabled)
                return;

            TransparencySlider.IsEnabled = true;
            HalftoneCheckBox.IsEnabled = true;
        }

        private void TransparencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateTransparencyText();
        }

        private void UpdateTransparencyText()
        {
            if (TransparencyValueText == null || TransparencySlider == null)
                return;

            TransparencyValueText.Text = $"{Convert.ToInt32(Math.Round(TransparencySlider.Value))} %";
        }

        private void SelectActiveView()
        {
            View activeView = _uidoc?.ActiveView;

            if (!IsUsableGraphicView(activeView))
                return;

            SetCheckBoxByViewUniqueId(ViewsTreeView.Items, activeView.UniqueId, true);
        }

        private List<View> CollectSelectedViews()
        {
            HashSet<string> collectedUniqueIds = new HashSet<string>();
            List<View> result = new List<View>();

            CollectSelectedViewsRecursive(ViewsTreeView.Items, collectedUniqueIds, result);

            return result
                .Where(IsUsableGraphicView)
                .GroupBy(v => v.UniqueId)
                .Select(g => g.First())
                .ToList();
        }

        private void CollectSelectedViewsRecursive(
            ItemCollection items,
            HashSet<string> collectedUniqueIds,
            List<View> result)
        {
            foreach (object item in items)
            {
                if (item is TreeViewItem treeViewItem)
                {
                    if (treeViewItem.Header is CheckBox checkBox &&
                        checkBox.IsChecked == true &&
                        checkBox.Tag is string viewUniqueId &&
                        !string.IsNullOrWhiteSpace(viewUniqueId))
                    {
                        View view = _document.GetElement(viewUniqueId) as View;

                        if (IsUsableGraphicView(view) && collectedUniqueIds.Add(view.UniqueId))
                        {
                            result.Add(view);
                        }
                    }

                    CollectSelectedViewsRecursive(treeViewItem.Items, collectedUniqueIds, result);
                }
            }
        }

        private void SetAllCheckBoxes(ItemCollection items, bool isChecked)
        {
            foreach (object item in items)
            {
                if (item is TreeViewItem treeViewItem)
                {
                    if (treeViewItem.Header is CheckBox checkBox)
                    {
                        checkBox.IsChecked = isChecked;
                    }

                    SetAllCheckBoxes(treeViewItem.Items, isChecked);
                }
            }
        }

        private void SetChildCheckBoxes(TreeViewItem parent, bool isChecked)
        {
            if (parent == null)
                return;

            foreach (object item in parent.Items)
            {
                if (item is TreeViewItem treeViewItem)
                {
                    if (treeViewItem.Header is CheckBox checkBox)
                    {
                        checkBox.IsChecked = isChecked;
                    }

                    SetChildCheckBoxes(treeViewItem, isChecked);
                }
            }
        }

        private bool SetCheckBoxByViewUniqueId(ItemCollection items, string viewUniqueId, bool isChecked)
        {
            foreach (object item in items)
            {
                if (item is TreeViewItem treeViewItem)
                {
                    if (treeViewItem.Header is CheckBox checkBox &&
                        checkBox.Tag is string checkBoxViewUniqueId &&
                        checkBoxViewUniqueId == viewUniqueId)
                    {
                        checkBox.IsChecked = isChecked;
                        treeViewItem.IsExpanded = true;
                        return true;
                    }

                    if (SetCheckBoxByViewUniqueId(treeViewItem.Items, viewUniqueId, isChecked))
                    {
                        treeViewItem.IsExpanded = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsUsableGraphicView(View view)
        {
            if (view == null)
                return false;

            if (!view.IsValidObject)
                return false;

            if (view.IsTemplate)
                return false;

            if (IsBrowserView(view))
                return false;

            try
            {
                if (!view.AreGraphicsOverridesAllowed())
                    return false;
            }
            catch
            {
                return false;
            }

            if (view is ViewSheet)
                return false;

            return true;
        }

        private static bool IsBrowserView(View view)
        {
            if (view == null)
                return true;

            string viewTypeName = view.ViewType.ToString();

            return viewTypeName.IndexOf("Browser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   view.ViewType == ViewType.ProjectBrowser ||
                   view.ViewType == ViewType.SystemBrowser;
        }

        private static string GetViewTypeLabel(ViewType viewType)
        {
            switch (viewType)
            {
                case ViewType.FloorPlan:
                    return "Plan d'étage";

                case ViewType.CeilingPlan:
                    return "Plan de plafond";

                case ViewType.EngineeringPlan:
                    return "Plan d'ingénierie";

                case ViewType.AreaPlan:
                    return "Plan de surface";

                case ViewType.ThreeD:
                    return "Vue 3D";

                case ViewType.Section:
                    return "Coupe";

                case ViewType.Elevation:
                    return "Élévation";

                case ViewType.Detail:
                    return "Détail";

                case ViewType.DraftingView:
                    return "Vue de dessin";

                case ViewType.Legend:
                    return "Légende";

                case ViewType.DrawingSheet:
                    return "Feuille";

                case ViewType.Schedule:
                    return "Nomenclature";

                default:
                    return viewType.ToString();
            }
        }
    }
}
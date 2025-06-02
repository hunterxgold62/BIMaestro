using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using System.IO;
using Newtonsoft.Json;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace ScanTextRevit
{
    public partial class SelectViewsWindow : Window
    {
        private List<View> _allViews;
        private List<ViewSheet> _allSheets;
        private Document _doc;

        private Preferences _preferences;
        private static string PrefFilePath
        {
            get
            {
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs",
                    "SauvegardePréférence"
                );
                Directory.CreateDirectory(baseDir);
                return Path.Combine(baseDir, "thème IA auto.json");
            }
        }

        public SelectViewsWindow(List<View> allViews, List<ViewSheet> allSheets, Document doc)
        {
            InitializeComponent();
            _allViews = allViews;
            _allSheets = allSheets;
            _doc = doc;
            LoadPreferences();
            ApplyTheme();
            PopulateTreeView();
        }

        private void PopulateTreeView()
        {
            ViewsTreeView.Items.Clear();

            // 1) Vues placées sur des feuilles
            var placedViewIds = new HashSet<ElementId>();
            if (_allSheets != null && _allSheets.Count > 0 && _doc != null)
            {
                foreach (var sheet in _allSheets)
                {
                    var vports = new FilteredElementCollector(_doc, sheet.Id)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .ToList();
                    foreach (var vp in vports)
                    {
                        placedViewIds.Add(vp.ViewId);
                    }
                }
            }

            // 2) Vues indépendantes
            var independentViews = _allViews.Where(v => !placedViewIds.Contains(v.Id)).ToList();

            if (independentViews.Count > 0)
            {
                CheckBox groupCheckBoxVues = new CheckBox
                {
                    Content = "VUES (cocher/décocher tout)",
                    IsChecked = false,
                    Foreground = this.Foreground
                };
                TreeViewItem groupItemVues = new TreeViewItem
                {
                    Header = groupCheckBoxVues,
                    IsExpanded = true
                };
                groupCheckBoxVues.Checked += (s, e) => SetChildCheckBoxes(groupItemVues, true);
                groupCheckBoxVues.Unchecked += (s, e) => SetChildCheckBoxes(groupItemVues, false);

                foreach (var view in independentViews)
                {
                    string label = $"{GetViewTypeLabel(view)} : {view.Name}";
                    CheckBox cb = new CheckBox
                    {
                        Content = label,
                        Tag = view.Id,
                        IsChecked = false,
                        Foreground = this.Foreground
                    };
                    TreeViewItem childItem = new TreeViewItem { Header = cb };
                    groupItemVues.Items.Add(childItem);
                }
                ViewsTreeView.Items.Add(groupItemVues);
            }

            if (_allSheets != null && _allSheets.Count > 0 && _doc != null)
            {
                CheckBox groupCheckBoxSheets = new CheckBox
                {
                    Content = "FEUILLES (cocher/décocher tout)",
                    IsChecked = false,
                    Foreground = this.Foreground
                };
                TreeViewItem groupItemSheets = new TreeViewItem
                {
                    Header = groupCheckBoxSheets,
                    IsExpanded = true
                };
                groupCheckBoxSheets.Checked += (s, e) => SetChildCheckBoxes(groupItemSheets, true);
                groupCheckBoxSheets.Unchecked += (s, e) => SetChildCheckBoxes(groupItemSheets, false);

                foreach (var sheet in _allSheets)
                {
                    string sheetLabel = $"Feuille : {sheet.SheetNumber} - {sheet.Name}";
                    CheckBox sheetCheckBox = new CheckBox
                    {
                        Content = sheetLabel,
                        Tag = sheet.Id,
                        IsChecked = false,
                        Foreground = this.Foreground
                    };
                    TreeViewItem sheetItem = new TreeViewItem
                    {
                        Header = sheetCheckBox,
                        IsExpanded = true
                    };
                    sheetCheckBox.Checked += (s, e) => SetChildCheckBoxes(sheetItem, true);
                    sheetCheckBox.Unchecked += (s, e) => SetChildCheckBoxes(sheetItem, false);

                    // (A) Vues placées
                    var vports = new FilteredElementCollector(_doc, sheet.Id)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .ToList();
                    foreach (var vp in vports)
                    {
                        View placedView = _doc.GetElement(vp.ViewId) as View;
                        if (placedView == null) continue;
                        string prefix = GetViewTypeLabel(placedView);
                        string childLabel = $"{prefix} : {placedView.Name}";
                        CheckBox childCb = new CheckBox
                        {
                            Content = childLabel,
                            Tag = placedView.Id,
                            IsChecked = false,
                            Foreground = this.Foreground
                        };
                        TreeViewItem childItem = new TreeViewItem { Header = childCb };
                        sheetItem.Items.Add(childItem);
                    }

                    // (B) Nomenclatures placées
                    var scheduleInstances = new FilteredElementCollector(_doc, sheet.Id)
                        .OfClass(typeof(ScheduleSheetInstance))
                        .Cast<ScheduleSheetInstance>()
                        .ToList();
                    foreach (var ssi in scheduleInstances)
                    {
                        ViewSchedule vsched = _doc.GetElement(ssi.ScheduleId) as ViewSchedule;
                        if (vsched == null) continue;
                        string childLabel = $"Nomenclature : {vsched.Name}";
                        CheckBox childCb = new CheckBox
                        {
                            Content = childLabel,
                            Tag = vsched.Id,
                            IsChecked = false,
                            Foreground = this.Foreground
                        };
                        TreeViewItem childItem = new TreeViewItem { Header = childCb };
                        sheetItem.Items.Add(childItem);
                    }
                    groupItemSheets.Items.Add(sheetItem);
                }
                ViewsTreeView.Items.Add(groupItemSheets);
            }
        }

        private void SetChildCheckBoxes(TreeViewItem parentItem, bool isChecked)
        {
            foreach (var child in parentItem.Items)
            {
                if (child is TreeViewItem cItem)
                {
                    if (cItem.Header is CheckBox cb)
                    {
                        cb.IsChecked = isChecked;
                    }
                    SetChildCheckBoxes(cItem, isChecked);
                }
            }
        }

        private string GetViewTypeLabel(View view)
        {
            if (view is ViewSchedule) return "Nomenclature";
            if (view.ViewType == ViewType.Legend) return "Légende";
            return "Vue";
        }

        public List<ElementId> GetSelectedElementIds()
        {
            var selectedIds = new List<ElementId>();
            foreach (var topItem in ViewsTreeView.Items)
            {
                if (topItem is TreeViewItem tvi)
                {
                    CollectCheckedElementIds(tvi, selectedIds);
                }
            }
            return selectedIds;
        }

        private void CollectCheckedElementIds(TreeViewItem item, List<ElementId> selectedIds)
        {
            if (item.Header is CheckBox cb && cb.IsChecked == true)
            {
                if (cb.Tag is ElementId eId)
                {
                    selectedIds.Add(eId);
                }
            }
            foreach (var child in item.Items)
            {
                if (child is TreeViewItem cItem)
                {
                    CollectCheckedElementIds(cItem, selectedIds);
                }
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // Gestion des Préférences

        private void LoadPreferences()
        {
            if (File.Exists(PrefFilePath))
            {
                try
                {
                    string json = File.ReadAllText(PrefFilePath);
                    _preferences = JsonConvert.DeserializeObject<Preferences>(json);
                }
                catch
                {
                    _preferences = new Preferences();
                }
            }
            else
            {
                _preferences = new Preferences();
            }
        }

        private void SavePreferences()
        {
            string json = JsonConvert.SerializeObject(_preferences, Formatting.Indented);
            File.WriteAllText(PrefFilePath, json);
        }

        private void ApplyTheme()
        {
            if (_preferences.DarkMode)
            {
                MainBorder.Background = new SolidColorBrush(Color.FromRgb(45, 45, 48));
                this.Foreground = new SolidColorBrush(Colors.White);
                ViewsTreeView.Background = new SolidColorBrush(Color.FromRgb(45, 45, 48));
                ViewsTreeView.Foreground = new SolidColorBrush(Colors.White);
            }
            else
            {
                MainBorder.Background = new SolidColorBrush(Color.FromRgb(250, 250, 250));
                this.Foreground = new SolidColorBrush(Colors.Black);
                ViewsTreeView.Background = new SolidColorBrush(Color.FromRgb(250, 250, 250));
                ViewsTreeView.Foreground = new SolidColorBrush(Colors.Black);
            }
        }
    }
}

// SelectViewsWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Color = System.Windows.Media.Color;

namespace ScanTextRevit
{
    public partial class SelectViewsWindow : Window
    {
        private readonly List<View> _allViews;
        private readonly List<ViewSheet> _allSheets;
        private readonly Document _doc;

        // Pour la pré‑charge des viewports et des nomenclatures
        private Dictionary<ElementId, List<Viewport>> _vpsBySheet;
        private Dictionary<ElementId, List<ScheduleSheetInstance>> _schedulesBySheet;

        private CheckBox _viewsGroupCheckBox;
        private CheckBox _sheetsGroupCheckBox;

        public SelectViewsWindow(List<View> allViews, List<ViewSheet> allSheets, Document doc)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            _allViews = allViews;
            _allSheets = allSheets;
            _doc = doc;

            PreloadData();      // 1) on charge une seule fois tous les viewports et nomenclatures
            PopulateTreeView(); // 2) on affiche les groupes de vues/feuilles dans l'arbre

        }

        /// <summary>
        /// Récupère en un seul passage tous les Viewport et ScheduleSheetInstance,
        /// puis les groupe par feuille (via OwnerViewId).
        /// </summary>
        private void PreloadData()
        {
            // Tous les viewports du doc
            var allVps = new FilteredElementCollector(_doc)
                            .OfClass(typeof(Viewport))
                            .Cast<Viewport>()
                            .ToList();
            _vpsBySheet = allVps
                            .GroupBy(vp => vp.SheetId)
                            .ToDictionary(g => g.Key, g => g.ToList());

            // Toutes les instances de nomenclature placées
            var allSchedules = new FilteredElementCollector(_doc)
                                   .OfClass(typeof(ScheduleSheetInstance))
                                   .Cast<ScheduleSheetInstance>()
                                   .ToList();
            // **On utilise OwnerViewId** pour connaître la feuille qui porte la nomenclature
            _schedulesBySheet = allSchedules
                                   .GroupBy(ssi => ssi.OwnerViewId)
                                   .ToDictionary(g => g.Key, g => g.ToList());
        }

        private void PopulateTreeView()
        {
            ViewsTreeView.Items.Clear();

            // 1) Repérer toutes les vues déjà placées sur une feuille
            var placedViewIds = new HashSet<ElementId>();
            foreach (var sheet in _allSheets)
            {
                if (_vpsBySheet.TryGetValue(sheet.Id, out var vps))
                    foreach (var vp in vps)
                        placedViewIds.Add(vp.ViewId);
            }

            // 2) Groupe "VUES indépendantes"
            var independentViews = _allViews
                                        .Where(v => !placedViewIds.Contains(v.Id))
                                        .ToList();
            if (independentViews.Any())
            {
                var chkGroupVues = new CheckBox
                {
                    Content = "VUES (cocher/décocher tout)",
                    IsChecked = false,
                    Foreground = this.Foreground
                };
                _viewsGroupCheckBox = chkGroupVues;
                var groupVues = new TreeViewItem
                {
                    Header = chkGroupVues,
                    Tag = independentViews, // stocke les données pour lazy‑load
                    IsExpanded = false
                };
                chkGroupVues.Checked += (s, e) => SetChildCheckBoxes(groupVues, true);
                chkGroupVues.Unchecked += (s, e) => SetChildCheckBoxes(groupVues, false);

                // placeholder pour déclencher le lazy‑load
                groupVues.Items.Add((object)null);
                groupVues.Expanded += (s, e) =>
                {
                    if (groupVues.Items.Count == 1 && groupVues.Items[0] == null)
                    {
                        groupVues.Items.Clear();
                        var listVues = (List<View>)groupVues.Tag;
                        foreach (var view in listVues)
                        {
                            var cb = new CheckBox
                            {
                                Content = $"{GetViewTypeLabel(view)} : {view.Name}",
                                Tag = view.Id,
                                Foreground = this.Foreground,
                                IsChecked = _viewsGroupCheckBox?.IsChecked == true
                            };
                            groupVues.Items.Add(new TreeViewItem { Header = cb });
                        }
                    }
                };

                ViewsTreeView.Items.Add(groupVues);
            }

            // 3) Groupe "FEUILLES"
            if (_allSheets.Any())
            {
                var chkGroupSheets = new CheckBox
                {
                    Content = "FEUILLES (cocher/décocher tout)",
                    IsChecked = false,
                    Foreground = this.Foreground
                };
                _sheetsGroupCheckBox = chkGroupSheets;
                var groupSheets = new TreeViewItem
                {
                    Header = chkGroupSheets,
                    Tag = _allSheets, // lazy‑load
                    IsExpanded = false
                };
                chkGroupSheets.Checked += (s, e) => SetChildCheckBoxes(groupSheets, true);
                chkGroupSheets.Unchecked += (s, e) => SetChildCheckBoxes(groupSheets, false);

                groupSheets.Items.Add((object)null);
                groupSheets.Expanded += (s, e) =>
                {
                    if (groupSheets.Items.Count == 1 && groupSheets.Items[0] == null)
                    {
                        groupSheets.Items.Clear();
                        var listSheets = (List<ViewSheet>)groupSheets.Tag;
                        foreach (var sheet in listSheets)
                        {
                            var sheetCb = new CheckBox
                            {
                                Content = $"Feuille : {sheet.SheetNumber} - {sheet.Name}",
                                Tag = sheet.Id,
                                Foreground = this.Foreground,
                                IsChecked = _sheetsGroupCheckBox?.IsChecked == true
                            };
                            var sheetItem = new TreeViewItem
                            {
                                Header = sheetCb,
                                IsExpanded = false
                            };
                            sheetCb.Checked += (s2, e2) => SetChildCheckBoxes(sheetItem, true);
                            sheetCb.Unchecked += (s2, e2) => SetChildCheckBoxes(sheetItem, false);

                            // (A) Vues placées sur cette feuille
                            if (_vpsBySheet.TryGetValue(sheet.Id, out var vps))
                            {
                                foreach (var vp in vps)
                                {
                                    if (!(_doc.GetElement(vp.ViewId) is View placedView)) continue;
                                    var cbChild = new CheckBox
                                    {
                                        Content = $"{GetViewTypeLabel(placedView)} : {placedView.Name}",
                                        Tag = placedView.Id,
                                        Foreground = this.Foreground,
                                        IsChecked = _sheetsGroupCheckBox?.IsChecked == true
                                    };
                                    sheetItem.Items.Add(new TreeViewItem { Header = cbChild });
                                }
                            }

                            // (B) Nomenclatures placées sur cette feuille
                            if (_schedulesBySheet.TryGetValue(sheet.Id, out var ssiList))
                            {
                                foreach (var ssi in ssiList)
                                {
                                    if (!(_doc.GetElement(ssi.ScheduleId) is ViewSchedule vsched)) continue;
                                    var cbChild = new CheckBox
                                    {
                                        Content = $"Nomenclature : {vsched.Name}",
                                        Tag = vsched.Id,
                                        Foreground = this.Foreground,
                                        IsChecked = _sheetsGroupCheckBox?.IsChecked == true
                                    };
                                    sheetItem.Items.Add(new TreeViewItem { Header = cbChild });
                                }
                            }

                            groupSheets.Items.Add(sheetItem);
                        }
                    }
                };

                ViewsTreeView.Items.Add(groupSheets);
            }
        }

        /// <summary>
        /// Coche/décoche récursivement tous les enfants d'un TreeViewItem.
        /// </summary>
        private void SetChildCheckBoxes(TreeViewItem parentItem, bool isChecked)
        {
            foreach (var child in parentItem.Items)
            {
                if (child is TreeViewItem cItem)
                {
                    if (cItem.Header is CheckBox cb)
                        cb.IsChecked = isChecked;
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

        /// <summary>
        /// Récupère la liste des ElementId cochés dans l'arbre.
        /// </summary>
        public List<ElementId> GetSelectedElementIds()
        {
            var selected = new HashSet<ElementId>();
            foreach (var top in ViewsTreeView.Items)
            {
                if (top is TreeViewItem tvi)
                    CollectCheckedElementIds(tvi, selected);
            }
            // Si l'utilisateur coche un groupe sans le déplier (lazy-load),
            // on ajoute explicitement les éléments correspondants.
            if (_viewsGroupCheckBox?.IsChecked == true)
            {
                var placedViewIds = new HashSet<ElementId>();
                foreach (var sheet in _allSheets)
                {
                    if (_vpsBySheet.TryGetValue(sheet.Id, out var vps))
                        foreach (var vp in vps)
                            placedViewIds.Add(vp.ViewId);
                }

                foreach (var view in _allViews.Where(v => !placedViewIds.Contains(v.Id)))
                    selected.Add(view.Id);
            }

            if (_sheetsGroupCheckBox?.IsChecked == true)
            {
                foreach (var sheet in _allSheets)
                {
                    selected.Add(sheet.Id);

                    if (_vpsBySheet.TryGetValue(sheet.Id, out var vps))
                        foreach (var vp in vps)
                            selected.Add(vp.ViewId);

                    if (_schedulesBySheet.TryGetValue(sheet.Id, out var ssiList))
                        foreach (var ssi in ssiList)
                            selected.Add(ssi.ScheduleId);
                }
            }

            return selected.ToList();
        }

        private void CollectCheckedElementIds(TreeViewItem item, HashSet<ElementId> list)
        {
            if (item.Header is CheckBox cb && cb.IsChecked == true && cb.Tag is ElementId id)
                list.Add(id);

            foreach (var child in item.Items)
                if (child is TreeViewItem cItem)
                    CollectCheckedElementIds(cItem, list);
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

       
    }

}

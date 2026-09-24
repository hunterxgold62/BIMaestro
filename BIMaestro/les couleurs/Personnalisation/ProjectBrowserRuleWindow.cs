using BrowserOrganization = Autodesk.Revit.DB.BrowserOrganization;
using Document = Autodesk.Revit.DB.Document;
using FilteredElementCollector = Autodesk.Revit.DB.FilteredElementCollector;
using FolderItemInfo = Autodesk.Revit.DB.FolderItemInfo;
using View = Autodesk.Revit.DB.View;
using ViewSheet = Autodesk.Revit.DB.ViewSheet;
using ViewSchedule = Autodesk.Revit.DB.ViewSchedule;
using BIMaestro.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;

namespace Couleur
{
    /// <summary>Interactive copy of the current Revit view browser.</summary>
    public sealed class ProjectBrowserRuleWindow : Window
    {
        private sealed class BrowserNode
        {
            public string Name;
            public string SortKey;
            public string FolderPath;
            public bool IsFolder;
            public bool IsEditable;
            public int Depth;
            public TreeViewItem Item;
            public Border Row;
            public TextBlock Label;
            public TextBlock Prefix;
            public TextBlock Adornment;
            public Color? ViewColor;
            public readonly List<BrowserNode> Children = new List<BrowserNode>();
        }

        private readonly Action<ProjectBrowserCategoryColorRule, ProjectBrowserCategoryColorRule, Action<Exception>> _apply;
        private readonly Action<ProjectBrowserCategoryColorRule, Action<Exception>> _remove;
        private readonly Action<Action<ProjectBrowserColorSettings, bool, Exception>> _undo;
        private readonly ProjectBrowserColorSettings _settings;
        private readonly List<ProjectBrowserCategoryColorRule> _rules;
        private readonly List<BrowserNode> _nodes = new List<BrowserNode>();
        private readonly List<BrowserNode> _roots = new List<BrowserNode>();
        private readonly TreeView _tree = new TreeView();
        private readonly TextBox _search = new TextBox();
        private readonly ComboBox _effect = new ComboBox();
        private readonly SimpleColorPicker _color = new SimpleColorPicker();
        private readonly TextBlock _path = new TextBlock();
        private readonly TextBlock _rulesSummary = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBlock _effectHint = new TextBlock();
        private readonly Button _applyButton = new Button();
        private readonly Button _undoButton = new Button();
        private readonly Button _removeButton = new Button();
        private readonly bool _appliesToProject;
        private readonly RadioButton _folderOnly = new RadioButton();
        private readonly RadioButton _contentsOnly = new RadioButton();
        private readonly RadioButton _wholeBranch = new RadioButton();
        private BrowserNode _root;
        private bool _loading;
        private bool _isApplying;
        private bool _suppressDraft;
        private bool _draftDirty;
        private ProjectBrowserCategoryColorRule _appliedRule;

        public ProjectBrowserRuleWindow(
            Document document,
            ProjectBrowserColorSettings settings,
            Action<ProjectBrowserCategoryColorRule, ProjectBrowserCategoryColorRule, Action<Exception>> apply,
            Action<ProjectBrowserCategoryColorRule, Action<Exception>> remove,
            Action<Action<ProjectBrowserColorSettings, bool, Exception>> undo,
            bool canUndo,
            ProjectBrowserCategoryColorRule existing = null)
        {
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _remove = remove ?? throw new ArgumentNullException(nameof(remove));
            _undo = undo ?? throw new ArgumentNullException(nameof(undo));
            _settings = ProjectBrowserColorPreferences.Clone(settings);
            _appliesToProject = document != null && !document.IsReadOnly && !document.IsFamilyDocument;
            _appliedRule = existing?.Clone();
            _rules = _settings.CategoryColorRules
                .Select(rule => rule.Clone()).ToList();
            Title = "Colorer un dossier dans les vues du projet";
            Width = 1160;
            Height = 760;
            MinWidth = 850;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var title = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            title.Children.Add(new TextBlock
            {
                Text = "Choisissez un dossier et voyez le résultat dans les vues du projet",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
            });
            title.Children.Add(new TextBlock
            {
                Text = "Choisissez un dossier, réglez son effet et vérifiez-le à gauche. Les effets se cumulent ; Annuler restaure la dernière application.",
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(title);

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(columns, 1);
            root.Children.Add(columns);

            var browser = new Border
            {
                Background = _settings.IsEnabled ? new SolidColorBrush(_settings.BackgroundColor) : Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            Grid.SetColumn(browser, 0);
            columns.Children.Add(browser);
            var browserLayout = new Grid();
            browserLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            browserLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            browserLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            browser.Child = browserLayout;
            var browserBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(231, 234, 238)),
                Padding = new Thickness(10, 6, 10, 6),
                Child = new TextBlock { Text = "⌂   Vues du projet — " + document.Title, FontWeight = FontWeights.SemiBold }
            };
            browserLayout.Children.Add(browserBar);
            var searchPanel = new Grid();
            Grid.SetRow(searchPanel, 1);
            browserLayout.Children.Add(searchPanel);
            _search.Margin = new Thickness(10, 8, 10, 8);
            _search.Height = 30;
            _search.ToolTip = "Rechercher un dossier ou une vue";
            _search.Padding = new Thickness(28, 3, 6, 3);
            searchPanel.Children.Add(_search);
            var searchHint = new TextBlock
            {
                Text = "⌕  Rechercher",
                Foreground = Brushes.Gray,
                Margin = new Thickness(19, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            searchPanel.Children.Add(searchHint);
            _search.TextChanged += (_, __) =>
            {
                searchHint.Visibility = string.IsNullOrEmpty(_search.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
                FilterTree();
            };
            _tree.BorderThickness = new Thickness(0);
            _tree.Background = _settings.IsEnabled ? new SolidColorBrush(_settings.BackgroundColor) : Brushes.White;
            _tree.SelectedItemChanged += (_, __) => OnSelectionChanged();
            Grid.SetRow(_tree, 2);
            browserLayout.Children.Add(_tree);

            var editor = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16)
            };
            Grid.SetColumn(editor, 2);
            columns.Children.Add(editor);
            var editorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            editor.Child = editorScroll;
            var fields = new StackPanel();
            editorScroll.Content = fields;
            fields.Children.Add(new TextBlock { Text = "Dossier choisi", FontWeight = FontWeights.SemiBold });
            _path.Text = "Cliquez sur un dossier à gauche";
            _path.TextWrapping = TextWrapping.Wrap;
            _path.Margin = new Thickness(0, 5, 0, 6);
            fields.Children.Add(_path);
            _rulesSummary.TextWrapping = TextWrapping.Wrap;
            _rulesSummary.Foreground = Brushes.DimGray;
            _rulesSummary.Margin = new Thickness(0, 0, 0, 18);
            fields.Children.Add(_rulesSummary);

            fields.Children.Add(new TextBlock { Text = "Apparence", FontWeight = FontWeights.SemiBold });
            foreach (string effect in new[]
            {
                "Fond", "Texte", "Souligné", "Gras", "Italique", "Barré",
                "Bordure", "Pastille", "Icône", "Badge", "Dégradé", "Animation"
            }) _effect.Items.Add(CreateEffectChoice(effect));
            _effect.SelectedIndex = 0;
            _effect.Margin = new Thickness(0, 6, 0, 16);
            _effect.SelectionChanged += (_, __) => OnEffectChanged();
            fields.Children.Add(_effect);
            _removeButton.Content = "Supprimer cet effet appliqué";
            _removeButton.Margin = new Thickness(0, -8, 0, 12);
            _removeButton.IsEnabled = false;
            _removeButton.Click += (_, __) => RemoveSelectedEffect();
            fields.Children.Add(_removeButton);
            _effectHint.TextWrapping = TextWrapping.Wrap;
            _effectHint.Foreground = Brushes.DimGray;
            _effectHint.Margin = new Thickness(0, -9, 0, 12);
            fields.Children.Add(_effectHint);

            fields.Children.Add(new TextBlock { Text = "Couleur", FontWeight = FontWeights.SemiBold });
            _color.SelectedColor = Color.FromRgb(93, 159, 229);
            _color.Margin = new Thickness(0, 6, 0, 18);
            _color.SelectedColorChanged += (_, __) => MarkDraftDirty();
            fields.Children.Add(_color);

            fields.Children.Add(new TextBlock { Text = "Où afficher cet effet ?", FontWeight = FontWeights.SemiBold });
            _wholeBranch.Content = "Le dossier et tout son contenu";
            _contentsOnly.Content = "Uniquement ce qu’il contient";
            _folderOnly.Content = "Uniquement le dossier";
            _wholeBranch.IsChecked = true;
            foreach (RadioButton choice in new[] { _wholeBranch, _contentsOnly, _folderOnly })
            {
                choice.GroupName = "BrowserRuleScope";
                choice.Margin = new Thickness(0, 9, 0, 0);
                choice.Checked += (_, __) => MarkDraftDirty();
                fields.Children.Add(choice);
            }
            fields.Children.Add(new TextBlock
            {
                Text = "Le résultat s’affiche à gauche avant d’être appliqué à Revit.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 20, 0, 0)
            });

            var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            _status.Text = "Aperçu en direct — aucune modification enregistrée";
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Foreground = Brushes.DimGray;
            footer.Children.Add(_status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            _undoButton.Content = "Annuler la dernière application";
            _undoButton.MinWidth = 180;
            _undoButton.Height = 34;
            _undoButton.IsEnabled = canUndo;
            _undoButton.Margin = new Thickness(0, 0, 8, 0);
            _undoButton.Click += (_, __) => Undo();
            _applyButton.Content = _appliesToProject ? "Appliquer à Revit et au projet" : "Appliquer à Revit (personnel)";
            _applyButton.MinWidth = 205;
            _applyButton.Height = 34;
            _applyButton.Margin = new Thickness(0, 0, 8, 0);
            _applyButton.Click += (_, __) => Apply();
            var closeButton = new Button { Content = "Fermer", MinWidth = 90, Height = 34 };
            closeButton.Click += (_, __) => Close();
            Closing += (_, args) =>
            {
                if (_isApplying)
                {
                    args.Cancel = true;
                    _status.Text = "Patientez : Revit applique ce style au projet…";
                }
            };
            actions.Children.Add(_undoButton);
            actions.Children.Add(_applyButton);
            actions.Children.Add(closeButton);
            Grid.SetColumn(actions, 1);
            footer.Children.Add(actions);

            _loading = true;
            LoadBrowser(document);
            if (existing != null)
            {
                var match = _nodes.FirstOrDefault(node => node.IsFolder &&
                    string.Equals(node.FolderPath, existing.FolderPath, StringComparison.OrdinalIgnoreCase));
                if (match != null) SelectNode(match);
            }
            _loading = false;
            OnSelectionChanged();
            if (existing != null) LoadRuleIntoEditor(existing);
            UpdateTreePreview();
        }

        private void LoadBrowser(Document document)
        {
            BrowserOrganization organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(document);
            LoadSection(document, organization, "Vues", view => view.ViewType != Autodesk.Revit.DB.ViewType.Legend &&
                !(view is ViewSchedule) && !(view is ViewSheet), true);
            LoadSection(document, organization, "Légendes", view => view.ViewType == Autodesk.Revit.DB.ViewType.Legend, false);
            LoadSection(document, BrowserOrganization.GetCurrentBrowserOrganizationForSchedules(document),
                "Nomenclatures/Quantités", view => view is ViewSchedule, false);
            LoadSection(document, BrowserOrganization.GetCurrentBrowserOrganizationForSheets(document),
                "Feuilles", view => view is ViewSheet, false);
        }

        private void LoadSection(Document document, BrowserOrganization organization, string section,
            Func<View, bool> include, bool editable)
        {
            string rootName = "Vues";
            rootName = section;
            try
            {
                if (!string.IsNullOrWhiteSpace(organization?.Name))
                    rootName += " (" + organization.Name + ")";
            }
            catch { }
            BrowserNode sectionRoot = AddNode(null, rootName, string.Empty, true);
            sectionRoot.IsEditable = false;
            _roots.Add(sectionRoot);
            if (editable) _root = sectionRoot;
            sectionRoot.Item.IsExpanded = editable;
            if (organization == null) return;
            var folders = new Dictionary<string, BrowserNode>(StringComparer.OrdinalIgnoreCase);
            foreach (View view in new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (view.IsTemplate || !include(view) || !organization.AreFiltersSatisfied(view.Id)) continue;
                IList<FolderItemInfo> items = null;
                try
                {
                    items = organization.GetFolderItems(view.Id)?.Where(item => item != null).ToList();
                    if (items == null) continue;
                    BrowserNode parent = sectionRoot;
                    var parts = new List<string>();
                    foreach (FolderItemInfo folder in items)
                    {
                        string name = folder.Name?.Trim();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        parts.Add(name);
                        string path = string.Join(" / ", parts);
                        if (!folders.TryGetValue(path, out BrowserNode node))
                        {
                            node = AddNode(parent, name, path, true);
                            node.IsEditable = editable;
                            folders.Add(path, node);
                        }
                        parent = node;
                    }
                    string leafName = editable ? ViewLabel(view) :
                        view is ViewSheet sheet ? sheet.SheetNumber + " - " + sheet.Name : view.Name;
                    BrowserNode leaf = AddNode(parent, leafName, string.Join(" / ", parts), view is ViewSheet);
                    leaf.ViewColor = ViewColor(view);
                    if (view is ViewSheet placedSheet)
                    {
                        foreach (var placedId in placedSheet.GetAllPlacedViews())
                        {
                            if (!(document.GetElement(placedId) is View placedView)) continue;
                            var placedNode = AddNode(leaf, ViewLabel(placedView),
                                string.Join(" / ", parts), false);
                            placedNode.ViewColor = ViewColor(placedView);
                        }
                    }
                    try
                    {
                        var parameter = view.Parameters.Cast<Autodesk.Revit.DB.Parameter>()
                            .FirstOrDefault(item => item.Id.Equals(organization.SortingParameterId));
                        leaf.SortKey = parameter?.AsString() ?? parameter?.AsValueString() ?? leaf.Name;
                    }
                    catch { leaf.SortKey = leaf.Name; }
                }
                finally
                {
                    if (items != null)
                        foreach (FolderItemInfo item in items) item.Dispose();
                }
            }
            SortChildren(sectionRoot, organization.SortingOrder == Autodesk.Revit.DB.SortingOrder.Descending);
        }

        private static void SortChildren(BrowserNode parent, bool descending)
        {
            var ordered = descending
                ? parent.Children.OrderByDescending(node => node.SortKey, StringComparer.CurrentCultureIgnoreCase).ToList()
                : parent.Children.OrderBy(node => node.SortKey, StringComparer.CurrentCultureIgnoreCase).ToList();
            parent.Children.Clear();
            parent.Item.Items.Clear();
            foreach (BrowserNode child in ordered)
            {
                parent.Children.Add(child);
                parent.Item.Items.Add(child.Item);
                SortChildren(child, descending);
            }
        }

        private static string ViewLabel(View view)
        {
            string kind = view.ViewType.ToString();
            if (kind == "FloorPlan") kind = "Plan d’étage";
            else if (kind == "CeilingPlan") kind = "Plan de plafond";
            else if (kind == "Section") kind = "Coupe";
            else if (kind == "ThreeD") kind = "Vue 3D";
            else if (kind == "Elevation") kind = "Élévation";
            return kind + ": " + view.Name;
        }

        private Color? ViewColor(View view)
        {
            if (!_settings.IsViewTypeColoringEnabled) return null;
            switch (view.ViewType.ToString())
            {
                case "FloorPlan": case "CeilingPlan": case "EngineeringPlan": return _settings.PlanViewColor;
                case "Section": return _settings.SectionViewColor;
                case "ThreeD": return _settings.ThreeDViewColor;
                case "Elevation": return _settings.ElevationViewColor;
                case "Schedule": return _settings.ScheduleViewColor;
                default: return _settings.OtherViewColor;
            }
        }

        private BrowserNode AddNode(BrowserNode parent, string name, string path, bool folder)
        {
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var prefix = new TextBlock { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            var adornment = new TextBlock { Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = folder ? "▤" : "▣",
                Foreground = folder ? Brushes.SlateGray : new SolidColorBrush(Color.FromRgb(85, 155, 202)),
                Margin = new Thickness(0, 0, 6, 0)
            });
            header.Children.Add(prefix);
            header.Children.Add(label);
            header.Children.Add(adornment);
            var row = new Border
            {
                Child = header, Padding = new Thickness(4, 2, 6, 2),
                MinHeight = 21, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var item = new TreeViewItem
            {
                Header = row, Tag = path,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var node = new BrowserNode
            {
                Name = name,
                SortKey = name,
                FolderPath = path,
                IsFolder = folder,
                IsEditable = parent != null && parent.IsEditable,
                Depth = parent == null ? 0 : parent.Depth + 1,
                Item = item,
                Row = row,
                Label = label,
                Prefix = prefix,
                Adornment = adornment
            };
            item.IsExpanded = folder && node.Depth <= 2;
            item.Tag = node;
            if (parent == null) _tree.Items.Add(item);
            else { parent.Children.Add(node); parent.Item.Items.Add(item); }
            _nodes.Add(node);
            return node;
        }

        private void SelectNode(BrowserNode node)
        {
            for (var parent = node.Item.Parent as TreeViewItem; parent != null; parent = parent.Parent as TreeViewItem)
                parent.IsExpanded = true;
            node.Item.IsSelected = true;
            node.Item.BringIntoView();
        }

        private void OnSelectionChanged()
        {
            if (_loading) return;
            if (!(_tree.SelectedItem is TreeViewItem item) || !(item.Tag is BrowserNode node)) return;
            if (!node.IsFolder && item.Parent is TreeViewItem parent)
            {
                parent.IsSelected = true;
                return;
            }
            _path.Text = !node.IsEditable ? "Cette section est affichée pour comparaison ; choisissez un dossier sous Vues."
                : "Vues / " + node.FolderPath;
            var saved = node.IsEditable
                ? _rules.FirstOrDefault(rule => string.Equals(rule.FolderPath, node.FolderPath,
                    StringComparison.OrdinalIgnoreCase)) : null;
            LoadRuleIntoEditor(saved);
        }

        private void LoadRuleIntoEditor(ProjectBrowserCategoryColorRule rule)
        {
            bool wasLoading = _loading;
            _loading = true;
            if (rule != null)
            {
                _effect.SelectedItem = _effect.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string,
                        string.IsNullOrWhiteSpace(rule.Effect) ? "Fond" : rule.Effect,
                        StringComparison.OrdinalIgnoreCase));
                _color.SelectedColor = rule.Color;
                _folderOnly.IsChecked = rule.Scope == "Dossier";
                _contentsOnly.IsChecked = rule.Scope == "Enfants";
                _wholeBranch.IsChecked = rule.Scope != "Dossier" && rule.Scope != "Enfants";
            }
            _loading = wasLoading;
            _appliedRule = rule?.Clone();
            _draftDirty = false;
            _suppressDraft = false;
            _removeButton.IsEnabled = rule != null && !_isApplying;
            UpdateTreePreview();
        }

        private void OnEffectChanged()
        {
            if (_loading) return;
            var draft = DraftRule();
            var saved = draft == null ? null : _rules.FirstOrDefault(rule => SameRule(rule, draft));
            if (saved != null) LoadRuleIntoEditor(saved);
            else MarkDraftDirty();
        }

        private void MarkDraftDirty()
        {
            if (_loading) return;
            _draftDirty = true;
            _suppressDraft = false;
            UpdateTreePreview();
        }

        private ProjectBrowserCategoryColorRule DraftRule()
        {
            if (!(_tree.SelectedItem is TreeViewItem item) ||
                !(item.Tag is BrowserNode node) || !node.IsFolder || !node.IsEditable ||
                string.IsNullOrWhiteSpace(node.FolderPath)) return null;
            return new ProjectBrowserCategoryColorRule
            {
                CategoryName = node.Name,
                FolderPath = node.FolderPath,
                Color = _color.SelectedColor ?? Colors.LightBlue,
                Effect = SelectedEffect,
                Scope = _folderOnly.IsChecked == true ? "Dossier" :
                    _contentsOnly.IsChecked == true ? "Enfants" : "Branche"
            };
        }

        private static bool Matches(ProjectBrowserCategoryColorRule rule, BrowserNode node)
        {
            if (rule == null || !node.IsEditable || string.IsNullOrWhiteSpace(rule.CategoryName)) return false;
            string path = rule.FolderPath;
            if (string.IsNullOrWhiteSpace(path))
                return node.FolderPath.Split(new[] { " / " }, StringSplitOptions.None)
                    .Any(part => string.Equals(part, rule.CategoryName, StringComparison.OrdinalIgnoreCase));
            bool sameFolder = string.Equals(node.FolderPath, path, StringComparison.OrdinalIgnoreCase);
            bool nestedFolder = node.FolderPath.StartsWith(path + " / ", StringComparison.OrdinalIgnoreCase);
            if (!sameFolder && !nestedFolder) return false;
            if (rule.Scope == "Dossier") return node.IsFolder && sameFolder;
            if (rule.Scope == "Enfants") return !node.IsFolder || nestedFolder;
            return true;
        }

        private void UpdateTreePreview()
        {
            if (_loading || _root == null) return;
            ProjectBrowserCategoryColorRule draft = _suppressDraft || !_draftDirty ? null : DraftRule();
            _applyButton.IsEnabled = draft != null && !_isApplying;
            _effectHint.Text = EffectExplanation(SelectedEffect);
            if (_tree.SelectedItem is TreeViewItem selected && selected.Tag is BrowserNode selectedNode && selectedNode.IsEditable)
            {
                var visibleRules = _settings.IsCategoryColoringEnabled
                    ? _rules.Where(rule => Matches(rule, selectedNode)).Take(5).ToList()
                    : new List<ProjectBrowserCategoryColorRule>();
                _rulesSummary.Text = visibleRules.Count == 0
                    ? "Aucun effet enregistré sur cette branche."
                    : "Effets déjà présents : " + string.Join(" · ", visibleRules.Select(rule =>
                        (string.IsNullOrWhiteSpace(rule.Effect) ? "Fond" : rule.Effect) + " " +
                        ProjectBrowserColorPreferences.ToHex(rule.Color) + " (" + rule.FolderPath + ")"));
            }
            else _rulesSummary.Text = string.Empty;
            foreach (BrowserNode node in _nodes)
            {
                var applicable = _rules.Where(candidate => _settings.IsCategoryColoringEnabled &&
                    Matches(candidate, node) &&
                    (draft == null || !SameRule(candidate, draft)))
                    .OrderBy(candidate => (candidate.FolderPath ?? string.Empty).Length).ToList();
                if (draft != null && Matches(draft, node)) applicable.Add(draft);
                PaintNode(node, applicable);
            }
            _status.Text = draft == null
                ? "Styles appliqués — modifiez un réglage pour prévisualiser"
                : "Aperçu non enregistré — appliquez lorsque le résultat vous convient";
        }

        private string SelectedEffect => (_effect.SelectedItem as ComboBoxItem)?.Tag as string ?? "Fond";

        private static ComboBoxItem CreateEffectChoice(string effect)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            string symbol;
            switch (effect)
            {
                case "Pastille": symbol = "●"; break;
                case "Icône": symbol = "✦"; break;
                case "Badge": symbol = "★"; break;
                case "Bordure": symbol = "▏"; break;
                case "Dégradé": symbol = "▥"; break;
                case "Animation": symbol = "◉"; break;
                case "Souligné": symbol = "U̲"; break;
                case "Barré": symbol = "S̶"; break;
                case "Gras": symbol = "B"; break;
                case "Italique": symbol = "I"; break;
                case "Fond": symbol = "▰"; break;
                default: symbol = "A"; break;
            }
            row.Children.Add(new TextBlock
            {
                Text = symbol,
                Width = 26,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 164)),
                FontWeight = effect == "Gras" ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = effect == "Italique" ? FontStyles.Italic : FontStyles.Normal
            });
            var label = new TextBlock
            {
                Text = effect,
                FontWeight = effect == "Gras" ? FontWeights.ExtraBold : FontWeights.Normal,
                FontStyle = effect == "Italique" ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = effect == "Souligné" ? TextDecorations.Underline :
                    effect == "Barré" ? TextDecorations.Strikethrough : null
            };
            row.Children.Add(label);
            return new ComboBoxItem { Tag = effect, Content = row };
        }

        private static string EffectExplanation(string effect)
        {
            switch (effect)
            {
                case "Fond": return "Colore le fond de la ligne.";
                case "Texte": return "Colore les lettres du nom.";
                case "Souligné": return "Souligne et colore le nom.";
                case "Gras": return "Épaissit et colore le nom.";
                case "Italique": return "Incline et colore le nom.";
                case "Barré": return "Barre et colore le nom.";
                case "Bordure": return "Ajoute un trait coloré à gauche.";
                case "Pastille": return "Ajoute un point coloré devant le nom.";
                case "Icône": return "Ajoute une étoile colorée devant le nom.";
                case "Badge": return "Ajoute une étoile colorée après le nom.";
                case "Dégradé": return "Colore le fond avec un dégradé.";
                case "Animation": return "Fait pulser doucement le repère coloré.";
                default: return string.Empty;
            }
        }

        private static bool SameRule(ProjectBrowserCategoryColorRule a, ProjectBrowserCategoryColorRule b)
        {
            return a != null && b != null &&
                string.Equals(a.FolderPath, b.FolderPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Effect, b.Effect, StringComparison.OrdinalIgnoreCase);
        }

        private void PaintNode(BrowserNode node, IList<ProjectBrowserCategoryColorRule> rules)
        {
            node.Row.BeginAnimation(UIElement.OpacityProperty, null);
            node.Row.Opacity = 1;
            node.Row.Background = Brushes.Transparent;
            node.Row.BorderBrush = Brushes.Transparent;
            node.Row.BorderThickness = new Thickness(0);
            node.Label.Foreground = _settings.IsEnabled
                ? new SolidColorBrush(_settings.TextColor) : Brushes.Black;
            node.Label.FontWeight = FontWeights.Normal;
            node.Label.FontStyle = FontStyles.Normal;
            node.Label.TextDecorations = null;
            node.Prefix.Text = string.Empty;
            node.Adornment.Text = string.Empty;
            if (node.ViewColor.HasValue)
            {
                var viewBrush = new SolidColorBrush(node.ViewColor.Value);
                if (_settings.ViewColorTarget == "Texte") node.Label.Foreground = viewBrush;
                else node.Row.Background = viewBrush;
            }
            foreach (var rule in rules)
            {
              var color = new SolidColorBrush(rule.Color);
              switch (rule.Effect)
              {
                case "Texte": node.Label.Foreground = color; break;
                case "Souligné": node.Label.Foreground = color; node.Label.TextDecorations = TextDecorations.Underline; break;
                case "Gras": node.Label.Foreground = color; node.Label.FontWeight = FontWeights.ExtraBold; break;
                case "Italique": node.Label.Foreground = color; node.Label.FontStyle = FontStyles.Italic; break;
                case "Barré": node.Label.Foreground = color; node.Label.TextDecorations = TextDecorations.Strikethrough; break;
                case "Bordure": node.Row.BorderBrush = color; node.Row.BorderThickness = new Thickness(4, 0, 0, 0); break;
                case "Pastille": node.Prefix.Text = "●"; node.Prefix.Foreground = color; break;
                case "Icône": node.Prefix.Text = "✦"; node.Prefix.Foreground = color; break;
                case "Badge": node.Adornment.Text = "★"; node.Adornment.Foreground = color; break;
                case "Dégradé": node.Row.Background = new LinearGradientBrush(rule.Color, Colors.Transparent, 0); break;
                case "Animation":
                    node.Row.BorderBrush = color;
                    node.Row.BorderThickness = new Thickness(3, 0, 0, 0);
                    node.Row.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0.5, 1, TimeSpan.FromSeconds(0.9))
                        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                    break;
                default: node.Row.Background = color; break;
              }
            }
        }

        private void FilterTree()
        {
            if (_roots.Count == 0) return;
            string query = (_search.Text ?? string.Empty).Trim();
            foreach (var root in _roots) FilterNode(root, query);
        }

        private static bool FilterNode(BrowserNode node, string query, bool parentMatches = false)
        {
            bool own = node.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
            bool child = false;
            foreach (BrowserNode descendant in node.Children)
                child |= FilterNode(descendant, query, parentMatches || own);
            bool show = query.Length == 0 || parentMatches || own || child;
            node.Item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (query.Length > 0 && child) node.Item.IsExpanded = true;
            return show;
        }

        private void Apply()
        {
            var rule = _draftDirty ? DraftRule() : null;
            if (rule == null)
            {
                _status.Text = "Sélectionnez d’abord un dossier dans l’arborescence";
                return;
            }
            try
            {
                _isApplying = true;
                _applyButton.IsEnabled = false;
                _status.Text = _appliesToProject ? "Application au projet en cours…" : "Application en cours…";
                _apply(rule, null, error =>
                {
                    _isApplying = false;
                    _applyButton.IsEnabled = true;
                    if (error != null)
                    {
                        _status.Text = "Le style n’a pas été appliqué.";
                        MessageBox.Show(this, error.Message, "Application du style", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    _appliedRule = rule.Clone();
                    _rules.RemoveAll(item => SameRule(item, rule));
                    _rules.Insert(0, rule.Clone());
                    _settings.IsCategoryColoringEnabled = true;
                    _undoButton.IsEnabled = true;
                    LoadRuleIntoEditor(rule);
                    _status.Text = _appliesToProject
                        ? "Appliqué à Revit et à la maquette. Enregistrez ou synchronisez le projet pour le partager."
                        : "Appliqué à Revit sur ce poste. La maquette n’a pas été modifiée.";
                });
            }
            catch (Exception error)
            {
                _isApplying = false;
                _applyButton.IsEnabled = true;
                MessageBox.Show(this, error.Message, "Application du style", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveSelectedEffect()
        {
            var selected = DraftRule();
            var saved = selected == null ? null : _rules.FirstOrDefault(rule => SameRule(rule, selected));
            if (saved == null || _isApplying) return;
            _isApplying = true;
            _removeButton.IsEnabled = false;
            _applyButton.IsEnabled = false;
            _status.Text = "Suppression en cours…";
            _remove(saved.Clone(), error =>
            {
                _isApplying = false;
                if (error != null)
                {
                    _removeButton.IsEnabled = true;
                    _status.Text = "La suppression a échoué.";
                    MessageBox.Show(this, error.Message, "Supprimer le style", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _rules.RemoveAll(rule => SameRule(rule, saved));
                _settings.IsCategoryColoringEnabled = _rules.Count > 0;
                _undoButton.IsEnabled = true;
                LoadRuleIntoEditor(null);
                _status.Text = "Effet supprimé. Vous pouvez annuler cette opération.";
            });
        }

        private void Undo()
        {
            if (_isApplying) return;
            _isApplying = true;
            _undoButton.IsEnabled = false;
            _applyButton.IsEnabled = false;
            _status.Text = "Annulation en cours…";
            _undo((settings, hasMore, error) =>
            {
                _isApplying = false;
                if (error != null)
                {
                    _undoButton.IsEnabled = true;
                    _status.Text = "L’annulation a échoué.";
                    MessageBox.Show(this, error.Message, "Annuler le style", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateTreePreview();
                    return;
                }
                _rules.Clear();
                _rules.AddRange(settings.CategoryColorRules.Select(item => item.Clone()));
                _settings.IsCategoryColoringEnabled = settings.IsCategoryColoringEnabled;
                _appliedRule = null;
                _suppressDraft = true;
                _undoButton.IsEnabled = hasMore;
                var selected = DraftRule();
                var saved = selected == null ? null : _rules.FirstOrDefault(rule => SameRule(rule, selected));
                LoadRuleIntoEditor(saved);
                _status.Text = "Dernière application annulée.";
            });
        }
    }
}

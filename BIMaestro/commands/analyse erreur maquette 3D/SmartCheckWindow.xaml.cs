using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Modification;
using BIMaestro.Localization;

namespace Analyse
{
    public partial class SmartCheckWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/analyse?outil=clash-3d";
        private static readonly string[] StatusFilters =
        {
            "Tous",
            "Actives",
            ModelIssue.StatusActive,
            ModelIssue.StatusToFix,
            ModelIssue.StatusReview,
            ModelIssue.StatusFixed,
            ModelIssue.StatusIgnored
        };

        private readonly ExternalEvent _extEvent;
        private readonly SmartExternalHandler _handler;

        private readonly List<ModelIssue> _all;
        private readonly List<ModelIssue> _mepNoSleeve;
        private readonly List<ModelIssue> _linkClashes;
        private readonly List<ModelIssue> _openConnectors;
        private readonly string _docKey;
        private readonly string _thumbnailFolder;

        private List<ModelIssue> _filtered = new List<ModelIssue>();
        private List<IssueCard> _visualCards = new List<IssueCard>();
        private int _cursor = -1;
        private bool _suppressAutoFocus;
        private bool _isBindingFilters;

        public SmartCheckWindow(IEnumerable<ModelIssue> issues, ExternalEvent extEvent, SmartExternalHandler handler, string docKey)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _extEvent = extEvent;
            _handler = handler;
            _docKey = docKey;
            _thumbnailFolder = SmartCheckState.GetThumbnailFolder(docKey);

            _all = (issues ?? Enumerable.Empty<ModelIssue>()).ToList();
            _mepNoSleeve = _all.Where(i => i.Kind == IssueKind.MepThroughWallNoSleeve).ToList();
            _linkClashes = _all.Where(i => i.Kind == IssueKind.LinkPipeClash).ToList();
            _openConnectors = _all.Where(i => i.Kind == IssueKind.MepUnconnected).ToList();

            RestoreCachedThumbnails();
            PopulateFilters();
            Bind();

            GridAll.MouseDoubleClick += (s, e) => FocusFromGrid(GridAll);
            GridMEP.MouseDoubleClick += (s, e) => FocusFromGrid(GridMEP);
            GridLinks.MouseDoubleClick += (s, e) => FocusFromGrid(GridLinks);
            GridOpen.MouseDoubleClick += (s, e) => FocusFromGrid(GridOpen);

            GridAll.SelectionChanged += OnGridSelectionChanged;
            GridMEP.SelectionChanged += OnGridSelectionChanged;
            GridLinks.SelectionChanged += OnGridSelectionChanged;
            GridOpen.SelectionChanged += OnGridSelectionChanged;

            GridAll.PreviewMouseRightButtonDown += OnGridRightClick;
            GridMEP.PreviewMouseRightButtonDown += OnGridRightClick;
            GridLinks.PreviewMouseRightButtonDown += OnGridRightClick;
            GridOpen.PreviewMouseRightButtonDown += OnGridRightClick;
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiLanguage.T($"Impossible d’ouvrir la page d’aide : {ex.Message}", $"Unable to open the help page: {ex.Message}"), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void PopulateFilters()
        {
            _isBindingFilters = true;
            try
            {
                SeverityFilterCombo.ItemsSource = ToFilterOptions(new[] { "Toutes", "Critique", "À vérifier", "Info", "OK" });
                SeverityFilterCombo.SelectedIndex = 0;

                TypeFilterCombo.ItemsSource = ToFilterOptions(ValuesWithAll(_all.Select(i => i.Category), "Tous"));
                TypeFilterCombo.SelectedIndex = 0;

                StateFilterCombo.ItemsSource = ToFilterOptions(StatusFilters);
                StateFilterCombo.SelectedIndex = 0;

                LevelFilterCombo.ItemsSource = ToFilterOptions(ValuesWithAll(_all.Select(i => i.LevelName), "Tous"));
                LevelFilterCombo.SelectedIndex = 0;

                LinkFilterCombo.ItemsSource = ToFilterOptions(ValuesWithAll(_all.Select(i => i.LinkName), "Tous"));
                LinkFilterCombo.SelectedIndex = 0;

                ElementCategoryFilterCombo.ItemsSource = ToFilterOptions(ValuesWithAll(_all.Select(i => i.ElementCategory), "Toutes"));
                ElementCategoryFilterCombo.SelectedIndex = 0;

                VisualModeCombo.ItemsSource = ToFilterOptions(new[] { "Groupes intelligents", "Anomalies" });
                VisualModeCombo.SelectedIndex = 0;
            }
            finally
            {
                _isBindingFilters = false;
            }
        }

        private static List<string> ValuesWithAll(IEnumerable<string> values, string allLabel)
        {
            var list = (values ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(s => s)
                .ToList();
            list.Insert(0, allLabel);
            return list;
        }

        private sealed class FilterOption
        {
            public FilterOption(string value)
            {
                Value = value;
                Label = UiLanguage.T(value);
            }

            public string Value { get; }
            public string Label { get; }
            public override string ToString() => Label;
        }

        private static List<FilterOption> ToFilterOptions(IEnumerable<string> values)
            => (values ?? Enumerable.Empty<string>()).Select(value => new FilterOption(value)).ToList();

        private void Bind()
        {
            ApplyFilters();
            UpdateIssueCount();
        }

        private void ApplyFilters()
        {
            if (GridAll == null) return;

            IEnumerable<ModelIssue> query = _all;

            var severity = SelectedText(SeverityFilterCombo, "Toutes");
            if (severity == "OK")
                query = query.Where(i => i.Ignored);
            else if (severity != "Toutes")
                query = query.Where(i => !i.Ignored && string.Equals(i.SeverityText, severity, StringComparison.CurrentCultureIgnoreCase));

            var type = SelectedText(TypeFilterCombo, "Tous");
            if (type != "Tous")
                query = query.Where(i => string.Equals(i.Category, type, StringComparison.CurrentCultureIgnoreCase));

            var state = SelectedText(StateFilterCombo, "Tous");
            if (state == "Actives")
                query = query.Where(i => !i.Ignored);
            else if (state != "Tous")
                query = query.Where(i => string.Equals(i.StatusText, state, StringComparison.CurrentCultureIgnoreCase));

            var level = SelectedText(LevelFilterCombo, "Tous");
            if (level != "Tous")
                query = query.Where(i => string.Equals(i.LevelName, level, StringComparison.CurrentCultureIgnoreCase));

            var link = SelectedText(LinkFilterCombo, "Tous");
            if (link != "Tous")
                query = query.Where(i => string.Equals(i.LinkName, link, StringComparison.CurrentCultureIgnoreCase));

            var category = SelectedText(ElementCategoryFilterCombo, "Toutes");
            if (category != "Toutes")
                query = query.Where(i => string.Equals(i.ElementCategory, category, StringComparison.CurrentCultureIgnoreCase));

            var search = SearchBox?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var tokens = search.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                query = query.Where(i =>
                {
                    var text = i.SearchText ?? string.Empty;
                    return tokens.All(t => text.IndexOf(t, StringComparison.CurrentCultureIgnoreCase) >= 0);
                });
            }

            _filtered = query
                .OrderBy(i => i.PriorityRank)
                .ThenBy(i => StatusSort(i.StatusText))
                .ThenBy(i => i.LevelName)
                .ThenBy(i => i.LinkName)
                .ThenBy(i => i.Category)
                .ThenBy(i => i.ElementIdValue)
                .ToList();

            BindGrid(GridAll, _filtered);
            BindGrid(GridMEP, _filtered.Where(i => i.Kind == IssueKind.MepThroughWallNoSleeve));
            BindGrid(GridLinks, _filtered.Where(i => i.Kind == IssueKind.LinkPipeClash));
            BindGrid(GridOpen, _filtered.Where(i => i.Kind == IssueKind.MepUnconnected));

            RebuildVisualCards();
            EmptyStateText.Visibility = _filtered.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            UpdateResultText();
            UpdateActiveFilterText();
            UpdateIssueCount();
            UpdateBackToGroupsButton();
        }

        private void RebuildVisualCards()
        {
            foreach (var card in _visualCards)
                card.Dispose();

            var mode = SelectedText(VisualModeCombo, "Groupes intelligents");
            if (mode == "Anomalies")
            {
                _visualCards = _filtered.Select(i => new IssueCard(new[] { i })).ToList();
            }
            else
            {
                _visualCards = _filtered
                    .GroupBy(i => i.GroupKey)
                    .Select(g => new IssueCard(g.ToList()))
                    .OrderBy(c => c.PriorityRank)
                    .ThenByDescending(c => c.ActiveCount)
                    .ThenBy(c => c.VisualTitle)
                    .ToList();
            }

            VisualIssuesList.ItemsSource = null;
            VisualIssuesList.ItemsSource = _visualCards;
        }

        private void UpdateIssueCount()
        {
            var ignored = _all.Count(i => i.Ignored);
            var active = _all.Count - ignored;
            TotalStatText.Text = _all.Count.ToString();
            CriticalStatText.Text = _all.Count(i => !i.Ignored && i.Severity == IssueSeverity.Critical).ToString();
            CheckStatText.Text = _all.Count(i => !i.Ignored && i.Severity == IssueSeverity.Check).ToString();
            OkStatText.Text = ignored.ToString();
            ActiveStatText.Text = active.ToString();
        }

        private void UpdateResultText()
        {
            if (ResultText == null) return;
            var cardLabel = UiLanguage.IsEnglish
                ? (_visualCards.Count == 1 ? "card" : "cards")
                : (_visualCards.Count > 1 ? "cartes" : "carte");
            ResultText.Text = UiLanguage.T(
                $"{_filtered.Count} / {_all.Count} anomalies - {_visualCards.Count} {cardLabel}",
                $"{_filtered.Count} / {_all.Count} issues - {_visualCards.Count} {cardLabel}");
        }

        private void UpdateActiveFilterText()
        {
            if (ActiveFilterText == null) return;

            var parts = new List<string>();
            AddFilterPart(parts, SelectedText(SeverityFilterCombo, "Toutes"), "Toutes");
            AddFilterPart(parts, SelectedText(TypeFilterCombo, "Tous"), "Tous");
            AddFilterPart(parts, SelectedText(StateFilterCombo, "Tous"), "Tous");
            AddFilterPart(parts, SelectedText(LevelFilterCombo, "Tous"), "Tous");
            AddFilterPart(parts, SelectedText(LinkFilterCombo, "Tous"), "Tous");
            AddFilterPart(parts, SelectedText(ElementCategoryFilterCombo, "Toutes"), "Toutes");

            var search = SearchBox?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(search)) parts.Add(UiLanguage.T($"Recherche : {search}", $"Search: {search}"));

            ActiveFilterText.Text = parts.Count == 0
                ? UiLanguage.T("Aucun filtre actif", "No Active Filter")
                : UiLanguage.T("Filtre actif : ", "Active Filter: ") + string.Join(" · ", parts);
        }

        private static void AddFilterPart(ICollection<string> parts, string value, string allValue)
        {
            if (!string.IsNullOrWhiteSpace(value) && value != allValue)
                parts.Add(UiLanguage.T(value));
        }

        private static int StatusSort(string status)
        {
            if (status == ModelIssue.StatusToFix) return 0;
            if (status == ModelIssue.StatusReview) return 1;
            if (status == ModelIssue.StatusActive) return 2;
            if (status == ModelIssue.StatusFixed) return 8;
            if (status == ModelIssue.StatusIgnored) return 9;
            return 5;
        }

        private static string SelectedText(System.Windows.Controls.ComboBox combo, string fallback)
            => combo?.SelectedItem is FilterOption option ? option.Value : combo?.SelectedItem as string ?? fallback;

        private static void BindGrid(DataGrid grid, IEnumerable<ModelIssue> source)
        {
            grid.ItemsSource = null;
            grid.ItemsSource = source.ToList();
        }

        private static bool IsValidId(ElementId id)
            => id != null && id != ElementId.InvalidElementId && id.GetIdValue() > 0;

        private IEnumerable<ModelIssue> IssuesForCurrentTab()
            => _filtered.Where(i => !i.Ignored);

        private IssueCard CurrentCard()
            => VisualIssuesList?.SelectedItem as IssueCard;

        private ModelIssue CurrentSelection()
        {
            return CurrentCard()?.PrimaryIssue
                ?? GridAll?.SelectedItem as ModelIssue
                ?? GridMEP?.SelectedItem as ModelIssue
                ?? GridLinks?.SelectedItem as ModelIssue
                ?? GridOpen?.SelectedItem as ModelIssue;
        }

        private List<ModelIssue> ResolveIssuesFromSender(object sender)
        {
            object candidate = null;
            if (sender is MenuItem menu)
            {
                candidate = menu.CommandParameter;
                if (candidate == null && menu.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target)
                    candidate = target.DataContext;
            }
            else
            {
                candidate = (sender as FrameworkElement)?.DataContext;
            }

            if (candidate is IssueCard card) return card.Issues.ToList();
            if (candidate is ModelIssue issue) return new List<ModelIssue> { issue };

            var selectedCard = CurrentCard();
            if (selectedCard != null) return selectedCard.Issues.ToList();

            var selected = CurrentSelection();
            return selected == null ? new List<ModelIssue>() : new List<ModelIssue> { selected };
        }

        private ModelIssue ResolveIssueFromSender(object sender)
            => ResolveIssuesFromSender(sender).FirstOrDefault();

        private void FocusFromGrid(DataGrid grid)
        {
            var issue = grid.SelectedItem as ModelIssue;
            HandleSmartDoubleClick(issue);
        }

        private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAutoFocus) return;

            var issue = (sender as DataGrid)?.SelectedItem as ModelIssue;
            if (issue == null) return;

            UpdateSelection(issue);

            if (ChkAutoFocus?.IsChecked == true && !issue.Ignored)
                DoFocus(issue, keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void VisualIssuesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAutoFocus) return;

            var card = CurrentCard();
            var issue = card?.PrimaryIssue;
            if (issue == null) return;

            UpdateSelection(issue);

            if (!card.IsGroup && ChkAutoFocus?.IsChecked == true && !issue.Ignored)
                DoFocus(issue, keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void VisualIssuesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var card = CurrentCard();
            if (card == null) return;

            if (card.IsGroup)
                ApplyGroupFilter(card);
            else
                HandleSmartDoubleClick(card.PrimaryIssue);
        }

        private void OnGridRightClick(object sender, MouseButtonEventArgs e)
        {
            if (!(e.OriginalSource is DependencyObject source)) return;

            var row = FindAncestor<DataGridRow>(source);
            if (!(row?.Item is ModelIssue issue)) return;

            UpdateSelection(issue);
            var menu = BuildIssueContextMenu(issue);
            row.ContextMenu = menu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu BuildIssueContextMenu(ModelIssue issue)
        {
            var menu = new ContextMenu();
            menu.Items.Add(BuildMenuItem("Voir ce type d'erreur", issue, QuickFilterKind_Click));
            menu.Items.Add(BuildMenuItem("Voir cet élément", issue, QuickFilterElement_Click));
            menu.Items.Add(BuildMenuItem("Voir les erreurs liées", issue, QuickFilterRelated_Click));
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildStatusMenu(issue));
            menu.Items.Add(BuildMenuItem("Ajouter commentaire", issue, CommentStatus_Click));
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildMenuItem("Marquer OK / Annuler OK", issue, QuickMarkOk_Click));
            menu.Items.Add(BuildMenuItem("Réinitialiser les filtres", issue, QuickFilterReset_Click));
            return menu;
        }

        private static MenuItem BuildMenuItem(string header, object parameter, RoutedEventHandler handler)
        {
            var item = new MenuItem
            {
                Header = UiLanguage.T(header),
                CommandParameter = parameter
            };
            item.Click += handler;
            return item;
        }

        private MenuItem BuildStatusMenu(object parameter)
        {
            var menu = new MenuItem { Header = UiLanguage.T("Statut", "Status") };
            foreach (var status in new[] { ModelIssue.StatusActive, ModelIssue.StatusToFix, ModelIssue.StatusReview, ModelIssue.StatusFixed, ModelIssue.StatusIgnored })
            {
                var item = new MenuItem
                {
                    Header = UiLanguage.T(status),
                    Tag = status,
                    CommandParameter = parameter
                };
                item.Click += StatusMenu_Click;
                menu.Items.Add(item);
            }

            return menu;
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T typed) return typed;
                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void HandleSmartDoubleClick(ModelIssue issue)
        {
            if (issue == null) return;

            if (issue.Ignored)
            {
                ShowIssueDetails(issue);
                return;
            }

            switch (issue.Kind)
            {
                case IssueKind.LinkPipeClash:
                case IssueKind.MepThroughWallNoSleeve:
                    DoFocus(issue, keepShowAll: false, forceSectionBox: true);
                    break;
                case IssueKind.MepUnconnected:
                    DoFocus(issue, keepShowAll: BtnShowAll.IsChecked == true, forceSectionBox: false);
                    break;
                default:
                    if (IsValidId(issue.ElementId))
                        DoFocus(issue, keepShowAll: BtnShowAll.IsChecked == true, forceSectionBox: false);
                    else
                        ShowIssueDetails(issue);
                    break;
            }
        }

        private void ShowIssueDetails(ModelIssue issue)
        {
            var related = IsValidId(issue.RelatedId) ? issue.RelatedId.GetIdValue().ToString() : "-";
            var localizedStatus = UiLanguage.T(issue.StatusText);
            var statusInfo = string.IsNullOrWhiteSpace(issue.StatusUpdatedText)
                ? localizedStatus
                : localizedStatus + " (" + issue.StatusUpdatedText + ")";
            var comment = string.IsNullOrWhiteSpace(issue.StatusComment) ? string.Empty : UiLanguage.T("\nCommentaire : ", "\nComment: ") + issue.StatusComment;

            TaskDialog.Show(
                UiLanguage.T("Clash 3D - détail", "3D Clash - Details"),
                UiLanguage.T("Gravité : ", "Severity: ") + UiLanguage.T(issue.SeverityText) + "\n" +
                UiLanguage.T("Statut : ", "Status: ") + statusInfo + "\n" +
                UiLanguage.T("Type : ", "Type: ") + UiLanguage.T(issue.Category) + "\n" +
                UiLanguage.T("Niveau : ", "Level: ") + EmptyDash(issue.LevelName) + "\n" +
                UiLanguage.T("Catégorie : ", "Category: ") + EmptyDash(issue.ElementCategory) + "\n" +
                UiLanguage.T("Lien : ", "Link: ") + EmptyDash(issue.LinkName) + "\n" +
                UiLanguage.T("Élément : ", "Element: ") + issue.ElementIdValue + "\n" +
                UiLanguage.T("Élément lié : ", "Related Element: ") + related + "\n\n" +
                $"{issue.WhyText}\n{issue.AdviceText}\n\n" +
                $"{issue.Message}{comment}");
        }

        private void OnEnsure3D(object sender, RoutedEventArgs e)
        {
            _handler.Action = SmartAction.Ensure3D;
            SafeRaise();
        }

        private void OnShowAll(object sender, RoutedEventArgs e)
        {
            var ids = IssuesForCurrentTab()
                .Select(i => i.ElementId)
                .Where(IsValidId)
                .Distinct(new IdCmp())
                .ToList();

            _handler.AllIssueIds = ids;
            _handler.ShowAllEnabled = (BtnShowAll.IsChecked == true);

            _handler.Action = SmartAction.ShowAllApply;
            SafeRaise();
        }

        private class IdCmp : IEqualityComparer<ElementId>
        {
            public bool Equals(ElementId a, ElementId b) => (a?.GetIdValue() ?? int.MinValue) == (b?.GetIdValue() ?? int.MinValue);
            public int GetHashCode(ElementId obj) => obj?.GetIdValue().GetHashCode() ?? 0;
        }

        private void OnPrev(object sender, RoutedEventArgs e)
        {
            var list = IssuesForCurrentTab().ToList();
            if (list.Count == 0) return;
            _cursor = (_cursor <= 0 || _cursor >= list.Count) ? list.Count - 1 : _cursor - 1;
            DoFocus(list[_cursor], keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void OnNext(object sender, RoutedEventArgs e)
        {
            var list = IssuesForCurrentTab().ToList();
            if (list.Count == 0) return;
            _cursor = (_cursor < 0 || _cursor >= list.Count) ? 0 : (_cursor + 1) % list.Count;
            DoFocus(list[_cursor], keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void DoFocus(ModelIssue issue, bool keepShowAll, bool forceSectionBox = false)
        {
            DoFocus(new[] { issue }, keepShowAll, forceSectionBox);
        }

        private void DoFocus(IEnumerable<ModelIssue> issues, bool keepShowAll, bool forceSectionBox = false)
        {
            var list = (issues ?? Enumerable.Empty<ModelIssue>())
                .Where(i => i != null)
                .ToList();
            if (list.Count == 0) return;

            var first = list[0];
            UpdateSelection(first);

            _handler.Action = SmartAction.FocusApply;
            _handler.FocusIssues = list;
            _handler.IssueId = first.ElementId ?? ElementId.InvalidElementId;
            _handler.RelatedId = first.RelatedId ?? ElementId.InvalidElementId;
            _handler.CurrentKind = first.Kind;
            _handler.IssueBox = first.BBox;
            _handler.ShowAllMode = keepShowAll;
            _handler.AutoSectionBox = forceSectionBox;
            SafeRaise();

            UpdateCursor(first);
        }

        private void OnFocus(object sender, RoutedEventArgs e)
        {
            var issues = ResolveIssuesFromSender(sender);
            DoFocus(issues, keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void OnFocusIsolate(object sender, RoutedEventArgs e)
        {
            var issues = ResolveIssuesFromSender(sender);
            DoFocus(issues, keepShowAll: false, forceSectionBox: true);
        }

        private void OnIgnore(object sender, RoutedEventArgs e)
        {
            ToggleIgnored(ResolveIssuesFromSender(sender));
        }

        private void ToggleIgnored(IEnumerable<ModelIssue> issues)
        {
            var list = (issues ?? Enumerable.Empty<ModelIssue>()).Where(i => i != null).ToList();
            if (list.Count == 0) return;

            var allResolved = list.All(i => i.Ignored);
            ApplyStatus(list, allResolved ? ModelIssue.StatusActive : ModelIssue.StatusFixed, keepComment: true);
        }

        private void StatusMenu_Click(object sender, RoutedEventArgs e)
        {
            var status = (sender as MenuItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(status)) return;
            ApplyStatus(ResolveIssuesFromSender(sender), status, keepComment: true);
        }

        private void CommentStatus_Click(object sender, RoutedEventArgs e)
        {
            var list = ResolveIssuesFromSender(sender).Where(i => i != null).ToList();
            if (list.Count == 0) return;

            var existing = list.Count == 1 ? list[0].StatusComment ?? string.Empty : string.Empty;
            var comment = Microsoft.VisualBasic.Interaction.InputBox(
                UiLanguage.T("Commentaire optionnel pour ce statut :", "Optional comment for this status:"),
                UiLanguage.T("Clash 3D - commentaire", "3D Clash - Comment"),
                existing);

            if (comment == null) return;
            foreach (var issue in list)
            {
                SmartCheckState.SetIssueStatus(_docKey, issue, issue.StatusText, comment, Environment.UserName);
            }

            ApplyFilters();
            UpdateSelection(list[0]);
        }

        private void ApplyStatus(IEnumerable<ModelIssue> issues, string status, bool keepComment)
        {
            var list = (issues ?? Enumerable.Empty<ModelIssue>()).Where(i => i != null).ToList();
            if (list.Count == 0) return;

            foreach (var issue in list)
            {
                var comment = keepComment ? issue.StatusComment : null;
                SmartCheckState.SetIssueStatus(_docKey, issue, status, comment, Environment.UserName);
            }

            _handler.IssueId = list[0].ElementId ?? ElementId.InvalidElementId;
            _handler.Action = SmartAction.MarkIgnored;
            SafeRaise();

            ApplyFilters();
            UpdateSelection(list[0]);
        }

        private void UpdateSelection(ModelIssue issue)
        {
            if (issue == null) return;

            _suppressAutoFocus = true;
            try
            {
                SelectInList(VisualIssuesList, issue);
                SelectInGrid(GridAll, issue);
                SelectInGrid(GridMEP, issue);
                SelectInGrid(GridLinks, issue);
                SelectInGrid(GridOpen, issue);
            }
            finally
            {
                _suppressAutoFocus = false;
            }
        }

        private static void SelectInList(ListBox list, ModelIssue issue)
        {
            if (list?.ItemsSource == null) return;

            foreach (var item in list.ItemsSource)
            {
                if (item is IssueCard card && card.Contains(issue))
                {
                    list.SelectedItem = card;
                    list.ScrollIntoView(card);
                    return;
                }
            }

            list.SelectedItem = null;
        }

        private static void SelectInGrid(DataGrid grid, ModelIssue issue)
        {
            if (grid?.ItemsSource == null) return;

            if (ContainsIssue(grid.ItemsSource, issue))
            {
                grid.SelectedItem = issue;
                grid.ScrollIntoView(issue);
            }
            else
            {
                grid.SelectedItem = null;
            }
        }

        private static bool ContainsIssue(System.Collections.IEnumerable source, ModelIssue issue)
        {
            foreach (var item in source)
            {
                if (ReferenceEquals(item, issue)) return true;
            }
            return false;
        }

        private void UpdateCursor(ModelIssue issue)
        {
            if (issue == null) return;
            var list = IssuesForCurrentTab().ToList();
            var idx = list.IndexOf(issue);
            if (idx >= 0) _cursor = idx;
        }

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isBindingFilters) return;
            ApplyFilters();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isBindingFilters) return;
            ApplyFilters();
        }

        private void QuickFilterKind_Click(object sender, RoutedEventArgs e)
        {
            var issue = ResolveIssueFromSender(sender);
            if (issue == null || string.IsNullOrWhiteSpace(issue.Category)) return;

            ResetFilterBinding(() =>
            {
                SeverityFilterCombo.SelectedIndex = 0;
                StateFilterCombo.SelectedIndex = 0;
                SearchBox.Text = string.Empty;
                SelectComboValue(TypeFilterCombo, issue.Category, "Tous");
            });
        }

        private void QuickFilterElement_Click(object sender, RoutedEventArgs e)
        {
            var issue = ResolveIssueFromSender(sender);
            if (issue == null) return;
            ApplySearchFilter(issue.ElementIdValue.ToString());
        }

        private void QuickFilterRelated_Click(object sender, RoutedEventArgs e)
        {
            var issue = ResolveIssueFromSender(sender);
            if (issue == null) return;

            var id = IsValidId(issue.RelatedId)
                ? issue.RelatedId.GetIdValue().ToString()
                : issue.ElementIdValue.ToString();
            ApplySearchFilter(id);
        }

        private void QuickFilterGroup_Click(object sender, RoutedEventArgs e)
        {
            var card = ResolveCardFromSender(sender);
            if (card != null)
                ApplyGroupFilter(card);
        }

        private IssueCard ResolveCardFromSender(object sender)
        {
            if (sender is MenuItem menu)
            {
                if (menu.CommandParameter is IssueCard card) return card;
                if (menu.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement target && target.DataContext is IssueCard targetCard)
                    return targetCard;
            }

            return (sender as FrameworkElement)?.DataContext as IssueCard ?? CurrentCard();
        }

        private void ApplyGroupFilter(IssueCard card)
        {
            if (card == null || card.PrimaryIssue == null) return;
            if (!card.IsGroup) return;
            var issue = card.PrimaryIssue;

            ResetFilterBinding(() =>
            {
                SearchBox.Text = string.Empty;
                SeverityFilterCombo.SelectedIndex = 0;
                StateFilterCombo.SelectedIndex = 0;
                SelectComboValue(TypeFilterCombo, issue.Category, "Tous");
                SelectComboValue(LevelFilterCombo, issue.LevelName, "Tous");
                SelectComboValue(LinkFilterCombo, issue.LinkName, "Tous");
                SelectComboValue(ElementCategoryFilterCombo, issue.ElementCategory, "Toutes");
                SelectComboValue(VisualModeCombo, "Anomalies", "Anomalies");
            });
        }

        private void OnBackToGroups(object sender, RoutedEventArgs e)
        {
            ResetFilterBinding(() =>
            {
                SeverityFilterCombo.SelectedIndex = 0;
                TypeFilterCombo.SelectedIndex = 0;
                StateFilterCombo.SelectedIndex = 0;
                LevelFilterCombo.SelectedIndex = 0;
                LinkFilterCombo.SelectedIndex = 0;
                ElementCategoryFilterCombo.SelectedIndex = 0;
                SearchBox.Text = string.Empty;
                SelectComboValue(VisualModeCombo, "Groupes intelligents", "Groupes intelligents");
            });
        }

        private void UpdateBackToGroupsButton()
        {
            if (BackToGroupsButton == null) return;

            BackToGroupsButton.Visibility = SelectedText(VisualModeCombo, "Groupes intelligents") == "Anomalies"
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        private void QuickMarkOk_Click(object sender, RoutedEventArgs e)
        {
            ToggleIgnored(ResolveIssuesFromSender(sender));
        }

        private void QuickFilterReset_Click(object sender, RoutedEventArgs e)
        {
            ResetFilterBinding(() =>
            {
                SeverityFilterCombo.SelectedIndex = 0;
                TypeFilterCombo.SelectedIndex = 0;
                StateFilterCombo.SelectedIndex = 0;
                LevelFilterCombo.SelectedIndex = 0;
                LinkFilterCombo.SelectedIndex = 0;
                ElementCategoryFilterCombo.SelectedIndex = 0;
                SearchBox.Text = string.Empty;
            });
        }

        private void ApplySearchFilter(string text)
        {
            ResetFilterBinding(() =>
            {
                SeverityFilterCombo.SelectedIndex = 0;
                TypeFilterCombo.SelectedIndex = 0;
                StateFilterCombo.SelectedIndex = 0;
                LevelFilterCombo.SelectedIndex = 0;
                LinkFilterCombo.SelectedIndex = 0;
                ElementCategoryFilterCombo.SelectedIndex = 0;
                SearchBox.Text = text ?? string.Empty;
            });
        }

        private void ResetFilterBinding(Action action)
        {
            _isBindingFilters = true;
            try
            {
                action?.Invoke();
            }
            finally
            {
                _isBindingFilters = false;
            }
            ApplyFilters();
        }

        private static void SelectComboValue(System.Windows.Controls.ComboBox combo, string value, string fallback)
        {
            if (combo == null) return;
            var desired = combo.Items.OfType<FilterOption>()
                .FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.CurrentCultureIgnoreCase));
            if (desired == null)
                desired = combo.Items.OfType<FilterOption>()
                    .FirstOrDefault(option => string.Equals(option.Value, fallback, StringComparison.CurrentCultureIgnoreCase));
            if (desired != null)
                combo.SelectedItem = desired;
            else if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private void GenerateThumbnails_Click(object sender, RoutedEventArgs e)
        {
            var issues = ResolveIssuesFromSender(sender);
            GenerateThumbnailsFor(issues);
        }

        private void OnGenerateThumbnails(object sender, RoutedEventArgs e)
        {
            var card = CurrentCard();
            var issues = card != null
                ? card.Issues
                : _filtered.Where(i => !i.HasThumbnail).Take(12).ToList();
            GenerateThumbnailsFor(issues);
        }

        private void GenerateThumbnailsFor(IEnumerable<ModelIssue> issues)
        {
            var list = (issues ?? Enumerable.Empty<ModelIssue>())
                .Where(i => i != null && !i.HasThumbnail && !i.ThumbnailLoading)
                .OrderBy(i => i.PriorityRank)
                .Take(12)
                .ToList();

            if (list.Count == 0) return;

            Directory.CreateDirectory(_thumbnailFolder);
            foreach (var issue in list)
                issue.ThumbnailLoading = true;

            _handler.ThumbnailFolder = _thumbnailFolder;
            _handler.ThumbnailLimit = 12;
            _handler.ThumbnailIssues = list;
            _handler.Action = SmartAction.GenerateThumbnails;
            SafeRaise();
        }

        private void RestoreCachedThumbnails()
        {
            if (!Directory.Exists(_thumbnailFolder)) return;

            foreach (var issue in _all)
            {
                var path = Path.Combine(_thumbnailFolder, MakeSafeFileName(issue.IssueKey) + ".png");
                if (File.Exists(path))
                    issue.ThumbnailPath = path;
            }
        }

        private void OnExportReport(object sender, RoutedEventArgs e)
        {
            var path = ExportHtmlReportV2(_filtered.Count > 0 ? _filtered : _all);
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch
            {
                TaskDialog.Show(UiLanguage.T("Clash 3D", "3D Clash"), UiLanguage.T("Rapport exporté :\n", "Report exported:\n") + path);
            }
        }

        private string ExportHtmlReportV2(IEnumerable<ModelIssue> issues)
        {
            var list = (issues ?? Enumerable.Empty<ModelIssue>()).ToList();
            if (list.Count == 0) return null;

            var folder = SmartCheckState.GetReportFolder();
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "Clash3D_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".html");

            var groups = list.GroupBy(i => i.GroupTitle)
                .OrderByDescending(g => g.Count(i => !i.Ignored))
                .ThenBy(g => g.Key)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Rapport Clash 3D</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:0;color:#111827;background:#eef2f7}.page{max-width:1280px;margin:0 auto;padding:28px}.top{background:#115c3a;color:white;border-radius:18px;padding:24px;margin-bottom:18px}.top h1{margin:0 0 8px;font-size:30px}.top p{margin:0;color:#e7f6ee}.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:22px}.stat{background:white;border:1px solid #dbe3ea;border-radius:12px;padding:14px 16px}.stat strong{font-size:25px;display:block}.section-title{margin:24px 0 12px;font-size:21px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(265px,1fr));gap:16px}.card{background:white;border:1px solid #dbe3ea;border-radius:14px;overflow:hidden;box-shadow:0 8px 22px rgba(15,23,42,.06)}.visual{height:156px;background:#e8f1ed;position:relative;display:flex;align-items:center;justify-content:center;overflow:hidden}.thumb{width:100%;height:100%;object-fit:cover}.initials{font-size:42px;font-weight:800;color:#115c3a;opacity:.36}.badge{display:inline-block;border-radius:999px;padding:5px 10px;color:white;font-size:12px;font-weight:700}.badge-count{position:absolute;right:10px;top:10px;background:#111827;color:white;border-radius:999px;padding:6px 10px;font-weight:700;font-size:12px}.badge-kind{position:absolute;left:10px;top:10px}.crit{background:#d83030}.check{background:#e28a00}.info{background:#64748b}.ok{background:#278d42}.card-body{padding:14px}.card h3{margin:8px 0 7px;font-size:16px;line-height:1.25}.meta{color:#64748b;font-size:12px;margin:4px 0}.advice{color:#374151;font-size:13px;line-height:1.35}.button{display:inline-block;margin-top:10px;background:#115c3a;color:white;text-decoration:none;border-radius:8px;padding:8px 11px;font-size:12px;font-weight:700}.detail{background:white;border:1px solid #dbe3ea;border-radius:14px;margin:18px 0 0;overflow:hidden}.detail-head{display:flex;gap:12px;align-items:center;justify-content:space-between;padding:15px 16px;background:#f8fafc;border-bottom:1px solid #e5e7eb}.detail-head h3{margin:0;font-size:17px}.detail-body{padding:0 16px 16px}table{border-collapse:collapse;width:100%;background:white;margin-top:14px}td,th{border-bottom:1px solid #e5e7eb;padding:9px 7px;text-align:left;font-size:12px;vertical-align:top}th{background:#f8fafc;color:#334155;font-weight:700}.message{min-width:280px}.comment{color:#64748b}.small{font-size:12px;color:#64748b}@media print{body{background:white}.page{padding:0}.button{display:none}.card{break-inside:avoid}.detail{break-inside:avoid}}");
            sb.AppendLine("</style></head><body><div class=\"page\">");
            sb.AppendLine("<div class=\"top\"><h1>Rapport Clash 3D</h1><p>BIMaestro - " + Html(DateTime.Now.ToString("dd/MM/yyyy HH:mm")) + " - " + list.Count + " anomalie(s), " + groups.Count + " groupe(s)</p></div>");
            sb.AppendLine("<div class=\"stats\">");
            AddStat(sb, "Anomalies", list.Count);
            AddStat(sb, "Groupes", groups.Count);
            AddStat(sb, "Critiques", list.Count(i => !i.Ignored && i.Severity == IssueSeverity.Critical));
            AddStat(sb, "À corriger", list.Count(i => i.StatusText == ModelIssue.StatusToFix));
            AddStat(sb, "À revoir", list.Count(i => i.StatusText == ModelIssue.StatusReview));
            AddStat(sb, "OK / ignorées", list.Count(i => i.Ignored));
            sb.AppendLine("</div>");
            sb.AppendLine("<h2 class=\"section-title\">Vue par vignettes</h2><div class=\"grid\">");

            for (int index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var first = group.OrderBy(i => i.PriorityRank).First();
                var anchor = "detail-" + index.ToString("000");
                var image = group.Select(i => i.ThumbnailPath).FirstOrDefault(IsUsableImagePath);

                sb.AppendLine("<div class=\"card\"><div class=\"visual\">");
                if (!string.IsNullOrWhiteSpace(image))
                    sb.AppendLine("<img class=\"thumb\" src=\"" + HtmlAttr(ToFileUrl(image)) + "\" alt=\"Aperçu " + HtmlAttr(first.VisualTitle) + "\">");
                else
                    sb.AppendLine("<div class=\"initials\">" + Html(first.VisualInitials) + "</div>");
                sb.AppendLine("<span class=\"badge badge-kind " + SeverityClass(first) + "\">" + Html(first.SeverityText) + "</span>");
                sb.AppendLine("<span class=\"badge-count\">" + group.Count() + "</span>");
                sb.AppendLine("</div><div class=\"card-body\">");
                sb.AppendLine("<div class=\"meta\">" + Html(EmptyDash(first.LevelName)) + " · " + Html(EmptyDash(first.LinkName)) + "</div>");
                sb.AppendLine("<h3>" + Html(group.Key) + "</h3>");
                sb.AppendLine("<div class=\"advice\">" + Html(first.WhyText) + "<br>" + Html(first.AdviceText) + "</div>");
                sb.AppendLine("<a class=\"button\" href=\"#" + anchor + "\">Voir le détail</a>");
                sb.AppendLine("</div></div>");
            }

            sb.AppendLine("</div><h2 class=\"section-title\">Détail des groupes</h2>");
            for (int index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var first = group.OrderBy(i => i.PriorityRank).First();
                var anchor = "detail-" + index.ToString("000");

                sb.AppendLine("<section class=\"detail\" id=\"" + anchor + "\">");
                sb.AppendLine("<div class=\"detail-head\"><div><h3>" + Html(group.Key) + "</h3><div class=\"small\">" + group.Count() + " anomalie(s), " + group.Count(i => !i.Ignored) + " active(s)</div></div><span class=\"badge " + SeverityClass(first) + "\">" + Html(first.SeverityText) + "</span></div>");
                sb.AppendLine("<div class=\"detail-body\"><table><thead><tr><th>Statut</th><th>Type</th><th>Niveau</th><th>Catégorie</th><th>Lien</th><th>Élément</th><th>Lié</th><th class=\"message\">Message</th><th>Commentaire</th></tr></thead><tbody>");
                foreach (var issue in group.OrderBy(i => i.PriorityRank).ThenBy(i => i.ElementIdValue))
                {
                    var related = IsValidId(issue.RelatedId) ? issue.RelatedId.GetIdValue().ToString() : "-";
                    sb.AppendLine("<tr><td>" + Html(issue.StatusText) + "</td><td>" + Html(issue.Category) + "</td><td>" + Html(EmptyDash(issue.LevelName)) + "</td><td>" + Html(EmptyDash(issue.ElementCategory)) + "</td><td>" + Html(EmptyDash(issue.LinkName)) + "</td><td>" + issue.ElementIdValue + "</td><td>" + Html(related) + "</td><td class=\"message\">" + Html(issue.Message) + "</td><td class=\"comment\">" + Html(issue.StatusComment) + "</td></tr>");
                }
                sb.AppendLine("</tbody></table></div></section>");
            }

            sb.AppendLine("</div></body></html>");
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            return file;
        }

        private string ExportHtmlReport(IEnumerable<ModelIssue> issues)
        {
            var list = (issues ?? Enumerable.Empty<ModelIssue>()).ToList();
            if (list.Count == 0) return null;

            var folder = SmartCheckState.GetReportFolder();
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "Clash3D_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".html");

            var groups = list.GroupBy(i => i.GroupTitle)
                .OrderByDescending(g => g.Count(i => !i.Ignored))
                .ThenBy(g => g.Key)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Rapport Clash 3D</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:28px;color:#111827;background:#f8fafc}.top{background:#115c3a;color:white;border-radius:16px;padding:22px;margin-bottom:18px}.stats{display:flex;gap:12px;flex-wrap:wrap}.stat{background:white;border:1px solid #e5e7eb;border-radius:10px;padding:12px 16px;min-width:120px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:14px}.card{background:white;border:1px solid #e5e7eb;border-radius:12px;padding:14px}.badge{display:inline-block;border-radius:999px;padding:4px 9px;color:white;font-size:12px;font-weight:700}.crit{background:#d83030}.check{background:#e28a00}.info{background:#64748b}.ok{background:#278d42}.thumb{width:100%;height:150px;object-fit:cover;background:#eef2f7;border-radius:10px;margin:10px 0}table{border-collapse:collapse;width:100%;background:white;margin-top:22px}td,th{border:1px solid #e5e7eb;padding:7px;text-align:left;font-size:12px}th{background:#f1f5f9}</style></head><body>");
            sb.AppendLine("<div class=\"top\"><h1>Rapport Clash 3D</h1><div>BIMaestro - " + Html(DateTime.Now.ToString("dd/MM/yyyy HH:mm")) + "</div></div>");
            sb.AppendLine("<div class=\"stats\">");
            AddStat(sb, "Total", list.Count);
            AddStat(sb, "Critiques", list.Count(i => !i.Ignored && i.Severity == IssueSeverity.Critical));
            AddStat(sb, "À corriger", list.Count(i => i.StatusText == ModelIssue.StatusToFix));
            AddStat(sb, "À revoir", list.Count(i => i.StatusText == ModelIssue.StatusReview));
            AddStat(sb, "OK", list.Count(i => i.Ignored));
            sb.AppendLine("</div><h2>Groupes</h2><div class=\"grid\">");

            foreach (var group in groups)
            {
                var first = group.OrderBy(i => i.PriorityRank).First();
                sb.AppendLine("<div class=\"card\">");
                sb.AppendLine("<span class=\"badge " + SeverityClass(first) + "\">" + Html(first.SeverityText) + "</span>");
                sb.AppendLine("<h3>" + Html(group.Key) + "</h3>");
                sb.AppendLine("<div>" + group.Count() + " anomalie(s), " + group.Count(i => !i.Ignored) + " active(s)</div>");
                sb.AppendLine("<p>" + Html(first.WhyText) + "<br>" + Html(first.AdviceText) + "</p>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</div><h2>Détail</h2><table><thead><tr><th>Gravité</th><th>Statut</th><th>Type</th><th>Niveau</th><th>Catégorie</th><th>Lien</th><th>Élément</th><th>Message</th><th>Commentaire</th></tr></thead><tbody>");
            foreach (var issue in list.OrderBy(i => i.PriorityRank).ThenBy(i => i.Category))
            {
                sb.AppendLine("<tr><td>" + Html(issue.SeverityText) + "</td><td>" + Html(issue.StatusText) + "</td><td>" + Html(issue.Category) + "</td><td>" + Html(issue.LevelName) + "</td><td>" + Html(issue.ElementCategory) + "</td><td>" + Html(issue.LinkName) + "</td><td>" + issue.ElementIdValue + "</td><td>" + Html(issue.Message) + "</td><td>" + Html(issue.StatusComment) + "</td></tr>");
            }

            sb.AppendLine("</tbody></table></body></html>");
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            return file;
        }

        private static void AddStat(StringBuilder sb, string label, int value)
            => sb.AppendLine("<div class=\"stat\"><strong>" + value + "</strong><br>" + Html(label) + "</div>");

        private static string SeverityClass(ModelIssue issue)
        {
            if (issue.Ignored) return "ok";
            if (issue.Severity == IssueSeverity.Critical) return "crit";
            if (issue.Severity == IssueSeverity.Check) return "check";
            return "info";
        }

        private static string Html(string value)
            => WebUtility.HtmlEncode(value ?? string.Empty);

        private static string HtmlAttr(string value)
            => Html(value);

        private static string EmptyDash(string value)
            => string.IsNullOrWhiteSpace(value) ? "-" : value;

        private static bool IsUsableImagePath(string path)
            => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        private static string ToFileUrl(string path)
        {
            try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; }
            catch { return path ?? string.Empty; }
        }

        private static string MakeSafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string((value ?? "issue").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return safe.Length > 120 ? safe.Substring(0, 120) : safe;
        }

        private async void SafeRaise()
        {
            int tries = 0;
            while (SmartExternalHandler.IsExecuting && tries < 20)
            {
                await Task.Delay(25);
                tries++;
            }

            try
            {
                _extEvent.Raise();
            }
            catch (ExternalApplicationException)
            {
                await Task.Delay(50);
                try { _extEvent.Raise(); } catch { }
            }
            catch { }
        }

        private sealed class IssueCard : INotifyPropertyChanged, IDisposable
        {
            public IssueCard(IEnumerable<ModelIssue> issues)
            {
                Issues = (issues ?? Enumerable.Empty<ModelIssue>())
                    .OrderBy(i => i.PriorityRank)
                    .ThenBy(i => i.ElementIdValue)
                    .ToList();
                PrimaryIssue = Issues.FirstOrDefault();

                foreach (var issue in Issues)
                    issue.PropertyChanged += Issue_PropertyChanged;
            }

            public List<ModelIssue> Issues { get; }
            public ModelIssue PrimaryIssue { get; }
            public bool IsGroup => Issues.Count > 1;
            public int Count => Issues.Count;
            public int ActiveCount => Issues.Count(i => !i.Ignored);
            public int PriorityRank => Issues.Count == 0 ? 99 : Issues.Min(i => i.PriorityRank);
            public string VisualTitle => IsGroup ? $"{Count} × {PrimaryIssue?.GroupTitle}" : PrimaryIssue?.VisualTitle;
            public string VisualSubtitle => IsGroup ? $"{ActiveCount} actives - {PrimaryIssue?.WhyText}" : PrimaryIssue?.VisualSubtitle;
            public string WhyText => PrimaryIssue?.WhyText;
            public string AdviceText => PrimaryIssue?.AdviceText;
            public string SeverityText => PrimaryIssue?.SeverityText;
            public string IssueStateText => IsGroup ? $"{ActiveCount}/{Count} actives" : PrimaryIssue?.IssueStateText;
            public string VisualInitials => PrimaryIssue?.VisualInitials;
            public string IssueFamily => IsGroup ? PrimaryIssue?.ElementCategory : PrimaryIssue?.IssueFamily;
            public string RelatedLabel => IsGroup ? PrimaryIssue?.LevelName : PrimaryIssue?.RelatedLabel;
            public string ThumbnailPath => PrimaryIssue?.ThumbnailPath;
            public string ThumbnailStateText => PrimaryIssue?.ThumbnailStateText;
            public bool ThumbnailLoading => Issues.Any(i => i.ThumbnailLoading);
            public System.Windows.Visibility ThumbnailVisibility => string.IsNullOrWhiteSpace(ThumbnailPath) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            public System.Windows.Visibility InitialsVisibility => string.IsNullOrWhiteSpace(ThumbnailPath) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            public System.Windows.Visibility DetailActionVisibility => IsGroup ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            public string GroupBadgeText => IsGroup ? Count + " anomalies" : "1 anomalie";

            public bool Contains(ModelIssue issue)
                => Issues.Any(i => ReferenceEquals(i, issue));

            public event PropertyChangedEventHandler PropertyChanged;

            public void Dispose()
            {
                foreach (var issue in Issues)
                    issue.PropertyChanged -= Issue_PropertyChanged;
            }

            private void Issue_PropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                OnPropertyChanged(nameof(SeverityText));
                OnPropertyChanged(nameof(IssueStateText));
                OnPropertyChanged(nameof(ThumbnailPath));
                OnPropertyChanged(nameof(ThumbnailStateText));
                OnPropertyChanged(nameof(ThumbnailLoading));
                OnPropertyChanged(nameof(ThumbnailVisibility));
                OnPropertyChanged(nameof(InitialsVisibility));
                OnPropertyChanged(nameof(ActiveCount));
                OnPropertyChanged(nameof(VisualSubtitle));
            }

            private void OnPropertyChanged(string propertyName)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

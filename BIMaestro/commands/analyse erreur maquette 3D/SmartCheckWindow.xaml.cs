using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Modification;

namespace Analyse
{
    public partial class SmartCheckWindow : Window
    {
        private readonly ExternalEvent _extEvent;
        private readonly SmartExternalHandler _handler;

        private readonly List<ModelIssue> _all;
        private readonly List<ModelIssue> _mepNoSleeve;
        private readonly List<ModelIssue> _linkClashes;
        private readonly List<ModelIssue> _openConnectors;
        private readonly string _docKey;
        private int _cursor = -1;
        private bool _suppressAutoFocus;

        public SmartCheckWindow(IEnumerable<ModelIssue> issues, ExternalEvent extEvent, SmartExternalHandler handler, string docKey)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _extEvent = extEvent;
            _handler = handler;
            _docKey = docKey;

            _all = issues.ToList();
            _mepNoSleeve = _all.Where(i => i.Kind == IssueKind.MepThroughWallNoSleeve).ToList();
            _linkClashes = _all.Where(i => i.Kind == IssueKind.LinkPipeClash).ToList();
            _openConnectors = _all.Where(i => i.Kind == IssueKind.MepUnconnected).ToList();

            TxtIssueCount.Text = $"{_all.Count} anomalies";

            Bind();

            GridAll.MouseDoubleClick += (s, e) => FocusFromGrid(GridAll);
            GridMEP.MouseDoubleClick += (s, e) => FocusFromGrid(GridMEP);
            GridLinks.MouseDoubleClick += (s, e) => FocusFromGrid(GridLinks);
            GridOpen.MouseDoubleClick += (s, e) => FocusFromGrid(GridOpen);

            GridAll.SelectionChanged += OnGridSelectionChanged;
            GridMEP.SelectionChanged += OnGridSelectionChanged;
            GridLinks.SelectionChanged += OnGridSelectionChanged;
            GridOpen.SelectionChanged += OnGridSelectionChanged;
        }

        private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAutoFocus) return;
            if (ChkAutoFocus?.IsChecked != true) return;

            var issue = (sender as DataGrid)?.SelectedItem as ModelIssue;
            if (issue == null || issue.Ignored) return;

            DoFocus(issue, keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void Bind()
        {
            BindGrid(GridAll, _all);
            BindGrid(GridMEP, _mepNoSleeve);
            BindGrid(GridLinks, _linkClashes);
            BindGrid(GridOpen, _openConnectors);
            UpdateIssueCount();
        }

        private void UpdateIssueCount()
        {
            var ignored = _all.Count(i => i.Ignored);
            var active = _all.Count - ignored;
            TxtIssueCount.Text = ignored > 0
                ? $"{active} actives / {_all.Count} anomalies"
                : $"{_all.Count} anomalies";
        }

        private static void BindGrid(DataGrid grid, IEnumerable<ModelIssue> source)
        {
            grid.ItemsSource = null;
            grid.ItemsSource = source.ToList();
        }

        private static bool IsValidId(ElementId id)
            => id != null && id != ElementId.InvalidElementId && id.GetIdValue() > 0;

        private string CurrentTabName() => (Tabs.SelectedItem as TabItem)?.Header?.ToString() ?? "Toutes";

        private IEnumerable<ModelIssue> IssuesForCurrentTab()
        {
            switch (CurrentTabName())
            {
                case "Traversées (sans réservation)": return _mepNoSleeve.Where(i => !i.Ignored);
                case "Collisions liens / tuyaux": return _linkClashes.Where(i => !i.Ignored);
                case "Raccords ouverts": return _openConnectors.Where(i => !i.Ignored);
                default: return _all.Where(i => !i.Ignored);
            }
        }

        private ModelIssue CurrentSelection()
        {
            switch (CurrentTabName())
            {
                case "Traversées (sans réservation)": return GridMEP.SelectedItem as ModelIssue;
                case "Collisions liens / tuyaux": return GridLinks.SelectedItem as ModelIssue;
                case "Raccords ouverts": return GridOpen.SelectedItem as ModelIssue;
                default: return GridAll.SelectedItem as ModelIssue;
            }
        }

        private DataGrid GridForCurrentTab()
        {
            switch (CurrentTabName())
            {
                case "Traversées (sans réservation)": return GridMEP;
                case "Collisions liens / tuyaux": return GridLinks;
                case "Raccords ouverts": return GridOpen;
                default: return GridAll;
            }
        }

        private ModelIssue ResolveIssueFromSender(object sender)
        {
            var ctx = (sender as FrameworkElement)?.DataContext as ModelIssue;
            return ctx ?? CurrentSelection();
        }

        private void FocusFromGrid(DataGrid grid)
        {
            var issue = grid.SelectedItem as ModelIssue;
            if (issue == null) return;
            DoFocus(issue, keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void OnEnsure3D(object sender, RoutedEventArgs e)
        {
            _handler.Action = SmartAction.Ensure3D;
            SafeRaise();
        }

        // ----- Afficher toutes les erreurs (atomique) -----
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

        private void DoFocus(ModelIssue issue, bool keepShowAll)
        {
            UpdateSelection(issue);

            _handler.Action = SmartAction.FocusApply;
            _handler.IssueId = issue.ElementId ?? ElementId.InvalidElementId;
            _handler.RelatedId = issue.RelatedId ?? ElementId.InvalidElementId;
            _handler.CurrentKind = issue.Kind;
            _handler.IssueBox = issue.BBox;
            _handler.ShowAllMode = keepShowAll;
            _handler.AutoSectionBox = (ChkAutoFocus?.IsChecked == true); SafeRaise();

            UpdateCursor(issue);
        }

        private void OnFocus(object sender, RoutedEventArgs e)
        {
            var issue = ResolveIssueFromSender(sender);
            if (issue == null) return;
            UpdateCursor(issue);
            DoFocus(issue, keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void OnIgnore(object sender, RoutedEventArgs e)
        {
            var issue = ResolveIssueFromSender(sender);
            if (issue == null) return;

            issue.Ignored = !issue.Ignored;
            SmartCheckState.SetIgnored(_docKey, issue, issue.Ignored);

            _handler.Action = SmartAction.MarkIgnored;
            SafeRaise();

            var current = issue;
            Bind();
            UpdateSelection(current);
        }

        private void UpdateSelection(ModelIssue issue)
        {
            if (issue == null) return;

            _suppressAutoFocus = true;
            try
            {
                SelectInGrid(GridAll, issue);
                SelectInGrid(GridMEP, issue);
                SelectInGrid(GridLinks, issue);
                SelectInGrid(GridOpen, issue);

                var currentGrid = GridForCurrentTab();
                if (currentGrid != GridAll)
                    SelectInGrid(currentGrid, issue);
            }
            finally
            {
                _suppressAutoFocus = false;
            }
        }

        private static void SelectInGrid(DataGrid grid, ModelIssue issue)
        {
            if (grid?.ItemsSource == null) return;
            grid.SelectedItem = issue;
            grid.ScrollIntoView(issue);
        }

        private void UpdateCursor(ModelIssue issue)
        {
            if (issue == null) return;
            var list = IssuesForCurrentTab().ToList();
            var idx = list.IndexOf(issue);
            if (idx >= 0) _cursor = idx;
        }

        /// <summary>
        /// Lancement sécurisé de l'ExternalEvent (anti "already raised").
        /// </summary>
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
            catch { /* rien */ }
        }
    }
}

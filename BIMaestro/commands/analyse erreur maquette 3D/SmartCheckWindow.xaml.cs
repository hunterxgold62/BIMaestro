using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Analyse
{
    public partial class SmartCheckWindow : Window
    {
        private readonly ExternalEvent _extEvent;
        private readonly SmartExternalHandler _handler;

        private readonly List<ModelIssue> _all;
        private readonly List<ModelIssue> _walls;
        private readonly List<ModelIssue> _mepNoSleeve;
        private readonly List<ModelIssue> _openConnectors;
        private int _cursor = -1;

        public SmartCheckWindow(IEnumerable<ModelIssue> issues, ExternalEvent extEvent, SmartExternalHandler handler)
        {
            InitializeComponent();
            _extEvent = extEvent;
            _handler = handler;

            _all = issues.ToList();
            _walls = _all.Where(i => i.Kind == IssueKind.WallFloating || i.Kind == IssueKind.WallOnWall || i.Kind == IssueKind.WallEmbeddedInFloor).ToList();
            _mepNoSleeve = _all.Where(i => i.Kind == IssueKind.MepThroughWallNoSleeve).ToList();
            _openConnectors = _all.Where(i => i.Kind == IssueKind.MepUnconnected).ToList();

            Bind();

            GridAll.MouseDoubleClick += (s, e) => FocusFromGrid(GridAll);
            GridWalls.MouseDoubleClick += (s, e) => FocusFromGrid(GridWalls);
            GridMEP.MouseDoubleClick += (s, e) => FocusFromGrid(GridMEP);
            GridOpen.MouseDoubleClick += (s, e) => FocusFromGrid(GridOpen);
        }

        private void Bind()
        {
            GridAll.ItemsSource = _all.Where(i => !i.Ignored).ToList();
            GridWalls.ItemsSource = _walls.Where(i => !i.Ignored).ToList();
            GridMEP.ItemsSource = _mepNoSleeve.Where(i => !i.Ignored).ToList();
            GridOpen.ItemsSource = _openConnectors.Where(i => !i.Ignored).ToList();
        }

        private static bool IsValidId(ElementId id)
            => id != null && id != ElementId.InvalidElementId && id.IntegerValue > 0;

        private string CurrentTabName() => (Tabs.SelectedItem as TabItem)?.Header?.ToString() ?? "Toutes";

        private IEnumerable<ModelIssue> IssuesForCurrentTab()
        {
            switch (CurrentTabName())
            {
                case "Murs": return _walls.Where(i => !i.Ignored);
                case "Traversées (sans réservation)": return _mepNoSleeve.Where(i => !i.Ignored);
                case "Raccords ouverts": return _openConnectors.Where(i => !i.Ignored);
                default: return _all.Where(i => !i.Ignored);
            }
        }

        private ModelIssue CurrentSelection()
        {
            switch (CurrentTabName())
            {
                case "Murs": return GridWalls.SelectedItem as ModelIssue;
                case "Traversées (sans réservation)": return GridMEP.SelectedItem as ModelIssue;
                case "Raccords ouverts": return GridOpen.SelectedItem as ModelIssue;
                default: return GridAll.SelectedItem as ModelIssue;
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
            public bool Equals(ElementId a, ElementId b) => (a?.IntegerValue ?? int.MinValue) == (b?.IntegerValue ?? int.MinValue);
            public int GetHashCode(ElementId obj) => obj?.IntegerValue.GetHashCode() ?? 0;
        }

        private void OnPrev(object sender, RoutedEventArgs e)
        {
            var list = IssuesForCurrentTab().ToList();
            if (list.Count == 0) return;
            _cursor = (_cursor <= 0) ? list.Count - 1 : _cursor - 1;
            DoFocus(list[_cursor], keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void OnNext(object sender, RoutedEventArgs e)
        {
            var list = IssuesForCurrentTab().ToList();
            if (list.Count == 0) return;
            _cursor = (_cursor + 1) % list.Count;
            DoFocus(list[_cursor], keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void DoFocus(ModelIssue issue, bool keepShowAll)
        {
            GridAll.SelectedItem = issue;
            GridAll.ScrollIntoView(issue);

            _handler.Action = SmartAction.FocusApply;
            _handler.IssueId = issue.ElementId ?? ElementId.InvalidElementId;
            _handler.RelatedId = issue.RelatedId ?? ElementId.InvalidElementId;
            _handler.CurrentKind = issue.Kind;
            _handler.IssueBox = issue.BBox;
            _handler.ShowAllMode = keepShowAll;
            _handler.AutoSectionBox = true;
            SafeRaise();
        }

        private void OnFocus(object sender, RoutedEventArgs e)
        {
            var issue = ResolveIssueFromSender(sender);
            if (issue == null) return;
            DoFocus(issue, keepShowAll: BtnShowAll.IsChecked == true);
        }

        private void OnIgnore(object sender, RoutedEventArgs e)
        {
            var issue = ResolveIssueFromSender(sender);
            if (issue == null) return;
            issue.Ignored = true;

            _handler.Action = SmartAction.MarkIgnored;
            SafeRaise();

            Bind();
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

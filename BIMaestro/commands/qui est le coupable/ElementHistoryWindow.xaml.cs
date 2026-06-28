using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Analyse
{
    public partial class ElementHistoryWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/analyse?outil=qui-a-fait-ca";
        private const string PreviewPrefix = "BIMaestro_Preview_";
        private const string DeletedPreviewPrefix = PreviewPrefix + "Deleted_";
        private const string MoveOldPreviewPrefix = PreviewPrefix + "MoveOld_";
        private const string MoveNewPreviewPrefix = PreviewPrefix + "MoveNew_";
        private const string MoveArrowPreviewPrefix = PreviewPrefix + "MoveArrow_";
        private const int MaxVisibleHistoryEvents = 2000;
        private const int MaxLoadedHistoryEvents = 2000;

        private sealed class RowVm
        {
            public int ElementId { get; set; }
            public string ElementIdText { get; set; }
            public string DateText { get; set; }
            public ElementHistoryEvent Source { get; set; }
            public List<ElementHistoryEvent> Events { get; set; } = new List<ElementHistoryEvent>();
            public int EventCount => Events?.Count > 0 ? Events.Count : 1;
            public bool IsCluster => EventCount > 1;
            public string Action { get; set; }
            public string ActionText { get; set; }
            public string User { get; set; }
            public string Category { get; set; }
            public string Family { get; set; }
            public string TypeName { get; set; }
            public string PositionText { get; set; }
            public string Tx { get; set; }
            public string StoryText { get; set; }
            public string VisualSubtitle { get; set; }
            public string EvidenceText { get; set; }
            public string VisualCueText { get; set; }
            public string ActionBadgeShort { get; set; }
            public string ClusterBadgeText { get; set; }
            public string ImportanceBadgeText { get; set; }
            public string VisualInitials { get; set; }
            public string TileTitle { get; set; }
            public string TileSubtitle { get; set; }
            public string TileCountText { get; set; }
            public string TileTimeText { get; set; }
            public string TimelineGroup { get; set; }
            public string TimelineTitle { get; set; }
            public bool IsTimelineSeparator { get; set; }
            public string ImagePath { get; set; }
            public List<string> MosaicImages { get; set; } = new List<string>();
            public Brush AccentBrush { get; set; }
            public Brush AccentSoftBrush { get; set; }
            public System.Windows.Visibility TimelineSeparatorVisibility => IsTimelineSeparator ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            public System.Windows.Visibility CardVisibility => IsTimelineSeparator ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            public System.Windows.Visibility ImportanceBadgeVisibility => string.IsNullOrWhiteSpace(ImportanceBadgeText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            public bool HasMosaic => MosaicImages != null && MosaicImages.Count > 1;
            public System.Windows.Visibility MosaicVisibility => HasMosaic ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            public System.Windows.Visibility ImageVisibility => !HasMosaic && !string.IsNullOrWhiteSpace(ImagePath) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            public System.Windows.Visibility InitialsVisibility => !HasMosaic && string.IsNullOrWhiteSpace(ImagePath) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            public System.Windows.Visibility ClusterVisibility => IsCluster ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            public List<MiniEventVm> PreviewItems { get; set; } = new List<MiniEventVm>();
        }

        private sealed class MiniEventVm
        {
            public string Label { get; set; }
            public string Detail { get; set; }
        }

        private sealed class ClusterItemVm
        {
            public ElementHistoryEvent Source { get; set; }
            public string FamilyGroup { get; set; }
            public string Display { get; set; }
        }

        private sealed class ParameterRestoreChange
        {
            public string Name { get; set; }
            public string OldValue { get; set; }
            public string NewValue { get; set; }
        }

        private sealed class RestoreResult
        {
            public int Applied { get; set; }
            public int Failed { get; set; }
        }

        private sealed class SelectedContext
        {
            public int ElementId { get; set; }
            public string ElementUniqueId { get; set; }
            public string TypeUniqueId { get; set; }
            public string Category { get; set; }
            public string Family { get; set; }
            public string TypeName { get; set; }
            public bool HasSelection => ElementId > 0 || !string.IsNullOrWhiteSpace(ElementUniqueId);
        }

        private enum UiRequestType { None, Focus, VisualizeEvents, CleanPreviews, RestoreParameters, CaptureSelectedDetails }

        private sealed class UiRequest
        {
            public UiRequestType Type { get; set; }
            public List<ElementHistoryEvent> Events { get; set; } = new List<ElementHistoryEvent>();
            public ElementHistoryEvent Event { get; set; }
            public int? FocusElementId { get; set; }
            public List<int> FocusElementIds { get; set; } = new List<int>();
        }

        private sealed class UiRequestHandler : IExternalEventHandler
        {
            private readonly ElementHistoryWindow _owner;

            public UiRequestHandler(ElementHistoryWindow owner)
            {
                _owner = owner;
            }

            public string GetName() => "BIMaestro ElementHistory UiRequestHandler";

            public void Execute(UIApplication app)
            {
                var req = _owner._pendingRequest;
                _owner._pendingRequest = null;
                if (req == null || _owner._doc == null) return;

                try
                {
                    if (req.Type == UiRequestType.Focus)
                    {
                        _owner.ExecuteFocus(req.FocusElementIds.Count > 0
                            ? req.FocusElementIds
                            : req.FocusElementId.HasValue
                                ? new List<int> { req.FocusElementId.Value }
                                : new List<int>());
                        return;
                    }

                    if (req.Type == UiRequestType.VisualizeEvents)
                    {
                        _owner.ExecuteVisualize(req.Events);
                        if (req.FocusElementIds.Count > 0)
                            _owner.ExecuteFocus(req.FocusElementIds);
                        return;
                    }


                    if (req.Type == UiRequestType.CleanPreviews)
                    {
                        _owner.ExecuteCleanPreviews();
                        return;
                    }

                    if (req.Type == UiRequestType.CaptureSelectedDetails)
                    {
                        _owner.ExecuteCaptureSelectedDetails();
                        return;
                    }

                    if (req.Type == UiRequestType.RestoreParameters)
                    {
                        var result = _owner.ExecuteRestoreParameters(req.Event);
                        _owner.Dispatcher.BeginInvoke(new Action(() => _owner.ShowRestoreResult(result)));
                    }
                }
                catch
                {
                }
            }
        }

        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly SelectedContext _selectedContext;
        private readonly List<string> _initialUniqueIds;
        private List<RowVm> _rows = new List<RowVm>();
        private ExternalEvent _externalEvent;
        private UiRequestHandler _requestHandler;
        private UiRequest _pendingRequest;
        private bool _detailsVisible;
        private bool _syncingSelection;
        private int _loadVersion;
        private string _scopeFilter = "model";
        private bool _showAllLoadedEvents;

        public ElementHistoryWindow(UIDocument uidoc, Element selected)
            : this(uidoc, selected, null, null)
        {
        }

        internal ElementHistoryWindow(UIDocument uidoc, Element selected, List<ElementHistoryEvent> initialEvents, string defaultAction)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _uidoc = uidoc;
            _doc = uidoc?.Document;
            _selectedContext = BuildSelectedContext(_doc, selected);
            _initialUniqueIds = selected == null ? null : GetSelectedHistoryUniqueIds(_doc, selected);
            _scopeFilter = _selectedContext?.HasSelection == true ? "element" : "model";
            _requestHandler = new UiRequestHandler(this);
            _externalEvent = ExternalEvent.Create(_requestHandler);
            ConfigureScopePanel();
            ConfigureDeletedMeshMode();
            if (HistoryDayPicker != null)
                HistoryDayPicker.SelectedDate = DateTime.Today;

            if (selected != null)
            {
                HeaderText.Text = "Qui a fait ça ??";
                HeaderSubtitleText.Text = $"BETA - Lecture visuelle des évènements liés à {selected.Name} (Id {selected.Id.GetIdValue()}).";
                if (initialEvents != null)
                    Bind(initialEvents, defaultAction);
                else
                    BeginProgressiveLoad(ElementHistoryTracker.GetDocumentKeysForHistory(_doc), _initialUniqueIds, null);
            }
            else
            {
                HeaderText.Text = "Qui a fait ça ??";
                HeaderSubtitleText.Text = "BETA - Lecture visuelle des suppressions, déplacements, créations et clusters de la maquette.";
                if (initialEvents != null)
                    Bind(initialEvents, defaultAction);
                else
                    BeginProgressiveLoad(ElementHistoryTracker.GetDocumentKeysForHistory(_doc), null, null);
            }
        }

        internal static List<ElementHistoryEvent> LoadInitialHistory(Document doc, Element selected, out string defaultAction)
        {
            defaultAction = null;
            var modelKeys = ElementHistoryTracker.GetDocumentKeysForHistory(doc);
            var uniqueIds = selected == null ? null : GetSelectedHistoryUniqueIds(doc, selected);
            return LoadWindowHistory(modelKeys, uniqueIds, MaxLoadedHistoryEvents);
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d’ouvrir la page d’aide : {ex.Message}", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _loadVersion++;
            try { _externalEvent?.Dispose(); } catch { }
            base.OnClosed(e);
        }

        private void BeginProgressiveLoad(List<string> modelKeys, List<string> uniqueIds, string defaultAction)
        {
            var version = ++_loadVersion;
            Bind(new List<ElementHistoryEvent>(), defaultAction, false, false);
            ResultText.Text = "Chargement des évènements...";

            Task.Run(() =>
            {
                try
                {
                    var events = LoadWindowHistory(modelKeys, uniqueIds, MaxLoadedHistoryEvents);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (version != _loadVersion) return;
                        Bind(events, defaultAction, false, false);
                    }));
                }
                catch
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (version != _loadVersion) return;
                        if (ResultText != null)
                            ResultText.Text = "Historique partiellement chargé.";
                    }));
                }
            });
        }

        private static List<ElementHistoryEvent> LoadWindowHistory(List<string> modelKeys, List<string> uniqueIds, int take)
        {
            var ids = (uniqueIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count > 0)
            {
                var perElement = ids
                    .SelectMany(id => ElementHistoryTracker.LoadElementHistory(modelKeys, id, take))
                    .GroupBy(ev => BuildHistoryEventKey(ev), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(ev => ev.Ts).First())
                    .ToList();

                var modelEvents = ElementHistoryTracker.LoadRecentModelHistory(modelKeys, take);
                return perElement
                    .Concat(modelEvents)
                    .GroupBy(ev => BuildHistoryEventKey(ev), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(ev => ev.Ts).First())
                    .OrderByDescending(ev => ev.Ts)
                    .Take(take)
                    .ToList();
            }

            return ElementHistoryTracker.LoadRecentModelHistory(modelKeys, take);
        }

        private void BeginDayLoad(DateTime localDate)
        {
            var modelKeys = ElementHistoryTracker.GetDocumentKeysForHistory(_doc);
            var version = ++_loadVersion;
            Bind(new List<ElementHistoryEvent>(), null, false, true);
            ResultText.Text = "Chargement complet du " + localDate.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + "...";

            if (ScopeModelRadio != null)
                ScopeModelRadio.IsChecked = true;

            Task.Run(() =>
            {
                try
                {
                    var events = ElementHistoryTracker.LoadModelHistoryForLocalDate(modelKeys, localDate.Date);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (version != _loadVersion) return;
                        Bind(events, null, false, true);
                    }));
                }
                catch
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (version != _loadVersion) return;
                        if (ResultText != null)
                            ResultText.Text = "Historique du jour partiellement chargé.";
                    }));
                }
            });
        }

        private static SelectedContext BuildSelectedContext(Document doc, Element selected)
        {
            if (doc == null || selected == null) return null;

            var context = new SelectedContext
            {
                ElementId = selected.Id?.GetIdValue() ?? -1,
                ElementUniqueId = selected.UniqueId,
                Category = CleanCellText(selected.Category?.Name)
            };

            try
            {
                var typeId = selected.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var type = doc.GetElement(typeId) as ElementType;
                    context.TypeUniqueId = type?.UniqueId;
                    context.TypeName = CleanCellText(type?.Name);
                    if (type is FamilySymbol fs)
                        context.Family = CleanCellText(fs.FamilyName);
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(context.Family))
                context.Family = CleanCellText(selected.LookupParameter("Famille")?.AsValueString());
            if (string.IsNullOrWhiteSpace(context.TypeName))
                context.TypeName = CleanCellText(selected.Name);

            return context;
        }

        private static string BuildHistoryEventKey(ElementHistoryEvent ev)
        {
            if (ev == null) return Guid.NewGuid().ToString("N");
            return string.Join("|", new[]
            {
                ev.ModelKey ?? string.Empty,
                ev.UniqueId ?? string.Empty,
                ev.Action ?? string.Empty,
                ev.Ts.ToString("O", CultureInfo.InvariantCulture),
                ev.ElementId.ToString(CultureInfo.InvariantCulture)
            });
        }

        private static List<string> GetSelectedHistoryUniqueIds(Document doc, Element selected)
        {
            var ids = new List<string>();
            if (!string.IsNullOrWhiteSpace(selected?.UniqueId))
                ids.Add(selected.UniqueId);

            try
            {
                var typeId = selected?.GetTypeId();
                if (doc != null && typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var type = doc.GetElement(typeId);
                    if (!string.IsNullOrWhiteSpace(type?.UniqueId))
                        ids.Add(type.UniqueId);
                }
            }
            catch
            {
            }

            return ids;
        }

        private void Bind(List<ElementHistoryEvent> eventsData, string defaultAction = null, bool preserveFilters = false, bool showAllLoadedEvents = false)
        {
            _showAllLoadedEvents = showAllLoadedEvents;
            var previousAction = preserveFilters ? ActionFilterCombo?.SelectedItem as string : null;
            var previousUser = preserveFilters ? UserFilterCombo?.SelectedItem as string : null;
            var previousSearch = preserveFilters ? SearchBox?.Text : null;

            var query = (eventsData ?? new List<ElementHistoryEvent>())
                .Where(ElementHistoryTracker.IsDisplayableHistoryEvent)
                .OrderByDescending(e => e.Ts);
            var events = showAllLoadedEvents
                ? query.ToList()
                : query.Take(MaxLoadedHistoryEvents).ToList();

            _rows = BuildRows(events);
            ActionFilterCombo.ItemsSource = new[] { "Toutes" }.Concat(_rows.Select(x => x.ActionText).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
            UserFilterCombo.ItemsSource = new[] { "Tous" }.Concat(_rows.Select(x => x.User).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
            var defaultActionText = GetDefaultActionText(defaultAction, _rows);
            if (preserveFilters && !string.IsNullOrWhiteSpace(previousAction) && ActionFilterCombo.Items.Contains(previousAction))
                ActionFilterCombo.SelectedItem = previousAction;
            else
                ActionFilterCombo.SelectedItem = !string.IsNullOrWhiteSpace(defaultActionText) && ActionFilterCombo.Items.Contains(defaultActionText)
                    ? defaultActionText
                    : "Toutes";

            if (preserveFilters && !string.IsNullOrWhiteSpace(previousUser) && UserFilterCombo.Items.Contains(previousUser))
                UserFilterCombo.SelectedItem = previousUser;
            else
                UserFilterCombo.SelectedIndex = 0;

            if (preserveFilters && SearchBox != null)
                SearchBox.Text = previousSearch ?? string.Empty;
            else if (SearchBox != null && !string.IsNullOrEmpty(SearchBox.Text))
                SearchBox.Text = string.Empty;

            ApplyFilters();
        }

        private static List<RowVm> BuildRows(List<ElementHistoryEvent> events)
        {
            var rows = new List<RowVm>();
            foreach (var group in events.GroupBy(GetClusterKey))
            {
                var items = group.OrderByDescending(e => e.Ts).ToList();
                if (items.Count > 1 && CanCluster(items))
                    rows.Add(CreateClusterRow(items));
                else
                    rows.AddRange(items.Select(CreateSingleRow));
            }

            return rows
                .OrderByDescending(r => r.Source?.Ts ?? DateTime.MinValue)
                .ToList();
        }

        private static RowVm CreateSingleRow(ElementHistoryEvent e)
        {
            var row = new RowVm
            {
                ElementId = e.ElementId,
                ElementIdText = e.ElementId.ToString(CultureInfo.InvariantCulture),
                DateText = e.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Source = e,
                Events = new List<ElementHistoryEvent> { e },
                Action = e.Action,
                ActionText = GetActionText(e.Action),
                User = e.User,
                Category = CleanCellText(e.Category),
                Family = CleanCellText(e.Family),
                TypeName = CleanCellText(e.TypeName),
                PositionText = GetPositionText(e),
                Tx = CleanCellText(e.Tx)
            };
            EnrichVisualRow(row);
            return row;
        }

        private static RowVm CreateClusterRow(List<ElementHistoryEvent> events)
        {
            var first = events.OrderByDescending(e => e.Ts).First();
            var row = new RowVm
            {
                ElementId = first.ElementId,
                ElementIdText = "Cluster x" + events.Count.ToString(CultureInfo.InvariantCulture),
                DateText = first.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Source = first,
                Events = events,
                Action = first.Action,
                ActionText = GetClusterActionText(first.Action),
                User = first.User,
                Category = SummarizeText(events.Select(e => CleanCellText(e.Category)), "catégories"),
                Family = SummarizeText(events.Select(e => CleanCellText(e.Family)), "familles"),
                TypeName = SummarizeText(events.Select(e => CleanCellText(e.TypeName)), "types"),
                PositionText = events.Count.ToString(CultureInfo.InvariantCulture) + " évènements groupés",
                Tx = CleanCellText(first.Tx)
            };
            EnrichVisualRow(row);
            return row;
        }

        private static void EnrichVisualRow(RowVm row)
        {
            var events = GetRowEvents(row).ToList();
            row.AccentBrush = GetActionBrush(row.Action, 1.0);
            row.AccentSoftBrush = GetActionBrush(row.Action, 0.14);
            row.ActionBadgeShort = GetActionBadgeShort(row.Action);
            row.ClusterBadgeText = row.IsCluster
                ? row.EventCount.ToString(CultureInfo.InvariantCulture) + " éléments"
                : string.Empty;
            row.ImportanceBadgeText = GetImportanceBadgeText(row);
            row.VisualInitials = BuildVisualInitials(row, events);
            row.MosaicImages = ResolveMosaicImagePaths(events);
            row.ImagePath = ResolveBestImagePath(events);
            row.TileTitle = BuildTileTitle(row, events);
            row.TileSubtitle = BuildTileSubtitle(row, events);
            row.TileCountText = "x" + Math.Max(1, events.Count).ToString(CultureInfo.InvariantCulture);
            row.TileTimeText = (row.Source?.Ts ?? DateTime.UtcNow).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            row.TimelineGroup = BuildTimelineGroup(row.Source?.Ts ?? DateTime.UtcNow);
            row.VisualSubtitle = BuildVisualSubtitle(row, events);
            row.StoryText = BuildStoryText(row, events);
            row.EvidenceText = BuildEvidenceText(row);
            row.VisualCueText = GetVisualCueText(row.Action);
            row.PreviewItems = BuildPreviewItems(events);
        }

        private static string BuildTileTitle(RowVm row, List<ElementHistoryEvent> events)
        {
            if (row.IsCluster && string.Equals(row.Action, "move", StringComparison.OrdinalIgnoreCase))
                return "Déplacements";

            var title = row.IsCluster
                ? GetMostUsefulLabel(events)
                : FirstNonEmpty(row.TypeName, row.Family, row.Category);

            return string.IsNullOrWhiteSpace(title) ? "Elément " + row.ElementIdText : title;
        }

        private static string BuildTileSubtitle(RowVm row, List<ElementHistoryEvent> events)
        {
            var typeChangeSummary = GetTypeChangeSummary(events);
            if (string.Equals(row.Action, "type_change", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(typeChangeSummary))
                return typeChangeSummary;

            var parameterSummary = GetParameterDeltaSummary(events);
            if (string.Equals(row.Action, "param_change", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(parameterSummary))
                return parameterSummary;

            if (string.Equals(row.Action, "geometry_change", StringComparison.OrdinalIgnoreCase))
                return "Forme ou dimensions modifiées";

            if (row.IsCluster)
            {
                var values = new[]
                    {
                        SummarizeText(events.Select(e => CleanCellText(e.Category)), "catégories"),
                        SummarizeText(events.Select(e => CleanCellText(e.Family)), "familles"),
                    }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                return values.Count == 0 ? row.ActionText : string.Join(" · ", values);
            }

            return FirstNonEmpty(
                row.Family != row.TileTitle ? row.Family : null,
                row.Category != row.TileTitle ? row.Category : null,
                row.ActionText);
        }

        private static string GetImportanceBadgeText(RowVm row)
        {
            if (row == null) return string.Empty;

            if (string.Equals(row.Action, "param_change", StringComparison.OrdinalIgnoreCase))
            {
                var count = GetParameterDeltaCount(GetRowEvents(row));
                if (count > 1)
                    return count.ToString(CultureInfo.InvariantCulture) + " PARAMÈTRES";
            }

            if (!row.IsCluster) return string.Empty;
            if (row.EventCount >= 50) return "MASSIF";
            if (row.EventCount >= 20) return "IMPORTANT";
            return string.Empty;
        }

        private static string BuildStoryText(RowVm row, List<ElementHistoryEvent> events)
        {
            var count = Math.Max(1, events?.Count ?? row.EventCount);
            var actor = string.IsNullOrWhiteSpace(row.User) ? "Un utilisateur" : row.User;
            var target = GetStoryTarget(row, events, count);

            switch ((row.Action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "delete":
                    return actor + " a supprimé " + target;
                case "move":
                    return actor + " a déplacé " + target;
                case "create":
                    return actor + " a créé " + target;
                case "type_change":
                    return actor + " a changé le type de " + target;
                case "param_change":
                    var parameterSummary = GetParameterDeltaSummary(events);
                    return string.IsNullOrWhiteSpace(parameterSummary)
                        ? actor + " a modifié les paramètres de " + target
                        : actor + " a modifié " + parameterSummary + " de " + target;
                case "geometry_change":
                    return actor + " a modifié la forme ou les dimensions de " + target;
                default:
                    return actor + " a modifié " + target;
            }
        }

        private static string GetStoryTarget(RowVm row, List<ElementHistoryEvent> events, int count)
        {
            if (row.IsCluster)
            {
                var label = GetMostUsefulLabel(events);
                if (string.IsNullOrWhiteSpace(label)) label = "éléments";
                return count.ToString(CultureInfo.InvariantCulture) + " " + label;
            }

            var single = events?.FirstOrDefault() ?? row.Source;
            var text = FirstNonEmpty(CleanCellText(single?.TypeName), CleanCellText(single?.Family), CleanCellText(single?.Category));
            return string.IsNullOrWhiteSpace(text) ? "l'élément " + row.ElementIdText : text;
        }

        private static string BuildVisualSubtitle(RowVm row, List<ElementHistoryEvent> events)
        {
            if (row.IsCluster)
            {
                var categories = CountDistinct(events.Select(e => CleanCellText(e.Category)));
                var families = CountDistinct(events.Select(e => CleanCellText(e.Family)));
                var types = CountDistinct(events.Select(e => CleanCellText(e.TypeName)));
                return categories + " catégories · " + families + " familles · " + types + " types";
            }

            return string.Join(" · ", new[]
                {
                    CleanCellText(row.Category),
                    CleanCellText(row.Family),
                    CleanCellText(row.TypeName)
                }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string BuildEvidenceText(RowVm row)
        {
            var tx = CleanCellText(row.Tx);
            if (string.IsNullOrWhiteSpace(tx)) tx = "Transaction non renseignée";
            return row.DateText + " · " + tx;
        }

        private static string BuildTimelineGroup(DateTime utc)
        {
            var local = utc.ToLocalTime();
            var today = DateTime.Today;
            var date = local.Date;

            if (date == today) return "Aujourd'hui";
            if (date == today.AddDays(-1)) return "Hier";
            if (date >= today.AddDays(-7)) return "Cette semaine";
            if (date.Year == today.Year) return local.ToString("MMMM", CultureInfo.CurrentCulture);
            return local.ToString("yyyy", CultureInfo.InvariantCulture);
        }

        private static List<MiniEventVm> BuildPreviewItems(List<ElementHistoryEvent> events)
        {
            return (events ?? new List<ElementHistoryEvent>())
                .OrderBy(e => e.ElementId)
                .Take(4)
                .Select(e => new MiniEventVm
                {
                    Label = "Id " + e.ElementId.ToString(CultureInfo.InvariantCulture),
                    Detail = FirstNonEmpty(CleanCellText(e.TypeName), CleanCellText(e.Family), CleanCellText(e.Category), "Elément")
                })
                .ToList();
        }

        private static string GetParameterDeltaSummary(IEnumerable<ElementHistoryEvent> events)
        {
            var names = new List<string>();
            foreach (var ev in events ?? Enumerable.Empty<ElementHistoryEvent>())
            {
                if (ev?.Delta == null || !ev.Delta.TryGetValue("parameters", out var raw) || raw == null)
                    continue;

                names.AddRange(ReadParameterNames(raw));
            }

            var distinct = names
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinct.Count == 0) return string.Empty;
            if (distinct.Count == 1) return distinct[0];

            var preview = distinct.Take(3).ToList();
            var extra = distinct.Count - preview.Count;
            return distinct.Count.ToString(CultureInfo.InvariantCulture) +
                   " paramètres modifiés: " +
                   string.Join(", ", preview) +
                   (extra > 0 ? " +" + extra.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        private static int GetParameterDeltaCount(IEnumerable<ElementHistoryEvent> events)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in events ?? Enumerable.Empty<ElementHistoryEvent>())
            {
                if (ev?.Delta == null || !ev.Delta.TryGetValue("parameters", out var raw) || raw == null)
                    continue;

                foreach (var name in ReadParameterNames(raw))
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }

            return names.Count;
        }

        private static string GetTypeChangeSummary(IEnumerable<ElementHistoryEvent> events)
        {
            foreach (var ev in events ?? Enumerable.Empty<ElementHistoryEvent>())
            {
                if (ev?.Delta == null) continue;
                var oldType = ReadDeltaString(ev.Delta, "oldType");
                var newType = ReadDeltaString(ev.Delta, "newType");
                if (!string.IsNullOrWhiteSpace(oldType) && !string.IsNullOrWhiteSpace(newType))
                    return oldType + " -> " + newType;
            }

            return string.Empty;
        }

        private static string GetReadableDeltaSummary(ElementHistoryEvent ev)
        {
            if (ev == null) return string.Empty;

            var action = (ev.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (action == "type_change")
            {
                var summary = GetTypeChangeSummary(new[] { ev });
                return string.IsNullOrWhiteSpace(summary) ? string.Empty : "Avant / après: " + summary;
            }

            if (action == "param_change")
            {
                var summary = GetParameterBeforeAfterSummary(ev);
                return string.IsNullOrWhiteSpace(summary) ? string.Empty : "Avant / après: " + summary;
            }

            if (action == "move" && ev.Delta != null && ev.Delta.TryGetValue("new", out var newPos) && ev.Delta.TryGetValue("old", out var oldPos))
            {
                var moveSummary = GetMoveDeltaSummary(ev);
                return string.IsNullOrWhiteSpace(moveSummary)
                    ? "Avant / après: " + CompactPoint(oldPos) + " -> " + CompactPoint(newPos)
                    : "Avant / après: " + moveSummary;
            }

            if (action == "geometry_change")
            {
                var summary = GetGeometrySizeSummary(ev);
                return string.IsNullOrWhiteSpace(summary) ? "Forme / dimensions modifiées" : "Avant / après: " + summary;
            }

            return string.Empty;
        }

        private static string GetGeometrySizeSummary(ElementHistoryEvent ev)
        {
            if (ev?.Delta == null) return string.Empty;
            if (!ev.Delta.TryGetValue("oldSize", out var oldRaw) || !ev.Delta.TryGetValue("newSize", out var newRaw))
                return string.Empty;

            if (!TryReadVector(oldRaw, out var oldX, out var oldY, out var oldZ)
                || !TryReadVector(newRaw, out var newX, out var newY, out var newZ))
                return string.Empty;

            var parts = new List<string>();
            AddSizeChange(parts, "X", oldX, newX);
            AddSizeChange(parts, "Y", oldY, newY);
            AddSizeChange(parts, "Z", oldZ, newZ);

            return parts.Count == 0 ? "Forme modifiée sans variation de taille lisible" : "Dimensions " + string.Join(", ", parts);
        }

        private static string GetMoveDeltaSummary(ElementHistoryEvent ev)
        {
            if (ev?.Delta == null) return string.Empty;
            var parts = new List<string>();
            AddMoveAxis(parts, "X", ReadDeltaDouble(ev.Delta, "dx"));
            AddMoveAxis(parts, "Y", ReadDeltaDouble(ev.Delta, "dy"));
            AddMoveAxis(parts, "Z", ReadDeltaDouble(ev.Delta, "dz"));
            return parts.Count == 0 ? string.Empty : "Déplacement " + string.Join(", ", parts);
        }

        private static void AddMoveAxis(List<string> parts, string label, double? feet)
        {
            if (parts == null || feet == null) return;
            var mm = feet.Value * 304.8;
            if (Math.Abs(mm) < 2.0) return;
            parts.Add("Δ" + label + " " + mm.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + " mm");
        }

        private static double? ReadDeltaDouble(Dictionary<string, object> delta, string key)
        {
            if (delta == null || !delta.TryGetValue(key, out var value) || value == null) return null;
            try
            {
                if (value is JValue jv)
                    return jv.Value<double?>();
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static void AddSizeChange(List<string> parts, string label, double oldFeet, double newFeet)
        {
            if (parts == null) return;
            var deltaMm = Math.Abs((newFeet - oldFeet) * 304.8);
            if (deltaMm < 2.0) return;
            parts.Add(label + " " + FormatFeetAsMm(oldFeet) + " -> " + FormatFeetAsMm(newFeet));
        }

        private static bool TryReadVector(object raw, out double x, out double y, out double z)
        {
            x = y = z = 0;
            try
            {
                var j = raw as JObject ?? JObject.FromObject(raw);
                x = j.Value<double?>("x") ?? 0;
                y = j.Value<double?>("y") ?? 0;
                z = j.Value<double?>("z") ?? 0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatFeetAsMm(double feet)
        {
            return (feet * 304.8).ToString("0.#", CultureInfo.InvariantCulture) + " mm";
        }

        private static string GetParameterBeforeAfterSummary(ElementHistoryEvent ev)
        {
            if (ev?.Delta == null || !ev.Delta.TryGetValue("parameters", out var raw) || raw == null)
                return string.Empty;

            var changes = ReadParameterChangeLines(raw).Take(3).ToList();
            return changes.Count == 0 ? string.Empty : string.Join("; ", changes);
        }

        private static IEnumerable<string> ReadParameterChangeLines(object raw)
        {
            foreach (var change in ReadParameterChanges(raw))
                yield return change.Name + ": " + FormatParameterDisplayValue(change.OldValue) + " -> " + FormatParameterDisplayValue(change.NewValue);
        }

        private static string FormatParameterDisplayValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "vide" : value.Trim();
        }

        private static List<ParameterRestoreChange> ReadParameterChanges(object raw)
        {
            var result = new List<ParameterRestoreChange>();
            if (raw == null || raw is string) return result;

            if (raw is JArray jArray)
            {
                foreach (var item in jArray.OfType<JObject>())
                    AddParameterChange(result,
                        item.Value<string>("Name") ?? item.Value<string>("name"),
                        item.Value<string>("OldValue") ?? item.Value<string>("oldValue"),
                        item.Value<string>("NewValue") ?? item.Value<string>("newValue"));
                return result;
            }

            if (raw is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (item is JObject jo)
                    {
                        AddParameterChange(result,
                            jo.Value<string>("Name") ?? jo.Value<string>("name"),
                            jo.Value<string>("OldValue") ?? jo.Value<string>("oldValue"),
                            jo.Value<string>("NewValue") ?? jo.Value<string>("newValue"));
                        continue;
                    }

                    var type = item.GetType();
                    AddParameterChange(result,
                        type.GetProperty("Name")?.GetValue(item, null) as string,
                        type.GetProperty("OldValue")?.GetValue(item, null) as string,
                        type.GetProperty("NewValue")?.GetValue(item, null) as string);
                }
            }

            return result;
        }

        private static void AddParameterChange(List<ParameterRestoreChange> result, string name, string oldValue, string newValue)
        {
            var cleanName = CleanCellText(name);
            var cleanOld = CleanCellText(oldValue);
            var cleanNew = CleanCellText(newValue);
            if (string.IsNullOrWhiteSpace(cleanName)) return;
            if (string.IsNullOrWhiteSpace(cleanOld) && string.IsNullOrWhiteSpace(cleanNew)) return;

            result.Add(new ParameterRestoreChange
            {
                Name = cleanName,
                OldValue = cleanOld,
                NewValue = cleanNew
            });
        }

        private static string ReadDeltaString(Dictionary<string, object> delta, string key)
        {
            if (delta == null || !delta.TryGetValue(key, out var value) || value == null) return string.Empty;
            if (value is JValue jv) return CleanCellText(Convert.ToString(jv.Value, CultureInfo.InvariantCulture));
            return CleanCellText(value.ToString());
        }

        private static IEnumerable<string> ReadParameterNames(object raw)
        {
            if (raw is JArray jArray)
            {
                foreach (var item in jArray.OfType<JObject>())
                {
                    var name = CleanCellText(item.Value<string>("Name") ?? item.Value<string>("name"));
                    if (!string.IsNullOrWhiteSpace(name)) yield return name;
                }
                yield break;
            }

            if (raw is string) yield break;
            if (raw is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (item is JObject jo)
                    {
                        var name = CleanCellText(jo.Value<string>("Name") ?? jo.Value<string>("name"));
                        if (!string.IsNullOrWhiteSpace(name)) yield return name;
                        continue;
                    }

                    var prop = item.GetType().GetProperty("Name");
                    var value = CleanCellText(prop?.GetValue(item, null) as string);
                    if (!string.IsNullOrWhiteSpace(value)) yield return value;
                }
            }
        }

        private static bool CanRestoreParameters(ElementHistoryEvent ev)
        {
            if (ev?.Delta == null || !string.Equals(ev.Action, "param_change", StringComparison.OrdinalIgnoreCase))
                return false;
            return ev.Delta.TryGetValue("parameters", out var raw) && ReadParameterChanges(raw).Count > 0;
        }

        private static string ResolveBestImagePath(List<ElementHistoryEvent> events)
        {
            foreach (var ev in events ?? new List<ElementHistoryEvent>())
            {
                var resolved = ResolveEventThumbnailPath(ev);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }

            return null;
        }

        private static List<string> ResolveMosaicImagePaths(List<ElementHistoryEvent> events)
        {
            var result = new List<string>();
            var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ev in events ?? new List<ElementHistoryEvent>())
            {
                var family = CleanCellText(ev?.Family);
                var type = CleanCellText(ev?.TypeName);
                var familyKey = FirstNonEmpty(family, type, CleanCellText(ev?.Category));
                if (!string.IsNullOrWhiteSpace(familyKey) && seenFamilies.Contains(familyKey))
                    continue;

                var image = ResolveEventThumbnailPath(ev);
                if (string.IsNullOrWhiteSpace(image) || seenImages.Contains(image))
                    continue;

                seenImages.Add(image);
                if (!string.IsNullOrWhiteSpace(familyKey))
                    seenFamilies.Add(familyKey);
                result.Add(image);

                if (result.Count >= 4)
                    break;
            }

            return result.Count > 1 ? result : new List<string>();
        }

        private static string ResolveEventThumbnailPath(ElementHistoryEvent ev)
        {
            var stored = CleanCellText(ev?.ThumbnailPath);
            var family = CleanCellText(ev?.Family);
            var typeName = CleanCellText(ev?.TypeName);

            if (!IsAnnotationOrTagCategory(CleanCellText(ev?.Category)))
            {
                var catalog = ElementHistoryTracker.ResolveThumbnailPath(family, typeName);
                if (!string.IsNullOrWhiteSpace(catalog))
                    return catalog;
            }

            if (ElementHistoryTracker.IsThumbnailPathValidForFamily(stored, family))
                return stored;

            return null;
        }

        private static bool IsAnnotationOrTagCategory(string category)
        {
            var text = CleanCellText(category);
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("étiquette", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("etiquette", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf(" tag", StringComparison.OrdinalIgnoreCase) >= 0
                || text.EndsWith("tag", StringComparison.OrdinalIgnoreCase)
                || text.IndexOf("tags", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("annotation", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildVisualInitials(RowVm row, List<ElementHistoryEvent> events)
        {
            var label = row.IsCluster
                ? GetMostUsefulLabel(events)
                : FirstNonEmpty(row.TypeName, row.Family, row.Category);

            if (string.IsNullOrWhiteSpace(label)) return row.ActionBadgeShort;

            var parts = label
                .Split(new[] { ' ', '-', '_', '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.Length > 0)
                .Take(2)
                .Select(x => char.ToUpperInvariant(x[0]).ToString(CultureInfo.InvariantCulture));

            var text = string.Concat(parts);
            return string.IsNullOrWhiteSpace(text) ? row.ActionBadgeShort : text;
        }

        private static string GetMostUsefulLabel(List<ElementHistoryEvent> events)
        {
            var types = events.Select(e => CleanCellText(e.TypeName)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (types.Count == 1) return types[0];

            var families = events.Select(e => CleanCellText(e.Family)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (families.Count == 1) return families[0];

            var categories = events.Select(e => CleanCellText(e.Category)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (categories.Count == 1) return categories[0];

            return "éléments";
        }

        private static int CountDistinct(IEnumerable<string> values)
        {
            return values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private static string GetActionBadgeShort(string action)
        {
            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "delete": return "SUPPR.";
                case "move": return "DEPL.";
                case "create": return "AJOUT";
                case "type_change": return "TYPE";
                case "param_change": return "PARAM.";
                case "geometry_change": return "FORME";
                default: return "MODIF.";
            }
        }

        private static string GetVisualCueText(string action)
        {
            return CanVisualize(new ElementHistoryEvent { Action = action }) ? "Visualisable" : "Données";
        }

        private static Brush GetActionBrush(string action, double opacity)
        {
            System.Windows.Media.Color color;
            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "delete":
                    color = System.Windows.Media.Color.FromRgb(216, 48, 48);
                    break;
                case "move":
                    color = System.Windows.Media.Color.FromRgb(226, 138, 0);
                    break;
                case "create":
                    color = System.Windows.Media.Color.FromRgb(39, 141, 66);
                    break;
                case "type_change":
                    color = System.Windows.Media.Color.FromRgb(86, 83, 216);
                    break;
                case "param_change":
                    color = System.Windows.Media.Color.FromRgb(0, 118, 163);
                    break;
                case "geometry_change":
                    color = System.Windows.Media.Color.FromRgb(17, 121, 102);
                    break;
                default:
                    color = System.Windows.Media.Color.FromRgb(100, 116, 139);
                    break;
            }

            var brush = new SolidColorBrush(color) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }

        private static string GetClusterKey(ElementHistoryEvent e)
        {
            var ticks = e.Ts.ToUniversalTime().Ticks / TimeSpan.FromSeconds(5).Ticks;
            if (string.Equals(e.Action, "create", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join("|",
                    CleanCellText(e.Action),
                    CleanCellText(e.User),
                    CleanCellText(e.Tx),
                    "bulk-create",
                    ticks.ToString(CultureInfo.InvariantCulture));
            }

            if (string.Equals(e.Action, "delete", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join("|",
                    CleanCellText(e.Action),
                    CleanCellText(e.User),
                    CleanCellText(e.Tx),
                    "bulk-delete",
                    ticks.ToString(CultureInfo.InvariantCulture));
            }

            if (string.Equals(e.Action, "move", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join("|",
                    CleanCellText(e.Action),
                    CleanCellText(e.User),
                    CleanCellText(e.Tx),
                    "bulk-move",
                    ticks.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join("|",
                CleanCellText(e.Action),
                CleanCellText(e.User),
                CleanCellText(e.Tx),
                CleanCellText(e.Category),
                CleanCellText(e.Family),
                CleanCellText(e.TypeName),
                ticks.ToString(CultureInfo.InvariantCulture));
        }

        private static bool CanCluster(List<ElementHistoryEvent> events)
        {
            var first = events.FirstOrDefault();
            if (first == null || string.IsNullOrWhiteSpace(first.Tx)) return false;

            if (string.Equals(first.Action, "delete", StringComparison.OrdinalIgnoreCase))
            {
                return events.All(e =>
                    string.Equals(e.Action, first.Action, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.User, first.User, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Tx, first.Tx, StringComparison.OrdinalIgnoreCase));
            }

            if (string.Equals(first.Action, "create", StringComparison.OrdinalIgnoreCase))
            {
                return events.All(e =>
                    string.Equals(e.Action, first.Action, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.User, first.User, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Tx, first.Tx, StringComparison.OrdinalIgnoreCase));
            }

            if (string.Equals(first.Action, "move", StringComparison.OrdinalIgnoreCase))
            {
                return events.All(e =>
                    string.Equals(e.Action, first.Action, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.User, first.User, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Tx, first.Tx, StringComparison.OrdinalIgnoreCase));
            }

            return events.All(e =>
                string.Equals(e.Action, first.Action, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.User, first.User, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Tx, first.Tx, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(CleanCellText(e.Category), CleanCellText(first.Category), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(CleanCellText(e.Family), CleanCellText(first.Family), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(CleanCellText(e.TypeName), CleanCellText(first.TypeName), StringComparison.OrdinalIgnoreCase));
        }

        private static string SummarizeText(IEnumerable<string> values, string label)
        {
            var distinct = values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

            if (distinct.Count == 0) return string.Empty;
            if (distinct.Count == 1) return distinct[0];
            return distinct.Count.ToString(CultureInfo.InvariantCulture) + " " + label;
        }

        private static string CleanCellText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.Trim();
            return text.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? string.Empty : text;
        }

        private static string GetActionText(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return string.Empty;
            switch (action.Trim().ToLowerInvariant())
            {
                case "delete": return "Suppression";
                case "move": return "Déplacement";
                case "create": return "Création";
                case "type_change": return "Changement de type";
                case "param_change": return "Modification paramètres";
                case "geometry_change": return "Forme / dimensions";
                case "modify": return "Modification";
                default: return action.Trim();
            }
        }

        private static string GetClusterActionText(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return string.Empty;
            switch (action.Trim().ToLowerInvariant())
            {
                case "move": return "Déplacements";
                case "delete": return "Suppressions";
                case "create": return "Créations";
                default: return GetActionText(action);
            }
        }

        private static string GetDefaultActionText(string action, IEnumerable<RowVm> rows)
        {
            var single = GetActionText(action);
            if (string.IsNullOrWhiteSpace(single)) return single;

            var available = (rows ?? Enumerable.Empty<RowVm>())
                .Select(r => r?.ActionText)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (available.Contains(single)) return single;

            var clustered = GetClusterActionText(action);
            return available.Contains(clustered) ? clustered : single;
        }

        private void HistoryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_syncingSelection && HistoryGrid?.SelectedItem is RowVm row && VisualCardsList != null)
            {
                _syncingSelection = true;
                VisualCardsList.SelectedItem = row;
                VisualCardsList.ScrollIntoView(row);
                _syncingSelection = false;
            }

            UpdateVisualizeButtonLabel();
            UpdateDetailsLayout();
            UpdateDetails();
        }

        private void VisualCardsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_syncingSelection && VisualCardsList?.SelectedItem is RowVm row && !row.IsTimelineSeparator && HistoryGrid != null)
            {
                _syncingSelection = true;
                HistoryGrid.SelectedItem = row;
                HistoryGrid.ScrollIntoView(row);
                _syncingSelection = false;
            }

            UpdateVisualizeButtonLabel();
            UpdateDetailsLayout();
            UpdateDetails();
        }

        private void VisualCardsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            HandleSmartDoubleClick();
        }

        private void UpdateVisualizeButtonLabel()
        {
            var row = GetPrimarySelectedRow();
            var hasRow = row?.Source != null;

            if (VisualizeDeletedButton != null)
            {
                VisualizeDeletedButton.IsEnabled = hasRow;
                VisualizeDeletedButton.Content = hasRow ? GetPrimaryActionText(row) : "Visualiser";
            }

            if (FocusButton != null)
                FocusButton.IsEnabled = hasRow;
            if (DetailsButton != null)
                DetailsButton.IsEnabled = hasRow;

            UpdateRestoreButtonLabel(row);
        }

        private static string GetPrimaryActionText(RowVm row)
        {
            if (row == null || row.Source == null)
                return "Visualiser";

            if (row.IsCluster)
                return GetRowEvents(row).Any(CanVisualize) ? "Visualiser cluster" : "Focus cluster";

            if (string.Equals(row.Source.Action, "delete", StringComparison.OrdinalIgnoreCase))
                return "Visualiser suppression";
            if (HasMoveDelta(row.Source))
                return "Visualiser déplacement";

            return "Focus élément";
        }

        private void UpdateRestoreButtonLabel(RowVm row)
        {
            if (RestoreParametersButton == null) return;
            var canRestore = CanRestoreParameters(row?.Source);
            RestoreParametersButton.IsEnabled = canRestore;
            RestoreParametersButton.Visibility = canRestore ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            RestoreParametersButton.Content = "Restaurer";
            RestoreParametersButton.ToolTip = canRestore
                ? "Réappliquer les anciennes valeurs de paramètres enregistrées pour cette ligne"
                : "Disponible uniquement sur une modification de paramètres avec avant/après";
        }

        private void ActionFilterCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void UserFilterCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ConfigureScopePanel()
        {
            if (ContextScopePanel == null) return;

            var hasContext = _selectedContext?.HasSelection == true;
            ContextScopePanel.Visibility = hasContext ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (!hasContext) return;

            if (ContextScopeText != null)
            {
                var label = FirstNonEmpty(_selectedContext.TypeName, _selectedContext.Family, _selectedContext.Category, "sélection");
                ContextScopeText.Text = "Portée : " + label;
            }

            if (ScopeTypeRadio != null)
                ScopeTypeRadio.IsEnabled = !string.IsNullOrWhiteSpace(_selectedContext.TypeName) || !string.IsNullOrWhiteSpace(_selectedContext.TypeUniqueId);
            if (ScopeFamilyRadio != null)
                ScopeFamilyRadio.IsEnabled = !string.IsNullOrWhiteSpace(_selectedContext.Family);
            if (ScopeElementRadio != null)
                ScopeElementRadio.IsChecked = true;
        }

        private void ConfigureDeletedMeshMode()
        {
            var detailed = ElementHistoryTracker.CaptureDetailedDeletedMesh;
            if (SimpleMeshModeRadio != null)
                SimpleMeshModeRadio.IsChecked = !detailed;
            if (DetailedMeshModeRadio != null)
                DetailedMeshModeRadio.IsChecked = detailed;
        }

        private void ScopeFilter_Checked(object sender, RoutedEventArgs e)
        {
            var tag = (sender as RadioButton)?.Tag as string;
            _scopeFilter = string.IsNullOrWhiteSpace(tag) ? "model" : tag;
            if (!AreFilterControlsReady()) return;
            ApplyFilters();
        }

        private void DeletedMeshMode_Checked(object sender, RoutedEventArgs e)
        {
            var tag = (sender as RadioButton)?.Tag as string;
            if (string.IsNullOrWhiteSpace(tag)) return;

            var detailed = string.Equals(tag, "detailed", StringComparison.OrdinalIgnoreCase);
            ElementHistoryTracker.CaptureDetailedDeletedMesh = detailed;

            if (detailed)
                RaiseRequest(new UiRequest { Type = UiRequestType.CaptureSelectedDetails });
        }

        private void LoadDayButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedDate = HistoryDayPicker?.SelectedDate ?? DateTime.Today;
            BeginDayLoad(selectedDate);
        }

        private void QuickFilterFamily_Click(object sender, RoutedEventArgs e)
        {
            var row = GetMenuRow(sender);
            var family = GetSingleDistinctValue(row, x => x.Family);
            ApplyQuickFilter("Toutes", "Tous", family);
        }

        private void QuickFilterType_Click(object sender, RoutedEventArgs e)
        {
            var row = GetMenuRow(sender);
            var type = GetSingleDistinctValue(row, x => x.TypeName);
            ApplyQuickFilter("Toutes", "Tous", type);
        }

        private void QuickFilterUser_Click(object sender, RoutedEventArgs e)
        {
            var row = GetMenuRow(sender);
            ApplyQuickFilter("Toutes", FirstNonEmpty(row?.User, "Tous"), string.Empty);
        }

        private void QuickFilterAction_Click(object sender, RoutedEventArgs e)
        {
            var row = GetMenuRow(sender);
            ApplyQuickFilter(FirstNonEmpty(row?.ActionText, "Toutes"), "Tous", string.Empty);
        }

        private void QuickFilterReset_Click(object sender, RoutedEventArgs e)
        {
            ApplyQuickFilter("Toutes", "Tous", string.Empty);
        }

        private static RowVm GetMenuRow(object sender)
        {
            return (sender as MenuItem)?.CommandParameter as RowVm;
        }

        private static string GetSingleDistinctValue(RowVm row, Func<ElementHistoryEvent, string> selector)
        {
            if (row == null) return string.Empty;

            var values = GetRowEvents(row)
                .Select(selector)
                .Select(CleanCellText)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values.Count == 1 ? values[0] : FirstNonEmpty(CleanCellText(row.TileTitle), CleanCellText(row.Family), CleanCellText(row.TypeName), CleanCellText(row.Category));
        }

        private void ApplyQuickFilter(string action, string user, string search)
        {
            if (ActionFilterCombo?.Items.Contains(action) == true)
                ActionFilterCombo.SelectedItem = action;
            else if (ActionFilterCombo?.Items.Contains("Toutes") == true)
                ActionFilterCombo.SelectedItem = "Toutes";

            if (UserFilterCombo?.Items.Contains(user) == true)
                UserFilterCombo.SelectedItem = user;
            else if (UserFilterCombo?.Items.Contains("Tous") == true)
                UserFilterCombo.SelectedItem = "Tous";

            if (SearchBox != null)
                SearchBox.Text = search ?? string.Empty;

            ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (!AreFilterControlsReady()) return;

            var action = ActionFilterCombo?.SelectedItem as string ?? "Toutes";
            var user = UserFilterCombo?.SelectedItem as string ?? "Tous";
            var q = (SearchBox?.Text ?? string.Empty).Trim();

            IEnumerable<RowVm> rows = _rows;
            rows = rows.Where(MatchesScope);

            if (!string.Equals(action, "Toutes", StringComparison.OrdinalIgnoreCase))
                rows = rows.Where(r => string.Equals(r.ActionText, action, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(user, "Tous", StringComparison.OrdinalIgnoreCase))
                rows = rows.Where(r => string.Equals(r.User, user, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(q))
            {
                rows = rows.Where(r => (r.Category ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (r.Family ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (r.TypeName ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (r.Tx ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (r.ElementIdText ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || r.Events.Any(ev => ev.ElementId.ToString(CultureInfo.InvariantCulture).Contains(q)));
            }

            var matching = rows.ToList();
            var filtered = _showAllLoadedEvents
                ? matching.ToList()
                : matching.Take(MaxVisibleHistoryEvents).ToList();
            HistoryGrid.ItemsSource = filtered;
            VisualCardsList.ItemsSource = BuildTimelineItems(filtered);
            if (filtered.Count > 0 && GetPrimarySelectedRow() == null)
            {
                VisualCardsList.SelectedItem = filtered[0];
                HistoryGrid.SelectedItem = filtered[0];
            }
            UpdateStats(filtered);
            UpdateVisualizeButtonLabel();
            UpdateDetailsLayout();
            UpdateDetails();
        }

        private bool AreFilterControlsReady()
        {
            return HistoryGrid != null
                && VisualCardsList != null
                && ActionFilterCombo != null
                && UserFilterCombo != null
                && SearchBox != null;
        }

        private bool MatchesScope(RowVm row)
        {
            if (row == null || row.IsTimelineSeparator) return false;
            if (_selectedContext?.HasSelection != true) return true;
            if (string.Equals(_scopeFilter, "model", StringComparison.OrdinalIgnoreCase)) return true;

            return GetRowEvents(row).Any(MatchesScope);
        }

        private bool MatchesScope(ElementHistoryEvent ev)
        {
            if (ev == null || _selectedContext == null) return false;

            var scope = (_scopeFilter ?? "model").Trim().ToLowerInvariant();
            if (scope == "model") return true;

            if (scope == "element")
            {
                if (_selectedContext.ElementId > 0 && ev.ElementId == _selectedContext.ElementId) return true;
                if (!string.IsNullOrWhiteSpace(_selectedContext.ElementUniqueId)
                    && string.Equals(ev.UniqueId, _selectedContext.ElementUniqueId, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrWhiteSpace(_selectedContext.TypeUniqueId)
                    && string.Equals(ev.UniqueId, _selectedContext.TypeUniqueId, StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }

            if (scope == "type")
            {
                if (!string.IsNullOrWhiteSpace(_selectedContext.TypeUniqueId)
                    && string.Equals(ev.UniqueId, _selectedContext.TypeUniqueId, StringComparison.OrdinalIgnoreCase))
                    return true;
                return !string.IsNullOrWhiteSpace(_selectedContext.TypeName)
                    && string.Equals(CleanCellText(ev.TypeName), _selectedContext.TypeName, StringComparison.OrdinalIgnoreCase);
            }

            if (scope == "family")
            {
                return !string.IsNullOrWhiteSpace(_selectedContext.Family)
                    && string.Equals(CleanCellText(ev.Family), _selectedContext.Family, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static List<RowVm> BuildTimelineItems(List<RowVm> rows)
        {
            var items = new List<RowVm>();
            string currentGroup = null;

            foreach (var row in rows ?? new List<RowVm>())
            {
                var group = string.IsNullOrWhiteSpace(row.TimelineGroup)
                    ? BuildTimelineGroup(row.Source?.Ts ?? DateTime.UtcNow)
                    : row.TimelineGroup;

                if (!string.Equals(currentGroup, group, StringComparison.Ordinal))
                {
                    currentGroup = group;
                    items.Add(new RowVm
                    {
                        IsTimelineSeparator = true,
                        TimelineGroup = group,
                        TimelineTitle = group,
                        ElementIdText = string.Empty
                    });
                }

                items.Add(row);
            }

            return items;
        }

        private void UpdateStats(List<RowVm> rows)
        {
            if (TotalStatText == null) return;
            var events = rows.SelectMany(GetRowEvents).ToList();
            TotalStatText.Text = events.Count.ToString(CultureInfo.InvariantCulture);
            DeleteStatText.Text = events.Count(e => string.Equals(e.Action, "delete", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
            MoveStatText.Text = events.Count(e => string.Equals(e.Action, "move", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
            CreateStatText.Text = events.Count(e => string.Equals(e.Action, "create", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
            UserStatText.Text = events.Select(e => e.User).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.InvariantCulture);

            if (ResultText != null)
            {
                var total = _rows.SelectMany(GetRowEvents).Count();
                ResultText.Text = events.Count == total
                    ? total.ToString(CultureInfo.InvariantCulture) + " évènements affichés"
                    : events.Count.ToString(CultureInfo.InvariantCulture) + " / " + total.ToString(CultureInfo.InvariantCulture) + " évènements affichés";
            }
        }

        private void HistoryGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            HandleSmartDoubleClick();
        }

        private void FocusButton_Click(object sender, RoutedEventArgs e)
        {
            FocusSelectedElement();
        }

        private void DetailsButton_Click(object sender, RoutedEventArgs e)
        {
            _detailsVisible = !_detailsVisible;
            UpdateDetailsLayout();
            if (DetailsButton != null)
                DetailsButton.Content = _detailsVisible ? "Masquer détails" : "Détails";
            UpdateDetails();
        }

        private void UpdateDetailsLayout()
        {
            if (DetailsPanel == null) return;
            DetailsPanel.Visibility = _detailsVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            var wide = _detailsVisible && ShouldUseWideDetails();
            if (HistoryTabs != null)
                HistoryTabs.Visibility = wide ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            System.Windows.Controls.Grid.SetRow(DetailsPanel, wide ? 3 : 0);
            System.Windows.Controls.Grid.SetRowSpan(DetailsPanel, wide ? 1 : 4);
            System.Windows.Controls.Grid.SetColumn(DetailsPanel, wide ? 0 : 1);
            System.Windows.Controls.Grid.SetColumnSpan(DetailsPanel, wide ? 2 : 1);
            DetailsPanel.Width = wide ? double.NaN : 420;
            DetailsPanel.Margin = wide ? new Thickness(0, 12, 0, 0) : new Thickness(12, 0, 0, 0);

            if (DetailsScroll != null)
            {
                System.Windows.Controls.Grid.SetRow(DetailsScroll, 1);
                System.Windows.Controls.Grid.SetRowSpan(DetailsScroll, wide ? 2 : 1);
                DetailsScroll.MaxHeight = wide ? double.PositiveInfinity : 220;
                DetailsScroll.VerticalAlignment = wide ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            }
        }

        private bool ShouldUseWideDetails()
        {
            var row = GetPrimarySelectedRow();
            return row != null && row.Source != null && !row.IsCluster;
        }


        private void CleanPreviewsButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseRequest(new UiRequest { Type = UiRequestType.CleanPreviews });
        }

        private void RestoreParametersButton_Click(object sender, RoutedEventArgs e)
        {
            var row = GetPrimarySelectedRow();
            if (!CanRestoreParameters(row?.Source)) return;

            var confirm = MessageBox.Show(
                "Restaurer les anciennes valeurs de paramètres pour cet évènement ?",
                "BIMaestro - Qui a fait ça ?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.RestoreParameters,
                Event = row.Source
            });
        }

        private void FocusSelectedElement()
        {
            var row = GetPrimarySelectedRow();
            if (row == null) return;
            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.Focus,
                FocusElementIds = GetRowEvents(row).Select(e => e.ElementId).Distinct().ToList()
            });
        }

        private void FocusHistoryEvent(ElementHistoryEvent ev)
        {
            if (ev == null) return;
            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.Focus,
                FocusElementIds = new List<int> { ev.ElementId }
            });
        }

        private void HandleSmartDoubleClick()
        {
            var row = GetPrimarySelectedRow();
            if (row == null || row.Source == null) return;

            var events = GetRowEvents(row).ToList();
            if (events.Any(e => string.Equals(e.Action, "move", StringComparison.OrdinalIgnoreCase)))
            {
                VisualizeRows(new List<RowVm> { row }, true);
                return;
            }

            if (events.Any(e => string.Equals(e.Action, "delete", StringComparison.OrdinalIgnoreCase)))
            {
                VisualizeRows(new List<RowVm> { row }, true);
                return;
            }

            if (events.Any(CanVisualize))
            {
                VisualizeRows(new List<RowVm> { row });
                return;
            }

            ShowDetails();
        }

        private void ShowDetails()
        {
            _detailsVisible = true;
            if (DetailsButton != null)
                DetailsButton.Content = "Masquer détails";

            UpdateDetailsLayout();
            UpdateDetails();
        }

        private void VisualizeDeletedButton_Click(object sender, RoutedEventArgs e)
        {
            var row = GetPrimarySelectedRow();
            if (row == null || row.Source == null) return;

            if (GetRowEvents(row).Any(CanVisualize))
                VisualizeRows(new List<RowVm> { row });
            else
                FocusSelectedElement();
        }

        private void VisualizeRows(List<RowVm> rows, bool focusAfterVisualize = false)
        {
            var events = (rows ?? new List<RowVm>())
                .SelectMany(GetRowEvents)
                .Where(CanVisualize)
                .ToList();
            if (events.Count == 0) return;

            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.VisualizeEvents,
                Events = events,
                FocusElementIds = focusAfterVisualize
                    ? events.Select(e => e.ElementId).Distinct().ToList()
                    : new List<int>()
            });
        }

        private static IEnumerable<ElementHistoryEvent> GetRowEvents(RowVm row)
        {
            if (row?.Events != null && row.Events.Count > 0) return row.Events;
            return row?.Source == null ? Enumerable.Empty<ElementHistoryEvent>() : new[] { row.Source };
        }

        private static bool CanVisualize(ElementHistoryEvent ev)
        {
            if (IsAxisLineHistoryEvent(ev)) return false;
            return string.Equals(ev.Action, "delete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ev.Action, "move", StringComparison.OrdinalIgnoreCase)
                || HasMoveDelta(ev);
        }

        private static bool IsAxisLineHistoryEvent(ElementHistoryEvent ev)
        {
            return ev != null
                && (IsAxisLineText(ev.Category)
                    || IsAxisLineText(ev.Family)
                    || IsAxisLineText(ev.TypeName)
                    || IsAxisLineText(ev.UniqueId));
        }

        private static bool IsAxisLineText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var value = text.Trim();
            var hasAxisWord = value.IndexOf("axe", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("axis", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("center", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("centre", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasLineWord = value.IndexOf("ligne", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("line", StringComparison.OrdinalIgnoreCase) >= 0;

            return (hasAxisWord && hasLineWord)
                || value.Equals("Axe", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Axes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Axis", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Line", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Lines", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Ligne", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Lignes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasMoveDelta(ElementHistoryEvent ev)
        {
            return ev?.Delta != null
                && ev.Delta.TryGetValue("new", out var newPos)
                && ev.Delta.TryGetValue("old", out var oldPos)
                && ReadPoint(newPos) != null
                && ReadPoint(oldPos) != null;
        }

        private RowVm GetPrimarySelectedRow()
        {
            var visualRow = VisualCardsList?.SelectedItem as RowVm;
            if (visualRow != null && !visualRow.IsTimelineSeparator) return visualRow;
            return HistoryGrid?.SelectedItem as RowVm;
        }

        private void ClusterFocusButton_Click(object sender, RoutedEventArgs e)
        {
            var ids = GetSelectedClusterItems()
                .Select(x => x.Source)
                .Where(x => x != null)
                .Select(x => x.ElementId)
                .Distinct()
                .ToList();
            if (ids.Count == 0) return;

            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.Focus,
                FocusElementIds = ids
            });
        }

        private void ClusterVisualizeButton_Click(object sender, RoutedEventArgs e)
        {
            var events = GetSelectedClusterItems()
                .Select(x => x.Source)
                .Where(x => x != null && CanVisualize(x))
                .GroupBy(x => x.ElementId)
                .Select(x => x.First())
                .ToList();
            if (events.Count == 0) return;

            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.VisualizeEvents,
                Events = events
            });
        }

        private void ClusterListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ClusterFocusButton_Click(sender, e);
        }

        private List<ClusterItemVm> GetSelectedClusterItems()
        {
            var selected = ClusterListBox?.SelectedItems
                .Cast<ClusterItemVm>()
                .Where(x => x?.Source != null)
                .ToList() ?? new List<ClusterItemVm>();

            if (selected.Count == 0 && ClusterListBox?.SelectedItem is ClusterItemVm item && item.Source != null)
                selected.Add(item);

            return selected;
        }

        private void RaiseRequest(UiRequest request)
        {
            _pendingRequest = request;
            _externalEvent?.Raise();
        }

        private void UpdateDetails()
        {
            if (DetailsText == null || !_detailsVisible) return;
            var row = GetPrimarySelectedRow();
            if (row == null || row.Source == null)
            {
                DetailsText.Text = "Sélectionne une ligne pour afficher ses détails.";
                SetClusterDetails(null);
                return;
            }

            var ev = row.Source;
            if (row.IsCluster)
            {
                DetailsText.Text =
                    $"Résumé: {BuildClusterSummary(row.Events)}\n\n" +
                    $"Cluster: {row.EventCount} évènements\n" +
                    $"Action: {row.ActionText}\n" +
                    $"Utilisateur: {row.User}\n" +
                    $"Date UTC: {ev.Ts:O}\n" +
                    $"Projet: {ev.Project}\n" +
                    $"Maquette: {ev.ModelKey}\n" +
                    $"Catégorie: {row.Category}\n" +
                    $"Famille: {row.Family}\n" +
                    $"Type: {row.TypeName}\n" +
                    $"Transaction: {row.Tx}";
                SetClusterDetails(row.Events);
                return;
            }

            SetClusterDetails(null);
            var readableDelta = GetReadableDeltaSummary(ev);
            var readableDeltaBlock = GetReadableDeltaBlock(ev);
            DetailsText.Text =
                $"Id: {ev.ElementId}\n" +
                $"UniqueId: {ev.UniqueId}\n" +
                $"Action: {GetActionText(ev.Action)}\n" +
                $"Utilisateur: {ev.User}\n" +
                $"Date UTC: {ev.Ts:O}\n" +
                $"Projet: {ev.Project}\n" +
                $"Maquette: {ev.ModelKey}\n" +
                $"Catégorie: {ev.Category}\n" +
                $"Famille: {ev.Family}\n" +
                $"Type: {ev.TypeName}\n" +
                $"Transaction: {ev.Tx}\n" +
                (string.IsNullOrWhiteSpace(readableDeltaBlock)
                    ? (string.IsNullOrWhiteSpace(readableDelta) ? string.Empty : readableDelta + "\n")
                    : readableDeltaBlock + "\n") +
                "\n" +
                "Delta:\n" +
                (ev.Delta == null ? "-" : JsonConvert.SerializeObject(ev.Delta, Formatting.Indented));
        }

        private static string GetReadableDeltaBlock(ElementHistoryEvent ev)
        {
            if (ev == null) return string.Empty;
            var action = (ev.Action ?? string.Empty).Trim().ToLowerInvariant();

            if (action == "param_change" && ev.Delta != null && ev.Delta.TryGetValue("parameters", out var raw) && raw != null)
            {
                var lines = ReadParameterChangeLines(raw).Take(12).ToList();
                if (lines.Count > 0)
                    return "Avant / après:\n" + string.Join("\n", lines.Select(x => "- " + x));
            }

            var summary = GetReadableDeltaSummary(ev);
            return string.IsNullOrWhiteSpace(summary) ? string.Empty : summary;
        }

        private void SetClusterDetails(List<ElementHistoryEvent> events)
        {
            if (ClusterListBox == null || ClusterActionsPanel == null) return;
            if (events == null || events.Count == 0)
            {
                ClusterListBox.ItemsSource = null;
                ClusterListBox.Visibility = System.Windows.Visibility.Collapsed;
                ClusterActionsPanel.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            var items = events
                .OrderBy(e => CleanCellText(e.Family))
                .ThenBy(e => CleanCellText(e.TypeName))
                .ThenBy(e => e.ElementId)
                .Select(e =>
                {
                    var family = FirstNonEmpty(CleanCellText(e.Family), CleanCellText(e.Category), "Sans famille");
                    return new ClusterItemVm
                    {
                        Source = e,
                        FamilyGroup = family,
                        Display = "Id " + e.ElementId.ToString(CultureInfo.InvariantCulture) +
                                  " | " + FirstNonEmpty(CleanCellText(e.TypeName), CleanCellText(e.Category), "Elément")
                    };
                })
                .ToList();

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(items);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ClusterItemVm.FamilyGroup)));
            ClusterListBox.ItemsSource = view;
            ClusterListBox.SelectedIndex = 0;
            ClusterListBox.Visibility = System.Windows.Visibility.Visible;
            ClusterActionsPanel.Visibility = System.Windows.Visibility.Visible;
        }

        private static string BuildClusterSummary(List<ElementHistoryEvent> events)
        {
            events = events ?? new List<ElementHistoryEvent>();
            var action = GetActionText(events.FirstOrDefault()?.Action);
            if (string.IsNullOrWhiteSpace(action)) action = "évènements";

            var families = CountDistinct(events.Select(e => CleanCellText(e.Family)));
            var types = CountDistinct(events.Select(e => CleanCellText(e.TypeName)));

            return events.Count.ToString(CultureInfo.InvariantCulture) + " " + action.ToLowerInvariant() +
                   " · " + families.ToString(CultureInfo.InvariantCulture) + " familles" +
                   " · " + types.ToString(CultureInfo.InvariantCulture) + " types";
        }

        private RestoreResult ExecuteRestoreParameters(ElementHistoryEvent ev)
        {
            var result = new RestoreResult();
            if (!CanRestoreParameters(ev)) return result;

            var changes = ReadParameterChanges(ev.Delta["parameters"]);
            if (changes.Count == 0) return result;

            using (var t = new Transaction(_doc, "BIMaestro - Restaurer paramètres historique"))
            {
                t.Start();

                if (_doc.IsFamilyDocument)
                    RestoreFamilyTypeParameters(ev, changes, result);

                var element = ev.ElementId > 0 ? _doc.GetElement(new ElementId(ev.ElementId)) : null;
                if (element != null)
                    RestoreElementOrTypeParameters(element, changes, result);

                t.Commit();
            }

            return result;
        }

        private void RestoreElementOrTypeParameters(Element element, List<ParameterRestoreChange> changes, RestoreResult result)
        {
            var targets = new List<Element>();
            if (element != null)
                targets.Add(element);

            try
            {
                var typeId = element?.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var type = _doc.GetElement(typeId);
                    if (type != null && type.Id != element.Id)
                        targets.Add(type);
                }
            }
            catch
            {
            }

            foreach (var change in changes)
            {
                if (IsFormulaChange(change.Name)) continue;

                Parameter parameter = null;
                foreach (var target in targets)
                {
                    parameter = FindWritableParameter(target, change.Name);
                    if (parameter != null) break;
                }

                if (parameter == null) continue;

                if (TrySetParameterValue(parameter, change.OldValue))
                    result.Applied++;
                else
                    result.Failed++;
            }
        }

        private void RestoreFamilyTypeParameters(ElementHistoryEvent ev, List<ParameterRestoreChange> changes, RestoreResult result)
        {
            FamilyManager manager;
            try { manager = _doc.FamilyManager; }
            catch { return; }

            var originalType = manager.CurrentType;
            try
            {
                var targetType = manager.Types
                    .Cast<FamilyType>()
                    .FirstOrDefault(t => string.Equals(CleanCellText(t?.Name), CleanCellText(ev.TypeName), StringComparison.OrdinalIgnoreCase));
                if (targetType != null)
                    manager.CurrentType = targetType;

                foreach (var change in changes)
                {
                    var parameterName = StripFormulaSuffix(change.Name);
                    var parameter = manager.Parameters
                        .Cast<FamilyParameter>()
                        .FirstOrDefault(p => string.Equals(CleanCellText(p?.Definition?.Name), parameterName, StringComparison.OrdinalIgnoreCase));
                    if (parameter == null)
                    {
                        result.Failed++;
                        continue;
                    }

                    if (IsFormulaChange(change.Name))
                    {
                        if (TrySetFamilyFormula(manager, parameter, change.OldValue))
                            result.Applied++;
                        else
                            result.Failed++;
                        continue;
                    }

                    if (TrySetFamilyParameterValue(manager, parameter, change.OldValue))
                        result.Applied++;
                    else
                        result.Failed++;
                }
            }
            finally
            {
                try
                {
                    if (originalType != null)
                        manager.CurrentType = originalType;
                }
                catch
                {
                }
            }
        }

        private static Parameter FindWritableParameter(Element element, string name)
        {
            if (element == null || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                foreach (Parameter parameter in element.GetParameters(name))
                {
                    if (parameter != null && !parameter.IsReadOnly)
                        return parameter;
                }
            }
            catch
            {
            }

            try
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter != null
                        && !parameter.IsReadOnly
                        && string.Equals(CleanCellText(parameter.Definition?.Name), CleanCellText(name), StringComparison.OrdinalIgnoreCase))
                        return parameter;
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool TrySetParameterValue(Parameter parameter, string value)
        {
            if (parameter == null || parameter.IsReadOnly) return false;
            var oldValue = value ?? string.Empty;

            try
            {
                if (parameter.StorageType != StorageType.String && parameter.SetValueString(oldValue))
                    return true;
            }
            catch
            {
            }

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.Set(oldValue);
                    case StorageType.Integer:
                        if (TryParseIntegerValue(oldValue, out var intValue))
                            return parameter.Set(intValue);
                        return false;
                    case StorageType.Double:
                        if (TryParseDoubleValue(oldValue, out var doubleValue))
                            return parameter.Set(doubleValue);
                        return false;
                    case StorageType.ElementId:
                        if (int.TryParse(oldValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
                            return parameter.Set(new ElementId(idValue));
                        return false;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetFamilyParameterValue(FamilyManager manager, FamilyParameter parameter, string value)
        {
            if (manager == null || parameter == null) return false;
            var oldValue = value ?? string.Empty;

            if (TryInvokeFamilySetValueString(manager, parameter, oldValue))
                return true;

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        manager.Set(parameter, oldValue);
                        return true;
                    case StorageType.Integer:
                        if (!TryParseIntegerValue(oldValue, out var intValue)) return false;
                        manager.Set(parameter, intValue);
                        return true;
                    case StorageType.Double:
                        if (!TryParseDoubleValue(oldValue, out var doubleValue)) return false;
                        manager.Set(parameter, doubleValue);
                        return true;
                    case StorageType.ElementId:
                        if (!int.TryParse(oldValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue)) return false;
                        manager.Set(parameter, new ElementId(idValue));
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInvokeFamilySetValueString(FamilyManager manager, FamilyParameter parameter, string value)
        {
            try
            {
                var method = typeof(FamilyManager).GetMethod("SetValueString", new[] { typeof(FamilyParameter), typeof(string) });
                if (method == null) return false;

                var invokeResult = method.Invoke(manager, new object[] { parameter, value ?? string.Empty });
                return invokeResult == null || (invokeResult is bool ok && ok);
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetFamilyFormula(FamilyManager manager, FamilyParameter parameter, string formula)
        {
            try
            {
                manager.SetFormula(parameter, string.IsNullOrWhiteSpace(formula) ? null : formula);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseIntegerValue(string value, out int result)
        {
            var clean = (value ?? string.Empty).Trim();
            if (int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)) return true;
            if (int.TryParse(clean, NumberStyles.Integer, CultureInfo.CurrentCulture, out result)) return true;
            if (clean.Equals("oui", StringComparison.OrdinalIgnoreCase) || clean.Equals("yes", StringComparison.OrdinalIgnoreCase) || clean.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                result = 1;
                return true;
            }
            if (clean.Equals("non", StringComparison.OrdinalIgnoreCase) || clean.Equals("no", StringComparison.OrdinalIgnoreCase) || clean.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                result = 0;
                return true;
            }
            return false;
        }

        private static bool TryParseDoubleValue(string value, out double result)
        {
            var clean = (value ?? string.Empty).Trim();
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return true;
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.CurrentCulture, out result)) return true;
            result = 0;
            return false;
        }

        private static bool IsFormulaChange(string name)
        {
            return CleanCellText(name).EndsWith(" (formule)", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripFormulaSuffix(string name)
        {
            var text = CleanCellText(name);
            const string suffix = " (formule)";
            return text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? text.Substring(0, text.Length - suffix.Length)
                : text;
        }

        private void ShowRestoreResult(RestoreResult result)
        {
            if (result == null) return;
            MessageBox.Show(
                result.Applied > 0
                    ? $"{result.Applied} valeur(s) restaurée(s)." + (result.Failed > 0 ? $"\n{result.Failed} valeur(s) n'ont pas pu être restaurée(s)." : "")
                    : "Aucune valeur n'a pu être restaurée.",
                "BIMaestro - Qui a fait ça ?",
                MessageBoxButton.OK,
                result.Applied > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void ExecuteFocus(List<int> focusElementIds)
        {
            if (focusElementIds == null || focusElementIds.Count == 0) return;

            var ids = focusElementIds
                .Distinct()
                .Select(x => new ElementId(x))
                .Where(id => _doc.GetElement(id) != null)
                .ToList();

            if (ids.Count > 0)
            {
                _uidoc.Selection.SetElementIds(ids);
                if (ids.Count == 1)
                {
                    var element = _doc.GetElement(ids[0]);
                    if (element is View view && !view.IsTemplate)
                    {
                        try
                        {
                            _uidoc.ActiveView = view;
                            return;
                        }
                        catch
                        {
                        }
                    }

                    _uidoc.ShowElements(ids[0]);
                }
                else
                {
                    _uidoc.ShowElements(ids);
                }
                return;
            }

            var requestedIds = new HashSet<string>(focusElementIds.Select(x => "_" + x.ToString(CultureInfo.InvariantCulture)), StringComparer.OrdinalIgnoreCase);
            var preview = GetPreviewElements(_doc)
                .Where(x => requestedIds.Any(suffix => x.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Id)
                .ToList();

            if (preview.Count > 0)
            {
                _uidoc.Selection.SetElementIds(preview);
                if (preview.Count == 1)
                    _uidoc.ShowElements(preview[0]);
                else
                    _uidoc.ShowElements(preview);
            }
        }

        private void ExecuteVisualize(List<ElementHistoryEvent> events)
        {
            using (var t = new Transaction(_doc, "BIMaestro - Visualisation historique"))
            {
                t.Start();

                var existingPreviewIds = GetPreviewElements(_doc).Select(e => e.Id).ToList();
                if (existingPreviewIds.Count > 0)
                    _doc.Delete(existingPreviewIds);

                foreach (var ev in events ?? new List<ElementHistoryEvent>())
                {
                    try
                    {
                        if (HasMoveDelta(ev))
                            CreateMovePreview(ev);
                        else if (string.Equals(ev.Action, "delete", StringComparison.OrdinalIgnoreCase))
                            CreateDeletedPreview(ev);
                    }
                    catch
                    {
                    }
                }
                t.Commit();
            }
        }

        private void ExecuteCleanPreviews()
        {
            var ids = GetPreviewElements(_doc).Select(e => e.Id).ToList();
            if (ids.Count == 0) return;

            using (var t = new Transaction(_doc, "BIMaestro - Nettoyer previews historique"))
            {
                t.Start();
                _doc.Delete(ids);
                t.Commit();
            }
        }

        private void ExecuteCaptureSelectedDetails()
        {
            try
            {
                var ids = _uidoc?.Selection?.GetElementIds();
                if (ids == null || ids.Count == 0) return;
                ElementHistoryTracker.CaptureSelectedElementDetails(_doc, ids);
            }
            catch
            {
            }
        }

        private void CreateDeletedPreview(ElementHistoryEvent ev)
        {
            var geoms = BuildDeletedPreviewGeometry(ev);
            if (geoms.Count == 0) return;
            var ds = CreatePreviewDirectShape(DeletedPreviewPrefix + ev.ElementId.ToString(CultureInfo.InvariantCulture), geoms);
            ApplyOverride(ds.Id, new Autodesk.Revit.DB.Color(220, 30, 30), 65);
        }

        private void CreateMovePreview(ElementHistoryEvent ev)
        {
            if (ev?.Delta == null) return;
            if (!ev.Delta.TryGetValue("old", out var oldObj) || !ev.Delta.TryGetValue("new", out var newObj)) return;

            var oldPt = ReadPoint(oldObj);
            var newPt = ReadPoint(newObj);
            if (oldPt == null || newPt == null) return;

            var suffix = ev.ElementId.ToString(CultureInfo.InvariantCulture);
            var oldShape = CreatePreviewDirectShape(MoveOldPreviewPrefix + suffix, BuildPointMarker(oldPt, 0.65, 0.25));
            ApplyOverride(oldShape.Id, new Autodesk.Revit.DB.Color(220, 30, 30), 35);

            var newShape = CreatePreviewDirectShape(MoveNewPreviewPrefix + suffix, BuildPointMarker(newPt, 0.65, 0.25));
            ApplyOverride(newShape.Id, new Autodesk.Revit.DB.Color(35, 155, 75), 45);

            var arrow = CreatePreviewDirectShape(MoveArrowPreviewPrefix + suffix, BuildMoveArrow(oldPt, newPt));
            ApplyOverride(arrow.Id, new Autodesk.Revit.DB.Color(240, 145, 20), 0);
        }

        private DirectShape CreatePreviewDirectShape(string name, List<GeometryObject> geoms)
        {
            var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.Name = name;
            ds.SetShape(geoms);
            return ds;
        }

        private void ApplyOverride(ElementId id, Autodesk.Revit.DB.Color color, int transparency)
        {
            var activeView = _uidoc?.ActiveView;
            if (activeView == null) return;

            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetSurfaceForegroundPatternColor(color);
            var solidFill = new FilteredElementCollector(_doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(x => x.GetFillPattern().IsSolidFill);
            if (solidFill != null)
            {
                ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                ogs.SetCutForegroundPatternId(solidFill.Id);
                ogs.SetCutForegroundPatternColor(color);
            }
            ogs.SetSurfaceTransparency(transparency);
            activeView.SetElementOverrides(id, ogs);
        }

        private static List<DirectShape> GetPreviewElements(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .Cast<DirectShape>()
                .Where(IsPreviewDirectShape)
                .ToList();
        }

        internal static bool IsPreviewDirectShape(Element element)
        {
            var name = element?.Name ?? string.Empty;
            return element is DirectShape &&
                   (name.StartsWith(PreviewPrefix, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("BIMaestro_DeletedPreview_", StringComparison.OrdinalIgnoreCase));
        }

        private static string GetPositionText(ElementHistoryEvent ev)
        {
            if (ev?.Delta == null) return "-";
            var readableDelta = GetReadableDeltaSummary(ev);
            if (!string.IsNullOrWhiteSpace(readableDelta))
                return readableDelta.Replace("Avant / après: ", string.Empty);
            if (ev.Delta.TryGetValue("lastKnown", out var lastKnown))
                return "Supprimé @ " + CompactPoint(lastKnown);
            if (ev.Delta.TryGetValue("new", out var newPos) && ev.Delta.TryGetValue("old", out var oldPos))
                return $"{CompactPoint(oldPos)} -> {CompactPoint(newPos)}";
            if (ev.Delta.TryGetValue("parameters", out var parameters))
            {
                var names = ReadParameterNames(parameters).Take(2).ToList();
                if (names.Count > 0) return "Paramètre: " + string.Join(", ", names);
            }
            if (ev.Delta.ContainsKey("oldBox") && ev.Delta.ContainsKey("newBox"))
                return "Géométrie modifiée";
            return "-";
        }

        private static string CompactPoint(object o)
        {
            try
            {
                var j = o is JObject jo ? jo : JObject.FromObject(o);
                var x = j.Value<double?>("x") ?? 0;
                var y = j.Value<double?>("y") ?? 0;
                var z = j.Value<double?>("z") ?? 0;
                return $"({x:F4},{y:F4},{z:F4})";
            }
            catch
            {
                return "(n/a)";
            }
        }

        private static List<GeometryObject> BuildDeletedPreviewGeometry(ElementHistoryEvent ev)
        {
            var list = new List<GeometryObject>();
            if (ev?.Delta == null) return list;

            if (ElementHistoryTracker.CaptureDetailedDeletedMesh)
            {
                if (ev.Delta.TryGetValue("ghostMesh", out var meshObj))
                {
                    var mesh = BuildGhostMeshGeometry(meshObj);
                    if (mesh.Count > 0)
                        return mesh;
                }

                if (ev.Delta.TryGetValue("ghostFaces", out var ghostObj))
                {
                    var ghost = BuildGhostGeometry(ghostObj);
                    if (ghost.Count > 0)
                        return ghost;
                }

                if (ev.Delta.TryGetValue("obbCorners", out var arrObj)
                    && arrObj is JArray arr && arr.Count == 8)
                {
                    var pts = arr.Select(x => ReadPoint(x)).ToList();
                    if (pts.All(x => x != null))
                    {
                        var obbGeometry = BuildBoxGeometryFromCorners(pts);
                        if (obbGeometry.Count > 0)
                            return obbGeometry;
                    }
                }
            }

            if (ev.Delta.TryGetValue("bboxMin", out var minObj) && ev.Delta.TryGetValue("bboxMax", out var maxObj))
            {
                var min = ReadPoint(minObj);
                var max = ReadPoint(maxObj);
                if (min != null && max != null)
                    list.AddRange(BuildBox(min, max));
            }
            return list;
        }

        private static List<GeometryObject> BuildGhostMeshGeometry(object raw)
        {
            var faces = ReadGhostMeshFaces(raw);
            return faces.Count == 0 ? new List<GeometryObject>() : BuildTessellatedGeometry(faces);
        }

        private static List<List<XYZ>> ReadGhostMeshFaces(object raw)
        {
            var result = new List<List<XYZ>>();
            if (raw == null) return result;

            try
            {
                var obj = raw as JObject ?? JObject.FromObject(raw);
                var vertexTokens = obj["vertices"] as JArray;
                var faceTokens = obj["faces"] as JArray;
                if (vertexTokens == null || faceTokens == null) return result;

                var vertices = vertexTokens
                    .Select(x => ReadPoint(x))
                    .ToList();

                foreach (var faceToken in faceTokens)
                {
                    var indices = faceToken as JArray ?? JArray.FromObject(faceToken);
                    var pts = new List<XYZ>();
                    foreach (var indexToken in indices)
                    {
                        var index = indexToken.Value<int?>();
                        if (!index.HasValue || index.Value < 0 || index.Value >= vertices.Count)
                        {
                            pts.Clear();
                            break;
                        }

                        var pt = vertices[index.Value];
                        if (pt == null)
                        {
                            pts.Clear();
                            break;
                        }

                        pts.Add(pt);
                    }

                    if (pts.Count >= 3)
                        result.Add(pts);
                }
            }
            catch
            {
            }

            return result;
        }

        private static List<GeometryObject> BuildGhostGeometry(object raw)
        {
            var faces = ReadGhostFaces(raw);
            return faces.Count == 0 ? new List<GeometryObject>() : BuildTessellatedGeometry(faces);
        }

        private static List<List<XYZ>> ReadGhostFaces(object raw)
        {
            var result = new List<List<XYZ>>();
            if (raw == null) return result;

            try
            {
                var arr = raw as JArray ?? JArray.FromObject(raw);
                foreach (var faceToken in arr)
                {
                    var faceArr = faceToken as JArray ?? JArray.FromObject(faceToken);
                    var pts = faceArr
                        .Select(x => ReadPoint(x))
                        .Where(x => x != null)
                        .ToList();
                    if (pts.Count >= 3)
                        result.Add(pts);
                }
            }
            catch
            {
            }

            return result;
        }

        private static List<GeometryObject> BuildPointMarker(XYZ point, double size, double height)
        {
            var min = new XYZ(point.X - size, point.Y - size, point.Z);
            var max = new XYZ(point.X + size, point.Y + size, point.Z + height);
            return BuildBox(min, max);
        }

        private static List<GeometryObject> BuildMoveArrow(XYZ oldPt, XYZ newPt)
        {
            var geoms = new List<GeometryObject>();
            if (oldPt.DistanceTo(newPt) < 0.001) return geoms;

            geoms.Add(Line.CreateBound(oldPt, newPt));
            var dir = Normalize(new XYZ(newPt.X - oldPt.X, newPt.Y - oldPt.Y, newPt.Z - oldPt.Z));
            var side = Math.Abs(dir.X) > 0.8 ? XYZ.BasisY : XYZ.BasisX;
            var cross = Normalize(dir.CrossProduct(side));
            var back = new XYZ(newPt.X - dir.X * 0.8, newPt.Y - dir.Y * 0.8, newPt.Z - dir.Z * 0.8);
            geoms.Add(Line.CreateBound(newPt, new XYZ(back.X + cross.X * 0.35, back.Y + cross.Y * 0.35, back.Z + cross.Z * 0.35)));
            geoms.Add(Line.CreateBound(newPt, new XYZ(back.X - cross.X * 0.35, back.Y - cross.Y * 0.35, back.Z - cross.Z * 0.35)));
            return geoms;
        }

        private static List<GeometryObject> BuildBox(XYZ min, XYZ max)
        {
            var list = new List<GeometryObject>();
            var p1 = new XYZ(min.X, min.Y, min.Z);
            var p2 = new XYZ(max.X, min.Y, min.Z);
            var p3 = new XYZ(max.X, max.Y, min.Z);
            var p4 = new XYZ(min.X, max.Y, min.Z);

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p4));
            loop.Append(Line.CreateBound(p4, p1));

            var h = Math.Max(0.1, max.Z - min.Z);
            list.Add(GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h));
            return list;
        }

        private static List<GeometryObject> BuildBoxGeometryFromCorners(List<XYZ> pts)
        {
            if (pts == null || pts.Count != 8) return new List<GeometryObject>();

            var faces = new List<List<XYZ>>
            {
                new List<XYZ> { pts[0], pts[2], pts[6], pts[4] },
                new List<XYZ> { pts[1], pts[5], pts[7], pts[3] },
                new List<XYZ> { pts[0], pts[4], pts[5], pts[1] },
                new List<XYZ> { pts[2], pts[3], pts[7], pts[6] },
                new List<XYZ> { pts[0], pts[1], pts[3], pts[2] },
                new List<XYZ> { pts[4], pts[6], pts[7], pts[5] }
            };

            return BuildTessellatedGeometry(faces);
        }

        private static List<GeometryObject> BuildTessellatedGeometry(List<List<XYZ>> faces)
        {
            var result = new List<GeometryObject>();
            if (faces == null || faces.Count == 0) return result;

            try
            {
                var builder = new TessellatedShapeBuilder
                {
                    Target = TessellatedShapeBuilderTarget.AnyGeometry,
                    Fallback = TessellatedShapeBuilderFallback.Mesh
                };

                var added = 0;
                builder.OpenConnectedFaceSet(false);
                foreach (var face in faces)
                {
                    if (face == null || face.Count < 3) continue;

                    try
                    {
                        builder.AddFace(new TessellatedFace(face, ElementId.InvalidElementId));
                        added++;
                    }
                    catch
                    {
                    }
                }
                builder.CloseConnectedFaceSet();

                if (added == 0) return result;

                builder.Build();
                result.AddRange(builder.GetBuildResult().GetGeometricalObjects());
            }
            catch
            {
            }

            return result;
        }

        private static XYZ ReadPoint(object o)
        {
            try
            {
                if (o is JArray arr && arr.Count >= 3)
                {
                    var ax = arr[0].Value<double?>();
                    var ay = arr[1].Value<double?>();
                    var az = arr[2].Value<double?>();
                    if (!ax.HasValue || !ay.HasValue || !az.HasValue) return null;
                    return new XYZ(ax.Value, ay.Value, az.Value);
                }

                var j = o is JObject jo ? jo : JObject.FromObject(o);
                var x = j.Value<double?>("x");
                var y = j.Value<double?>("y");
                var z = j.Value<double?>("z");
                if (!x.HasValue || !y.HasValue || !z.HasValue) return null;
                return new XYZ(x.Value, y.Value, z.Value);
            }
            catch { return null; }
        }

        private static XYZ Normalize(XYZ v)
        {
            var len = v.GetLength();
            if (len < 1e-9) return XYZ.BasisX;
            return new XYZ(v.X / len, v.Y / len, v.Z / len);
        }
    }
}

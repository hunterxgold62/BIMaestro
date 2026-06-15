using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Analyse
{
    public partial class ElementHistoryWindow : Window
    {
        private const string PreviewPrefix = "BIMaestro_Preview_";
        private const string DeletedPreviewPrefix = PreviewPrefix + "Deleted_";
        private const string MoveOldPreviewPrefix = PreviewPrefix + "MoveOld_";
        private const string MoveNewPreviewPrefix = PreviewPrefix + "MoveNew_";
        private const string MoveArrowPreviewPrefix = PreviewPrefix + "MoveArrow_";

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

        private enum UiRequestType { None, Focus, VisualizeEvents, CleanPreviews }

        private sealed class UiRequest
        {
            public UiRequestType Type { get; set; }
            public List<ElementHistoryEvent> Events { get; set; } = new List<ElementHistoryEvent>();
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
                        return;
                    }


                    if (req.Type == UiRequestType.CleanPreviews)
                    {
                        _owner.ExecuteCleanPreviews();
                    }
                }
                catch
                {
                }
            }
        }

        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private List<RowVm> _rows = new List<RowVm>();
        private ExternalEvent _externalEvent;
        private UiRequestHandler _requestHandler;
        private UiRequest _pendingRequest;
        private bool _detailsVisible;
        private bool _syncingSelection;

        public ElementHistoryWindow(UIDocument uidoc, Element selected)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _uidoc = uidoc;
            _doc = uidoc?.Document;
            _requestHandler = new UiRequestHandler(this);
            _externalEvent = ExternalEvent.Create(_requestHandler);

            if (selected != null)
            {
                HeaderText.Text = "Qui a fait ça ??";
                HeaderSubtitleText.Text = $"BETA - Lecture visuelle des évènements liés à {selected.Name} (Id {selected.Id.IntegerValue}).";
                var perElement = ElementHistoryTracker.LoadElementHistory(_doc, selected);
                Bind(perElement.Count > 0 ? perElement : ElementHistoryTracker.LoadRecentModelHistory(_doc));
            }
            else
            {
                HeaderText.Text = "Qui a fait ça ??";
                HeaderSubtitleText.Text = "BETA - Lecture visuelle des suppressions, déplacements, créations et clusters de la maquette.";
                Bind(ElementHistoryTracker.LoadRecentModelHistory(_doc, 1000), "delete");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _externalEvent?.Dispose(); } catch { }
            base.OnClosed(e);
        }

        private void Bind(List<ElementHistoryEvent> eventsData, string defaultAction = null)
        {
            var events = eventsData
                .Where(ElementHistoryTracker.IsDisplayableHistoryEvent)
                .OrderByDescending(e => e.Ts)
                .Take(1000)
                .ToList();

            _rows = BuildRows(events);
            ActionFilterCombo.ItemsSource = new[] { "Toutes" }.Concat(_rows.Select(x => x.ActionText).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
            UserFilterCombo.ItemsSource = new[] { "Tous" }.Concat(_rows.Select(x => x.User).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
            var defaultActionText = GetActionText(defaultAction);
            ActionFilterCombo.SelectedItem = !string.IsNullOrWhiteSpace(defaultActionText) && ActionFilterCombo.Items.Contains(defaultActionText)
                ? defaultActionText
                : "Toutes";
            UserFilterCombo.SelectedIndex = 0;
            UpdateStats(_rows);
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
                ActionText = GetActionText(first.Action),
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
            var title = row.IsCluster
                ? GetMostUsefulLabel(events)
                : FirstNonEmpty(row.TypeName, row.Family, row.Category);

            return string.IsNullOrWhiteSpace(title) ? "Elément " + row.ElementIdText : title;
        }

        private static string BuildTileSubtitle(RowVm row, List<ElementHistoryEvent> events)
        {
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
                    return actor + " a modifié la géométrie de " + target;
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
                .Take(3)
                .ToList();

            if (distinct.Count == 0) return string.Empty;
            if (distinct.Count == 1) return distinct[0];
            return string.Join(", ", distinct);
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

        private static string ResolveBestImagePath(List<ElementHistoryEvent> events)
        {
            foreach (var ev in events ?? new List<ElementHistoryEvent>())
            {
                var resolved = ElementHistoryTracker.ResolveThumbnailPath(CleanCellText(ev?.Family), CleanCellText(ev?.TypeName));
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

                var image = ElementHistoryTracker.ResolveThumbnailPath(family, type);
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
                case "geometry_change": return "GEOM.";
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
            if (string.Equals(e.Action, "delete", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join("|",
                    CleanCellText(e.Action),
                    CleanCellText(e.User),
                    CleanCellText(e.Tx),
                    "bulk-delete",
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
                case "geometry_change": return "Modification géométrie";
                case "modify": return "Modification";
                default: return action.Trim();
            }
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
            UpdateDetails();
        }

        private void VisualCardsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            FocusSelectedElement();
            VisualizeDeletedButton_Click(sender, e);
        }

        private void UpdateVisualizeButtonLabel()
        {
            var row = GetPrimarySelectedRow();
            if (row == null || row.Source == null)
            {
                VisualizeDeletedButton.Content = "Visualiser évènement";
                return;
            }

            if (row.IsCluster)
                VisualizeDeletedButton.Content = "Visualiser cluster";
            else if (string.Equals(row.Source.Action, "delete", StringComparison.OrdinalIgnoreCase))
                VisualizeDeletedButton.Content = "Visualiser suppression";
            else if (string.Equals(row.Source.Action, "move", StringComparison.OrdinalIgnoreCase))
                VisualizeDeletedButton.Content = "Visualiser déplacement";
            else
                VisualizeDeletedButton.Content = "Visualiser évènement";
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

        private void ApplyFilters()
        {
            var action = ActionFilterCombo?.SelectedItem as string ?? "Toutes";
            var user = UserFilterCombo?.SelectedItem as string ?? "Tous";
            var q = (SearchBox?.Text ?? string.Empty).Trim();

            IEnumerable<RowVm> rows = _rows;
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

            var filtered = rows.ToList();
            HistoryGrid.ItemsSource = filtered;
            VisualCardsList.ItemsSource = BuildTimelineItems(filtered);
            if (filtered.Count > 0 && GetPrimarySelectedRow() == null)
            {
                VisualCardsList.SelectedItem = filtered[0];
                HistoryGrid.SelectedItem = filtered[0];
            }
            UpdateStats(filtered);
            UpdateVisualizeButtonLabel();
            UpdateDetails();
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
                ResultText.Text = events.Count.ToString(CultureInfo.InvariantCulture) + " / " + total.ToString(CultureInfo.InvariantCulture) + " évènements affichés";
            }
        }

        private void HistoryGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            FocusSelectedElement();
            VisualizeDeletedButton_Click(sender, e);
        }

        private void FocusButton_Click(object sender, RoutedEventArgs e)
        {
            FocusSelectedElement();
        }

        private void DetailsButton_Click(object sender, RoutedEventArgs e)
        {
            _detailsVisible = !_detailsVisible;
            DetailsPanel.Visibility = _detailsVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            DetailsButton.Content = _detailsVisible ? "Masquer détails" : "Détails";
            UpdateDetails();
        }


        private void CleanPreviewsButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseRequest(new UiRequest { Type = UiRequestType.CleanPreviews });
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

        private void VisualizeDeletedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedRows = HistoryGrid.SelectedItems
                .Cast<RowVm>()
                .Where(r => r?.Source != null && GetRowEvents(r).Any(CanVisualize))
                .ToList();

            var single = GetPrimarySelectedRow();
            if (selectedRows.Count == 0 && single?.Source != null && GetRowEvents(single).Any(CanVisualize))
                selectedRows.Add(single);
            if (selectedRows.Count == 0) return;

            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.VisualizeEvents,
                Events = selectedRows.SelectMany(GetRowEvents).Where(CanVisualize).ToList()
            });
        }

        private static IEnumerable<ElementHistoryEvent> GetRowEvents(RowVm row)
        {
            if (row?.Events != null && row.Events.Count > 0) return row.Events;
            return row?.Source == null ? Enumerable.Empty<ElementHistoryEvent>() : new[] { row.Source };
        }

        private static bool CanVisualize(ElementHistoryEvent ev)
        {
            return string.Equals(ev.Action, "delete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ev.Action, "move", StringComparison.OrdinalIgnoreCase);
        }

        private RowVm GetPrimarySelectedRow()
        {
            var visualRow = VisualCardsList?.SelectedItem as RowVm;
            if (visualRow != null && !visualRow.IsTimelineSeparator) return visualRow;
            return HistoryGrid?.SelectedItem as RowVm;
        }

        private void ClusterFocusButton_Click(object sender, RoutedEventArgs e)
        {
            FocusHistoryEvent((ClusterListBox?.SelectedItem as ClusterItemVm)?.Source);
        }

        private void ClusterVisualizeButton_Click(object sender, RoutedEventArgs e)
        {
            var ev = (ClusterListBox?.SelectedItem as ClusterItemVm)?.Source;
            if (ev == null || !CanVisualize(ev)) return;
            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.VisualizeEvents,
                Events = new List<ElementHistoryEvent> { ev }
            });
        }

        private void ClusterListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            FocusHistoryEvent((ClusterListBox?.SelectedItem as ClusterItemVm)?.Source);
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
                $"Transaction: {ev.Tx}\n\n" +
                "Delta:\n" +
                (ev.Delta == null ? "-" : JsonConvert.SerializeObject(ev.Delta, Formatting.Indented));
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
                    _uidoc.ShowElements(ids[0]);
                else
                    _uidoc.ShowElements(ids);
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
                foreach (var ev in events ?? new List<ElementHistoryEvent>())
                {
                    try
                    {
                        if (string.Equals(ev.Action, "move", StringComparison.OrdinalIgnoreCase))
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

        private void CreateDeletedPreview(ElementHistoryEvent ev)
        {
            var geoms = BuildDeletedPreviewGeometry(ev);
            if (geoms.Count == 0) return;
            var ds = CreatePreviewDirectShape(DeletedPreviewPrefix + ev.ElementId.ToString(CultureInfo.InvariantCulture), geoms);
            ApplyOverride(ds.Id, new Autodesk.Revit.DB.Color(220, 30, 30), 30);
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

            var category = ev.Category ?? string.Empty;
            if (!category.Equals("Walls", StringComparison.OrdinalIgnoreCase)
                && ev.Delta.TryGetValue("obbCorners", out var arrObj)
                && arrObj is JArray arr && arr.Count == 8)
            {
                var pts = arr.Select(x => ReadPoint(x)).ToList();
                if (pts.All(x => x != null))
                {
                    var obbSolid = BuildBoxSolidFromCorners(pts);
                    if (obbSolid != null)
                    {
                        list.Add(obbSolid);
                        return list;
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

        private static Solid BuildBoxSolidFromCorners(List<XYZ> pts)
        {
            try
            {
                var ordered = pts.OrderBy(p => p.Z).ToList();
                if (ordered.Count < 8) return null;

                var bottom = ordered.Take(4).ToList();
                var centerX = bottom.Average(q => q.X);
                var centerY = bottom.Average(q => q.Y);
                var bottom4 = bottom
                    .OrderBy(p => Math.Atan2(p.Y - centerY, p.X - centerX))
                    .ToList();

                var loop = new CurveLoop();
                for (int i = 0; i < 4; i++)
                {
                    var a = bottom4[i];
                    var b = bottom4[(i + 1) % 4];
                    loop.Append(Line.CreateBound(a, b));
                }

                var minZ = pts.Min(p => p.Z);
                var maxZ = pts.Max(p => p.Z);
                var h = Math.Max(0.1, maxZ - minZ);
                return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h);
            }
            catch
            {
                return null;
            }
        }

        private static XYZ ReadPoint(object o)
        {
            try
            {
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

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

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
            public string DateText { get; set; }
            public ElementHistoryEvent Source { get; set; }
            public string Action { get; set; }
            public string User { get; set; }
            public string Category { get; set; }
            public string Family { get; set; }
            public string TypeName { get; set; }
            public string PositionText { get; set; }
            public string Tx { get; set; }
        }

        private enum UiRequestType { None, Focus, VisualizeEvents, CleanPreviews }

        private sealed class UiRequest
        {
            public UiRequestType Type { get; set; }
            public List<ElementHistoryEvent> Events { get; set; } = new List<ElementHistoryEvent>();
            public int? FocusElementId { get; set; }
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
                        _owner.ExecuteFocus(req.FocusElementId);
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
                HeaderText.Text = $"Historique — {selected.Name} (Id {selected.Id.IntegerValue})";
                var perElement = ElementHistoryTracker.LoadElementHistory(_doc, selected);
                Bind(perElement.Count > 0 ? perElement : ElementHistoryTracker.LoadRecentModelHistory(_doc));
            }
            else
            {
                HeaderText.Text = "Historique des éléments supprimés récents";
                Bind(ElementHistoryTracker.LoadRecentDeletedHistory(_doc));
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _externalEvent?.Dispose(); } catch { }
            base.OnClosed(e);
        }

        private void Bind(List<ElementHistoryEvent> eventsData)
        {
            _rows = eventsData
                .OrderByDescending(e => e.Ts)
                .Take(400)
                .Select(e => new RowVm
                {
                    ElementId = e.ElementId,
                    DateText = e.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    Source = e,
                    Action = e.Action,
                    User = e.User,
                    Category = e.Category,
                    Family = e.Family,
                    TypeName = e.TypeName,
                    PositionText = GetPositionText(e),
                    Tx = e.Tx
                })
                .ToList();

            ActionFilterCombo.ItemsSource = new[] { "Toutes" }.Concat(_rows.Select(x => x.Action).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
            UserFilterCombo.ItemsSource = new[] { "Tous" }.Concat(_rows.Select(x => x.User).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x)).ToList();
            ActionFilterCombo.SelectedIndex = 0;
            UserFilterCombo.SelectedIndex = 0;
            UpdateStats(_rows);
            ApplyFilters();
        }

        private void HistoryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateVisualizeButtonLabel();
            UpdateDetails();
        }

        private void UpdateVisualizeButtonLabel()
        {
            var row = HistoryGrid.SelectedItem as RowVm;
            if (row == null || row.Source == null)
            {
                VisualizeDeletedButton.Content = "Visualiser évènement";
                return;
            }

            if (string.Equals(row.Source.Action, "delete", StringComparison.OrdinalIgnoreCase))
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
                rows = rows.Where(r => string.Equals(r.Action, action, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(user, "Tous", StringComparison.OrdinalIgnoreCase))
                rows = rows.Where(r => string.Equals(r.User, user, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(q))
            {
                rows = rows.Where(r => (r.Category ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (r.Family ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (r.TypeName ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (r.Tx ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                   || r.ElementId.ToString(CultureInfo.InvariantCulture).Contains(q));
            }

            var filtered = rows.ToList();
            HistoryGrid.ItemsSource = filtered;
            UpdateStats(filtered);
            UpdateVisualizeButtonLabel();
            UpdateDetails();
        }

        private void UpdateStats(List<RowVm> rows)
        {
            if (TotalStatText == null) return;
            TotalStatText.Text = rows.Count.ToString(CultureInfo.InvariantCulture);
            DeleteStatText.Text = rows.Count(r => string.Equals(r.Action, "delete", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
            MoveStatText.Text = rows.Count(r => string.Equals(r.Action, "move", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
            CreateStatText.Text = rows.Count(r => string.Equals(r.Action, "create", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
            UserStatText.Text = rows.Select(r => r.User).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.InvariantCulture);
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
            if (!(HistoryGrid.SelectedItem is RowVm row)) return;
            RaiseRequest(new UiRequest { Type = UiRequestType.Focus, FocusElementId = row.ElementId });
        }

        private void VisualizeDeletedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedRows = HistoryGrid.SelectedItems
                .Cast<RowVm>()
                .Where(r => r?.Source != null && CanVisualize(r.Source))
                .ToList();

            if (selectedRows.Count == 0 && HistoryGrid.SelectedItem is RowVm single && single.Source != null && CanVisualize(single.Source))
                selectedRows.Add(single);
            if (selectedRows.Count == 0) return;

            RaiseRequest(new UiRequest
            {
                Type = UiRequestType.VisualizeEvents,
                Events = selectedRows.Select(r => r.Source).ToList()
            });
        }

        private static bool CanVisualize(ElementHistoryEvent ev)
        {
            return string.Equals(ev.Action, "delete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ev.Action, "move", StringComparison.OrdinalIgnoreCase);
        }

        private void RaiseRequest(UiRequest request)
        {
            _pendingRequest = request;
            _externalEvent?.Raise();
        }

        private void UpdateDetails()
        {
            if (DetailsText == null || !_detailsVisible) return;
            if (!(HistoryGrid.SelectedItem is RowVm row) || row.Source == null)
            {
                DetailsText.Text = "Sélectionne une ligne pour afficher ses détails.";
                return;
            }

            var ev = row.Source;
            DetailsText.Text =
                $"Id: {ev.ElementId}\n" +
                $"UniqueId: {ev.UniqueId}\n" +
                $"Action: {ev.Action}\n" +
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

        private void ExecuteFocus(int? focusElementId)
        {
            if (!focusElementId.HasValue) return;

            var id = new ElementId(focusElementId.Value);
            var el = _doc.GetElement(id);
            if (el != null)
            {
                _uidoc.Selection.SetElementIds(new List<ElementId> { id });
                _uidoc.ShowElements(id);
                return;
            }

            var preview = GetPreviewElements(_doc)
                .Where(x => x.Name.EndsWith("_" + focusElementId.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Id.IntegerValue)
                .FirstOrDefault();

            if (preview != null)
            {
                _uidoc.Selection.SetElementIds(new List<ElementId> { preview.Id });
                _uidoc.ShowElements(preview.Id);
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
            ApplyOverride(ds.Id, new Color(220, 30, 30), 30);
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
            ApplyOverride(oldShape.Id, new Color(220, 30, 30), 35);

            var newShape = CreatePreviewDirectShape(MoveNewPreviewPrefix + suffix, BuildPointMarker(newPt, 0.65, 0.25));
            ApplyOverride(newShape.Id, new Color(35, 155, 75), 45);

            var arrow = CreatePreviewDirectShape(MoveArrowPreviewPrefix + suffix, BuildMoveArrow(oldPt, newPt));
            ApplyOverride(arrow.Id, new Color(240, 145, 20), 0);
        }

        private DirectShape CreatePreviewDirectShape(string name, List<GeometryObject> geoms)
        {
            var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.Name = name;
            ds.SetShape(geoms);
            return ds;
        }

        private void ApplyOverride(ElementId id, Color color, int transparency)
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

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Analyse
{
    public class SmartExternalHandler : IExternalEventHandler, IDisposable
    {
        private readonly UIApplication _uiapp;
        public Document OwnerDocument { get; }
        public SmartAction Action { get; set; }
        public SmartScanSession Session { get; set; }
        public ModelIssue FocusIssue { get; set; }
        public ModelIssue DisplayedIssue { get; private set; }
        public bool ContextVisible { get; private set; }
        public string ReservationMessage { get; private set; }
        private BoundingBoxXYZ _focusBox;
        public bool ShowAllEnabled { get; set; }
        public IList<ModelIssue> FocusIssues { get; set; } = new List<ModelIssue>();
        public string ThumbnailFolder { get; set; }
        public event Action Completed;
        public event Action<string> Failed;
        public event Action<SmartScanSetup> SetupRefreshed;
        public event Action ModelChanged;
        private ElementId _originalView;
        private IList<ElementId> _originalSelection;
        private View3D _view;
        private BoundingBoxXYZ _originalSection;
        private bool _originalSectionActive;
        private readonly Dictionary<ElementId, OverrideGraphicSettings> _overrides = new Dictionary<ElementId, OverrideGraphicSettings>();
        private bool _disposed;
        private bool _ownChanges;
        public SmartExternalHandler(UIApplication app)
        {
            _uiapp = app; OwnerDocument = app.ActiveUIDocument.Document;
            app.Application.DocumentChanged += DocumentChanged;
        }
        public string GetName() => "BIMaestro.Clash3D";
        private void DocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (_disposed || _ownChanges) return;
            // Linked geometry can change between slices as well as host geometry.
            if (!OwnerDocument.Equals(e.GetDocument()) && !e.GetDocument().IsLinked) return;
            if (Session != null && !Session.Complete) Session.CancelRequested = true;
            ModelChanged?.Invoke();
        }
        public void Execute(UIApplication app)
        {
            if (_disposed) return;
            try
            {
                if (Action == SmartAction.CloseSession)
                {
                    // Event unsubscription must happen in a valid Revit API callback, before WPF closes.
                    try
                    {
                        if (OwnerDocument.IsValidObject && OwnerDocument.Equals(app.ActiveUIDocument?.Document)) Restore(app.ActiveUIDocument);
                        else if (OwnerDocument.IsValidObject && _view?.IsValidObject == true)
                            Mutate(OwnerDocument, "Clash 3D · rétablir la vue", RestoreGraphics);
                    }
                    finally { Dispose(); }
                    return;
                }
                if (!OwnerDocument.IsValidObject || !OwnerDocument.Equals(app.ActiveUIDocument?.Document))
                    throw new InvalidOperationException("Revenez à la maquette de cette analyse pour poursuivre.");
                var ui = app.ActiveUIDocument;
                switch (Action)
                {
                    case SmartAction.ScanBatch: Session?.Advance(); break;
                    case SmartAction.RefreshSetup: SetupRefreshed?.Invoke(SmartScanSetup.Capture(ui)); break;
                    case SmartAction.TutorialCorrect: BIMaestro.Tutorials.DemoClashExercise.Correct(OwnerDocument); break;
                    case SmartAction.RestoreView: Restore(ui); break;
                    case SmartAction.ToggleContext: ToggleContext(ui); break;
                    case SmartAction.CreateReservation: CreateReservation(ui, FocusIssue); break;
                    case SmartAction.GenerateThumbnails:
                        Focus(ui, FocusIssue); CapturePreview(OwnerDocument, FocusIssue); break;
                    case SmartAction.ShowAllApply:
                        if (ShowAllEnabled) ShowAll(ui); else Restore(ui); break;
                    default: Focus(ui, FocusIssue); break;
                }
            }
            catch (Exception ex)
            {
                if (Action == SmartAction.ScanBatch && Session != null) { Session.CancelRequested = true; Session.Advance(); }
                Failed?.Invoke(ex.Message);
            }
            finally { Completed?.Invoke(); }
        }
        private View3D EnsureView(UIDocument ui)
        {
            var doc = ui.Document;
            if (_originalView == null)
            { _originalView = ui.ActiveView.Id; _originalSelection = ui.Selection.GetElementIds().ToList(); }
            if (_view == null || !_view.IsValidObject)
            {
                _view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name == SmartClashCommand.Smart3DName);
                if (_view == null)
                    Mutate(doc, "Clash 3D · vue de coordination", () =>
                    {
                        var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                        _view = View3D.CreateIsometric(doc, type.Id); _view.Name = SmartClashCommand.Smart3DName;
                        _view.DetailLevel = ViewDetailLevel.Fine; _view.DisplayStyle = DisplayStyle.FlatColors;
                    });
                _originalSectionActive = _view.IsSectionBoxActive; _originalSection = _view.GetSectionBox();
            }
            ui.ActiveView = _view;
            return _view;
        }
        private void Focus(UIDocument ui, ModelIssue issue)
        {
            if (issue == null) return;
            var doc = ui.Document;
            var source = ResolveSource(doc, issue);
            var related = !string.IsNullOrWhiteSpace(issue.LinkUniqueId) ? doc.GetElement(issue.LinkUniqueId)
                : !string.IsNullOrWhiteSpace(issue.RelatedUniqueId) ? doc.GetElement(issue.RelatedUniqueId) : doc.GetElement(issue.RelatedId);
            if (source == null) throw new InvalidOperationException("Cet objet a été supprimé. Relancez l'analyse.");
            if (!string.IsNullOrWhiteSpace(issue.RelatedUniqueId) && related == null)
                throw new InvalidOperationException("L'obstacle a été supprimé. Relancez l'analyse.");
            if (related is RevitLinkInstance link && (link.GetLinkDocument() == null || link.GetLinkDocument().GetElement(issue.RelatedUniqueId) == null))
                throw new InvalidOperationException("L'obstacle lié n'est plus disponible. Rechargez le lien et relancez l'analyse.");
            var view = EnsureView(ui);
            var box = Expand(issue.BBox ?? SmartGeometry.WorldBox(source.get_BoundingBox(null), Transform.Identity), 450 / 304.8);
            if (box == null) throw new InvalidOperationException("La zone du conflit n'est plus disponible.");
            Mutate(doc, "Clash 3D · voir le conflit", () =>
            {
                ClearOverrides(); view.IsSectionBoxActive = true; view.SetSectionBox(box);
                var visible = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(box.Min, box.Max)))
                    .Where(e => e.Category?.CategoryType == CategoryType.Model).ToList();
                var fade = new OverrideGraphicSettings().SetSurfaceTransparency(75).SetHalftone(true);
                foreach (var e in visible) Override(e.Id, fade);
                Override(source.Id, Highlight(doc, new Color(225, 89, 36)));
                if (related != null) Override(related.Id, Highlight(doc, new Color(36, 109, 196)));
            });
            // Selecting a whole link would overwhelm the local highlight.
            ui.Selection.SetElementIds(new List<ElementId> { source.Id });
            Zoom(ui, box); ui.RefreshActiveView();
            DisplayedIssue = issue; _focusBox = box; ContextVisible = false;
        }
        private void ToggleContext(UIDocument ui)
        {
            var issue = DisplayedIssue;
            if (issue == null || _focusBox == null) return;
            if (ContextVisible) { Focus(ui, issue); return; }
            var view = EnsureView(ui);
            var source = ResolveSource(ui.Document, issue);
            if (source == null) throw new InvalidOperationException("Cet objet a été supprimé. Relancez l'analyse.");
            var related = !string.IsNullOrWhiteSpace(issue.LinkUniqueId) ? ui.Document.GetElement(issue.LinkUniqueId)
                : !string.IsNullOrWhiteSpace(issue.RelatedUniqueId) ? ui.Document.GetElement(issue.RelatedUniqueId) : ui.Document.GetElement(issue.RelatedId);
            Mutate(ui.Document, "Clash 3D · voir autour", () =>
            {
                ClearOverrides(); view.IsSectionBoxActive = false;
                Override(source.Id, Highlight(ui.Document, new Color(225, 89, 36)));
                if (related != null) Override(related.Id, Highlight(ui.Document, new Color(36, 109, 196)));
            });
            Zoom(ui, Expand(_focusBox, 8000 / 304.8)); ui.RefreshActiveView(); ContextVisible = true;
        }
        private void CreateReservation(UIDocument ui, ModelIssue issue)
        {
            ReservationMessage = null;
            if (issue == null || !issue.CanCreateReservation || issue.IsApproximate)
                throw new InvalidOperationException("La réservation directe nécessite une intersection confirmée d'un objet avec un mur ou sol.");
            var source = ResolveSource(ui.Document, issue);
            var link = string.IsNullOrWhiteSpace(issue.LinkUniqueId) ? null : ui.Document.GetElement(issue.LinkUniqueId) as RevitLinkInstance;
            if (!string.IsNullOrWhiteSpace(issue.LinkUniqueId) && link?.GetLinkDocument() == null)
                throw new InvalidOperationException("Le lien n'est plus disponible. Rechargez-le et relancez l'analyse.");
            var host = (link?.GetLinkDocument() ?? ui.Document).GetElement(issue.RelatedUniqueId);
            if (source == null || host == null) throw new InvalidOperationException("Les objets ont changé. Relancez l'analyse.");
            var instance = Modification.ReservationAutoV3Command.CreateForClash(ui.Document, source, host, link);
            ReservationMessage = "Réservation créée · #" + instance.Id.GetIdLongValue()
                + (link == null ? ". Relancez l'analyse pour vérifier la traversée." : " dans la maquette active. Le lien reste à coordonner ; relancez l'analyse.");
        }
        private static Element ResolveSource(Document doc, ModelIssue issue) => !string.IsNullOrWhiteSpace(issue.ElementUniqueId)
            ? doc.GetElement(issue.ElementUniqueId) : doc.GetElement(issue.ElementId);
        private void ShowAll(UIDocument ui)
        {
            DisplayedIssue = null; ContextVisible = false; _focusBox = null;
            var issues = FocusIssues.Where(i => i != null).ToList(); if (issues.Count == 0) return;
            var view = EnsureView(ui); var box = Expand(SmartGeometry.Union(issues.Select(i => i.BBox)), 600 / 304.8);
            Mutate(ui.Document, "Clash 3D · vue d'ensemble", () =>
            {
                ClearOverrides(); if (box != null) { view.IsSectionBoxActive = true; view.SetSectionBox(box); }
                foreach (var issue in issues)
                { var e = ResolveSource(ui.Document, issue); if (e != null) Override(e.Id, Highlight(ui.Document, new Color(225, 89, 36))); }
            });
            Zoom(ui, box); ui.RefreshActiveView();
        }
        private static OverrideGraphicSettings Highlight(Document doc, Color color)
        {
            var settings = new OverrideGraphicSettings().SetProjectionLineColor(color).SetProjectionLineWeight(5).SetSurfaceTransparency(0);
            var fill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            if (fill != null) settings.SetSurfaceForegroundPatternId(fill.Id).SetSurfaceForegroundPatternColor(color)
                .SetCutForegroundPatternId(fill.Id).SetCutForegroundPatternColor(color);
            return settings;
        }
        private void Override(ElementId id, OverrideGraphicSettings settings)
        {
            if (!_overrides.ContainsKey(id)) _overrides[id] = _view.GetElementOverrides(id);
            _view.SetElementOverrides(id, settings);
        }
        private void ClearOverrides()
        {
            foreach (var entry in _overrides) if (OwnerDocument.GetElement(entry.Key) != null) _view.SetElementOverrides(entry.Key, entry.Value);
            _overrides.Clear();
        }
        private void Restore(UIDocument ui)
        {
            if (_view != null && _view.IsValidObject)
                Mutate(ui.Document, "Clash 3D · rétablir la vue", RestoreGraphics);
            if (_originalView != null && ui.Document.GetElement(_originalView) is View original && original.ViewType != ViewType.Internal) ui.ActiveView = original;
            if (_originalSelection != null) ui.Selection.SetElementIds(_originalSelection.Where(id => ui.Document.GetElement(id) != null).ToList());
            ui.RefreshActiveView(); _originalView = null; _originalSelection = null; _view = null;
            DisplayedIssue = null; ContextVisible = false; _focusBox = null;
        }
        private void RestoreGraphics()
        { ClearOverrides(); if (_originalSection != null) _view.SetSectionBox(_originalSection); _view.IsSectionBoxActive = _originalSectionActive; }
        private void Mutate(Document doc, string name, Action action)
        {
            _ownChanges = true;
            try { using (var t = new Transaction(doc, name))
                { t.Start(); action(); if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("La modification de la vue n'a pas été validée."); } }
            finally { _ownChanges = false; }
        }
        private static BoundingBoxXYZ Expand(BoundingBoxXYZ box, double pad)
        {
            if (box == null) return null;
            var p = new XYZ(pad, pad, pad); return new BoundingBoxXYZ { Min = box.Min - p, Max = box.Max + p };
        }
        private void Zoom(UIDocument ui, BoundingBoxXYZ box)
        { if (box != null) ui.GetOpenUIViews().FirstOrDefault(v => v.ViewId == _view.Id)?.ZoomAndCenterRectangle(box.Min, box.Max); }
        private void CapturePreview(Document doc, ModelIssue issue)
        {
            if (issue == null || _view == null) return;
            Directory.CreateDirectory(ThumbnailFolder);
            var path = Path.Combine(ThumbnailFolder, SmartClashReport.PreviewName(issue));
            var opts = new ImageExportOptions { FilePath = Path.ChangeExtension(path, null), ExportRange = ExportRange.SetOfViews,
                HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                ZoomType = ZoomFitType.FitToPage, PixelSize = 900, FitDirection = FitDirectionType.Horizontal };
            opts.SetViewsAndSheets(new List<ElementId> { _view.Id }); doc.ExportImage(opts);
            var exported = Directory.GetFiles(ThumbnailFolder, Path.GetFileNameWithoutExtension(path) + "*.png")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (exported != null) { if (exported != path) File.Copy(exported, path, true); issue.ThumbnailPath = path; }
        }
        public void Dispose()
        {
            // Call from an API callback. Closed only reaches here after CloseSession has disposed the handler.
            if (_disposed) return; _disposed = true;
            _uiapp.Application.DocumentChanged -= DocumentChanged;
            Session?.Dispose(); _overrides.Clear();
        }
    }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    public class SmartExternalHandler : IExternalEventHandler
    {
        private readonly UIApplication _uiapp;

        public static volatile bool IsExecuting = false;

        public SmartAction Action { get; set; } = SmartAction.SelectOnly;
        public ElementId IssueId { get; set; } = ElementId.InvalidElementId;
        public ElementId RelatedId { get; set; } = ElementId.InvalidElementId;
        public IssueKind CurrentKind { get; set; } = IssueKind.WallFloating;
        public BoundingBoxXYZ IssueBox { get; set; }

        public IList<ElementId> AllIssueIds { get; set; } = new List<ElementId>();
        public bool ShowAllMode { get; set; } = false;
        public bool ShowAllEnabled { get; set; } = false;

        public bool AutoSectionBox { get; set; } = true;

        public SmartExternalHandler(UIApplication app) { _uiapp = app; }
        public string GetName() => "BIMastro.SmartExternalHandler";

        public void Execute(UIApplication app)
        {
            if (IsExecuting) return;
            IsExecuting = true;
            try
            {
                var uidoc = _uiapp.ActiveUIDocument;
                var doc = uidoc.Document;

                switch (Action)
                {
                    case SmartAction.Ensure3D:
                        {
                            var v = EnsureSmart3D(doc);
                            uidoc.ActiveView = v;
                            break;
                        }

                    case SmartAction.FocusApply:        // Ensure3D + Focus + Zoom
                    case SmartAction.FocusIssue:        // legacy
                        {
                            var v = EnsureSmart3D(doc);
                            uidoc.ActiveView = v;

                            BoundingBoxXYZ focusBox = null;
                            using (var t = new Transaction(doc, "BIMastro Focus"))
                            {
                                t.Start();
                                if (!ShowAllMode) ClearOverrides(uidoc, v);
                                focusBox = FocusIn3D(uidoc, v, IssueId, RelatedId, CurrentKind, IssueBox, AutoSectionBox);
                                doc.Regenerate();
                                t.Commit();
                            }
                            ZoomTo(uidoc, v, focusBox, IssueId);
                            TryRefresh(uidoc);
                            break;
                        }

                    case SmartAction.ShowAllApply:
                        {
                            var v = EnsureSmart3D(doc);
                            uidoc.ActiveView = v;

                            using (var t = new Transaction(doc, "BIMastro ShowAll APPLY"))
                            {
                                t.Start();
                                TryDisableSectionBox(v);
                                ClearOverrides(uidoc, v);
                                if (ShowAllEnabled)
                                    ShowAllIssues(uidoc, v, CleanIds(AllIssueIds));
                                doc.Regenerate();
                                t.Commit();
                            }
                            TryRefresh(uidoc);
                            break;
                        }

                    case SmartAction.MarkIgnored:
                    case SmartAction.SelectOnly:
                    default:
                        {
                            if (IsValidId(IssueId))
                            {
                                var el = doc.GetElement(IssueId);
                                if (el != null)
                                {
                                    uidoc.Selection.SetElementIds(new List<ElementId> { el.Id });
                                    var uiview = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == uidoc.ActiveView.Id);
                                    var bb = el.get_BoundingBox(uidoc.ActiveView) ?? el.get_BoundingBox(null);
                                    if (uiview != null && bb != null) uiview.ZoomAndCenterRectangle(bb.Min, bb.Max);
                                }
                            }
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMastro – SmartExternalHandler", ex.Message);
            }
            finally
            {
                IsExecuting = false;
            }
        }

        // ---------- Helpers IDs ----------
        private static bool IsValidId(ElementId id)
            => id != null && id != ElementId.InvalidElementId && id.GetIdValue() > 0;

        private static List<ElementId> CleanIds(IEnumerable<ElementId> ids)
            => (ids ?? Enumerable.Empty<ElementId>()).Where(IsValidId).Distinct(new ElemIdCmp()).ToList();

        private class ElemIdCmp : IEqualityComparer<ElementId>
        {
            public bool Equals(ElementId a, ElementId b) => (a?.GetIdValue() ?? int.MinValue) == (b?.GetIdValue() ?? int.MinValue);
            public int GetHashCode(ElementId obj) => obj?.GetIdValue().GetHashCode() ?? 0;
        }

        // ---------- Vue 3D dédiée ----------
        private View3D EnsureSmart3D(Document doc)
        {
            var v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(x => !x.IsTemplate && x.Name.Equals(SmartCheckCommand.Smart3DName, StringComparison.OrdinalIgnoreCase));

            if (v == null)
            {
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                using (var t = new Transaction(doc, "Créer vue SmartCheck 3D"))
                {
                    t.Start();
                    v = View3D.CreateIsometric(doc, vft.Id);
                    v.Name = SmartCheckCommand.Smart3DName;
                    t.Commit();
                }
            }

            using (var t2 = new Transaction(doc, "Réglages SmartCheck 3D"))
            {
                t2.Start();
                try { v.ViewTemplateId = ElementId.InvalidElementId; } catch { }
                try { v.DetailLevel = ViewDetailLevel.Fine; } catch { }
                try { v.DisplayStyle = (DisplayStyle)Enum.Parse(typeof(DisplayStyle), "FlatColors", true); }
                catch { try { v.DisplayStyle = DisplayStyle.Shading; } catch { } }
                t2.Commit();
            }

            return v;
        }

        private static void TryEnableSectionBox(View3D v) { try { v.IsSectionBoxActive = true; } catch { } }
        private static void TryDisableSectionBox(View3D v) { try { v.IsSectionBoxActive = false; } catch { } }
        private static void TryRefresh(UIDocument uidoc) { try { uidoc.RefreshActiveView(); } catch { } }

        private BoundingBoxXYZ FocusIn3D(UIDocument uidoc, View3D v,
            ElementId id, ElementId related, IssueKind kind, BoundingBoxXYZ box, bool setSection)
        {
            var doc = uidoc.Document;

            // Sélection — nettoyée
            var ids = new List<ElementId>();
            if (IsValidId(id)) ids.Add(id);
            if (IsValidId(related)) ids.Add(related);
            ids = CleanIds(ids);

            if (ids.Count > 0)
                uidoc.Selection.SetElementIds(ids);

            // Boîte de focus
            BoundingBoxXYZ focus = box;
            if (IsValidId(related))
            {
                var rel = doc.GetElement(related);
                var rbb = rel?.get_BoundingBox(null);
                if (rbb != null && box != null) focus = Union(new[] { box, rbb });
                else if (rbb != null && box == null) focus = rbb;
            }
            if (focus == null && IsValidId(id))
            {
                var el = doc.GetElement(id);
                focus = el?.get_BoundingBox(null);
            }

            // Section box compacte (+200 mm)
            if (setSection && focus != null)
            {
                var pad = new XYZ(1, 1, 1) * (200.0 / 304.8);
                var b = new BoundingBoxXYZ { Min = focus.Min - pad, Max = focus.Max + pad };
                v.SetSectionBox(b);
                TryEnableSectionBox(v);
            }

            // Overrides
            var emphasize = new OverrideGraphicSettings();
            emphasize.SetProjectionLineColor(new Color(255, 0, 0));
#if REVIT2022_OR_LATER
            emphasize.SetProjectionLineWeight(8);
#endif
            emphasize.SetSurfaceTransparency(0);

            var fade = new OverrideGraphicSettings();
            fade.SetSurfaceTransparency(85);
            fade.SetHalftone(true);

            if ((kind == IssueKind.MepThroughWallNoSleeve || kind == IssueKind.LinkPipeClash) && ids.Count > 0)
            {
                foreach (var eid in ids) v.SetElementOverrides(eid, emphasize);

                var allIds = new FilteredElementCollector(doc, v.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds();

                var keep = new HashSet<ElementId>(ids, new ElemIdCmp());
                foreach (var oid in allIds)
                    if (!keep.Contains(oid))
                        v.SetElementOverrides(oid, fade);
            }
            else
            {
                if (IsValidId(id))
                    v.SetElementOverrides(id, emphasize);
            }

            return focus;
        }

        private void ZoomTo(UIDocument uidoc, View3D v, BoundingBoxXYZ focusBox, ElementId fallbackId)
        {
            var uiview = uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == v.Id)
                      ?? uidoc.GetOpenUIViews().FirstOrDefault(x => x.ViewId == uidoc.ActiveView.Id);
            if (uiview == null) return;

            BoundingBoxXYZ bb = focusBox;
            if (bb == null && IsValidId(fallbackId))
            {
                var el = uidoc.Document.GetElement(fallbackId);
                bb = el?.get_BoundingBox(v) ?? el?.get_BoundingBox(null);
            }
            if (bb == null) return;

            try { uiview.ZoomAndCenterRectangle(bb.Min, bb.Max); } catch { }
        }

        private void ShowAllIssues(UIDocument uidoc, View3D v, IList<ElementId> issueIds)
        {
            var doc = uidoc.Document;
            var ids = CleanIds(issueIds);

            var err = new OverrideGraphicSettings();
            err.SetProjectionLineColor(new Color(255, 0, 0));
#if REVIT2022_OR_LATER
            err.SetProjectionLineWeight(8);
#endif
            err.SetSurfaceTransparency(0);
            foreach (var id in ids)
                v.SetElementOverrides(id, err);

            var allIds = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().ToElementIds();
            var set = new HashSet<ElementId>(ids, new ElemIdCmp());
            var others = allIds.Where(i => !set.Contains(i)).ToList();

            var fade = new OverrideGraphicSettings();
            fade.SetSurfaceTransparency(85);
            fade.SetHalftone(true);
            foreach (var oid in others)
                v.SetElementOverrides(oid, fade);

            TryDisableSectionBox(v);
        }

        private void ClearOverrides(UIDocument uidoc, View3D v)
        {
            var doc = uidoc.Document;
            var allIds = new FilteredElementCollector(doc, v.Id)
                .WhereElementIsNotElementType().ToElementIds();
            var neutral = new OverrideGraphicSettings();
            foreach (var id in allIds) v.SetElementOverrides(id, neutral);
        }

        private static BoundingBoxXYZ Union(IEnumerable<BoundingBoxXYZ> bbs)
        {
            BoundingBoxXYZ u = null;
            foreach (var bb in bbs.Where(b => b != null))
            {
                if (u == null) u = new BoundingBoxXYZ { Min = bb.Min, Max = bb.Max };
                else
                {
                    u.Min = new XYZ(Math.Min(u.Min.X, bb.Min.X), Math.Min(u.Min.Y, bb.Min.Y), Math.Min(u.Min.Z, bb.Min.Z));
                    u.Max = new XYZ(Math.Max(u.Max.X, bb.Max.X), Math.Max(u.Max.Y, bb.Max.Y), Math.Max(u.Max.Z, bb.Max.Z));
                }
            }
            return u;
        }
    }
}

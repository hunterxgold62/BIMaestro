using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

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
        public IList<ModelIssue> ThumbnailIssues { get; set; } = new List<ModelIssue>();
        public string ThumbnailFolder { get; set; }
        public int ThumbnailLimit { get; set; } = 12;

        public SmartExternalHandler(UIApplication app) { _uiapp = app; }
        public string GetName() => "BIMaestro.SmartExternalHandler";

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
                            using (var t = new Transaction(doc, "BIMaestro Focus"))
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

                            using (var t = new Transaction(doc, "BIMaestro ShowAll APPLY"))
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

                    case SmartAction.GenerateThumbnails:
                        {
                            GenerateThumbnails(uidoc, doc);
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
                TaskDialog.Show("BIMaestro – SmartExternalHandler", ex.Message);
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
                .FirstOrDefault(x => !x.IsTemplate && x.Name.Equals(SmartClashCommand.Smart3DName, StringComparison.OrdinalIgnoreCase));

            if (v == null)
            {
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                using (var t = new Transaction(doc, "Créer vue SmartCheck 3D"))
                {
                    t.Start();
                    v = View3D.CreateIsometric(doc, vft.Id);
                    v.Name = SmartClashCommand.Smart3DName;
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

            // Boîte de focus : base sur les éléments en conflit, avec priorité à la box d'intersection
            var pairBoxes = new List<BoundingBoxXYZ>();
            foreach (var eid in ids)
            {
                var el = doc.GetElement(eid);
                var ebb = el?.get_BoundingBox(v) ?? el?.get_BoundingBox(null);
                if (ebb != null) pairBoxes.Add(ebb);
            }

            BoundingBoxXYZ focus = null;
            if (kind == IssueKind.LinkPipeClash)
            {
                // Collision lien/tuyau : ne pas cadrer sur le lien complet, garder le focus sur le tuyau + zone de collision
                var mainEl = IsValidId(id) ? doc.GetElement(id) : null;
                var mainBox = mainEl?.get_BoundingBox(v) ?? mainEl?.get_BoundingBox(null);
                focus = Union(new[] { box, mainBox });
            }
            else if (kind == IssueKind.MepThroughWallNoSleeve)
            {
                // Traversée MEP : garder les 2 éléments + box calculée
                focus = Union(new[] { box, Union(pairBoxes) });
            }
            else if (kind == IssueKind.MepUnconnected)
            {
                // Raccord ouvert : se concentrer sur l'élément MEP principal, avec une taille mini de box
                var mainEl = IsValidId(id) ? doc.GetElement(id) : null;
                var mainBox = mainEl?.get_BoundingBox(v) ?? mainEl?.get_BoundingBox(null);
                focus = EnsureMinimumBoxSize(mainBox ?? box, 300.0 / 304.8);
            }
            else
            {
                focus = Union(pairBoxes) ?? box;
            }

            // Section box compacte (+100 mm)
            if (setSection)
            {
                if (focus != null)
                {
                    var pad = new XYZ(1, 1, 1) * (100.0 / 304.8);
                    var b = new BoundingBoxXYZ { Min = focus.Min - pad, Max = focus.Max + pad };
                    v.SetSectionBox(b);
                    TryEnableSectionBox(v);
                }
                else
                {
                    // Évite de rester bloqué sur une section box précédente si aucune box fiable n'est trouvée
                    TryDisableSectionBox(v);
                }
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

            if (ids.Count > 0)
            {
                foreach (var eid in ids) v.SetElementOverrides(eid, emphasize);

                var allIds = CollectModelElementIds(doc);

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

            var allIds = CollectModelElementIds(doc);
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
            var allIds = CollectModelElementIds(doc);
            var neutral = new OverrideGraphicSettings();
            foreach (var id in allIds) v.SetElementOverrides(id, neutral);
        }

        private static IList<ElementId> CollectModelElementIds(Document doc)
        {
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e?.Category != null)
                .Select(e => e.Id)
                .ToList();
        }

        private void GenerateThumbnails(UIDocument uidoc, Document doc)
        {
            var queue = (ThumbnailIssues ?? new List<ModelIssue>())
                .Where(i => i != null)
                .Distinct()
                .Take(Math.Max(1, ThumbnailLimit))
                .ToList();
            if (queue.Count == 0) return;

            var folder = string.IsNullOrWhiteSpace(ThumbnailFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitLogs", "Clash3D", "Miniatures")
                : ThumbnailFolder;
            Directory.CreateDirectory(folder);

            var v = EnsureSmart3D(doc);
            uidoc.ActiveView = v;

            foreach (var issue in queue)
            {
                var target = Path.Combine(folder, MakeSafeFileName(issue.IssueKey) + ".png");
                if (File.Exists(target))
                {
                    issue.ThumbnailPath = target;
                    issue.ThumbnailLoading = false;
                    continue;
                }

                issue.ThumbnailLoading = true;
                try
                {
                    BoundingBoxXYZ focusBox = null;
                    using (var t = new Transaction(doc, "BIMaestro miniature Clash 3D"))
                    {
                        t.Start();
                        ClearOverrides(uidoc, v);
                        focusBox = FocusIn3D(
                            uidoc,
                            v,
                            issue.ElementId ?? ElementId.InvalidElementId,
                            issue.RelatedId ?? ElementId.InvalidElementId,
                            issue.Kind,
                            issue.BBox,
                            setSection: true);
                        doc.Regenerate();
                        t.Commit();
                    }

                    ZoomTo(uidoc, v, focusBox, issue.ElementId);
                    var exported = ExportViewToPng(doc, v, target, 420);
                    if (!string.IsNullOrWhiteSpace(exported) && File.Exists(exported))
                        issue.ThumbnailPath = exported;
                }
                catch
                {
                    // Une miniature ratée ne doit pas interrompre tout le lot.
                }
                finally
                {
                    issue.ThumbnailLoading = false;
                }
            }

            TryRefresh(uidoc);
        }

        private static string ExportViewToPng(Document doc, View3D view, string targetPng, int pixelSize)
        {
            var outDir = Path.GetDirectoryName(targetPng);
            if (string.IsNullOrWhiteSpace(outDir)) return null;
            Directory.CreateDirectory(outDir);

            var baseName = Path.GetFileNameWithoutExtension(targetPng);
            var basePath = Path.Combine(outDir, baseName);
            var before = new HashSet<string>(Directory.EnumerateFiles(outDir, "*.png"), StringComparer.OrdinalIgnoreCase);

            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = basePath,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = Math.Max(256, pixelSize),
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_150
            };

            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);

            string picked = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(2500);
            while (DateTime.UtcNow < deadline)
            {
                var after = Directory.EnumerateFiles(outDir, "*.png").ToList();
                picked = after
                    .Where(f => !before.Contains(f) || Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(picked)) break;
                Thread.Sleep(100);
            }

            if (string.IsNullOrWhiteSpace(picked)) return null;

            try
            {
                if (!string.Equals(picked, targetPng, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(targetPng)) File.Delete(targetPng);
                    File.Copy(picked, targetPng, overwrite: true);
                }

                return targetPng;
            }
            catch
            {
                return picked;
            }
        }

        private static string MakeSafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string((value ?? "issue").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return safe.Length > 120 ? safe.Substring(0, 120) : safe;
        }

        private static BoundingBoxXYZ EnsureMinimumBoxSize(BoundingBoxXYZ bb, double minSizeFt)
        {
            if (bb == null) return null;

            var cx = (bb.Min.X + bb.Max.X) * 0.5;
            var cy = (bb.Min.Y + bb.Max.Y) * 0.5;
            var cz = (bb.Min.Z + bb.Max.Z) * 0.5;

            var hx = Math.Max((bb.Max.X - bb.Min.X) * 0.5, minSizeFt * 0.5);
            var hy = Math.Max((bb.Max.Y - bb.Min.Y) * 0.5, minSizeFt * 0.5);
            var hz = Math.Max((bb.Max.Z - bb.Min.Z) * 0.5, minSizeFt * 0.5);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(cx - hx, cy - hy, cz - hz),
                Max = new XYZ(cx + hx, cy + hy, cz + hz)
            };
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
